using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Railway restarts: a new process (new DI container, poller, listener, caches) on the same SQLite database and the same
/// Discord channel. Nothing is announced twice; corrections still edit the original message.
/// </summary>
public sealed class F1RestartTests
{
    /// <summary>"Restart": a second host on the first host's database, sharing its Discord (fake transport).</summary>
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

    [Fact]
    public async Task Start_delivered_then_restart_during_the_race_sends_no_second_start()
    {
        var (first, world, race) = await RaceWeekendAsync();
        await using var _ = first;
        await StepAsync(first, TimeSpan.Zero);
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        await StepAsync(first, TimeSpan.FromMinutes(32));
        first.Transport.Messages.Should().ContainSingle();

        // Restart 4 minutes later (still inside the start freshness window), the provider still reports the start.
        await using var second = await RestartAsync(first, world, first.Clock.GetUtcNow().AddMinutes(4));
        for (var i = 0; i < 3; i++)
            await StepAsync(second, TimeSpan.FromMinutes(1));
        first.Transport.Messages.Should().ContainSingle("no second \"race started\" after a restart");
        first.Transport.EditCalls.Should().Be(0);
        (await OutboxAsync(second, "started:")).Should().ContainSingle();
    }

    [Fact]
    public async Task Result_delivered_then_restart_and_the_same_result_downloaded_sends_nothing()
    {
        var (first, world, race) = await RaceWeekendAsync();
        await using var _ = first;
        await StepAsync(first, TimeSpan.Zero);
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        world.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(first, TimeSpan.FromMinutes(141));
        var sent = first.Transport.Messages.Count;
        sent.Should().Be(1, "result only (the start was not fresh)");

        await using var second = await RestartAsync(first, world, first.Clock.GetUtcNow().AddMinutes(10));
        for (var i = 0; i < 4; i++)
            await StepAsync(second, TimeSpan.FromMinutes(21)); // correction polling downloads the same classification again
        first.Transport.Messages.Should().HaveCount(sent);
        first.Transport.Messages.Single().Edits.Should().BeEmpty("identical content is never re-sent or edited");
        (await OutboxAsync(second, "result:")).Should().ContainSingle();
    }

    [Fact]
    public async Task Result_delivered_then_restart_then_a_penalty_edits_the_existing_message_without_a_ping()
    {
        var (first, world, race) = await RaceWeekendAsync(new(Live) { ["Formula1:StandingsSettleWindowMinutes"] = "5" });
        await using var _ = first;
        await PingOnResultsAsync(first);
        await StepAsync(first, TimeSpan.Zero);
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddMinutes(140));
        world.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race);
        await StepAsync(first, TimeSpan.FromMinutes(141));
        var message = first.Transport.Messages.Single();
        message.Pinged.Should().BeTrue("results ping is enabled for this guild");
        await StepAsync(first, TimeSpan.FromMinutes(10)); // standings window (5 min) closes → one ping-free edit
        var editsBefore = message.Edits.Count;

        await using var second = await RestartAsync(first, world, first.Clock.GetUtcNow().AddMinutes(5));
        world.ResultsByRef[F1FakeProviders.Ref(race)] = F1FakeProviders.Result(race, swapFirstTwo: 1); // steward penalty after the restart
        second.Services.GetRequiredService<F1FakeProviders>().ResultsByRef[F1FakeProviders.Ref(race)] = world.ResultsByRef[F1FakeProviders.Ref(race)];
        for (var i = 0; i < 3; i++)
            await StepAsync(second, TimeSpan.FromMinutes(21));

        first.Transport.Messages.Should().ContainSingle("a correction is an edit, never a second message");
        message.Edits.Should().HaveCount(editsBefore + 1);
        var corrected = message.Edits[^1];
        corrected.Mentions.Roles.Should().BeEmpty("a correction never pings again");
        corrected.Embed!.Description.Should().Contain("🥇 1. Driver 2");
        (await OutboxAsync(second, "result:")).Should().ContainSingle();

        static async Task PingOnResultsAsync(TestHost host) => await host.InScopeAsync(async sp => (await sp.GetRequiredService<Formula1ConfigService>()
            .SetRoleAsync(TestHost.Admin(Guild), Role.Value, pingOnStarts: true, pingOnResults: true, CancellationToken.None)).Succeeded.Should().BeTrue());
    }

    [Fact]
    public async Task Lifecycle_state_survives_a_restart_so_a_resume_after_restart_is_still_not_a_start()
    {
        var (first, world, race) = await RaceWeekendAsync();
        await using var _ = first;
        await StepAsync(first, TimeSpan.Zero);
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(31));
        world.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Suspended, T0.AddMinutes(33));
        await StepAsync(first, TimeSpan.FromMinutes(34));

        await using var second = await RestartAsync(first, world, first.Clock.GetUtcNow().AddMinutes(1));
        second.Services.GetRequiredService<F1FakeProviders>().AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddMinutes(36));
        await StepAsync(second, TimeSpan.FromMinutes(2));
        var snapshot = await SnapshotAsync(second, race.Key);
        snapshot.State.Should().Be((int)F1SessionState.Started);
        snapshot.ResumeCount.Should().Be(1);
        snapshot.StartedObservedAt.Should().Be(T0.AddMinutes(31));
        first.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Warm_up_restores_the_command_cache_with_original_freshness()
    {
        var (first, world, race) = await RaceWeekendAsync();
        await using var _ = first;
        await StepAsync(first, TimeSpan.Zero);
        var fetched = first.Clock.GetUtcNow();

        await using var second = await RestartAsync(first, world, fetched.AddHours(1));
        var cache = second.Services.GetRequiredService<Formula1Cache>();
        cache.Schedule.FetchedAt.Should().Be(fetched, "restored data keeps its real fetch time");
        cache.Sessions.Should().Contain(s => s.Session.Key == race.Key);
        cache.DriverStandings.Data.Should().NotBeNull();
    }
}
