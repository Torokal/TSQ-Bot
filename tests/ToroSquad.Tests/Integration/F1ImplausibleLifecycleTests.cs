using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// 2026 Mexico City GP: OpenF1 published the practice messages of Saturday under the key of Sunday's race. The race was marked
/// finished a day early, so no listener ran during the real race and its start and red-flag cards were lost.
/// </summary>
public sealed class F1ImplausibleLifecycleTests
{
    private static readonly DateTimeOffset RaceStart = T0.AddHours(30);

    /// <summary>Practice about to start (so the provider sessions get mapped) and tomorrow's race; red-flag cards on.</summary>
    private static async Task<(TestHost Host, F1FakeProviders World, F1Session Race)> WeekendAsync(Dictionary<string, string?>? overrides = null)
    {
        var (host, world) = await F1TestHostExtensions.CreateF1HostAsync(overrides ?? Live);
        var meeting = world.AddMeeting(2026, 19, (F1SessionType.Practice3, T0.AddMinutes(10)), (F1SessionType.Race, RaceStart));
        await host.SetUpF1GuildAsync(Guild, Channel);
        await host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<Formula1ConfigService>().SetNotificationsAsync(TestHost.Admin(Guild), new(SafetyCar: true, RedFlag: true), CancellationToken.None)).Succeeded.Should().BeTrue());
        await StepAsync(host, TimeSpan.Zero);
        return (host, world, meeting.Sessions[1]);
    }

    private static Task<List<F1RaceControlEventEntity>> IncidentsAsync(TestHost host, string session) => host.InScopeAsync(sp =>
        sp.GetRequiredService<ToroDbContext>().Set<F1RaceControlEventEntity>().AsNoTracking().Where(e => e.SessionKey == session).ToListAsync());

    private static Formula1LiveListener Listener(TestHost host) => host.Services.GetRequiredService<Formula1LiveListener>();

    private static F1LifecycleEvent Event(string reference, F1LifecycleSignal signal, DateTimeOffset at) => new(F1FakeProviders.LifecycleId, reference, signal, at);

    private static F1RaceControlIncident Incident(string reference, F1IncidentKind kind, DateTimeOffset at) => new(F1FakeProviders.LifecycleId, reference, kind, at, 3, null, null, null);

    private static int RedFlagCards(TestHost host) =>
        host.Transport.Messages.Count(m => m.Message.Embed!.Description!.Contains("KIRMIZI BAYRAK", StringComparison.Ordinal));

    [Fact]
    public async Task Messages_dated_a_day_before_the_race_neither_move_it_nor_cost_its_real_cards()
    {
        var (host, world, race) = await WeekendAsync();
        await using var _ = host;
        var reference = F1FakeProviders.Ref(race);

        // Saturday: live messages of another session arrive under the race's key, over MQTT and in the REST history.
        var saturday = T0.AddMinutes(10);
        await Listener(host).OnEventAsync(Event(reference, F1LifecycleSignal.Started, saturday), CancellationToken.None);
        await Listener(host).OnIncidentAsync(Incident(reference, F1IncidentKind.RedFlag, saturday.AddMinutes(1)), CancellationToken.None);
        await Listener(host).OnEventAsync(Event(reference, F1LifecycleSignal.Finished, saturday.AddMinutes(60)), CancellationToken.None);
        world.AddEvent(reference, F1LifecycleSignal.Started, saturday);
        world.AddEvent(reference, F1LifecycleSignal.Finished, saturday.AddMinutes(60));
        host.Clock.Advance(TimeSpan.FromMinutes(70));
        await StepAsync(host, TimeSpan.Zero);

        var row = await SnapshotAsync(host, race.Key);
        row.State.Should().Be((int)F1SessionState.Scheduled);
        row.StartedObservedAt.Should().BeNull();
        row.FinishedObservedAt.Should().BeNull();
        row.LastLifecycleEventAt.Should().BeNull();
        (await IncidentsAsync(host, race.Key)).Should().BeEmpty();
        host.Transport.Messages.Should().BeEmpty();

        // Sunday: the real race is tracked and announced as usual.
        host.Clock.SetUtcNow(RaceStart.AddMinutes(1));
        await StepAsync(host, TimeSpan.Zero);
        await Listener(host).OnEventAsync(Event(reference, F1LifecycleSignal.Started, RaceStart.AddSeconds(30)), CancellationToken.None);
        await StepAsync(host, TimeSpan.FromSeconds(5));
        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Started);
        (await OutboxAsync(host, "started:")).Should().ContainSingle(o => o.SourceKey == race.Key);

        await Listener(host).OnIncidentAsync(Incident(reference, F1IncidentKind.RedFlag, host.Clock.GetUtcNow()), CancellationToken.None);
        await StepAsync(host, TimeSpan.FromSeconds(71));
        RedFlagCards(host).Should().Be(1);
    }

    [Fact]
    public async Task Inside_the_tolerance_an_early_start_is_still_a_start()
    {
        var (host, _, race) = await WeekendAsync();
        await using var _1 = host;
        host.Clock.SetUtcNow(RaceStart.AddMinutes(-40));
        await StepAsync(host, TimeSpan.Zero);

        await Listener(host).OnEventAsync(Event(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, RaceStart.AddMinutes(-40)), CancellationToken.None);
        await StepAsync(host, TimeSpan.FromSeconds(1));

        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Started);
    }

    [Fact]
    public async Task A_cancellation_is_accepted_however_early_it_is_announced()
    {
        var (host, _, race) = await WeekendAsync();
        await using var _1 = host;

        await Listener(host).OnEventAsync(Event(F1FakeProviders.Ref(race), F1LifecycleSignal.Cancelled, T0), CancellationToken.None);
        await StepAsync(host, TimeSpan.FromSeconds(1));

        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Cancelled);
    }

    [Fact]
    public async Task A_session_re_keyed_by_the_provider_follows_the_new_id_and_the_old_holder_lets_go()
    {
        var (host, world, race) = await WeekendAsync();
        await using var _ = host;
        var practice = world.ProviderSessions.Single(s => s.Type == F1SessionType.Practice3);
        var raceSession = world.ProviderSessions.Single(s => s.Type == F1SessionType.Race);
        (await SnapshotAsync(host, race.Key)).LifecycleProviderRef.Should().Be(raceSession.Ref);

        // The provider's id for the race turns out to be practice's, and the race gets a new one.
        world.ProviderSessions.Clear();
        world.ProviderSessions.Add(practice with { Ref = raceSession.Ref });
        world.ProviderSessions.Add(raceSession with { Ref = "P-real-race" });
        await StepAsync(host, TimeSpan.FromMinutes(6));

        (await SnapshotAsync(host, race.Key)).LifecycleProviderRef.Should().Be("P-real-race");
        (await SnapshotAsync(host, "2026-19-fp3")).LifecycleProviderRef.Should().Be(raceSession.Ref);

        world.ProviderSessions.Clear();
        world.ProviderSessions.Add(raceSession with { Ref = raceSession.Ref });
        await StepAsync(host, TimeSpan.FromMinutes(6));
        (await SnapshotAsync(host, race.Key)).LifecycleProviderRef.Should().Be(raceSession.Ref);
        (await SnapshotAsync(host, "2026-19-fp3")).LifecycleProviderRef.Should().BeNull("no two sessions keep one provider id");
    }

    /// <summary>The state production was left in: the race "finished" on Saturday, with Saturday's messages on it.</summary>
    private static Task CorruptAsync(TestHost host, string session, F1SessionState state) => host.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<ToroDbContext>();
        var row = await db.Set<F1SessionSnapshotEntity>().SingleAsync(s => s.SessionKey == session);
        row.State = (int)state;
        row.StartedObservedAt = T0;
        row.StartedRecordedAt = T0;
        row.FinishedObservedAt = T0.AddHours(1);
        row.LastLifecycleEventAt = T0.AddHours(1);
        db.Set<F1RaceControlEventEntity>().Add(new F1RaceControlEventEntity
        {
            SessionKey = session,
            Kind = (int)F1IncidentKind.RedFlagCleared,
            OccurredAt = T0,
            Fingerprint = "saturday",
            Provider = F1FakeProviders.LifecycleId,
            RecordedAt = T0,
        });
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task A_race_already_moved_by_such_messages_is_repaired_and_then_tracked()
    {
        var (host, world, race) = await WeekendAsync();
        await using var _ = host;
        await CorruptAsync(host, race.Key, F1SessionState.FinishedPendingResults);

        await StepAsync(host, TimeSpan.FromSeconds(1));

        var row = await SnapshotAsync(host, race.Key);
        row.State.Should().Be((int)F1SessionState.Scheduled);
        row.StartedObservedAt.Should().BeNull();
        row.StartedRecordedAt.Should().BeNull();
        row.FinishedObservedAt.Should().BeNull();
        row.LastLifecycleEventAt.Should().BeNull();
        (await IncidentsAsync(host, race.Key)).Should().BeEmpty();

        // After the race has really started the provider's history puts it right; a late repair announces nothing stale.
        var reference = F1FakeProviders.Ref(race);
        world.AddEvent(reference, F1LifecycleSignal.Started, RaceStart);
        world.AddEvent(reference, F1LifecycleSignal.Finished, RaceStart.AddMinutes(100));
        host.Clock.SetUtcNow(RaceStart.AddMinutes(110));
        await StepAsync(host, TimeSpan.Zero);
        row = await SnapshotAsync(host, race.Key);
        row.StartedObservedAt.Should().Be(RaceStart);
        row.State.Should().Be((int)F1SessionState.FinishedPendingResults);
        host.Transport.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_session_with_a_final_classification_is_left_alone()
    {
        var (host, _, race) = await WeekendAsync();
        await using var _1 = host;
        await CorruptAsync(host, race.Key, F1SessionState.Finalised);

        await StepAsync(host, TimeSpan.FromSeconds(1));

        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Finalised);
    }

    // ---------------------------------------------------------------- one-off red-flag backfill

    private static readonly DateTimeOffset BackfillUntil = RaceStart.AddHours(5);

    private static Dictionary<string, string?> Backfill(string session, DateTimeOffset until) => new(Live)
    {
        ["Formula1:IncidentBackfillSessionKey"] = session,
        ["Formula1:IncidentBackfillUntilUtc"] = until.ToString("O"),
    };

    /// <summary>The race ran unobserved: an hour after its end the provider's history holds its incidents.</summary>
    private static void RaceRanUnobserved(F1FakeProviders world, F1Session race, TestHost host)
    {
        var reference = F1FakeProviders.Ref(race);
        world.AddIncident(reference, F1IncidentKind.SafetyCarDeployed, RaceStart.AddMinutes(10), lap: 5);
        world.AddIncident(reference, F1IncidentKind.RedFlag, RaceStart.AddMinutes(20), lap: 9);
        world.AddIncident(reference, F1IncidentKind.RedFlagCleared, RaceStart.AddMinutes(40), lap: 9);
        host.Clock.SetUtcNow(RaceStart.AddHours(3));
    }

    [Fact]
    public async Task Backfill_sends_the_missed_red_flag_card_once_and_nothing_else()
    {
        const string RaceKey = "2026-19-race";
        var (host, world, race) = await WeekendAsync(Backfill(RaceKey, BackfillUntil));
        await using var _ = host;
        race.Key.Should().Be(RaceKey);
        RaceRanUnobserved(world, race, host);

        await StepAsync(host, TimeSpan.Zero);
        await StepAsync(host, TimeSpan.Zero);

        var row = (await OutboxAsync(host, "red_flag:")).Should().ContainSingle().Subject;
        row.Status.Should().Be(OutboxStatus.Sent);
        row.ExpiresAt.Should().Be(BackfillUntil);
        RedFlagCards(host).Should().Be(1);
        (await OutboxAsync(host, "safety_car:")).Should().BeEmpty("only red flags are recovered");
        (await OutboxAsync(host, "started:")).Should().BeEmpty();

        // Later ticks (and the 5-minute re-read of the history) never repeat it.
        for (var i = 0; i < 3; i++)
            await StepAsync(host, TimeSpan.FromMinutes(6));
        RedFlagCards(host).Should().Be(1);
    }

    [Fact]
    public async Task Without_backfill_a_stale_red_flag_stays_unannounced()
    {
        var (host, world, race) = await WeekendAsync();
        await using var _ = host;
        RaceRanUnobserved(world, race, host);

        await StepAsync(host, TimeSpan.Zero);

        (await IncidentsAsync(host, race.Key)).Should().Contain(i => i.Kind == (int)F1IncidentKind.RedFlag, "reconciliation still records it");
        (await OutboxAsync(host, "red_flag:")).Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_ends_at_its_time_and_covers_only_its_session()
    {
        var (expired, expiredWorld, expiredRace) = await WeekendAsync(Backfill("2026-19-race", RaceStart.AddHours(2)));
        await using var _ = expired;
        RaceRanUnobserved(expiredWorld, expiredRace, expired);
        await StepAsync(expired, TimeSpan.Zero);
        (await OutboxAsync(expired, "red_flag:")).Should().BeEmpty("the window has passed");

        var (other, otherWorld, otherRace) = await WeekendAsync(Backfill("2026-19-fp3", BackfillUntil));
        await using var _1 = other;
        RaceRanUnobserved(otherWorld, otherRace, other);
        await StepAsync(other, TimeSpan.Zero);
        (await OutboxAsync(other, "red_flag:")).Should().BeEmpty("another session's backfill does not cover this race");
    }

    [Fact]
    public void Backfill_options_are_set_together()
    {
        new Formula1Options().Validate().Should().BeEmpty();
        new Formula1Options { IncidentBackfillSessionKey = "2026-19-race", IncidentBackfillUntilUtc = T0 }.Validate().Should().BeEmpty();
        new Formula1Options { IncidentBackfillSessionKey = "2026-19-race" }.Validate().Should().ContainSingle(e => e.Contains("IncidentBackfill", StringComparison.Ordinal));
        new Formula1Options { IncidentBackfillUntilUtc = T0 }.Validate().Should().ContainSingle(e => e.Contains("IncidentBackfill", StringComparison.Ordinal));
        new Formula1Options { LifecycleEarlyToleranceMinutes = 1 }.Validate().Should().ContainSingle(e => e.Contains("LifecycleEarlyToleranceMinutes", StringComparison.Ordinal));
    }
}
