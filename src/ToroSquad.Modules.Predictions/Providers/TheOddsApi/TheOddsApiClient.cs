using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Predictions.Application.Automation;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Providers.TheOddsApi;

/// <summary>
/// The Odds API v4 (https://the-odds-api.com/liveapi/guides/v4/), read-only, through one named <see cref="IHttpClientFactory"/>
/// client whose base address is the configured OFFICIAL host (validated; no redirects are followed, no URL from a response
/// is ever requested). Three GETs: /v4/sports and /v4/sports/{sport}/events (free) and /v4/sports/{sport}/odds with
/// regions=eu, markets=h2h, oddsFormat=decimal, dateFormat=iso and an eventIds filter (1 credit when it returns anything).
/// <para>
/// The provider takes its key only as the query parameter apiKey. The request URI therefore holds the secret, so: the URI is
/// never logged, never put in an exception text or a result, the HttpClient's own logging is removed for this client, and
/// failures are described by status code and exception TYPE only (an exception message could contain the URI).
/// </para>
/// <para>
/// One attempt per call — no hidden retry (every attempt that may consume credits is visible to the quota guard). Bounded in
/// time (per-request timeout on the injected clock) and size (the client's buffer limit). Every response's
/// x-requests-remaining / -used / -last headers are returned. Parsing is strict and uses decimal for prices; items that do
/// not match the documented shape are dropped, a body that is not the documented array is a BadResponse.
/// </para>
/// </summary>
public sealed class TheOddsApiClient(
    IHttpClientFactory http,
    FootballOddsApiKey key,
    IOptions<AutoFootballOptions> options,
    TimeProvider clock,
    ILogger<TheOddsApiClient> logger) : IFootballOddsProvider
{
    public const string HttpClientName = "predictions-theoddsapi";

    /// <summary>A day of events or odds for a handful of matches is a few KB to a few hundred KB.</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    public string Name => AutoFootballOptions.ProviderName;

    public bool IsConfigured => key.IsSet;

    public Task<ProviderCall<IReadOnlyList<ProviderSport>>> GetSportsAsync(CancellationToken cancellationToken) =>
        GetAsync("sports", "v4/sports", [], ParseSports, costly: false, cancellationToken);

    public Task<ProviderCall<IReadOnlyList<ProviderEvent>>> GetEventsAsync(string sportKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        GetAsync("events", "v4/sports/" + SafeSegment(sportKey) + "/events",
            [("dateFormat", "iso"), ("commenceTimeFrom", Iso(from)), ("commenceTimeTo", Iso(to))], ParseEvents, costly: false, cancellationToken);

    public Task<ProviderCall<IReadOnlyList<ProviderOddsEvent>>> GetOddsAsync(string sportKey, IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0 || eventIds.Any(id => !IsSafeId(id)))
            return Task.FromResult(new ProviderCall<IReadOnlyList<ProviderOddsEvent>>(ProviderCallOutcome.BadResponse, null, ProviderQuota.None));
        return GetAsync("odds", "v4/sports/" + SafeSegment(sportKey) + "/odds",
            [
                ("regions", options.Value.Region), ("markets", OddsSelector.Market), ("oddsFormat", "decimal"), ("dateFormat", "iso"),
                ("eventIds", string.Join(',', eventIds.Order(StringComparer.Ordinal))),
            ],
            ParseOdds, costly: true, cancellationToken);
    }

    private async Task<ProviderCall<T>> GetAsync<T>(string endpoint, string path, IReadOnlyList<(string Name, string Value)> query, Func<JsonElement, T?> parse, bool costly,
        CancellationToken cancellationToken) where T : class
    {
        if (!key.IsSet)
            return new ProviderCall<T>(ProviderCallOutcome.NotConfigured, null, ProviderQuota.None);

        using var deadline = new CancellationTokenSource(options.Value.Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var parameters = query.Prepend(("apiKey", key.Reveal())).Select(p => Uri.EscapeDataString(p.Item1) + "=" + Uri.EscapeDataString(p.Item2));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path + "?" + string.Join('&', parameters), UriKind.Relative));
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);
            var quota = Quota(response.Headers);
            var status = (int)response.StatusCode;
            logger.LogInformation("the_odds_api endpoint={Endpoint} status={Status} remaining={Remaining} used={Used} last={Last}",
                endpoint, status, quota.Remaining, quota.Used, quota.LastCost);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new ProviderCall<T>(ProviderCallOutcome.AuthFailed, null, quota, status);
                case HttpStatusCode.TooManyRequests:
                    return new ProviderCall<T>(ProviderCallOutcome.RateLimited, null, quota, status, RetryAfter(response.Headers.RetryAfter));
                case >= HttpStatusCode.InternalServerError:
                    return new ProviderCall<T>(ProviderCallOutcome.Unavailable, null, quota, status, MayHaveCost: costly && !quota.Known);
                default:
                    return new ProviderCall<T>(ProviderCallOutcome.BadResponse, null, quota, status);
            }

            var body = await response.Content.ReadAsByteArrayAsync(linked.Token);
            T? value;
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
                value = parse(document.RootElement);
            }
            catch (JsonException)
            {
                value = null;
            }

            return value is null
                ? new ProviderCall<T>(ProviderCallOutcome.BadResponse, null, quota, status, MayHaveCost: costly && !quota.Known)
                : new ProviderCall<T>(ProviderCallOutcome.Ok, value, quota, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("the_odds_api endpoint={Endpoint} timed out after {Timeout}", endpoint, options.Value.Timeout);
            return new ProviderCall<T>(ProviderCallOutcome.Timeout, null, ProviderQuota.None, MayHaveCost: costly);
        }
        catch (HttpRequestException ex)
        {
            // The message may contain the request URI (and so the key): only the type is recorded.
            logger.LogWarning("the_odds_api endpoint={Endpoint} failed: {Error}", endpoint, ex.GetType().Name);
            return new ProviderCall<T>(ProviderCallOutcome.Unavailable, null, ProviderQuota.None, ex.StatusCode is { } s ? (int)s : null, MayHaveCost: costly);
        }
    }

    private static ProviderQuota Quota(HttpResponseHeaders headers) =>
        new(Header(headers, "x-requests-remaining"), Header(headers, "x-requests-used"), Header(headers, "x-requests-last"));

    private static int? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) && decimal.TryParse(values.FirstOrDefault(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
        number is >= 0 and <= int.MaxValue
            ? (int)decimal.Floor(number)
            : null;

    private TimeSpan? RetryAfter(RetryConditionHeaderValue? value) => value switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - clock.GetUtcNow(),
        _ => null,
    };

    // ---- parsing (strict; a malformed item is dropped, a malformed body is a BadResponse) ----

    private static IReadOnlyList<ProviderSport>? ParseSports(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return null;
        var sports = new List<ProviderSport>();
        foreach (var item in root.EnumerateArray())
        {
            if (Text(item, "key") is { } k && Text(item, "title") is { } title && item.TryGetProperty("active", out var active) &&
                active.ValueKind is JsonValueKind.True or JsonValueKind.False)
                sports.Add(new ProviderSport(k, title, active.GetBoolean()));
        }

        return sports;
    }

    private static IReadOnlyList<ProviderEvent>? ParseEvents(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return null;
        var events = new List<ProviderEvent>();
        foreach (var item in root.EnumerateArray())
        {
            if (Event(item) is { } e)
                events.Add(new ProviderEvent(e.Id, e.Sport, e.Kickoff, e.Home, e.Away));
        }

        return events;
    }

    private static IReadOnlyList<ProviderOddsEvent>? ParseOdds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return null;
        var events = new List<ProviderOddsEvent>();
        foreach (var item in root.EnumerateArray())
        {
            if (Event(item) is not { } e)
                continue;
            var bookmakers = new List<ProviderBookmaker>();
            if (item.TryGetProperty("bookmakers", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in list.EnumerateArray())
                {
                    if (Text(b, "key") is not { } bookKey || Text(b, "title") is not { } title)
                        continue;
                    var markets = new List<ProviderMarket>();
                    if (b.TryGetProperty("markets", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in ms.EnumerateArray())
                        {
                            if (Text(m, "key") is not { } marketKey)
                                continue;
                            var prices = new List<ProviderPrice>();
                            if (m.TryGetProperty("outcomes", out var os) && os.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var o in os.EnumerateArray())
                                {
                                    // Prices are JSON numbers read as decimal (never double); a string or null price is not a price.
                                    if (Text(o, "name") is { } name && o.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Number &&
                                        price.TryGetDecimal(out var value) && !o.TryGetProperty("point", out _))
                                        prices.Add(new ProviderPrice(name, value));
                                }
                            }

                            markets.Add(new ProviderMarket(marketKey, Instant(m, "last_update"), prices));
                        }
                    }

                    bookmakers.Add(new ProviderBookmaker(bookKey, title, markets));
                }
            }

            events.Add(new ProviderOddsEvent(e.Id, e.Sport, e.Kickoff, e.Home, e.Away, bookmakers));
        }

        return events;
    }

    private static (string Id, string Sport, DateTimeOffset Kickoff, string Home, string Away)? Event(JsonElement item) =>
        Text(item, "id") is { } id && IsSafeId(id) && Text(item, "sport_key") is { } sport && Instant(item, "commence_time") is { } kickoff &&
        Text(item, "home_team") is { } home && Text(item, "away_team") is { } away
            ? (id, sport, kickoff, home, away)
            : null;

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 200 } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>An ISO-8601 instant WITH its offset ("…Z"); anything else is not a time.</summary>
    private static DateTimeOffset? Instant(JsonElement item, string name) =>
        Text(item, name) is { } text && (text.EndsWith('Z') || text.Contains('+', StringComparison.Ordinal) || text.LastIndexOf('-') > 9) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    private static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit);

    private static string SafeSegment(string sportKey) =>
        sportKey.Length is > 0 and <= 64 && sportKey.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? sportKey
            : throw new ArgumentException("Not a sport key.", nameof(sportKey));
}
