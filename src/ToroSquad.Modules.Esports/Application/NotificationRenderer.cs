using System.Globalization;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// Builds notification/listing messages. Rules enforced here (and tested):
/// <list type="bullet">
/// <item>All provider text is treated as untrusted (mention/markdown defused).</item>
/// <item>Spoiler mode: no score, winner, map result or winner-dependent colour outside ||spoiler|| tags — the
/// title and message content only ever name both teams in source order.</item>
/// <item>No score is shown unless the source stated it; missing map data is called out, not filled in.</item>
/// <item>Every message links the source and shows data freshness; demo data is labelled TEST/DEMO.</item>
/// </list>
/// </summary>
public sealed class NotificationRenderer(ILocalizer localizer, EsportsDataMode mode)
{
    public const uint ReminderColor = 0x1C7ED6;
    public const uint ResultColor = 0x495057; // neutral on purpose (no winner colour, spoiler-safe)

    public static readonly IReadOnlyCollection<string> AllowedLinkHosts = ["liquipedia.net", "twitch.tv", "youtube.com", "kick.com", "github.com"];

    public OutgoingMessage Reminder(EsportsMatch match, string language, MentionPolicy pings, DateTimeOffset fetchedAt, DateTimeOffset? previousStart)
    {
        var lines = new List<string>();
        if (match.ScheduledStartUtc is { } start)
        {
            lines.Add(L(language, "esports.reminder.starts", DiscordText.Timestamp(start, 'F'), DiscordText.Timestamp(start, 'R')));
            if (!match.StartTimeExact)
                lines.Add(L(language, "esports.reminder.estimated"));
        }

        if (previousStart is { } previous && match.ScheduledStartUtc != previous)
            lines.Add(L(language, "esports.reminder.time_updated", DiscordText.Timestamp(previous, 'f')));
        lines.Add(L(language, "esports.reminder.planned_note"));
        lines.Add(Freshness(language, fetchedAt));

        var fields = CommonFields(match, language);
        var streams = match.Streams.Select(s => Link(s.Url) is { } url ? $"[{s.Platform}]({url})" : null)
            .OfType<string>().Take(5).ToList();
        if (streams.Count > 0)
            fields.Add(new EmbedField(L(language, "esports.field.streams"), string.Join(" · ", streams), false));

        return new OutgoingMessage(
            Content(pings),
            new MessageEmbed(
                Demo(language) + L(language, "esports.reminder.title", Name(match.A, language), Name(match.B, language)),
                string.Join("\n", lines),
                Link(match.SourceUrl),
                fields,
                Footer(language),
                fetchedAt,
                ReminderColor),
            pings);
    }

    public OutgoingMessage Result(EsportsMatch match, string language, bool spoiler, MentionPolicy pings, DateTimeOffset fetchedAt)
    {
        // Spoiler mode: a single fixed-layout line (both teams in source order) inside the spoiler. The winner's name is
        // NOT repeated and per-map lines are omitted, because the blurred width/height of those would reveal the result.
        var description = spoiler
            ? L(language, "esports.result.spoiler_hint") + "\n" + DiscordText.Spoiler(SpoilerLine(match, language)) + "\n" + L(language, "esports.result.spoiler_maps_hidden")
            : string.Join("\n", ResultLines(match, language));

        var fields = CommonFields(match, language);
        var maps = spoiler ? [] : MapLines(match, language);
        if (maps.Count > 0)
            fields.Add(new EmbedField(L(language, "esports.field.maps"), string.Join("\n", maps), false));

        return new OutgoingMessage(
            Content(pings),
            new MessageEmbed(
                Demo(language) + L(language, "esports.result.title", Name(match.A, language), Name(match.B, language)),
                description + "\n" + Freshness(language, fetchedAt),
                Link(match.SourceUrl),
                fields,
                Footer(language),
                fetchedAt,
                ResultColor),
            pings);
    }

    /// <summary>Result as one line whose visible length does not depend on who won.</summary>
    public string SpoilerLine(EsportsMatch match, string language)
    {
        if (match.SeriesScoreKnown)
            return L(language, "esports.result.score", NameMd(match.A, language), match.A.Score, match.B.Score, NameMd(match.B, language));
        if (match.IsForfeit && match.WinnerIndex is { } w)
            return L(language, "esports.result.score", NameMd(match.A, language), w == 0 ? "W" : "FF", w == 0 ? "FF" : "W", NameMd(match.B, language));
        return L(language, "esports.result.series_unknown");
    }

