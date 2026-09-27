using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.VbLifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Regressions for the independent review findings and for the real FIVB VIS behaviour observed on 2026-09-26 (a live
/// match answered "in set 1, 15-14" and "scheduled, no score" alternately).
/// </summary>
public sealed class VbReviewRegressionTests
{
    private static async Task<(TestHost Host, VbFakeProvider World, VolleyballMatch Match)> LiveAsync()
    {
        var (host, world, match) = await MatchDayAsync(pingRole: false);
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(62));
        return (host, world, match);
    }

    private static VolleyballMatch Live(VolleyballMatch m) => m with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 };

    [Fact]
    public async Task Flapping_live_and_scheduled_answers_announce_started_once_after_confirmation()
    {
        var (host, world, match) = await LiveAsync();
        await using var _ = host;
        var before = host.Transport.Messages.Count;
        for (var i = 0; i < 6; i++)
        {
            world.Set(i % 2 == 0 ? Live(match) : match); // exactly what VIS returned for FIN–SLO
            await StepAsync(host, TimeSpan.FromMinutes(1));
        }

        host.Transport.Messages.Should().HaveCount(before, "a live answer contradicted by the next answer is never confirmed");
        world.Set(Live(match));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        host.Transport.Messages.Should().HaveCount(before + 1);
        host.Transport.Messages[^1].Message.Embed!.Description.Should().Contain("Maç başladı");
        await RunAsync(host, TimeSpan.FromMinutes(5));
        (await OutboxAsync(host, "started")).Should().ContainSingle();
    }

    [Fact]
    public async Task A_single_spurious_forward_jump_produces_no_set_card()
    {
        var (host, world, match) = await LiveAsync();
        await using var _ = host;
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        var before = host.Transport.Messages.Count;

        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (25, 20)));
        await StepAsync(host, TimeSpan.FromMinutes(1));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        host.Transport.Messages.Should().HaveCount(before, "one unconfirmed '2-0' answer is never announced");
    }

    [Fact]
    public async Task The_deciding_set_gets_no_card_of_its_own_only_the_final()
    {
        var (host, world, match) = await LiveAsync();
        await using var _ = host;
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25), (25, 19)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        var before = host.Transport.Messages.Count;

        // VIS passes through "set 4 finished" (in play, 3-1) before "finished".
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25), (25, 19), (25, 23)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        host.Transport.Messages.Should().HaveCount(before, "the deciding set is covered by the final card");
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (22, 25), (25, 19), (25, 23)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        host.Transport.Messages.Should().HaveCount(before + 1);
        host.Transport.Messages[^1].Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 3-1 İtalya 🇮🇹");
        (await OutboxAsync(host, "set:4")).Should().BeEmpty();
    }

    [Fact]
    public async Task Re_enabling_after_every_guild_had_the_module_off_does_not_replay_the_final()
    {
        var (host, world, match) = await MatchDayAsync(pingRole: false);
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        var admin = TestHost.Admin(Guild);
        await host.InScopeAsync(async sp => (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(admin, "volleyball", false, CancellationToken.None)).Succeeded.Should().BeTrue());
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (25, 20), (25, 19)));
        await RunAsync(host, TimeSpan.FromHours(3)); // module off everywhere: nothing is polled

        await host.InScopeAsync(async sp => (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(admin, "volleyball", true, CancellationToken.None)).Succeeded.Should().BeTrue());
        await RunAsync(host, TimeSpan.FromMinutes(90));
        host.Transport.Messages.Should().BeEmpty("the match ended while nobody had the module on: re-enabling never replays history");
    }

    [Fact]
    public async Task A_frozen_provider_feed_is_stale_and_creates_no_live_card()
    {
        var (host, world, match) = await LiveAsync();
        await using var _ = host;
        world.Set(Live(match) with { LastProviderUpdateUtc = host.Clock.GetUtcNow() });
        await RunAsync(host, TimeSpan.FromMinutes(3));
        var before = host.Transport.Messages.Count;

        // The provider's own update time stops moving for 30 minutes, then a set "arrives" late.
        var frozenAt = host.Clock.GetUtcNow();
        await RunAsync(host, TimeSpan.FromMinutes(30));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21)) with { LastProviderUpdateUtc = frozenAt });
        await RunAsync(host, TimeSpan.FromMinutes(3));
        host.Transport.Messages.Should().HaveCount(before, "stale data never creates a live notification");
    }

    [Fact]
    public async Task A_live_score_correction_is_accepted_after_repeated_confirmation_and_the_match_continues()
    {
        var (host, world, match) = await LiveAsync();
        await using var _ = host;
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (25, 22)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        var afterWrong = host.Transport.Messages.Count;

        // The scorer corrects set 2 (it went to Italy): the recorded 2-0 becomes 1-1 — repeated, consistent answers.
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25)));
        await RunAsync(host, TimeSpan.FromMinutes(5));
        host.Transport.Messages.Should().HaveCount(afterWrong, "a correction produces no card");
        var snapshot = await SnapshotAsync(host);
        (snapshot.HomeSets, snapshot.AwaySets).Should().Be((1, 1));

        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25), (25, 18)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        host.Transport.Messages[^1].Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 2-1 İtalya 🇮🇹", "the match is not frozen after a correction");
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (22, 25), (25, 18), (25, 20)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        host.Transport.Messages[^1].Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 3-1 İtalya 🇮🇹");
        (await OutboxAsync(host, "set:2")).Should().ContainSingle("an announced set number is never announced again");
    }

    [Fact]
    public async Task A_match_that_vanished_from_the_fixture_listing_gets_no_reminder()
    {
        var (host, world, match) = await MatchDayAsync(overrides: new() { ["Volleyball:FixtureRefreshNearMinutes"] = "15" });
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        world.World.Remove(match.ProviderMatchId); // deleted/renumbered by the provider
        await RunAsync(host, TimeSpan.FromMinutes(55));
        host.Transport.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_start_time_that_becomes_unconfirmed_is_cleared_and_no_reminder_fires_at_the_old_time()
    {
        var (host, world, match) = await MatchDayAsync(overrides: new() { ["Volleyball:FixtureRefreshNearMinutes"] = "15" });
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        world.Set(match with { StartTimeUtc = null });
        await RunAsync(host, TimeSpan.FromMinutes(55));
        host.Transport.Messages.Should().BeEmpty();
        (await SnapshotAsync(host)).StartTimeUtc.Should().BeNull();
    }

    [Fact]
    public async Task Fixture_data_never_surfaces_after_switching_to_live_data()
    {
        await using var demo = await TestHost.CreateAsync(new() { ["Volleyball:Provider:Mode"] = "Fixture" });
        await demo.SetUpVbGuildAsync(Guild, Channel);
        await StepAsync(demo, TimeSpan.Zero);
        await RunAsync(demo, TimeSpan.FromMinutes(125));
        demo.Transport.Messages.Should().NotBeEmpty();
        demo.Services.GetRequiredService<IVolleyballDataProvider>().Id.Should().Be("fivb-demo");

        // Same database, now in live mode (the real FIVB provider id is "fivb"; here a fake live provider).
        var (live, _) = await VbTestHostExtensions.CreateVbHostAsync(new() { ["Bot:DataDirectory"] = demo.Directory }, start: demo.Clock.GetUtcNow().AddMinutes(1));
        await using var __ = live;
        await live.Services.GetRequiredService<VolleyballPoller>().WarmUpAsync(CancellationToken.None);
        await RunAsync(live, TimeSpan.FromMinutes(30));
        live.Transport.Messages.Should().BeEmpty("demo rows (provider fivb-demo) are never planned, shown or polled in live mode");
        live.Services.GetRequiredService<VolleyballCache>().Matches.Should().BeEmpty();
    }

    [Fact]
    public void The_real_provider_id_differs_between_live_and_fixture_mode()
    {
        static string Id(VbProviderMode mode) => new ToroSquad.Modules.Volleyball.Providers.Fivb.FivbVisProvider(null!,
            Microsoft.Extensions.Options.Options.Create(new ToroSquad.Modules.Volleyball.Providers.Fivb.FivbVisOptions()), new VbDataMode(mode),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToroSquad.Modules.Volleyball.Providers.Fivb.FivbVisProvider>.Instance).Id;
        Id(VbProviderMode.Live).Should().Be("fivb");
        Id(VbProviderMode.Fixture).Should().Be("fivb-demo");
    }

    [Fact]
    public async Task A_suppressed_catch_up_final_is_never_revived_with_a_ping()
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync(new() { ["Volleyball:MaxCatchUpPerGuildPerRun"] = "0" });
        await using var _ = host;
        var match = VbFakeProvider.Scheduled("m1", Start);
        world.Set(match);
        await host.SetUpVbGuildAsync(Guild, Channel, Role);
        await host.InScopeAsync(async sp => await sp.GetRequiredService<VolleyballConfigService>().SetRoleAsync(TestHost.Admin(Guild), Role.Value, true, true, CancellationToken.None));
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(61));
        world.Set(VbFakeProvider.WithSets(Live(match), VolleyballMatchStatus.Live, (25, 20)));
        await RunAsync(host, TimeSpan.FromMinutes(3));

        // Outage, then the result: catch-up limit 0 → recorded as expired, never sent (not even later, not with a ping).
        host.Clock.Advance(TimeSpan.FromMinutes(40));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 20), (25, 20), (25, 20)));
        var sent = host.Transport.Messages.Count;
        await RunAsync(host, TimeSpan.FromMinutes(30));
        host.Transport.Messages.Should().HaveCount(sent);
        (await OutboxAsync(host, "final")).Should().ContainSingle().Which.Status.Should().NotBe(ToroSquad.Core.Notifications.OutboxStatus.Sent);
    }
}
