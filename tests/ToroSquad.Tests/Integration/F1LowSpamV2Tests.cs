using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Messaging;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Low-spam V2 end to end (real poller, workflow, planner, outbox, SQLite; fake providers and Discord): Thursday weekend
/// schedule, race reminder, Safety Car, red flag and disqualification. Every test checks the exact message count.
/// TestHost.T0 = Thursday 2026-09-24 12:00 UTC.
/// </summary>
public sealed class F1LowSpamV2Tests
{
    private static Task EnableAsync(TestHost host, F1NotificationChanges changes) => host.InScopeAsync(async sp =>
        (await sp.GetRequiredService<Formula1ConfigService>().SetNotificationsAsync(TestHost.Admin(Guild), changes, CancellationToken.None)).Succeeded.Should().BeTrue());

    private static Task<List<F1RaceControlEventEntity>> IncidentRowsAsync(TestHost host) => host.InScopeAsync(sp =>
        sp.GetRequiredService<ToroDbContext>().Set<F1RaceControlEventEntity>().AsNoTracking().ToListAsync());

    /// <summary>A second process on the first host's database and Discord (see F1RestartTests).</summary>
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

    /// <summary>A started race with the given V2 switches on (turned on before anything happened).</summary>
    private static async Task<(TestHost Host, F1FakeProviders World, F1Session Race, string Ref)> RunningRaceAsync(F1NotificationChanges changes)
    {
        var (host, world, race) = await RaceWeekendAsync();
        await EnableAsync(host, changes);
        await StepAsync(host, TimeSpan.Zero);
        var reference = F1FakeProviders.Ref(race);
        world.AddEvent(reference, F1LifecycleSignal.Started, T0.AddMinutes(31));
        await StepAsync(host, TimeSpan.FromMinutes(32));
        host.Transport.Messages.Should().ContainSingle("the race start card");
        return (host, world, race, reference);
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public async Task New_switches_default_off_and_existing_behaviour_sends_nothing_new()
    {
        var (host, world, race) = await RaceWeekendAsync();
        await using var _ = host;
        var config = await host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<Formula1GuildConfigEntity>().AsNoTracking().SingleAsync());
        new[] { config.NotifyWeekendSchedule, config.NotifyRaceReminder, config.NotifySafetyCar, config.NotifyRedFlag, config.NotifyDisqualification }
            .Should().OnlyContain(on => !on);

        await StepAsync(host, TimeSpan.Zero);
        var reference = F1FakeProviders.Ref(race);
        world.AddEvent(reference, F1LifecycleSignal.Started, T0.AddMinutes(31));
        world.AddIncident(reference, F1IncidentKind.SafetyCarDeployed, T0.AddMinutes(33), lap: 2);
        world.AddIncident(reference, F1IncidentKind.RedFlag, T0.AddMinutes(34), lap: 3);
        for (var i = 0; i < 4; i++)
            await StepAsync(host, TimeSpan.FromMinutes(i == 0 ? 15 : 10));

        host.Transport.Messages.Should().ContainSingle("only the existing race start card");
        (await OutboxAsync(host, "")).Select(o => o.Kind).Should().Equal("started:race");
    }

    // ---------------------------------------------------------------- Safety Car

