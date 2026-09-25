using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Commands;

/// <summary>
/// Public Formula 1 commands. Every answer comes from <see cref="Formula1Cache"/> (never a provider call on the
/// interaction path), carries its source and freshness, and degrades honestly: missing/failed data is shown as such,
/// never as "no sessions"; live status is never guessed from the schedule.
/// </summary>
[ToroModule(Formula1Module.ModuleIdValue)]
[Group("f1", "Formula 1 schedule, results and championship standings")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class Formula1Commands(
    InteractionServices services,
    Formula1Cache cache,
    Formula1NotificationRenderer renderer,
    F1DataMode mode,
    DeploymentPolicy deployment,
    F1Sources sources,
    IOptions<Formula1Options> options) : ToroInteractionModule(services)
{
    [SlashCommand("next", "The next Grand Prix weekend and its sessions")]
    public async Task NextAsync()
    {
        await DeferEphemeralAsync();
        if (!await ScheduleReadyAsync())
            return;
        var now = Services.Clock.GetUtcNow();
        var meeting = F1CommandViews.NextMeeting(cache.SessionsOrdered(), now);
        if (meeting is null)
        {
            await ReplyTextAsync("f1.next.none");
            return;
        }

        var lang = await LangAsync();
        var race = meeting.FirstOrDefault(s => s.Session.Type == F1SessionType.Race);
        var head = meeting[0];
        var sb = new StringBuilder();
        sb.Append("🏁 ").Append(Localizer.Get(lang, "f1.card.round", head.Session.Round, head.Session.Season)).Append(" · ").Append(Formula1NotificationRenderer.Place(head)).Append('\n');
        if (race is not null)
            sb.Append("🏎️ ").Append(Localizer.Get(lang, "f1.next.race_at", DiscordText.Timestamp(race.Session.ScheduledStartUtc, 'F'), DiscordText.Timestamp(race.Session.ScheduledStartUtc, 'R'))).Append('\n');
        sb.Append('\n');
        foreach (var s in meeting)
            sb.Append(SessionLine(s, lang, now, withRelative: false)).Append('\n');
        await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(lang) + Localizer.Get(lang, "f1.next.title", Formula1NotificationRenderer.MeetingName(head)),
            sb.ToString().TrimEnd() + await NotesAsync(cache.Schedule, options.Value.ScheduleStaleAfter, sources.ScheduleKey),
            null, [], renderer.Footer(lang, sources.ScheduleKey, null), cache.Schedule.FetchedAt, NeutralColor));
    }

    [SlashCommand("schedule", "Session schedule of the current/next weekend (or a given round)")]
    public async Task ScheduleAsync([Summary("round", "Round number (default: current or next weekend)"), MinValue(1), MaxValue(40)] int? round = null)
    {
        await DeferEphemeralAsync();
        if (!await ScheduleReadyAsync())
            return;
        var now = Services.Clock.GetUtcNow();
        var all = cache.SessionsOrdered();
        var meeting = round is { } r
            ? F1CommandViews.Meeting(all, F1CommandViews.CurrentSeason(all, now), r)
            : F1CommandViews.NextMeeting(all, now);
        if (meeting is null)
        {
            await ReplyTextAsync(round is null ? "f1.next.none" : "f1.schedule.unknown_round", round ?? 0);
            return;
        }

        var lang = await LangAsync();
        var head = meeting[0];
        var lines = new List<string>
        {
            "🏁 " + Localizer.Get(lang, "f1.card.round", head.Session.Round, head.Session.Season) + " · " + Formula1NotificationRenderer.Place(head),
            Localizer.Get(lang, head.Session.Season > 0 && meeting.Any(s => s.Session.Type == F1SessionType.Sprint) ? "f1.schedule.sprint_weekend" : "f1.schedule.standard_weekend"),
            "",
        };
        lines.AddRange(meeting.Select(s => SessionLine(s, lang, now, withRelative: true)));
        if (!sources.LifecycleConfigured)
            lines.Add("\n" + Localizer.Get(lang, "f1.schedule.live_unavailable_note"));
        await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(lang) + Localizer.Get(lang, "f1.schedule.title", Formula1NotificationRenderer.MeetingName(head)),
            string.Join("\n", lines) + await NotesAsync(cache.Schedule, options.Value.ScheduleStaleAfter, sources.ScheduleKey),
            null, [], renderer.Footer(lang, sources.ScheduleKey, null), cache.Schedule.FetchedAt, NeutralColor));
    }

    [SlashCommand("results", "Latest session classification (from the bot's cache)")]
    public async Task ResultsAsync(
        [Summary("session", "Which session (default: the latest with a result)"),
         Choice("latest", "latest"), Choice("race", "race"), Choice("sprint", "sprint"), Choice("qualifying", "quali"),
         Choice("sprint-qualifying", "sq"), Choice("fp1", "fp1"), Choice("fp2", "fp2"), Choice("fp3", "fp3")] string session = "latest",
        [Summary("spoiler", "Hide the classification behind a spoiler")] bool spoiler = false)
    {
        await DeferEphemeralAsync();
        if (!await DemoAllowedAsync())
            return;
        var lang = await LangAsync();
        F1SessionType? type = session != "latest" && F1SessionTypes.TryParseSlug(session, out var t) ? t : null;
        var pick = F1CommandViews.LatestResult(cache.SessionsOrdered(), cache.Results, type);
        if (pick is null)
        {
            var failing = cache.ResultsFeed.LastOutcome is { } o && o is not (F1ProviderOutcome.Success or F1ProviderOutcome.Partial);
            await ReplyTextAsync(failing ? "f1.results.unavailable" : "f1.results.none",
                type is { } tt ? renderer.SessionName(tt, lang) : Localizer.Get(lang, "f1.results.any_session"), cache.ResultsFeed.LastOutcome?.ToString() ?? "-");
            return;
        }

        var (view, result) = pick.Value;
        var (body, truncated) = renderer.Classification(result.Result, lang, Formula1NotificationRenderer.DescriptionBudget);
        var text = (spoiler ? DiscordText.Spoiler(body) : body) + (truncated ? "\n" + Localizer.Get(lang, "f1.card.truncated") : "");
        text += "\n\n" + Localizer.Get(lang, "f1.freshness", DiscordText.Timestamp(result.FetchedAt, 'R'));
        if (Services.Clock.GetUtcNow() - result.FetchedAt > options.Value.ResultStaleAfter && view.FinalisedAt is { } f &&
            Services.Clock.GetUtcNow() - f < TimeSpan.FromHours(options.Value.ResultCorrectionHours))
            text += "\n" + Localizer.Get(lang, "f1.stale_warning", cache.ResultsFeed.LastOutcome?.ToString() ?? "-");
        await ReplyEmbedAsync(new MessageEmbed(
            renderer.Demo(lang) + Localizer.Get(lang, F1SessionTypes.AwardsChampionshipPoints(view.Session.Type) ? "f1.card.result_title_race" : "f1.card.result_title_timed",
                Formula1NotificationRenderer.MeetingName(view), renderer.SessionName(view.Session.Type, lang)),
            Formula1NotificationRenderer.Clip(text, DiscordLimits.EmbedDescriptionMax), null, [],
            renderer.Footer(lang, "f1.source." + result.Result.Source, null), result.FirstAvailableAt, NeutralColor));
    }

    [SlashCommand("now", "Is a session running right now? (live provider status, never guessed)")]
    public async Task NowAsync()
    {
        await DeferEphemeralAsync();
        if (!await DemoAllowedAsync())
            return;
        var lang = await LangAsync();
        if (!sources.LifecycleConfigured)
        {
            await ReplyTextAsync("f1.now.unavailable_not_configured");
            return;
        }

        var now = Services.Clock.GetUtcNow();
        var feed = cache.LifecycleFeed;
        var sessions = cache.SessionsOrdered();
        // A session left "running" by a missed finish long ago is not shown as live.
        var running = sessions.Where(s => s.State is F1SessionState.Started or F1SessionState.Suspended && now - s.Session.ScheduledStartUtc < TimeSpan.FromHours(16)).ToList();
        var live = cache.Live;
        var healthy = live.State == F1LiveState.Connected || (feed.FetchedAt is { } at && now - at < TimeSpan.FromMinutes(5));
        if (running.Count == 0)
        {
            var lead = TimeSpan.FromMinutes(options.Value.LifecycleLeadMinutes);
            var inWindow = sessions.Any(s => now >= s.Session.ScheduledStartUtc - lead && now <= s.Session.PlannedEndUtc + TimeSpan.FromHours(options.Value.LifecycleTrailingHours) &&
                                             s.State is F1SessionState.Scheduled or F1SessionState.Unknown);
            if (!inWindow && sessions.FirstOrDefault(s => s.Session.ScheduledStartUtc > now) is { } next)
            {
                // Nothing can be running this far from any session; say so and point at the next one (schedule info only).
                await ReplyTextAsync("f1.now.none_scheduled", renderer.SessionName(next.Session.Type, lang) + " · " + Formula1NotificationRenderer.MeetingName(next),
                    DiscordText.Timestamp(next.Session.ScheduledStartUtc, 'R'));
                return;
            }

            // Around a session: only "no session running" when the lifecycle source was reachable recently; otherwise we do not know.
            await ReplyTextAsync(healthy ? "f1.now.none" : "f1.now.unavailable", Localizer.Get(lang, sources.LifecycleKey));
            return;
        }

        var s = running[^1];
        var lines = new List<string>
        {
            "📍 " + Formula1NotificationRenderer.Place(s),
            Localizer.Get(lang, s.State == F1SessionState.Started ? "f1.now.state_running" : "f1.now.state_suspended"),
            Localizer.Get(lang, "f1.now.started", s.StartedAt is { } st ? DiscordText.Timestamp(st, 'R') : "?"),
            Localizer.Get(lang, "f1.freshness", s.LastLifecycleEventAt is { } le ? DiscordText.Timestamp(le, 'R') : "-"),
        };
        if (!healthy)
            lines.Add(Localizer.Get(lang, "f1.now.connection_warning", live.State.ToString()));
        await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(lang) + Localizer.Get(lang, "f1.card.title", Formula1NotificationRenderer.MeetingName(s), renderer.SessionName(s.Session.Type, lang)),
            string.Join("\n", lines), null, [], renderer.Footer(lang, sources.LifecycleKey, null), s.LastLifecycleEventAt, Formula1NotificationRenderer.StartedColor));
    }

    [ToroModule(Formula1Module.ModuleIdValue)]
    [Group("standings", "Championship standings")]
    public sealed class StandingsCommands(
        InteractionServices services,
        Formula1Cache cache,
        Formula1NotificationRenderer renderer,
        F1DataMode mode,
        DeploymentPolicy deployment,
        F1Sources sources,
        IOptions<Formula1Options> options) : ToroInteractionModule(services)
    {
        [SlashCommand("drivers", "Drivers' championship standings")]
        public Task DriversAsync() => ShowAsync(F1StandingsKind.Drivers);

        [SlashCommand("constructors", "Constructors' championship standings")]
        public Task ConstructorsAsync() => ShowAsync(F1StandingsKind.Constructors);

        private async Task ShowAsync(F1StandingsKind kind)
        {
            await DeferEphemeralAsync();
            if (mode.IsDemo && !deployment.MayShowDemoData(Actor.GuildId))
            {
                await ReplyTextAsync("f1.demo_only_test_guild");
                return;
            }

            var lang = await LangAsync();
            var feed = cache.Standings(kind);
            if (feed.Data is null)
            {
                await ReplyTextAsync(feed.LastOutcome is null ? "f1.no_data_yet" : "f1.data_unavailable", feed.LastOutcome?.ToString() ?? "-");
                return;
            }

            var snapshot = feed.Data;
            if (snapshot.Count == 0)
            {
                await ReplyTextAsync("f1.standings.empty", snapshot.Season);
                return;
            }

            var lines = snapshot.Kind == F1StandingsKind.Drivers
                ? snapshot.Drivers.OrderBy(d => d.Position ?? int.MaxValue).Select(d =>
                    $"`{Pos(d.Position)}` {DiscordText.Untrusted(d.DriverName, 50)}{(d.TeamName is null ? "" : " — " + DiscordText.Untrusted(d.TeamName, 40))} — **{Localizer.Get(lang, "f1.points", F1Canonical.Points(d.Points))}**")
                : snapshot.Constructors.OrderBy(c => c.Position ?? int.MaxValue).Select(c =>
                    $"`{Pos(c.Position)}` {DiscordText.Untrusted(c.Name, 50)} — **{Localizer.Get(lang, "f1.points", F1Canonical.Points(c.Points))}**");
            var text = string.Join("\n", lines);
            var notes = new List<string>
            {
                Localizer.Get(lang, snapshot.Round is { } r ? "f1.standings.after_round" : "f1.standings.season_only", snapshot.Season, snapshot.Round ?? 0),
                Localizer.Get(lang, "f1.freshness", DiscordText.Timestamp(feed.FetchedAt!.Value, 'R')),
            };
            if (feed.IsStale(Services.Clock.GetUtcNow(), options.Value.StandingsStaleAfter))
                notes.Add(Localizer.Get(lang, "f1.stale_warning", feed.LastOutcome?.ToString() ?? "-"));
            notes.Add(Localizer.Get(lang, "f1.standings.official_note"));
            var title = Localizer.Get(lang, kind == F1StandingsKind.Drivers ? "f1.standings.drivers_title" : "f1.standings.constructors_title", snapshot.Season);
            await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(lang) + title,
                Formula1NotificationRenderer.Clip(text + "\n\n" + string.Join("\n", notes), DiscordLimits.EmbedDescriptionMax),
                null, [], renderer.Footer(lang, sources.StandingsKey, null), feed.FetchedAt, NeutralColor));
        }

        private static string Pos(int? p) => p is { } v ? v.ToString("00", CultureInfo.InvariantCulture) : "--";
    }

    private string SessionLine(F1SessionView s, string lang, DateTimeOffset now, bool withRelative)
    {
        var state = s.State switch
        {
            F1SessionState.Started => " · 🔴 " + Localizer.Get(lang, "f1.state.running"),
            F1SessionState.Suspended => " · 🟥 " + Localizer.Get(lang, "f1.state.suspended"),
            F1SessionState.FinishedPendingResults => " · 🏁 " + Localizer.Get(lang, "f1.state.finished_pending"),
            F1SessionState.Finalised => " · ✅ " + Localizer.Get(lang, "f1.state.finalised"),
            F1SessionState.Cancelled => " · ❌ " + Localizer.Get(lang, "f1.state.cancelled"),
            // Scheduled: never "live" from the clock; after the planned time without lifecycle data it is just "scheduled".
            _ => "",
        };
        var when = DiscordText.Timestamp(s.Session.ScheduledStartUtc, 'f') + (withRelative && s.Session.ScheduledStartUtc > now ? " (" + DiscordText.Timestamp(s.Session.ScheduledStartUtc, 'R') + ")" : "");
        return $"• **{renderer.SessionName(s.Session.Type, lang)}** — {when}{state}";
    }

    private async Task<string> NotesAsync<TData>(F1Feed<TData> feed, TimeSpan staleAfter, string sourceKey)
        where TData : class
    {
        var notes = "\n\n" + await T("f1.freshness", DiscordText.Timestamp(feed.FetchedAt!.Value, 'R'));
        if (feed.IsStale(Services.Clock.GetUtcNow(), staleAfter))
            notes += "\n" + await T("f1.stale_warning", feed.LastOutcome?.ToString() ?? "-");
        return notes;
    }

    private async Task<bool> DemoAllowedAsync()
    {
        if (mode.IsDemo && !deployment.MayShowDemoData(Actor.GuildId))
        {
            await ReplyTextAsync("f1.demo_only_test_guild");
            return false;
        }

        return true;
    }

    private async Task<bool> ScheduleReadyAsync()
    {
        if (!await DemoAllowedAsync())
            return false;
        var feed = cache.Schedule;
        if (feed.Data is null || feed.FetchedAt is null)
        {
            await ReplyTextAsync(feed.LastOutcome is null ? "f1.no_data_yet" : "f1.data_unavailable", feed.LastOutcome?.ToString() ?? "-");
            return false;
        }

        return true;
    }
}

