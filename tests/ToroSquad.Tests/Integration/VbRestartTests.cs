using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.VbLifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Railway restarts: a new process (new DI container, poller, caches, provider version cache) on the same SQLite database
/// and the same Discord channel. Nothing is announced twice and nothing missed while down is replayed.
/// </summary>
public sealed class VbRestartTests
{
    /// <summary>"Restart": a second host on the first host's database, sharing its Discord (fake transport) and real world.</summary>
    private static async Task<(TestHost Host, VbFakeProvider World)> RestartAsync(TestHost first, VbFakeProvider world, DateTimeOffset at)
    {
        var transport = first.Transport;
        var (second, secondWorld) = await VbTestHostExtensions.CreateVbHostAsync(
            new() { ["Bot:DataDirectory"] = first.Directory }, start: at, copyFrom: world,
            extra: s =>
            {
                s.AddSingleton(transport);
                s.AddSingleton<IMessageTransport>(transport);
            });
        await second.Services.GetRequiredService<VolleyballPoller>().WarmUpAsync(CancellationToken.None);
        return (second, secondWorld);
    }

    private static async Task<(TestHost Host, VbFakeProvider World, VolleyballMatch Match)> AtTwoOneAsync()
    {
        var (host, world, match) = await MatchDayAsync(pingRole: false);
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(62));
        world.Set(match with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 });
        await RunAsync(host, TimeSpan.FromMinutes(2));
        foreach (var sets in new[] { new[] { (25, 21) }, [(25, 21), (22, 25)], [(25, 21), (22, 25), (25, 19)] })
        {
            world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, sets));
            await RunAsync(host, TimeSpan.FromMinutes(5));
        }

        host.Transport.Messages.Should().HaveCount(5, "reminder + started + 3 sets");
        return (host, world, match);
    }

    [Fact]
    public async Task Restart_after_set_3_replays_nothing_and_later_transitions_still_arrive_once()
    {
        var (first, world, match) = await AtTwoOneAsync();
        await using var _ = first;

        await using var second = (await RestartAsync(first, world, first.Clock.GetUtcNow().AddMinutes(2))).Host;
        var secondWorld = second.Services.GetRequiredService<VbFakeProvider>();
        await RunAsync(second, TimeSpan.FromMinutes(5));
        first.Transport.Messages.Should().HaveCount(5, "no reminder/started/set 1-3 replay after the restart");

        secondWorld.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25), (25, 19), (20, 25)));
        await RunAsync(second, TimeSpan.FromMinutes(3));
        first.Transport.Messages.Should().HaveCount(6);
        first.Transport.Messages[^1].Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 2-2 İtalya 🇮🇹");
        first.Transport.EditCalls.Should().Be(0);
        (await OutboxAsync(second, "set:")).Select(o => o.Kind).Should().BeEquivalentTo("set:1", "set:2", "set:3", "set:4");
    }

    [Fact]
    public async Task Outage_during_sets_2_and_3_sends_no_catch_up_cards_but_the_final_still_arrives()
    {
        var (first, world, match) = await MatchDayAsync(pingRole: false);
        await using var _ = first;
        await StepAsync(first, TimeSpan.Zero);
        await RunAsync(first, TimeSpan.FromMinutes(62));
        world.Set(VbFakeProvider.WithSets(match with { Status = VolleyballMatchStatus.Live }, VolleyballMatchStatus.Live, (25, 21)));
        await RunAsync(first, TimeSpan.FromMinutes(3));
        var before = first.Transport.Messages.Count;

        // Bot down for 45 minutes; meanwhile sets 2 and 3 are played.
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25), (25, 19)));
        var (second, secondWorld) = await RestartAsync(first, world, first.Clock.GetUtcNow().AddMinutes(45));
        await using var __ = second;
        await RunAsync(second, TimeSpan.FromMinutes(5));
        first.Transport.Messages.Should().HaveCount(before, "sets 2 and 3 happened while the bot was down: no catch-up spam");

        secondWorld.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (22, 25), (25, 19), (25, 17)));
        await RunAsync(second, TimeSpan.FromMinutes(3));
        first.Transport.Messages.Should().HaveCount(before + 1);
        first.Transport.Messages[^1].Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 3-1 İtalya 🇮🇹");
    }

    [Fact]
    public async Task Restart_after_the_final_sends_nothing()
    {
        var (first, world, match) = await AtTwoOneAsync();
        await using var _ = first;
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (22, 25), (25, 19), (25, 23)));
        await RunAsync(first, TimeSpan.FromMinutes(3));
        var sent = first.Transport.Messages.Count;

        var (second, _) = await RestartAsync(first, world, first.Clock.GetUtcNow().AddMinutes(1));
        await using var __ = second;
        await RunAsync(second, TimeSpan.FromMinutes(120));
        first.Transport.Messages.Should().HaveCount(sent);
        first.Transport.EditCalls.Should().Be(0, "an identical final is never edited");
        (await OutboxAsync(second, "final")).Should().ContainSingle();
    }

    [Fact]
    public async Task Warm_up_restores_the_command_cache_with_the_original_freshness()
    {
        var (first, world, _) = await MatchDayAsync();
        await using var _ = first;
        await StepAsync(first, TimeSpan.Zero);
        var fetched = first.Clock.GetUtcNow();

        var (second, _) = await RestartAsync(first, world, fetched.AddHours(1));
        await using var __ = second;
        var cache = second.Services.GetRequiredService<VolleyballCache>();
        cache.Fixtures.FetchedAt.Should().Be(fetched, "restored data keeps its real fetch time");
        cache.Matches.Should().ContainSingle(m => m.MatchKey == "fakevb:m1");
    }
}
