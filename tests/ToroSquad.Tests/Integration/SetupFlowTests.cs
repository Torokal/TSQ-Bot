using Discord;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Core;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Commands;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>The /setup esports step renders controls from the saved state (regression found in the first live test).</summary>
public sealed class SetupFlowTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(900_000_000_000_000_501);
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.CreateAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Enable_button_follows_the_saved_channel_and_module_state()
    {
        var admin = TestHost.Admin(Guild);
        _host.Guilds.SetSnapshot(ToroSquad.Discord.Guilds.FakeGuildGateway.DemoSnapshot(Guild));
        _host.Guilds.SetChannel(Guild, new ChannelId(77), new ToroSquad.Core.Roles.BotChannelAccess(true, true,
            ToroSquad.Core.Security.GuildPermission.ViewChannel | ToroSquad.Core.Security.GuildPermission.SendMessages | ToroSquad.Core.Security.GuildPermission.EmbedLinks));

        await _host.InScopeAsync(async sp =>
        {
            var flow = sp.GetServices<IModuleSetupFlow>().Single(f => f.Module.Value == "esports");

            EnableDisabled(await flow.RenderAsync(admin, "tr", CancellationToken.None)).Should().BeTrue("no channel yet");

            (await sp.GetRequiredService<EsportsConfigService>().ConfigureAsync(admin, 77, null, null, null, null, CancellationToken.None)).Succeeded.Should().BeTrue();
            var afterChannel = await flow.RenderAsync(admin, "tr", CancellationToken.None);
            EnableDisabled(afterChannel).Should().BeFalse("a channel is saved, so the wizard must offer Enable");
            afterChannel.Embed.Description.Should().Contain("<#77>");

            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(admin, "esports", true, CancellationToken.None)).Succeeded.Should().BeTrue();
            EnableDisabled(await flow.RenderAsync(admin, "tr", CancellationToken.None)).Should().BeTrue("already enabled");
        });
    }

    private static bool EnableDisabled((ToroSquad.Core.Messaging.MessageEmbed Embed, MessageComponent Components) view) =>
        view.Components.Components.OfType<ActionRowComponent>().SelectMany(row => row.Components).OfType<ButtonComponent>()
            .Single(b => b.CustomId.StartsWith(EsportsSetupFlow.EnableId, StringComparison.Ordinal)).IsDisabled;
}
