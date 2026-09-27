using System.Globalization;
using System.Text;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Commands;

/// <summary>What a /f1 command answers: an embed or a text, and whether only the invoking user sees it.</summary>
public sealed record F1CommandReply(MessageEmbed? Embed, string? Text, bool Ephemeral)
{
    public static F1CommandReply Public(MessageEmbed embed) => new(embed, null, false);

    public static F1CommandReply PublicText(string text) => new(null, text, false);

    public static F1CommandReply Private(MessageEmbed embed) => new(embed, null, true);

    public static F1CommandReply PrivateText(string text) => new(null, text, true);
}

/// <summary>
/// The answers of the /f1 commands as pure functions of the cache (no Discord SDK, no provider call — unit-tested).
/// Visibility: next, schedule, standings and now answer publicly in the channel; results stays private (spoilers);
/// "no data yet" style errors stay private so a failed lookup never posts noise. No provider attribution is shown
/// (freshness is, without naming the source); live state is only ever the lifecycle provider's, never the clock's.
/// </summary>
public sealed class F1CommandResponses(Formula1Cache cache, Formula1NotificationRenderer renderer, ILocalizer localizer, F1Sources sources, Formula1Options options)
{
    public const uint NeutralColor = 0x5865F2;

    /// <summary>Indents the race date under the Grand Prix name in the season overview.</summary>
    private const char EmSpace = '\u2003';

    /// <summary>A lifecycle state older than this is never shown as live (a missed finish long ago).</summary>
    public static readonly TimeSpan LiveStateMaxAge = TimeSpan.FromHours(16);

    // ---------------------------------------------------------------- /f1 next

    public F1CommandReply Next(string lang, DateTimeOffset now)
    {
        if (ScheduleProblem(lang) is { } problem)
            return problem;
        var meeting = F1CommandViews.NextMeeting(cache.SessionsOrdered(), now);
        if (meeting is null)
            return F1CommandReply.PublicText(L(lang, "f1.next.none"));

        var race = meeting.FirstOrDefault(s => s.Session.Type == F1SessionType.Race);
        var head = meeting[0];
        var sb = new StringBuilder();
        sb.Append("🏁 ").Append(L(lang, "f1.card.round", head.Session.Round, head.Session.Season)).Append(" · ").Append(Formula1NotificationRenderer.Place(head)).Append('\n');
        if (race is not null)
            sb.Append("🏎️ ").Append(L(lang, "f1.next.race_at", DiscordText.Timestamp(race.Session.ScheduledStartUtc, 'F'), DiscordText.Timestamp(race.Session.ScheduledStartUtc, 'R'))).Append('\n');
        sb.Append('\n');
        foreach (var s in meeting)
            sb.Append(SessionLine(s, lang, now, withRelative: false)).Append('\n');
        return F1CommandReply.Public(new MessageEmbed(renderer.Demo(lang) + L(lang, "f1.next.title", Formula1NotificationRenderer.MeetingName(head)),
            sb.ToString().TrimEnd() + Notes(lang, cache.Schedule, options.ScheduleStaleAfter, now), null, [], renderer.CardFooter(lang), cache.Schedule.FetchedAt, NeutralColor));
    }

    // ---------------------------------------------------------------- /f1 schedule

    /// <summary>No round: the season overview. A round: that weekend's detailed programme.</summary>
    public F1CommandReply Schedule(string lang, DateTimeOffset now, int? round)
    {
        if (ScheduleProblem(lang) is { } problem)
            return problem;
        var all = cache.SessionsOrdered();
        var season = F1CommandViews.CurrentSeason(all, now);
        return round is { } r ? Weekend(lang, now, all, season, r) : Season(lang, now, all, season);
    }