    /// <summary>Plain (non-spoiler) result lines.</summary>
    public List<string> ResultLines(EsportsMatch match, string language)
    {
        var lines = new List<string>();
        if (match.SeriesScoreKnown)
            lines.Add(L(language, "esports.result.score", NameMd(match.A, language), match.A.Score, match.B.Score, NameMd(match.B, language)));
        else
            lines.Add(L(language, "esports.result.series_unknown"));

        if (match.IsDraw)
            lines.Add(L(language, "esports.result.draw"));
        else if (match.WinnerName is { } winner)
            lines.Add(L(language, match.IsForfeit ? "esports.result.forfeit" : "esports.result.winner", DiscordText.Untrusted(winner, 100)));
        else
            lines.Add(L(language, "esports.result.winner_unknown"));

        if (match.SeriesScoreKnown && !match.MapsComplete && !match.IsForfeit)
            lines.Add(L(language, "esports.result.maps_incomplete"));
        return lines;
    }

    public List<string> MapLines(EsportsMatch match, string language) =>
        match.Maps
            .Where(m => m.Status == GameStatus.Played && m.ScoreA is not null && m.ScoreB is not null)
            .Select(m => L(language, "esports.map_line", m.Index,
                m.MapName is null ? L(language, "esports.map_tba") : DiscordText.Untrusted(m.MapName, 40), m.ScoreA, m.ScoreB))
            .ToList();

    public string MatchLine(EsportsMatch match, string language, bool hideResult, DateTimeOffset now)
    {
        var when = match.ScheduledStartUtc is { } s ? DiscordText.Timestamp(s, 'f') : "?";
        var teams = $"{NameMd(match.A, language)} vs {NameMd(match.B, language)}";
        var status = match.Status switch
        {
            MatchStatus.Finished when match.SeriesScoreKnown => hideResult
                ? DiscordText.Spoiler($"{match.A.Score}–{match.B.Score}")
                : $"**{match.A.Score}–{match.B.Score}**",
            MatchStatus.Finished => L(language, "esports.status.finished"),
            MatchStatus.Cancelled => L(language, "esports.status.not_played"),
            MatchStatus.Scheduled when match.ScheduledStartUtc is { } start && start <= now =>
                L(language, "esports.status.awaiting_result"),
            MatchStatus.Live => L(language, "esports.status.live"),
            _ => match.BestOf is { } bo ? string.Create(CultureInfo.InvariantCulture, $"BO{bo}") : "",
        };
        return $"{when} — {teams} · {status} · {DiscordText.Untrusted(match.Tournament.Name, 80)}";
    }

    public string Name(MatchOpponent opponent, string language) => opponent.Kind switch
    {
        OpponentKind.Team => DiscordText.UntrustedPlain(opponent.Team!.Name, 60),
        OpponentKind.Tbd => L(language, "esports.tbd"),
        _ => L(language, "esports.unknown_opponent"),
    };

    /// <summary>Team name for markdown contexts (descriptions, fields).</summary>
    public string NameMd(MatchOpponent opponent, string language) => opponent.Kind switch
    {
        OpponentKind.Team => DiscordText.Untrusted(opponent.Team!.Name, 60),
        OpponentKind.Tbd => L(language, "esports.tbd"),
        _ => L(language, "esports.unknown_opponent"),
    };

    public string Freshness(string language, DateTimeOffset fetchedAt) =>
        L(language, "esports.freshness", DiscordText.Timestamp(fetchedAt, 'R'));

    // Demo data is synthetic: it must not claim a real source or link to real pages/channels (a synthetic stream name
    // could belong to a stranger). Found in the first live TEST/DEMO notification, 2026-09-25.
    public string Footer(string language) =>
        mode.IsDemo ? L(language, "esports.demo_footer") + " • " + L(language, "esports.footer_source_demo") : L(language, "esports.footer_source");

    /// <summary>Safe outgoing link, or none at all for demo data.</summary>
    public string? Link(string? url) => mode.IsDemo ? null : DiscordText.SafeUrl(url, AllowedLinkHosts);

    public string Demo(string language) => mode.IsDemo ? L(language, "esports.demo_label") + " " : "";

    private List<EmbedField> CommonFields(EsportsMatch match, string language)
    {
        var fields = new List<EmbedField>
        {
            new(L(language, "esports.field.tournament"), DiscordText.Untrusted(match.Tournament.Name, 200), true),
        };
        if (match.Stage is { } stage)
            fields.Add(new EmbedField(L(language, "esports.field.stage"), DiscordText.Untrusted(stage, 100), true));
        if (match.BestOf is { } bo)
            fields.Add(new EmbedField(L(language, "esports.field.format"), string.Create(CultureInfo.InvariantCulture, $"BO{bo}"), true));
        return fields;
    }

    private static string? Content(MentionPolicy pings) =>
        pings.Roles.Count == 0 ? null : string.Join(' ', pings.Roles.Select(DiscordText.RoleMention));

    private string L(string language, string key, params object?[] args) => localizer.Get(language, key, args);

    public static string SourceName => LiquipediaParser.Source;
}