    [Fact]
    public async Task Safety_car_notifies_once_per_real_phase_never_for_repeats_or_replays()
    {
        var (host, world, _, reference) = await RunningRaceAsync(new(SafetyCar: true));
        await using var _ = host;

        world.AddIncident(reference, F1IncidentKind.SafetyCarDeployed, T0.AddMinutes(40), lap: 5);
        await StepAsync(host, TimeSpan.FromMinutes(9));
        (await OutboxAsync(host, "safety_car:")).Should().ContainSingle();
        var card = host.Transport.Messages[1];
        card.Pinged.Should().BeFalse("incident cards never ping");
        card.Message.Embed!.Description.Should().Contain("SAFETY CAR").And.Contain("5");
        card.Message.Embed.ThumbnailUrl.Should().EndWith("safety-car.png");

        // The same provider message again (replay) and a repeated "deployed" inside the open phase: nothing new.
        world.AddIncident(reference, F1IncidentKind.SafetyCarDeployed, T0.AddMinutes(40), lap: 5);
        world.AddIncident(reference, F1IncidentKind.SafetyCarDeployed, T0.AddMinutes(42), lap: 6);
        await host.Services.GetRequiredService<Formula1LiveListener>()
            .OnIncidentAsync(new F1RaceControlIncident(F1FakeProviders.LifecycleId, reference, F1IncidentKind.SafetyCarDeployed, T0.AddMinutes(40), 5, null, null, null), CancellationToken.None);
        for (var i = 0; i < 3; i++)
            await StepAsync(host, TimeSpan.FromMinutes(1));
        (await OutboxAsync(host, "safety_car:")).Should().ContainSingle();
        (await IncidentRowsAsync(host)).Count(r => r.Kind == (int)F1IncidentKind.SafetyCarDeployed).Should().Be(2, "stored once per provider message");

        // "In this lap", then a new deployment: a second, genuinely separate phase → exactly one more card.
        world.AddIncident(reference, F1IncidentKind.SafetyCarEnding, T0.AddMinutes(46), lap: 8);
        world.AddIncident(reference, F1IncidentKind.SafetyCarDeployed, T0.AddMinutes(50), lap: 10);
        await StepAsync(host, TimeSpan.FromMinutes(5));
        await StepAsync(host, TimeSpan.FromMinutes(1));
        (await OutboxAsync(host, "safety_car:")).Should().HaveCount(2);
        host.Transport.Messages.Should().HaveCount(2, "the new phase is staged but spoiler-held for 30 s after the provider's event time");
        await StepAsync(host, TimeSpan.FromSeconds(30));
        host.Transport.Messages.Should().HaveCount(3);
    }