    private F1CommandReply Season(string lang, DateTimeOffset now, IReadOnlyList<F1SessionView> all, int season)
    {
        var meetings = F1CommandViews.SeasonMeetings(all, season);
        if (meetings.Count == 0)
            return F1CommandReply.PublicText(L(lang, "f1.season.none", season));

        var current = F1CommandViews.NextMeeting(all, now)?[0].Session.MeetingKey;
        var sb = new StringBuilder();
        foreach (var m in meetings)
        {
            var mark = F1CommandViews.SeasonMark(m, current, now) switch
            {
                F1SeasonMark.Live => "🔴",
                F1SeasonMark.Done => "✅",
                F1SeasonMark.Cancelled => "❌",
                F1SeasonMark.Current => "➡️",
                _ => "▫️",
            };
            var head = m[0];
            var race = m.FirstOrDefault(s => s.Session.Type == F1SessionType.Race) ?? m[^1];
            var sprint = m.Any(s => s.Session.Type is F1SessionType.Sprint or F1SessionType.SprintQualifying) ? L(lang, "f1.season.sprint") : "";
            sb.Append(mark).Append(" `").Append(head.Session.Round.ToString("00", CultureInfo.InvariantCulture)).Append("` ")
                .Append(ShortName(head)).Append(sprint).Append('\n')
                .Append(EmSpace).Append(DiscordText.Timestamp(race.Session.ScheduledStartUtc, 'D')).Append('\n');
        }

        var text = sb.ToString().TrimEnd() + "\n\n" + L(lang, "f1.season.legend") + "\n" + L(lang, "f1.season.hint");
        if (!sources.LifecycleConfigured)
            text += "\n" + L(lang, "f1.schedule.live_unavailable_note");
        text += Notes(lang, cache.Schedule, options.ScheduleStaleAfter, now);
        return F1CommandReply.Public(new MessageEmbed(renderer.Demo(lang) + L(lang, "f1.season.title", season),
            Formula1NotificationRenderer.Clip(text, DiscordLimits.EmbedDescriptionMax), null, [], renderer.CardFooter(lang), cache.Schedule.FetchedAt, NeutralColor));
    }

    private F1CommandReply Weekend(string lang, DateTimeOffset now, IReadOnlyList<F1SessionView> all, int season, int round)
    {
        var meeting = F1CommandViews.Meeting(all, season, round);
        if (meeting is null)
            return F1CommandReply.PrivateText(L(lang, "f1.schedule.unknown_round", round));

        var head = meeting[0];
        var lines = new List<string>
        {
            "🏁 " + L(lang, "f1.card.round", head.Session.Round, head.Session.Season) + " · " + Formula1NotificationRenderer.Place(head),
            L(lang, meeting.Any(s => s.Session.Type is F1SessionType.Sprint or F1SessionType.SprintQualifying) ? "f1.schedule.sprint_weekend" : "f1.schedule.standard_weekend"),
            "",
        };
        lines.AddRange(meeting.Select(s => SessionLine(s, lang, now, withRelative: true)));
        if (!sources.LifecycleConfigured)
            lines.Add("\n" + L(lang, "f1.schedule.live_unavailable_note"));
        return F1CommandReply.Public(new MessageEmbed(renderer.Demo(lang) + L(lang, "f1.schedule.title", Formula1NotificationRenderer.MeetingName(head)),
            string.Join("\n", lines) + Notes(lang, cache.Schedule, options.ScheduleStaleAfter, now), null, [], renderer.CardFooter(lang), cache.Schedule.FetchedAt, NeutralColor));
    }

    // ---------------------------------------------------------------- /f1 standings

    public F1CommandReply Standings(string lang, DateTimeOffset now, F1StandingsKind kind)
    {
        var feed = cache.Standings(kind);
        if (feed.Data is null)
            return F1CommandReply.PrivateText(L(lang, feed.LastOutcome is null ? "f1.no_data_yet" : "f1.data_unavailable", feed.LastOutcome?.ToString() ?? "-"));
        var snapshot = feed.Data;
        if (snapshot.Count == 0)
            return F1CommandReply.PublicText(L(lang, "f1.standings.empty", snapshot.Season));

        var lines = snapshot.Kind == F1StandingsKind.Drivers
            ? snapshot.Drivers.OrderBy(d => d.Position ?? int.MaxValue).Select(d =>
                $"`{Pos(d.Position)}` {DiscordText.Untrusted(d.DriverName, 50)}{(d.TeamName is null ? "" : " — " + DiscordText.Untrusted(d.TeamName, 40))} — **{L(lang, "f1.points", F1Canonical.Points(d.Points))}**")
            : snapshot.Constructors.OrderBy(c => c.Position ?? int.MaxValue).Select(c =>
                $"`{Pos(c.Position)}` {DiscordText.Untrusted(c.Name, 50)} — **{L(lang, "f1.points", F1Canonical.Points(c.Points))}**");
        var notes = new List<string>
        {
            L(lang, snapshot.Round is not null ? "f1.standings.after_round" : "f1.standings.season_only", snapshot.Season, snapshot.Round ?? 0),
            L(lang, "f1.freshness", DiscordText.Timestamp(feed.FetchedAt!.Value, 'R')),
        };
        if (feed.IsStale(now, options.StandingsStaleAfter))
            notes.Add(L(lang, "f1.stale_warning", feed.LastOutcome?.ToString() ?? "-"));
        notes.Add(L(lang, "f1.standings.official_note"));
        var title = L(lang, kind == F1StandingsKind.Drivers ? "f1.standings.drivers_title" : "f1.standings.constructors_title", snapshot.Season);
        return F1CommandReply.Public(new MessageEmbed(renderer.Demo(lang) + title,
            Formula1NotificationRenderer.Clip(string.Join("\n", lines) + "\n\n" + string.Join("\n", notes), DiscordLimits.EmbedDescriptionMax),
            null, [], renderer.CardFooter(lang), feed.FetchedAt, NeutralColor));
    }

