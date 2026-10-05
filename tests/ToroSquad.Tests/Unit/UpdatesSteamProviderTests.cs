using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Domain.Games;
using ToroSquad.Modules.Updates.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The Steam news provider against a scripted server: the exact request (fixed address, AppID, no key, no credentials), every
/// outcome class, the bounded and validated answer, and the link policy. Nothing here touches the network.
/// </summary>
public sealed class UpdatesSteamProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameUpdateDefinition Cs2 = Cs2Game.Definition;

    private static (SteamNewsUpdateProvider Provider, SteamNewsServer Server) Create(UpdatesOptions? options = null)
    {
        var server = new SteamNewsServer();
        var settings = Options.Create(options ?? new UpdatesOptions());
        var clock = new FakeTimeProvider(Now);
        var provider = new SteamNewsUpdateProvider(new ProviderHttp(new SingleHandlerFactory(server), settings, clock, NullLogger<ProviderHttp>.Instance), settings, clock);
        return (provider, server);
    }

    private static SteamParseResult Parse(string json, GameUpdateDefinition? game = null) => SteamNewsParser.Parse(SteamNews.Bytes(json), game ?? Cs2, Now);

    // ---------- the request ----------

    [Fact]
    public async Task The_request_is_the_public_endpoint_for_app_730_without_any_key_or_credentials()
    {
        var (provider, server) = Create();
        server.Serve([SteamNews.Update(10, Now.AddHours(-1))]);
        (await provider.FetchAsync(Cs2, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.Ok);

        var request = server.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=730&count=20&maxlength=0&feeds=steam_community_announcements&format=json");
        request.RequestUri.Query.Should().NotContain("key", "the public method needs no API key");
        request.Headers.Authorization.Should().BeNull();
        request.Headers.Contains("Cookie").Should().BeFalse();
        request.Headers.UserAgent.ToString().Should().Contain("TSQBot").And.Contain("https://github.com/Torokal/TSQ-Bot");
        SteamNewsUpdateProvider.Endpoint.Should().StartWith("https://api.steampowered.com/").And.NotContain("Authed").And.NotContain("partner.");
    }

    [Fact]
    public async Task The_count_comes_from_configuration_and_a_game_without_a_numeric_app_id_is_never_requested()
    {
        var (provider, server) = Create(new UpdatesOptions { ItemsPerRequest = 10 });
        server.Serve([SteamNews.Update(10, Now)]);
        await provider.FetchAsync(Cs2, CancellationToken.None);
        server.Requests.Single().RequestUri!.Query.Should().Contain("count=10");

        foreach (var id in new[] { "730&key=x", "abc", "", "0", "-1", "730 " })
        {
            var result = await provider.FetchAsync(Cs2 with { ProviderGameId = id }, CancellationToken.None);
            result.Outcome.Should().Be(UpdateFetchOutcome.UnexpectedSchema, id);
        }

        server.Requests.Should().ContainSingle("nothing but digits ever reaches the query string");
    }

    [Fact]
    public void The_handler_follows_no_redirects_and_keeps_no_cookies()
    {
        using var handler = ProviderHttp.CreateHandler();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
    }

    // ---------- outcomes ----------

    [Fact]
    public async Task A_valid_answer_is_normalized_and_keeps_the_text_only_as_classifier_input()
    {
        var (provider, server) = Create();
        server.Serve([SteamNews.Update(1845383656000001, Now.AddHours(-2)), SteamNews.Announcement(1845383656000000, Now.AddDays(-3), "Synthetic event")],
            expiresIn: TimeSpan.FromMinutes(60));
        var result = await provider.FetchAsync(Cs2, CancellationToken.None);

        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        result.HttpStatus.Should().Be(200);
        result.SkippedItems.Should().Be(0);
        result.CacheLifetime.Should().Be(TimeSpan.FromMinutes(60));
        var update = result.Items[0];
        update.Provider.Should().Be("steam");
        update.GameKey.Should().Be("cs2");
        update.ExternalId.Should().Be("1845383656000001");
        update.Title.Should().Be("Counter-Strike 2 Update");
        update.CanonicalUrl.Should().Be("https://store.steampowered.com/news/externalpost/steam_community_announcements/1845383656000001");
        update.PublishedAt.Should().Be(Now.AddHours(-2));
        update.Labels.Should().Equal("patchnotes");
        update.Body.Should().Be(SteamNews.PatchNotes);
        result.Items[1].Labels.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_list_is_its_own_outcome()
    {
        var (provider, server) = Create();
        server.Serve([]);
        var result = await provider.FetchAsync(Cs2, CancellationToken.None);
        result.Outcome.Should().Be(UpdateFetchOutcome.Empty);
        result.Succeeded.Should().BeTrue();
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rate_limit_carries_the_bounded_retry_after()
    {
        var (provider, server) = Create();
        server.Respond = _ => SteamNews.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(45));
        var result = await provider.FetchAsync(Cs2, CancellationToken.None);
        result.Outcome.Should().Be(UpdateFetchOutcome.RateLimited);
        result.Succeeded.Should().BeFalse();
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(45));

        server.Respond = _ => SteamNews.Status(HttpStatusCode.TooManyRequests);
        (await provider.FetchAsync(Cs2, CancellationToken.None)).RetryAfter.Should().Be(TimeSpan.FromMinutes(30), "the fallback when the header is missing");
        server.Respond = _ => SteamNews.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(1));
        (await provider.FetchAsync(Cs2, CancellationToken.None)).RetryAfter.Should().Be(TimeSpan.FromMinutes(5));
        server.Respond = _ => SteamNews.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromDays(3));
        (await provider.FetchAsync(Cs2, CancellationToken.None)).RetryAfter.Should().Be(TimeSpan.FromHours(6));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFetchOutcome.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, UpdateFetchOutcome.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UpdateFetchOutcome.ServerError)]
    [InlineData(HttpStatusCode.Forbidden, UpdateFetchOutcome.HttpError)] // observed for an unknown AppID: 403 with "{}"
    [InlineData(HttpStatusCode.NotFound, UpdateFetchOutcome.HttpError)]
    [InlineData(HttpStatusCode.Found, UpdateFetchOutcome.HttpError)]
    [InlineData(HttpStatusCode.NoContent, UpdateFetchOutcome.HttpError)]
    public async Task Error_statuses_are_failures_never_an_empty_list(HttpStatusCode status, UpdateFetchOutcome expected)
    {
        var (provider, server) = Create();
        server.Respond = _ => SteamNews.Status(status);
        var result = await provider.FetchAsync(Cs2, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.Succeeded.Should().BeFalse();
        result.HttpStatus.Should().Be((int)status);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_redirect_is_reported_and_its_target_is_never_requested()
    {
        var (provider, server) = Create();
        server.Respond = _ =>
        {
            var response = SteamNews.Status(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri("https://evil.example/news");
            return response;
        };
        var result = await provider.FetchAsync(Cs2, CancellationToken.None);
        result.Outcome.Should().Be(UpdateFetchOutcome.HttpError);
        result.Detail.Should().Contain("not followed");
        server.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_timeout_and_a_transport_error_are_failures_and_a_shutdown_is_not_swallowed()
    {
        var (provider, server) = Create(new UpdatesOptions { RequestTimeoutSeconds = 5 });
        server.Respond = _ => throw new TaskCanceledException("simulated timeout");
        (await provider.FetchAsync(Cs2, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.Timeout);

        server.Respond = _ => throw new HttpRequestException("simulated", null, HttpStatusCode.BadGateway);
        (await provider.FetchAsync(Cs2, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.TransportError);

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        server.Respond = _ => throw new OperationCanceledException(stopping.Token);
        await provider.Invoking(p => p.FetchAsync(Cs2, stopping.Token)).Should().ThrowAsync<OperationCanceledException>("host shutdown must stay a cancellation");
    }

    [Fact]
    public async Task Malformed_json_wrong_content_type_and_too_deep_nesting_are_malformed()
    {
        var (provider, server) = Create();
        server.Respond = _ => SteamNews.Ok("{\"appnews\":{\"appid\":730,\"newsitems\":[");
        (await provider.FetchAsync(Cs2, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.Malformed);

        server.Respond = _ => SteamNews.Ok("<html><body>Access denied</body></html>", "text/html");
        var html = await provider.FetchAsync(Cs2, CancellationToken.None);
        html.Outcome.Should().Be(UpdateFetchOutcome.Malformed);
        html.Detail.Should().Contain("not JSON");

        var deep = new StringBuilder();
        deep.Append('[', 40).Append(']', 40);
        server.Respond = _ => SteamNews.Ok("{\"appnews\":{\"appid\":730,\"newsitems\":" + deep + "}}");
        (await provider.FetchAsync(Cs2, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.Malformed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"appnews\":[]}")]
    [InlineData("{\"appnews\":{\"newsitems\":[]}}")]
    [InlineData("{\"appnews\":{\"appid\":\"730\",\"newsitems\":[]}}")]
    [InlineData("{\"appnews\":{\"appid\":730}}")]
    [InlineData("{\"appnews\":{\"appid\":730,\"newsitems\":{}}}")]
    [InlineData("{\"appnews\":{\"appid\":730,\"newsitems\":\"none\"}}")]
    public async Task An_answer_that_is_not_the_documented_shape_is_an_unexpected_schema(string json)
    {
        var (provider, server) = Create();
        server.Respond = _ => SteamNews.Ok(json);
        var result = await provider.FetchAsync(Cs2, CancellationToken.None);
        result.Outcome.Should().Be(UpdateFetchOutcome.UnexpectedSchema);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task An_answer_for_another_app_is_refused_as_a_whole()
    {
        var (provider, server) = Create();
        server.Respond = _ => SteamNews.Ok(SteamNews.Json([SteamNews.Update(10, Now)], appId: 570));
        var result = await provider.FetchAsync(Cs2, CancellationToken.None);
        result.Outcome.Should().Be(UpdateFetchOutcome.UnexpectedSchema);
        result.Detail.Should().Contain("another AppID");
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversized_answer_is_refused_by_its_declared_and_by_its_real_size()
    {
        var (provider, server) = Create(new UpdatesOptions { MaxResponseBytes = 64 * 1024 });
        var big = SteamNews.Json([SteamNews.Update(10, Now) with { Contents = new string('x', 80 * 1024) }]);
        server.Respond = _ => SteamNews.Ok(big);
        var declared = await provider.FetchAsync(Cs2, CancellationToken.None);
        declared.Outcome.Should().Be(UpdateFetchOutcome.TooLarge);

        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(Encoding.UTF8.GetBytes(big)) };
        var streamed = await provider.FetchAsync(Cs2, CancellationToken.None);
        streamed.Outcome.Should().Be(UpdateFetchOutcome.TooLarge);
        streamed.Detail.Should().Contain("larger than");
    }

    [Fact]
    public async Task The_cache_lifetime_comes_from_the_answer_itself()
    {
        var (provider, server) = Create();
        server.Serve([SteamNews.Update(10, Now)]);
        (await provider.FetchAsync(Cs2, CancellationToken.None)).CacheLifetime.Should().BeNull("the source declared nothing");

        server.Serve([SteamNews.Update(10, Now)], expiresIn: TimeSpan.FromMinutes(59));
        (await provider.FetchAsync(Cs2, CancellationToken.None)).CacheLifetime.Should().Be(TimeSpan.FromMinutes(59), "Expires minus the answer's own Date");

        server.Respond = _ =>
        {
            var response = SteamNews.Ok(SteamNews.Json([SteamNews.Update(10, Now)]), expiresIn: TimeSpan.FromMinutes(59));
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
            return response;
        };
        (await provider.FetchAsync(Cs2, CancellationToken.None)).CacheLifetime.Should().Be(TimeSpan.FromMinutes(10), "Cache-Control max-age wins");

        server.Respond = _ =>
        {
            var response = SteamNews.Ok(SteamNews.Json([SteamNews.Update(10, Now)]), expiresIn: TimeSpan.FromMinutes(59));
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            return response;
        };
        (await provider.FetchAsync(Cs2, CancellationToken.None)).CacheLifetime.Should().BeNull();

        server.Respond = _ => SteamNews.Ok(SteamNews.Json([SteamNews.Update(10, Now)]), expiresIn: TimeSpan.FromMinutes(-5));
        (await provider.FetchAsync(Cs2, CancellationToken.None)).CacheLifetime.Should().BeNull("an already expired answer declares no lifetime");
    }

    // ---------- single posts ----------

    [Fact]
    public void Broken_or_foreign_posts_are_skipped_and_counted_without_breaking_the_rest()
    {
        var good = SteamNews.Update(100, Now.AddHours(-1));
        var result = Parse(SteamNews.Json(
        [
            good,
            good, // the same id twice
            SteamNews.Update(101, Now) with { Feed = "PC Gamer", Url = "https://steamstore-a.akamaihd.net/news/externalpost/PC Gamer/101" },
            SteamNews.Update(102, Now) with { AppId = 570 },
            SteamNews.Update(103, Now) with { Title = "   " },
            SteamNews.Update(104, Now) with { Gid = "12ab" },
            SteamNews.Update(105, Now) with { Gid = "" },
            SteamNews.Update(106, Now) with { Url = "https://evil.example/news/externalpost/steam_community_announcements/106" },
        ]));
        result.Outcome.Should().Be(SteamParseOutcome.Ok);
        result.Items.Should().ContainSingle().Which.ExternalId.Should().Be("100");
        result.Skipped.Should().Be(7);
    }

    [Fact]
    public void An_answer_with_posts_but_none_usable_is_an_unexpected_schema_not_an_empty_list()
    {
        var result = Parse(SteamNews.Json([SteamNews.Update(101, Now) with { Url = "https://evil.example/x" }, SteamNews.Update(102, Now) with { Feed = "other" }]));
        result.Outcome.Should().Be(SteamParseOutcome.UnexpectedSchema);
        result.Skipped.Should().Be(2);
    }

    [Fact]
    public void A_missing_or_future_date_is_unknown_and_never_invented()
    {
        var result = Parse(SteamNews.Json(
        [
            SteamNews.Update(200, Now) with { Published = null },
            SteamNews.Update(201, Now) with { Published = Now.AddDays(2) },
            SteamNews.Update(202, Now) with { Published = Now.AddMinutes(20) }, // small clock difference: accepted
        ]));
        result.Items.Single(i => i.ExternalId == "200").PublishedAt.Should().BeNull();
        result.Items.Single(i => i.ExternalId == "201").PublishedAt.Should().BeNull();
        result.Items.Single(i => i.ExternalId == "202").PublishedAt.Should().Be(Now.AddMinutes(20));
    }

    [Fact]
    public void Titles_text_and_tags_are_bounded_and_the_title_is_one_clean_line()
    {
        var result = Parse(SteamNews.Json(
        [
            SteamNews.Update(300, Now) with
            {
                Title = "  Counter-Strike 2\r\n\tUpdate\u0000  " + new string('y', 1000),
                Contents = new string('z', GameUpdateCandidate.BodyMax + 5000),
                Tags = Enumerable.Range(0, 50).Select(i => "tag" + i).ToArray(),
            },
        ]));
        var item = result.Items.Single();
        item.Title.Should().StartWith("Counter-Strike 2 Update yyy").And.HaveLength(GameUpdateCandidate.TitleMax);
        item.Body.Should().HaveLength(GameUpdateCandidate.BodyMax);
        item.Labels.Should().HaveCount(SteamNewsParser.MaxLabels);
    }

    [Fact]
    public void A_title_is_never_longer_than_its_bound_whatever_the_spacing()
    {
        foreach (var title in new[] { new string('a', 299) + " b c", new string('a', 298) + "  bc", string.Join(' ', Enumerable.Repeat("ab", 200)), new string('a', 299) + "😀" })
        {
            var parsed = Parse(SteamNews.Json([SteamNews.Update(310, Now) with { Title = title }])).Items.Single().Title;
            parsed.Length.Should().BeLessThanOrEqualTo(GameUpdateCandidate.TitleMax);
            parsed.Should().NotEndWith(" ");
            char.IsHighSurrogate(parsed[^1]).Should().BeFalse("an emoji is never cut in half");
        }
    }

    [Fact]
    public void A_post_with_bytes_that_are_not_text_is_skipped_and_the_others_are_kept()
    {
        var bytes = SteamNews.Bytes(SteamNews.Json([SteamNews.Update(320, Now, "BROKEN#TITLE"), SteamNews.Update(321, Now)]));
        bytes[Array.IndexOf(bytes, (byte)'#')] = 0xFF; // not valid UTF-8 inside one title
        var result = SteamNewsParser.Parse(bytes, Cs2, Now);
        result.Outcome.Should().Be(SteamParseOutcome.Ok);
        result.Items.Should().ContainSingle().Which.ExternalId.Should().Be("321");
        result.Skipped.Should().Be(1);
    }

    [Fact]
    public void At_most_a_hundred_posts_are_read_from_one_answer()
    {
        var result = Parse(SteamNews.Json(Enumerable.Range(1, 150).Select(i => SteamNews.Update(1000 + i, Now.AddMinutes(-i)))));
        result.Items.Should().HaveCount(SteamNewsParser.MaxItems);
    }

    [Fact]
    public void The_content_hash_changes_with_everything_a_decision_depends_on_and_with_nothing_else()
    {
        GameUpdateCandidate Item(SteamPost post) => Parse(SteamNews.Json([post])).Items.Single();
        var post = SteamNews.Update(400, Now.AddHours(-1));
        var hash = Item(post).ContentHash;
        Item(post).ContentHash.Should().Be(hash);
        Item(post with { External = false }).ContentHash.Should().Be(hash, "is_external_url is not used");
        Item(post with { Title = "Counter-Strike 2 Update!" }).ContentHash.Should().NotBe(hash);
        Item(post with { Published = Now }).ContentHash.Should().NotBe(hash);
        Item(post with { Tags = [] }).ContentHash.Should().NotBe(hash);
        Item(post with { Contents = SteamNews.PatchNotes + " more" }).ContentHash.Should().NotBe(hash);
    }

    // ---------- link policy ----------

    [Theory]
    [InlineData("https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/777")]
    [InlineData("https://store.steampowered.com/news/externalpost/steam_community_announcements/777")]
    [InlineData("https://STORE.steampowered.com/news/externalpost/steam_community_announcements/777")]
    public void The_steam_redirector_link_of_the_post_is_accepted_and_shown_on_the_store_host(string url)
    {
        SteamNewsUrl.TryCanonical(url, "777", out var canonical).Should().BeTrue();
        canonical.Should().Be("https://store.steampowered.com/news/externalpost/steam_community_announcements/777");
        SteamNewsUrl.IsCanonical(canonical).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://store.steampowered.com/news/externalpost/steam_community_announcements/777")] // not https
    [InlineData("https://store.steampowered.com:8443/news/externalpost/steam_community_announcements/777")]
    [InlineData("https://user:pass@store.steampowered.com/news/externalpost/steam_community_announcements/777")]
    [InlineData("https://store.steampowered.com.evil.example/news/externalpost/steam_community_announcements/777")]
    [InlineData("https://evil.example/news/externalpost/steam_community_announcements/777")]
    [InlineData("https://steamcommunity.com/ogg/730/announcements/detail/777")]
    [InlineData("https://store.steampowered.com/news/externalpost/PC Gamer/777")] // a press feed's post
    [InlineData("https://store.steampowered.com/news/externalpost/steam_community_announcements/778")] // another post
    [InlineData("https://store.steampowered.com/news/externalpost/steam_community_announcements/777/extra")]
    [InlineData("https://store.steampowered.com/news/externalpost/steam_community_announcements/777?next=https://evil.example")]
    [InlineData("https://store.steampowered.com/news/externalpost/steam_community_announcements/777#x")]
    [InlineData("https://store.steampowered.com/news/externalpost/steam_community_announcements/../../login")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_link_is_refused(string? url)
    {
        SteamNewsUrl.TryCanonical(url, "777", out var canonical).Should().BeFalse();
        canonical.Should().BeEmpty();
    }

    [Fact]
    public void Only_the_exact_card_form_counts_as_canonical()
    {
        SteamNewsUrl.IsCanonical("https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/777").Should().BeFalse("the card shows the store host");
        SteamNewsUrl.IsCanonical("https://store.steampowered.com/news/externalpost/steam_community_announcements/777/").Should().BeFalse();
        SteamNewsUrl.IsCanonical("https://evil.example/").Should().BeFalse();
        SteamNewsUrl.TryCanonical(SteamNews.CdnUrl("777"), "77x", out _).Should().BeFalse("the id itself must be numeric");
        SteamNewsUrl.IsGid("123456789012345678901").Should().BeFalse("longer than a 64-bit id");
    }

    /// <summary>A JSON body without a declared length (as a chunked answer would be).</summary>
    private sealed class UnsizedContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnsizedContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(_bytes));
    }
}
