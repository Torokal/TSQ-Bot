using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using ToroSquad.Modules.News.Providers;

namespace ToroSquad.Tests.Support;

/// <summary>One synthetic feed item (never real HLTV content: made-up ids, headlines and dates).</summary>
public sealed record FeedItem(long Id, string Title, string? Description = null, DateTimeOffset? Published = null, string? Slug = null, string? LinkOverride = null,
    string? GuidOverride = null);

/// <summary>Builds RSS 2.0 documents in the observed HLTV shape (channel ttl, item title/description/link/guid/pubDate/media).</summary>
public static class NewsFeeds
{
    public static string Rss(IEnumerable<FeedItem> items, int? ttl = 60)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" ?>""").Append('\n');
        sb.Append("""<rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/"><channel>""");
        sb.Append("<title>Synthetic test feed</title><link>https://www.hltv.org/</link><language>en</language>");
        if (ttl is { } t)
            sb.Append("<ttl>").Append(t.ToString(CultureInfo.InvariantCulture)).Append("</ttl>");
        foreach (var item in items)
        {
            var slug = item.Slug ?? "synthetic-" + item.Id.ToString(CultureInfo.InvariantCulture);
            var link = item.LinkOverride ?? $"https://www.hltv.org/news/{item.Id}/{slug}";
            sb.Append("<item><title>").Append(SecurityElement.Escape(item.Title)).Append("</title>");
            if (item.Description is not null)
                sb.Append("<description>").Append(SecurityElement.Escape(item.Description)).Append("</description>");
            sb.Append("<link>").Append(SecurityElement.Escape(link)).Append("</link>");
            sb.Append("""<guid isPermaLink="false">""").Append(item.GuidOverride ?? "hltvnews" + item.Id.ToString(CultureInfo.InvariantCulture)).Append("</guid>");
            if (item.Published is { } p)
                sb.Append("<pubDate>").Append(p.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture)).Append("</pubDate>");
            sb.Append("""<media:content url="https://img-cdn.hltv.org/gallerypicture/x.jpg"></media:content></item>""");
        }

        sb.Append("</channel></rss>");
        return sb.ToString();
    }

    public static HttpResponseMessage Ok(string xml, string contentType = "application/rss+xml", string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, contentType) };
        if (etag is not null)
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("", Encoding.UTF8, "text/html") };
        if (retryAfter is { } r)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(r);
        return response;
    }
}

/// <summary>
/// The scripted "HLTV" and "Liquipedia" of a test: every request is recorded (URL and headers) and answered by the current
/// responder. Registered as the primary handler of the news HTTP clients, so the production client code runs unchanged.
/// </summary>
public sealed class NewsFeedServer : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => NewsFeeds.Status(HttpStatusCode.ServiceUnavailable);

    public List<HttpRequestMessage> Requests { get; } = [];

    public void Serve(IEnumerable<FeedItem> items, int? ttl = 60) => Respond = _ => NewsFeeds.Ok(NewsFeeds.Rss(items, ttl));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}

/// <summary>Forwards to a shared handler without ever disposing it (the HTTP client factory rotates its handlers).</summary>
public sealed class ForwardingHandler(HttpMessageHandler target) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _invoker = new(target, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _invoker.SendAsync(request, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _invoker.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> for client-level tests.</summary>
public sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public static class NewsFeedRequests
{
    public static bool IsFeed(HttpRequestMessage r) => r.RequestUri!.AbsoluteUri == HltvRssClient.FeedUrl;
}
