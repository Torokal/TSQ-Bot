using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Formula1.Providers;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Formula1.Commands;

/// <summary>
/// <c>/tsq-admin modul:f1</c> — Manage Server required: the shared command is hidden by default_member_permissions AND every
/// service call re-authorizes (a bypassed Discord permission check still fails server-side). Works while the module is disabled
/// so setup can precede activation. Multi-field settings open a private form showing the current values; only what the admin
/// changes there is written.
/// </summary>
public sealed class Formula1AdminOperations(
    Formula1ConfigService config,
    Formula1Doctor doctor,
    Formula1PreviewService preview,
    Formula1Cache cache,
    F1Sources sources,
    F1DataMode mode,
    IModuleGate gate) : IAdminFormHandler
{
    public static readonly AdminModule Definition = AdminModule.For<Formula1AdminOperations>(Formula1Module.AdminId, Formula1Module.ModuleIdTyped)
        .Op("preview", (_, c) => PreviewAsync(c))
        .Op("status", (h, c) => h.StatusAsync(c))
        .Op("doctor", (h, c) => h.DoctorAsync(c))
        .Op("pause", (h, c) => h.PauseAsync(c, true))
        .Op("resume", (h, c) => h.PauseAsync(c, false))
        .Op("configure-channel", (h, c) => h.ChannelAsync(c), AdminFields.Channel)
        .Op("configure-notifications", (h, c) => h.NotificationsAsync(c))
        .Op("configure-role", (h, c) => h.RoleAsync(c), AdminFields.Role)
        .Op("configure-spoilers", (h, c) => h.SpoilersAsync(c))
        .Build();

    private const string CardAction = "card";
    private const string SaveAction = "save";
    private const string OnAction = "on";
    private const string OffAction = "off";

    private static readonly string[] Cards = ["practice-start", "race-start", "practice-result", "race-result", "race-result-standings"];

    /// <summary>Every notification switch, in the order of the former /f1-admin configure notifications options.</summary>
    private static readonly (string Name, Func<Formula1GuildConfigEntity, bool> Current)[] Switches =
    [
        ("practice_start", c => c.NotifyPracticeStart),
        ("practice_results", c => c.NotifyPracticeResults),
        ("sprint_start", c => c.NotifySprintStart),
        ("sprint_results", c => c.NotifySprintResults),
        ("race_start", c => c.NotifyRaceStart),
        ("race_results", c => c.NotifyRaceResults),
        ("standings", c => c.NotifyStandings),
        ("qualifying_start", c => c.NotifyQualifyingStart),
        ("qualifying_results", c => c.NotifyQualifyingResults),
        ("sprint_qualifying_start", c => c.NotifySprintQualifyingStart),
        ("sprint_qualifying_results", c => c.NotifySprintQualifyingResults),
        ("weekend_schedule", c => c.NotifyWeekendSchedule),
        ("race_reminder", c => c.NotifyRaceReminder),
        ("disqualification", c => c.NotifyDisqualification),
        ("safety_car", c => c.NotifySafetyCar),
        ("red_flag", c => c.NotifyRedFlag),
    ];

    private static readonly (string Name, Func<Formula1GuildConfigEntity, bool> Current)[] Pings =
    [
        ("ping_starts", c => c.PingOnStarts),
        ("ping_results", c => c.PingOnResults),
    ];

    private sealed record Snapshot(IReadOnlyDictionary<string, bool> Values);

    /// <summary>A card picker (the former <c>card</c> choices); each pick shows one ping-free TEST/DEMO preview privately.</summary>
    public static Task PreviewAsync(AdminCall call) =>
        AdminForms.ChooseAsync(call, call.T("admin.f1.preview.pick"), CardAction,
            Cards.Select(card => (call.T("admin.f1.preview.card." + card), card, card == "race-result-standings")));

    public async Task StatusAsync(AdminCall call)
    {
        await call.DeferAsync();
        var c = await config.GetAsync(call.Actor.GuildId, CancellationToken.None);
        var enabled = await gate.IsEnabledAsync(call.Actor.GuildId, Formula1Module.ModuleIdTyped, CancellationToken.None);
        string YesNo(bool v) => AdminForms.YesNo(call, v);
        var fields = new List<EmbedField>
        {
            new(call.T("f1.status.module"), call.T(enabled ? "modules.state_on" : "modules.state_off"), true),
            new(call.T("f1.status.channel"), c?.ChannelId is { } ch ? "<#" + ch.ToString(CultureInfo.InvariantCulture) + ">" : "—", true),
            new(call.T("f1.status.paused"), YesNo(c?.Paused ?? false), true),
            new(call.T("f1.status.role"), c?.PingRoleId is { } r ? DiscordText.RoleMention(new RoleId(r)) + " · " + call.T("f1.status.role_pings", YesNo(c.PingOnStarts), YesNo(c.PingOnResults)) : "—", false),
            new(call.T("f1.status.spoilers"), YesNo(c?.SpoilerMode ?? false), true),
            new(call.T("f1.status.mode"), mode.IsDemo ? "FIXTURE / TEST-DEMO" : "LIVE", true),
            new(call.T("f1.status.lifecycle"), sources.LifecycleConfigured ? cache.Live.State.ToString() : call.T("f1.status.lifecycle_not_configured"), true),
        };
        if (c is not null)
        {
            fields.Add(new(call.T("f1.status.notifications"), string.Join("\n",
            [
                call.T("f1.status.line", call.T("f1.category.practice"), YesNo(c.NotifyPracticeStart), YesNo(c.NotifyPracticeResults)),
                call.T("f1.status.line", call.T("f1.category.sprint"), YesNo(c.NotifySprintStart), YesNo(c.NotifySprintResults)),
                call.T("f1.status.line", call.T("f1.category.race"), YesNo(c.NotifyRaceStart), YesNo(c.NotifyRaceResults)),
                call.T("f1.status.line", call.T("f1.category.qualifying"), YesNo(c.NotifyQualifyingStart), YesNo(c.NotifyQualifyingResults)),
                call.T("f1.status.line", call.T("f1.category.sprint_qualifying"), YesNo(c.NotifySprintQualifyingStart), YesNo(c.NotifySprintQualifyingResults)),
                call.T("f1.status.standings_line", YesNo(c.NotifyStandings)),
                call.T("f1.status.extras_line", YesNo(c.NotifyWeekendSchedule), YesNo(c.NotifyRaceReminder)),
                call.T("f1.status.incidents_line", YesNo(c.NotifySafetyCar), YesNo(c.NotifyRedFlag), YesNo(c.NotifyDisqualification)),
            ]), false));
        }

        await call.ReplyEmbedAsync(new MessageEmbed(call.T("f1.status.title"), c is null ? call.T("f1.status.not_configured") : null, null, fields, null, null,
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
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("f1.doctor.title"), Formula1NotificationRenderer.Clip(string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
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
            await AdminForms.PickChannelAsync(call, "admin.f1.configure-channel.pick");
            return;
        }

        await call.DeferAsync();
        await call.ReplyResultAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
    }

    /// <summary>All 16 switches in one private form, each pre-selected as stored; nothing changes until it is submitted.</summary>
    public async Task NotificationsAsync(AdminCall call)
    {
        var current = await config.GetAsync(call.Actor.GuildId, CancellationToken.None) ?? new Formula1GuildConfigEntity();
        var snapshot = Switches.ToDictionary(s => s.Name, s => s.Current(current));
        var draft = call.OpenDraft(new Snapshot(snapshot));
        await AdminForms.SwitchesModalAsync(call, draft, SaveAction, call.T("admin.f1.configure-notifications.form_title"), call.T("admin.f1.configure-notifications.form_label"),
            Switches.Select(s => (s.Name, call.T("admin.f1.configure-notifications." + s.Name), snapshot[s.Name])).ToList());
    }

    /// <summary>Role (pre-filled with <c>rol</c> or the current one), its two ping switches and an explicit "remove".</summary>
    public async Task RoleAsync(AdminCall call)
    {
        var current = await config.GetAsync(call.Actor.GuildId, CancellationToken.None) ?? new Formula1GuildConfigEntity();
        var snapshot = Pings.ToDictionary(p => p.Name, p => p.Current(current));
        var draft = call.OpenDraft(new Snapshot(snapshot));
        await AdminForms.RoleModalAsync(call, draft, SaveAction, call.T("admin.f1.configure-role.form_title"), call.Args.RoleId ?? current.PingRoleId,
            Pings.Select(p => (p.Name, call.T("admin.f1.configure-role." + p.Name), snapshot[p.Name])).ToList());
    }

    /// <summary>Two explicit buttons (on / off) under the current state.</summary>
    public async Task SpoilersAsync(AdminCall call)
    {
        var current = await config.GetAsync(call.Actor.GuildId, CancellationToken.None);
        await AdminForms.ButtonsAsync(call, call.T("admin.f1.configure-spoilers.pick", AdminForms.YesNo(call, current?.SpoilerMode ?? false)),
            [(call.T("admin.form.turn_on"), OnAction), (call.T("admin.form.turn_off"), OffAction)]);
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
                await call.FinishAsync(await config.SetNotificationsAsync(call.Actor, new F1NotificationChanges(
                    Get("practice_start"), Get("practice_results"), Get("sprint_start"), Get("sprint_results"), Get("race_start"), Get("race_results"),
                    Get("standings"), Get("qualifying_start"), Get("qualifying_results"), Get("sprint_qualifying_start"), Get("sprint_qualifying_results"),
                    Get("weekend_schedule"), Get("race_reminder"), Get("disqualification"), Get("safety_car"), Get("red_flag")), CancellationToken.None));
                return;
            case ("configure-role", SaveAction) when call.Draft?.State is Snapshot pings:
                if (!await AdminForms.ClaimAsync(call))
                    return;
                var (clear, role) = AdminForms.SubmittedRole(call);
                var pingChanges = AdminForms.ChangedSwitches(call, pings.Values);
                // As before: an empty role keeps the current one; only "remove" clears it (read now, not from the form's start).
                var roleId = clear ? null : role ?? (await config.GetAsync(call.Actor.GuildId, CancellationToken.None))?.PingRoleId;
                await call.FinishAsync(await config.SetRoleAsync(call.Actor, roleId,
                    pingChanges.TryGetValue("ping_starts", out var starts) ? starts : null,
                    pingChanges.TryGetValue("ping_results", out var results) ? results : null, CancellationToken.None));
                return;
            case ("configure-spoilers", OnAction or OffAction):
                if (await AdminForms.ClaimAsync(call))
                    await call.FinishAsync(await config.SetSpoilersAsync(call.Actor, action == OnAction, CancellationToken.None));
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
            "practice-start" => F1PreviewKind.PracticeStart,
            "race-start" => F1PreviewKind.RaceStart,
            "practice-result" => F1PreviewKind.PracticeResult,
            "race-result" => F1PreviewKind.RaceResult,
            _ => F1PreviewKind.RaceResultStandings,
        };
        var result = await preview.BuildAsync(call.Actor, call.Language, kind, CancellationToken.None);
        if (!result.Auth.Succeeded || result.Message is null)
        {
            await call.ReplyResultAsync(result.Auth);
            return;
        }

        var note = call.T("f1.preview.note") + "\n" + (result.WouldPing.Count == 0
            ? call.T("f1.preview.no_pings")
            : call.T("f1.preview.would_ping", string.Join(" ", result.WouldPing.Select(DiscordText.RoleMention))));
        // Private + allowed_mentions none: a preview can never ping anyone.
        await call.Respond.SendAsync(note, result.Message.Embed);
    }

    private static string Icon(F1CheckState state) => state switch
    {
        F1CheckState.Ok => "✅",
        F1CheckState.Warning => "⚠️",
        F1CheckState.Problem => "❌",
        _ => "ℹ️",
    };
}
