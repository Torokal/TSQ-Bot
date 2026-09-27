using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Automatic HLTV links from Liquipedia's free MediaWiki API (fallback while LiquipediaDB is not usable): wikitext parsing,
/// the strict matcher (both teams, order-free, normalized name/acronym, ±90 min, exactly one id), the cache (positive
/// forever, misses re-checked later, persisted across restarts), request spacing and budget, failure isolation, and that
/// only the Liquipedia API is ever contacted — never HLTV.
/// </summary>
public sealed class LiquipediaWikiLinkTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero); // 11:00 CEST
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(90);
    private const string Url = "https://www.hltv.org/matches/2398672/match";
    private const string PageTitle = "Stake Ranked/Episode 5/Qualifier";

    /// <summary>Shape copied from the real page read on 2026-09-26 (Eternal Fire vs WBT, R1M6).</summary>
    private const string Bracket = """
        {{Bracket|Bracket/8|id=vgs9GqhoU8
        |R1M5={{Match
            |opponent1={{TeamOpponent|monte}}|opponent2={{TeamOpponent|bb team}}
            |date=September 25, 2026 - 14:15 {{Abbr/CEST}} |finished=true
            |map1={{Map|map=Inferno|finished=true
                |t1firstside=t|t1t=2|t1ct=5|t2t=3|t2ct=10
                |stats=239527|vod=}}
            |hltv=2398676
            }}
        |R1M6={{Match
            |opponent1={{TeamOpponent|ef}}|opponent2={{TeamOpponent|wbt}}
            |date=September 25, 2026 - 11:00 {{Abbr/CEST}} |finished=true
            |kick=StarLadder
            |map1={{Map|map=Cache|finished=true
                |t1firstside=t|t1t=6|t1ct=7|t2t=0|t2ct=6
                |stats=239486|vod=}}
            |map3={{Map|map=Mirage|finished=skip}}
            |hltv=2398672
        	|faceit=1-4f0476ea-ad76-4c2b-9f02-cd52a4939467
            }}
        }}
        """;

    private static EsportsMatch Panda(string id = "1", string a = "Eternal Fire", string? aShort = "EF", string b = "WBT", string? bShort = "WBT",
        DateTimeOffset? start = null, MatchLinks? links = null, string aKey = "ps-team:1", string bKey = "ps-team:2") => new(
        new MatchKey("pandascore", id),
        new TournamentRef("pandascore", "ps-tournament:1", "Stake Ranked Episode 5: Closed Qualifier 2026", "4", null, null, null),
        start ?? Start, true, 3, MatchStatus.Scheduled, "test",
        new MatchOpponent(OpponentKind.Team, new TeamRef("pandascore", aKey, a, aShort), null, OpponentResult.None),
        new MatchOpponent(OpponentKind.Team, new TeamRef("pandascore", bKey, b, bShort), null, OpponentResult.None),
        null, false, false, [], null, null, [],
        Links: links);

    private static WikiMatch W(string o1, string o2, DateTimeOffset start, long? id) => new(o1, o2, start, id);

    // ------------------------------------------------------------------ wikitext

    [Fact]
    public void Known_bracket_match_is_parsed_with_its_utc_start_and_hltv_id()
    {
        var matches = LiquipediaWikitext.ParseMatches(Bracket);
        matches.Should().HaveCount(2, "map sub-templates are not matches");
        matches.Should().ContainEquivalentOf(new WikiMatch("ef", "wbt", Start, 2398672));
        matches.Should().ContainEquivalentOf(new WikiMatch("monte", "bb team", new DateTimeOffset(2026, 9, 25, 12, 15, 0, TimeSpan.Zero), 2398676));
        LiquipediaWikitext.HltvUrl(2398672).Should().Be(Url);
        MatchLinkPolicy.ValidHltvMatchUrl(Url).Should().Be(Url);
    }

    [Fact]
    public void Match_namespace_pages_are_parsed_too()
    {
        const string page = """
            {{#invoke:Lua|invoke|module=MatchGroup|fn=TemplateMatchPage|dev=MischiefMS
                |date=July 18, 2026 - 21:00 {{Abbr/BST}}
                |twitch=EPIC.LAN 1
                |opponent1={{TeamOpponent|glitchtech uk}}|opponent2={{TeamOpponent|voracity esports}}
                |map1={{Map|nuselo=f215006f5997e36a|reversed=|vod=|stats=233115}}
                |hltv=2396037
                |epiclan=epic48-cs2/matches/23390
            }}
            """;
        LiquipediaWikitext.ParseMatches(page).Should().ContainSingle()
            .Which.Should().Be(new WikiMatch("glitchtech uk", "voracity esports", new DateTimeOffset(2026, 7, 18, 20, 0, 0, TimeSpan.Zero), 2396037));
    }

    [Theory]
    [InlineData("2398672a")]
    [InlineData("https://www.hltv.org/matches/2398672/x")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12345678901")]
    [InlineData("2398672 ")] // trimmed → valid? no: covered separately below
    public void Only_plain_numeric_hltv_ids_are_accepted(string raw)
    {
        if (raw == "2398672 ")
        {
            LiquipediaWikitext.ParseHltvId(raw).Should().Be(2398672, "surrounding whitespace is layout, not data");
            return;
        }

        LiquipediaWikitext.ParseHltvId(raw).Should().BeNull();
        var wikitext = Bracket.Replace("|hltv=2398672", "|hltv=" + raw, StringComparison.Ordinal);
        WikiLinkMatcher.Find(Panda(), LiquipediaWikitext.ParseMatches(wikitext), Tolerance).Should().BeNull("a malformed id is never turned into a link");
    }

    [Theory]
    [InlineData("September 25, 2026 - 11:00 {{Abbr/IST}}")] // ambiguous (India / Ireland / Israel)
    [InlineData("September 25, 2026 - 11:00 {{Abbr/CST}}")] // ambiguous (China / US Central)
    [InlineData("September 25, 2026 - 11:00 {{Abbr/XYZ}}")]
    [InlineData("September 25, 2026 - 11:00")]
    [InlineData("September 25, 2026 - 25:00 {{Abbr/CEST}}")]
    [InlineData("Septober 25, 2026 - 11:00 {{Abbr/CEST}}")]
    [InlineData("TBA")]
    public void Unknown_ambiguous_or_missing_time_zones_and_bad_dates_give_no_start(string raw)
    {
        LiquipediaWikitext.ParseDate(raw).Should().BeNull();
        var wikitext = Bracket.Replace("September 25, 2026 - 11:00 {{Abbr/CEST}}", raw, StringComparison.Ordinal);
        WikiLinkMatcher.Find(Panda(), LiquipediaWikitext.ParseMatches(wikitext), Tolerance).Should().BeNull("no reliable start time → no link");
    }

    [Theory]
    [InlineData("Sep 25, 2026 - 11:00 {{Abbr/CEST}}", 9)]
    [InlineData("2026-09-25 - 11:00 {{Abbr/CEST}}", 9)]
    [InlineData("September 25, 2026 - 09:00 {{Abbr/UTC}}", 9)]
    [InlineData("September 25, 2026 - 12:00 {{Abbr/TRT}}", 9)]
    [InlineData("September 25, 2026 - 05:00 {{Abbr/EDT}}", 9)]
    public void Supported_date_formats_convert_to_utc(string raw, int utcHour) =>
        LiquipediaWikitext.ParseDate(raw).Should().Be(new DateTimeOffset(2026, 9, 25, utcHour, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Unbalanced_or_empty_wikitext_is_ignored_without_errors()
    {
        LiquipediaWikitext.ParseMatches(null).Should().BeEmpty();
        LiquipediaWikitext.ParseMatches("").Should().BeEmpty();
        LiquipediaWikitext.ParseMatches("{{Match|opponent1={{TeamOpponent|ef}}|hltv=1").Should().BeEmpty();
        LiquipediaWikitext.ParseMatches("{{Match|opponent1={{TeamOpponent|tbd}}|opponent2={{TeamOpponent|ef}}|date=September 25, 2026 - 11:00 {{Abbr/CEST}}|hltv=5}}")
            .Should().ContainSingle().Which.Opponent1.Should().BeNull("TBD is not a team");
    }

    // ------------------------------------------------------------------ matcher

    [Fact]
    public void Known_match_is_found_by_acronym_in_either_team_order()
    {
        var candidates = LiquipediaWikitext.ParseMatches(Bracket);
        WikiLinkMatcher.Find(Panda(), candidates, Tolerance).Should().Be(Url);
        WikiLinkMatcher.Find(Panda(a: "WBT", aShort: "WBT", b: "Eternal Fire", bShort: "EF"), candidates, Tolerance).Should().Be(Url, "team order does not matter");
    }

    [Fact]
    public void Normalized_names_match()
    {
        var candidates = new[] { W("team vitality", "Natus Vincere", Start, 7), W("eternalfire", "mouz", Start.AddDays(1), 8) };
        WikiLinkMatcher.Find(Panda(a: "Vitality", aShort: null, b: "NATUS VINCERE", bShort: null), candidates, Tolerance)
            .Should().Be("https://www.hltv.org/matches/7/match", "case and filler words like 'team' are ignored");
        WikiLinkMatcher.Find(Panda(a: "Eternal Fire", aShort: null, b: "MOUZ", bShort: null, start: Start.AddDays(1)), candidates, Tolerance)
            .Should().Be("https://www.hltv.org/matches/8/match", "spaces are ignored");
    }

    [Fact]
    public void The_start_time_must_be_within_90_minutes()
    {
        WikiLinkMatcher.Find(Panda(), [W("ef", "wbt", Start.AddMinutes(85), 5)], Tolerance).Should().NotBeNull();
        WikiLinkMatcher.Find(Panda(), [W("ef", "wbt", Start.AddMinutes(-85), 5)], Tolerance).Should().NotBeNull();
        WikiLinkMatcher.Find(Panda(), [W("ef", "wbt", Start.AddMinutes(95), 5)], Tolerance).Should().BeNull();
    }

    [Fact]
    public void Zero_candidates_or_one_team_only_give_no_link()
    {
        WikiLinkMatcher.Find(Panda(), [], Tolerance).Should().BeNull();
        WikiLinkMatcher.Find(Panda(), [W("ef", "monte", Start, 5)], Tolerance).Should().BeNull("both teams must match");
        WikiLinkMatcher.Find(Panda(), [W("ef", "ef", Start, 5)], Tolerance).Should().BeNull();
    }

    [Fact]
    public void Two_different_ids_are_ambiguous_but_the_same_id_twice_is_not()
    {
        WikiLinkMatcher.Find(Panda(), [W("ef", "wbt", Start, 5), W("wbt", "ef", Start.AddMinutes(30), 6)], Tolerance)
            .Should().BeNull("a wrong link is worse than none");
        WikiLinkMatcher.Find(Panda(), [W("ef", "wbt", Start, 5), W("eternal fire", "wbt", Start, 5)], Tolerance)
            .Should().Be("https://www.hltv.org/matches/5/match", "the same match on a tournament page and a Match: page");
    }

    [Fact]
    public void Teams_that_cannot_be_told_apart_get_no_link() =>
        WikiLinkMatcher.Find(Panda(a: "EF", aShort: "EF", b: "EF Academy", bShort: "EF"), [W("ef", "ef academy", Start, 5)], Tolerance)
            .Should().BeNull("both sides share the acronym");

    // ------------------------------------------------------------------ source: requests, cache, isolation

    private sealed record Rig(LiquipediaWikiLinkSource Source, StubHttpHandler Handler, LiquipediaWikiClient Client, FakeTimeProvider Clock, EsportsOptions Options);

    private static Rig Build(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond, FakeTimeProvider? clock = null, int perHour = 60,
        string? lpdbKey = null, ProviderMode mode = ProviderMode.Live, EsportsOptions? esports = null)
    {
        clock ??= new FakeTimeProvider(Start.AddMinutes(-30));
        var handler = new StubHttpHandler(respond);
        var lpOptions = new LiquipediaOptions { WikiBaseUrl = "https://liquipedia.test/", WikiRequestsPerHour = perHour, ApiKey = lpdbKey, TimeoutSeconds = 5 };
        var client = new LiquipediaWikiClient(new HttpClient(handler) { BaseAddress = new Uri("https://liquipedia.test/") },
            Options.Create(lpOptions), new RequestBudget(clock), clock, NullLogger<LiquipediaWikiClient>.Instance);
        var lpdbClient = new LiquipediaClient(new HttpClient(new StubHttpHandler((_, _) => throw new InvalidOperationException("LPDB must not be called"))),
            Options.Create(lpOptions), new RequestBudget(clock), clock, NullLogger<LiquipediaClient>.Instance);
        esports ??= new EsportsOptions();
        var dataMode = new EsportsDataMode(mode);
        var lpdb = new LiquipediaHltvLinkSource(lpdbClient, new PandaProvider(), dataMode, Options.Create(esports), clock, NullLogger<LiquipediaHltvLinkSource>.Instance);
        var source = new LiquipediaWikiLinkSource(client, lpdb, new PandaProvider(), dataMode, Options.Create(esports), clock, NullLogger<LiquipediaWikiLinkSource>.Instance);
        return new Rig(source, handler, client, clock, esports);
    }

    private static Task<HttpResponseMessage> Wiki(HttpRequestMessage request, string wikitext = Bracket)
    {
        var query = request.RequestUri!.Query;
        if (query.Contains("list=search", StringComparison.Ordinal))
            return Task.FromResult(StubHttpHandler.Json("""{"batchcomplete":"","query":{"search":[{"ns":0,"title":"Stake Ranked/Episode 5/Qualifier","timestamp":"2026-09-26T17:15:15Z"}]}}"""));
        var body = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["pages"] = new JsonObject
                {
                    ["1"] = new JsonObject
                    {
                        ["title"] = PageTitle,
                        ["revisions"] = new JsonArray(new JsonObject { ["slots"] = new JsonObject { ["main"] = new JsonObject { ["*"] = wikitext } } }),
                    },
                },
            },
        };
        return Task.FromResult(StubHttpHandler.Json(body.ToJsonString()));
    }

    private static readonly IReadOnlySet<string> FollowsEf = new HashSet<string> { "ps-team:1" };

    /// <summary>Runs work that waits on the fake clock (request spacing), advancing it until the work completes.</summary>
    private static async Task<T> Pump<T>(FakeTimeProvider clock, Task<T> work)
    {
        for (var i = 0; !work.IsCompleted && i < 10_000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        return await work;
    }

    [Fact]
    public async Task A_followed_match_gets_its_link_with_one_search_and_one_wikitext_request_only_from_liquipedia()
    {
        var rig = Build((r, _) => Wiki(r));
        rig.Source.Enabled.Should().BeTrue("live mode without an LPDB key uses the MediaWiki fallback");
        (await Pump(rig.Clock, rig.Source.RefreshAsync([Panda()], FollowsEf, CancellationToken.None))).Should().BeTrue();

        var linked = rig.Source.Apply(Panda());
        linked.Links!.HltvMatchUrl.Should().Be(Url);
        linked.Links.HltvVia.Should().Be(MatchLinks.ViaLiquipedia);
        rig.Handler.Requests.Should().HaveCount(2);
        rig.Handler.Requests.Should().OnlyContain(u => u.Host == "liquipedia.test" && u.AbsolutePath == "/counterstrike/api.php", "no HLTV request is ever made");
        rig.Handler.Requests.Should().OnlyContain(u => u.Query.Contains("action=query", StringComparison.Ordinal) && !u.Query.Contains("action=parse", StringComparison.Ordinal));
        Uri.UnescapeDataString(rig.Handler.Requests[0].Query).Should().Contain("insource:\"Eternal Fire\" insource:\"WBT\"");
    }

    [Fact]
    public async Task A_found_link_is_cached_and_never_requested_again_or_replaced()
    {
        var rig = Build((r, _) => Wiki(r));
        await Pump(rig.Clock, rig.Source.RefreshAsync([Panda()], FollowsEf, CancellationToken.None));
        rig.Clock.Advance(TimeSpan.FromHours(3));
        (await Pump(rig.Clock, rig.Source.RefreshAsync([Panda()], FollowsEf, CancellationToken.None))).Should().BeFalse();
        rig.Handler.Requests.Should().HaveCount(2, "cache reuse");
        rig.Source.Apply(Panda(links: new MatchLinks(HltvMatchUrl: "https://www.hltv.org/matches/1/other"))).Links!.HltvMatchUrl
            .Should().Be("https://www.hltv.org/matches/1/other", "an existing link is never replaced");
    }

    [Fact]
    public async Task A_miss_is_rechecked_later_with_a_growing_interval_never_permanently()
    {
        var empty = Bracket.Replace("|hltv=2398672", "", StringComparison.Ordinal); // editors have not entered it yet
        var wikitext = empty;
        var rig = Build((r, _) => Wiki(r, wikitext));
        var target = Panda(start: Start.AddHours(1));

        await Pump(rig.Clock, rig.Source.RefreshAsync([target], FollowsEf, CancellationToken.None));
        var afterFirst = rig.Handler.Requests.Count;
        afterFirst.Should().BeGreaterThan(0);
        rig.Source.Get(target.Key)!.Url.Should().BeNull();

        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        await Pump(rig.Clock, rig.Source.RefreshAsync([target], FollowsEf, CancellationToken.None));
        rig.Handler.Requests.Should().HaveCount(afterFirst, "not due before 30 minutes");

        rig.Clock.Advance(TimeSpan.FromMinutes(15));
        await Pump(rig.Clock, rig.Source.RefreshAsync([target], FollowsEf, CancellationToken.None));
        var afterSecond = rig.Handler.Requests.Count;
        afterSecond.Should().BeGreaterThan(afterFirst, "re-checked after 30 minutes");
        var entry = rig.Source.Get(target.Key)!;
        (entry.NextCheckAt - entry.CheckedAt).Should().Be(TimeSpan.FromMinutes(60), "the interval doubles per miss");

        wikitext = Bracket.Replace("11:00 {{Abbr/CEST}}", "12:00 {{Abbr/CEST}}", StringComparison.Ordinal); // editor adds id; start 10:00Z
        rig.Clock.Advance(TimeSpan.FromMinutes(61));
        await Pump(rig.Clock, rig.Source.RefreshAsync([target], FollowsEf, CancellationToken.None));
        rig.Source.Apply(target).Links!.HltvMatchUrl.Should().Be(Url, "a miss is never permanent");
    }

    [Fact]
    public async Task The_cache_survives_a_restart_without_new_requests()
    {
        var rig = Build((r, _) => Wiki(r));
        var missing = Panda(id: "2", a: "Monte", aShort: "MON", b: "Other", bShort: "OTH");
        await Pump(rig.Clock, rig.Source.RefreshAsync([Panda(), missing], FollowsEfAndMonte, CancellationToken.None));
        var json = rig.Source.Export();
        var before = rig.Handler.Requests.Count;

        var restarted = Build((_, _) => throw new InvalidOperationException("no request after a restart"), rig.Clock);
        restarted.Source.Restore(json);
        restarted.Source.Apply(Panda()).Links!.HltvMatchUrl.Should().Be(Url);
        (await Pump(rig.Clock, restarted.Source.RefreshAsync([Panda(), missing], FollowsEfAndMonte, CancellationToken.None))).Should().BeFalse();
        restarted.Handler.Requests.Should().BeEmpty("found links and not-yet-due misses come from the restored cache");
        before.Should().BeGreaterThan(0);

        restarted.Source.Restore("{ not json");
        restarted.Source.Apply(Panda()).Links!.HltvMatchUrl.Should().Be(Url, "an unreadable cache is ignored, the current one stays");
    }

    private static readonly IReadOnlySet<string> FollowsEfAndMonte = new HashSet<string> { "ps-team:1" };

    [Fact]
    public async Task Requests_are_spaced_at_least_two_seconds_and_limited_per_poll_and_per_hour()
    {
        var rig = Build((r, _) => Wiki(r, "no matches here"));
        var matches = Enumerable.Range(1, 4).Select(i => Panda(id: i.ToString(System.Globalization.CultureInfo.InvariantCulture), start: Start.AddMinutes(i))).ToList();
        await Pump(rig.Clock, rig.Source.RefreshAsync(matches, FollowsEf, CancellationToken.None));

        var sent = rig.Client.SentAt;
        sent.Count.Should().BeInRange(2, 8);
        sent.Zip(sent.Skip(1), (x, y) => y - x).Should().OnlyContain(gap => gap >= TimeSpan.FromSeconds(2), "MediaWiki terms: at most 1 request per 2 s");
        matches.Count(m => rig.Source.Get(m.Key) is not null).Should().Be(rig.Options.WikiLinkMaxLookupsPerPoll, "only a few lookups per poll");

        var tight = Build((r, _) => Wiki(r, "no matches here"), perHour: 3);
        await Pump(tight.Clock, tight.Source.RefreshAsync(matches, FollowsEf, CancellationToken.None));
        tight.Handler.Requests.Should().HaveCount(3, "the hourly budget refuses the 4th request locally");
        tight.Source.LastOutcome.Should().Be(ProviderOutcome.QuotaExceeded);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderOutcome.TransportError)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderOutcome.TransportError)]
    [InlineData(HttpStatusCode.NotFound, ProviderOutcome.SchemaError)]
    public async Task Upstream_errors_keep_known_links_and_never_throw(HttpStatusCode status, ProviderOutcome expected)
    {
        var rig = Build((r, _) => Wiki(r));
        await Pump(rig.Clock, rig.Source.RefreshAsync([Panda()], FollowsEf, CancellationToken.None));

        var broken = Build((_, _) => Task.FromResult(new HttpResponseMessage(status)), rig.Clock);
        broken.Source.Restore(rig.Source.Export());
        var other = Panda(id: "9", start: Start.AddMinutes(10));
        (await Pump(rig.Clock, broken.Source.RefreshAsync([other], FollowsEf, CancellationToken.None))).Should().BeTrue();
        broken.Source.LastOutcome.Should().Be(expected);
        broken.Source.Apply(Panda()).Links!.HltvMatchUrl.Should().Be(Url, "an outage never removes known links");
        broken.Source.Apply(other).Links.Should().BeNull();
    }

    [Fact]
    public async Task Malformed_json_timeouts_and_429_are_isolated_and_429_pauses_lookups()
    {
        var garbage = Build((_, _) => Task.FromResult(StubHttpHandler.Json("{ not json")));
        await Pump(garbage.Clock, garbage.Source.RefreshAsync([Panda()], FollowsEf, CancellationToken.None));
        garbage.Source.LastOutcome.Should().Be(ProviderOutcome.SchemaError);

        var slow = Build((_, _) => throw new HttpRequestException("connection reset"));
        await Pump(slow.Clock, slow.Source.RefreshAsync([Panda()], FollowsEf, CancellationToken.None));
        slow.Source.LastOutcome.Should().Be(ProviderOutcome.TransportError);

        var limited = Build((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        });
        await Pump(limited.Clock, limited.Source.RefreshAsync([Panda()], FollowsEf, CancellationToken.None));
        limited.Source.LastOutcome.Should().Be(ProviderOutcome.QuotaExceeded);
        limited.Handler.Requests.Should().HaveCount(1);

        limited.Clock.Advance(TimeSpan.FromMinutes(5));
        await Pump(limited.Clock, limited.Source.RefreshAsync([Panda(id: "2")], FollowsEf, CancellationToken.None));
        limited.Handler.Requests.Should().HaveCount(1, "paused until Retry-After");
        limited.Clock.Advance(TimeSpan.FromMinutes(6));
        await Pump(limited.Clock, limited.Source.RefreshAsync([Panda(id: "2")], FollowsEf, CancellationToken.None));
        limited.Handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Only_followed_nearby_unlinked_matches_are_looked_up()
    {
        var rig = Build((r, _) => Wiki(r));
        var notFollowed = Panda(id: "a", a: "Monte", aShort: "MON", b: "Other", bShort: "OTH", aKey: "ps-team:7", bKey: "ps-team:8");
        var tooFar = Panda(id: "b", start: Start.AddHours(5));
        var tooOld = Panda(id: "c", start: Start.AddHours(-20));
        var linked = Panda(id: "d", links: new MatchLinks(HltvMatchUrl: Url));
        var tbd = Panda(id: "e") with { B = MatchOpponent.Tbd };
        (await Pump(rig.Clock, rig.Source.RefreshAsync([notFollowed, tooFar, tooOld, linked, tbd], FollowsEf, CancellationToken.None))).Should().BeFalse();
        (await Pump(rig.Clock, rig.Source.RefreshAsync([Panda()], new HashSet<string>(), CancellationToken.None))).Should().BeFalse("no server follows a team");
        rig.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Off_in_fixture_mode_and_while_liquipediadb_is_usable()
    {
        Build((_, _) => throw new InvalidOperationException()).Source.Enabled.Should().BeTrue();
        Build((_, _) => throw new InvalidOperationException(), mode: ProviderMode.Fixture).Source.Enabled.Should().BeFalse("demo data never links");
        Build((_, _) => throw new InvalidOperationException(), lpdbKey: "test-key-not-real").Source.Enabled.Should().BeFalse("LiquipediaDB first");
        Build((_, _) => throw new InvalidOperationException(), esports: new EsportsOptions { HltvLinksFromWikiApi = false }).Source.Enabled.Should().BeFalse();
        Build((_, _) => throw new InvalidOperationException(), esports: new EsportsOptions { HltvLinksFromLiquipedia = false }).Source.Enabled.Should().BeFalse();
        LiquipediaWikiClient.ConfigurationProblem(new LiquipediaOptions { WikiMinIntervalSeconds = 1 }).Should().Contain("2");
        LiquipediaWikiClient.ConfigurationProblem(new LiquipediaOptions { WikiBaseUrl = "https://www.hltv.org/" }).Should().Contain("liquipedia.net");
    }

    private sealed class PandaProvider : IEsportsDataProvider
    {
        public string Id => "pandascore";

        public ProviderCapability Capabilities => ProviderCapability.Fixtures;

        public bool IsConfigured => true;

        public Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
