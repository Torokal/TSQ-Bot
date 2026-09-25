using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Fixture (TEST/DEMO) timelines must survive a bot restart. Found live 2026-09-25: the anchor was the process start, so
/// every restart moved the demo match times and the planner correctly saw a "new time" → one new TEST/DEMO
/// "Maçın saati değişti" card per restart in the test guild.
/// </summary>
public sealed class FixtureRestartTests
{
    private static readonly GuildId Guild = new(900_000_000_000_000_777);
    private static readonly ChannelId Channel = new(777);

    private static Task<int> RescheduledRowsAsync(TestHost host) => host.InScopeAsync(sp =>
        sp.GetRequiredService<ToroDbContext>().Outbox.CountAsync(o => o.Kind.StartsWith("rescheduled-")));

    [Fact]
    public async Task A_restart_keeps_the_fixture_timeline_and_posts_no_new_lifecycle_card()
    {
        await using var first = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" });
        await first.SetUpEsportsGuildAsync(Guild, Channel);
        await first.Services.GetRequiredService<EsportsPoller>().RefreshMatchesAsync(CancellationToken.None);
        var before = first.Services.GetRequiredService<EsportsCache>().Matches.Data!.ToDictionary(m => m.Key, m => m.ScheduledStartUtc);
        var baseline = await RescheduledRowsAsync(first);

        // "Restart" 40 minutes later: a new process (new DI container, new anchor object) on the same database.
        await using (var second = await TestHost.CreateAsync(
            new() { ["Esports:Provider:Name"] = "PandaScore", ["Bot:DataDirectory"] = first.Directory }, start: TestHost.T0.AddMinutes(40)))
        {
            second.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild));
            second.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true,
                GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory));
            var poller = second.Services.GetRequiredService<EsportsPoller>();
            await poller.WarmUpAsync(CancellationToken.None);
            await poller.RefreshMatchesAsync(CancellationToken.None);

            second.Services.GetRequiredService<EsportsCache>().Matches.Data!.Should().OnlyContain(m => m.ScheduledStartUtc == before[m.Key],
                "the persisted anchor keeps every demo time where it was");
            (await RescheduledRowsAsync(second)).Should().Be(baseline, "a restart is not a reschedule");
        }
    }

    [Fact]
    public async Task The_anchor_is_reused_within_a_day_and_renewed_after()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TestHost.T0);
        var store = new MemoryStore();
        var first = await new FixtureAnchor(clock, store).GetAsync(CancellationToken.None);
        first.Should().Be(TestHost.T0);

        clock.Advance(TimeSpan.FromHours(5));
        (await new FixtureAnchor(clock, store).GetAsync(CancellationToken.None)).Should().Be(first, "restart within a day");

        clock.Advance(TimeSpan.FromHours(20));
        (await new FixtureAnchor(clock, store).GetAsync(CancellationToken.None)).Should().Be(clock.GetUtcNow(),
            "after a day the demo timeline is renewed (otherwise every demo match would be in the past)");

        store.Value = clock.GetUtcNow().AddHours(1);
        (await new FixtureAnchor(clock, store).GetAsync(CancellationToken.None)).Should().Be(clock.GetUtcNow(), "an anchor in the future is not trusted");

        var anchor = new FixtureAnchor(clock, store);
        var once = await anchor.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(10));
        (await anchor.GetAsync(CancellationToken.None)).Should().Be(once, "fixed for the process lifetime");
        (await new FixtureAnchor(clock).GetAsync(CancellationToken.None)).Should().Be(clock.GetUtcNow(), "without a store: process start");
    }

    private sealed class MemoryStore : IFixtureAnchorStore
    {
        public DateTimeOffset? Value { get; set; }

        public Task<DateTimeOffset?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);

        public Task SaveAsync(DateTimeOffset anchor, CancellationToken cancellationToken)
        {
            Value = anchor;
            return Task.CompletedTask;
        }
    }
}
