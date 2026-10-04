using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Notifications;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Spoiler hold for live incident cards: Safety Car and red-flag cards reach Discord no earlier than the provider's event
/// time + 30 s, while ingestion, persistence, dedupe and staging stay immediate. Real poller, workflow, planner, outbox and
/// SQLite; incidents arrive through the live listener (as from MQTT) and are also visible to REST reconciliation.
/// </summary>
public sealed class F1SpoilerDelayTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(30);

    private static Task EnableAsync(TestHost host, F1NotificationChanges changes) => host.InScopeAsync(async sp =>
        (await sp.GetRequiredService<Formula1ConfigService>().SetNotificationsAsync(TestHost.Admin(Guild), changes, CancellationToken.None)).Succeeded.Should().BeTrue());

    /// <summary>A running race (start card delivered) with Safety Car and red-flag cards on.</summary>
    private static async Task<(TestHost Host, F1FakeProviders World, string Ref)> RunningRaceAsync(F1NotificationChanges? changes = null)
    {
        var (host, world, race) = await RaceWeekendAsync();
        await EnableAsync(host, changes ?? new(SafetyCar: true, RedFlag: true));
        await StepAsync(host, TimeSpan.Zero);
        var reference = F1FakeProviders.Ref(race);
        world.AddEvent(reference, F1LifecycleSignal.Started, T0.AddMinutes(31));
        await StepAsync(host, TimeSpan.FromMinutes(32));
        host.Transport.Messages.Should().ContainSingle("the race start card");
        return (host, world, reference);
    }

    /// <summary>The provider reports an incident that happened at <paramref name="at"/> (live message + REST history).</summary>
    private static async Task IncidentAsync(TestHost host, F1FakeProviders world, string reference, F1IncidentKind kind, DateTimeOffset at, int? lap = null)
    {
        world.AddIncident(reference, kind, at, lap);
        await host.Services.GetRequiredService<Formula1LiveListener>()
            .OnIncidentAsync(new F1RaceControlIncident(F1FakeProviders.LifecycleId, reference, kind, at, lap, null, null, null), CancellationToken.None);
    }

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

    private static int Cards(TestHost host, string headline) =>
        host.Transport.Messages.Count(m => m.Message.Embed!.Description!.Contains(headline, StringComparison.Ordinal));

    [Theory]
    [InlineData(F1IncidentKind.SafetyCarDeployed, F1IncidentKind.SafetyCarEnding, "safety_car:", "SAFETY CAR")]
    [InlineData(F1IncidentKind.RedFlag, F1IncidentKind.RedFlagCleared, "red_flag:", "KIRMIZI BAYRAK")]
    public async Task Incident_is_recorded_and_staged_at_once_but_delivered_only_after_the_hold(F1IncidentKind open, F1IncidentKind close, string kindPrefix, string headline)
    {
        var (host, world, reference) = await RunningRaceAsync();
        await using var _ = host;
        var t = host.Clock.GetUtcNow();

        // The provider message reaches us 3 s after the event: everything but the Discord send happens now.
        host.Clock.Advance(TimeSpan.FromSeconds(3));
        await IncidentAsync(host, world, reference, open, t, lap: 12);
        await StepAsync(host, TimeSpan.Zero);
        var row = (await OutboxAsync(host, kindPrefix)).Should().ContainSingle().Subject;
        row.Status.Should().Be(OutboxStatus.Pending);
        row.CreatedAt.Should().Be(t.AddSeconds(3), "staged immediately");
        row.NextAttemptAt.Should().Be(t + Hold, "provider event time + 30 s, not receipt + 30 s");
        row.ExpiresAt.Should().BeAfter(row.NextAttemptAt!.Value);
        Cards(host, headline).Should().Be(0);

        await StepAsync(host, TimeSpan.FromSeconds(26)); // event + 29 s
        Cards(host, headline).Should().Be(0, "29 s after the event is still too early");

        await StepAsync(host, TimeSpan.FromSeconds(1)); // event + 30 s
        Cards(host, headline).Should().Be(1);

        // Replays (REST reconciliation, repeated live message) never add a card or a second row.
        await IncidentAsync(host, world, reference, open, t, lap: 12);
        for (var i = 0; i < 3; i++)
            await StepAsync(host, TimeSpan.FromMinutes(1));
        Cards(host, headline).Should().Be(1);
        (await OutboxAsync(host, kindPrefix)).Should().ContainSingle();

        // A second, separate phase gets its own hold from its own event time.
        var t2 = host.Clock.GetUtcNow();
        await IncidentAsync(host, world, reference, close, t2.AddSeconds(-20));
        await IncidentAsync(host, world, reference, open, t2, lap: 20);
        await StepAsync(host, TimeSpan.FromSeconds(2));
        (await OutboxAsync(host, kindPrefix)).Should().HaveCount(2).And.Contain(r => r.Status == OutboxStatus.Pending && r.NextAttemptAt == t2 + Hold);
        await StepAsync(host, TimeSpan.FromSeconds(27));
        Cards(host, headline).Should().Be(1);
        await StepAsync(host, TimeSpan.FromSeconds(1));
        Cards(host, headline).Should().Be(2);
    }

    [Fact]
    public async Task Staging_again_during_the_hold_does_not_slide_the_delivery_time()
    {
        var (host, world, reference) = await RunningRaceAsync();
        await using var _ = host;
        var t = host.Clock.GetUtcNow();
        await IncidentAsync(host, world, reference, F1IncidentKind.SafetyCarDeployed, t, lap: 5);

        foreach (var seconds in new[] { 3, 5, 5, 5, 5 }) // planner runs at +3, +8, +13, +18, +23
        {
            await StepAsync(host, TimeSpan.FromSeconds(seconds));
            await host.InScopeAsync(sp => sp.GetRequiredService<Formula1NotificationPlanner>().PlanAsync(CancellationToken.None));
            (await OutboxAsync(host, "safety_car:")).Single().NextAttemptAt.Should().Be(t + Hold);
        }

        Cards(host, "SAFETY CAR").Should().Be(0);
        await StepAsync(host, TimeSpan.FromSeconds(7));
        Cards(host, "SAFETY CAR").Should().Be(1);
    }

    [Fact]
    public async Task Two_close_incidents_keep_their_own_times_and_their_order()
    {
        var (host, world, reference) = await RunningRaceAsync();
        await using var _ = host;
        var t = host.Clock.GetUtcNow();

        await IncidentAsync(host, world, reference, F1IncidentKind.SafetyCarDeployed, t, lap: 30);
        await StepAsync(host, TimeSpan.FromSeconds(15));
        await IncidentAsync(host, world, reference, F1IncidentKind.RedFlag, t.AddSeconds(15), lap: 30);
        await StepAsync(host, TimeSpan.FromSeconds(1));
        (await OutboxAsync(host, "safety_car:")).Single().NextAttemptAt.Should().Be(t.AddSeconds(30));
        (await OutboxAsync(host, "red_flag:")).Single().NextAttemptAt.Should().Be(t.AddSeconds(45));

        await StepAsync(host, TimeSpan.FromSeconds(14)); // +30
        (Cards(host, "SAFETY CAR"), Cards(host, "KIRMIZI BAYRAK")).Should().Be((1, 0));
        await StepAsync(host, TimeSpan.FromSeconds(14)); // +44
        Cards(host, "KIRMIZI BAYRAK").Should().Be(0);
        await StepAsync(host, TimeSpan.FromSeconds(1)); // +45
        (Cards(host, "SAFETY CAR"), Cards(host, "KIRMIZI BAYRAK")).Should().Be((1, 1));
        host.Transport.Messages.Select(m => m.Message.Embed!.Description!.Split('\n')[0]).Skip(1).Should().Equal("🚗 **SAFETY CAR**", "🚩 **KIRMIZI BAYRAK**");
    }

    [Fact]
    public async Task Restart_during_the_hold_neither_sends_early_nor_twice()
    {
        var (host, world, reference) = await RunningRaceAsync();
        await using var _ = host;
        var t = host.Clock.GetUtcNow();
        host.Clock.Advance(TimeSpan.FromSeconds(3));
        await IncidentAsync(host, world, reference, F1IncidentKind.SafetyCarDeployed, t, lap: 9);
        await StepAsync(host, TimeSpan.Zero);
        (await OutboxAsync(host, "safety_car:")).Single().NextAttemptAt.Should().Be(t + Hold);

        // Process stops at +10 s, the new one is up at +18 s.
        await using var second = await RestartAsync(host, world, t.AddSeconds(18));
        await StepAsync(second, TimeSpan.Zero);
        Cards(host, "SAFETY CAR").Should().Be(0, "a restart does not release the hold");
        (await OutboxAsync(second, "safety_car:")).Single().Should().Match<ToroSquad.Infrastructure.Persistence.OutboxMessageEntity>(r =>
            r.Status == OutboxStatus.Pending && r.NextAttemptAt == t + Hold);

        await StepAsync(second, TimeSpan.FromSeconds(11)); // +29
        Cards(host, "SAFETY CAR").Should().Be(0);
        await StepAsync(second, TimeSpan.FromSeconds(1)); // +30
        for (var i = 0; i < 3; i++)
            await StepAsync(second, TimeSpan.FromSeconds(20));
        Cards(host, "SAFETY CAR").Should().Be(1, "exactly one card after the restart");
        (await OutboxAsync(second, "safety_car:")).Should().ContainSingle();
    }

    [Fact]
    public async Task An_event_that_arrives_later_than_the_hold_is_not_held_again()
    {
        var (host, world, reference) = await RunningRaceAsync();
        await using var _ = host;
        var t = host.Clock.GetUtcNow();

        host.Clock.Advance(TimeSpan.FromSeconds(45)); // the provider delivered it 45 s late
        await IncidentAsync(host, world, reference, F1IncidentKind.RedFlag, t, lap: 2);
        await StepAsync(host, TimeSpan.Zero);

        Cards(host, "KIRMIZI BAYRAK").Should().Be(1, "event time + 30 s has already passed");
    }

    [Fact]
    public async Task Pause_or_switching_the_type_off_during_the_hold_cancels_the_card()
    {
        var (host, world, reference) = await RunningRaceAsync();
        await using var _ = host;
        var t = host.Clock.GetUtcNow();
        await IncidentAsync(host, world, reference, F1IncidentKind.SafetyCarDeployed, t, lap: 4);
        await IncidentAsync(host, world, reference, F1IncidentKind.RedFlag, t.AddSeconds(1), lap: 4);
        await StepAsync(host, TimeSpan.FromSeconds(3));
        (await OutboxAsync(host, "")).Count(r => r.Status == OutboxStatus.Pending).Should().Be(2);

        // Safety Car switched off, then the whole module paused, while both cards wait.
        await EnableAsync(host, new(SafetyCar: false));
        await host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<Formula1ConfigService>().PauseAsync(TestHost.Admin(Guild), true, CancellationToken.None)).Succeeded.Should().BeTrue());
        await host.Services.GetRequiredService<ToroSquad.Infrastructure.Delivery.OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);
        host.Clock.Advance(TimeSpan.FromSeconds(40));
        await DeliverAsync(host);

        host.Transport.Messages.Should().ContainSingle("only the earlier start card");
        (await OutboxAsync(host, "safety_car:")).Single().Status.Should().Be(OutboxStatus.Cancelled);
        (await OutboxAsync(host, "red_flag:")).Single().Status.Should().Be(OutboxStatus.Cancelled);
    }

    [Fact]
    public async Task Race_start_and_disqualification_cards_are_not_held()
    {
        var (host, world, race) = await RaceWeekendAsync();
        await using var _ = host;
        await EnableAsync(host, new(Disqualification: true, SafetyCar: true, RedFlag: true));
        await StepAsync(host, TimeSpan.Zero);
        var reference = F1FakeProviders.Ref(race);

        // Start: provider time 4 s ago → staged and due at once, delivered by the same dispatcher run.
        host.Clock.Advance(TimeSpan.FromMinutes(31));
        world.AddEvent(reference, F1LifecycleSignal.Started, host.Clock.GetUtcNow().AddSeconds(-4));
        await host.Services.GetRequiredService<Formula1Poller>().TickAsync(CancellationToken.None);
        var start = (await OutboxAsync(host, "started:")).Single();
        start.NextAttemptAt.Should().Be(start.CreatedAt, "the race start card is never spoiler-held");
        await DeliverAsync(host);
        host.Transport.Messages.Should().ContainSingle();

        // Disqualification from the classification: due at once as well.
        var result = F1FakeProviders.Result(race);
        var position = 0;
        world.ResultsByRef[reference] = result with
        {
            Entries = result.Entries.Select(e => e.DriverNumber == 3 ? e with { Position = null, Status = F1ResultStatus.Dsq, GapSeconds = null } : e with { Position = ++position }).ToList(),
        };
        world.AddEvent(reference, F1LifecycleSignal.Finished, host.Clock.GetUtcNow().AddMinutes(60));
        host.Clock.Advance(TimeSpan.FromMinutes(61));
        await host.Services.GetRequiredService<Formula1Poller>().TickAsync(CancellationToken.None);
        await host.Services.GetRequiredService<Formula1Poller>().TickAsync(CancellationToken.None);
        var dsq = (await OutboxAsync(host, "disqualification:")).Single();
        dsq.NextAttemptAt.Should().Be(dsq.CreatedAt, "disqualifications come from the classification and are not held");
        (await OutboxAsync(host, "result:")).Single().Should().Match<ToroSquad.Infrastructure.Persistence.OutboxMessageEntity>(r => r.NextAttemptAt == r.CreatedAt);
    }

    [Fact]
    public async Task Hold_of_zero_turns_the_delay_off()
    {
        var (host, world, race) = await RaceWeekendAsync(new(Live) { ["Formula1:LiveIncidentSpoilerDelaySeconds"] = "0" });
        await using var _ = host;
        await EnableAsync(host, new(SafetyCar: true));
        await StepAsync(host, TimeSpan.Zero);
        var reference = F1FakeProviders.Ref(race);
        world.AddEvent(reference, F1LifecycleSignal.Started, T0.AddMinutes(31));
        await StepAsync(host, TimeSpan.FromMinutes(32));

        await IncidentAsync(host, world, reference, F1IncidentKind.SafetyCarDeployed, host.Clock.GetUtcNow(), lap: 3);
        await StepAsync(host, TimeSpan.Zero);

        Cards(host, "SAFETY CAR").Should().Be(1);
    }

    [Fact]
    public void Hold_setting_defaults_to_thirty_seconds_and_is_validated()
    {
        new Formula1Options().LiveIncidentSpoilerDelaySeconds.Should().Be(30);
        new Formula1Options().Validate().Should().BeEmpty();
        new Formula1Options { LiveIncidentSpoilerDelaySeconds = 0 }.Validate().Should().BeEmpty();
        new Formula1Options { LiveIncidentSpoilerDelaySeconds = 301 }.Validate().Should().Contain(e => e.Contains("LiveIncidentSpoilerDelaySeconds", StringComparison.Ordinal));
        new Formula1Options { LiveIncidentSpoilerDelaySeconds = 60, IncidentFreshMinutes = 1 }.Validate()
            .Should().Contain(e => e.Contains("shorter than IncidentFreshMinutes", StringComparison.Ordinal));
    }
}
