using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Commands.Core;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Volleyball.Application;

namespace ToroSquad.Modules.Volleyball.Commands;

/// <summary>Volleyball step inside the core /setup wizard: channel → ping-free TEST/DEMO preview → explicit enable.</summary>
public sealed class VolleyballSetupFlow(ILocalizer localizer, VolleyballConfigService config, IModuleGate gate) : IModuleSetupFlow
{
    public const string ChannelId = "tsq:vb:setup:channel:";
    public const string PreviewId = "tsq:vb:setup:preview:";
    public const string EnableId = "tsq:vb:setup:enable:";

    public ModuleId Module => VolleyballModule.ModuleIdTyped;

    public async Task<(MessageEmbed Embed, MessageComponent Components)> RenderAsync(ActorContext actor, string language, CancellationToken cancellationToken)
    {
        var c = await config.GetAsync(actor.GuildId, cancellationToken);
        var enabled = await gate.IsEnabledAsync(actor.GuildId, Module, cancellationToken);
        var owner = actor.UserId.ToString();
        var description = localizer.Get(language, "vb.setup.intro",
            c?.ChannelId is { } ch ? $"<#{ch.ToString(CultureInfo.InvariantCulture)}>" : localizer.Get(language, "vb.setup.no_channel"),
            localizer.Get(language, enabled ? "modules.state_on" : "modules.state_off"));
        var components = new ComponentBuilder()
            .WithSelectMenu(new SelectMenuBuilder()
                .WithType(ComponentType.ChannelSelect)
                .WithCustomId(ChannelId + owner)
                .WithChannelTypes(ChannelType.Text, ChannelType.News)
                .WithPlaceholder(localizer.Get(language, "vb.setup.channel_placeholder"))
                .WithMinValues(1).WithMaxValues(1), row: 0)
            .WithButton(localizer.Get(language, "vb.setup.preview"), PreviewId + owner, ButtonStyle.Secondary, row: 1)
            .WithButton(localizer.Get(language, "vb.setup.enable"), EnableId + owner, ButtonStyle.Success, disabled: enabled || c?.ChannelId is null, row: 1)
            .Build();
        return (new MessageEmbed(localizer.Get(language, "vb.setup.title"), description, null, [], localizer.Get(language, "vb.setup.more"), null, ToroInteractionModule.BrandColor), components);
    }
}

/// <summary>
/// Setup components. They work BEFORE the module is enabled (AllowWhenDisabled) — still guild-only, limited to the admin
/// who opened the wizard, and re-authorized (Manage Server) on every click.
/// </summary>
[ToroModule(VolleyballModule.ModuleIdValue, AllowWhenDisabled = true)]
public sealed class VolleyballSetupComponents(
    InteractionServices services,
    VolleyballConfigService config,
    VolleyballPreviewService preview,
    ModuleManagementService modules,
    IModuleGate gate) : ToroInteractionModule(services)
{
    [ComponentInteraction(VolleyballSetupFlow.ChannelId + "*", ignoreGroupNames: true)]
    public async Task ChannelAsync(string owner, string[] values)
    {
        if (!await OwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        if (values.Length != 1 || !ulong.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var channel))
        {
            await ReplyTextAsync("error.bad_input");
            return;
        }

        var result = await config.SetChannelAsync(Actor, channel, CancellationToken.None);
        if (result.Succeeded)
            await RefreshWizardAsync();
        await ReplyResultAsync(result);
    }

    [ComponentInteraction(VolleyballSetupFlow.PreviewId + "*", ignoreGroupNames: true)]
    public async Task PreviewAsync(string owner)
    {
        if (!await OwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        var result = await preview.BuildAsync(Actor, await LangAsync(), VbPreviewKind.FinalWon, CancellationToken.None);
        if (result.Message is null)
        {
            await ReplyResultAsync(result.Auth);
            return;
        }

        await SendEphemeralAsync(await T("vb.preview.note"), DiscordConversions.ToEmbed(result.Message.Embed), null);
    }

    [ComponentInteraction(VolleyballSetupFlow.EnableId + "*", ignoreGroupNames: true)]
    public async Task EnableAsync(string owner)
    {
        if (!await OwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        if ((await config.GetAsync(Actor.GuildId, CancellationToken.None))?.ChannelId is null)
        {
            await ReplyTextAsync("vb.setup.channel_required");
            return;
        }

        var result = await modules.SetEnabledAsync(Actor, VolleyballModule.ModuleIdValue, true, CancellationToken.None);
        if (result.Succeeded)
            await RefreshWizardAsync();
        await ReplyResultAsync(result);
    }

    private async Task RefreshWizardAsync()
    {
        if (Context.Interaction is not IComponentInteraction component)
            return;
        var (embed, components) = await new VolleyballSetupFlow(Localizer, config, gate).RenderAsync(Actor, await LangAsync(), CancellationToken.None);
        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = DiscordConversions.ToEmbed(embed);
            m.Components = components;
        });
    }

    private async Task<bool> OwnerAsync(string owner)
    {
        if (owner != Actor.UserId.ToString())
        {
            await ReplyTextAsync("error.not_owner");
            return false;
        }

        var auth = Authorize.Require(Actor, Actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
        {
            await ReplyResultAsync(OperationResult.Forbidden(auth));
            return false;
        }

        return true;
    }
}
