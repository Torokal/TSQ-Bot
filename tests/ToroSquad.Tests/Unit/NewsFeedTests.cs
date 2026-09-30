using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Domain;
using ToroSquad.Modules.News.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ News feed reading: the hardened RSS parser, the news-link allow-list, plain-text handling and the HTTP client
/// (status mapping, no redirects, bounded body, validators, only the official feed address). Synthetic feeds only.
/// </summary>
public sealed class NewsFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static FeedParseResult Parse(string xml) => HltvRssParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), Now);

    // ---------- parser ----------

    [Fact]
    public void A_valid_feed_yields_every_item_with_id_canonical_link_title_date_and_ttl()
    {
        var result = Parse(NewsFeeds.Rss([
            new FeedItem(45700, "Aurora sign someone", "The Turkish side made a move.", Now.AddHours(-1), "aurora-sign-someone"),
            new FeedItem(45701, "Other news", null, Now.AddHours(-2)),
        ]));

        result.Outcome.Should().Be(FeedParseOutcome.Ok);
        result.TtlMinutes.Should().Be(60);
        result.Skipped.Should().Be(0);
        result.Items.Should().HaveCount(2);
        var first = result.Items[0];
        first.ArticleId.Should().Be(45700);
        first.CanonicalUrl.Should().Be("https://www.hltv.org/news/45700/aurora-sign-someone");
        first.Title.Should().Be("Aurora sign someone");
        first.PublishedAt.Should().Be(Now.AddHours(-1));
        first.MatchText.Should().Be("The Turkish side made a move.");
        result.Items[1].MatchText.Should().BeEmpty("a missing description is fine");
    }

    [Fact]
    public void Cdata_entities_and_html_in_the_description_become_plain_text_without_fetching_anything()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel><ttl>60</ttl>
            <item><title>XANTARES: &quot;we are back&quot; &amp; more</title>
            <description><![CDATA[<p>Aurora&#39;s <b>star</b> spoke.<img src="https://evil.example/x.png"><script>alert(1)</script><a href="https://evil.example">link</a></p>]]></description>
            <link>https://www.hltv.org/news/45702/xantares-we-are-back</link><guid isPermaLink="false">hltvnews45702</guid>
            <pubDate>Wed, 30 Sep 2026 09:50:00 GMT</pubDate></item>
            </channel></rss>
            """;
        var item = Parse(xml).Items.Single();
        item.Title.Should().Be("XANTARES: \"we are back\" & more");
        item.MatchText.Should().Be("Aurora's star spoke. link");
        item.MatchText.Should().NotContain("<").And.NotContain("evil").And.NotContain("alert");
    }

    [Fact]
    public void Missing_invalid_and_future_dates_are_treated_as_missing()
    {
        HltvRssParser.ParseDate(null, Now).Should().BeNull();
        HltvRssParser.ParseDate("not a date", Now).Should().BeNull();
        HltvRssParser.ParseDate("Wed, 30 Sep 2026 09:50:00 GMT", Now).Should().Be(new DateTimeOffset(2026, 9, 30, 9, 50, 0, TimeSpan.Zero));
        HltvRssParser.ParseDate("Wed, 30 Sep 2026 12:50:00 +0300", Now).Should().Be(new DateTimeOffset(2026, 9, 30, 9, 50, 0, TimeSpan.Zero));
        HltvRssParser.ParseDate("Fri, 30 Oct 2026 09:50:00 GMT", Now).Should().BeNull("a month in the future is not a publication time");
        HltvRssParser.ParseDate("Sat, 01 Jan 1994 09:50:00 GMT", Now).Should().BeNull();
    }

    [Fact]
    public void One_broken_item_is_skipped_but_a_feed_without_any_usable_item_is_malformed()
    {
        var partial = Parse(NewsFeeds.Rss([
            new FeedItem(45703, "Good", Published: Now),
            new FeedItem(45704, "Bad link", LinkOverride: "https://www.hltv.org/matches/1/x"),
            new FeedItem(45705, "", Published: Now),
            new FeedItem(45706, "Guid names another article", GuidOverride: "hltvnews1"),
        ]));
        partial.Outcome.Should().Be(FeedParseOutcome.Ok);
        partial.Items.Select(i => i.ArticleId).Should().Equal(45703);
        partial.Skipped.Should().Be(3);
        partial.Detail.Should().Contain("3 of 4");

        var broken = Parse(NewsFeeds.Rss([new FeedItem(45707, "x", LinkOverride: "https://evil.example/news/1/x")]));
        broken.Outcome.Should().Be(FeedParseOutcome.Malformed, "partial success must never be reported as a verified feed");

        Parse(NewsFeeds.Rss([])).Outcome.Should().Be(FeedParseOutcome.Empty);
    }

    [Theory]
    [InlineData("<html><body>Just a moment...</body></html>")]
    [InlineData("<rss version=\"2.0\"><channel><item>")]
    [InlineData("not xml at all")]
    [InlineData("<feed><entry/></feed>")]
    public void Non_rss_or_truncated_documents_are_malformed(string body) =>
        Parse(body).Outcome.Should().Be(FeedParseOutcome.Malformed);

    [Fact]
    public void Dtd_and_external_entities_are_rejected()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///etc/passwd"><!ENTITY big "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA">]>
            <rss version="2.0"><channel><item><title>&xxe;&big;</title><link>https://www.hltv.org/news/1/a</link></item></channel></rss>
            """;
        var result = Parse(xxe);
        result.Outcome.Should().Be(FeedParseOutcome.Malformed);
        result.Detail.Should().Contain("XML");
    }

    [Fact]
    public void Excessive_nesting_and_item_counts_are_rejected()
    {
        var deep = "<rss version=\"2.0\"><channel><item><title>" + string.Concat(Enumerable.Repeat("<x>", 30)) + "t" +
                   string.Concat(Enumerable.Repeat("</x>", 30)) + "</title><link>https://www.hltv.org/news/1/a</link></item></channel></rss>";
        Parse(deep).Outcome.Should().Be(FeedParseOutcome.Malformed);

        var many = NewsFeeds.Rss(Enumerable.Range(1, HltvRssParser.MaxItems + 1).Select(i => new FeedItem(i, "t" + i)));
        Parse(many).Outcome.Should().Be(FeedParseOutcome.Malformed);
    }

    [Fact]
    public void Characters_beyond_the_limit_are_rejected()
    {
        var big = NewsFeeds.Rss([new FeedItem(1, new string('a', 5000))]);
        HltvRssParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(big)), Now, maxCharacters: 1000).Outcome.Should().Be(FeedParseOutcome.Malformed);
    }

    [Fact]
    public void An_item_right_after_the_ttl_element_is_not_swallowed()
    {
        const string xml = """<rss version="2.0"><channel><ttl>60</ttl><item><title>First</title><link>https://www.hltv.org/news/9/first</link></item></channel></rss>""";
        var result = Parse(xml);
        result.TtlMinutes.Should().Be(60);
        result.Items.Should().ContainSingle(i => i.ArticleId == 9);
    }

    [Fact]
    public void Duplicate_ids_in_one_feed_count_once()
    {
        var result = Parse(NewsFeeds.Rss([new FeedItem(5, "A"), new FeedItem(5, "A again", Slug: "other")]));
        result.Items.Should().ContainSingle();
        result.Skipped.Should().Be(1);
    }

    // ---------- links ----------

    [Theory]
    [InlineData("https://www.hltv.org/news/45597/without-a-roof-officially-seek-roof", 45597, "https://www.hltv.org/news/45597/without-a-roof-officially-seek-roof")]
    [InlineData("https://WWW.HLTV.ORG/news/12/Some-Slug?utm=x#top", 12, "https://www.hltv.org/news/12/some-slug")]
    [InlineData("https://www.hltv.org/news/12/some-slug/", 12, "https://www.hltv.org/news/12/some-slug")]
    public void News_links_are_accepted_and_normalized(string url, long id, string canonical)
    {
        NewsUrl.TryParse(url, out var parsedId, out var parsed).Should().BeTrue();
        parsedId.Should().Be(id);
        parsed.Should().Be(canonical);
    }

    [Theory]
    [InlineData("http://www.hltv.org/news/1/a")]
    [InlineData("https://hltv.org/news/1/a")]
    [InlineData("https://www.hltv.org.evil.example/news/1/a")]
    [InlineData("https://evilhltv.org/news/1/a")]
    [InlineData("https://www.hltv.org:8443/news/1/a")]
    [InlineData("https://user:pw@www.hltv.org/news/1/a")]
    [InlineData("https://www.hltv.org/matches/1/a")]
    [InlineData("https://www.hltv.org/news/0/a")]
    [InlineData("https://www.hltv.org/news/abc/a")]
    [InlineData("https://www.hltv.org/news/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Anything_else_is_rejected(string url) => NewsUrl.TryParse(url, out _, out _).Should().BeFalse();

    [Fact]
    public void The_same_id_with_another_slug_is_the_same_article()
    {
        NewsUrl.TryParse("https://www.hltv.org/news/77/old-slug", out var a, out _).Should().BeTrue();
        NewsUrl.TryParse("https://www.hltv.org/news/77/new-slug", out var b, out _).Should().BeTrue();
        a.Should().Be(b);
    }

    // ---------- HTTP client ----------

    private static (HltvRssClient Client, NewsFeedServer Server) Client(NewsOptions? options = null)
    {
        var server = new NewsFeedServer();
        var client = new HltvRssClient(new SingleHandlerFactory(server), Options.Create(options ?? new NewsOptions()), new FakeTimeProvider(Now),
            NullLogger<HltvRssClient>.Instance);
        return (client, server);
    }

    [Fact]
    public async Task Only_the_official_feed_is_requested_with_a_contact_user_agent_and_no_credentials()
    {
        var (client, server) = Client();
        server.Serve([new FeedItem(1, "A", Published: Now)]);
        var result = await client.FetchAsync(null, CancellationToken.None);

        result.Outcome.Should().Be(FeedOutcome.Ok);
        var request = server.Requests.Single();
        request.RequestUri!.AbsoluteUri.Should().Be("https://www.hltv.org/rss/news");
        request.Method.Should().Be(HttpMethod.Get);
        request.Headers.Authorization.Should().BeNull();
        request.Headers.Contains("Cookie").Should().BeFalse();
        request.Headers.UserAgent.ToString().Should().Contain("TSQBot").And.Contain("github.com/Torokal/TSQ-Bot");
        request.Headers.Contains("If-None-Match").Should().BeFalse("no validators before a baseline");
    }

    [Fact]
    public async Task Validators_are_sent_and_304_is_reported_as_unchanged()
    {
        var (client, server) = Client();
        server.Respond = _ => NewsFeeds.Status(HttpStatusCode.NotModified);
        var result = await client.FetchAsync(new FeedValidators("\"v1\"", "Wed, 30 Sep 2026 09:00:00 GMT"), CancellationToken.None);
        result.Outcome.Should().Be(FeedOutcome.NotModified);
        result.Succeeded.Should().BeTrue();
        server.Requests.Single().Headers.GetValues("If-None-Match").Should().Equal("\"v1\"");
        server.Requests.Single().Headers.GetValues("If-Modified-Since").Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, FeedOutcome.Blocked)]
    [InlineData(HttpStatusCode.Unauthorized, FeedOutcome.Blocked)]
    [InlineData(HttpStatusCode.TooManyRequests, FeedOutcome.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FeedOutcome.ServerError)]
    [InlineData(HttpStatusCode.InternalServerError, FeedOutcome.ServerError)]
    [InlineData(HttpStatusCode.NotFound, FeedOutcome.HttpError)]
    [InlineData(HttpStatusCode.Found, FeedOutcome.Redirected)]
    [InlineData(HttpStatusCode.MovedPermanently, FeedOutcome.Redirected)]
    public async Task Failures_are_classified_never_as_no_news(HttpStatusCode status, FeedOutcome expected)
    {
        var (client, server) = Client();
        server.Respond = _ =>
        {
            var response = NewsFeeds.Status(status);
            if ((int)status is >= 300 and < 400)
                response.Headers.Location = new Uri("https://www.hltv.org/elsewhere");
            return response;
        };
        var result = await client.FetchAsync(null, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.Succeeded.Should().BeFalse();
        result.Items.Should().BeEmpty();
        server.Requests.Should().ContainSingle("no inline retry, no redirect follow-up");
    }

    [Fact]
    public async Task A_429_carries_its_retry_after()
    {
        var (client, server) = Client();
        server.Respond = _ => NewsFeeds.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(90));
        (await client.FetchAsync(null, CancellationToken.None)).RetryAfter.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public async Task An_html_page_or_an_oversized_body_is_refused()
    {
        var (client, server) = Client(new NewsOptions { MaxFeedBytes = 16 * 1024 });
        server.Respond = _ => NewsFeeds.Ok("<html>challenge</html>", "text/html");
        (await client.FetchAsync(null, CancellationToken.None)).Outcome.Should().Be(FeedOutcome.WrongContentType);

        server.Serve(Enumerable.Range(1, 200).Select(i => new FeedItem(i, new string('x', 200))));
        (await client.FetchAsync(null, CancellationToken.None)).Outcome.Should().Be(FeedOutcome.TooLarge);
    }

    [Fact]
    public async Task A_timeout_and_a_transport_error_are_reported_as_such()
    {
        var server = new StubHttpHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new HltvRssClient(new SingleHandlerFactory(server), Options.Create(new NewsOptions { RequestTimeoutSeconds = 1 }), TimeProvider.System,
            NullLogger<HltvRssClient>.Instance);
        var timeout = client.FetchAsync(null, CancellationToken.None);
        (await timeout).Outcome.Should().Be(FeedOutcome.Timeout);

        var broken = new StubHttpHandler((_, _) => throw new HttpRequestException("boom"));
        var client2 = new HltvRssClient(new SingleHandlerFactory(broken), Options.Create(new NewsOptions()), TimeProvider.System, NullLogger<HltvRssClient>.Instance);
        (await client2.FetchAsync(null, CancellationToken.None)).Outcome.Should().Be(FeedOutcome.TransportError);
    }

    [Fact]
    public void The_real_handler_follows_no_redirects_and_keeps_no_cookies()
    {
        using var handler = HltvRssClient.CreateHandler();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
    }
}