    // ---------------------------------------------------------------- /f1 now

    /// <summary>Live only from the lifecycle provider (Started/Suspended); otherwise an honest "none" or "unknown", publicly.</summary>
    public F1CommandReply Now(string lang, DateTimeOffset now)
    {
        if (!sources.LifecycleConfigured)
            return F1CommandReply.PublicText(L(lang, "f1.now.unavailable_not_configured"));

        var sessions = cache.SessionsOrdered();
        var running = sessions.Where(s => s.State is F1SessionState.Started or F1SessionState.Suspended && now - s.Session.ScheduledStartUtc < LiveStateMaxAge).ToList();
        var live = cache.Live;
        var healthy = live.State == F1LiveState.Connected || (cache.LifecycleFeed.FetchedAt is { } at && now - at < TimeSpan.FromMinutes(5));
        if (running.Count == 0)
        {
            var lead = TimeSpan.FromMinutes(options.LifecycleLeadMinutes);
            var inWindow = sessions.Any(s => now >= s.Session.ScheduledStartUtc - lead && now <= s.Session.PlannedEndUtc + TimeSpan.FromHours(options.LifecycleTrailingHours) &&
                                             s.State is F1SessionState.Scheduled or F1SessionState.Unknown);
            if (!inWindow && sessions.FirstOrDefault(s => s.Session.ScheduledStartUtc > now) is { } next)
            {
                return F1CommandReply.PublicText(L(lang, "f1.now.none_scheduled",
                    renderer.SessionName(next.Session.Type, lang) + " · " + Formula1NotificationRenderer.MeetingName(next), DiscordText.Timestamp(next.Session.ScheduledStartUtc, 'R')));
            }

            // Around a session: "none running" only when the lifecycle source was reachable recently; otherwise unknown.
            return F1CommandReply.PublicText(L(lang, healthy ? "f1.now.none" : "f1.now.unavailable"));
        }

        var s = running[^1];
        var lines = new List<string>
        {
            "📍 " + Formula1NotificationRenderer.Place(s),
            L(lang, s.State == F1SessionState.Started ? "f1.now.state_running" : "f1.now.state_suspended"),
            L(lang, "f1.now.started", s.StartedAt is { } st ? DiscordText.Timestamp(st, 'R') : "?"),
            L(lang, "f1.freshness", s.LastLifecycleEventAt is { } le ? DiscordText.Timestamp(le, 'R') : "-"),
        };
        if (!healthy)
            lines.Add(L(lang, "f1.now.connection_warning", live.State.ToString()));
        return F1CommandReply.Public(new MessageEmbed(renderer.Demo(lang) + L(lang, "f1.card.title", Formula1NotificationRenderer.MeetingName(s), renderer.SessionName(s.Session.Type, lang)),
            string.Join("\n", lines), null, [], renderer.CardFooter(lang), s.LastLifecycleEventAt, Formula1NotificationRenderer.StartedColor));
    }

    // ---------------------------------------------------------------- /f1 results (private)

