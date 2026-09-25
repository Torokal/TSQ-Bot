using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Bot;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Single-guild live operation (owner decision 2026-09-25): with Discord:AllowedGuildIds the bot serves ONLY those guilds.
/// Guarded server-side at every layer — interaction entry (<see cref="DeploymentPolicy.IsGuildAllowed(ulong?)"/>, checked
/// before any command, button, modal or autocomplete runs), notification planning and last-moment delivery — so a guild
/// outside the list can neither use the bot nor receive anything, even if commands were somehow registered there.
/// </summary>
public sealed class SingleGuildTests
{
    private const ulong Main = 689812743242514448;
    private const ulong OldTest = 618763184815472651;

    [Fact]
    public void The_allow_list_admits_only_the_main_guild_and_refuses_dms()
    {
        var policy = new DeploymentPolicy(true, new HashSet<ulong>(), new HashSet<ulong> { Main }, "Railway");
        policy.SingleGuild.Should().BeTrue();
        policy.IsGuildAllowed(Main).Should().BeTrue();
        policy.IsGuildAllowed(OldTest).Should().BeFalse("the old test guild is no longer served");
        policy.IsGuildAllowed((ulong?)null).Should().BeFalse("a DM or a hand-built interaction without a guild is refused");
        policy.IsGuildAllowed(new GuildId(OldTest)).Should().BeFalse();

        var local = new DeploymentPolicy(false, new HashSet<ulong>());
        local.GuildRestricted.Should().BeFalse();
        local.IsGuildAllowed(OldTest).Should().BeTrue("no allow-list = local development, unrestricted");
    }

    [Theory]
    [InlineData("Discord:TestGuildIds:0", "618763184815472651", "outside Discord:AllowedGuildIds")]
    [InlineData("Discord:CommandSyncGuildIds:0", "618763184815472651", "outside Discord:AllowedGuildIds")]
    [InlineData("Discord:AllowGlobalCommandSync", "true", "AllowGlobalCommandSync must be false")]
    public void A_restricted_bot_refuses_config_that_reaches_beyond_the_allow_list(string key, string value, string expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Discord:AllowedGuildIds:0"] = Main.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Discord:CommandSyncGuildIds:0"] = Main.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        ToroHost.ValidateConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .Should().NotContain(p => p.Contains("AllowedGuildIds", StringComparison.Ordinal), "main guild only is consistent");

        settings[key] = value;
        ToroHost.ValidateConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .Should().Contain(p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_delivered_to_a_guild_outside_the_allow_list()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Discord:AllowedGuildIds:0"] = "111" });
        await host.SetUpEsportsGuildAsync(new GuildId(111), new ChannelId(1110));
        await host.SetUpEsportsGuildAsync(new GuildId(222), new ChannelId(2220));

        async Task StageAsync(ulong guild, ulong channel) => await host.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<INotificationOutbox>().StageAsync(new NotificationRequest(new GuildId(guild), EsportsModule.ModuleIdTyped,
                "liquipedia:counterstrike:M1", new ChannelId(channel), NotificationPlanner.KindResult,
                new OutgoingMessage(null, new MessageEmbed("A vs B", "body", null, [], "footer", null, null), MentionPolicy.None),
                TestHost.T0.AddHours(6), IsDryRun: false), CancellationToken.None);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        });
        await StageAsync(111, 1110);
        await StageAsync(222, 2220); // e.g. staged before the allow-list changed

        await host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

        var rows = await host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().ToListAsync());
        rows.Single(r => r.GuildId == 111).Status.Should().Be(OutboxStatus.Sent);
        var other = rows.Single(r => r.GuildId == 222);
        other.Status.Should().Be(OutboxStatus.Cancelled);
        other.LastError.Should().Be("guild_not_allowed");
        host.Transport.Messages.Should().ContainSingle().Which.Channel.Should().Be(new ChannelId(1110));
    }

    [Fact]
    public async Task The_planner_only_considers_the_allowed_guild()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Discord:AllowedGuildIds:0"] = "111" });
        await host.SetUpEsportsGuildAsync(new GuildId(111), new ChannelId(1110));
        await host.SetUpEsportsGuildAsync(new GuildId(222), new ChannelId(2220));
        var match = ToroSquad.Tests.Unit.FilterAndRankingTests.Match("SG1", new TeamRef("liquipedia", "counterstrike/Alpha", "Alpha", "ALP"),
            new TeamRef("liquipedia", "counterstrike/Bravo", "Bravo", "BRV")) with
        { ScheduledStartUtc = TestHost.T0.AddMinutes(10) };

        var report = await host.InScopeAsync(sp => sp.GetRequiredService<NotificationPlanner>().PlanAsync([match], host.Clock.GetUtcNow(), false, CancellationToken.None));

        report.GuildsConsidered.Should().Be(1, "guild 222 is configured but not allowed");
        var rows = await host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().ToListAsync());
        rows.Should().NotContain(r => r.GuildId == 222);
    }
}
