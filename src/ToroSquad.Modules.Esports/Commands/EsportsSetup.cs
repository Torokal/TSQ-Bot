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
using ToroSquad.Modules.Esports.Application;

namespace ToroSquad.Modules.Esports.Commands;

/// <summary>Esports step inside the core /setup wizard: channel → ping-free preview → explicit enable.</summary>
public sealed class EsportsSetupFlow(ILocalizer localizer, EsportsConfigService config, IModuleGate gate) : IModuleSetupFlow
{
    public const string ChannelId = "tsq:esp:setup:channel:";
    public const string PreviewId = "tsq:esp:setup:preview:";
    public const string EnableId = "tsq:esp:setup:enable:";

    public ModuleId Module => EsportsModule.ModuleIdTyped;

    public async Task<(MessageEmbed Embed, MessageComponent Components)> RenderAsync(ActorContext actor, string language, CancellationToken cancellationToken)
    {
        var view = await config.GetAsync(actor.GuildId, cancellationToken);
        var enabled = await gate.IsEnabledAsync(actor.GuildId, Module, cancellationToken);
        var owner = actor.UserId.ToString();
        var description = localizer.Get(language, "esports.setup.intro",
            view.ChannelId is { } c ? $"<#{c.ToString(CultureInfo.InvariantCulture)}>" : localizer.Get(language, "esports.setup.no_channel"),
            localizer.Get(language, view.NotifyReminders ? "common.yes" : "common.no"),
            localizer.Get(language, view.NotifyResults ? "common.yes" : "common.no"),
            localizer.Get(language, enabled ? "modules.state_on" : "modules.state_off"));
        var components = new ComponentBuilder()
            .WithSelectMenu(new SelectMenuBuilder()
                .WithType(ComponentType.ChannelSelect)
                .WithCustomId(ChannelId + owner)
                .WithChannelTypes(ChannelType.Text, ChannelType.News)
                .WithPlaceholder(localizer.Get(language, "esports.setup.channel_placeholder"))
                .WithMinValues(1).WithMaxValues(1), row: 0)
            .WithButton(localizer.Get(language, "esports.setup.preview"), PreviewId + owner, ButtonStyle.Secondary, row: 1)
            .WithButton(localizer.Get(language, "esports.setup.enable"), EnableId + owner, ButtonStyle.Success, disabled: enabled || view.ChannelId is null, row: 1)
            .Build();
        return (new MessageEmbed(localizer.Get(language, "esports.setup.title"), description, null, [], localizer.Get(language, "esports.setup.more"), null, ToroInteractionModule.BrandColor), components);
    }
}

/// <summary>
/// Setup components. They must work BEFORE esports is enabled (AllowWhenDisabled) — still guild-only, still limited to
/// the admin who opened the wizard, and re-authorized (Manage Server) on every click.
/// </summary>
[ToroModule(EsportsModule.ModuleIdValue, AllowWhenDisabled = true)]
public sealed class EsportsSetupComponents(
    InteractionServices services,
    EsportsConfigService config,
    EsportsPreviewService preview,
    ModuleManagementService modules) : ToroInteractionModule(services)
{
    [ComponentInteraction(EsportsSetupFlow.ChannelId + "*", ignoreGroupNames: true)]
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

        await ReplyResultAsync(await config.ConfigureAsync(Actor, channel, null, null, null, null, CancellationToken.None));
    }

    [ComponentInteraction(EsportsSetupFlow.PreviewId + "*", ignoreGroupNames: true)]
    public async Task PreviewAsync(string owner)
    {
        if (!await OwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        var result = await preview.BuildAsync(Actor, await LangAsync(), CancellationToken.None);
        if (result.Message is null)
        {
            await ReplyResultAsync(result.Auth);
            return;
        }

        var note = await T(result.UsedSample ? "esports.preview.sample_note" : "esports.preview.real_note");
        note += "\n" + (result.WouldPing.Count == 0
            ? await T("esports.preview.no_pings")
            : await T("esports.preview.would_ping", string.Join(" ", result.WouldPing.Select(DiscordText.RoleMention))));
        await SendEphemeralAsync(note, DiscordConversions.ToEmbed(result.Message.Embed), null);
    }

    [ComponentInteraction(EsportsSetupFlow.EnableId + "*", ignoreGroupNames: true)]
    public async Task EnableAsync(string owner)
    {
        if (!await OwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        var view = await config.GetAsync(Actor.GuildId, CancellationToken.None);
        if (view.ChannelId is null)
        {
            await ReplyTextAsync("esports.setup.channel_required");
            return;
        }

        await ReplyResultAsync(await modules.SetEnabledAsync(Actor, EsportsModule.ModuleIdValue, true, CancellationToken.None));
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