    public F1CommandReply Results(string lang, DateTimeOffset now, F1SessionType? type, bool spoiler)
    {
        var pick = F1CommandViews.LatestResult(cache.SessionsOrdered(), cache.Results, type);
        if (pick is null)
        {
            var failing = cache.ResultsFeed.LastOutcome is { } o && o is not (F1ProviderOutcome.Success or F1ProviderOutcome.Partial);
            return F1CommandReply.PrivateText(L(lang, failing ? "f1.results.unavailable" : "f1.results.none",
                type is { } tt ? renderer.SessionName(tt, lang) : L(lang, "f1.results.any_session"), cache.ResultsFeed.LastOutcome?.ToString() ?? "-"));
        }

        var (view, result) = pick.Value;
        var (body, truncated) = renderer.Classification(result.Result, lang, Formula1NotificationRenderer.DescriptionBudget);
        var text = (spoiler ? DiscordText.Spoiler(body) : body) + (truncated ? "\n" + L(lang, "f1.card.truncated") : "");
        text += "\n\n" + L(lang, "f1.freshness", DiscordText.Timestamp(result.FetchedAt, 'R'));
        if (now - result.FetchedAt > options.ResultStaleAfter && view.FinalisedAt is { } f && now - f < TimeSpan.FromHours(options.ResultCorrectionHours))
            text += "\n" + L(lang, "f1.stale_warning", cache.ResultsFeed.LastOutcome?.ToString() ?? "-");
        return F1CommandReply.Private(new MessageEmbed(
            renderer.Demo(lang) + L(lang, F1SessionTypes.AwardsChampionshipPoints(view.Session.Type) ? "f1.card.result_title_race" : "f1.card.result_title_timed",
                Formula1NotificationRenderer.MeetingName(view), renderer.SessionName(view.Session.Type, lang)),
            Formula1NotificationRenderer.Clip(text, DiscordLimits.EmbedDescriptionMax), null, [], renderer.CardFooter(lang), result.FirstAvailableAt, NeutralColor));
    }

    // ---------------------------------------------------------------- helpers

    private F1CommandReply? ScheduleProblem(string lang)
    {
        var feed = cache.Schedule;
        return feed.Data is null || feed.FetchedAt is null
            ? F1CommandReply.PrivateText(L(lang, feed.LastOutcome is null ? "f1.no_data_yet" : "f1.data_unavailable", feed.LastOutcome?.ToString() ?? "-"))
            : null;
    }

    public string SessionLine(F1SessionView s, string lang, DateTimeOffset now, bool withRelative)
    {
        var state = s.State switch
        {
            F1SessionState.Started => " · 🔴 " + L(lang, "f1.state.running"),
            F1SessionState.Suspended => " · 🟥 " + L(lang, "f1.state.suspended"),
            F1SessionState.FinishedPendingResults => " · 🏁 " + L(lang, "f1.state.finished_pending"),
            F1SessionState.Finalised => " · ✅ " + L(lang, "f1.state.finalised"),
            F1SessionState.Cancelled => " · ❌ " + L(lang, "f1.state.cancelled"),
            // Scheduled: never "live" from the clock; after the planned time without lifecycle data it is just "scheduled".
            _ => "",
        };
        var when = DiscordText.Timestamp(s.Session.ScheduledStartUtc, 'f') + (withRelative && s.Session.ScheduledStartUtc > now ? " (" + DiscordText.Timestamp(s.Session.ScheduledStartUtc, 'R') + ")" : "");
        return $"• **{renderer.SessionName(s.Session.Type, lang)}** — {when}{state}";
    }

    private string Notes<TData>(string lang, F1Feed<TData> feed, TimeSpan staleAfter, DateTimeOffset now)
        where TData : class
    {
        var notes = "\n\n" + L(lang, "f1.freshness", DiscordText.Timestamp(feed.FetchedAt!.Value, 'R'));
        if (feed.IsStale(now, staleAfter))
            notes += "\n" + L(lang, "f1.stale_warning", feed.LastOutcome?.ToString() ?? "-");
        return notes;
    }

    /// <summary>"Azerbaijan Grand Prix" → "Azerbaijan GP" (overview only; the weekend view keeps the full name).</summary>
    private static string ShortName(F1SessionView v)
    {
        var name = Formula1NotificationRenderer.MeetingName(v);
        return name.EndsWith(" Grand Prix", StringComparison.Ordinal) ? name[..^" Grand Prix".Length] + " GP" : name;
    }

    private static string Pos(int? p) => p is { } v ? v.ToString("00", CultureInfo.InvariantCulture) : "--";

    private string L(string lang, string key, params object?[] args) => localizer.Get(lang, key, args);
}
