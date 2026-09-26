using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Providers;
using DiscordPermission = Discord.GuildPermission;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Volleyball.Commands;

/// <summary>
/// /volleyball-admin — Manage Server required: hidden by default_member_permissions AND re-authorized in every service call.
/// Works while the module is disabled so setup can precede activation. There is no option to follow another team.
/// </summary>
[ToroModule(VolleyballModule.ModuleIdValue, AllowWhenDisabled = true)]
[Group("volleyball-admin", "Filenin Sultanları notification settings for this server (admins)")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class VolleyballAdminCommands(
    InteractionServices services,
    VolleyballConfigService config,
    VolleyballDoctor doctor,
    VolleyballPreviewService preview,
    VbSources sources,
    VbDataMode mode,
    IModuleGate gate) : ToroInteractionModule(services)
{
    [SlashCommand("preview", "Ping-free TEST/DEMO preview of a volleyball card")]
    public async Task PreviewAsync(
        [Summary("card", "Which card"), Choice("reminder", "reminder"), Choice("started", "started"), Choice("set-won", "set-won"), Choice("set-lost", "set-lost"),
         Choice("final-won", "final-won"), Choice("final-lost", "final-lost"), Choice("postponed", "postponed"), Choice("cancelled", "cancelled")] string card = "final-won")
    {
        await DeferEphemeralAsync();
        var kind = card switch
        {
            "reminder" => VbPreviewKind.Reminder,
            "started" => VbPreviewKind.Started,
            "set-won" => VbPreviewKind.SetWon,
            "set-lost" => VbPreviewKind.SetLost,
            "final-lost" => VbPreviewKind.FinalLost,
            "postponed" => VbPreviewKind.Postponed,
            "cancelled" => VbPreviewKind.Cancelled,
            _ => VbPreviewKind.FinalWon,
        };
        var result = await preview.BuildAsync(Actor, await LangAsync(), kind, CancellationToken.None);
        if (!result.Auth.Succeeded || result.Message is null)
        {
            await ReplyResultAsync(result.Auth);
            return;
        }

        var note = await T("vb.preview.note") + "\n" + (result.WouldPing.Count == 0
            ? await T("vb.preview.no_pings")
            : await T("vb.preview.would_ping", string.Join(" ", result.WouldPing.Select(DiscordText.RoleMention))));
        // Ephemeral + allowed_mentions none: a preview can never ping anyone.
        await SendEphemeralAsync(note, DiscordConversions.ToEmbed(result.Message.Embed), null);
    }

    [SlashCommand("status", "Current volleyball configuration of this server")]
    public async Task StatusAsync()
    {
        var auth = Authorize.Require(Actor, Actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
        {
            await ReplyResultAsync(OperationResult.Forbidden(auth));
            return;
        }

        await DeferEphemeralAsync();
        var lang = await LangAsync();
        var c = await config.GetAsync(Actor.GuildId, CancellationToken.None);
        var enabled = await gate.IsEnabledAsync(Actor.GuildId, VolleyballModule.ModuleIdTyped, CancellationToken.None);
        string YesNo(bool v) => Localizer.Get(lang, v ? "common.yes" : "common.no");
        var fields = new List<EmbedField>
        {
            new(await T("vb.status.module"), Localizer.Get(lang, enabled ? "modules.state_on" : "modules.state_off"), true),
            new(await T("vb.status.channel"), c?.ChannelId is { } ch ? "<#" + ch.ToString(CultureInfo.InvariantCulture) + ">" : "—", true),
            new(await T("vb.status.paused"), YesNo(c?.Paused ?? false), true),
            new(await T("vb.status.role"), c?.PingRoleId is { } r ? DiscordText.RoleMention(new RoleId(r)) + " · " + await T("vb.status.role_pings", YesNo(c.PingOnReminder), YesNo(c.PingOnFinal)) : "—", false),
            new(await T("vb.status.team"), await T("vb.status.team_value"), false),
            new(await T("vb.status.mode"), mode.IsDemo ? "FIXTURE / TEST-DEMO" : "LIVE", true),
            new(await T("vb.status.provider"), Localizer.Get(lang, sources.AttributionKey), true),
        };
        if (c is not null)
        {
            fields.Add(new(await T("vb.status.notifications"), string.Join("\n",
            [
                await T("vb.status.line", await T("vb.kind.reminder"), YesNo(c.NotifyReminder)),
                await T("vb.status.line", await T("vb.kind.started"), YesNo(c.NotifyStarted)),
                await T("vb.status.line", await T("vb.kind.sets"), YesNo(c.NotifySets)),
                await T("vb.status.line", await T("vb.kind.final"), YesNo(c.NotifyFinal)),
                await T("vb.status.line", await T("vb.kind.postponed_cancelled"), YesNo(c.NotifyPostponedCancelled)),
            ]), false));
        }

        await ReplyEmbedAsync(new MessageEmbed(await T("vb.status.title"), c is null ? await T("vb.status.not_configured") : null, null, fields, null, null, NeutralColor));
    }

    [SlashCommand("doctor", "Diagnose permissions, data provider health and delivery")]
    public async Task DoctorAsync()
    {
        await DeferEphemeralAsync();
        var (auth, checks) = await doctor.RunAsync(Actor, CancellationToken.None);
        if (!auth.Succeeded)
        {
            await ReplyResultAsync(auth);
            return;
        }

        var lang = await LangAsync();
        var lines = checks.Select(c => $"{Icon(c.State)} **{Localizer.Get(lang, c.LabelKey)}** — {Localizer.Get(lang, c.DetailKey, c.Args.ToArray())}");
        await ReplyEmbedAsync(new MessageEmbed(await T("vb.doctor.title"), VolleyballNotificationRenderer.Clip(string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
            null, [], null, null, NeutralColor));
    }

    [SlashCommand("pause", "Pause volleyball notifications in this server")]
    public async Task PauseAsync()
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await config.PauseAsync(Actor, true, CancellationToken.None));
    }

    [SlashCommand("resume", "Resume notifications (nothing missed while paused is sent)")]
    public async Task ResumeAsync()
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await config.PauseAsync(Actor, false, CancellationToken.None));
    }

    private static string Icon(VbCheckState state) => state switch
    {
        VbCheckState.Ok => "✅",
        VbCheckState.Warning => "⚠️",
        VbCheckState.Problem => "❌",
        _ => "ℹ️",
    };

    [ToroModule(VolleyballModule.ModuleIdValue, AllowWhenDisabled = true)]
    [Group("configure", "Volleyball notification settings")]
    public sealed class ConfigureCommands(InteractionServices services, VolleyballConfigService config) : ToroInteractionModule(services)
    {
        [SlashCommand("channel", "Channel for automatic volleyball notifications")]
        public async Task ChannelAsync([Summary("channel", "Notification channel"), ChannelTypes(ChannelType.Text, ChannelType.News)] IChannel channel)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetChannelAsync(Actor, channel.Id, CancellationToken.None));
        }

        [SlashCommand("notifications", "Which volleyball notifications are sent")]
        public async Task NotificationsAsync(
            [Summary("match_reminder_15m", "Reminder before the match")] bool? reminder = null,
            [Summary("match_started", "Match started")] bool? started = null,
            [Summary("set_finished", "Each finished set")] bool? sets = null,
            [Summary("match_finished", "Final result")] bool? final = null,
            [Summary("match_postponed_cancelled", "Postponed / cancelled")] bool? postponedCancelled = null)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetNotificationsAsync(Actor, new VbNotificationChanges(reminder, started, sets, final, postponedCancelled), CancellationToken.None));
        }

        [SlashCommand("role", "Optional role pinged by volleyball notifications (never @everyone)")]
        public async Task RoleAsync(
            [Summary("ping_role", "Role to ping (leave empty to keep the current one)")] IRole? pingRole = null,
            [Summary("ping_reminder", "Ping on the pre-match reminder")] bool? pingReminder = null,
            [Summary("ping_final", "Ping on the final result")] bool? pingFinal = null,
            [Summary("clear", "Remove the notification role")] bool clear = false)
        {
            await DeferEphemeralAsync();
            var current = await config.GetAsync(Actor.GuildId, CancellationToken.None);
            var roleId = clear ? null : pingRole?.Id ?? current?.PingRoleId;
            await ReplyResultAsync(await config.SetRoleAsync(Actor, roleId, pingReminder, pingFinal, CancellationToken.None));
        }
    }
}
