using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Automatic HLTV links end to end (real SQLite, poller, planner, outbox, fake Discord): a link found after the result was
/// sent edits the same message without a ping and credits Liquipedia in the footer; a Liquipedia MediaWiki outage never
/// blocks PandaScore alerts and never removes known links; the lookup cache is persisted for restarts.
/// </summary>
public sealed class HltvLinkDeliveryTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(777);
    private static readonly ChannelId Channel = new(7770);
    private static readonly RoleId AlphaRole = new(7771);
    private const string Url = "https://www.hltv.org/matches/2398672/match";
    private static readonly TeamRef Alpha = new("pandascore", "ps-team:1", "Eternal Fire", "EF");
    private static readonly TeamRef Bravo = new("pandascore", "ps-team:2", "WBT", "WBT");

    private TestHost _host = null!;
    private readonly FakeMatches _provider = new();
    private StubHttpHandler _wiki = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" }, replace: s =>
        {
            s.AddSingleton(new EsportsDataMode(ProviderMode.Live));
            s.AddSingleton<IEsportsDataProvider>(_provider);
            s.AddSingleton(sp => new LiquipediaWikiClient(
                new HttpClient(new DelegatingStub(() => _wiki)) { BaseAddress = new Uri("https://liquipedia.test/") },
                Options.Create(new LiquipediaOptions { WikiBaseUrl = "https://liquipedia.test/" }),
                sp.GetRequiredService<RequestBudget>(), sp.GetRequiredService<TimeProvider>(), NullLogger<LiquipediaWikiClient>.Instance));
        });
        _provider.Clock = _host.Clock;
        await _host.SetUpEsportsGuildAsync(Guild, Channel, new RoleInfo(AlphaRole, "EF fans", 3, GuildPermission.None, false, false, true));
        _provider.Matches = [M("BOOT", MatchStatus.Scheduled, TestHost.T0.AddDays(5))];
        await _host.Services.GetRequiredService<EsportsPoller>().RefreshMatchesAsync(CancellationToken.None); // baseline
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Set<EsportsFilterEntity>().Add(new EsportsFilterEntity { GuildId = Guild.Value, Dimension = (int)FilterDimension.Team, Value = "ps-team:1" });
            await db.SaveChangesAsync();
            (await sp.GetRequiredService<RoleMappingService>().MapAsync(TestHost.Admin(Guild), AlphaRole, "ps-team:1", true, true, CancellationToken.None))
                .Succeeded.Should().BeTrue();
        });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static EsportsMatch M(string id, MatchStatus status, DateTimeOffset start, int? winner = null) => new(
        new MatchKey("pandascore", id),
        new TournamentRef("pandascore", "ps-tournament:1", "Stake Ranked Episode 5: Closed Qualifier 2026", "4", null, null, null),
        start, true, 3, status, "test",
        new MatchOpponent(OpponentKind.Team, Alpha, status == MatchStatus.Finished ? 2 : null, OpponentResult.Scored),
        new MatchOpponent(OpponentKind.Team, Bravo, status == MatchStatus.Finished ? 0 : null, OpponentResult.Scored),
        winner, false, false, [], "Playoffs", null, [],
        BeginAtUtc: status is MatchStatus.Live or MatchStatus.Finished ? start : null,
        EndAtUtc: status == MatchStatus.Finished ? start.AddHours(1) : null);

    private async Task PollAsync()
    {
        var work = _host.Services.GetRequiredService<EsportsPoller>().RefreshMatchesAsync(CancellationToken.None);
        for (var i = 0; !work.IsCompleted && i < 10_000; i++)
        {
            _host.Clock.Advance(TimeSpan.FromMilliseconds(250)); // request spacing waits on the fake clock
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await work;
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);
    }

    private static StubHttpHandler WikiWith(string wikitext) => new((r, _) =>
    {
        if (r.RequestUri!.Query.Contains("list=search", StringComparison.Ordinal))
            return Task.FromResult(StubHttpHandler.Json("""{"query":{"search":[{"ns":0,"title":"Stake Ranked/Episode 5/Qualifier"}]}}"""));
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["query"] = new System.Text.Json.Nodes.JsonObject
            {
                ["pages"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["1"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["title"] = "Stake Ranked/Episode 5/Qualifier",
                        ["revisions"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                        {
                            ["slots"] = new System.Text.Json.Nodes.JsonObject { ["main"] = new System.Text.Json.Nodes.JsonObject { ["*"] = wikitext } },
                        }),
                    },
                },
            },
        };
        return Task.FromResult(StubHttpHandler.Json(body.ToJsonString()));
    });

    private static string Wikitext(DateTimeOffset startUtc, string hltv) =>
        "{{Match|opponent1={{TeamOpponent|ef}}|opponent2={{TeamOpponent|wbt}}|date=" +
        startUtc.ToOffset(TimeSpan.FromHours(2)).ToString("MMMM d, yyyy - HH:mm", System.Globalization.CultureInfo.InvariantCulture) +
        " {{Abbr/CEST}}|hltv=" + hltv + "}}";

    [Fact]
    public async Task A_link_found_after_the_result_edits_the_same_message_without_a_ping_and_credits_liquipedia()
    {
        var start = _host.Clock.GetUtcNow().AddHours(-2);
        _wiki = WikiWith(Wikitext(start, "")); // editors have not entered the HLTV id yet
        _provider.Matches = [M("R1", MatchStatus.Live, start)];
        await PollAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        _provider.Matches = [M("R1", MatchStatus.Finished, start, winner: 0)];
        await PollAsync();

        var result = _host.Transport.Messages.Should().ContainSingle(m => m.Message.Embed!.Title!.Contains('[')).Subject;
        result.Message.Mentions.Roles.Select(r => r.Value).Should().Equal(new[] { AlphaRole.Value }, "the first result message pings the mapped role");
        result.Message.Embed!.Url.Should().BeNull();
        result.Message.Embed.Footer.Should().Be("Kaynak: PandaScore");
        result.Message.Embed.Fields.Should().NotContain(f => f.Value.Contains("Maç Sayfası", StringComparison.Ordinal));

        _wiki = WikiWith(Wikitext(start, "2398672")); // the editor adds the id later
        _host.Clock.Advance(TimeSpan.FromMinutes(40));
        await PollAsync();

        var sends = _host.Transport.SendCalls;
        var edited = _host.Transport.Messages.Single(m => m.Id == result.Id);
        var edit = edited.Edits.Should().ContainSingle().Subject;
        edit.Mentions.Roles.Should().BeEmpty("an edit never pings");
        edit.Embed!.Url.Should().Be(Url);
        edit.Embed.Fields[^1].Value.Should().Be("[Maç Sayfası](" + Url + ")");
        edit.Embed.Footer.Should().Be("Kaynak: PandaScore · Link: Liquipedia");
        edit.Embed.Title.Should().Be(result.Message.Embed.Title);

        await PollAsync();
        _host.Transport.SendCalls.Should().Be(sends, "no new message");
        edited.Edits.Should().HaveCount(1, "the same data does not edit again");
        _wiki.Requests.Should().OnlyContain(u => u.Host == "liquipedia.test", "HLTV is never contacted");
    }

    [Fact]
    public async Task A_liquipedia_outage_never_blocks_pandascore_alerts_and_keeps_known_links()
    {
        var source = _host.Services.GetRequiredService<LiquipediaWikiLinkSource>();
        var known = M("K1", MatchStatus.Scheduled, _host.Clock.GetUtcNow().AddHours(1));
        var at = _host.Clock.GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var knownStart = known.ScheduledStartUtc!.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        source.Restore("{\"pandascore:K1\":{\"url\":\"https://www.hltv.org/matches/555/match\",\"startUtc\":\"" + knownStart + "\",\"checkedAt\":\"" + at + "\",\"nextCheckAt\":\"" + at + "\",\"misses\":0}}");

        _wiki = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var start = _host.Clock.GetUtcNow().AddMinutes(10); // reminder due now
        _provider.Matches = [M("O1", MatchStatus.Scheduled, start), known];
        await PollAsync();

        _wiki.Requests.Should().NotBeEmpty("the lookup was attempted");
        source.LastOutcome.Should().Be(ProviderOutcome.TransportError);
        var reminder = _host.Transport.Messages.Should().ContainSingle(m => m.Message.Embed!.Description!.Contains("Planlanan başlangıç", StringComparison.Ordinal)).Subject;
        reminder.Message.Mentions.Roles.Should().NotBeEmpty("the PandaScore alert went out normally");
        _host.Services.GetRequiredService<EsportsCache>().Matches.Data!.Single(m => m.Key.Id == "K1").Links!.HltvMatchUrl
            .Should().Be("https://www.hltv.org/matches/555/match", "known links survive the outage");

        var row = await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<ProviderStateEntity>().AsNoTracking()
            .SingleAsync(s => s.Key == LiquipediaWikiLinkSource.StateKey));
        row.DataJson.Should().Contain("pandascore:K1").And.Contain("pandascore:O1", "the cache (incl. the failed lookup's retry time) is persisted for restarts");
        row.ConsecutiveFailures.Should().Be(1);
    }

    /// <summary>Lets a test swap the MediaWiki responder between polls while the client (and its throttle) stays the same.</summary>
    private sealed class DelegatingStub(Func<StubHttpHandler> current) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var invoker = new HttpMessageInvoker(current(), disposeHandler: false);
            return await invoker.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FakeMatches : IEsportsDataProvider
    {
        public IReadOnlyList<EsportsMatch> Matches { get; set; } = [];

        public TimeProvider? Clock { get; set; }

        public string Id => "pandascore";

        public ProviderCapability Capabilities => ProviderCapability.Fixtures | ProviderCapability.Results | ProviderCapability.VerifiedLiveStatus;

        public bool IsConfigured => true;

        public Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<IReadOnlyList<EsportsMatch>>.Ok(Matches, Clock?.GetUtcNow() ?? TestHost.T0));

        public Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<IReadOnlyList<EsportsEvent>>.Fail(ProviderOutcome.Timeout, "x", TestHost.T0));
    }
}