/// <summary>Pure selection logic behind the commands (unit-tested).</summary>
public static class F1CommandViews
{
    /// <summary>
    /// The current or next weekend: the earliest meeting whose race (or last session) has not been over for more than a
    /// few hours. Uses only the schedule for choosing what to SHOW — never to claim anything happened.
    /// </summary>
    public static IReadOnlyList<F1SessionView>? NextMeeting(IReadOnlyList<F1SessionView> sessions, DateTimeOffset now) =>
        sessions.GroupBy(s => s.Session.MeetingKey)
            .Select(g => g.OrderBy(s => s.Session.ScheduledStartUtc).ThenBy(s => F1SessionTypes.Order(s.Session.Type)).ToList())
            .Where(m => m.Max(s => s.Session.PlannedEndUtc) + TimeSpan.FromHours(6) >= now && m.Any(s => s.State != F1SessionState.Cancelled))
            .OrderBy(m => m[0].Session.ScheduledStartUtc)
            .FirstOrDefault();

    public static int CurrentSeason(IReadOnlyList<F1SessionView> sessions, DateTimeOffset now) =>
        NextMeeting(sessions, now)?[0].Session.Season ?? now.Year;

    public static IReadOnlyList<F1SessionView>? Meeting(IReadOnlyList<F1SessionView> sessions, int season, int round)
    {
        var list = sessions.Where(s => s.Session.Season == season && s.Session.Round == round)
            .OrderBy(s => s.Session.ScheduledStartUtc).ThenBy(s => F1SessionTypes.Order(s.Session.Type)).ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>The most recent session (optionally of one type) that has a stored classification.</summary>
    public static (F1SessionView View, F1CachedResult Result)? LatestResult(IReadOnlyList<F1SessionView> sessions, IReadOnlyDictionary<string, F1CachedResult> results, F1SessionType? type)
    {
        var pick = sessions.Where(s => (type is null || s.Session.Type == type) && results.ContainsKey(s.Session.Key))
            .OrderByDescending(s => s.Session.ScheduledStartUtc)
            .FirstOrDefault();
        return pick is null ? null : (pick, results[pick.Session.Key]);
    }
}
