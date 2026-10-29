using System.Globalization;
using System.Net;
using System.Security;
using System.Text;

namespace ToroSquad.Tests.Support;

/// <summary>
/// One synthetic feed entry (made-up ids, films, dates and texts) in the observed Letterboxd shape. <see cref="Rating"/> is
/// the feed's memberRating text ("3.5") or null for no element; <see cref="Rewatch"/> null leaves the element out;
/// <see cref="Review"/> non-null makes a "letterboxd-review-N" item with that HTML as review paragraphs.
/// </summary>
public sealed record LbItem(
    long Id,
    string Film,
    DateTimeOffset Published,
    string? Rating = null,
    bool? Rewatch = false,
    string? Review = null,
    string? Slug = null,
    int? Year = 2020,
    DateOnly? Watched = null,
    bool Spoilers = false,
    string? LinkOverride = null,
    string? GuidOverride = null,
    int? Index = null);

/// <summary>Builds RSS documents in the observed shape of https://letterboxd.com/&lt;member&gt;/rss/ (2026-10-29).</summary>
public static class LetterboxdFeeds
{
    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "letterboxd", name);

    public static string Recorded => File.ReadAllText(FixturePath("lordtoro-rss-2026-10-29-sanitized.xml"));

    public static string Rss(IEnumerable<LbItem> items, string member = "lordtoro", string? channelLink = null)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version='1.0' encoding='utf-8'?>\n");
        sb.Append("""<rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:letterboxd="https://letterboxd.com" xmlns:tmdb="https://themoviedb.org"><channel>""");
        sb.Append("<title>Letterboxd - Synthetic</title><link>").Append(channelLink ?? $"https://letterboxd.com/{member}/").Append("</link><description>Synthetic test feed</description>");
        foreach (var item in items)
        {
            var slug = item.Slug ?? "film-" + item.Id.ToString(CultureInfo.InvariantCulture);
            var link = item.LinkOverride ?? $"https://letterboxd.com/{member}/film/{slug}/" + (item.Index is { } i ? i.ToString(CultureInfo.InvariantCulture) + "/" : "");
            var kind = item.Review is null ? "watch" : "review";
            var watched = item.Watched ?? DateOnly.FromDateTime(item.Published.UtcDateTime);
            sb.Append("<item><title>").Append(SecurityElement.Escape(item.Film + (item.Spoilers ? " (contains spoilers)" : ""))).Append("</title>");
            sb.Append("<link>").Append(SecurityElement.Escape(link)).Append("</link>");
            sb.Append("""<guid isPermaLink="false">""").Append(item.GuidOverride ?? $"letterboxd-{kind}-{item.Id}").Append("</guid>");
            sb.Append("<pubDate>").Append(item.Published.ToUniversalTime().ToString("ddd, d MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture)).Append("</pubDate>");
            sb.Append("<letterboxd:watchedDate>").Append(watched.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</letterboxd:watchedDate>");
            if (item.Rewatch is { } rw)
                sb.Append("<letterboxd:rewatch>").Append(rw ? "Yes" : "No").Append("</letterboxd:rewatch>");
            sb.Append("<letterboxd:filmTitle>").Append(SecurityElement.Escape(item.Film)).Append("</letterboxd:filmTitle>");
            if (item.Year is { } y)
                sb.Append("<letterboxd:filmYear>").Append(y.ToString(CultureInfo.InvariantCulture)).Append("</letterboxd:filmYear>");
            if (item.Rating is { } r)
                sb.Append("<letterboxd:memberRating>").Append(SecurityElement.Escape(r)).Append("</letterboxd:memberRating>");
            sb.Append("<letterboxd:memberLike>No</letterboxd:memberLike><tmdb:movieId>").Append(item.Id.ToString(CultureInfo.InvariantCulture)).Append("</tmdb:movieId>");
            var description = $"<p><img src=\"https://a.ltrbxd.com/resized/film-poster/1/{slug}-0-600-0-900-crop.jpg?v=abc\"/></p> " +
                              (item.Review is null
                                  ? $"<p>Watched on {watched.ToString("dddd MMMM d, yyyy", CultureInfo.InvariantCulture)}.</p>"
                                  : (item.Spoilers ? "<p><em>This review may contain spoilers.</em></p> " : "") + item.Review);
            sb.Append("<description><![CDATA[ ").Append(description).Append(" ]]></description><dc:creator>Synthetic</dc:creator></item>");
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

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null, string body = "", string contentType = "text/html")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (retryAfter is { } r)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(r);
        return response;
    }
}

/// <summary>The scripted "Letterboxd" of a test: every request is recorded and answered by the current responder.</summary>
public sealed class LetterboxdFeedServer : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => LetterboxdFeeds.Status(HttpStatusCode.ServiceUnavailable);

    public List<HttpRequestMessage> Requests { get; } = [];

    public void Serve(IEnumerable<LbItem> items) => Respond = _ => LetterboxdFeeds.Ok(LetterboxdFeeds.Rss(items));

    public void ServeXml(string xml) => Respond = _ => LetterboxdFeeds.Ok(xml);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}
