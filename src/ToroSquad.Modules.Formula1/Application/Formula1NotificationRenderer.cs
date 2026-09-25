using System.Globalization;
using System.Text;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

/// <summary>What the standings part of a sprint/race result card shows.</summary>
public enum F1StandingsSection
{
    /// <summary>No standings part (practice, qualifying, or the server turned standings off).</summary>
    None = 0,

    /// <summary>Settle window open, provider not updated yet.</summary>
    Pending = 1,

    /// <summary>Provider standings changed after this session: shown (top rows).</summary>
    Attached = 2,

    /// <summary>Window closed without a provider update (or provider unavailable): point to /f1 standings.</summary>
    NotUpdated = 3,
}

public sealed record F1StandingsAttachment(F1StandingsSection Section, F1StandingsSnapshot? Drivers, F1StandingsSnapshot? Constructors, string? SourceKey);

/// <summary>
/// Formula 1 cards in TSQ's compact style. Rules (tested): all provider text is untrusted (mentions, markdown and links
/// defused); nothing the provider did not state is shown (no invented times, gaps or winners); spoiler mode hides the whole
/// classification and standings in fixed-layout spoilers, and title, colour and thumbnail never depend on the outcome;
/// demo data is labelled TEST/DEMO and names no real source; everything is sized under Discord's embed limits.
/// Rendering is a pure function of persisted state, so an unchanged state never causes an edit.
/// </summary>
public sealed class Formula1NotificationRenderer(ILocalizer localizer, F1DataMode mode)
{
    public const uint StartedColor = 0xE10600;
    public const uint ResultColor = 0x15151E;
    public const uint InfoColor = 0x5865F2;

    /// <summary>Keeps a full 20–22 car classification plus standings well under the 6000-character embed limit.</summary>
    public const int DescriptionBudget = 3000;

    public bool IsDemo => mode.IsDemo;

    public OutgoingMessage Started(F1SessionView view, string language, MentionPolicy pings, string lifecycleSourceKey)
    {
        var at = view.StartedAt ?? view.Session.ScheduledStartUtc;
        var lines = new List<string>
        {
            L(language, "f1.card.started", SessionName(view.Session.Type, language), DiscordText.Timestamp(at, 'R')),
            "📍 " + Place(view),
            "🏁 " + L(language, "f1.card.round", view.Session.Round, view.Session.Season),
        };
        return new OutgoingMessage(
            Content(pings),
            new MessageEmbed(Title(view, language, "f1.card.title"), string.Join("\n", lines), null, [], Footer(language, lifecycleSourceKey, null), at, StartedColor),
            pings);
    }

    public OutgoingMessage Result(F1SessionView view, F1CachedResult result, F1StandingsAttachment standings, bool spoiler, string language, MentionPolicy pings,
        string resultsSourceKey, int standingsRows)
    {
        var type = view.Session.Type;
        var titleKey = F1SessionTypes.AwardsChampionshipPoints(type) ? "f1.card.result_title_race" : "f1.card.result_title_timed";
        var (body, truncated) = Classification(result.Result, language, DescriptionBudget);
        var description = new StringBuilder();
        description.Append("🏁 ").Append(L(language, "f1.card.round", view.Session.Round, view.Session.Season)).Append(" · ").Append(Place(view)).Append("\n\n");
        // Spoiler mode: ONE spoiler around the whole classification; its visible shape does not depend on the order.
        description.Append(spoiler ? DiscordText.Spoiler(body) : body);
        if (truncated)
            description.Append('\n').Append(L(language, "f1.card.truncated"));

        var fields = StandingsFields(standings, spoiler, language, standingsRows);
        return new OutgoingMessage(
            Content(pings),
            new MessageEmbed(Title(view, language, titleKey), description.ToString(), null, fields,
                Footer(language, resultsSourceKey, standings.Section == F1StandingsSection.Attached ? standings.SourceKey : null),
                result.FirstAvailableAt, ResultColor),
            pings);
    }

