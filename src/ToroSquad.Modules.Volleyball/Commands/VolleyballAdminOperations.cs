using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Persistence;
using ToroSquad.Modules.Volleyball.Providers;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Volleyball.Commands;

/// <summary>
/// <c>/tsq-admin modul:volleyball</c> — Manage Server required: the shared command is hidden by default_member_permissions AND
/// every service call re-authorizes. Works while the module is disabled so setup can precede activation. There is no option
/// to follow another team. Multi-field settings open a private form with the current values; only changes are written.
/// </summary>
public sealed class VolleyballAdminOperations(
    VolleyballConfigService config,
    VolleyballDoctor doctor,
    VolleyballPreviewService preview,
    VbSources sources,
    VbDataMode mode,
    IModuleGate gate) : IAdminFormHandler
{
    public static readonly AdminModule Definition = AdminModule.For<VolleyballAdminOperations>(VolleyballModule.AdminId, VolleyballModule.ModuleIdTyped)
        .Op("preview", (_, c) => PreviewAsync(c))
        .Op("status", (h, c) => h.StatusAsync(c))
        .Op("doctor", (h, c) => h.DoctorAsync(c))
        .Op("pause", (h, c) => h.PauseAsync(c, true))
        .Op("resume", (h, c) => h.PauseAsync(c, false))
        .Op("configure-channel", (h, c) => h.ChannelAsync(c), AdminFields.Channel)
        .Op("configure-notifications", (h, c) => h.NotificationsAsync(c))
        .Op("configure-role", (h, c) => h.RoleAsync(c), AdminFields.Role)
        .Build();

    private const string CardAction = "card";
    private const string SaveAction = "save";

    private static readonly string[] Cards = ["reminder", "started", "set-won", "set-lost", "final-won", "final-lost", "postponed", "cancelled"];

    private static readonly (string Name, Func<VolleyballGuildConfigEntity, bool> Current)[] Switches =
    [
        ("match_reminder_15m", c => c.NotifyReminder),
        ("match_started", c => c.NotifyStarted),
        ("set_finished", c => c.NotifySets),
        ("match_finished", c => c.NotifyFinal),
        ("match_postponed_cancelled", c => c.NotifyPostponedCancelled),
    ];

    private static readonly (string Name, Func<VolleyballGuildConfigEntity, bool> Current)[] Pings =
    [
        ("ping_reminder", c => c.PingOnReminder),
        ("ping_final", c => c.PingOnFinal),
    ];

    private sealed record Snapshot(IReadOnlyDictionary<string, bool> Values);

    public static Task PreviewAsync(AdminCall call) =>
        AdminForms.ChooseAsync(call, call.T("admin.volleyball.preview.pick"), CardAction,
            Cards.Select(card => (call.T("admin.volleyball.preview.card." + card), card, card == "final-won")));

    public async Task StatusAsync(AdminCall call)
    {
        await call.DeferAsync();
        var c = await config.GetAsync(call.Actor.GuildId, CancellationToken.None);
        var enabled = await gate.IsEnabledAsync(call.Actor.GuildId, VolleyballModule.ModuleIdTyped, CancellationToken.None);
        string YesNo(bool v) => AdminForms.YesNo(call, v);
        var fields = new List<EmbedField>
        {
            new(call.T("vb.status.module"), call.T(enabled ? "modules.state_on" : "modules.state_off"), true),
            new(call.T("vb.status.channel"), c?.ChannelId is { } ch ? "<#" + ch.ToString(CultureInfo.InvariantCulture) + ">" : "—", true),
            new(call.T("vb.status.paused"), YesNo(c?.Paused ?? false), true),
            new(call.T("vb.status.role"), c?.PingRoleId is { } r ? DiscordText.RoleMention(new RoleId(r)) + " · " + call.T("vb.status.role_pings", YesNo(c.PingOnReminder), YesNo(c.PingOnFinal)) : "—", false),
            new(call.T("vb.status.team"), call.T("vb.status.team_value"), false),
            new(call.T("vb.status.mode"), mode.IsDemo ? "FIXTURE / TEST-DEMO" : "LIVE", true),
            new(call.T("vb.status.provider"), call.T(sources.AttributionKey), true),
        };
        if (c is not null)
        {
            fields.Add(new(call.T("vb.status.notifications"), string.Join("\n",
            [
                call.T("vb.status.line", call.T("vb.kind.reminder"), YesNo(c.NotifyReminder)),
                call.T("vb.status.line", call.T("vb.kind.started"), YesNo(c.NotifyStarted)),
                call.T("vb.status.line", call.T("vb.kind.sets"), YesNo(c.NotifySets)),
                call.T("vb.status.line", call.T("vb.kind.final"), YesNo(c.NotifyFinal)),
                call.T("vb.status.line", call.T("vb.kind.postponed_cancelled"), YesNo(c.NotifyPostponedCancelled)),
            ]), false));
        }

        await call.ReplyEmbedAsync(new MessageEmbed(call.T("vb.status.title"), c is null ? call.T("vb.status.not_configured") : null, null, fields, null, null,
            ToroInteractionModule.NeutralColor));
    }

    public async Task DoctorAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, checks) = await doctor.RunAsync(call.Actor, CancellationToken.None);
        if (!auth.Succeeded)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var lines = checks.Select(c => $"{Icon(c.State)} **{call.T(c.LabelKey)}** — {call.T(c.DetailKey, c.Args.ToArray())}");
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("vb.doctor.title"), VolleyballNotificationRenderer.Clip(string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
            null, [], null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task PauseAsync(AdminCall call, bool pause)
    {
        await call.DeferAsync();
        await call.ReplyResultAsync(await config.PauseAsync(call.Actor, pause, CancellationToken.None));
    }

    public async Task ChannelAsync(AdminCall call)
    {
        if (call.Args.ChannelId is not { } channel)
        {
            await AdminForms.PickChannelAsync(call, "admin.volleyball.configure-channel.pick");
            return;
        }

        await call.DeferAsync();
        await call.ReplyResultAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
    }

    public async Task NotificationsAsync(AdminCall call)
    {
        var current = await config.GetAsync(call.Actor.GuildId, CancellationToken.None) ?? new VolleyballGuildConfigEntity();
        var snapshot = Switches.ToDictionary(s => s.Name, s => s.Current(current));
        var draft = call.OpenDraft(new Snapshot(snapshot));
        await AdminForms.SwitchesModalAsync(call, draft, SaveAction, call.T("admin.volleyball.configure-notifications.form_title"),
            call.T("admin.volleyball.configure-notifications.form_label"),
            Switches.Select(s => (s.Name, call.T("admin.volleyball.configure-notifications." + s.Name), snapshot[s.Name])).ToList());
    }

    public async Task RoleAsync(AdminCall call)
    {
        var current = await config.GetAsync(call.Actor.GuildId, CancellationToken.None) ?? new VolleyballGuildConfigEntity();
        var snapshot = Pings.ToDictionary(p => p.Name, p => p.Current(current));
        var draft = call.OpenDraft(new Snapshot(snapshot));
        await AdminForms.RoleModalAsync(call, draft, SaveAction, call.T("admin.volleyball.configure-role.form_title"), call.Args.RoleId ?? current.PingRoleId,
            Pings.Select(p => (p.Name, call.T("admin.volleyball.configure-role." + p.Name), snapshot[p.Name])).ToList());
    }

    public async Task OnFormAsync(AdminCall call, string action)
    {
        switch (call.Operation.Id, action)
        {
            case ("preview", CardAction):
                await ShowPreviewAsync(call, call.Input.FirstValue ?? "");
                return;
            case ("configure-channel", AdminForms.ChannelAction):
                if (AdminForms.ChosenChannel(call) is not { } channel)
                    await call.ReplyTextAsync("admin.error.channel_invalid");
                else if (await AdminForms.ClaimAsync(call))
                    await call.FinishAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
                return;
            case ("configure-notifications", SaveAction) when call.Draft?.State is Snapshot snapshot:
                if (!await AdminForms.ClaimAsync(call))
                    return;
                var changed = AdminForms.ChangedSwitches(call, snapshot.Values);
                if (changed.Count == 0)
                {
                    await call.FinishTextAsync(call.T("admin.form.no_changes"));
                    return;
                }

                bool? Get(string name) => changed.TryGetValue(name, out var value) ? value : null;
                await call.FinishAsync(await config.SetNotificationsAsync(call.Actor, new VbNotificationChanges(
                    Get("match_reminder_15m"), Get("match_started"), Get("set_finished"), Get("match_finished"), Get("match_postponed_cancelled")), CancellationToken.None));
                return;
            case ("configure-role", SaveAction) when call.Draft?.State is Snapshot pings:
                if (!await AdminForms.ClaimAsync(call))
                    return;
                var (clear, role) = AdminForms.SubmittedRole(call);
                var pingChanges = AdminForms.ChangedSwitches(call, pings.Values);
                var roleId = clear ? null : role ?? (await config.GetAsync(call.Actor.GuildId, CancellationToken.None))?.PingRoleId;
                await call.FinishAsync(await config.SetRoleAsync(call.Actor, roleId,
                    pingChanges.TryGetValue("ping_reminder", out var reminder) ? reminder : null,
                    pingChanges.TryGetValue("ping_final", out var final) ? final : null, CancellationToken.None));
                return;
            default:
                await call.ReplyTextAsync("admin.form.expired");
                return;
        }
    }

    private async Task ShowPreviewAsync(AdminCall call, string card)
    {
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
        var result = await preview.BuildAsync(call.Actor, call.Language, kind, CancellationToken.None);
        if (!result.Auth.Succeeded || result.Message is null)
        {
            await call.ReplyResultAsync(result.Auth);
            return;
        }

        var note = call.T("vb.preview.note") + "\n" + (result.WouldPing.Count == 0
            ? call.T("vb.preview.no_pings")
            : call.T("vb.preview.would_ping", string.Join(" ", result.WouldPing.Select(DiscordText.RoleMention))));
        // Private + allowed_mentions none: a preview can never ping anyone.
        await call.Respond.SendAsync(note, result.Message.Embed);
    }

    private static string Icon(VbCheckState state) => state switch
    {
        VbCheckState.Ok => "✅",
        VbCheckState.Warning => "⚠️",
        VbCheckState.Problem => "❌",
        _ => "ℹ️",
    };
}
