using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Formula 1 notifications end to end on a real SQLite database with the real poller, workflow, planner, outbox and
/// dispatcher (only the providers and Discord are fakes). Wrong notifications are worse than missing ones: every test
/// here checks both that the right message exists and that nothing else was sent.
/// </summary>
public sealed class F1LifecycleIntegrationTests
{
    internal static readonly GuildId Guild = new(555);
    internal static readonly ChannelId Channel = new(5550);
    internal static readonly RoleId Role = new(5551);
    internal static readonly Dictionary<string, string?> Live = new() { ["Formula1:Provider:Mode"] = "Live" };
    internal static readonly DateTimeOffset T0 = TestHost.T0;

    internal static async Task TickAsync(TestHost host) => await host.Services.GetRequiredService<Formula1Poller>().TickAsync(CancellationToken.None);

    internal static async Task DeliverAsync(TestHost host) => await host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

    internal static async Task StepAsync(TestHost host, TimeSpan advance)
    {
        host.Clock.Advance(advance);
        await TickAsync(host);
        await DeliverAsync(host);
    }

    internal static Task<List<OutboxMessageEntity>> OutboxAsync(TestHost host, string kindPrefix) => host.InScopeAsync(sp =>
        sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "formula1" && o.Kind.StartsWith(kindPrefix)).ToListAsync());

    internal static Task<F1SessionSnapshotEntity> SnapshotAsync(TestHost host, string key) => host.InScopeAsync(sp =>
        sp.GetRequiredService<ToroDbContext>().Set<F1SessionSnapshotEntity>().AsNoTracking().SingleAsync(s => s.SessionKey == key));

    private static F1StandingsSnapshot DriversBefore => F1FakeProviders.DriverTable(2026, 17, ("a", 100), ("b", 90), ("c", 80));
    private static F1StandingsSnapshot DriversAfter => F1FakeProviders.DriverTable(2026, 18, ("b", 108), ("a", 118), ("c", 95));
    private static F1StandingsSnapshot TeamsBefore => F1FakeProviders.ConstructorTable(2026, 17, ("x", 190), ("y", 80));
    private static F1StandingsSnapshot TeamsAfter => F1FakeProviders.ConstructorTable(2026, 18, ("x", 226), ("y", 95));

    /// <summary>A race 30 minutes after T0 in a live-mode host with a ping role, standings known.</summary>
    internal static async Task<(TestHost Host, F1FakeProviders Fake, F1Session Race)> RaceWeekendAsync(Dictionary<string, string?>? overrides = null, bool pingRole = true,
        F1SessionType type = F1SessionType.Race)
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(overrides ?? Live);
        var meeting = fake.AddMeeting(2026, 18, (type, T0.AddMinutes(30)));
        fake.Drivers = DriversBefore;
        fake.Constructors = TeamsBefore;
        await host.SetUpF1GuildAsync(Guild, Channel, pingRole ? Role : null);
        return (host, fake, meeting.Sessions[0]);
    }

    [Fact]
    public async Task Main_flow_scheduled_start_live_start_red_flag_finish_result_and_late_standings_edit_one_message()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        var reference = F1FakeProviders.Ref(race);

        // Session scheduled; bot polling.
        await StepAsync(host, TimeSpan.Zero);
        (await OutboxAsync(host, "")).Should().BeEmpty();

        // Scheduled time passes — no lifecycle signal — NO message.
        await StepAsync(host, TimeSpan.FromMinutes(31));
        (await OutboxAsync(host, "")).Should().BeEmpty("a scheduled time is never a session start");
        host.Transport.Messages.Should().BeEmpty();

        // Live provider reports Started → exactly ONE start item, pinging the configured role.
        fake.AddEvent(reference, F1LifecycleSignal.Started, T0.AddMinutes(32));
        await StepAsync(host, TimeSpan.FromMinutes(2));
        (await OutboxAsync(host, "started:")).Should().ContainSingle();
        var start = host.Transport.Messages.Should().ContainSingle().Subject;
        start.Pinged.Should().BeTrue();
        start.Message.Mentions.Roles.Should().Equal(Role);
        start.Message.Embed!.Title.Should().Contain("Yarış");
        start.Message.Embed.Footer.Should().Contain("OpenF1");

        // Red flag, then the session is started again — a RESUME: no new start item, no edit.
        fake.AddEvent(reference, F1LifecycleSignal.Suspended, T0.AddMinutes(50));
        await StepAsync(host, TimeSpan.FromMinutes(18));
        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Suspended);
        fake.AddEvent(reference, F1LifecycleSignal.Started, T0.AddMinutes(60));
        await StepAsync(host, TimeSpan.FromMinutes(10));
        var resumed = await SnapshotAsync(host, race.Key);
        resumed.State.Should().Be((int)F1SessionState.Started);
        resumed.ResumeCount.Should().Be(1);
        resumed.StartedObservedAt.Should().Be(T0.AddMinutes(32), "the first logical start never moves");
        (await OutboxAsync(host, "started:")).Should().ContainSingle();
        host.Transport.Messages.Should().ContainSingle();
        host.Transport.EditCalls.Should().Be(0);

        // Finished, results not ready — NO result yet.
        fake.AddEvent(reference, F1LifecycleSignal.Finished, T0.AddMinutes(150));
        await StepAsync(host, TimeSpan.FromMinutes(91));
        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.FinishedPendingResults);
        (await OutboxAsync(host, "result:")).Should().BeEmpty("SESSION FINISHED does not mean the classification is available");

        // Results appear → ONE result item (no ping: results ping is off by default), standings pending.
        fake.ResultsByRef[reference] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(3));
        (await OutboxAsync(host, "result:")).Should().ContainSingle();
        host.Transport.Messages.Should().HaveCount(2);
        var result = host.Transport.Messages[1];
        result.Pinged.Should().BeFalse();
        result.Message.Embed!.Fields.Should().ContainSingle(f => f.Value.Contains("bekleniyor", StringComparison.Ordinal));

        // Standings provider has not updated yet (same hash) → no change.
        await StepAsync(host, TimeSpan.FromMinutes(1));
        result.Edits.Should().BeEmpty("unchanged standings are not a change");

        // Standings update → the SAME logical result message is edited, without a ping; no new message.
        fake.Drivers = DriversAfter;
        fake.Constructors = TeamsAfter;
        await StepAsync(host, TimeSpan.FromMinutes(6));
        (await OutboxAsync(host, "result:")).Should().ContainSingle();
        host.Transport.Messages.Should().HaveCount(2);
        var edit = result.Edits.Should().ContainSingle().Subject;
        edit.Mentions.Roles.Should().BeEmpty("edits never ping");
        edit.Embed!.Fields.Select(f => f.Name).Should().Contain(["🏆 Sürücüler", "🏭 Takımlar"]);
        edit.Embed.Fields.Should().Contain(f => f.Value.Contains("Driver a", StringComparison.Ordinal) && f.Value.Contains("118", StringComparison.Ordinal));
        edit.Embed.Footer.Should().Contain("Jolpica F1").And.Contain("OpenF1");
    }

    [Fact]
    public async Task Duplicate_provider_start_events_produce_one_start_notification()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        await StepAsync(host, TimeSpan.FromMinutes(32));
        await StepAsync(host, TimeSpan.FromMinutes(2)); // the REST reconciliation returns the same events again
        (await OutboxAsync(host, "started:")).Should().ContainSingle();
        host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task A_start_seen_long_after_it_happened_is_recorded_but_not_announced()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.LifecycleFailure = F1ProviderOutcome.TransportError; // bot cannot see the provider (downtime)
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        await StepAsync(host, TimeSpan.FromMinutes(33));
        fake.LifecycleFailure = null;
        await StepAsync(host, TimeSpan.FromMinutes(30)); // back 32 minutes after the start
        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Started);
        (await OutboxAsync(host, "started:")).Should().BeEmpty("no \"race started\" 30 minutes late");
    }

    [Fact]
    public async Task Lifecycle_provider_failure_fabricates_nothing()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        fake.LifecycleFailure = F1ProviderOutcome.Unavailable;
        for (var i = 0; i < 12; i++)
            await StepAsync(host, TimeSpan.FromMinutes(20));
        var snapshot = await SnapshotAsync(host, race.Key);
        snapshot.State.Should().Be((int)F1SessionState.Scheduled, "an outage is not started, finished or cancelled");
        snapshot.StartedObservedAt.Should().BeNull();
        snapshot.CancelledObservedAt.Should().BeNull();
        (await OutboxAsync(host, "")).Should().BeEmpty();
        host.Services.GetRequiredService<Formula1Cache>().LifecycleFeed.LastOutcome.Should().Be(F1ProviderOutcome.Unavailable);
    }

    [Fact]
    public async Task Without_live_lifecycle_access_nothing_is_announced_as_started_but_a_verified_result_still_arrives()
    {
        var (host, fake, race) = await RaceWeekendAsync(type: F1SessionType.Practice1);
        await using var _ = host;
        fake.LifecycleConfigured = false;
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(30));
        await StepAsync(host, TimeSpan.Zero);
        await StepAsync(host, TimeSpan.FromMinutes(45));
        (await OutboxAsync(host, "started:")).Should().BeEmpty("no lifecycle provider → no start notification, ever");
        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Scheduled);

        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(50)); // after the planned end the results provider is asked
        (await OutboxAsync(host, "result:fp1")).Should().ContainSingle("the classification itself is provider-verified data");
    }

    [Fact]
    public async Task Finish_without_results_never_creates_a_result_and_polling_is_bounded()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        for (var i = 0; i < 60; i++)
            await StepAsync(host, TimeSpan.FromMinutes(15));
        (await OutboxAsync(host, "result:")).Should().BeEmpty();
        fake.ResultCalls.Should().BeInRange(1, 60, "bounded retries with backoff, then give up");
        var callsAtGiveUp = fake.ResultCalls;
        await StepAsync(host, TimeSpan.FromHours(3));
        fake.ResultCalls.Should().Be(callsAtGiveUp, "no polling after ResultsMaxWaitHours");
    }

    [Fact]
    public async Task Incomplete_classification_is_not_published_until_it_is_valid()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race, entries: 3); // half-populated
        await StepAsync(host, TimeSpan.FromMinutes(145));
        (await OutboxAsync(host, "result:")).Should().BeEmpty();
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(5));
        (await OutboxAsync(host, "result:")).Should().ContainSingle();
    }

    [Fact]
    public async Task Results_provider_failure_retries_safely_and_then_delivers_once()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        fake.ResultsFailure = F1ProviderOutcome.QuotaExceeded;
        await StepAsync(host, TimeSpan.FromMinutes(145));
        await StepAsync(host, TimeSpan.FromMinutes(5));
        (await OutboxAsync(host, "result:")).Should().BeEmpty();
        fake.ResultsFailure = null;
        await StepAsync(host, TimeSpan.FromMinutes(10));
        await StepAsync(host, TimeSpan.FromMinutes(10));
        (await OutboxAsync(host, "result:")).Should().ContainSingle();
        host.Transport.Messages.Count(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Practice_result_never_starts_the_standings_workflow()
    {
        var (host, fake, practice) = await RaceWeekendAsync(type: F1SessionType.Practice1);
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(practice), F1LifecycleSignal.Started, T0.AddMinutes(30));
        fake.AddEvent(F1FakeProviders.Ref(practice), F1LifecycleSignal.Finished, T0.AddMinutes(90));
        fake.ResultsByRef[F1FakeProviders.Ref(practice)] = F1FakeProviders.Result(practice);
        await StepAsync(host, TimeSpan.FromMinutes(95));
        var standingsCalls = fake.StandingsCalls;
        fake.Drivers = DriversAfter;
        await StepAsync(host, TimeSpan.FromMinutes(30));
        var snapshot = await SnapshotAsync(host, practice.Key);
        snapshot.StandingsWatchUntil.Should().BeNull();
        snapshot.StandingsBaselineDriversHash.Should().BeNull();
        fake.StandingsCalls.Should().Be(standingsCalls, "a practice session never triggers a standings refresh");
        var card = host.Transport.Messages.Single(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal));
        card.Message.Embed!.Fields.Should().BeEmpty();
        card.Message.Embed.Description.Should().NotContain("🥇", "practice has no winner");
    }

    [Fact]
    public async Task Unchanged_standings_add_nothing_and_the_window_closes_with_a_pointer_to_the_command()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(141));
        var card = host.Transport.Messages.Single(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal));
        for (var i = 0; i < 20; i++)
            await StepAsync(host, TimeSpan.FromMinutes(10)); // well past the 180-minute settle window
        var snapshot = await SnapshotAsync(host, race.Key);
        snapshot.StandingsWindowClosed.Should().BeTrue();
        snapshot.StandingsDriversSnapshotId.Should().BeNull();
        card.Edits.Should().ContainSingle("pending → 'not updated yet' is the only change");
        card.Edits[0].Embed!.Fields.Single().Value.Should().Contain("/f1 standings drivers");
        card.Edits[0].Mentions.Roles.Should().BeEmpty();
        (await OutboxAsync(host, "result:")).Should().ContainSingle();
    }

    [Fact]
    public async Task A_late_correction_of_an_earlier_round_is_never_shown_as_the_standings_after_this_race()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(141));
        var card = host.Transport.Messages.Single(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal));

        fake.Drivers = F1FakeProviders.DriverTable(2026, 17, ("a", 99), ("b", 90), ("c", 80)); // round 17 corrected, not round 18
        await StepAsync(host, TimeSpan.FromMinutes(6));
        card.Edits.Should().BeEmpty();
        (await SnapshotAsync(host, race.Key)).StandingsDriversSnapshotId.Should().BeNull();

        fake.Drivers = DriversAfter;
        await StepAsync(host, TimeSpan.FromMinutes(6));
        card.Edits.Should().ContainSingle().Which.Embed!.Fields.Single(f => f.Name == "🏆 Sürücüler").Value.Should().Contain("18. yarış sonrası");
    }

    [Fact]
    public async Task Standings_provider_failure_never_blocks_the_race_result()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.StandingsFailure = F1ProviderOutcome.TransportError;
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(141));
        var card = host.Transport.Messages.Single(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal));
        card.Message.Embed!.Fields.Single().Value.Should().Contain("bekleniyor");
    }

    [Fact]
    public async Task Stale_schedule_data_creates_no_notifications()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        var race = fake.AddMeeting(2026, 20, (F1SessionType.Race, T0.AddHours(50))).Sessions[0];
        await host.SetUpF1GuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);
        fake.ScheduleFailure = F1ProviderOutcome.TransportError;
        for (var i = 0; i < 50; i++)
            await StepAsync(host, TimeSpan.FromHours(1)); // T0+50h, schedule last refreshed 50 h ago
        host.Services.GetRequiredService<Formula1Cache>().Schedule.IsStale(host.Clock.GetUtcNow(), TimeSpan.FromHours(48)).Should().BeTrue();
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddHours(50).AddMinutes(1));
        await StepAsync(host, TimeSpan.FromMinutes(2));
        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Started, "state is still tracked");
        (await OutboxAsync(host, "")).Should().BeEmpty("stale data never creates notifications");
    }

    [Fact]
    public async Task Stale_result_data_creates_no_new_message_and_no_edit()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(141));
        // The guild was paused while the result arrived; the classification then ages beyond ResultStaleAfterMinutes.
        (await OutboxAsync(host, "result:")).Should().ContainSingle();
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            await db.Outbox.Where(o => o.ModuleId == "formula1").ExecuteDeleteAsync();
            await db.Set<F1ResultSnapshotEntity>().ExecuteUpdateAsync(s => s.SetProperty(r => r.FetchedAt, T0));
        });
        await host.InScopeAsync(async sp => (await sp.GetRequiredService<Formula1NotificationPlanner>().PlanAsync(CancellationToken.None)).Created.Should().Be(0));
        (await OutboxAsync(host, "")).Should().BeEmpty();
    }

    [Fact]
    public async Task Schedule_provider_failure_keeps_the_known_calendar_and_marks_it_stale()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        fake.AddMeeting(2026, 19, (F1SessionType.Practice1, T0.AddDays(3)), (F1SessionType.Race, T0.AddDays(5)));
        await host.SetUpF1GuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);
        fake.ScheduleFailure = F1ProviderOutcome.Timeout;
        await StepAsync(host, TimeSpan.FromHours(7));
        var cache = host.Services.GetRequiredService<Formula1Cache>();
        cache.Schedule.Data!.SelectMany(s => s.Sessions).Should().HaveCount(2, "one failed request never wipes known sessions");
        cache.Schedule.LastOutcome.Should().Be(F1ProviderOutcome.Timeout);
        cache.Schedule.ConsecutiveFailures.Should().Be(1);
        cache.Sessions.Should().HaveCount(2);
    }

    [Fact]
    public async Task First_installation_treats_already_completed_sessions_as_baseline_without_notifications()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        var meeting = fake.AddMeeting(2026, 17, (F1SessionType.Practice1, T0.AddHours(-26)), (F1SessionType.Sprint, T0.AddHours(-22)), (F1SessionType.Race, T0.AddHours(-3)));
        foreach (var s in meeting.Sessions)
        {
            fake.AddEvent(F1FakeProviders.Ref(s), F1LifecycleSignal.Started, s.ScheduledStartUtc);
            fake.AddEvent(F1FakeProviders.Ref(s), F1LifecycleSignal.Finished, s.PlannedEndUtc);
            fake.ResultsByRef[F1FakeProviders.Ref(s)] = F1FakeProviders.Result(s);
        }

        await host.SetUpF1GuildAsync(Guild, Channel, Role);
        for (var i = 0; i < 4; i++)
            await StepAsync(host, TimeSpan.FromMinutes(5));
        (await OutboxAsync(host, "")).Should().BeEmpty("no flood of old sessions");
        host.Transport.Messages.Should().BeEmpty();
        var cache = host.Services.GetRequiredService<Formula1Cache>();
        cache.Results.Should().ContainKey(meeting.Find(F1SessionType.Race)!.Key, "baseline results are still shown by /f1 results");
    }

    [Fact]
    public async Task Results_that_finalised_before_the_module_was_enabled_are_never_announced()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        await host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "formula1", false, CancellationToken.None)).Succeeded.Should().BeTrue());
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        // Disabled: the poller idles (no guild enabled). Another guild keeps it running so the result is finalised meanwhile.
        await host.SetUpF1GuildAsync(new GuildId(556), new ChannelId(5560));
        await StepAsync(host, TimeSpan.FromMinutes(145));
        host.Clock.Advance(TimeSpan.FromMinutes(1)); // the admin re-enables after the result was finalised
        await host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "formula1", true, CancellationToken.None)).Succeeded.Should().BeTrue());
        await StepAsync(host, TimeSpan.FromMinutes(5));
        (await OutboxAsync(host, "result:")).Should().NotContain(o => o.GuildId == Guild.Value, "re-enabling moves the watermark: no history");
        (await OutboxAsync(host, "result:")).Should().ContainSingle(o => o.GuildId == 556);
    }

    [Fact]
    public async Task Resuming_or_changing_the_channel_or_reenabling_a_type_never_releases_history()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        var admin = TestHost.Admin(Guild);
        await host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<Formula1ConfigService>();
            (await config.PauseAsync(admin, true, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await config.SetNotificationsAsync(admin, new F1NotificationChanges(RaceResults: false), CancellationToken.None)).Succeeded.Should().BeTrue();
        });
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(145));
        (await OutboxAsync(host, "")).Should().BeEmpty("paused");

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        host.Guilds.SetChannel(Guild, new ChannelId(5559), new Core.Roles.BotChannelAccess(true, true, F1TestHostExtensions.ChannelPermissions));
        await host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<Formula1ConfigService>();
            (await config.PauseAsync(admin, false, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await config.SetChannelAsync(admin, 5559, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await config.SetNotificationsAsync(admin, new F1NotificationChanges(RaceResults: true), CancellationToken.None)).Succeeded.Should().BeTrue();
        });
        await StepAsync(host, TimeSpan.FromMinutes(5));
        (await OutboxAsync(host, "")).Should().BeEmpty("the watermark moved on resume / channel change / re-enable");
    }

    [Fact]
    public async Task Disabled_notification_types_are_not_sent()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await host.InScopeAsync(async sp => (await sp.GetRequiredService<Formula1ConfigService>()
            .SetNotificationsAsync(TestHost.Admin(Guild), new F1NotificationChanges(RaceStart: false), CancellationToken.None)).Succeeded.Should().BeTrue());
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        await StepAsync(host, TimeSpan.FromMinutes(32));
        (await OutboxAsync(host, "")).Should().BeEmpty();

        var policy = await host.InScopeAsync(sp => sp.GetServices<IDeliveryPolicy>().Single(p => p.Module.Value == "formula1")
            .CanDeliverAsync(Guild, Channel, "started:race", CancellationToken.None));
        policy.Should().BeOfType<DeliveryDecision.Cancel>("the dispatcher re-checks the switch before sending");
    }

    [Fact]
    public async Task Qualifying_notifications_are_modelled_but_off_by_default()
    {
        var (host, fake, quali) = await RaceWeekendAsync(type: F1SessionType.Qualifying);
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(quali), F1LifecycleSignal.Started, T0.AddMinutes(30), 1);
        await StepAsync(host, TimeSpan.FromMinutes(32));
        (await SnapshotAsync(host, quali.Key)).State.Should().Be((int)F1SessionState.Started);
        (await OutboxAsync(host, "")).Should().BeEmpty();

        await host.InScopeAsync(async sp => (await sp.GetRequiredService<Formula1ConfigService>()
            .SetNotificationsAsync(TestHost.Admin(Guild), new F1NotificationChanges(QualifyingResults: true), CancellationToken.None)).Succeeded.Should().BeTrue());
        fake.AddEvent(F1FakeProviders.Ref(quali), F1LifecycleSignal.Finished, T0.AddMinutes(48), 1);
        fake.AddEvent(F1FakeProviders.Ref(quali), F1LifecycleSignal.Started, T0.AddMinutes(55), 2);
        fake.AddEvent(F1FakeProviders.Ref(quali), F1LifecycleSignal.Finished, T0.AddMinutes(90), 3);
        fake.ResultsByRef[F1FakeProviders.Ref(quali)] = F1FakeProviders.Result(quali);
        await StepAsync(host, TimeSpan.FromMinutes(60));
        (await OutboxAsync(host, "result:quali")).Should().ContainSingle("switched on explicitly, no code change needed");
    }
}
