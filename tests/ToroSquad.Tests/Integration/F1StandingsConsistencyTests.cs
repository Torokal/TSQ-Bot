using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Result/standings consistency (final static review of PR #6): a table is only "the standings after a session" if it
/// first appeared after the session was provably over, and a corrected sprint/race result reopens the standings
/// reconciliation so the same card never shows a corrected classification next to stale standings.
/// </summary>
public sealed class F1StandingsConsistencyTests
{
    private static F1StandingsSnapshot Drivers(int round, params (string Id, decimal Points)[] rows) => F1FakeProviders.DriverTable(2026, round, rows);

    private static FakeMessageTransport.FakeMessage ResultCard(TestHost host) =>
        host.Transport.Messages.Single(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal));

    private static OutgoingMessage Current(FakeMessageTransport.FakeMessage card) => card.Edits.Count > 0 ? card.Edits[^1] : card.Message;

    private static string? DriversField(OutgoingMessage message) => message.Embed!.Fields.SingleOrDefault(f => f.Name == "🏆 Sürücüler")?.Value;

    /// <summary>A race (T0+30) whose start is only seen when no longer fresh, finished at T0+140, result available.</summary>
    private static async Task<(TestHost Host, F1FakeProviders Fake, F1Session Race)> FinishedRaceAsync(Dictionary<string, string?>? overrides = null)
    {
        var (host, fake, race) = await RaceWeekendAsync(overrides);
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        return (host, fake, race);
    }

    // ---------------------------------------------------------------- Fix 1: post-session proof

    [Fact]
    public async Task A_and_B_a_table_that_changed_during_the_race_is_not_the_race_standings_but_the_real_post_race_table_is()
    {
        var (host, fake, race) = await RaceWeekendAsync(new(Live) { ["Formula1:StandingsRefreshHours"] = "1" });
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);                              // pre-race table B stored
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        await StepAsync(host, TimeSpan.FromMinutes(45));                   // race running; baseline = B
        var before = await SnapshotAsync(host, race.Key);
        before.State.Should().Be((int)F1SessionState.Started);
        before.StandingsBaselineDriversHash.Should().Be(Drivers(17, ("a", 100), ("b", 90), ("c", 80)).CanonicalHash());

        // A late correction of the earlier sprint changes the table WHILE the race is running (same round).
        var during = Drivers(18, ("a", 103), ("b", 90), ("c", 80));
        fake.Drivers = during;
        await StepAsync(host, TimeSpan.FromMinutes(20));                   // T0+65: routine refresh stores C
        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Started, "C was fetched during the race");

        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(76));                   // T0+141: finished, result finalised, provider still returns C
        var card = ResultCard(host);
        for (var i = 0; i < 4; i++)
            await StepAsync(host, TimeSpan.FromMinutes(6));
        (await SnapshotAsync(host, race.Key)).StandingsDriversSnapshotId.Should().BeNull("C existed before the race was over");
        DriversField(Current(card)).Should().BeNull();
        Current(card).Embed!.Fields.Single().Value.Should().Contain("bekleniyor");

        // B: the real post-race table appears after the finish → attached to the SAME message, without a ping.
        fake.Drivers = Drivers(18, ("a", 128), ("b", 108), ("c", 95));
        await StepAsync(host, TimeSpan.FromMinutes(31));
        host.Transport.Messages.Should().ContainSingle(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal));
        DriversField(Current(card)).Should().Contain("128");
        card.Pinged.Should().BeFalse();
        card.Edits.Should().NotBeEmpty().And.OnlyContain(e => e.Mentions.Roles.Count == 0);
    }

    [Fact]
    public async Task C_without_lifecycle_access_a_restart_after_the_race_proves_completion_by_the_result_and_never_baselines_the_post_race_table()
    {
        var (first, world) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = first;
        world.LifecycleConfigured = false;
        var race = world.AddMeeting(2026, 18, (F1SessionType.Race, T0.AddHours(2))).Sessions[0];
        var preRace = Drivers(17, ("a", 100), ("b", 90));
        world.Drivers = preRace;
        await first.SetUpF1GuildAsync(Guild, Channel);
        await StepAsync(first, TimeSpan.Zero);                             // online: pre-race table persisted

        // Offline through the race; no lifecycle finish will ever be known. Afterwards: result + post-race table.
        world.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        var postRace = Drivers(18, ("b", 115), ("a", 118));
        world.Drivers = postRace;
        var transport = first.Transport;
        var (second, _) = await F1TestHostExtensions.CreateF1HostAsync(new(Live) { ["Bot:DataDirectory"] = first.Directory }, start: T0.AddHours(5), copyFrom: world,
            extra: s =>
            {
                s.AddSingleton(transport);
                s.AddSingleton<IMessageTransport>(transport);
            });
        await using var __ = second;
        await second.Services.GetRequiredService<Formula1Poller>().WarmUpAsync(CancellationToken.None);
        await StepAsync(second, TimeSpan.Zero);
        await StepAsync(second, TimeSpan.FromMinutes(6));

        var snapshot = await SnapshotAsync(second, race.Key);
        snapshot.FinishedObservedAt.Should().BeNull("no lifecycle access");
        snapshot.FinalisedObservedAt.Should().NotBeNull("the complete classification is the completion proof");
        snapshot.StandingsBaselineDriversHash.Should().Be(preRace.CanonicalHash(), "the post-race table is never the pre-session baseline");
        snapshot.StandingsDriversSnapshotId.Should().NotBeNull("first fetched after the result proved the race over");
        var card = first.Transport.Messages.Should().ContainSingle().Subject;
        DriversField(Current(card)).Should().Contain("118");
        card.Edits.Should().OnlyContain(e => e.Mentions.Roles.Count == 0);
    }

    // ---------------------------------------------------------------- Fix 2: a correction reopens the standings watch

    private static async Task<(TestHost Host, F1FakeProviders Fake, F1Session Race, FakeMessageTransport.FakeMessage Card)> RaceWithAttachedStandingsAsync()
    {
        var (host, fake, race) = await FinishedRaceAsync();
        await StepAsync(host, TimeSpan.FromMinutes(141));                  // result card (standings pending)
        fake.Drivers = Drivers(18, ("a", 125), ("b", 108), ("c", 95));
        fake.Constructors = F1FakeProviders.ConstructorTable(2026, 18, ("x", 233), ("y", 95));
        await StepAsync(host, TimeSpan.FromMinutes(6));                    // post-race standings attached
        var card = ResultCard(host);
        DriversField(Current(card)).Should().Contain("125");
        for (var i = 0; i < 20; i++)
            await StepAsync(host, TimeSpan.FromMinutes(10));               // past the 180-minute settle window
        (await SnapshotAsync(host, race.Key)).StandingsWindowClosed.Should().BeTrue();
        return (host, fake, race, card);
    }

    [Fact]
    public async Task D_a_late_dsq_after_the_settle_window_edits_classification_and_then_standings_on_the_same_message()
    {
        var (host, fake, race, card) = await RaceWithAttachedStandingsAsync();
        await using var _ = host;
        var editsBefore = card.Edits.Count;

        // ~+5 h: steward decision — the winner is disqualified (classification changes).
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race, swapFirstTwo: 1);
        for (var i = 0; i < 3; i++)
            await StepAsync(host, TimeSpan.FromMinutes(30));
        var snapshot = await SnapshotAsync(host, race.Key);
        snapshot.StandingsWindowClosed.Should().BeFalse("the correction reopened the standings reconciliation");
        snapshot.StandingsBaselineDriversHash.Should().Be(Drivers(17, ("a", 100), ("b", 90), ("c", 80)).CanonicalHash(), "the pre-session baseline is never overwritten");
        Current(card).Embed!.Description.Should().Contain("🥇 1. Driver 2", "classification corrected");
        DriversField(Current(card)).Should().Contain("125", "the attached table stays until the provider publishes a new one");

        // The provider publishes the corrected championship → SAME message edited again.
        fake.Drivers = Drivers(18, ("b", 126), ("a", 100), ("c", 95));
        await StepAsync(host, TimeSpan.FromMinutes(31)); // next bounded check (backoff 5 → 30 min)
        host.Transport.Messages.Should().ContainSingle(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal), "no second message");
        card.Edits.Count.Should().BeGreaterThan(editsBefore + 1);
        Current(card).Embed!.Description.Should().Contain("🥇 1. Driver 2");
        DriversField(Current(card)).Should().Contain("Driver b — 126 puan");
        card.Edits.Should().OnlyContain(e => e.Mentions.Roles.Count == 0, "no edit ever pings");
        (await OutboxAsync(host, "result:")).Should().ContainSingle();
    }

    [Fact]
    public async Task E_a_correction_without_a_standings_change_keeps_the_attached_table_and_the_reopened_watch_closes_again()
    {
        var (host, fake, race, card) = await RaceWithAttachedStandingsAsync();
        await using var _ = host;
        var attached = (await SnapshotAsync(host, race.Key)).StandingsDriversSnapshotId;

        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race, swapFirstTwo: 1);
        for (var i = 0; i < 3; i++)
            await StepAsync(host, TimeSpan.FromMinutes(30));
        (await SnapshotAsync(host, race.Key)).StandingsWindowClosed.Should().BeFalse();
        for (var i = 0; i < 25; i++)
            await StepAsync(host, TimeSpan.FromMinutes(10));

        var snapshot = await SnapshotAsync(host, race.Key);
        snapshot.StandingsWindowClosed.Should().BeTrue("bounded: the reopened watch closes again");
        snapshot.StandingsWatchUntil.Should().BeOnOrBefore(snapshot.FinalisedObservedAt!.Value.AddHours(24), "never beyond the result correction window");
        snapshot.StandingsDriversSnapshotId.Should().Be(attached, "an unchanged provider table never removes what is shown");
        DriversField(Current(card)).Should().Contain("125");
        Current(card).Embed!.Description.Should().Contain("🥇 1. Driver 2");
        host.Transport.Messages.Should().ContainSingle(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal));
        card.Edits.Should().OnlyContain(e => e.Mentions.Roles.Count == 0);
    }

    [Fact]
    public async Task A_practice_correction_never_starts_standings_polling()
    {
        var (host, fake, practice) = await RaceWeekendAsync(type: F1SessionType.Practice1);
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(practice), F1LifecycleSignal.Started, T0.AddMinutes(30));
        fake.AddEvent(F1FakeProviders.Ref(practice), F1LifecycleSignal.Finished, T0.AddMinutes(90));
        fake.ResultsByRef[F1FakeProviders.Ref(practice)] = F1FakeProviders.Result(practice);
        await StepAsync(host, TimeSpan.FromMinutes(95));
        var calls = fake.StandingsCalls;

        fake.ResultsByRef[F1FakeProviders.Ref(practice)] = F1FakeProviders.Result(practice, swapFirstTwo: 1);
        for (var i = 0; i < 4; i++)
            await StepAsync(host, TimeSpan.FromMinutes(20));
        ResultCard(host).Edits.Should().NotBeEmpty("the practice classification is corrected");
        var snapshot = await SnapshotAsync(host, practice.Key);
        snapshot.StandingsWatchUntil.Should().BeNull();
        fake.StandingsCalls.Should().Be(calls, "a practice correction never triggers standings requests");
    }
}
