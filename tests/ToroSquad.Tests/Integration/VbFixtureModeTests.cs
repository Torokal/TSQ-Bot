using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.VbLifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Explicit fixture (TEST/DEMO) mode end to end through the REAL FIVB VIS client and parser (synthetic HTTP answers in VIS's
/// JSON shape): the whole card sequence appears once, labelled TEST/DEMO, with no real source name, and the U19 decoy of the
/// same country is rejected. This proves wiring, not live data.
/// </summary>
public sealed class VbFixtureModeTests
{
    [Fact]
    public async Task Demo_match_runs_reminder_start_sets_and_final_through_the_real_vis_parser()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Volleyball:Provider:Mode"] = "Fixture" });
        host.Services.GetRequiredService<IVolleyballDataProvider>().Should().BeOfType<ToroSquad.Modules.Volleyball.Providers.Fivb.FivbVisProvider>();
        await host.SetUpVbGuildAsync(Guild, Channel);

        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(130));

        var cards = host.Transport.Messages.Select(m => m.Message.Embed!).ToList();
        cards.Select(c => c.Title).Should().Equal(
            "[TEST/DEMO] 🇹🇷 Türkiye vs Testland",
            "[TEST/DEMO] 🇹🇷 Türkiye vs Testland",
            "[TEST/DEMO] 🇹🇷 Türkiye 1-0 Testland",
            "[TEST/DEMO] 🇹🇷 Türkiye 1-1 Testland",
            "[TEST/DEMO] 🇹🇷 Türkiye 2-1 Testland",
            "[TEST/DEMO] 🇹🇷 Türkiye 3-1 Testland");
        cards[0].Description.Should().Contain("⏳");
        cards[1].Description.Should().Contain("Maç başladı");
        cards[^1].Description.Should().Contain("Filenin Sultanları kazandı").And.Contain("4. Set: 25-23");
        cards.Should().OnlyContain(c => c.Footer == "TEST/DEMO · sentetik veri, gerçek bir maç değildir" && c.ThumbnailUrl == null);
        cards.Should().OnlyContain(c => !c.Description!.Contains("U19", StringComparison.Ordinal) && !c.Title!.Contains("U19", StringComparison.Ordinal));
        host.Services.GetRequiredService<VolleyballCache>().Matches.Should().ContainSingle("the U19 decoy never enters the state");
    }

    [Fact]
    public async Task Demo_data_never_reaches_a_real_guild_that_is_not_an_authorized_test_guild()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Volleyball:Provider:Mode"] = "Fixture" }, replace: s =>
            s.AddSingleton(new DeploymentPolicy(RealDiscordConnection: true, TestGuildIds: new HashSet<ulong>())));
        await host.SetUpVbGuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(60));
        host.Transport.Messages.Should().BeEmpty();
    }
}
