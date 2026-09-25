using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Match lifecycle notifications on a real SQLite DB with the real planner and outbox: started / finished / postponed /
/// rescheduled / cancelled are each sent exactly once, survive restarts without duplicates, are never inferred from the
/// clock, first sight or Unknown, and never come from a provider outage.
/// </summary>
public sealed class LifecycleNotificationTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(444);
    private static readonly ChannelId Channel = new(4440);
    private static readonly RoleId AlphaRole = new(4441);
    private static readonly TeamRef Alpha = new("pandascore", "ps-team:1", "Alpha", "ALP");
    private static readonly TeamRef Bravo = new("pandascore", "ps-team:2", "Bravo", "BRV");

    /// <summary>Same display name as <see cref="Alpha"/>, different provider identity.</summary>
    private static readonly TeamRef OtherAlpha = new("pandascore", "ps-team:3", "Alpha", "ALP");

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" });
        await _host.SetUpEsportsGuildAsync(Guild, Channel, new RoleInfo(AlphaRole, "Alpha fans", 3, GuildPermission.None, false, false, true));
        // Steady state: a first poll records the provider baseline (and makes the teams known for role mapping).
        await PlanAsync([M("BOOT", MatchStatus.Scheduled, TestHost.T0.AddDays(5))]);
        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<RoleMappingService>().MapAsync(TestHost.Admin(Guild), AlphaRole, "ps-team:1", true, true, CancellationToken.None))
            .Succeeded.Should().BeTrue());
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static EsportsMatch M(string id, MatchStatus status, DateTimeOffset start, TeamRef? a = null, bool rescheduled = false,
        int? scoreA = null, int? scoreB = null, int? winner = null, bool forfeit = false) => new(
        new MatchKey("pandascore", id),
        new TournamentRef("pandascore", "ps-tournament:1", "Demo Masters 2026", "1", null, null, "ps-serie:1"),
        start, true, 3, status, "test",
        new MatchOpponent(OpponentKind.Team, a ?? Alpha, scoreA, OpponentResult.Scored),
        new MatchOpponent(OpponentKind.Team, Bravo, scoreB, OpponentResult.Scored),
        winner, false, forfeit, [], "Playoffs", null, [],
        BeginAtUtc: status is MatchStatus.Live or MatchStatus.Finished ? start : null,
        EndAtUtc: status == MatchStatus.Finished ? start.AddHours(2) : null,
        Rescheduled: rescheduled);

    private Task<PlanReport> PlanAsync(IReadOnlyList<EsportsMatch> matches) =>
        _host.InScopeAsync(sp => sp.GetRequiredService<NotificationPlanner>().PlanAsync(matches, _host.Clock.GetUtcNow(), false, CancellationToken.None));

    private Task<List<OutboxMessageEntity>> OutboxAsync(string? kind = null) =>
        _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking()
            .Where(o => kind == null || o.Kind == kind || (kind == "rescheduled" && o.Kind.StartsWith("rescheduled-")))
            .OrderBy(o => o.Id).ToListAsync());

    private async Task RestartAsync() => await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Database.CanConnectAsync());

    private void Advance(int minutes) => _host.Clock.Advance(TimeSpan.FromMinutes(minutes));

    /// <summary>The stored payload as readable text (the outbox stores JSON with escaped non-ASCII characters).</summary>
    private static string Text(OutboxMessageEntity o) =>
        JsonNode.Parse(o.PayloadJson)!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public async Task Scheduled_to_running_creates_exactly_one_started_card_across_polls_and_restart()
    {
        var start = _host.Clock.GetUtcNow().AddHours(3); // far away: no reminder in this test
        await PlanAsync([M("S1", MatchStatus.Scheduled, start)]);
        Advance(5);
        await PlanAsync([M("S1", MatchStatus.Live, start)]);
        Advance(5);
        await PlanAsync([M("S1", MatchStatus.Live, start)]);
        await RestartAsync();
        await PlanAsync([M("S1", MatchStatus.Live, start)]);

        var started = await OutboxAsync(NotificationPlanner.KindStarted);
        started.Should().ContainSingle();
        Text(started[0]).Should().Contain("Maç başladı").And.Contain("<@&4441>", "started may ping the mapped team role");
    }

    [Fact]
    public async Task Clock_passing_the_scheduled_time_never_creates_a_started_card()
    {
        var start = _host.Clock.GetUtcNow().AddMinutes(10);
        await PlanAsync([M("C1", MatchStatus.Scheduled, start)]);
        Advance(60);
        await PlanAsync([M("C1", MatchStatus.Scheduled, start)]);
        (await OutboxAsync(NotificationPlanner.KindStarted)).Should().BeEmpty("only a provider-stated running state is a start");
    }

    [Fact]
    public async Task Running_to_finished_creates_exactly_one_result_across_polls_and_restart()
    {
        var start = _host.Clock.GetUtcNow().AddHours(-1);
        await PlanAsync([M("F1", MatchStatus.Live, start)]);
        Advance(5);
        var finished = M("F1", MatchStatus.Finished, start, scoreA: 2, scoreB: 0, winner: 0);
        await PlanAsync([finished]);
        await PlanAsync([finished]);
        await RestartAsync();
        await PlanAsync([finished]);

        var results = await OutboxAsync(NotificationPlanner.KindResult);
        results.Should().ContainSingle();
        Text(results[0]).Should().Contain("Alpha [2] - [0] Bravo").And.Contain("Alpha maçı kazandı");
    }

    [Fact]
    public async Task Postponed_creates_exactly_one_card_without_pings()
    {
        var start = _host.Clock.GetUtcNow().AddHours(2);
        await PlanAsync([M("P1", MatchStatus.Scheduled, start)]);
        Advance(5);
        await PlanAsync([M("P1", MatchStatus.Postponed, start)]);
        Advance(5);
        await PlanAsync([M("P1", MatchStatus.Postponed, start)]);
        await RestartAsync();
        await PlanAsync([M("P1", MatchStatus.Postponed, start)]);

        var cards = await OutboxAsync(NotificationPlanner.KindPostponed);
        cards.Should().ContainSingle();
        Text(cards[0]).Should().Contain("Maç ertelendi").And.Contain("yeni tarih açıklanmadı").And.NotContain("<@&");
    }

    [Fact]
    public async Task Provider_flagged_reschedule_creates_one_card_per_new_time_and_small_or_unflagged_moves_none()
    {
        var start = _host.Clock.GetUtcNow().AddHours(4);
        await PlanAsync([M("R1", MatchStatus.Scheduled, start)]);

        Advance(5);
        await PlanAsync([M("R1", MatchStatus.Scheduled, start.AddMinutes(10), rescheduled: true)]);
        (await OutboxAsync("rescheduled")).Should().BeEmpty("a 10-minute move is below the threshold");

        Advance(5);
        await PlanAsync([M("R1", MatchStatus.Scheduled, start.AddHours(1))]);
        (await OutboxAsync("rescheduled")).Should().BeEmpty("without the provider's rescheduled flag a delay is not an announcement");

        Advance(5);
        var moved = M("R1", MatchStatus.Scheduled, start.AddHours(3), rescheduled: true);
        await PlanAsync([moved]);
        await PlanAsync([moved]);
        await RestartAsync();
        await PlanAsync([moved]);

        var cards = await OutboxAsync("rescheduled");
        cards.Should().ContainSingle();
        Text(cards[0]).Should().Contain("Maçın saati değişti").And.Contain("Yeni Saat").And.NotContain("<@&");
        Text(cards[0]).Should().Contain(NotificationRenderer.LocalTime(start.AddHours(3), TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")),
            "shown in the guild's Europe/Istanbul time zone");
    }

    [Fact]
    public async Task Postponed_match_getting_a_new_date_is_one_rescheduled_card()
    {
        var start = _host.Clock.GetUtcNow().AddHours(2);
        await PlanAsync([M("PR", MatchStatus.Scheduled, start)]);
        Advance(5);
        await PlanAsync([M("PR", MatchStatus.Postponed, start)]);
        Advance(5);
        await PlanAsync([M("PR", MatchStatus.Scheduled, start.AddDays(1), rescheduled: true)]);
        (await OutboxAsync("rescheduled")).Should().ContainSingle();
        (await OutboxAsync(NotificationPlanner.KindPostponed)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(MatchStatus.Scheduled)]
    [InlineData(MatchStatus.Live)]
    public async Task Cancelled_creates_exactly_one_card(MatchStatus before)
    {
        var start = _host.Clock.GetUtcNow().AddHours(1);
        await PlanAsync([M("X1", before, start)]);
        Advance(5);
        await PlanAsync([M("X1", MatchStatus.Cancelled, start)]);
        await PlanAsync([M("X1", MatchStatus.Cancelled, start)]);
        await RestartAsync();
        await PlanAsync([M("X1", MatchStatus.Cancelled, start)]);
        var cards = await OutboxAsync(NotificationPlanner.KindCancelled);
        cards.Should().ContainSingle();
        Text(cards[0]).Should().Contain("Maç iptal edildi").And.NotContain("<@&");
    }

    [Fact]
    public async Task First_sight_of_a_match_in_any_state_announces_no_lifecycle_change()
    {
        var now = _host.Clock.GetUtcNow();
        await PlanAsync(
        [
            M("N1", MatchStatus.Live, now.AddMinutes(-20)),
            M("N2", MatchStatus.Postponed, now.AddHours(2)),
            M("N3", MatchStatus.Cancelled, now.AddHours(1)),
            M("N4", MatchStatus.Scheduled, now.AddHours(6), rescheduled: true),
        ]);
        await PlanAsync([M("N1", MatchStatus.Live, now.AddMinutes(-20))]);
        (await OutboxAsync()).Should().BeEmpty("a transition must be observed, not assumed");
    }

    [Fact]
    public async Task First_boot_of_a_provider_does_not_announce_historical_or_ongoing_matches()
    {
        await using var fresh = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" });
        await fresh.SetUpEsportsGuildAsync(Guild, Channel);
        var now = fresh.Clock.GetUtcNow().AddMinutes(1);
        fresh.Clock.Advance(TimeSpan.FromMinutes(1));
        await fresh.InScopeAsync(sp => sp.GetRequiredService<NotificationPlanner>().PlanAsync(
        [
            M("H1", MatchStatus.Finished, now.AddHours(-3), scoreA: 2, scoreB: 1, winner: 0),
            M("H2", MatchStatus.Finished, now.AddHours(-2), scoreA: 0, scoreB: 2, winner: 1),
            M("H3", MatchStatus.Live, now.AddMinutes(-30)),
            M("H4", MatchStatus.Cancelled, now.AddHours(-1)),
        ], now, false, CancellationToken.None));
        (await fresh.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_status_never_creates_or_erases_a_transition()
    {
        var start = _host.Clock.GetUtcNow().AddHours(1);
        await PlanAsync([M("U1", MatchStatus.Scheduled, start)]);
        Advance(5);
        await PlanAsync([M("U1", MatchStatus.Unknown, start)]);
        (await OutboxAsync()).Should().BeEmpty();

        var snapshot = await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<MatchSnapshotEntity>().AsNoTracking().SingleAsync(s => s.MatchKey == "pandascore:U1"));
        ((MatchStatus)snapshot.Status).Should().Be(MatchStatus.Scheduled, "Unknown does not overwrite the last known state");

        Advance(5);
        await PlanAsync([M("U1", MatchStatus.Live, start)]);
        (await OutboxAsync(NotificationPlanner.KindStarted)).Should().ContainSingle("Scheduled → (Unknown) → Running is still a real start");
    }

    [Fact]
    public async Task Ambiguous_team_identity_never_triggers_another_teams_ping()
    {
        var start = _host.Clock.GetUtcNow().AddHours(3);
        await PlanAsync([M("A1", MatchStatus.Scheduled, start, a: OtherAlpha)]);
        Advance(5);
        await PlanAsync([M("A1", MatchStatus.Live, start, a: OtherAlpha)]);
        var card = (await OutboxAsync(NotificationPlanner.KindStarted)).Single();
        Text(card).Should().NotContain("<@&4441>", "the role is mapped to ps-team:1, not to another team that is also called 'Alpha'");
    }

    [Fact]
    public async Task Stale_transitions_and_transitions_before_the_guild_watermark_are_not_sent()
    {
        var start = _host.Clock.GetUtcNow().AddHours(3);
        await PlanAsync([M("W1", MatchStatus.Scheduled, start)]);
        Advance(5);
        await _host.InScopeAsync(async sp =>
        {
            // Guild pauses before the transition is planned, then resumes: resume moves the watermark forward.
            var config = sp.GetRequiredService<EsportsConfigService>();
            (await config.PauseAsync(TestHost.Admin(Guild), true, CancellationToken.None)).Succeeded.Should().BeTrue();
        });
        await PlanAsync([M("W1", MatchStatus.Live, start)]);
        Advance(5);
        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<EsportsConfigService>().PauseAsync(TestHost.Admin(Guild), false, CancellationToken.None)).Succeeded.Should().BeTrue());
        await PlanAsync([M("W1", MatchStatus.Live, start)]);
        (await OutboxAsync(NotificationPlanner.KindStarted)).Should().BeEmpty("it started while the guild was paused");
    }

    [Fact]
    public async Task Provider_outage_creates_no_transitions_and_no_messages()
    {
        var failing = new FailingProvider();
        await using var host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" },
            replace: s => s.AddSingleton<IEsportsDataProvider>(failing));
        await host.SetUpEsportsGuildAsync(Guild, Channel);
        var poller = host.Services.GetRequiredService<EsportsPoller>();

        failing.Next = ProviderResult<IReadOnlyList<EsportsMatch>>.Ok([M("BOOT", MatchStatus.Scheduled, TestHost.T0.AddDays(5)), M("O1", MatchStatus.Scheduled, TestHost.T0.AddHours(2))], TestHost.T0);
        await poller.RefreshMatchesAsync(CancellationToken.None);
        foreach (var outcome in new[] { ProviderOutcome.Timeout, ProviderOutcome.AuthFailed, ProviderOutcome.QuotaExceeded, ProviderOutcome.SchemaError, ProviderOutcome.TransportError })
        {
            host.Clock.Advance(TimeSpan.FromMinutes(10));
            failing.Next = ProviderResult<IReadOnlyList<EsportsMatch>>.Fail(outcome, "down", host.Clock.GetUtcNow());
            await poller.RefreshMatchesAsync(CancellationToken.None);
        }

        var snapshot = await host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<MatchSnapshotEntity>().AsNoTracking().SingleAsync(s => s.MatchKey == "pandascore:O1"));
        ((MatchStatus)snapshot.Status).Should().Be(MatchStatus.Scheduled);
        snapshot.StartedObservedAt.Should().BeNull();
        snapshot.CancelledObservedAt.Should().BeNull();
        snapshot.PostponedObservedAt.Should().BeNull();
        (await host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Demo_cards_are_staged_once_and_a_rerun_stages_nothing_new()
    {
        async Task<List<StageOutcome>> StageAsync() => await _host.InScopeAsync(async sp =>
        {
            var renderer = new NotificationRenderer(sp.GetRequiredService<ILocalizer>(), new EsportsDataMode(ProviderMode.Fixture));
            var now = _host.Clock.GetUtcNow();
            var cards = EsportsDemoCards.Build(renderer, "tr", TimeZoneInfo.Utc, now);
            var outbox = sp.GetRequiredService<INotificationOutbox>();
            var outcomes = new List<StageOutcome>();
            foreach (var request in EsportsDemoCards.Requests(Guild, Channel, cards, now))
                outcomes.Add(await outbox.StageAsync(request, CancellationToken.None));
            await sp.GetRequiredService<ToroDbContext>().SaveChangesAsync();
            return outcomes;
        });

        (await StageAsync()).Should().AllBeEquivalentTo(StageOutcome.Created);
        Advance(7); // a later re-run within the same hour
        (await StageAsync()).Should().AllBeEquivalentTo(StageOutcome.Unchanged, "re-running neither sends nor edits");
        (await OutboxAsync()).Count(o => o.SourceKey == EsportsDemoCards.SourceKey).Should().Be(7);
    }

    [Theory]
    [InlineData(NotificationPlanner.KindStarted)]
    [InlineData(NotificationPlanner.KindPostponed)]
    [InlineData(NotificationPlanner.KindCancelled)]
    [InlineData("rescheduled-202609251900")]
    public async Task Lifecycle_cards_follow_the_reminders_switch_at_delivery_time(string kind)
    {
        var decision = await _host.InScopeAsync(async sp =>
        {
            (await sp.GetRequiredService<EsportsConfigService>().ConfigureAsync(TestHost.Admin(Guild), null, false, null, null, null, CancellationToken.None))
                .Succeeded.Should().BeTrue();
            return await sp.GetServices<IDeliveryPolicy>().Single(p => p.Module == EsportsModule.ModuleIdTyped).CanDeliverAsync(Guild, Channel, kind, CancellationToken.None);
        });
        decision.Should().BeOfType<DeliveryDecision.Cancel>();
    }

    private sealed class FailingProvider : IEsportsDataProvider
    {
        public ProviderResult<IReadOnlyList<EsportsMatch>> Next { get; set; } = ProviderResult<IReadOnlyList<EsportsMatch>>.Fail(ProviderOutcome.Timeout, "x", TestHost.T0);

        public string Id => "pandascore";

        public ProviderCapability Capabilities => ProviderCapability.Fixtures | ProviderCapability.VerifiedLiveStatus;

        public bool IsConfigured => true;

        public Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken) => Task.FromResult(Next);

        public Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<IReadOnlyList<EsportsEvent>>.Fail(ProviderOutcome.Timeout, "x", TestHost.T0));
    }
}
