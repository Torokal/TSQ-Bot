using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Domain;

namespace ToroSquad.Modules.News.Providers;

public enum FeedOutcome
{
    Ok = 0,

    /// <summary>304: the feed did not change since the stored validators (only valid after a baseline).</summary>
    NotModified = 1,

    /// <summary>A 3xx answer: redirects are never followed.</summary>
    Redirected = 2,

    /// <summary>401/403 (or an access page instead of XML): access refused. Never bypassed.</summary>
    Blocked = 3,
    RateLimited = 4,
    ServerError = 5,
    HttpError = 6,
    Timeout = 7,
    TransportError = 8,

    /// <summary>200 but not an RSS/XML document (for example an HTML challenge page).</summary>
    WrongContentType = 9,
    TooLarge = 10,
    Malformed = 11,

    /// <summary>A well-formed feed without items: not a failure of the source, but never enough for a baseline.</summary>
    Empty = 12,
}

public sealed record FeedFetchResult(
    FeedOutcome Outcome,
    int? HttpStatus,
    IReadOnlyList<NewsArticle> Items,
    int SkippedItems,
    int? TtlMinutes,
    string? ETag,
    string? LastModified,
    TimeSpan? RetryAfter,
    string? Detail,
    DateTimeOffset At)
{
    /// <summary>The feed answered as a feed (new content, unchanged, or empty). Everything else is a failure, never "no news".</summary>
    public bool Succeeded => Outcome is FeedOutcome.Ok or FeedOutcome.NotModified or FeedOutcome.Empty;

    public static FeedFetchResult Fail(FeedOutcome outcome, int? status, string detail, DateTimeOffset at, TimeSpan? retryAfter = null) =>
        new(outcome, status, [], 0, null, null, null, retryAfter, detail, at);
}

/// <summary>The stored cache validators sent as If-None-Match / If-Modified-Since (the feed did not send any when observed).</summary>
public sealed record FeedValidators(string? ETag, string? LastModified);

/// <summary>
/// The ONE request TSQ makes to HLTV: GET https://www.hltv.org/rss/news (the official RSS feed) — a fixed address, never
/// a user supplied URL, never an article or team page. No redirects are followed, no cookies are kept, no credentials or
/// other providers' headers are sent, the body is bounded before and after decompression, and there is no inline retry
/// (the poller backs off). A 403, a 429 or an HTML access page is reported as such and never worked around.
/// </summary>
public sealed class HltvRssClient(IHttpClientFactory factory, IOptions<NewsOptions> options, TimeProvider clock, ILogger<HltvRssClient> logger)
{
    public const string HttpClientName = "news-hltv-rss";
    public const string FeedUrl = "https://www.hltv.org/rss/news";

    public async Task<FeedFetchResult> FetchAsync(FeedValidators? validators, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var http = factory.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.RequestTimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(FeedUrl));
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
                    return new(FeedOutcome.NotModified, status, [], 0, null, validators?.ETag, validators?.LastModified, null, null, at);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return FeedFetchResult.Fail(FeedOutcome.Blocked, status, $"HTTP {status} (access refused; not bypassed)", at);
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
                return FeedFetchResult.Fail(FeedOutcome.WrongContentType, status, $"content type '{Short(mediaType)}' is not RSS/XML", at);
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
            var parsed = HltvRssParser.Parse(buffer, at, maxCharacters: o.MaxFeedBytes);
            var etagHeader = response.Headers.ETag?.ToString();
            var lastModifiedHeader = response.Content.Headers.LastModified?.ToString("r", System.Globalization.CultureInfo.InvariantCulture);
            return parsed.Outcome switch
            {
                FeedParseOutcome.Ok => new(FeedOutcome.Ok, status, parsed.Items, parsed.Skipped, parsed.TtlMinutes, etagHeader, lastModifiedHeader, null, parsed.Detail, at),
                FeedParseOutcome.Empty => new(FeedOutcome.Empty, status, [], 0, parsed.TtlMinutes, etagHeader, lastModifiedHeader, null, parsed.Detail, at),
                _ => FeedFetchResult.Fail(FeedOutcome.Malformed, status, parsed.Detail ?? "malformed feed", at),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FeedFetchResult.Fail(FeedOutcome.Timeout, null, $"timed out after {o.RequestTimeoutSeconds}s", clock.GetUtcNow());
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("news feed transport error: {Error}", ex.GetType().Name);
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