    [Fact]
    public async Task Safety_car_seen_late_is_not_announced()
    {
        var (host, world, _, reference) = await RunningRaceAsync(new(SafetyCar: true));
        await using var _ = host;

        // Provider message time 20 minutes ago (e.g. the bot was offline): older than the freshness window.
        world.AddIncident(reference, F1IncidentKind.SafetyCarDeployed, host.Clock.GetUtcNow().AddMinutes(-20), lap: 4);
        await StepAsync(host, TimeSpan.FromMinutes(2));

        (await OutboxAsync(host, "safety_car:")).Should().BeEmpty();
        host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Safety_car_after_restart_is_not_announced_again()
    {
        var (host, world, _, reference) = await RunningRaceAsync(new(SafetyCar: true));
        await using var _ = host;
        world.AddIncident(reference, F1IncidentKind.SafetyCarDeployed, T0.AddMinutes(40), lap: 5);
        await StepAsync(host, TimeSpan.FromMinutes(9));
        host.Transport.Messages.Should().HaveCount(2);

        await using var second = await RestartAsync(host, world, host.Clock.GetUtcNow().AddMinutes(1));
        for (var i = 0; i < 3; i++)
            await StepAsync(second, TimeSpan.FromMinutes(1));

        host.Transport.Messages.Should().HaveCount(2, "the persisted phase and outbox row survive the restart");
        (await OutboxAsync(second, "safety_car:")).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- red flag

    [Fact]
    public async Task Red_flag_notifies_once_per_phase_and_a_restart_opens_the_next_phase()
    {
        var (host, world, _, reference) = await RunningRaceAsync(new(RedFlag: true));
        await using var _ = host;

        world.AddIncident(reference, F1IncidentKind.RedFlag, T0.AddMinutes(40), lap: 3);
        await StepAsync(host, TimeSpan.FromMinutes(9));
        var cards = await OutboxAsync(host, "red_flag:");
        cards.Should().ContainSingle();
        host.Transport.Messages[1].Message.Embed!.ThumbnailUrl.Should().EndWith("red-flag.png");

        // Repeated red-flag rows of the same stoppage (flag row + "RED FLAG - RACE SUSPENDED") → nothing new.
        world.AddIncident(reference, F1IncidentKind.RedFlag, T0.AddMinutes(40).AddSeconds(1), lap: 3);
        await StepAsync(host, TimeSpan.FromMinutes(1));
        (await OutboxAsync(host, "red_flag:")).Should().ContainSingle();

        // Restart ("SESSION STARTED"), later a second red flag → one more card.
        world.AddIncident(reference, F1IncidentKind.RedFlagCleared, T0.AddMinutes(44));
        world.AddIncident(reference, F1IncidentKind.RedFlag, T0.AddMinutes(48), lap: 9);
        await StepAsync(host, TimeSpan.FromMinutes(5));
        await StepAsync(host, TimeSpan.FromMinutes(1));
        (await OutboxAsync(host, "red_flag:")).Should().HaveCount(2);
        await StepAsync(host, TimeSpan.FromSeconds(30)); // spoiler hold of the second phase
        host.Transport.Messages.Should().HaveCount(3);
    }

    [Fact]
    public async Task Red_flag_after_restart_is_not_announced_again()
    {
        var (host, world, _, reference) = await RunningRaceAsync(new(RedFlag: true));
        await using var _ = host;
        world.AddIncident(reference, F1IncidentKind.RedFlag, T0.AddMinutes(40), lap: 3);
        await StepAsync(host, TimeSpan.FromMinutes(9));

        await using var second = await RestartAsync(host, world, host.Clock.GetUtcNow().AddMinutes(1));
        for (var i = 0; i < 3; i++)
            await StepAsync(second, TimeSpan.FromMinutes(1));

        (await OutboxAsync(second, "red_flag:")).Should().ContainSingle();
        host.Transport.Messages.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------- disqualification

    [Fact]
    public async Task Disqualification_from_the_classification_notifies_once_per_car()
    {
        var (host, world, race, reference) = await RunningRaceAsync(new(Disqualification: true));
        await using var _ = host;
        var result = F1FakeProviders.Result(race);
        world.ResultsByRef[reference] = WithDisqualified(result, 3);
        world.AddEvent(reference, F1LifecycleSignal.Finished, T0.AddMinutes(90));
        await StepAsync(host, TimeSpan.FromMinutes(60));
        await StepAsync(host, TimeSpan.FromMinutes(1));

        var dsq = await OutboxAsync(host, "disqualification:");
        dsq.Should().ContainSingle().Which.Kind.Should().Be("disqualification:3");
        var card = host.Transport.Messages.Single(m => m.Message.Embed!.Description!.Contains("DİSKALİFİYE", StringComparison.Ordinal));
        card.Message.Embed!.Description.Should().Contain("#3").And.Contain("Driver 3");
        card.Pinged.Should().BeFalse();

        // Same classification again (correction checks) → nothing new.
        await StepAsync(host, TimeSpan.FromMinutes(25));
        (await OutboxAsync(host, "disqualification:")).Should().ContainSingle();

        // A post-race decision disqualifies a second car (classification correction) → exactly one more card.
        world.ResultsByRef[reference] = WithDisqualified(result, 3, 5);
        await StepAsync(host, TimeSpan.FromMinutes(25));
        await StepAsync(host, TimeSpan.FromMinutes(1));
        (await OutboxAsync(host, "disqualification:")).Select(o => o.Kind).Should().BeEquivalentTo(["disqualification:3", "disqualification:5"]);
    }

    /// <summary>A valid classification with the given cars disqualified (no position; the others close up, as the provider publishes it).</summary>
    private static F1SessionResult WithDisqualified(F1SessionResult result, params int[] cars)
    {
        var position = 0;
        var entries = result.Entries.OrderBy(e => e.Position).Select(e => cars.Contains(e.DriverNumber)
            ? e with { Position = null, Status = F1ResultStatus.Dsq, GapSeconds = null }
            : e with { Position = ++position }).ToList();
        return result with { Entries = entries };
    }

    // ---------------------------------------------------------------- race reminder

    [Fact]
    public async Task Race_reminder_fires_once_fifteen_minutes_before_the_race()
    {
        var (host, _, _) = await RaceWeekendAsync(); // race at T0 + 30 min
        await using var h = host;
        await EnableAsync(host, new(RaceReminder: true));

        await StepAsync(host, TimeSpan.FromMinutes(14));
        (await OutboxAsync(host, "race_reminder")).Should().BeEmpty("not before start - 15 min");
        await StepAsync(host, TimeSpan.FromMinutes(1));
        (await OutboxAsync(host, "race_reminder")).Should().ContainSingle();
        for (var i = 0; i < 5; i++)
            await StepAsync(host, TimeSpan.FromMinutes(1));

        host.Transport.Messages.Should().ContainSingle();
        var reminder = host.Transport.Messages[0];
        reminder.Pinged.Should().BeFalse();
        reminder.Message.Embed!.Description.Should().Contain("15 dakika sonra başlıyor").And.Contain("<t:");
    }

    [Theory]
    [InlineData(F1SessionType.Sprint)]
    [InlineData(F1SessionType.Qualifying)]
    [InlineData(F1SessionType.SprintQualifying)]
    [InlineData(F1SessionType.Practice1)]
    public async Task Race_reminder_is_race_only(F1SessionType type)
    {
        var (host, _, _) = await RaceWeekendAsync(type: type);
        await using var _ = host;
        await EnableAsync(host, new(RaceReminder: true));

        for (var i = 0; i < 6; i++)
            await StepAsync(host, TimeSpan.FromMinutes(3));

        (await OutboxAsync(host, "race_reminder")).Should().BeEmpty();
        host.Transport.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Race_reminder_is_never_sent_late()
    {
        // Enabled early, but the bot is offline until 11 minutes before the race.
        var (host, _, _) = await RaceWeekendAsync();
        await using var _ = host;
        await EnableAsync(host, new(RaceReminder: true));
        host.Clock.Advance(TimeSpan.FromMinutes(19));
        for (var i = 0; i < 5; i++)
            await StepAsync(host, TimeSpan.FromMinutes(1));

        (await OutboxAsync(host, "race_reminder")).Should().BeEmpty();
        host.Transport.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Race_reminder_is_not_repeated_after_a_restart()
    {
        var (host, world, _) = await RaceWeekendAsync();
        await using var _ = host;
        await EnableAsync(host, new(RaceReminder: true));
        await StepAsync(host, TimeSpan.FromMinutes(15));
        host.Transport.Messages.Should().ContainSingle();

        await using var second = await RestartAsync(host, world, host.Clock.GetUtcNow().AddSeconds(30));
        await StepAsync(second, TimeSpan.Zero);
        await StepAsync(second, TimeSpan.FromMinutes(1));

        host.Transport.Messages.Should().ContainSingle();
        (await OutboxAsync(second, "race_reminder")).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- Thursday weekend schedule

    private static readonly DateTimeOffset ThursdayMorning = new(2026, 9, 24, 5, 0, 0, TimeSpan.Zero);

    private static async Task<(TestHost Host, F1FakeProviders World)> WeekendAsync(bool sprint, DateTimeOffset start)
    {
        var (host, world) = await F1TestHostExtensions.CreateF1HostAsync(Live, start: start);
        var friday = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        if (sprint)
        {
            world.AddMeeting(2026, 19, (F1SessionType.Practice1, friday.AddHours(10.5)), (F1SessionType.SprintQualifying, friday.AddHours(14.5)),
                (F1SessionType.Sprint, friday.AddDays(1).AddHours(10)), (F1SessionType.Qualifying, friday.AddDays(1).AddHours(14)), (F1SessionType.Race, friday.AddDays(2).AddHours(13)));
        }
        else
        {
            world.AddMeeting(2026, 19, (F1SessionType.Practice1, friday.AddHours(11.5)), (F1SessionType.Practice2, friday.AddHours(15)),
                (F1SessionType.Practice3, friday.AddDays(1).AddHours(10.5)), (F1SessionType.Qualifying, friday.AddDays(1).AddHours(14)), (F1SessionType.Race, friday.AddDays(2).AddHours(13)));
        }

        await host.SetUpF1GuildAsync(Guild, Channel);
        await EnableAsync(host, new(WeekendSchedule: true));
        return (host, world);
    }

    [Fact]
    public async Task Thursday_schedule_standard_weekend_sends_once_with_all_sessions_in_order()
    {
        var (host, world) = await WeekendAsync(sprint: false, ThursdayMorning);
        await using var _ = host;

        await StepAsync(host, TimeSpan.Zero);
        host.Transport.Messages.Should().BeEmpty("the Thursday window opens at 06:00 UTC");
        await StepAsync(host, TimeSpan.FromHours(1));
        for (var i = 0; i < 5; i++)
            await StepAsync(host, TimeSpan.FromHours(1));

        var card = host.Transport.Messages.Should().ContainSingle().Subject;
        (await OutboxAsync(host, "weekend_schedule")).Should().ContainSingle();
        var text = card.Message.Embed!.Description!;
        text.Should().Contain("Standart hafta sonu");
        new[] { "1. Antrenman", "2. Antrenman", "3. Antrenman", "Sıralama", "Yarış" }
            .Select(s => text.IndexOf("**" + s + "**", StringComparison.Ordinal)).Should().BeInAscendingOrder().And.NotContain(-1);
        text.Split("<t:").Length.Should().Be(11, "absolute and relative Discord timestamps for all five sessions");
        card.Message.Embed.ThumbnailUrl.Should().EndWith("weekend-schedule.png");
        card.Pinged.Should().BeFalse();

        await using var second = await RestartAsync(host, world, host.Clock.GetUtcNow().AddMinutes(5));
        await StepAsync(second, TimeSpan.FromMinutes(1));
        host.Transport.Messages.Should().ContainSingle("no second schedule card after a restart");
    }

    [Fact]
    public async Task Thursday_schedule_sprint_weekend_lists_sprint_sessions()
    {
        var (host, _) = await WeekendAsync(sprint: true, ThursdayMorning);
        await using var _ = host;

        await StepAsync(host, TimeSpan.FromHours(2));

        var text = host.Transport.Messages.Should().ContainSingle().Subject.Message.Embed!.Description!;
        text.Should().Contain("Sprint hafta sonu").And.Contain("**Sprint Sıralaması**").And.Contain("**Sprint**");
    }

    [Fact]
    public async Task Thursday_schedule_is_never_sent_late_or_for_an_old_weekend()
    {
        // Bot comes up on Friday morning: the Thursday window is over — nothing, now or later.
        var (host, _) = await WeekendAsync(sprint: false, new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
        await using var _ = host;
        for (var i = 0; i < 4; i++)
            await StepAsync(host, TimeSpan.FromHours(1));

        host.Transport.Messages.Should().BeEmpty();
        (await OutboxAsync(host, "weekend_schedule")).Should().BeEmpty();
    }

    [Fact]
    public async Task Thursday_schedule_turned_on_after_the_window_opened_waits_for_next_weekend()
    {
        var (host, _) = await WeekendAsync(sprint: false, ThursdayMorning.AddHours(4)); // enabled at 09:00, window opened 06:00
        await using var _ = host;
        for (var i = 0; i < 3; i++)
            await StepAsync(host, TimeSpan.FromHours(1));

        host.Transport.Messages.Should().BeEmpty("the watermark is later than the window start");
    }
}
