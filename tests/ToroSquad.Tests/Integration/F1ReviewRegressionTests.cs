using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Regression tests for the second engineering review of PR #6: roster-based result completeness, provable pre-session
/// standings baselines across downtime (incl. sprint + race in one round), and baseline semantics for delayed sessions.
/// </summary>
public sealed class F1ReviewRegressionTests
{
    private static F1StandingsSnapshot Drivers(int round, params (string Id, decimal Points)[] rows) => F1FakeProviders.DriverTable(2026, round, rows);

    private static F1StandingsSnapshot Teams(int round, params (string Id, decimal Points)[] rows) => F1FakeProviders.ConstructorTable(2026, round, rows);

    private static FakeMessageTransportMessage ResultCard(TestHost host) =>
        new(host.Transport.Messages.Single(m => m.Message.Embed!.Title!.Contains("Sonucu", StringComparison.Ordinal)));

    private sealed record FakeMessageTransportMessage(ToroSquad.Discord.Transport.FakeMessageTransport.FakeMessage Inner)
    {
        public OutgoingMessage Current => Inner.Edits.Count > 0 ? Inner.Edits[^1] : Inner.Message;
    }

    /// <summary>A second process on the first one's database and Discord (the "restart").</summary>
    private static async Task<TestHost> RestartAsync(TestHost first, F1FakeProviders world, DateTimeOffset at)
    {
        var transport = first.Transport;
        var (second, _) = await F1TestHostExtensions.CreateF1HostAsync(
            new(Live) { ["Bot:DataDirectory"] = first.Directory }, start: at, copyFrom: world,
            extra: s =>
            {
                s.AddSingleton(transport);
                s.AddSingleton<IMessageTransport>(transport);
            });
        await second.Services.GetRequiredService<Formula1Poller>().WarmUpAsync(CancellationToken.None);
        return second;
    }

    // ---------------------------------------------------------------- 1. result completeness

