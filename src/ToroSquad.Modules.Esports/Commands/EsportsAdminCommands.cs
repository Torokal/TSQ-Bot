using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using DiscordPermission = Discord.GuildPermission;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Esports.Commands;

/// <summary>
/// /esports-admin — Manage Server required (hidden by default_member_permissions AND re-authorized in every
/// service call). Configuration works while the module is still disabled so setup can precede activation; the
/// public role panel requires the module to be enabled.
/// </summary>
[ToroModule(EsportsModule.ModuleIdValue, AllowWhenDisabled = true)]
[Group("esports-admin", "Esports settings for this server (admins)")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class EsportsAdminCommands(
    InteractionServices services,
    EsportsConfigService config,
    EsportsDoctor doctor,
    EsportsPreviewService preview,
    RoleMappingService roleMappings,
    IModuleGate gate) : ToroInteractionModule(services)
{
    [SlashCommand("configure", "Notification channel and notification types")]
    public async Task ConfigureAsync(
        [Summary("channel", "Channel for automatic notifications"), ChannelTypes(ChannelType.Text, ChannelType.News)] IChannel? channel = null,
        [Summary("reminders", "Send planned-start reminders")] bool? reminders = null,
        [Summary("reminder_minutes", "Minutes before the planned start (1-120)"), MinValue(1), MaxValue(120)] int? reminderMinutes = null,
        [Summary("results", "Send result notifications")] bool? results = null,
        [Summary("spoilers", "Hide scores in result notifications")] bool? spoilers = null)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await config.ConfigureAsync(Actor, channel?.Id, reminders, reminderMinutes, results, spoilers, CancellationToken.None));
    }

    [SlashCommand("panel", "Post a public panel where members can follow teams / get notification roles")]
    public async Task PanelAsync()
    {
        var auth = Authorize.Require(Actor, Actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
        {
            await ReplyResultAsync(OperationResult.Forbidden(auth));
            return;
        }

        if (!await gate.IsEnabledAsync(Actor.GuildId, EsportsModule.ModuleIdTyped, CancellationToken.None))
        {
            await ReplyTextAsync("error.module_disabled");
            return;
        }

        var mappings = (await roleMappings.ListAsync(Actor.GuildId, CancellationToken.None)).Where(m => m.SelfService).Take(25).ToList();
        if (mappings.Count == 0)
        {
            await ReplyTextAsync("esports.panel.no_selfservice");
            return;
        }

        var builder = new ComponentBuilder();
        for (var i = 0; i < mappings.Count; i++)
        {
            var label = mappings[i].TeamKey.Length == 0 ? await T("esports.all_matches") : DiscordText.UntrustedPlain(mappings[i].TeamName ?? mappings[i].TeamKey, 70);
            builder.WithButton(label, EsportsCommands.PanelPrefix + mappings[i].Id.ToString(CultureInfo.InvariantCulture), ButtonStyle.Secondary, row: i / 5);
        }

        var embed = DiscordConversions.ToEmbed(new MessageEmbed(await T("esports.panel.title"), await T("esports.panel.description"), null, [], null, null, BrandColor));
        await RespondAsync(embed: embed, components: builder.Build(), ephemeral: false, allowedMentions: DiscordConversions.ToAllowedMentions(MentionPolicy.None));
    }

    [SlashCommand("preview", "Ping-free preview of a notification for this server")]
    public async Task PreviewAsync()
    {
        await DeferEphemeralAsync();
        var result = await preview.BuildAsync(Actor, await LangAsync(), CancellationToken.None);
        if (!result.Auth.Succeeded || result.Message is null)
        {
            await ReplyResultAsync(result.Auth);
            return;
        }

        var note = await T(result.UsedSample ? "esports.preview.sample_note" : "esports.preview.real_note");
        note += "\n" + (result.WouldPing.Count == 0
            ? await T("esports.preview.no_pings")
            : await T("esports.preview.would_ping", string.Join(" ", result.WouldPing.Select(DiscordText.RoleMention))));
        // Ephemeral + allowed_mentions none: a preview can never ping anyone.
        await SendEphemeralAsync(note, DiscordConversions.ToEmbed(result.Message.Embed), null);
    }

    [SlashCommand("pause", "Pause esports notifications in this server")]
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

    [SlashCommand("doctor", "Diagnose permissions, data provider and delivery")]
    public async Task DoctorAsync()
    {
        await DeferEphemeralAsync();
        var (auth, checks) = await doctor.RunAsync(Actor, CancellationToken.None);
        if (!auth.Succeeded)
        {
            await ReplyResultAsync(auth);
            return;
        }

        var language = await LangAsync();
        var lines = checks.Select(c => $"{Icon(c.State)} **{Localizer.Get(language, c.LabelKey)}** — {Localizer.Get(language, c.DetailKey, c.Args.ToArray())}");
        await ReplyEmbedAsync(new MessageEmbed(await T("doctor.title"), string.Join("\n", lines), null, [], null, null, NeutralColor));
    }

    private static string Icon(CheckState state) => state switch
    {
        CheckState.Ok => "✅",
        CheckState.Warning => "⚠️",
        CheckState.Problem => "❌",
        _ => "ℹ️",
    };

    [ToroModule(EsportsModule.ModuleIdValue, AllowWhenDisabled = true)]
    [Group("filters", "Which matches are announced in this server")]
    public sealed class FilterCommands(InteractionServices services, EsportsConfigService config, EsportsCache cache) : ToroInteractionModule(services)
    {
        [SlashCommand("show", "Show active filters and how they combine")]
        public async Task ShowAsync()
        {
            var auth = Authorize.Require(Actor, Actor.GuildId, Authorize.ServerSettings);
            if (!auth.IsAllowed)
            {
                await ReplyResultAsync(OperationResult.Forbidden(auth));
                return;
            }

            await DeferEphemeralAsync();
            var view = await config.GetAsync(Actor.GuildId, CancellationToken.None);
            var language = await LangAsync();
            string Values(FilterDimension d, IEnumerable<string> values) =>
                string.Join(", ", values.Select(v => DiscordText.Untrusted(view.FilterLabels.GetValueOrDefault((d, v)) ?? (d == FilterDimension.Team ? TeamLabel(v) : null) ?? v, 60)));
            var fields = new List<EmbedField>
            {
                new(await T("esports.filters.team"), Or(Values(FilterDimension.Team, view.Filters.TeamKeys)), false),
                new(await T("esports.filters.tournament"), Or(Values(FilterDimension.Tournament, view.Filters.TournamentKeys)), false),
                new(await T("esports.filters.tier"), Or(string.Join(", ", view.Filters.Tiers.Order().Select(t => Localizer.Get(language, "esports.tier." + t)))), false),
                new("VRS", view.VrsTopN is { } n ? await T("esports.filters.vrs_value", n) : "—", false),
            };
            await ReplyEmbedAsync(new MessageEmbed(await T("esports.filters.title"), await T("esports.filters.rules"), null, fields, null, null, NeutralColor));
        }

        [SlashCommand("team", "Add or remove a team filter")]
        public async Task TeamAsync(
            [Summary("action", "add or remove"), Choice("add", "add"), Choice("remove", "remove")] string action,
            [Summary("team", "Team"), Autocomplete(typeof(AdminTeamAutocomplete))] string team)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetFilterAsync(Actor, FilterDimension.Team, team, TeamLabel(team), action == "add", CancellationToken.None));
        }

        [SlashCommand("tournament", "Add or remove a tournament filter")]
        public async Task TournamentAsync(
            [Summary("action", "add or remove"), Choice("add", "add"), Choice("remove", "remove")] string action,
            [Summary("tournament", "Tournament"), Autocomplete(typeof(TournamentAutocomplete))] string tournament)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetFilterAsync(Actor, FilterDimension.Tournament, tournament, null, action == "add", CancellationToken.None));
        }

        [SlashCommand("tier", "Add or remove a Liquipedia tier filter")]
        public async Task TierAsync(
            [Summary("action", "add or remove"), Choice("add", "add"), Choice("remove", "remove")] string action,
            [Summary("tier", "Liquipedia tier"), Choice("S-Tier", "1"), Choice("A-Tier", "2"), Choice("B-Tier", "3"), Choice("C-Tier", "4"), Choice("D-Tier", "5")] string tier)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetFilterAsync(Actor, FilterDimension.Tier, tier, null, action == "add", CancellationToken.None));
        }

        [SlashCommand("vrs", "Only matches with at least one VRS Top-N team (0 = off)")]
        public async Task VrsAsync([Summary("top", "Top N (0 disables)"), MinValue(0), MaxValue(400)] int top)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.SetVrsTopNAsync(Actor, top == 0 ? null : top, CancellationToken.None));
        }

        [SlashCommand("clear", "Remove all filters (announce every CS2 match)")]
        public async Task ClearAsync()
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await config.ClearFiltersAsync(Actor, CancellationToken.None));
        }

        private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

        private string? TeamLabel(string key) => cache.Teams.FirstOrDefault(t => t.Key == key)?.Name;
    }

    [ToroModule(EsportsModule.ModuleIdValue, AllowWhenDisabled = true)]
    [Group("roles", "Notification role mappings (safe, explicit)")]
    public sealed class RoleCommands(InteractionServices services, RoleMappingService roleMappings) : ToroInteractionModule(services)
    {
        [SlashCommand("list", "Show notification role mappings")]
        public async Task ListAsync()
        {
            var auth = Authorize.Require(Actor, Actor.GuildId, Authorize.ServerSettings);
            if (!auth.IsAllowed)
            {
                await ReplyResultAsync(OperationResult.Forbidden(auth));
                return;
            }

            await DeferEphemeralAsync();
            var rows = await roleMappings.ListAsync(Actor.GuildId, CancellationToken.None);
            var language = await LangAsync();
            var lines = rows.Count == 0
                ? [await T("esports.roles.none")]
                : rows.Select(r => Localizer.Get(language, "esports.roles.line", r.Id, DiscordText.RoleMention(new RoleId(r.RoleId)),
                    r.TeamKey.Length == 0 ? Localizer.Get(language, "esports.all_matches") : DiscordText.Untrusted(r.TeamName ?? r.TeamKey, 60),
                    Localizer.Get(language, r.PingOnReminder ? "common.yes" : "common.no"),
                    Localizer.Get(language, r.PingOnResult ? "common.yes" : "common.no"),
                    Localizer.Get(language, r.SelfService ? "common.yes" : "common.no"))).ToList();
            await ReplyEmbedAsync(new MessageEmbed(await T("esports.roles.title"), string.Join("\n", lines) + "\n\n" + await T("esports.roles.explain"), null, [], null, null, NeutralColor));
        }

        [SlashCommand("map", "Use an existing role as a notification ping target")]
        public async Task MapAsync(
            [Summary("role", "Existing role")] IRole role,
            [Summary("team", "Only this team's matches (empty = all announced matches)"), Autocomplete(typeof(AdminTeamAutocomplete))] string? team = null,
            [Summary("ping_reminder", "Ping on planned-start reminders")] bool pingReminder = true,
            [Summary("ping_result", "Ping on results")] bool pingResult = false)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await roleMappings.MapAsync(Actor, new RoleId(role.Id), team, pingReminder, pingResult, CancellationToken.None));
        }

        [SlashCommand("unmap", "Remove a role mapping")]
        public async Task UnmapAsync([Summary("mapping", "Mapping"), Autocomplete(typeof(MappingAutocomplete))] long mapping)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await roleMappings.UnmapAsync(Actor, mapping, CancellationToken.None));
        }

        [SlashCommand("selfservice", "Allow/disallow members to self-assign a mapped role (safety-checked)")]
        public async Task SelfServiceAsync(
            [Summary("mapping", "Mapping"), Autocomplete(typeof(MappingAutocomplete))] long mapping,
            [Summary("enabled", "Allow self-service")] bool enabled)
        {
            await DeferEphemeralAsync();
            await ReplyResultAsync(await roleMappings.SetSelfServiceAsync(Actor, mapping, enabled, CancellationToken.None));
        }
    }
}
