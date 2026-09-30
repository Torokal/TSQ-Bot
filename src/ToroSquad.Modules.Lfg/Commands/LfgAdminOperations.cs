using System.Globalization;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Lfg.Application;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Lfg.Commands;

/// <summary>
/// <c>/tsq-admin modul:lfg</c> — Manage Server: the shared command is hidden by default_member_permissions AND the service
/// re-authorizes. Works while the module is disabled so the channel can be chosen before activation.
/// </summary>
public sealed class LfgAdminOperations(LfgConfigService config, IModuleGate gate, IOptions<LfgOptions> options) : IAdminFormHandler
{
    public static readonly AdminModule Definition = AdminModule.For<LfgAdminOperations>(LfgModule.AdminId, LfgModule.ModuleIdTyped)
        .Op("channel", (h, c) => h.ChannelAsync(c), AdminFields.Channel)
        .Op("status", (h, c) => h.StatusAsync(c))
        .Build();

    /// <summary>
    /// With <c>kanal</c>: /ekip only there. Without it the form offers a channel OR an explicit "any channel" button — leaving
    /// the option empty never removes the restriction by itself.
    /// </summary>
    public async Task ChannelAsync(AdminCall call)
    {
        if (call.Args.ChannelId is not { } channel)
        {
            await AdminForms.PickChannelAsync(call, "admin.lfg.channel.pick", "admin.lfg.channel.any");
            return;
        }

        await call.DeferAsync();
        await call.ReplyResultAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
    }

    public async Task OnFormAsync(AdminCall call, string action)
    {
        ulong? channel;
        switch (action)
        {
            case AdminForms.ClearAction:
                channel = null;
                break;
            case AdminForms.ChannelAction when AdminForms.ChosenChannel(call) is { } picked:
                channel = picked;
                break;
            default:
                await call.ReplyTextAsync("admin.error.channel_invalid");
                return;
        }

        if (await AdminForms.ClaimAsync(call))
            await call.FinishAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
    }

    public async Task StatusAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, status) = await config.StatusAsync(call.Actor, CancellationToken.None);
        if (status is null)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var enabled = await gate.IsEnabledAsync(call.Actor.GuildId, LfgModule.ModuleIdTyped, CancellationToken.None);
        var o = options.Value;
        var fields = new List<EmbedField>
        {
            new(call.T("lfg.status.module"), call.T(enabled ? "modules.state_on" : "modules.state_off"), true),
            new(call.T("lfg.status.channel"), status.ChannelId is { } c ? "<#" + c.ToString(CultureInfo.InvariantCulture) + ">" : call.T("lfg.status.any_channel"), true),
            new(call.T("lfg.status.active"), call.T("lfg.status.active_value", status.Open, status.Full), true),
            new(call.T("lfg.status.duration"), call.T("lfg.status.duration_value", o.DefaultExpirationMinutes), true),
            new(call.T("lfg.status.limit"), o.MaxActiveListingsPerUser.ToString(CultureInfo.InvariantCulture), true),
            new(call.T("lfg.status.max_players"), o.MaxPlayersPerListing.ToString(CultureInfo.InvariantCulture), true),
            new(call.T("lfg.status.pending"), status.PendingCardUpdates.ToString(CultureInfo.InvariantCulture), true),
            new(call.T("lfg.status.timezone"), call.T("lfg.status.timezone_value", status.TimeZoneId), true),
        };
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("lfg.status.title"), null, null, fields, null, null, ToroInteractionModule.NeutralColor));
    }
}
