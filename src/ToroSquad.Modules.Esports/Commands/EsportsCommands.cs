using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using EmbedField = ToroSquad.Core.Messaging.EmbedField;

namespace ToroSquad.Modules.Esports.Commands;

/// <summary>
/// Public CS2 commands. Answers come from the shared cache only (never a provider call on the interaction path),
/// always with source + freshness. Failures are shown as failures — never as "no matches".
/// </summary>
[ToroModule(EsportsModule.ModuleIdValue)]
[Group("esports", "Counter-Strike 2 matches, results, events and rankings")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class EsportsCommands(
    InteractionServices services,
    EsportsCache cache,
    NotificationRenderer renderer,
    IEsportsDataProvider provider,
    EsportsDataMode mode,
    DeploymentPolicy deployment,
    SubscriptionService subscriptions,
    RoleMappingService roleMappings) : ToroInteractionModule(services)
{
    public const string PanelPrefix = "tsq:esp:panel:";
    public const string PrefPrefix = "tsq:esp:pref:hide:";
    private const int MaxLines = 12;

    [SlashCommand("matches", "Upcoming CS2 matches")]
    public async Task MatchesAsync(
        [Summary("team", "Only matches of this team"), Autocomplete(typeof(TeamAutocomplete))] string? team = null,
        [Summary("tournament", "Only matches of this tournament"), Autocomplete(typeof(TournamentAutocomplete))] string? tournament = null,
        [Summary("mine", "Only teams you follow")] bool mine = false)
    {
        await DeferEphemeralAsync();
        if (!await CheckDataAsync(cache.Matches))
            return;
        var now = Services.Clock.GetUtcNow();
        var followed = mine ? (await subscriptions.ListAsync(Actor, CancellationToken.None)).Select(f => f.TeamKey).ToHashSet() : null;
        var list = cache.Matches.Data!
            .Where(m => m.Status == MatchStatus.Scheduled && m.ScheduledStartUtc is { } s && s >= now - TimeSpan.FromHours(12))
            .Where(m => team is null || m.InvolvesTeam(team))
            .Where(m => tournament is null || m.Tournament.Key == tournament || m.Tournament.ParentKey == tournament)
            .Where(m => followed is null || followed.Any(m.InvolvesTeam))
            .OrderBy(m => m.ScheduledStartUtc)
            .Take(MaxLines)
            .ToList();
        await ReplyListAsync("esports.matches.title", list.Select(m => renderer.MatchLine(m, Lang, false, now)).ToList(), "esports.matches.none", cache.Matches);
    }

    [SlashCommand("results", "Recent CS2 results (spoiler-aware)")]
    public async Task ResultsAsync(
        [Summary("team", "Only results of this team"), Autocomplete(typeof(TeamAutocomplete))] string? team = null,
        [Summary("spoiler", "Hide scores behind spoiler tags (default: your preference)")] bool? spoiler = null)
    {
        await DeferEphemeralAsync();
        if (!await CheckDataAsync(cache.Matches))
            return;
        var hide = spoiler ?? await subscriptions.GetHideResultsAsync(Actor.GuildId, Actor.UserId, CancellationToken.None);
        var now = Services.Clock.GetUtcNow();
        var list = cache.Matches.Data!
            .Where(m => m.Status is MatchStatus.Finished or MatchStatus.Cancelled)
            .Where(m => team is null || m.InvolvesTeam(team))
            .OrderByDescending(m => m.ScheduledStartUtc)
            .Take(MaxLines)
            .ToList();
        await ReplyListAsync("esports.results.title", list.Select(m => renderer.MatchLine(m, Lang, hide, now)).ToList(), "esports.results.none", cache.Matches);
    }

    [SlashCommand("events", "Ongoing and upcoming CS2 tournaments")]
    public async Task EventsAsync()
    {
        await DeferEphemeralAsync();
        if (!await CheckDataAsync(cache.Events))
            return;
        var today = DateOnly.FromDateTime(Services.Clock.GetUtcNow().UtcDateTime);
        var lines = cache.Events.Data!
            .Where(e => e.EndDate is null || e.EndDate >= today)
            .OrderBy(e => e.StartDate)
            .Take(MaxLines)
            .Select(e =>
            {
                var dates = e.StartDate is { } s ? $"{s:yyyy-MM-dd}" + (e.EndDate is { } en ? $" → {en:yyyy-MM-dd}" : "") : "?";
                var tier = e.Tournament.Tier is { } t ? Localizer.Get(Lang, "esports.tier." + t) : "";
                var place = e.Location is null ? "" : " · " + DiscordText.Untrusted(e.Location, 60);
                return $"**{DiscordText.Untrusted(e.Tournament.Name, 80)}** — {dates} {tier}{place}";
            })
            .ToList();
        await ReplyListAsync("esports.events.title", lines, "esports.events.none", cache.Events);
    }

    [SlashCommand("rankings", "Valve Regional Standings (VRS) with source and date")]
    public async Task RankingsAsync([Summary("top", "How many teams (1-50)"), MinValue(1), MaxValue(50)] int top = 20)
    {
        await DeferEphemeralAsync();
        var state = cache.Rankings;
        if (state.Data is null)
        {
            await ReplyTextAsync(state.LastOutcome is null ? "esports.rankings.not_loaded" : "esports.rankings.unavailable", state.LastOutcome?.ToString() ?? "");
            return;
        }

        if (!await DemoAllowedAsync())
            return;
        var snapshot = state.Data;
        var lines = snapshot.Entries.Take(top)
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"`#{e.Rank,3}` {DiscordText.Untrusted(e.TeamName, 60)} — {e.Points}"))
            .ToList();
        var description = string.Join("\n", lines) + "\n\n" +
                          await T(renderer.IsDemo ? "esports.rankings.source_demo" : "esports.rankings.source", snapshot.PublishedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DiscordText.Timestamp(snapshot.FetchedAt, 'R'));
        await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(Lang) + await T("esports.rankings.title"), description,
            renderer.Link(snapshot.SourceUrl), [], await T(renderer.IsDemo ? "esports.rankings.footer_demo" : "esports.rankings.footer"), snapshot.FetchedAt, NeutralColor));
    }

    [SlashCommand("team", "Team info, VRS match and upcoming/recent matches")]
    public async Task TeamAsync([Summary("team", "Team"), Autocomplete(typeof(TeamAutocomplete))] string team)
    {
        await DeferEphemeralAsync();
        if (!await CheckDataAsync(cache.Matches))
            return;
        var info = cache.Teams.FirstOrDefault(t => t.Key == team);
        if (info is null)
        {
            await ReplyTextAsync("esports.team.unknown");
            return;
        }

        var now = Services.Clock.GetUtcNow();
        var fields = new List<EmbedField>();
        var resolver = cache.Resolver;
        if (resolver is null)
        {
            fields.Add(new EmbedField("VRS", await T("esports.team.vrs_unavailable"), false));
        }
        else
        {
            var match = resolver.Resolve(info);
            var vrs = match.Kind switch
            {
                TeamMatchKind.Exact or TeamMatchKind.Alias or TeamMatchKind.Normalized => await T("esports.team.vrs_rank", match.Entry!.Rank, match.Entry.Points,
                    DiscordText.Untrusted(match.Entry.TeamName, 60), resolver.Snapshot.PublishedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                TeamMatchKind.Ambiguous => await T("esports.team.vrs_ambiguous", string.Join(", ", match.Candidates.Take(5).Select(c => DiscordText.Untrusted(c.TeamName, 40)))),
                _ => await T("esports.team.vrs_not_found"),
            };
            fields.Add(new EmbedField("VRS", vrs, false));
        }

        var hide = await subscriptions.GetHideResultsAsync(Actor.GuildId, Actor.UserId, CancellationToken.None);
        var upcoming = cache.Matches.Data!.Where(m => m.InvolvesTeam(team) && m.Status == MatchStatus.Scheduled).OrderBy(m => m.ScheduledStartUtc).Take(5)
            .Select(m => renderer.MatchLine(m, Lang, false, now)).ToList();
        var recent = cache.Matches.Data!.Where(m => m.InvolvesTeam(team) && m.Status == MatchStatus.Finished).OrderByDescending(m => m.ScheduledStartUtc).Take(5)
            .Select(m => renderer.MatchLine(m, Lang, hide, now)).ToList();
        fields.Add(new EmbedField(await T("esports.team.upcoming"), Clip(upcoming.Count > 0 ? string.Join("\n", upcoming) : await T("esports.matches.none")), false));
        fields.Add(new EmbedField(await T("esports.team.recent"), Clip(recent.Count > 0 ? string.Join("\n", recent) : await T("esports.results.none")), false));

        await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(Lang) + DiscordText.UntrustedPlain(info.Name, 200), renderer.Freshness(Lang, cache.Matches.FetchedAt!.Value),
            null, fields, renderer.Footer(Lang), cache.Matches.FetchedAt, NeutralColor));
    }

    [SlashCommand("follow", "Follow a team (you may receive the server's notification role)")]
    public async Task FollowAsync([Summary("team", "Team to follow"), Autocomplete(typeof(FollowAutocomplete))] string team)
    {
        await DeferEphemeralAsync();
        await ReplyOutcomeAsync(await subscriptions.FollowAsync(Actor, team, CancellationToken.None));
    }

    [SlashCommand("unfollow", "Stop following a team")]
    public async Task UnfollowAsync([Summary("team", "Team to unfollow"), Autocomplete(typeof(MyFollowsAutocomplete))] string team)
    {
        await DeferEphemeralAsync();
        await ReplyOutcomeAsync(await subscriptions.UnfollowAsync(Actor, team, CancellationToken.None));
    }

    [SlashCommand("subscriptions", "Your followed teams and personal notification preferences")]
    public async Task SubscriptionsAsync()
    {
        await DeferEphemeralAsync();
        var follows = await subscriptions.ListAsync(Actor, CancellationToken.None);
        var hide = await subscriptions.GetHideResultsAsync(Actor.GuildId, Actor.UserId, CancellationToken.None);
        var lines = follows.Count == 0
            ? [await T("esports.subs.none")]
            : follows.Select(f => $"• {(f.TeamKey == SubscriptionService.AllMatchesKey ? Localizer.Get(Lang, "esports.all_matches") : DiscordText.Untrusted(f.TeamName, 80))}" +
                                  (f.RoleIds.Count > 0 ? " — " + string.Join(" ", f.RoleIds.Select(r => DiscordText.RoleMention(new RoleId(r)))) : "")).ToList();
        var description = string.Join("\n", lines) + "\n\n" + await T(hide ? "esports.subs.hide_on" : "esports.subs.hide_off") + "\n" + await T("esports.subs.note");
        var components = new ComponentBuilder()
            .WithButton(await T(hide ? "esports.subs.show_button" : "esports.subs.hide_button"), PrefPrefix + (hide ? "0" : "1"), ButtonStyle.Secondary)
            .Build();
        await ReplyEmbedAsync(new MessageEmbed(await T("esports.subs.title"), description, null, [], null, null, NeutralColor), components);
    }

    /// <summary>Public role panel button: toggles the clicking user's own follow. Stateless → survives restarts.</summary>
    [ComponentInteraction(PanelPrefix + "*", ignoreGroupNames: true)]
    public async Task PanelAsync(string mappingId)
    {
        await DeferEphemeralAsync();
        if (!long.TryParse(mappingId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
            await roleMappings.PanelFollowKeyAsync(Actor.GuildId, id, CancellationToken.None) is not { } followKey)
        {
            await ReplyTextAsync("esports.panel.stale");
            return;
        }

        await ReplyOutcomeAsync(await subscriptions.ToggleAsync(Actor, followKey, CancellationToken.None));
    }

    [ComponentInteraction(PrefPrefix + "*", ignoreGroupNames: true)]
    public async Task PreferenceAsync(string value)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await subscriptions.SetHideResultsAsync(Actor, value == "1", CancellationToken.None));
    }

    private string Lang { get; set; } = "tr";

    public override async Task BeforeExecuteAsync(ICommandInfo command)
    {
        await base.BeforeExecuteAsync(command);
        Lang = await LangAsync();
    }

    private async Task ReplyOutcomeAsync(SubscriptionOutcome outcome)
    {
        var text = await T(outcome.Result.MessageKey, outcome.Result.Args.ToArray());
        foreach (var note in outcome.NoteKeys)
            text += "\n" + await T(note);
        if (!outcome.Result.Succeeded && outcome.Result.TraceCode is not null)
            text += "\n" + await T("error.trace_code", outcome.Result.TraceCode);
        await SendEphemeralAsync(text, null, null);
    }

    private async Task<bool> DemoAllowedAsync()
    {
        if (mode.IsDemo && !deployment.MayShowDemoData(Actor.GuildId))
        {
            await ReplyTextAsync("esports.demo_only_test_guild");
            return false;
        }

        return true;
    }

    private async Task<bool> CheckDataAsync<TData>(FeedState<TData> state)
        where TData : class
    {
        if (!await DemoAllowedAsync())
            return false;
        if (!provider.IsConfigured)
        {
            await ReplyTextAsync("esports.provider_not_configured");
            return false;
        }

        if (state.Data is null)
        {
            await ReplyTextAsync(state.LastOutcome is null ? "esports.no_data_yet" : "esports.data_unavailable", state.LastOutcome?.ToString() ?? "");
            return false;
        }

        return true;
    }

    private async Task ReplyListAsync<TData>(string titleKey, IReadOnlyList<string> lines, string emptyKey, FeedState<TData> state)
        where TData : class
    {
        var now = Services.Clock.GetUtcNow();
        var body = lines.Count > 0 ? string.Join("\n", lines) : await T(emptyKey);
        var notes = new List<string> { renderer.Freshness(Lang, state.FetchedAt!.Value) };
        if (state.IsStale(now, cache.StaleAfter))
            notes.Add(await T("esports.stale_warning", state.LastOutcome?.ToString() ?? "-"));
        if (state.LastOutcome == ProviderOutcome.Partial)
            notes.Add(await T("esports.partial_warning"));
        await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(Lang) + await T(titleKey), Clip(body + "\n\n" + string.Join("\n", notes), DiscordLimits.EmbedDescriptionMax),
            null, [], renderer.Footer(Lang), state.FetchedAt, NeutralColor));
    }

    /// <summary>Clips on line boundaries so a cut can never leave an unclosed ||spoiler|| showing a score.</summary>
    private static string Clip(string text, int max = DiscordLimits.EmbedFieldValueMax)
    {
        if (text.Length <= max)
            return text;
        var kept = new System.Text.StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (kept.Length + line.Length + 2 > max)
                break;
            kept.Append(line).Append('\n');
        }

        return kept.Append('…').ToString();
    }
}
