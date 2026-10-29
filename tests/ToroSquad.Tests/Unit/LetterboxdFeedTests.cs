using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Letterboxd;
using ToroSquad.Modules.Letterboxd.Application;
using ToroSquad.Modules.Letterboxd.Domain;
using ToroSquad.Modules.Letterboxd.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Letterboxd feed reading and cards: the hardened RSS parser on the RECORDED feed shape (sanitized fixture of
/// 2026-10-29) and on synthetic feeds, the entry identity, link/poster allow-lists, ratings, the review HTML reader, the
/// HTTP client (status mapping, challenge pages, bounded body, validators, only the member's feed address) and the card
/// renderer (one card per entry, no fake rating, spoiler and mention safety). No network.
/// </summary>
public sealed class LetterboxdFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static FeedParseResult Parse(string xml, string member = "lordtoro", int excerpt = 800) =>
        LetterboxdRssParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), member, Now, excerpt);

    private static LetterboxdEntry Recorded(long viewingId) => Parse(LetterboxdFeeds.Recorded).Items.Single(i => i.ViewingId == viewingId);

    private static LetterboxdCardRenderer Renderer() =>
        new(new LocalizationCatalog([new LocalizationSource(typeof(LetterboxdModule).Assembly, "ToroSquad.Modules.Letterboxd.Localization")]),
            Options.Create(new LetterboxdOptions()));

    // ---------- the recorded feed shape ----------

    [Fact]
    public void The_recorded_feed_parses_every_entry_with_its_own_identity_and_member_check()
    {
        var result = Parse(LetterboxdFeeds.Recorded);

        result.Outcome.Should().Be(FeedParseOutcome.Ok);
        result.TotalItems.Should().Be(7);
        result.Skipped.Total.Should().Be(0);
        result.Items.Select(i => i.ViewingId).Should().Equal(1559817559, 1548125398, 1511524309, 1430876508, 1400163932, 1338418054, 1023751338);
        result.TtlMinutes.Should().BeNull("the real feed sends no <ttl>");
    }

    [Fact]
    public void World_war_z_is_an_unrated_unreviewed_rewatch_card_with_no_invented_rating_or_review()
    {
        var wwz = Recorded(1548125398);

        wwz.GuidKind.Should().Be(EntryGuidKind.Watch);
        wwz.FilmSlug.Should().Be("world-war-z");
        wwz.TmdbMovieId.Should().Be(72190);
        var v = wwz.View;
        v.Url.Should().Be("https://letterboxd.com/lordtoro/film/world-war-z/");
        v.FilmTitle.Should().Be("World War Z");
        v.FilmYear.Should().Be(2013);
        v.RatingHalves.Should().BeNull("no memberRating element = no rating, never 0");
        v.Review.Should().BeNull("'Watched on …' is not a review");
        v.HasReview.Should().BeFalse();
        v.Rewatch.Should().BeTrue();
        v.WatchedDate.Should().Be(new DateOnly(2026, 10, 21));
        v.PublishedAt.Should().Be(new DateTimeOffset(2026, 10, 21, 15, 12, 45, TimeSpan.Zero), "+1300 is converted, not dropped");
        v.Spoilers.Should().BeNull();
        v.PosterUrl.Should().Be("https://a.ltrbxd.com/resized/film-poster/5/7/2/9/2/57292-world-war-z-0-600-0-900-crop.jpg", "the ?v= cache key is dropped");
        v.CardKind.Should().Be(EntryCardKind.Rewatch);
        v.Postable.Should().BeTrue();
    }

    [Fact]
    public void Ratings_rewatches_reviews_and_plain_watches_are_told_apart()
    {
        var hope = Recorded(1559817559).View;
        hope.RatingHalves.Should().Be(4);
        hope.Rewatch.Should().BeFalse();
        hope.CardKind.Should().Be(EntryCardKind.Rating);

        var f4 = Recorded(1511524309).View;
        f4.Url.Should().Be("https://letterboxd.com/lordtoro/film/the-fantastic-4-first-steps/1/", "a later entry of the same film keeps its own link");
        f4.RatingHalves.Should().Be(5);
        f4.CardKind.Should().Be(EntryCardKind.Rewatch, "rating + rewatch is still ONE card");

        var spider = Recorded(1430876508);
        spider.GuidKind.Should().Be(EntryGuidKind.Review);
        spider.View.Review.Should().Be("Synthetic review text with a spoiler: the hero survives.", "the feed's spoiler notice is not review text");
        spider.View.Spoilers.Should().BeTrue();
        spider.View.CardKind.Should().Be(EntryCardKind.Review);

        Recorded(1400163932).View.Spoilers.Should().BeNull("no notice is NOT 'no spoilers' — unknown");

        var plain = Recorded(1338418054).View;
        plain.RatingHalves.Should().BeNull();
        plain.Rewatch.Should().BeFalse();
        plain.HasReview.Should().BeFalse();
        plain.Postable.Should().BeFalse("an unrated first watch without review is not a rewatch and gets no card");
        plain.PosterUrl.Should().BeNull();

        var tv = Recorded(1023751338);
        tv.TmdbMovieId.Should().BeNull();
        tv.TmdbTvId.Should().Be(155292);
    }

    [Fact]
    public void A_review_added_later_keeps_the_viewing_id_only_the_guid_kind_changes()
    {
        var published = Now.AddHours(-1);
        var watch = Parse(LetterboxdFeeds.Rss([new LbItem(700, "Film", published, Rating: "3.0")])).Items.Single();
        var review = Parse(LetterboxdFeeds.Rss([new LbItem(700, "Film", published, Rating: "3.0", Review: "<p>Now with words.</p>")])).Items.Single();

        review.ViewingId.Should().Be(watch.ViewingId);
        watch.GuidKind.Should().Be(EntryGuidKind.Watch);
        review.GuidKind.Should().Be(EntryGuidKind.Review);
        review.View.ContentHash.Should().NotBe(watch.View.ContentHash);
        review.View.MemberHash.Should().NotBe(watch.View.MemberHash, "a review is the member's action");
    }

    [Fact]
    public void Lists_other_members_invalid_values_and_duplicates_are_skipped_and_counted()
    {
        var result = Parse(LetterboxdFeeds.Rss([
            new LbItem(1, "Good", Now.AddHours(-1), Rating: "4.0"),
            new LbItem(2, "A list", Now, GuidOverride: "letterboxd-list-2", LinkOverride: "https://letterboxd.com/lordtoro/list/best/"),
            new LbItem(3, "Someone else", Now, LinkOverride: "https://letterboxd.com/otheruser/film/x/"),
            new LbItem(4, "Look-alike host", Now, LinkOverride: "https://letterboxd.com.evil.example/lordtoro/film/x/"),
            new LbItem(5, "Bad rating", Now, Rating: "3.25"),
            new LbItem(6, "Out of range", Now, Rating: "6.0"),
            new LbItem(7, "Zero", Now, Rating: "0"),
            new LbItem(1, "Good", Now.AddHours(-1), Rating: "4.0"),
            new LbItem(8, "List with an entry's number", Now, GuidOverride: "letterboxd-list-1", LinkOverride: "https://letterboxd.com/lordtoro/list/x/"),
        ]));

        result.Outcome.Should().Be(FeedParseOutcome.Ok);
        result.Items.Select(i => i.ViewingId).Should().Equal(1);
        result.Items[0].View.FilmTitle.Should().Be("Good", "a list guid never feeds the viewing-id space");
        result.Skipped.Should().Be(new SkippedItems(NotAnEntry: 2, ForeignLink: 2, Invalid: 3, Duplicate: 1));
    }

    [Fact]
    public void A_viewing_id_repeated_with_other_content_drops_every_item_with_that_id()
    {
        var published = Now.AddHours(-1);
        var result = Parse(LetterboxdFeeds.Rss([
            new LbItem(10, "Film A", published, Rating: "3.0", Slug: "film-a"),
            new LbItem(11, "Other", published, Rating: "2.0"),
            new LbItem(10, "Film B", published, Rating: "3.0", Slug: "film-b", Review: "<p>x</p>"),
        ]));

        result.Items.Select(i => i.ViewingId).Should().Equal(11);
        result.Skipped.Conflict.Should().Be(2, "watch-10 and review-10 disagree: neither is picked, nothing is merged");
        result.Skipped.Duplicate.Should().Be(0);
    }

    [Fact]
    public void Another_members_feed_a_dtd_html_and_an_empty_feed_are_never_a_valid_answer()
    {
        Parse(LetterboxdFeeds.Rss([new LbItem(1, "x", Now)], member: "otheruser")).Outcome.Should().Be(FeedParseOutcome.Malformed);
        Parse(LetterboxdFeeds.Rss([new LbItem(1, "x", Now)], channelLink: "https://letterboxd.com/sign-in/")).Outcome.Should().Be(FeedParseOutcome.Malformed);
        Parse("""<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY x "boom">]><rss><channel><link>https://letterboxd.com/lordtoro/</link></channel></rss>""")
            .Outcome.Should().Be(FeedParseOutcome.Malformed, "DTDs are prohibited");
        Parse("<!DOCTYPE html><html><body>Just a moment...</body></html>").Outcome.Should().Be(FeedParseOutcome.Malformed);
        var empty = Parse(LetterboxdFeeds.Rss([]));
        empty.Outcome.Should().Be(FeedParseOutcome.Empty);
        empty.Items.Should().BeEmpty();
    }

    [Fact]
    public void Dates_ratings_and_numbers_are_read_exactly()
    {
        LetterboxdRssParser.ParseDate("Thu, 22 Oct 2026 04:12:45 +1300", Now).Should().Be(new DateTimeOffset(2026, 10, 21, 15, 12, 45, TimeSpan.Zero));
        LetterboxdRssParser.ParseDate("Sat, 22 Aug 2026 08:28:53 +1200", Now).Should().Be(new DateTimeOffset(2026, 8, 21, 20, 28, 53, TimeSpan.Zero));
        LetterboxdRssParser.ParseDate("Thu, 29 Oct 2026 20:00:00 +0000", Now).Should().BeNull("more than an hour in the future");
        LetterboxdRssParser.ParseDate("yesterday", Now).Should().BeNull();

        foreach (var (text, halves) in new[] { ("0.5", 1), ("1.0", 2), ("2.5", 5), ("5.0", 10), ("4", 8) })
        {
            EntryRating.TryParse(text, out var h).Should().BeTrue(text);
            h.Should().Be(halves);
        }

        foreach (var bad in new[] { "", "0", "0.0", "5.5", "3.25", "-1", "abc", "3,5" })
            EntryRating.TryParse(bad, out _).Should().BeFalse(bad);
        EntryRating.Stars(7).Should().Be("★★★½");
        EntryRating.Stars(1).Should().Be("½");
        EntryRating.Number(7, CultureInfo.GetCultureInfo("tr-TR")).Should().Be("3,5");
        EntryRating.Number(8, CultureInfo.GetCultureInfo("tr-TR")).Should().Be("4");
        EntryRating.Number(7, CultureInfo.InvariantCulture).Should().Be("3.5");
    }

    [Fact]
    public void Only_https_letterboxd_links_of_the_member_and_official_posters_are_accepted()
    {
        LetterboxdUrl.TryParseEntry("https://letterboxd.com/LordToro/film/Some-Film/2/", "lordtoro", out var slug, out var canonical).Should().BeTrue();
        slug.Should().Be("some-film");
        canonical.Should().Be("https://letterboxd.com/lordtoro/film/some-film/2/");
        foreach (var bad in new[]
                 {
                     "http://letterboxd.com/lordtoro/film/x/", "https://letterboxd.com:8443/lordtoro/film/x/", "https://user@letterboxd.com/lordtoro/film/x/",
                     "https://letterboxd.com/lordtoro/film/x/?a=1", "https://letterboxd.com/lordtoro/list/x/", "https://letterboxd.com/lordtoro/film/x/reviews/",
                     "https://evil.example/lordtoro/film/x/", "javascript:alert(1)",
                 })
            LetterboxdUrl.TryParseEntry(bad, "lordtoro", out _, out _).Should().BeFalse(bad);

        LetterboxdUrl.Poster("https://a.ltrbxd.com/resized/film-poster/1/x-0-600-0-900-crop.jpg?v=1").Should().Be("https://a.ltrbxd.com/resized/film-poster/1/x-0-600-0-900-crop.jpg");
        LetterboxdUrl.Poster("https://evil.example/resized/x.jpg").Should().BeNull();
        LetterboxdUrl.Poster("http://a.ltrbxd.com/resized/x.jpg").Should().BeNull();
        LetterboxdUrl.Poster("https://a.ltrbxd.com/resized/x.svg").Should().BeNull();
        LetterboxdUrl.Feed("LordToro").Should().Be("https://letterboxd.com/lordtoro/rss/");
        LetterboxdUrl.IsValidUsername("lordtoro").Should().BeTrue();
        foreach (var bad in new[] { "", "a", "lord toro", "../x", "lordtoro/rss", "x?y", new string('a', 31) })
            LetterboxdUrl.IsValidUsername(bad).Should().BeFalse(bad);
    }

    // ---------- review HTML ----------

    [Fact]
    public void The_review_reader_keeps_paragraphs_and_line_breaks_and_drops_markup_scripts_and_images()
    {
        var content = ReviewHtml.Read("<p><img src=\"https://a.ltrbxd.com/resized/p.jpg?v=1\"/></p><p>First &amp; <b>bold</b><br/>line two</p>" +
                                      "<script>alert('x')</script><p>Second <a href=\"https://evil.example\">link</a></p><iframe src=\"x\">hidden</iframe>");

        content.FirstImageSource.Should().Be("https://a.ltrbxd.com/resized/p.jpg?v=1");
        content.Paragraphs.Should().Equal("First & bold\nline two", "Second link");
        content.SpoilerNotice.Should().BeFalse();
        ReviewHtml.Read("<p><em>This review may contain spoilers.</em></p><p>Text</p>").Should()
            .BeEquivalentTo(new ReviewContent(["Text"], true, null));
        ReviewHtml.Read("<p>unterminated <b").Paragraphs.Should().Equal("unterminated");
    }

    [Fact]
    public void Long_reviews_are_cut_at_a_word_boundary_and_marked_truncated()
    {
        var words = string.Join(' ', Enumerable.Repeat("kelime", 200));
        var excerpt = ReviewHtml.Excerpt([words], 800, out var truncated);
        truncated.Should().BeTrue();
        excerpt!.Length.Should().BeLessThanOrEqualTo(800);
        excerpt.Should().EndWith("kelime…");
        ReviewHtml.Excerpt(["short"], 800, out truncated).Should().Be("short");
        truncated.Should().BeFalse();
        ReviewHtml.Excerpt([], 800, out _).Should().BeNull();
    }

    [Fact]
    public void Card_hashes_cover_visible_fields_and_the_member_hash_only_member_actions()
    {
        var a = Recorded(1559817559).View;
        a.ContentHash.Should().Be((a with { }).ContentHash);
        (a with { FilmTitle = "Hope (renamed)" }).ContentHash.Should().NotBe(a.ContentHash);
        (a with { FilmTitle = "Hope (renamed)" }).MemberHash.Should().Be(a.MemberHash, "metadata is not a member action");
        (a with { PosterUrl = null }).MemberHash.Should().Be(a.MemberHash);
        (a with { RatingHalves = 5 }).MemberHash.Should().NotBe(a.MemberHash);
        (a with { Rewatch = true }).MemberHash.Should().NotBe(a.MemberHash);
        (a with { Review = "x" }).MemberHash.Should().NotBe(a.MemberHash);
    }

    // ---------- the card ----------

    [Fact]
    public void A_rewatch_card_has_no_rating_field_and_no_review_section()
    {
        var card = Renderer().Render(Recorded(1548125398).View, "tr").Embed!;

        card.Title.Should().Be("🔁 LordToro yeniden izledi");
        card.Url.Should().Be("https://letterboxd.com/lordtoro/film/world-war-z/");
        card.Description.Should().Be("**World War Z** (2013)");
        card.Fields.Select(f => f.Name).Should().Equal("Yeniden izleme", "İzleme tarihi");
        card.Fields.Should().NotContain(f => f.Name == "Puan", "no rating is never shown as 0/5");
        card.Fields.Single(f => f.Name == "İzleme tarihi").Value.Should().Be("21 Ekim 2026");
        card.Footer.Should().Be("Kaynak: Letterboxd");
        card.Timestamp.Should().Be(new DateTimeOffset(2026, 10, 21, 15, 12, 45, TimeSpan.Zero));
        card.ThumbnailUrl.Should().Be("https://a.ltrbxd.com/resized/film-poster/5/7/2/9/2/57292-world-war-z-0-600-0-900-crop.jpg");
    }

    [Fact]
    public void Rated_and_reviewed_entries_render_one_card_with_the_entry_rating_and_a_spoiler_protected_review()
    {
        var renderer = Renderer();
        var rating = renderer.Render(Recorded(1559817559).View, "tr").Embed!;
        rating.Title.Should().Be("⭐ LordToro puan verdi");
        rating.Fields.Single(f => f.Name == "Puan").Value.Should().Be("★★ · 2/5");
        rating.Fields.Should().NotContain(f => f.Name == "Yeniden izleme");

        var review = renderer.Render(Recorded(1430876508).View, "tr");
        review.Mentions.Should().Be(MentionPolicy.None);
        var embed = review.Embed!;
        embed.Title.Should().Be("🎬 LordToro değerlendirdi");
        embed.Fields.Single(f => f.Name == "Puan").Value.Should().Be("★★½ · 2,5/5");
        embed.Description.Should().Contain("**İnceleme — ⚠️ spoiler içerir**").And.Contain("||Synthetic review text with a spoiler: the hero survives.||");

        var unknown = renderer.Render(Recorded(1400163932).View, "en").Embed!;
        unknown.Title.Should().Be("🎬 LordToro reviewed");
        unknown.Description.Should().Contain("**Review (spoiler-protected)**").And.Contain("||Synthetic short review.||", "unknown spoiler state is still hidden");
        DiscordLimits.Validate(review).Should().BeEmpty();
    }

    [Fact]
    public void Feed_text_cannot_ping_break_the_spoiler_or_inject_links_or_markdown()
    {
        var hostile = new EntryView("https://letterboxd.com/lordtoro/film/x/", "**@everyone** [click](https://evil.example) <@&123>", 2020, 6, true,
            new DateOnly(2026, 10, 1), Now, "End||spoiler visible|| @here <@456> ||| [x](https://evil.example)\n\nsecond", false, null, null);

        var message = Renderer().Render(hostile, "tr");
        message.Mentions.PingsAnything.Should().BeFalse();
        var description = message.Embed!.Description!;
        DiscordText.RawMentionPattern().IsMatch(description).Should().BeFalse();
        description.Should().NotContain("https://evil.example").And.NotContain("](https");
        var spoiler = description[description.IndexOf("||", StringComparison.Ordinal)..];
        spoiler.Should().StartWith("||").And.EndWith("||");
        spoiler[2..^2].Should().NotContain("||", "the review cannot close the spoiler early");
        message.Embed.Title.Should().Be("🎬 LordToro değerlendirdi", "review + rating + rewatch is one review card");
        message.Embed.Fields.Select(f => f.Name).Should().Equal("Puan", "Yeniden izleme", "İzleme tarihi");
    }

    [Fact]
    public void A_truncated_review_links_to_the_full_entry()
    {
        var view = Recorded(1400163932).View with { Review = "Cut text…", ReviewTruncated = true };
        Renderer().Render(view, "tr").Embed!.Description.Should().EndWith("[Devamını Letterboxd’da oku](https://letterboxd.com/lordtoro/film/the-odyssey-2026/)");
    }

    // ---------- HTTP client ----------

    private static (LetterboxdRssClient Client, LetterboxdFeedServer Server) Client(Action<LetterboxdOptions>? configure = null)
    {
        var server = new LetterboxdFeedServer();
        var options = new LetterboxdOptions();
        configure?.Invoke(options);
        return (new LetterboxdRssClient(new SingleHandlerFactory(server), Options.Create(options), new FakeTimeProvider(Now), NullLogger<LetterboxdRssClient>.Instance), server);
    }

    [Fact]
    public async Task The_only_request_is_the_member_feed_with_an_identifying_user_agent_and_validators_only_when_given()
    {
        var (client, server) = Client();
        server.ServeXml(LetterboxdFeeds.Recorded);
        var first = await client.FetchAsync(null, CancellationToken.None);
        first.Outcome.Should().Be(FeedOutcome.Ok);
        first.Items.Should().HaveCount(7);
        var request = server.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsoluteUri.Should().Be("https://letterboxd.com/lordtoro/rss/");
        request.Headers.UserAgent.ToString().Should().Contain("TSQBot").And.Contain("https://github.com/Torokal/TSQ-Bot");
        request.Headers.Contains("If-None-Match").Should().BeFalse();
        request.Headers.Contains("Cookie").Should().BeFalse();
        request.Headers.Authorization.Should().BeNull();

        await client.FetchAsync(new FeedValidators("\"abc\"", "Thu, 29 Oct 2026 09:00:00 GMT"), CancellationToken.None);
        server.Requests[1].Headers.GetValues("If-None-Match").Should().Equal("\"abc\"");
        server.Requests[1].Headers.GetValues("If-Modified-Since").Should().Equal("Thu, 29 Oct 2026 09:00:00 GMT");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotModified, FeedOutcome.NotModified, true)]
    [InlineData(HttpStatusCode.Forbidden, FeedOutcome.Blocked, false)]
    [InlineData(HttpStatusCode.Unauthorized, FeedOutcome.Blocked, false)]
    [InlineData(HttpStatusCode.NotFound, FeedOutcome.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, FeedOutcome.RateLimited, false)]
    [InlineData(HttpStatusCode.MovedPermanently, FeedOutcome.Redirected, false)]
    [InlineData(HttpStatusCode.Found, FeedOutcome.Redirected, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FeedOutcome.ServerError, false)]
    [InlineData(HttpStatusCode.BadRequest, FeedOutcome.HttpError, false)]
    public async Task Status_codes_map_to_distinct_outcomes_and_only_success_counts_as_success(HttpStatusCode status, FeedOutcome expected, bool succeeded)
    {
        var (client, server) = Client();
        server.Respond = _ => LetterboxdFeeds.Status(status, status == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(45) : null);
        var result = await client.FetchAsync(null, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.Succeeded.Should().Be(succeeded);
        result.HttpStatus.Should().Be((int)status);
        if (status == HttpStatusCode.TooManyRequests)
            result.RetryAfter.Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public async Task A_200_challenge_or_login_page_is_a_failure_never_an_empty_feed()
    {
        var (client, server) = Client();
        server.Respond = _ => LetterboxdFeeds.Ok("<!DOCTYPE html><html><title>Just a moment...</title></html>", "text/html");
        var html = await client.FetchAsync(null, CancellationToken.None);
        html.Outcome.Should().Be(FeedOutcome.WrongContentType);
        html.Succeeded.Should().BeFalse();

        server.Respond = _ => LetterboxdFeeds.Ok("<html><body>Sign in</body></html>", "application/xml");
        var disguised = await client.FetchAsync(null, CancellationToken.None);
        disguised.Outcome.Should().Be(FeedOutcome.Malformed);
        disguised.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Timeouts_dns_failures_and_oversized_bodies_are_reported_as_such()
    {
        var (client, server) = Client(o => o.MaxFeedBytes = 64 * 1024);
        server.Respond = _ => throw new TaskCanceledException("timeout");
        (await client.FetchAsync(null, CancellationToken.None)).Outcome.Should().Be(FeedOutcome.Timeout);

        server.Respond = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host");
        var dns = await client.FetchAsync(null, CancellationToken.None);
        dns.Outcome.Should().Be(FeedOutcome.TransportError);
        dns.Detail.Should().Be("NameResolutionError", "DNS is told apart from a timeout or a refusal");

        server.ServeXml(LetterboxdFeeds.Rss(Enumerable.Range(1, 150).Select(i => new LbItem(i, new string('x', 150) + i, Now.AddHours(-i), Review: "<p>" + new string('y', 400) + "</p>"))));
        (await client.FetchAsync(null, CancellationToken.None)).Outcome.Should().Be(FeedOutcome.TooLarge);
    }

    [Fact]
    public void The_client_handler_follows_no_redirect_and_keeps_no_cookie()
    {
        using var handler = LetterboxdRssClient.CreateHandler();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
    }

    [Fact]
    public void Options_reject_unsafe_values()
    {
        new LetterboxdOptions().Validate().Should().BeEmpty();
        new LetterboxdOptions { Username = "https://evil.example/" }.Validate().Should().ContainSingle(e => e.Contains("Username"));
        new LetterboxdOptions { PollIntervalMinutes = 1 }.Validate().Should().ContainSingle(e => e.Contains("PollIntervalMinutes"));
        new LetterboxdOptions { ReviewExcerptChars = 2000 }.Validate().Should().ContainSingle(e => e.Contains("ReviewExcerptChars"));
        new LetterboxdOptions().Mode.Should().Be(LetterboxdMode.Off, "the module is off by default");
    }

    [Fact]
    public void Standalone_ratings_are_reported_blocked_and_the_coverage_partial()
    {
        LetterboxdCapabilities.StandaloneRatings.State.Should().Be(CapabilityState.Blocked);
        LetterboxdCapabilities.ReviewDiary.State.Should().Be(CapabilityState.Supported);
        LetterboxdCapabilities.UnratedRewatch.State.Should().Be(CapabilityState.Supported);
        LetterboxdCapabilities.FullCoverage.Should().BeFalse("standalone ratings have no permitted source");
    }
}