    [Fact]
    public async Task Ten_of_twenty_session_drivers_is_never_published_and_the_complete_grid_is_published_once()
    {
        var (host, fake, race) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race, entries: 10, roster: 20); // positions 1..10 only
        await StepAsync(host, TimeSpan.FromMinutes(141));
        await StepAsync(host, TimeSpan.FromMinutes(5));
        (await OutboxAsync(host, "result:")).Should().BeEmpty("a contiguous top 10 is not the classification of a 20-car session");
        (await SnapshotAsync(host, race.Key)).FinalisedObservedAt.Should().BeNull();

        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race, entries: 20, roster: 20);
        await StepAsync(host, TimeSpan.FromMinutes(10));
        (await OutboxAsync(host, "result:")).Should().ContainSingle();
        ResultCard(host).Current.Embed!.Description.Should().Contain("20. Driver 20");
    }

    // ---------------------------------------------------------------- 2. pre-session baseline across downtime

    [Fact]
    public async Task After_downtime_the_post_race_table_is_never_taken_as_baseline_and_is_attached_to_the_same_result_card()
    {
        var (first, world) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = first;
        var race = world.AddMeeting(2026, 18, (F1SessionType.Race, T0.AddHours(2))).Sessions[0];
        world.Drivers = Drivers(17, ("a", 100), ("b", 90));
        world.Constructors = Teams(17, ("x", 190));
        await first.SetUpF1GuildAsync(Guild, Channel, Role);
        await StepAsync(first, TimeSpan.Zero); // online: schedule + pre-race standings stored; the race is not yet in its window

        // The bot goes offline before the race. The race happens, the provider updates standings and publishes the result.
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddHours(2).AddMinutes(3));
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddHours(3).AddMinutes(50));
        world.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        world.Drivers = Drivers(18, ("b", 115), ("a", 118));
        world.Constructors = Teams(18, ("x", 233));

        await using var second = await RestartAsync(first, world, T0.AddHours(5)); // restart after the race
        await StepAsync(second, TimeSpan.Zero);
        var snapshot = await SnapshotAsync(second, race.Key);
        snapshot.StandingsBaselineDriversHash.Should().Be(Drivers(17, ("a", 100), ("b", 90)).CanonicalHash(), "the persisted pre-race table, not the one fetched after the restart");
        (await OutboxAsync(second, "result:")).Should().ContainSingle();

        await StepAsync(second, TimeSpan.FromMinutes(1)); // first settle-window check
        first.Transport.Messages.Should().ContainSingle("one result message");
        var card = first.Transport.Messages.Single();
        card.Pinged.Should().BeFalse("results do not ping by default");
        var edit = card.Edits.Should().ContainSingle("the change is recognized and attached to the SAME message").Subject;
        edit.Mentions.Roles.Should().BeEmpty();
        edit.Embed!.Fields.Single(f => f.Name == "🏆 Sürücüler").Value.Should().Contain("Driver a — 118 puan");
        edit.Embed.Fields.Single(f => f.Name == "🏭 Takımlar").Value.Should().Contain("233");
    }

    [Fact]
    public async Task Without_a_provable_pre_session_table_the_result_still_publishes_and_standings_degrade_honestly()
    {
        var (first, world) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = first;
        var race = world.AddMeeting(2026, 18, (F1SessionType.Race, T0.AddHours(2))).Sessions[0];
        world.StandingsFailure = F1ProviderOutcome.TransportError; // nothing is ever fetched before the race
        await first.SetUpF1GuildAsync(Guild, Channel);
        await StepAsync(first, TimeSpan.Zero);

        world.StandingsFailure = null;
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddHours(2).AddMinutes(3));
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddHours(3).AddMinutes(50));
        world.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        world.Drivers = Drivers(18, ("b", 115), ("a", 118));
        world.Constructors = Teams(18, ("x", 233));

        await using var second = await RestartAsync(first, world, T0.AddHours(5));
        for (var i = 0; i < 25; i++)
            await StepAsync(second, TimeSpan.FromMinutes(10)); // past the settle window
        var snapshot = await SnapshotAsync(second, race.Key);
        snapshot.StandingsBaselineDriversHash.Should().BeNull("no false baseline");
        snapshot.StandingsDriversSnapshotId.Should().BeNull("no standings attributed without proof");
        var card = first.Transport.Messages.Should().ContainSingle("the result still publishes").Subject;
        var current = card.Edits.Count > 0 ? card.Edits[^1] : card.Message;
        current.Embed!.Fields.Single().Value.Should().Contain("/f1 standings drivers", "degrades to a pointer to the command");
    }

    [Fact]
    public async Task Sprint_and_race_in_one_round_each_get_their_own_table_when_both_updates_were_observed()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(new(Live) { ["Formula1:StandingsSettleWindowMinutes"] = "180" });
        await using var _ = host;
        var meeting = fake.AddMeeting(2026, 18, (F1SessionType.Sprint, T0.AddMinutes(30)), (F1SessionType.Race, T0.AddDays(1)));
        var sprint = meeting.Find(F1SessionType.Sprint)!;
        var race = meeting.Find(F1SessionType.Race)!;
        fake.Drivers = Drivers(17, ("a", 100), ("b", 90));
        fake.Constructors = Teams(17, ("x", 190));
        await host.SetUpF1GuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);

        fake.AddEvent(F1FakeProviders.Ref(sprint), F1LifecycleSignal.Started, T0.AddMinutes(31));
        fake.AddEvent(F1FakeProviders.Ref(sprint), F1LifecycleSignal.Finished, T0.AddMinutes(85));
        fake.ResultsByRef[F1FakeProviders.Ref(sprint)] = F1FakeProviders.Result(sprint);
        await StepAsync(host, TimeSpan.FromMinutes(86));
        fake.Drivers = Drivers(18, ("a", 108), ("b", 97)); // post-sprint table published before the race
        fake.Constructors = Teams(18, ("x", 205));
        await StepAsync(host, TimeSpan.FromMinutes(10));
        var sprintCard = host.Transport.Messages.Single(m => m.Message.Embed!.Title!.Contains("Sprint", StringComparison.Ordinal));
        sprintCard.Edits[^1].Embed!.Fields.Single(f => f.Name == "🏆 Sürücüler").Value.Should().Contain("108");

        await StepAsync(host, TimeSpan.FromHours(22)); // race day; the pre-race baseline is the post-sprint table
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddDays(1).AddMinutes(3));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddDays(1).AddMinutes(110));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        host.Clock.Advance(T0.AddDays(1).AddMinutes(112) - host.Clock.GetUtcNow());
        await TickAsync(host);
        await DeliverAsync(host);
        fake.Drivers = Drivers(18, ("a", 133), ("b", 115));
        fake.Constructors = Teams(18, ("x", 248));
        await StepAsync(host, TimeSpan.FromMinutes(6));
        var raceCard = host.Transport.Messages.Single(m => m.Message.Embed!.Title!.Contains("Yarış Sonucu", StringComparison.Ordinal));
        raceCard.Edits[^1].Embed!.Fields.Single(f => f.Name == "🏆 Sürücüler").Value.Should().Contain("133");
        sprintCard.Edits[^1].Embed!.Fields.Single(f => f.Name == "🏆 Sürücüler").Value.Should().Contain("108", "the sprint card keeps the post-sprint table");
    }

    [Fact]
    public async Task Sprint_and_race_after_downtime_never_attribute_an_ambiguous_same_round_table()
    {
        var (first, world) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = first;
        var meeting = world.AddMeeting(2026, 18, (F1SessionType.Sprint, T0.AddHours(2)), (F1SessionType.Race, T0.AddHours(8)));
        var sprint = meeting.Find(F1SessionType.Sprint)!;
        var race = meeting.Find(F1SessionType.Race)!;
        world.Drivers = Drivers(17, ("a", 100), ("b", 90));
        world.Constructors = Teams(17, ("x", 190));
        await first.SetUpF1GuildAsync(Guild, Channel);
        await StepAsync(first, TimeSpan.Zero);

        // Offline across both sessions; afterwards the provider only has a round-18 table (post-sprint OR post-race?).
        foreach (var (s, start, end) in new[] { (sprint, T0.AddHours(2), T0.AddHours(3)), (race, T0.AddHours(8), T0.AddHours(10)) })
        {
            world.AddEvent(F1FakeProviders.Ref(s), F1LifecycleSignal.Started, start.AddMinutes(1));
            world.AddEvent(F1FakeProviders.Ref(s), F1LifecycleSignal.Finished, end);
            world.ResultsByRef[F1FakeProviders.Ref(s)] = F1FakeProviders.Result(s);
        }

        world.Drivers = Drivers(18, ("a", 130), ("b", 110));
        world.Constructors = Teams(18, ("x", 240));
        await using var second = await RestartAsync(first, world, T0.AddHours(11));
        for (var i = 0; i < 25; i++)
            await StepAsync(second, TimeSpan.FromMinutes(10));

        (await SnapshotAsync(second, sprint.Key)).StandingsDriversSnapshotId.Should().BeNull("fetched after the race could have started: may include the race");
        (await SnapshotAsync(second, race.Key)).StandingsDriversSnapshotId.Should().BeNull("post-sprint vs post-race cannot be told apart");
        foreach (var card in first.Transport.Messages)
            (card.Edits.Count > 0 ? card.Edits[^1] : card.Message).Embed!.Fields.Should().NotContain(f => f.Name == "🏆 Sürücüler");
        (await OutboxAsync(second, "result:race")).Should().ContainSingle("the race result itself is still delivered");
    }

    // ---------------------------------------------------------------- 4. baseline semantics for delayed / running sessions

    [Fact]
    public async Task A_session_first_seen_after_its_scheduled_start_but_before_it_starts_is_announced_when_the_provider_confirms_the_delayed_start()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        var race = fake.AddMeeting(2026, 18, (F1SessionType.Race, T0.AddMinutes(-5))).Sessions[0]; // scheduled 11:55, first seen 12:00
        fake.Drivers = Drivers(17, ("a", 100));
        await host.SetUpF1GuildAsync(Guild, Channel, Role);
        await StepAsync(host, TimeSpan.Zero);
        (await SnapshotAsync(host, race.Key)).IsBaseline.Should().BeFalse("not over yet: a delayed session is not history");
        (await OutboxAsync(host, "")).Should().BeEmpty("the passed scheduled time is still not a start");

        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(20)); // weather delay
        await StepAsync(host, TimeSpan.FromMinutes(21));
        (await OutboxAsync(host, "started:race")).Should().ContainSingle("a fresh, provider-confirmed start after the watermark");
        host.Transport.Messages.Single().Pinged.Should().BeTrue();

        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(121));
        (await OutboxAsync(host, "result:race")).Should().ContainSingle();
    }

    [Fact]
    public async Task Enabling_mid_session_sends_no_stale_start_but_delivers_the_result_that_finalises_afterwards()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        var race = fake.AddMeeting(2026, 18, (F1SessionType.Race, T0.AddMinutes(-40))).Sessions[0];
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(-38)); // started before the module was enabled
        await host.SetUpF1GuildAsync(Guild, Channel, Role); // enabled at 12:00 → watermark
        await StepAsync(host, TimeSpan.Zero);
        await StepAsync(host, TimeSpan.FromMinutes(2));
        (await SnapshotAsync(host, race.Key)).State.Should().Be((int)F1SessionState.Started);
        (await OutboxAsync(host, "started:")).Should().BeEmpty("the start happened before the watermark and is not fresh");

        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(80));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(host, TimeSpan.FromMinutes(80));
        (await OutboxAsync(host, "result:race")).Should().ContainSingle("decision: a result that finalises after the guild watermark is new information and is delivered");
    }

    [Fact]
    public async Task A_session_first_seen_after_its_planned_end_stays_silent_even_if_it_is_still_running()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        var race = fake.AddMeeting(2026, 18, (F1SessionType.Race, T0.AddHours(-3))).Sessions[0]; // planned end 11:00
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddHours(-3));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Suspended, T0.AddHours(-2)); // very long red flag
        await host.SetUpF1GuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);
        (await SnapshotAsync(host, race.Key)).IsBaseline.Should().BeTrue("fail closed: first seen after its planned end");

        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(10));
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(60));
        fake.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        for (var i = 0; i < 6; i++)
            await StepAsync(host, TimeSpan.FromMinutes(15));
        (await OutboxAsync(host, "")).Should().BeEmpty();
    }
}
