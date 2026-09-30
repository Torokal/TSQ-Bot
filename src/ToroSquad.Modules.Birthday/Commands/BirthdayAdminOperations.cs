using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Birthday.Application;
using ToroSquad.Modules.Birthday.Persistence;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Birthday.Commands;

/// <summary>
/// <c>/tsq-admin modul:birthday</c> — Manage Server required: the shared command is hidden by default_member_permissions AND
/// re-authorized in the service. Works while the module is disabled so the channel can be set and checked before activation
/// (/modules enable birthday). Nothing here sends an announcement or changes a role, and there is no list of birthdays. `set`
/// and `show` change or read one member's personal data and therefore require Discord's Administrator permission (or guild
/// ownership) on top — declared on the operation (suggestions, early refusal) and checked in the service from the caller's
/// effective permissions. Dates are never logged.
/// </summary>
public sealed class BirthdayAdminOperations(BirthdayConfigService config, BirthdayService birthdays, BirthdayDoctor doctor, BirthdayHealth health)
    : IAdminFormHandler
{
    private const string DateAction = "date";
    private const string DateField = "date";

    public static readonly AdminModule Definition = AdminModule.For<BirthdayAdminOperations>(BirthdayModule.AdminId, BirthdayModule.ModuleIdTyped)
        .Op("set", (h, c) => h.SetAsync(c), AdminFields.User | AdminFields.Date, BirthdayService.SetForMemberPermission)
        .Op("show", (h, c) => h.ShowAsync(c), AdminFields.User, BirthdayService.SetForMemberPermission)
        .Op("configure", (h, c) => h.ConfigureAsync(c), AdminFields.Channel)
        .Op("status", (h, c) => h.StatusAsync(c))
        .Op("doctor", (h, c) => h.DoctorAsync(c))
        .Build();

    /// <summary>What a set/show form carries between its steps: the member once picked, a date given in the command.</summary>
    private sealed record Pending(AdminMember? Member, string? Date);

    /// <summary><c>uye</c> + <c>tarih</c>: saved at once. A missing member opens a member select, a missing date a date form.</summary>
    public async Task SetAsync(AdminCall call)
    {
        if (call.Args.Member is not { } member)
        {
            await AdminForms.PickUserAsync(call, "admin.birthday.set.pick", new Pending(null, call.Args.Date));
            return;
        }

        if (call.Args.Date is not { } date)
        {
            var draft = call.OpenDraft(new Pending(member, null));
            await DateFormAsync(call, draft);
            return;
        }

        await call.DeferAsync();
        await SaveAsync(call, member, date, finish: false);
    }

    public async Task ShowAsync(AdminCall call)
    {
        if (call.Args.Member is not { } member)
        {
            await AdminForms.PickUserAsync(call, "admin.birthday.show.pick");
            return;
        }

        await call.DeferAsync();
        await ShowMemberAsync(call, member, finish: false);
    }

    public async Task ConfigureAsync(AdminCall call)
    {
        if (call.Args.ChannelId is not { } channel)
        {
            await AdminForms.PickChannelAsync(call, "admin.birthday.configure.pick");
            return;
        }

        await call.DeferAsync();
        await call.ReplyResultAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
    }

    public async Task OnFormAsync(AdminCall call, string action)
    {
        switch (call.Operation.Id, action)
        {
            case ("configure", AdminForms.ChannelAction):
                if (AdminForms.ChosenChannel(call) is not { } channel)
                {
                    await call.ReplyTextAsync("admin.error.channel_invalid");
                    return;
                }

                if (await AdminForms.ClaimAsync(call))
                    await call.FinishAsync(await config.SetChannelAsync(call.Actor, channel, CancellationToken.None));
                return;
            case ("show", AdminForms.UserAction):
                if (AdminForms.ChosenMember(call) is { } shown && await AdminForms.ClaimAsync(call))
                    await ShowMemberAsync(call, shown, finish: true);
                return;
            case ("set", AdminForms.UserAction):
                if (AdminForms.ChosenMember(call) is not { } picked || call.Draft!.State is not Pending pending)
                    return;
                if (pending.Date is { } given)
                {
                    if (await AdminForms.ClaimAsync(call))
                        await SaveAsync(call, picked, given, finish: true);
                    return;
                }

                call.UpdateDraft(pending with { Member = picked });
                await DateFormAsync(call, call.Draft with { State = pending with { Member = picked } });
                return;
            case ("set", DateAction):
                if (call.Draft!.State is not Pending { Member: { } member })
                    return;
                if (await AdminForms.ClaimAsync(call))
                    await SaveAsync(call, member, call.Input.Text(DateField) ?? "", finish: true);
                return;
            default:
                await call.ReplyTextAsync("admin.form.expired");
                return;
        }
    }

    private static Task DateFormAsync(AdminCall call, AdminDraft draft) =>
        AdminForms.TextModalAsync(call, draft, DateAction, call.T("admin.birthday.set.form_title"), call.T("admin.birthday.set.form_label"), DateField,
            null, 10, placeholder: call.T("admin.birthday.set.form_placeholder"));

    /// <summary>The same validation and authorization as before (in the service); the reply never pings (mentions only render).</summary>
    private async Task SaveAsync(AdminCall call, AdminMember member, string date, bool finish)
    {
        var result = await birthdays.SetForMemberAsync(call.Actor, new UserId(member.Id), member.IsHumanMemberHere, date, CancellationToken.None);
        var text = result.Succeeded
            ? call.T(result.MessageKey, Mention(member), DateText(call, (int)result.Args[0], (int)result.Args[1]))
            : await call.Respond.DescribeAsync(result);
        await (finish ? call.FinishTextAsync(text) : call.Respond.SendAsync(text));
    }

    private async Task ShowMemberAsync(AdminCall call, AdminMember member, bool finish)
    {
        var (result, date) = await birthdays.GetForMemberAsync(call.Actor, new UserId(member.Id), member.IsHumanMemberHere, CancellationToken.None);
        var text = !result.Succeeded
            ? await call.Respond.DescribeAsync(result)
            : date is { } d ? call.T(result.MessageKey, Mention(member), DateText(call, d.Day, d.Month)) : call.T(result.MessageKey, Mention(member));
        await (finish ? call.FinishTextAsync(text) : call.Respond.SendAsync(text));
    }

    private static string Mention(AdminMember member) => "<@" + member.Id.ToString(CultureInfo.InvariantCulture) + ">";

    /// <summary>"14 Mart" / "March 14".</summary>
    private static string DateText(AdminCall call, int day, int month) => call.T("birthday.date", day, call.T(BirthdayCommands.MonthKeys[month - 1]));

    public async Task StatusAsync(AdminCall call)
    {
        await call.DeferAsync();
        var (auth, status) = await config.StatusAsync(call.Actor, CancellationToken.None);
        if (status is null)
        {
            await call.ReplyResultAsync(auth);
            return;
        }

        var channel = status.ChannelId is { } c ? "<#" + c.ToString(CultureInfo.InvariantCulture) + ">" : call.T("birthday.status.no_channel");
        if (status.ChannelProblem is not null)
            channel += " " + call.T("birthday.status.channel_problem", status.ChannelProblem);
        var fields = new List<EmbedField>
        {
            new(call.T("birthday.status.module"), call.T(status.Enabled ? "modules.state_on" : "modules.state_off"), true),
            new(call.T("birthday.status.channel"), channel, true),
            new(call.T("birthday.status.role"), status.RoleId == 0 ? call.T("birthday.status.no_role") : string.Create(CultureInfo.InvariantCulture, $"<@&{status.RoleId}> (`{status.RoleId}`)"), true),
            new(call.T("birthday.status.timezone"), call.T("birthday.status.timezone_value", status.TimeZone, status.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)), true),
            new(call.T("birthday.status.registered"), status.Registered.ToString(CultureInfo.InvariantCulture), true),
            new(call.T("birthday.status.today"), call.T("birthday.status.today_value", status.CelebratingToday, AnnouncementText(call, status.TodaysAnnouncement)), true),
            new(call.T("birthday.status.roles"), call.T("birthday.status.roles_value", status.ActiveRoles, status.RoleProblems), true),
            new(call.T("birthday.status.scheduler"), SchedulerText(call), false),
        };
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("birthday.status.title"), null, null, fields, null, null, ToroInteractionModule.NeutralColor));
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
        var failed = checks.Count(c => c.State == BirthdayCheckState.Problem);
        var summary = call.T(failed == 0 ? "birthday.doctor.summary_ok" : "birthday.doctor.summary_failed", failed);
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("birthday.doctor.title"), Clip(summary + "\n\n" + string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
            null, [], null, null, failed == 0 ? ToroInteractionModule.NeutralColor : ToroInteractionModule.WarningColor));
    }

    private static string AnnouncementText(AdminCall call, BirthdayAnnouncementState? state) => call.T(state switch
    {
        null => "birthday.status.announcement_none",
        BirthdayAnnouncementState.Queued => "birthday.status.announcement_queued",
        BirthdayAnnouncementState.Sent => "birthday.status.announcement_sent",
        BirthdayAnnouncementState.Simulated => "birthday.status.announcement_simulated",
        _ => "birthday.status.announcement_failed",
    });

    private string SchedulerText(AdminCall call)
    {
        if (health.WorkerStartedAt is null)
            return call.T("birthday.status.scheduler_not_running");
        if (health.LastCompletedAt is not { } done)
            return call.T("birthday.status.scheduler_no_pass");
        return call.T("birthday.status.scheduler_value", DiscordText.Timestamp(done, 'R'), health.ConsecutiveFailures);
    }

    private static string Icon(BirthdayCheckState state) => state switch
    {
        BirthdayCheckState.Ok => "✅",
        BirthdayCheckState.Warning => "⚠️",
        BirthdayCheckState.Problem => "❌",
        _ => "ℹ️",
    };

    private static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text.LastIndexOf('\n', Math.Max(0, max - 2));
        return (cut > 0 ? text[..cut] : text[..(max - 1)]) + "…";
    }
}
