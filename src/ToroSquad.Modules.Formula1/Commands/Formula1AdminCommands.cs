using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Providers;
using DiscordPermission = Discord.GuildPermission;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Formula1.Commands;

/// <summary>
/// /f1-admin — Manage Server required: hidden by default_member_permissions AND re-authorized in every service call
/// (a bypassed Discord permission check still fails server-side). Works while the module is disabled so setup can
/// precede activation.
/// </summary>
[ToroModule(Formula1Module.ModuleIdValue, AllowWhenDisabled = true)]
[Group("f1-admin", "Formula 1 settings for this server (admins)")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class Formula1AdminCommands(
    InteractionServices services,
    Formula1ConfigService config,
    Formula1Doctor doctor,
    Formula1PreviewService preview,
    Formula1Cache cache,
    F1Sources sources,
    F1DataMode mode,
    IModuleGate gate) : ToroInteractionModule(services)
{
    [SlashCommand("preview", "Ping-free TEST/DEMO preview of a Formula 1 card")]
    public async Task PreviewAsync(
        [Summary("card", "Which card"), Choice("practice-start", "practice-start"), Choice("race-start", "race-start"), Choice("practice-result", "practice-result"),
         Choice("race-result", "race-result"), Choice("race-result-standings", "race-result-standings")] string card = "race-result-standings")
    {
        await DeferEphemeralAsync();
        var kind = card switch
        {
            "practice-start" => F1PreviewKind.PracticeStart,
            "race-start" => F1PreviewKind.RaceStart,
            "practice-result" => F1PreviewKind.PracticeResult,
            "race-result" => F1PreviewKind.RaceResult,
            _ => F1PreviewKind.RaceResultStandings,
        };
        var result = await preview.BuildAsync(Actor, await LangAsync(), kind, CancellationToken.None);
        if (!result.Auth.Succeeded || result.Message is null)
        {
            await ReplyResultAsync(result.Auth);
            return;
        }

        var note = await T("f1.preview.note") + "\n" + (result.WouldPing.Count == 0
            ? await T("f1.preview.no_pings")
            : await T("f1.preview.would_ping", string.Join(" ", result.WouldPing.Select(DiscordText.RoleMention))));
        // Ephemeral + allowed_mentions none: a preview can never ping anyone.
        await SendEphemeralAsync(note, DiscordConversions.ToEmbed(result.Message.Embed), null);
    }

    [SlashCommand("status", "Current Formula 1 configuration of this server")]
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
        var enabled = await gate.IsEnabledAsync(Actor.GuildId, Formula1Module.ModuleIdTyped, CancellationToken.None);
        string YesNo(bool v) => Localizer.Get(lang, v ? "common.yes" : "common.no");
        var fields = new List<EmbedField>
        {
            new(await T("f1.status.module"), Localizer.Get(lang, enabled ? "modules.state_on" : "modules.state_off"), true),
            new(await T("f1.status.channel"), c?.ChannelId is { } ch ? "<#" + ch.ToString(CultureInfo.InvariantCulture) + ">" : "—", true),
            new(await T("f1.status.paused"), YesNo(c?.Paused ?? false), true),
            new(await T("f1.status.role"), c?.PingRoleId is { } r ? DiscordText.RoleMention(new RoleId(r)) + " · " + await T("f1.status.role_pings", YesNo(c.PingOnStarts), YesNo(c.PingOnResults)) : "—", false),
            new(await T("f1.status.spoilers"), YesNo(c?.SpoilerMode ?? false), true),
            new(await T("f1.status.mode"), mode.IsDemo ? "FIXTURE / TEST-DEMO" : "LIVE", true),
            new(await T("f1.status.lifecycle"), sources.LifecycleConfigured ? cache.Live.State.ToString() : await T("f1.status.lifecycle_not_configured"), true),
        };
        if (c is not null)
        {
            fields.Add(new(await T("f1.status.notifications"), string.Join("\n",
            [
                await T("f1.status.line", await T("f1.category.practice"), YesNo(c.NotifyPracticeStart), YesNo(c.NotifyPracticeResults)),
                await T("f1.status.line", await T("f1.category.sprint"), YesNo(c.NotifySprintStart), YesNo(c.NotifySprintResults)),
                await T("f1.status.line", await T("f1.category.race"), YesNo(c.NotifyRaceStart), YesNo(c.NotifyRaceResults)),
                await T("f1.status.line", await T("f1.category.qualifying"), YesNo(c.NotifyQualifyingStart), YesNo(c.NotifyQualifyingResults)),
                await T("f1.status.line", await T("f1.category.sprint_qualifying"), YesNo(c.NotifySprintQualifyingStart), YesNo(c.NotifySprintQualifyingResults)),
                await T("f1.status.standings_line", YesNo(c.NotifyStandings)),
            ]), false));
        }

        await ReplyEmbedAsync(new MessageEmbed(await T("f1.status.title"), c is null ? await T("f1.status.not_configured") : null, null, fields, null, null, NeutralColor));
    }

    [SlashCommand("doctor", "Diagnose permissions, data providers, live connection and delivery")]
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
        await ReplyEmbedAsync(new MessageEmbed(await T("f1.doctor.title"), Formula1NotificationRenderer.Clip(string.Join("\n", lines), DiscordLimits.EmbedDescriptionMax),
            null, [], null, null, NeutralColor));
    }

    [SlashCommand("pause", "Pause Formula 1 notifications in this server")]
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

    private static string Icon(F1CheckState state) => state switch
    {
        F1CheckState.Ok => "✅",
        F1CheckState.Warning => "⚠️",
        F1CheckState.Problem => "❌",
        _ => "ℹ️",
    };

    [ToroModule(Formula1Module.ModuleIdValue, AllowWhenDisabled = true)]
    [Group("configure", "Formula 1 notification settings")]
    public sealed class ConfigureCommands(InteractionServices services, Formula1ConfigService config) : ToroInteractionModule(services)
    {
        [SlashCommand("channel", "Channel for automatic Formula 1 notifications")]
        public async Task ChannelAsync([Summary("channel", "Notification channel"), ChannelTypes(ChannelType.Text, ChannelType.News)] IChannel channel)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetChannelAsync(Actor, channel.Id, CancellationToken.None));
        }

        [SlashCommand("notifications", "Which Formula 1 notifications are sent")]
        public async Task NotificationsAsync(
            [Summary("practice_start", "Practice session started")] bool? practiceStart = null,
            [Summary("practice_results", "Practice classification")] bool? practiceResults = null,
            [Summary("sprint_start", "Sprint started")] bool? sprintStart = null,
            [Summary("sprint_results", "Sprint result")] bool? sprintResults = null,
            [Summary("race_start", "Race started")] bool? raceStart = null,
            [Summary("race_results", "Race result")] bool? raceResults = null,
            [Summary("standings", "Championship standings on sprint/race results")] bool? standings = null,
            [Summary("qualifying_start", "Qualifying started")] bool? qualifyingStart = null,
            [Summary("qualifying_results", "Qualifying classification")] bool? qualifyingResults = null,
            [Summary("sprint_qualifying_start", "Sprint qualifying started")] bool? sprintQualifyingStart = null,
            [Summary("sprint_qualifying_results", "Sprint qualifying classification")] bool? sprintQualifyingResults = null)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetNotificationsAsync(Actor, new F1NotificationChanges(practiceStart, practiceResults, sprintStart, sprintResults,
                raceStart, raceResults, standings, qualifyingStart, qualifyingResults, sprintQualifyingStart, sprintQualifyingResults), CancellationToken.None));
        }

        [SlashCommand("role", "Optional role pinged by Formula 1 notifications (never @everyone)")]
        public async Task RoleAsync(
            [Summary("ping_role", "Role to ping (leave empty to keep the current one)")] IRole? pingRole = null,
            [Summary("ping_starts", "Ping on session starts")] bool? pingStarts = null,
            [Summary("ping_results", "Ping on results")] bool? pingResults = null,
            [Summary("clear", "Remove the notification role")] bool clear = false)
        {
            await DeferEphemeralAsync();
            var current = await config.GetAsync(Actor.GuildId, CancellationToken.None);
            var roleId = clear ? null : pingRole?.Id ?? current?.PingRoleId;
            await ReplyResultAsync(await config.SetRoleAsync(Actor, roleId, pingStarts, pingResults, CancellationToken.None));
        }

        [SlashCommand("spoilers", "Hide classifications and standings behind spoilers")]
        public async Task SpoilersAsync([Summary("enabled", "Spoiler mode")] bool enabled)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetSpoilersAsync(Actor, enabled, CancellationToken.None));
        }
    }
}
