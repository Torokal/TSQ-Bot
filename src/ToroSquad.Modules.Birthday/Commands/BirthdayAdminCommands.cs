using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Birthday.Application;
using ToroSquad.Modules.Birthday.Persistence;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Birthday.Commands;

/// <summary><c>/tsq-admin birthday</c>: this module's group in the shared admin root (see <see cref="TsqAdminRoot"/>).</summary>
public sealed class BirthdayTsqAdmin : TsqAdminRoot
{
    /// <summary>
    /// /tsq-admin birthday — Manage Server required: hidden by default_member_permissions AND re-authorized in the service. Works while
    /// the module is disabled so the channel can be set and checked before activation (/modules enable birthday). Nothing here
    /// sends an announcement or changes a role, and there is no list of birthdays. `set` and `show` change or read one member's
    /// personal data and therefore require Discord's Administrator permission (or guild ownership) on top — checked in the
    /// service from the caller's effective permissions; Discord can only hide a whole command group, not one subcommand.
    /// </summary>
    [ToroModule(BirthdayModule.ModuleIdValue, AllowWhenDisabled = true)]
    [Group("birthday", "Birthday announcements and role (admins)")]
    public sealed class BirthdayAdminCommands(
        InteractionServices services,
        BirthdayConfigService config,
        BirthdayService birthdays,
        BirthdayDoctor doctor,
        BirthdayHealth health) : ToroInteractionModule(services)
    {
        [SlashCommand("set", "Set a member's birthday (Administrator only)")]
        public async Task SetAsync(
            [Summary("member", "Member whose birthday you set")] IUser member,
            [Summary("date", "Day and month: 14.03, 14/03 or 14-03 (no year)")] string date)
        {
            await DeferEphemeralAsync();
            var result = await birthdays.SetForMemberAsync(Actor, new UserId(member.Id), IsHumanMemberHere(member), date, CancellationToken.None);
            if (!result.Succeeded)
            {
                await ReplyResultAsync(result);
                return;
            }

            // Private answer; the mention only renders the name (replies never ping).
            await ReplyTextAsync(result.MessageKey, Inv($"<@{member.Id}>"), await DateTextAsync((int)result.Args[0], (int)result.Args[1]));
        }

        [SlashCommand("show", "Show a member's saved birthday (Administrator only)")]
        public async Task ShowAsync([Summary("member", "Member whose birthday you want to see")] IUser member)
        {
            await DeferEphemeralAsync();
            var (result, date) = await birthdays.GetForMemberAsync(Actor, new UserId(member.Id), IsHumanMemberHere(member), CancellationToken.None);
            if (!result.Succeeded)
            {
                await ReplyResultAsync(result);
                return;
            }

            // Private answer for the admin only; the mention only renders the name (replies never ping).
            var mention = Inv($"<@{member.Id}>");
            if (date is { } d)
                await ReplyTextAsync(result.MessageKey, mention, await DateTextAsync(d.Day, d.Month));
            else
                await ReplyTextAsync(result.MessageKey, mention);
        }

        /// <summary>Only a human member of THIS guild; the id comes from Discord's resolved option, never typed text.</summary>
        private bool IsHumanMemberHere(IUser member) => member is IGuildUser { IsBot: false } target && target.GuildId == Context.Guild.Id;

        /// <summary>"14 Mart" / "March 14".</summary>
        private async Task<string> DateTextAsync(int day, int month) => await T("birthday.date", day, await T(BirthdayCommands.MonthKeys[month - 1]));

        [SlashCommand("configure", "Channel for the birthday announcements")]
        public async Task ConfigureAsync(
            [Summary("channel", "Announcement channel"), ChannelTypes(ChannelType.Text, ChannelType.News)] IChannel channel)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetChannelAsync(Actor, channel.Id, CancellationToken.None));
        }

        [SlashCommand("status", "Birthday module settings and today's state")]
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
            var channel = status.ChannelId is { } c ? "<#" + c.ToString(CultureInfo.InvariantCulture) + ">" : await T("birthday.status.no_channel");
            if (status.ChannelProblem is not null)
                channel += " " + await T("birthday.status.channel_problem", status.ChannelProblem);
            var fields = new List<EmbedField>
            {
                new(await T("birthday.status.module"), Localizer.Get(lang, status.Enabled ? "modules.state_on" : "modules.state_off"), true),
                new(await T("birthday.status.channel"), channel, true),
                new(await T("birthday.status.role"), status.RoleId == 0 ? await T("birthday.status.no_role") : Inv($"<@&{status.RoleId}> (`{status.RoleId}`)"), true),
                new(await T("birthday.status.timezone"), await T("birthday.status.timezone_value", status.TimeZone, status.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)), true),
                new(await T("birthday.status.registered"), status.Registered.ToString(CultureInfo.InvariantCulture), true),
                new(await T("birthday.status.today"), await T("birthday.status.today_value", status.CelebratingToday, await AnnouncementTextAsync(status.TodaysAnnouncement)), true),
                new(await T("birthday.status.roles"), await T("birthday.status.roles_value", status.ActiveRoles, status.RoleProblems), true),
                new(await T("birthday.status.scheduler"), await SchedulerTextAsync(), false),
            };
            await ReplyEmbedAsync(new MessageEmbed(await T("birthday.status.title"), null, null, fields, null, null, NeutralColor));
        }

        [SlashCommand("doctor", "Check channel, role hierarchy, permissions, database and scheduler")]
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
            var failed = checks.Count(c => c.State == BirthdayCheckState.Problem);
            var summary = await T(failed == 0 ? "birthday.doctor.summary_ok" : "birthday.doctor.summary_failed", failed);
            await ReplyEmbedAsync(new MessageEmbed(await T("birthday.doctor.title"), Clip(summary + "\n\n" + string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
                null, [], null, null, failed == 0 ? NeutralColor : WarningColor));
        }

        private async Task<string> AnnouncementTextAsync(BirthdayAnnouncementState? state) => state switch
        {
            null => await T("birthday.status.announcement_none"),
            BirthdayAnnouncementState.Queued => await T("birthday.status.announcement_queued"),
            BirthdayAnnouncementState.Sent => await T("birthday.status.announcement_sent"),
            BirthdayAnnouncementState.Simulated => await T("birthday.status.announcement_simulated"),
            _ => await T("birthday.status.announcement_failed"),
        };

        private async Task<string> SchedulerTextAsync()
        {
            if (health.WorkerStartedAt is null)
                return await T("birthday.status.scheduler_not_running");
            if (health.LastCompletedAt is not { } done)
                return await T("birthday.status.scheduler_no_pass");
            return await T("birthday.status.scheduler_value", DiscordText.Timestamp(done, 'R'), health.ConsecutiveFailures);
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
}
