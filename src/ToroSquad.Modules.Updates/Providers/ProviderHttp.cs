using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Providers;

/// <summary>
/// One bounded JSON answer of a provider request. Anything but <see cref="UpdateFetchOutcome.Ok"/> is a failure with its
/// kind; <see cref="Body"/> is only set on success.
/// </summary>
public sealed record ProviderHttpAnswer(UpdateFetchOutcome Outcome, int? HttpStatus, ReadOnlyMemory<byte> Body, TimeSpan? CacheLifetime, TimeSpan? RetryAfter, string? Detail)
{
    public bool Succeeded => Outcome == UpdateFetchOutcome.Ok;

    public UpdateFetchResult AsFailure() => UpdateFetchResult.Fail(Outcome, HttpStatus, Detail ?? Outcome.ToString(), RetryAfter);
}

/// <summary>
/// The one way this module's providers talk HTTP: a GET of a fixed, provider-built address that answers JSON. No API key,
/// no credentials header, no cookies. Redirects are not followed, the body is bounded, there is no inline retry (the poller
/// backs off), and every failure is reported as its own kind — never as an empty answer.
/// </summary>
public sealed class ProviderHttp(IHttpClientFactory factory, IOptions<UpdatesOptions> options, TimeProvider clock, ILogger<ProviderHttp> logger)
{
    public async Task<ProviderHttpAnswer> GetJsonAsync(string clientName, Uri address, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var http = factory.CreateClient(clientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.RequestTimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd(o.UserAgent);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Fail(UpdateFetchOutcome.RateLimited, status, "HTTP 429", RetryAfter(response, clock.GetUtcNow(), TimeSpan.FromMinutes(30)));
            if (status is >= 300 and < 400)
                return Fail(UpdateFetchOutcome.HttpError, status, $"HTTP {status} redirect (not followed)");
            if (status >= 500)
                return Fail(UpdateFetchOutcome.ServerError, status, $"HTTP {status}", status is 502 or 503 or 504 ? RetryAfter(response, clock.GetUtcNow()) : null);
            if (response.StatusCode != HttpStatusCode.OK)
                return Fail(UpdateFetchOutcome.HttpError, status, $"HTTP {status}");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                return Fail(UpdateFetchOutcome.Malformed, status, $"content type '{Short(mediaType)}' is not JSON");
            if (response.Content.Headers.ContentLength is { } declared && declared > o.MaxResponseBytes)
                return Fail(UpdateFetchOutcome.TooLarge, status, $"declared {declared} bytes > {o.MaxResponseBytes}");

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > o.MaxResponseBytes)
                    return Fail(UpdateFetchOutcome.TooLarge, status, $"body larger than {o.MaxResponseBytes} bytes");
                buffer.Write(chunk, 0, read);
            }

            return new ProviderHttpAnswer(UpdateFetchOutcome.Ok, status, buffer.ToArray(), CacheLifetime(response), null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(UpdateFetchOutcome.Timeout, null, $"timed out after {o.RequestTimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("update source transport error ({Client}): {Error}", clientName, ex.GetType().Name);
            return Fail(UpdateFetchOutcome.TransportError, null, ex.HttpRequestError.ToString());
        }
        catch (IOException ex)
        {
            logger.LogDebug("update source read error ({Client}): {Error}", clientName, ex.GetType().Name);
            return Fail(UpdateFetchOutcome.TransportError, null, "response body could not be read");
        }
    }

    private static ProviderHttpAnswer Fail(UpdateFetchOutcome outcome, int? status, string detail, TimeSpan? retryAfter = null) =>
        new(outcome, status, ReadOnlyMemory<byte>.Empty, null, retryAfter, detail);

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

    /// <summary>The handler of every provider client: no redirects, no cookies, decompression on.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
}
