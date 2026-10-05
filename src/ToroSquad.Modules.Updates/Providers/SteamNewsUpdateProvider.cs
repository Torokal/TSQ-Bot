using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Providers;

/// <summary>
/// Steam's public news API: GET https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/ (Steamworks documentation: no key;
/// the publisher-only GetNewsForAppAuthed on partner.steam-api.com is a different method and is never called). The address
/// is fixed; only the game's numeric AppID and the bounded count are put into the query. No API key, no credentials header,
/// no cookies, no Steam login. Redirects are not followed, the body is bounded, there is no inline retry (the poller backs
/// off), and a failure is always reported as a failure — never as "no posts".
/// <para><c>maxlength=0</c> asks for the full post text: any other value returns a generated blurb without the markup the
/// classifier reads. The text is used in memory only.</para>
/// </summary>
public sealed class SteamNewsUpdateProvider(IHttpClientFactory factory, IOptions<UpdatesOptions> options, TimeProvider clock, ILogger<SteamNewsUpdateProvider> logger)
    : IGameUpdateProvider
{
    public const string ProviderId = "steam";
    public const string HttpClientName = "updates-steam-news";
    public const string Endpoint = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/";

    public string Provider => ProviderId;

    public string DisplayName => "Steam";

    public string ReadLinkKey => "updates.card.read_steam";

    public bool IsCanonicalUrl(string url) => SteamNewsUrl.IsCanonical(url);

    /// <summary>The one request this provider makes for a game.</summary>
    public static Uri RequestUri(uint appId, int count) => new(string.Create(CultureInfo.InvariantCulture,
        $"{Endpoint}?appid={appId}&count={count}&maxlength=0&feeds={SteamNewsParser.AnnouncementFeed}&format=json"));

    public async Task<UpdateFetchResult> FetchAsync(GameUpdateDefinition game, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!uint.TryParse(game.ProviderGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0)
            return UpdateFetchResult.Fail(UpdateFetchOutcome.UnexpectedSchema, null, "game has no numeric AppID");

        var http = factory.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.RequestTimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri(appId, o.ItemsPerRequest));
            request.Headers.UserAgent.ParseAdd(o.UserAgent);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return UpdateFetchResult.Fail(UpdateFetchOutcome.RateLimited, status, "HTTP 429", RetryAfter(response, clock.GetUtcNow(), TimeSpan.FromMinutes(30)));
            if (status is >= 300 and < 400)
                return UpdateFetchResult.Fail(UpdateFetchOutcome.HttpError, status, $"HTTP {status} redirect (not followed)");
            if (status >= 500)
                return UpdateFetchResult.Fail(UpdateFetchOutcome.ServerError, status, $"HTTP {status}", status is 502 or 503 or 504 ? RetryAfter(response, clock.GetUtcNow()) : null);
            if (response.StatusCode != HttpStatusCode.OK)
                return UpdateFetchResult.Fail(UpdateFetchOutcome.HttpError, status, $"HTTP {status}");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                return UpdateFetchResult.Fail(UpdateFetchOutcome.Malformed, status, $"content type '{Short(mediaType)}' is not JSON");
            if (response.Content.Headers.ContentLength is { } declared && declared > o.MaxResponseBytes)
                return UpdateFetchResult.Fail(UpdateFetchOutcome.TooLarge, status, $"declared {declared} bytes > {o.MaxResponseBytes}");

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > o.MaxResponseBytes)
                    return UpdateFetchResult.Fail(UpdateFetchOutcome.TooLarge, status, $"body larger than {o.MaxResponseBytes} bytes");
                buffer.Write(chunk, 0, read);
            }

            var parsed = SteamNewsParser.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), game, clock.GetUtcNow());
            return parsed.Outcome switch
            {
                SteamParseOutcome.Ok => new(UpdateFetchOutcome.Ok, status, parsed.Items, parsed.Skipped, CacheLifetime(response), null, parsed.Detail),
                SteamParseOutcome.Empty => new(UpdateFetchOutcome.Empty, status, [], 0, CacheLifetime(response), null, parsed.Detail),
                SteamParseOutcome.Malformed => UpdateFetchResult.Fail(UpdateFetchOutcome.Malformed, status, parsed.Detail ?? "malformed answer"),
                _ => new(UpdateFetchOutcome.UnexpectedSchema, status, [], parsed.Skipped, null, null, parsed.Detail ?? "unexpected answer"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateFetchResult.Fail(UpdateFetchOutcome.Timeout, null, $"timed out after {o.RequestTimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("steam news transport error: {Error}", ex.GetType().Name);
            return UpdateFetchResult.Fail(UpdateFetchOutcome.TransportError, null, ex.HttpRequestError.ToString());
        }
        catch (IOException ex)
        {
            logger.LogDebug("steam news read error: {Error}", ex.GetType().Name);
            return UpdateFetchResult.Fail(UpdateFetchOutcome.TransportError, null, "response body could not be read");
        }
    }

    /// <summary>
    /// How long the answer itself says it stays fresh: Cache-Control max-age, else Expires minus the answer's own Date (so the
    /// local clock does not matter). Null when the source said nothing or the answer is already stale.
    /// </summary>
    public static TimeSpan? CacheLifetime(HttpResponseMessage response)
    {
        if (response.Headers.CacheControl is { } cache)
        {
            if (cache.NoCache || cache.NoStore)
                return null;
            if (cache.MaxAge is { } maxAge)
                return maxAge > TimeSpan.Zero ? maxAge : null;
        }

        if (response.Content.Headers.Expires is { } expires && response.Headers.Date is { } date && expires > date)
            return expires - date;
        return null;
    }

    /// <summary>Retry-After in seconds or as a date; <paramref name="fallback"/> when absent. Bounded to 5 minutes … 6 hours.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now, TimeSpan? fallback = null)
    {
        var wait = response.Headers.RetryAfter?.Delta;
        if (wait is null && response.Headers.RetryAfter?.Date is { } date)
            wait = date - now;
        wait ??= fallback;
        if (wait is null)
            return null;
        return wait < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : wait > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : wait;
    }

    private static string Short(string value) => value.Length > 60 ? value[..60] : value;

    /// <summary>The handler: no redirects, no cookies, decompression on.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
}