    /// <summary>Classification lines within <paramref name="budget"/> characters; cut on line boundaries only.</summary>
    public (string Text, bool Truncated) Classification(F1SessionResult result, string language, int budget)
    {
        var lines = Lines(result, language).ToList();
        var sb = new StringBuilder();
        var truncated = false;
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > budget)
            {
                truncated = true;
                break;
            }

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }

        return (sb.ToString(), truncated);
    }

    public IEnumerable<string> Lines(F1SessionResult result, string language)
    {
        var timed = !F1SessionTypes.AwardsChampionshipPoints(result.Type);
        foreach (var e in result.Ordered)
        {
            var who = DiscordText.Untrusted(e.DriverName, 60) + (e.TeamName is null ? "" : " — " + DiscordText.Untrusted(e.TeamName, 40));
            if (e.Position is not { } p)
            {
                yield return "`" + StatusLabel(e.Status, language) + "` " + who;
                continue;
            }

            var medal = !timed && p <= 3 ? (p == 1 ? "🥇 " : p == 2 ? "🥈 " : "🥉 ") : "";
            var line = medal + p.ToString(CultureInfo.InvariantCulture) + ". " + who;
            string? detail = null;
            if (timed)
                detail = p == 1 || e.GapSeconds is null ? LapTime(e.TimeSeconds) : Gap(e.GapSeconds);
            else if (p > 1)
                detail = e.GapLaps is { } laps ? L(language, "f1.result.laps_behind", laps) : Gap(e.GapSeconds);
            if (!string.IsNullOrEmpty(detail))
                line += " — " + detail;
            if (!timed && e.Status == F1ResultStatus.Dnf)
                line += " · " + StatusLabel(F1ResultStatus.Dnf, language);
            yield return line;
        }
    }

    private List<EmbedField> StandingsFields(F1StandingsAttachment standings, bool spoiler, string language, int rows)
    {
        var fields = new List<EmbedField>();
        switch (standings.Section)
        {
            case F1StandingsSection.Pending:
                fields.Add(new EmbedField(L(language, "f1.card.standings_field"), L(language, "f1.card.standings_pending"), false));
                break;
            case F1StandingsSection.NotUpdated:
                fields.Add(new EmbedField(L(language, "f1.card.standings_field"), L(language, "f1.card.standings_not_updated"), false));
                break;
            case F1StandingsSection.Attached:
                if (standings.Drivers is { } d)
                    fields.Add(new EmbedField(L(language, "f1.standings.drivers_title_short"), Wrap(StandingsLines(d, language, rows, "f1.more_drivers"), spoiler), false));
                if (standings.Constructors is { } c)
                    fields.Add(new EmbedField(L(language, "f1.standings.constructors_title_short"), Wrap(StandingsLines(c, language, rows, "f1.more_constructors"), spoiler), false));
                break;
        }

        return fields;
    }

    private static string Wrap(string text, bool spoiler) => spoiler ? DiscordText.Spoiler(text) : text;

    /// <summary>Top rows of a provider standings table (points exactly as published), clipped to one field.</summary>
    public string StandingsLines(F1StandingsSnapshot snapshot, string language, int rows, string moreKey)
    {
        var lines = snapshot.Kind == F1StandingsKind.Drivers
            ? snapshot.Drivers.OrderBy(d => d.Position ?? int.MaxValue).Take(rows).Select(d =>
                Pos(d.Position) + DiscordText.Untrusted(d.DriverName, 50) + " — " + L(language, "f1.points", F1Canonical.Points(d.Points)))
            : snapshot.Constructors.OrderBy(c => c.Position ?? int.MaxValue).Take(rows).Select(c =>
                Pos(c.Position) + DiscordText.Untrusted(c.Name, 50) + " — " + L(language, "f1.points", F1Canonical.Points(c.Points)));
        var text = string.Join("\n", lines);
        if (snapshot.Count > rows)
            text += "\n" + L(language, moreKey);
        if (snapshot.Round is { } round)
            text += "\n" + L(language, "f1.card.standings_after_round", round);
        return Clip(text, DiscordLimits.EmbedFieldValueMax - 8);
    }

    public string StatusLabel(F1ResultStatus status, string language) => status switch
    {
        F1ResultStatus.Dnf => "DNF",
        F1ResultStatus.Dns => "DNS",
        F1ResultStatus.Dsq => "DSQ",
        F1ResultStatus.NotClassified => L(language, "f1.result.not_classified"),
        _ => "",
    };

    public string SessionName(F1SessionType type, string language) => L(language, "f1.session." + F1SessionTypes.Slug(type));

    public static string MeetingName(F1SessionView view) => DiscordText.UntrustedPlain(view.MeetingName, 80);

    public static string Place(F1SessionView view)
    {
        var parts = new[] { view.CircuitName, view.Country }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => DiscordText.Untrusted(p, 80));
        return string.Join(" · ", parts);
    }

    /// <summary>Attribution footer. Demo data is synthetic and names no real source.</summary>
    public string Footer(string language, string primarySourceKey, string? secondarySourceKey)
    {
        if (mode.IsDemo)
            return L(language, "f1.demo_footer");
        var text = L(language, "f1.footer_source", L(language, primarySourceKey));
        if (secondarySourceKey is not null && secondarySourceKey != primarySourceKey)
            text += " · " + L(language, "f1.footer_standings_source", L(language, secondarySourceKey));
        return text;
    }

    public string Demo(string language) => mode.IsDemo ? L(language, "f1.demo_label") + " " : "";

    private string Title(F1SessionView view, string language, string key) =>
        Clip(Demo(language) + L(language, key, MeetingName(view), SessionName(view.Session.Type, language)), DiscordLimits.EmbedTitleMax);

    public static string LapTime(double? seconds)
    {
        if (seconds is not { } s || s < 0)
            return "";
        var t = TimeSpan.FromMilliseconds(Math.Round(s * 1000, MidpointRounding.AwayFromZero));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds:000}");
    }

    public static string? Gap(double? seconds) =>
        seconds is { } s && s > 0 ? "+" + s.ToString("0.000", CultureInfo.InvariantCulture) + "s" : null;

    private static string Pos(int? position) => position is { } p ? p.ToString(CultureInfo.InvariantCulture) + ". " : "–. ";

    private static string? Content(MentionPolicy pings) =>
        pings.Roles.Count == 0 ? null : string.Join(' ', pings.Roles.Select(DiscordText.RoleMention));

    public static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text.LastIndexOf('\n', Math.Max(0, max - 2));
        return (cut > 0 ? text[..cut] : text[..(max - 1)]) + "\n…";
    }

    private string L(string language, string key, params object?[] args) => localizer.Get(language, key, args);
}
