using System.Globalization;
using Discord;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Esports.Commands;

/// <summary>
/// <c>/tsq-admin modul:esports</c> — Manage Server required (the shared command is hidden by default_member_permissions AND
/// every service call re-authorizes; role operations need Manage Roles too). Configuration works while the module is still
/// disabled so setup can precede activation; the public role panel requires the module to be enabled. Team and tournament
/// pickers search the same sources as before (team directory, cached tournaments) and page long lists instead of cutting them.
/// </summary>
public sealed class EsportsAdminOperations(
    EsportsConfigService config,
    EsportsDoctor doctor,
    EsportsPreviewService preview,
    RoleMappingService roleMappings,
    TeamDirectory teams,
    EsportsCache cache,
    IModuleGate gate,
    IServiceProvider services) : IAdminFormHandler
{
    public static readonly AdminModule Definition = AdminModule.For<EsportsAdminOperations>(EsportsModule.AdminId, EsportsModule.ModuleIdTyped)
        .Op("configure", (h, c) => h.ConfigureAsync(c), AdminFields.Channel)
        .Op("panel", (h, c) => h.PanelAsync(c))
        .Op("preview", (h, c) => h.PreviewAsync(c))
        .Op("pause", (h, c) => h.PauseAsync(c, true))
        .Op("resume", (h, c) => h.PauseAsync(c, false))
        .Op("doctor", (h, c) => h.DoctorAsync(c))
        .Op("filters-show", (h, c) => h.FiltersShowAsync(c))
        .Op("filters-team", (_, c) => FilterMenuAsync(c))
        .Op("filters-tournament", (_, c) => FilterMenuAsync(c))
        .Op("filters-tier", (h, c) => h.TierAsync(c))
        .Op("filters-vrs", (h, c) => h.VrsAsync(c))
        .Op("filters-clear", (h, c) => h.ClearAsync(c))
        .Op("roles-list", (h, c) => h.RolesListAsync(c))
        .Op("roles-map", (_, c) => MapAsync(c), AdminFields.Role, Authorize.RoleSettings)
        .Op("roles-unmap", (h, c) => h.MappingPickerAsync(c), permission: Authorize.RoleSettings)
        .Op("roles-selfservice", (h, c) => h.MappingPickerAsync(c), permission: Authorize.RoleSettings)
        .Build();

    private const int PageSize = 25;
    private const string SaveAction = "save";
    private const string AddAction = "add";
    private const string SearchAction = "search";
    private const string AddPickAction = "addpick";
    private const string RemoveAction = "remove";
    private const string RemovePickAction = "removepick";
    private const string PagePrefix = "page-";
    private const string MappingAction = "mapping";
    private const string TeamAction = "team";
    private const string OnAction = "on";
    private const string OffAction = "off";
    private const string ChannelField = "channel";
    private const string MinutesField = "minutes";
    private const string QueryField = "query";
    private const string TopField = "top";

    private static readonly (string Label, string Value)[] Tiers = [("S-Tier", "1"), ("A-Tier", "2"), ("B-Tier", "3"), ("C-Tier", "4"), ("D-Tier", "5")];

    private sealed record ConfigureSnapshot(ulong? Channel, IReadOnlyDictionary<string, bool> Switches, int Minutes);

    private sealed record MapState(ulong Role, bool PingReminder, bool PingResult);

    private sealed record MappingState(long? Mapping);

    // ------------------------------------------------------------------ configure

    /// <summary>
    /// With <c>kanal</c> only the channel changes (as before with only <c>channel</c>). Without it, one private form shows the
    /// channel, the three switches and the reminder lead time as stored; only what changes there is written.
    /// </summary>
    public async Task ConfigureAsync(AdminCall call)
    {
        if (call.Args.ChannelId is { } channel)
        {
            await call.DeferAsync();
            await call.ReplyResultAsync(await config.ConfigureAsync(call.Actor, channel, null, null, null, null, CancellationToken.None));
            return;
        }

        var view = await config.GetAsync(call.Actor.GuildId, CancellationToken.None);
        var switches = new Dictionary<string, bool> { ["reminders"] = view.NotifyReminders, ["results"] = view.NotifyResults, ["spoilers"] = view.SpoilerMode };
        var draft = call.OpenDraft(new ConfigureSnapshot(view.ChannelId, switches, view.ReminderLeadMinutes));
        var channelSelect = new SelectMenuBuilder().WithType(ComponentType.ChannelSelect).WithCustomId(ChannelField).WithChannelTypes(ChannelType.Text, ChannelType.News)
            .WithMinValues(0).WithMaxValues(1).WithRequired(false);
        if (view.ChannelId is { } current)
            channelSelect.WithDefaultValues(new SelectMenuDefaultValue(current, SelectDefaultValueType.Channel));
        var boxes = new CheckboxGroupBuilder().WithCustomId(AdminForms.SwitchesField).WithMinValues(0).WithMaxValues(3).WithRequired(false);
        foreach (var (name, on) in switches)
            boxes.AddOption(AdminForms.Cut(call.T("admin.esports.configure." + name), 100), name, null, on);
        var minutes = new TextInputBuilder().WithCustomId(MinutesField).WithStyle(TextInputStyle.Short).WithMaxLength(3).WithRequired(true)
            .WithValue(view.ReminderLeadMinutes.ToString(CultureInfo.InvariantCulture));
        await call.Respond.ModalAsync(new ModalBuilder().WithTitle(AdminForms.Cut(call.T("admin.esports.configure.form_title"), 45))
            .WithCustomId(AdminCall.CustomId(draft, SaveAction))
            .AddLabel(AdminForms.Cut(call.T("admin.esports.configure.channel"), 45), channelSelect)
            .AddLabel(AdminForms.Cut(call.T("admin.esports.configure.switches"), 45), boxes)
            .AddLabel(AdminForms.Cut(call.T("admin.esports.configure.reminder_minutes"), 45), minutes)
            .Build());
    }

    private async Task SaveConfigureAsync(AdminCall call, ConfigureSnapshot snapshot)
    {
        var picked = call.Input.Id(ChannelField);
        if (picked is not null && !AdminRouter.IsUsableChannel(call.Input.Channels.FirstOrDefault(c => c.Id == picked), call.Actor.GuildId))
        {
            await call.ReplyTextAsync("admin.error.channel_invalid");
            return;
        }

        if (!int.TryParse(call.Input.Text(MinutesField), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            await call.ReplyTextAsync("admin.esports.configure.minutes_invalid");
            return;
        }

        if (!await AdminForms.ClaimAsync(call))
            return;
        var changed = AdminForms.ChangedSwitches(call, snapshot.Switches);
        bool? Get(string name) => changed.TryGetValue(name, out var value) ? value : null;
        var channel = picked is { } id && id != snapshot.Channel ? id : (ulong?)null;
        int? lead = minutes != snapshot.Minutes ? minutes : null;
        if (channel is null && lead is null && changed.Count == 0)
        {
            await call.FinishTextAsync(call.T("admin.form.no_changes"));
            return;
        }

        await call.FinishAsync(await config.ConfigureAsync(call.Actor, channel, Get("reminders"), lead, Get("results"), Get("spoilers"), CancellationToken.None));
    }

    // ------------------------------------------------------------------ simple operations

    /// <summary>The public follow panel: deliberately a normal (non-private) message, as before; only when the module is on.</summary>
    public async Task PanelAsync(AdminCall call)
    {
        if (!await gate.IsEnabledAsync(call.Actor.GuildId, EsportsModule.ModuleIdTyped, CancellationToken.None))
        {
            await call.ReplyTextAsync("error.module_disabled");
            return;
        }

        var mappings = (await roleMappings.ListAsync(call.Actor.GuildId, CancellationToken.None)).Where(m => m.SelfService).Take(25).ToList();
        if (mappings.Count == 0)
        {
            await call.ReplyTextAsync("esports.panel.no_selfservice");
            return;
        }

        var builder = new ComponentBuilder();
        for (var i = 0; i < mappings.Count; i++)
        {
            var label = mappings[i].TeamKey.Length == 0 ? call.T("esports.all_matches") : DiscordText.UntrustedPlain(mappings[i].TeamName ?? mappings[i].TeamKey, 70);
            builder.WithButton(label, EsportsCommands.PanelPrefix + mappings[i].Id.ToString(CultureInfo.InvariantCulture), ButtonStyle.Secondary, row: i / 5);
        }

        await call.Respond.SendAsync(null, new MessageEmbed(call.T("esports.panel.title"), call.T("esports.panel.description"), null, [], null, null,
            ToroInteractionModule.BrandColor), builder.Build(), ephemeral: false);
    }

    public async Task PreviewAsync(AdminCall call)
    {
        await call.DeferAsync();
        var result = await preview.BuildAsync(call.Actor, call.Language, CancellationToken.None);
        if (!result.Auth.Succeeded || result.Message is null)
        {
            await call.ReplyResultAsync(result.Auth);
            return;
        }

        var note = call.T(result.UsedSample ? "esports.preview.sample_note" : "esports.preview.real_note");
        note += "\n" + (result.WouldPing.Count == 0
            ? call.T("esports.preview.no_pings")
            : call.T("esports.preview.would_ping", string.Join(" ", result.WouldPing.Select(DiscordText.RoleMention))));
        // Private + allowed_mentions none: a preview can never ping anyone.
        await call.Respond.SendAsync(note, result.Message.Embed);
    }

    public async Task PauseAsync(AdminCall call, bool pause)
    {
        await call.DeferAsync();
        await call.ReplyResultAsync(await config.PauseAsync(call.Actor, pause, CancellationToken.None));
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
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("doctor.title"), string.Join("\n", lines), null, [], null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task FiltersShowAsync(AdminCall call)
    {
        await call.DeferAsync();
        var view = await config.GetAsync(call.Actor.GuildId, CancellationToken.None);
        string Values(FilterDimension d, IEnumerable<string> values) =>
            string.Join(", ", values.Select(v => DiscordText.Untrusted(view.FilterLabels.GetValueOrDefault((d, v)) ?? (d == FilterDimension.Team ? TeamLabel(v) : null) ?? v, 60)));
        var fields = new List<EmbedField>
        {
            new(call.T("esports.filters.team"), Or(Values(FilterDimension.Team, view.Filters.TeamKeys)), false),
            new(call.T("esports.filters.tournament"), Or(Values(FilterDimension.Tournament, view.Filters.TournamentKeys)), false),
            new(call.T("esports.filters.tier"), Or(string.Join(", ", view.Filters.Tiers.Order().Select(t => call.T("esports.tier." + t)))), false),
            new("VRS", view.VrsTopN is { } n ? call.T("esports.filters.vrs_value", n) : "—", false),
        };
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("esports.filters.title"), call.T("esports.filters.rules"), null, fields, null, null, ToroInteractionModule.NeutralColor));
    }

    public async Task ClearAsync(AdminCall call)
    {
        await call.DeferAsync();
        await call.ReplyResultAsync(await config.ClearFiltersAsync(call.Actor, CancellationToken.None));
    }

    public async Task RolesListAsync(AdminCall call)
    {
        await call.DeferAsync();
        var rows = await roleMappings.ListAsync(call.Actor.GuildId, CancellationToken.None);
        var lines = rows.Count == 0
            ? [call.T("esports.roles.none")]
            : rows.Select(r => call.T("esports.roles.line", r.Id, DiscordText.RoleMention(new RoleId(r.RoleId)),
                r.TeamKey.Length == 0 ? call.T("esports.all_matches") : DiscordText.Untrusted(r.TeamName ?? r.TeamKey, 60),
                AdminForms.YesNo(call, r.PingOnReminder), AdminForms.YesNo(call, r.PingOnResult), AdminForms.YesNo(call, r.SelfService))).ToList();
        await call.ReplyEmbedAsync(new MessageEmbed(call.T("esports.roles.title"), string.Join("\n", lines) + "\n\n" + call.T("esports.roles.explain"), null, [], null, null,
            ToroInteractionModule.NeutralColor));
    }

    // ------------------------------------------------------------------ team / tournament filters

    /// <summary>Add (search, then pick) or remove (pick from the current filters, paged) a team or tournament filter.</summary>
    public static Task FilterMenuAsync(AdminCall call) =>
        AdminForms.ButtonsAsync(call, call.T("admin.esports." + call.Operation.Id + ".pick"),
            [(call.T("admin.esports.filters.add"), AddAction), (call.T("admin.esports.filters.remove"), RemoveAction)]);

    private static FilterDimension Dimension(AdminCall call) => call.Operation.Id == "filters-team" ? FilterDimension.Team : FilterDimension.Tournament;

    private async Task FilterFormAsync(AdminCall call, string action)
    {
        var dimension = Dimension(call);
        switch (action)
        {
            case AddAction:
                await AdminForms.TextModalAsync(call, call.Draft!, SearchAction, call.T("admin.esports." + call.Operation.Id + ".search_title"),
                    call.T("admin.esports.filters.search_label"), QueryField, null, 100, placeholder: call.T("admin.esports.filters.search_placeholder"));
                return;
            case SearchAction:
                var typed = TeamRankingResolver.Fold(call.Input.Text(QueryField) ?? "");
                var found = dimension == FilterDimension.Team
                    ? (await teams.SuggestAsync(typed, CancellationToken.None)).Where(t => t.Key.Length <= 100).Select(t => (t.Display, t.Key)).ToList()
                    : TournamentAutocomplete.Search(cache, typed).ToList();
                await AdminForms.ChooseAsync(call, call.T(found.Count == 0 ? "admin.esports.filters.none_found" : "admin.esports.filters.found", found.Count),
                    AddPickAction, found.Take(PageSize).Select(f => (DiscordText.UntrustedPlain(f.Item1, 100), f.Item2, false)),
                    buttons: [(call.T("admin.esports.filters.search_again"), AddAction)], reuse: call.Draft);
                return;
            case AddPickAction:
                if (call.Input.FirstValue is not { } key || !await AdminForms.ClaimAsync(call))
                    return;
                await call.FinishAsync(await config.SetFilterAsync(call.Actor, dimension, key, dimension == FilterDimension.Team ? TeamLabel(key) : null,
                    true, CancellationToken.None));
                return;
            case RemovePickAction:
                if (call.Input.FirstValue is not { } removed || !await AdminForms.ClaimAsync(call))
                    return;
                await call.FinishAsync(await config.SetFilterAsync(call.Actor, dimension, removed, null, false, CancellationToken.None));
                return;
            default:
                var page = action == RemoveAction ? 0 : PageOf(action);
                var view = await config.GetAsync(call.Actor.GuildId, CancellationToken.None);
                var current = (dimension == FilterDimension.Team ? view.Filters.TeamKeys : view.Filters.TournamentKeys).Order(StringComparer.Ordinal)
                    .Select(v => (Label: view.FilterLabels.GetValueOrDefault((dimension, v)) ?? (dimension == FilterDimension.Team ? TeamLabel(v) : null) ?? v, Value: v)).ToList();
                await PagedAsync(call, current.Count == 0 ? call.T("admin.esports.filters.none_active") : call.T("admin.esports.filters.remove_pick"),
                    RemovePickAction, current, page);
                return;
        }
    }

    // ------------------------------------------------------------------ tier / VRS

    /// <summary>Two selects: tiers to add (not active) and tiers to remove (active).</summary>
    public async Task TierAsync(AdminCall call)
    {
        var active = (await config.GetAsync(call.Actor.GuildId, CancellationToken.None)).Filters.Tiers.Select(t => t.ToString(CultureInfo.InvariantCulture)).ToHashSet();
        var draft = call.OpenDraft(new object());
        var builder = new ComponentBuilder();
        var toAdd = Tiers.Where(t => !active.Contains(t.Value)).ToList();
        var toRemove = Tiers.Where(t => active.Contains(t.Value)).ToList();
        if (toAdd.Count > 0)
        {
            builder.WithSelectMenu(new SelectMenuBuilder().WithCustomId(AdminCall.CustomId(draft, AddPickAction)).WithPlaceholder(call.T("admin.esports.filters-tier.add"))
                .WithOptions(toAdd.Select(t => new SelectMenuOptionBuilder(t.Label, t.Value)).ToList()), row: 0);
        }

        if (toRemove.Count > 0)
        {
            builder.WithSelectMenu(new SelectMenuBuilder().WithCustomId(AdminCall.CustomId(draft, RemovePickAction)).WithPlaceholder(call.T("admin.esports.filters-tier.remove"))
                .WithOptions(toRemove.Select(t => new SelectMenuOptionBuilder(t.Label, t.Value)).ToList()), row: 1);
        }

        builder.WithButton(call.T("admin.form.cancel"), AdminCall.CustomId(draft, AdminRouter.CancelAction), ButtonStyle.Secondary, row: 2);
        await call.Respond.SendAsync(call.T("admin.esports.filters-tier.pick"), null, builder.Build());
    }

    /// <summary>A number form pre-filled with the current Top-N (0 turns the filter off, as before).</summary>
    public async Task VrsAsync(AdminCall call)
    {
        var current = (await config.GetAsync(call.Actor.GuildId, CancellationToken.None)).VrsTopN ?? 0;
        await AdminForms.TextModalAsync(call, call.OpenDraft(new object()), SaveAction, call.T("admin.esports.filters-vrs.form_title"),
            call.T("admin.esports.filters-vrs.form_label"), TopField, current.ToString(CultureInfo.InvariantCulture), 3);
    }

    // ------------------------------------------------------------------ roles

    /// <summary>
    /// Role (pre-filled with <c>rol</c>), optional team search text (empty = all announced matches) and the two ping switches,
    /// defaulting as before (reminder on, result off). An ambiguous team text is followed by a pick list.
    /// </summary>
    public static async Task MapAsync(AdminCall call)
    {
        var draft = call.OpenDraft(new object());
        var role = new SelectMenuBuilder().WithType(ComponentType.RoleSelect).WithCustomId(AdminForms.RoleField).WithMinValues(1).WithMaxValues(1).WithRequired(true);
        if (call.Args.RoleId is { } given)
            role.WithDefaultValues(new SelectMenuDefaultValue(given, SelectDefaultValueType.Role));
        var team = new TextInputBuilder().WithCustomId(QueryField).WithStyle(TextInputStyle.Short).WithMaxLength(100).WithRequired(false)
            .WithPlaceholder(AdminForms.Cut(call.T("admin.esports.roles-map.team_placeholder"), 100));
        var pings = new CheckboxGroupBuilder().WithCustomId(AdminForms.SwitchesField).WithMinValues(0).WithMaxValues(2).WithRequired(false)
            .AddOption(AdminForms.Cut(call.T("admin.esports.roles-map.ping_reminder"), 100), "ping_reminder", null, true)
            .AddOption(AdminForms.Cut(call.T("admin.esports.roles-map.ping_result"), 100), "ping_result", null, false);
        await call.Respond.ModalAsync(new ModalBuilder().WithTitle(AdminForms.Cut(call.T("admin.esports.roles-map.form_title"), 45))
            .WithCustomId(AdminCall.CustomId(draft, SaveAction))
            .AddLabel(AdminForms.Cut(call.T("admin.esports.roles-map.role"), 45), role)
            .AddLabel(AdminForms.Cut(call.T("admin.esports.roles-map.team"), 45), team)
            .AddLabel(AdminForms.Cut(call.T("admin.esports.roles-map.pings"), 45), pings)
            .Build());
    }

    private async Task SaveMapAsync(AdminCall call)
    {
        if (call.Input.Id(AdminForms.RoleField) is not { } role)
        {
            await call.ReplyTextAsync("admin.esports.roles-map.role_missing");
            return;
        }

        var pings = call.Input.Selected(AdminForms.SwitchesField);
        var state = new MapState(role, pings.Contains("ping_reminder"), pings.Contains("ping_result"));
        var typed = TeamRankingResolver.Fold(call.Input.Text(QueryField) ?? "");
        if (typed.Length == 0)
        {
            if (await AdminForms.ClaimAsync(call))
                await call.FinishAsync(await roleMappings.MapAsync(call.Actor, new RoleId(role), null, state.PingReminder, state.PingResult, CancellationToken.None));
            return;
        }

        var found = (await teams.SuggestAsync(typed, CancellationToken.None)).Where(t => t.Key.Length <= 100).ToList();
        var exact = found.Where(t => TeamRankingResolver.Fold(t.Key) == typed || TeamRankingResolver.Fold(TeamLabel(t.Key) ?? "") == typed).ToList();
        if (exact.Count == 1)
        {
            if (await AdminForms.ClaimAsync(call))
                await call.FinishAsync(await roleMappings.MapAsync(call.Actor, new RoleId(role), exact[0].Key, state.PingReminder, state.PingResult, CancellationToken.None));
            return;
        }

        call.UpdateDraft(state);
        await AdminForms.ChooseAsync(call, call.T(found.Count == 0 ? "admin.esports.filters.none_found" : "admin.esports.roles-map.pick_team", found.Count), TeamAction,
            found.Select(t => (DiscordText.UntrustedPlain(t.Display, 100), t.Key, false)), reuse: call.Draft);
    }

    /// <summary>The server's mappings, paged (25 per page); unmap acts on the pick, self-service asks on/off next.</summary>
    public Task MappingPickerAsync(AdminCall call) => ShowMappingsAsync(call, 0, first: true);

    private async Task ShowMappingsAsync(AdminCall call, int page, bool first = false)
    {
        var rows = await roleMappings.ListAsync(call.Actor.GuildId, CancellationToken.None);
        var labels = await MappingLabels.BuildAsync(call.Actor.GuildId, rows, services);
        var options = labels.Select(l => (Label: l.Label, Value: l.Id.ToString(CultureInfo.InvariantCulture))).ToList();
        var text = call.T(options.Count == 0 ? "esports.roles.none" : "admin.esports." + call.Operation.Id + ".pick");
        if (first)
            await PagedSendAsync(call, text, MappingAction, options, page, new MappingState(null));
        else
            await PagedAsync(call, text, MappingAction, options, page);
    }

    // ------------------------------------------------------------------ forms

    public async Task OnFormAsync(AdminCall call, string action)
    {
        switch (call.Operation.Id)
        {
            case "configure" when action == SaveAction && call.Draft?.State is ConfigureSnapshot snapshot:
                await SaveConfigureAsync(call, snapshot);
                return;
            case "filters-team" or "filters-tournament":
                await FilterFormAsync(call, action);
                return;
            case "filters-tier" when action is AddPickAction or RemovePickAction:
                if (call.Input.FirstValue is not { } tier || !await AdminForms.ClaimAsync(call))
                    return;
                await call.FinishAsync(await config.SetFilterAsync(call.Actor, FilterDimension.Tier, tier, null, action == AddPickAction, CancellationToken.None));
                return;
            case "filters-vrs" when action == SaveAction:
                if (!int.TryParse(call.Input.Text(TopField), NumberStyles.None, CultureInfo.InvariantCulture, out var top) || top > 400)
                {
                    await call.ReplyTextAsync("admin.esports.filters-vrs.invalid");
                    return;
                }

                if (await AdminForms.ClaimAsync(call))
                    await call.FinishAsync(await config.SetVrsTopNAsync(call.Actor, top == 0 ? null : top, CancellationToken.None));
                return;
            case "roles-map" when action == SaveAction:
                await SaveMapAsync(call);
                return;
            case "roles-map" when action == TeamAction && call.Draft?.State is MapState map:
                if (call.Input.FirstValue is not { } teamKey || !await AdminForms.ClaimAsync(call))
                    return;
                await call.FinishAsync(await roleMappings.MapAsync(call.Actor, new RoleId(map.Role), teamKey, map.PingReminder, map.PingResult, CancellationToken.None));
                return;
            case "roles-unmap" or "roles-selfservice" when action.StartsWith(PagePrefix, StringComparison.Ordinal):
                await ShowMappingsAsync(call, PageOf(action));
                return;
            case "roles-unmap" when action == MappingAction:
                if (!long.TryParse(call.Input.FirstValue, NumberStyles.None, CultureInfo.InvariantCulture, out var unmap) || !await AdminForms.ClaimAsync(call))
                    return;
                await call.FinishAsync(await roleMappings.UnmapAsync(call.Actor, unmap, CancellationToken.None));
                return;
            case "roles-selfservice" when action == MappingAction:
                if (!long.TryParse(call.Input.FirstValue, NumberStyles.None, CultureInfo.InvariantCulture, out var mapping))
                    return;
                call.UpdateDraft(new MappingState(mapping));
                var buttons = new ComponentBuilder()
                    .WithButton(call.T("admin.form.turn_on"), AdminCall.CustomId(call.Draft!, OnAction), ButtonStyle.Primary)
                    .WithButton(call.T("admin.form.turn_off"), AdminCall.CustomId(call.Draft!, OffAction), ButtonStyle.Primary)
                    .WithButton(call.T("admin.form.cancel"), AdminCall.CustomId(call.Draft!, AdminRouter.CancelAction), ButtonStyle.Secondary);
                await call.Respond.UpdateAsync(call.T("admin.esports.roles-selfservice.on_off", "#" + mapping.ToString(CultureInfo.InvariantCulture)), buttons.Build());
                return;
            case "roles-selfservice" when action is OnAction or OffAction && call.Draft?.State is MappingState { Mapping: { } chosen }:
                if (await AdminForms.ClaimAsync(call))
                    await call.FinishAsync(await roleMappings.SetSelfServiceAsync(call.Actor, chosen, action == OnAction, CancellationToken.None));
                return;
            default:
                await call.ReplyTextAsync("admin.form.expired");
                return;
        }
    }

    /// <summary>One page of a long list as a select, with previous/next buttons (nothing becomes unreachable).</summary>
    private static Task PagedAsync(AdminCall call, string text, string action, IReadOnlyList<(string Label, string Value)> options, int page) =>
        AdminForms.ChooseAsync(call, Paged(call, text, options.Count, page), action, Page(options, page), buttons: PageButtons(call, options.Count, page), reuse: call.Draft);

    private static Task PagedSendAsync(AdminCall call, string text, string action, IReadOnlyList<(string Label, string Value)> options, int page, object state) =>
        AdminForms.ChooseAsync(call, Paged(call, text, options.Count, page), action, Page(options, page), state, PageButtons(call, options.Count, page));

    private static IEnumerable<(string, string, bool)> Page(IReadOnlyList<(string Label, string Value)> options, int page) =>
        options.Skip(Math.Clamp(page, 0, Pages(options.Count) - 1) * PageSize).Take(PageSize).Select(o => (o.Label, o.Value, false));

    private static int Pages(int count) => Math.Max(1, (count + PageSize - 1) / PageSize);

    private static string Paged(AdminCall call, string text, int count, int page) =>
        Pages(count) == 1 ? text : text + "\n" + call.T("admin.form.page", Math.Clamp(page, 0, Pages(count) - 1) + 1, Pages(count));

    private static List<(string, string)> PageButtons(AdminCall call, int count, int page)
    {
        var buttons = new List<(string, string)>();
        page = Math.Clamp(page, 0, Pages(count) - 1);
        if (page > 0)
            buttons.Add((call.T("admin.form.previous"), PagePrefix + (page - 1).ToString(CultureInfo.InvariantCulture)));
        if (page < Pages(count) - 1)
            buttons.Add((call.T("admin.form.next"), PagePrefix + (page + 1).ToString(CultureInfo.InvariantCulture)));
        return buttons;
    }

    private static int PageOf(string action) =>
        action.StartsWith(PagePrefix, StringComparison.Ordinal) && int.TryParse(action[PagePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var page) ? page : 0;

    private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private string? TeamLabel(string key) => cache.Teams.FirstOrDefault(t => t.Key == key)?.Name;

    private static string Icon(CheckState state) => state switch
    {
        CheckState.Ok => "✅",
        CheckState.Warning => "⚠️",
        CheckState.Problem => "❌",
        _ => "ℹ️",
    };
}
