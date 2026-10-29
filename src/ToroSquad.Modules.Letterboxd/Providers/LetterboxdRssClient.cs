using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Letterboxd.Application;
using ToroSquad.Modules.Letterboxd.Domain;

namespace ToroSquad.Modules.Letterboxd.Providers;

public enum FeedOutcome
{
    Ok = 0,

    /// <summary>304: unchanged since the stored validators (only meaningful after a baseline; the feed sent none when observed).</summary>
    NotModified = 1,

    /// <summary>A 3xx answer: redirects are never followed (a login or another member's page is not this feed).</summary>
    Redirected = 2,

    /// <summary>401/403: access refused. Never bypassed.</summary>
    Blocked = 3,
    RateLimited = 4,
    ServerError = 5,
    HttpError = 6,
    Timeout = 7,
    TransportError = 8,

    /// <summary>200 but not RSS/XML — for example an HTML challenge or login page. Never read as "no entries".</summary>
    WrongContentType = 9,
    TooLarge = 10,
    Malformed = 11,

    /// <summary>A well-formed feed of the member without items: not a failure, but never enough for a baseline.</summary>
    Empty = 12,

    /// <summary>404: the member does not exist (or renamed). A failure, never "no entries".</summary>
    NotFound = 13,
}

public sealed record FeedFetchResult(
    FeedOutcome Outcome,
    int? HttpStatus,
    IReadOnlyList<LetterboxdEntry> Items,
    int TotalItems,
    SkippedItems Skipped,
    int? TtlMinutes,
    string? ETag,
    string? LastModified,
    TimeSpan? RetryAfter,
    string? Detail,
    DateTimeOffset At)
{
    /// <summary>The feed answered as the member's feed (new content, unchanged, or empty). Everything else is a failure.</summary>
    public bool Succeeded => Outcome is FeedOutcome.Ok or FeedOutcome.NotModified or FeedOutcome.Empty;

    public static FeedFetchResult Fail(FeedOutcome outcome, int? status, string detail, DateTimeOffset at, TimeSpan? retryAfter = null) =>
        new(outcome, status, [], 0, SkippedItems.None, null, null, null, retryAfter, detail, at);
}

/// <summary>Stored cache validators sent as If-None-Match / If-Modified-Since (only after a baseline, only when the feed sent them).</summary>
public sealed record FeedValidators(string? ETag, string? LastModified);

/// <summary>
/// The ONE request TSQ makes to Letterboxd: GET https://letterboxd.com/&lt;username&gt;/rss/ — the member's official public RSS
/// feed, built from the validated configured username (never from feed data or user input). No redirects followed, no
/// cookies kept (the feed sets a CSRF cookie that is dropped), no credentials, a bounded body before and after decompression,
/// no inline retry (the poller backs off). Profile, film, list and image pages are never requested; a 403, a 429 or an HTML
/// challenge page is reported as such and never worked around.
/// </summary>
public sealed class LetterboxdRssClient(IHttpClientFactory factory, IOptions<LetterboxdOptions> options, TimeProvider clock, ILogger<LetterboxdRssClient> logger)
{
    public const string HttpClientName = "letterboxd-rss";

    public string FeedUrl => LetterboxdUrl.Feed(options.Value.Username);

    public async Task<FeedFetchResult> FetchAsync(FeedValidators? validators, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!LetterboxdUrl.IsValidUsername(o.Username))
            return FeedFetchResult.Fail(FeedOutcome.HttpError, null, "invalid configured username", clock.GetUtcNow());
        var http = factory.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.RequestTimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(LetterboxdUrl.Feed(o.Username)));
            request.Headers.UserAgent.ParseAdd(o.UserAgent);
            request.Headers.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9, text/xml;q=0.8");
            if (validators?.ETag is { Length: > 0 } etag)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (validators?.LastModified is { Length: > 0 } lastModified)
                request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            var at = clock.GetUtcNow();
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.NotModified:
                    return new(FeedOutcome.NotModified, status, [], 0, SkippedItems.None, null, validators?.ETag, validators?.LastModified, null, null, at);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return FeedFetchResult.Fail(FeedOutcome.Blocked, status, $"HTTP {status} (access refused or challenge; not bypassed)", at);
                case HttpStatusCode.NotFound:
                    return FeedFetchResult.Fail(FeedOutcome.NotFound, status, "HTTP 404 (member not found)", at);
                case HttpStatusCode.TooManyRequests:
                    return FeedFetchResult.Fail(FeedOutcome.RateLimited, status, "HTTP 429", at, RetryAfter(response, at, TimeSpan.FromMinutes(30)));
                default:
                    if (status is >= 300 and < 400)
                        return FeedFetchResult.Fail(FeedOutcome.Redirected, status, $"HTTP {status} redirect (not followed)", at);
                    return FeedFetchResult.Fail(status >= 500 ? FeedOutcome.ServerError : FeedOutcome.HttpError, status, $"HTTP {status}", at,
                        status is 502 or 503 or 504 ? RetryAfter(response, at) : null);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!IsXml(mediaType))
                return FeedFetchResult.Fail(FeedOutcome.WrongContentType, status, $"content type '{Short(mediaType)}' is not RSS/XML (challenge or login page?)", at);
            if (response.Content.Headers.ContentLength is { } declared && declared > o.MaxFeedBytes)
                return FeedFetchResult.Fail(FeedOutcome.TooLarge, status, $"declared {declared} bytes > {o.MaxFeedBytes}", at);

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > o.MaxFeedBytes)
                    return FeedFetchResult.Fail(FeedOutcome.TooLarge, status, $"body larger than {o.MaxFeedBytes} bytes", at);
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            var parsed = LetterboxdRssParser.Parse(buffer, o.Account, at, o.ReviewExcerptChars, maxCharacters: o.MaxFeedBytes);
            var etagHeader = response.Headers.ETag?.ToString();
            var lastModifiedHeader = response.Content.Headers.LastModified?.ToString("r", CultureInfo.InvariantCulture);
            return parsed.Outcome switch
            {
                FeedParseOutcome.Ok => new(FeedOutcome.Ok, status, parsed.Items, parsed.TotalItems, parsed.Skipped, parsed.TtlMinutes, etagHeader, lastModifiedHeader, null, parsed.Detail, at),
                FeedParseOutcome.Empty => new(FeedOutcome.Empty, status, [], 0, parsed.Skipped, parsed.TtlMinutes, etagHeader, lastModifiedHeader, null, parsed.Detail, at),
                _ => FeedFetchResult.Fail(FeedOutcome.Malformed, status, parsed.Detail ?? "malformed feed", at),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FeedFetchResult.Fail(FeedOutcome.Timeout, null, $"timed out after {o.RequestTimeoutSeconds}s", clock.GetUtcNow());
        }
        catch (HttpRequestException ex)
        {
            // DNS, connection refused, TLS … (HttpRequestError tells them apart without exposing the address).
            logger.LogDebug("letterboxd feed transport error: {Error}", ex.HttpRequestError);
            return FeedFetchResult.Fail(FeedOutcome.TransportError, null, ex.HttpRequestError.ToString(), clock.GetUtcNow());
        }
    }

    private static bool IsXml(string mediaType) =>
        mediaType.Equals("application/rss+xml", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The handler: no redirects, no cookies, no proxy credentials, decompression on.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
}
