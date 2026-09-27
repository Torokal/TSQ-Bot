using System.Globalization;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Lfg.Application;
using DiscordPermission = Discord.GuildPermission;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Lfg.Commands;

/// <summary>
/// /lfg-admin — Manage Server required: hidden by default_member_permissions AND re-authorized in the service. Works while
/// the module is disabled so the channel can be chosen before activation.
/// </summary>
[ToroModule(LfgModule.ModuleIdValue, AllowWhenDisabled = true)]
[Group("lfg-admin", "TSQ LFG group finder settings (admins)")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class LfgAdminCommands(InteractionServices services, LfgConfigService config, IModuleGate gate, IOptions<LfgOptions> options)
    : ToroInteractionModule(services)
{
    [SlashCommand("channel", "Channel where /ekip listings can be opened (empty: any channel)")]
    public async Task ChannelAsync(
        [Summary("channel", "Listing channel; leave empty to allow every channel"), ChannelTypes(ChannelType.Text, ChannelType.News)] IChannel? channel = null)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await config.SetChannelAsync(Actor, channel?.Id, CancellationToken.None));
    }

    [SlashCommand("status", "LFG settings and active listings in this server")]
    public async Task StatusAsync()
    {
        await DeferEphemeralAsync();
        var (auth, status) = await config.StatusAsync(Actor, CancellationToken.None);
        if (status is null)
        {
            await ReplyResultAsync(auth);
            return;
        }

        var lang = await LangAsync();
        var enabled = await gate.IsEnabledAsync(Actor.GuildId, LfgModule.ModuleIdTyped, CancellationToken.None);
        var o = options.Value;
        var fields = new List<EmbedField>
        {
            new(await T("lfg.status.module"), Localizer.Get(lang, enabled ? "modules.state_on" : "modules.state_off"), true),
            new(await T("lfg.status.channel"), status.ChannelId is { } c ? "<#" + c.ToString(CultureInfo.InvariantCulture) + ">" : await T("lfg.status.any_channel"), true),
            new(await T("lfg.status.active"), await T("lfg.status.active_value", status.Open, status.Full), true),
            new(await T("lfg.status.duration"), await T("lfg.status.duration_value", o.DefaultExpirationMinutes), true),
            new(await T("lfg.status.limit"), o.MaxActiveListingsPerUser.ToString(CultureInfo.InvariantCulture), true),
            new(await T("lfg.status.max_players"), o.MaxPlayersPerListing.ToString(CultureInfo.InvariantCulture), true),
            new(await T("lfg.status.pending"), status.PendingCardUpdates.ToString(CultureInfo.InvariantCulture), true),
        };
        await ReplyEmbedAsync(new MessageEmbed(await T("lfg.status.title"), null, null, fields, null, null, NeutralColor));
    }
}
