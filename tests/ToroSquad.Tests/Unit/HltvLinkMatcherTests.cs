using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// PandaScore match ↔ Liquipedia match (for its editor-entered HLTV link). A wrong link is worse than none: a link is
/// attached only for exactly one distinct valid HLTV URL with the same two teams inside the time tolerance.
/// </summary>
public sealed class HltvLinkMatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 17, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(90);
    private const string Url1 = "https://www.hltv.org/matches/2388001/vitality-vs-magic";
    private const string Url2 = "https://www.hltv.org/matches/2388002/vitality-vs-magic";

    private static EsportsMatch M(string source, string id, string a, string? b, DateTimeOffset start, string? hltv = null) => new(
        new MatchKey(source, id),
        new TournamentRef(source, "t", "StarLadder StarSeries Fall 2026", "1", null, null, null),
        start, true, 3, MatchStatus.Scheduled, "test",
        new MatchOpponent(OpponentKind.Team, new TeamRef(source, source + ":" + a, a, null), null, OpponentResult.None),
        b is null ? MatchOpponent.Tbd : new MatchOpponent(OpponentKind.Team, new TeamRef(source, source + ":" + b, b, null), null, OpponentResult.None),
        null, false, false, [], null, null, [],
        Links: hltv is null ? null : new MatchLinks(HltvMatchUrl: hltv));

    private static EsportsMatch Panda(string a = "Vitality", string? b = "MAGIC", DateTimeOffset? start = null) => M("pandascore", "1", a, b, start ?? Start);

    private static EsportsMatch Lp(string id, string a, string b, DateTimeOffset start, string? hltv) => M("liquipedia:counterstrike", id, a, b, start, hltv);

    [Fact]
    public void Same_teams_in_either_order_with_normalized_names_and_close_time_get_the_link()
    {
        HltvLinkMatcher.Find(Panda(), [Lp("x", "Team Vitality", "Magic Esports", Start.AddMinutes(20), Url1)], Tolerance).Should().Be(Url1);
        HltvLinkMatcher.Find(Panda(), [Lp("x", "magic", "VITALITY", Start.AddMinutes(-60), Url1)], Tolerance).Should().Be(Url1, "order and case do not matter");
    }

    [Fact]
    public void Different_team_or_time_outside_the_tolerance_gets_no_link()
    {
        HltvLinkMatcher.Find(Panda(), [Lp("x", "Vitality", "MOUZ", Start, Url1)], Tolerance).Should().BeNull();
        HltvLinkMatcher.Find(Panda(), [Lp("x", "Vitality", "MAGIC", Start.AddHours(3), Url1)], Tolerance).Should().BeNull();
    }

    [Fact]
    public void Two_different_candidates_are_ambiguous_and_get_no_link()
    {
        var candidates = new[] { Lp("a", "Vitality", "MAGIC", Start, Url1), Lp("b", "Vitality", "MAGIC", Start.AddMinutes(45), Url2) };
        HltvLinkMatcher.Find(Panda(), candidates, Tolerance).Should().BeNull("a wrong link is worse than none");
    }

    [Fact]
    public void Duplicate_records_with_the_same_url_are_not_ambiguous()
    {
        var candidates = new[] { Lp("a", "Vitality", "MAGIC", Start, Url1), Lp("b", "Team Vitality", "MAGIC", Start.AddMinutes(5), Url1) };
        HltvLinkMatcher.Find(Panda(), candidates, Tolerance).Should().Be(Url1);
    }

    [Theory]
    [InlineData("https://evil.example/matches/2388001/vitality-vs-magic")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public void Candidates_without_a_valid_hltv_url_are_ignored(string? url) =>
        HltvLinkMatcher.Find(Panda(), [Lp("x", "Vitality", "MAGIC", Start, url)], Tolerance).Should().BeNull();

    [Fact]
    public void Tbd_or_unscheduled_targets_and_same_team_twice_get_no_link()
    {
        HltvLinkMatcher.Find(Panda(b: null), [Lp("x", "Vitality", "MAGIC", Start, Url1)], Tolerance).Should().BeNull();
        HltvLinkMatcher.Find(Panda() with { ScheduledStartUtc = null }, [Lp("x", "Vitality", "MAGIC", Start, Url1)], Tolerance).Should().BeNull();
        HltvLinkMatcher.Find(Panda("Vitality", "Team Vitality"), [Lp("x", "Vitality", "Vitality", Start, Url1)], Tolerance).Should().BeNull();
    }

    [Fact]
    public async Task Pandascore_matches_get_liquipedias_hltv_link_end_to_end_in_fixture_mode()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" });
        var source = host.Services.GetRequiredService<LiquipediaHltvLinkSource>();
        source.Enabled.Should().BeTrue("PandaScore supplies the data, Liquipedia (fixture) the links");
        await host.Services.GetRequiredService<EsportsPoller>().RefreshMatchesAsync(CancellationToken.None);

        var matches = host.Services.GetRequiredService<EsportsCache>().Matches.Data!;
        matches.Single(m => m.Key.Id == "910001").Links!.HltvMatchUrl
            .Should().Be("https://www.hltv.org/matches/1000001/demo-fixture-not-a-real-match", "same teams and time as Liquipedia DemoMst26_R01-M001");
        matches.Where(m => m.Key.Id != "910001").Should().OnlyContain(m => m.Links == null || m.Links.HltvMatchUrl == null, "no invented links");

        var withLink = matches.Single(m => m.Key.Id == "910001") with { Links = null };
        source.Apply(withLink).Links!.HltvMatchUrl.Should().NotBeNull();
        source.Apply(withLink with { Links = new MatchLinks(HltvMatchUrl: Url1) }).Links!.HltvMatchUrl.Should().Be(Url1, "an existing link is never replaced");
    }

    [Fact]
    public async Task A_liquipedia_outage_keeps_the_known_links_and_changes_nothing_else()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start.AddHours(-1));
        var lpMatch = System.Text.Json.Nodes.JsonNode.Parse("""
            {"match2id":"L1","pagename":"Cup/2026","tournament":"Cup","date":"2026-09-25 17:10:00","dateexact":1,"finished":0,"bestof":3,
             "links":{"hltv":{"1":{"1":"https://www.hltv.org/matches/2388001/vitality-vs-magic","2":0}}},
             "match2opponents":[{"type":"team","name":"Team Vitality","teamtemplate":{"page":"Team_Vitality","name":"Team Vitality"}},
                                {"type":"team","name":"MAGIC","teamtemplate":{"page":"MAGIC","name":"MAGIC"}}]}
            """)!;
        var handler = new StubHttpHandler((_, n) => Task.FromResult(n == 1
            ? StubHttpHandler.Json(new System.Text.Json.Nodes.JsonObject { ["result"] = new System.Text.Json.Nodes.JsonArray(lpMatch) }.ToJsonString())
            : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)));
        var lpOptions = new ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaOptions
        {
            ApiKey = "test-key-not-real",
            UserAgent = "TSQBot-tests/0 (https://localhost; tests)",
            MaxRetries = 0,
            PageSize = 50,
        };
        var client = new ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.liquipedia.test/api/v3/") },
            Microsoft.Extensions.Options.Options.Create(lpOptions), new ToroSquad.Modules.Esports.Providers.Liquipedia.RequestBudget(clock), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaClient>.Instance);
        var panda = new FakeProvider();
        var source = new LiquipediaHltvLinkSource(client, panda, new ToroSquad.Modules.Esports.Providers.Fixtures.EsportsDataMode(ToroSquad.Modules.Esports.Providers.Fixtures.ProviderMode.Live),
            Microsoft.Extensions.Options.Options.Create(new EsportsOptions()), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LiquipediaHltvLinkSource>.Instance);
        var window = new MatchWindow(Start.AddHours(-12), Start.AddHours(48));

        await source.RefreshIfDueAsync(window, CancellationToken.None);
        source.Apply(Panda()).Links!.HltvMatchUrl.Should().Be(Url1);

        clock.Advance(TimeSpan.FromMinutes(31)); // next refresh fails
        await source.RefreshIfDueAsync(window, CancellationToken.None);
        source.LastOutcome.Should().Be(ProviderOutcome.TransportError);
        source.CandidateCount.Should().Be(1, "an outage never removes known links");
        source.Apply(Panda()).Links!.HltvMatchUrl.Should().Be(Url1);
        handler.Requests.Should().HaveCount(2);

        await source.RefreshIfDueAsync(window, CancellationToken.None);
        handler.Requests.Should().HaveCount(2, "no extra request before the next refresh time");
    }

    private sealed class FakeProvider : IEsportsDataProvider
    {
        public string Id => "pandascore";

        public ProviderCapability Capabilities => ProviderCapability.Fixtures;

        public bool IsConfigured => true;

        public Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Link_source_is_off_when_liquipedia_is_the_match_provider_or_not_configured()
    {
        await using (var liquipedia = await TestHost.CreateAsync())
            liquipedia.Services.GetRequiredService<LiquipediaHltvLinkSource>().Enabled.Should().BeFalse("Liquipedia links are native then");

        await using (var off = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore", ["Esports:HltvLinksFromLiquipedia"] = "false" }))
            off.Services.GetRequiredService<LiquipediaHltvLinkSource>().Enabled.Should().BeFalse("switched off by the operator");

        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start);
        LiquipediaHltvLinkSource Live(string? key, string? ua) => new(
            new ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaClient(new HttpClient(new StubHttpHandler((_, _) => throw new InvalidOperationException("no request expected"))),
                Microsoft.Extensions.Options.Options.Create(new ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaOptions { ApiKey = key, UserAgent = ua }),
                new ToroSquad.Modules.Esports.Providers.Liquipedia.RequestBudget(clock), clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaClient>.Instance),
            new FakeProvider(), new ToroSquad.Modules.Esports.Providers.Fixtures.EsportsDataMode(ToroSquad.Modules.Esports.Providers.Fixtures.ProviderMode.Live),
            Microsoft.Extensions.Options.Options.Create(new EsportsOptions()), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LiquipediaHltvLinkSource>.Instance);

        var noKey = Live(null, "TSQBot/0 (https://localhost)");
        noKey.Enabled.Should().BeFalse("live Liquipedia needs an approved key: until then enrichment is BLOCKED/optional");
        (await noKey.RefreshIfDueAsync(new MatchWindow(Start.AddHours(-12), Start.AddHours(48)), CancellationToken.None)).Should().BeNull("no request without a key");
        Live("test-key-not-real", null).Enabled.Should().BeTrue("the contact User-Agent is not a LiquipediaDB requirement");
    }

    [Fact]
    public async Task A_restart_reuses_the_persisted_links_without_an_extra_request()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" });
        var poller = host.Services.GetRequiredService<EsportsPoller>();
        var source = host.Services.GetRequiredService<LiquipediaHltvLinkSource>();
        await poller.RefreshMatchesAsync(CancellationToken.None);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToroSquad.Infrastructure.Persistence.ToroDbContext>();
            var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.Set<ToroSquad.Modules.Esports.Persistence.ProviderStateEntity>(), s => s.Key == source.StateKey, TestContext.Current.CancellationToken);
            source.StateKey.Should().Be("liquipedia-fixture:hltv-links", "fixture links never land in the live cache");
            row.DataJson.Should().Contain("https://www.hltv.org/matches/1000001/demo-fixture-not-a-real-match");
            row.LastAttemptAt.Should().NotBeNull();

            // A fresh source (as after a restart) restored from that row has the links and waits for the normal interval.
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(row.LastAttemptAt!.Value.AddMinutes(1));
            var requests = 0;
            var restarted = new LiquipediaHltvLinkSource(
                new ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaClient(new HttpClient(new StubHttpHandler((_, _) =>
                    {
                        requests++;
                        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
                    }))
                { BaseAddress = new Uri("https://api.liquipedia.test/api/v3/") },
                    Microsoft.Extensions.Options.Options.Create(new ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaOptions { ApiKey = "test-key-not-real", MaxRetries = 0 }),
                    new ToroSquad.Modules.Esports.Providers.Liquipedia.RequestBudget(clock), clock,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ToroSquad.Modules.Esports.Providers.Liquipedia.LiquipediaClient>.Instance),
                new FakeProvider(), new ToroSquad.Modules.Esports.Providers.Fixtures.EsportsDataMode(ToroSquad.Modules.Esports.Providers.Fixtures.ProviderMode.Live),
                Microsoft.Extensions.Options.Options.Create(new EsportsOptions()), clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LiquipediaHltvLinkSource>.Instance);
            restarted.Restore(MatchJson.DeserializeList<EsportsMatch>(row.DataJson!), row.LastAttemptAt);
            restarted.CandidateCount.Should().Be(source.CandidateCount).And.BeGreaterThan(0);

            var window = new MatchWindow(clock.GetUtcNow().AddHours(-12), clock.GetUtcNow().AddHours(48));
            (await restarted.RefreshIfDueAsync(window, CancellationToken.None)).Should().BeNull("cached: not due yet");
            requests.Should().Be(0);
            clock.Advance(TimeSpan.FromMinutes(30));
            (await restarted.RefreshIfDueAsync(window, CancellationToken.None))!.Outcome.Should().NotBe(ProviderOutcome.Success);
            requests.Should().Be(1, "exactly one request once the interval has passed");
            restarted.CandidateCount.Should().Be(source.CandidateCount, "a failed refresh keeps the cached links");
        }
    }

    [Fact]
    public async Task Manual_verified_links_work_with_the_liquipedia_source_off()
    {
        await using var host = await TestHost.CreateAsync(new()
        {
            ["Esports:Provider:Name"] = "PandaScore",
            ["Esports:HltvLinksFromLiquipedia"] = "false",
            ["Esports:VerifiedMatchLinks:0:Match"] = "pandascore:910002",
            ["Esports:VerifiedMatchLinks:0:Hltv"] = Url1,
        });
        host.Services.GetRequiredService<LiquipediaHltvLinkSource>().Enabled.Should().BeFalse();
        await host.Services.GetRequiredService<EsportsPoller>().RefreshMatchesAsync(CancellationToken.None);

        var matches = host.Services.GetRequiredService<EsportsCache>().Matches.Data!;
        matches.Single(m => m.Key.Id == "910002").Links!.HltvMatchUrl.Should().Be(Url1);
        matches.Where(m => m.Key.Id != "910002").Should().OnlyContain(m => m.Links == null || m.Links.HltvMatchUrl == null,
            "no Liquipedia links when the source is off (910001 would otherwise get one)");
    }
}
