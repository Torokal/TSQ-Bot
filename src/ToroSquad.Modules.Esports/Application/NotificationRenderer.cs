using System.Globalization;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;
using ToroSquad.Modules.Esports.Providers.PandaScore;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// Builds compact match cards (docs/NOTIFICATIONS.md): a title that names the match, ONE status line, at most the
/// "Event" and "Format" fields (+ "New time" for reschedules), an optional "Match Page" link and the source attribution.
/// No maps, streams, rosters, freshness lines or internal ids. Rules enforced here (and tested):
/// <list type="bullet">
/// <item>All provider text is untrusted: mentions/markdown/links defused; only validated URLs become links.</item>
/// <item>Spoiler mode: title names both teams only; winner, score and forfeit live inside one fixed-layout
/// ||spoiler|| line; the colour does not depend on the winner.</item>
/// <item>Nothing the source did not state is shown (no invented winner, score, time or stars).</item>
/// <item>Demo data is labelled TEST/DEMO, never links anywhere and never claims a real source.</item>
/// </list>
/// </summary>
public sealed class NotificationRenderer(ILocalizer localizer, EsportsDataMode mode, IEsportsDataProvider? provider = null)
{
    public const uint ReminderColor = 0x1C7ED6;
    public const uint StartedColor = 0x1C7ED6;
    public const uint ResultColor = 0x2F9E44;
    public const uint ChangeColor = 0xF59F00;
    public const uint CancelledColor = 0xE03131;

    /// <summary>Hosts allowed for provider-supplied links in listings (rankings source etc.).</summary>
    public static readonly IReadOnlyCollection<string> AllowedLinkHosts = ["liquipedia.net", "twitch.tv", "youtube.com", "kick.com", "github.com"];

    public OutgoingMessage Reminder(EsportsMatch match, string language, MentionPolicy pings, DateTimeOffset fetchedAt, DateTimeOffset? previousStart)
    {
        var lines = new List<string>();
        if (match.ScheduledStartUtc is { } start)
            lines.Add(L(language, match.StartTimeExact ? "esports.card.reminder" : "esports.card.reminder_estimated", DiscordText.Timestamp(start, 'R')));
        if (previousStart is { } previous && match.ScheduledStartUtc != previous)
            lines.Add(L(language, "esports.reminder.time_updated", DiscordText.Timestamp(previous, 'f')));
        lines.Add(L(language, "esports.card.planned_note"));
        return Card(match, language, Title(match, language), lines, [], match.ScheduledStartUtc ?? fetchedAt, ReminderColor, pings);
    }

    /// <summary>Sent only on a provider-stated scheduled → running transition (never because the clock passed).</summary>
    public OutgoingMessage Started(EsportsMatch match, string language, MentionPolicy pings, DateTimeOffset observedAt) =>
        Card(match, language, Title(match, language), [L(language, "esports.card.started")], [], match.BeginAtUtc ?? observedAt, StartedColor, pings);

    public OutgoingMessage Postponed(EsportsMatch match, string language, DateTimeOffset observedAt) =>
        Card(match, language, Title(match, language), [L(language, "esports.card.postponed"), L(language, "esports.card.postponed_no_date")], [], observedAt, ChangeColor, MentionPolicy.None);

    public OutgoingMessage Rescheduled(EsportsMatch match, string language, DateTimeOffset newStartUtc, TimeZoneInfo zone, DateTimeOffset observedAt) =>
        Card(match, language, Title(match, language), [L(language, "esports.card.rescheduled")],
            [new EmbedField(L(language, "esports.field.new_time"), LocalTime(newStartUtc, zone), true)], observedAt, ChangeColor, MentionPolicy.None);

    public OutgoingMessage Cancelled(EsportsMatch match, string language, DateTimeOffset observedAt) =>
        Card(match, language, Title(match, language), [L(language, "esports.card.cancelled")], [], observedAt, CancelledColor, MentionPolicy.None);

    public OutgoingMessage Result(EsportsMatch match, string language, bool spoiler, MentionPolicy pings, DateTimeOffset fetchedAt)
    {
        string title;
        List<string> lines;
        if (spoiler)
        {
            // Everything result-related is inside ONE spoiler line whose visible layout does not depend on the winner.
            title = Title(match, language);
            lines = [L(language, "esports.card.finished"), L(language, "esports.card.spoiler", DiscordText.Spoiler(SpoilerLine(match, language)))];
        }
        else
        {
            title = match.SeriesScoreKnown
                ? L(language, "esports.card.score_title", Name(match.A, language), match.A.Score, match.B.Score, Name(match.B, language))
                : Title(match, language);
            lines = ResultLines(match, language);
        }

        return Card(match, language, title, lines, [], match.EndAtUtc ?? fetchedAt, ResultColor, pings);
    }

    /// <summary>Result as one line whose visible length does not depend on who won (spoiler content).</summary>
    public string SpoilerLine(EsportsMatch match, string language)
    {
        var line = match.SeriesScoreKnown
            ? L(language, "esports.result.score", NameMd(match.A, language), match.A.Score, match.B.Score, NameMd(match.B, language))
            : match.IsForfeit && match.WinnerIndex is { } w
                ? L(language, "esports.result.score", NameMd(match.A, language), w == 0 ? "W" : "FF", w == 0 ? "FF" : "W", NameMd(match.B, language))
                : L(language, "esports.result.series_unknown");
        return match.IsForfeit ? line + " · " + L(language, "esports.card.forfeit_short") : line;
    }

    /// <summary>Plain (non-spoiler) result status lines: winner / draw / forfeit — never guessed.</summary>
    public List<string> ResultLines(EsportsMatch match, string language)
    {
        var lines = new List<string>();
        if (match.IsForfeit)
            lines.Add(L(language, "esports.card.forfeit"));
        if (match.IsDraw)
            lines.Add(L(language, "esports.card.draw"));
        else if (match.WinnerName is { } winner)
            lines.Add(L(language, "esports.card.winner", DiscordText.Untrusted(winner, 100)));
        else if (!match.IsForfeit)
            lines.Add(L(language, "esports.card.finished_unknown"));
        return lines;
    }

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
            MatchStatus.Postponed => L(language, "esports.status.postponed"),
            MatchStatus.Live => L(language, "esports.status.live"),
            MatchStatus.Scheduled when match.ScheduledStartUtc is { } start && start <= now =>
                L(language, "esports.status.awaiting_result"),
            _ => match.BestOf is { } bo ? Format(bo) : "",
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

    /// <summary>Source attribution for listings (the configured provider).</summary>
    public string Footer(string language) => Footer(language, provider?.Id);

    /// <summary>
    /// Attribution required by the data source: PandaScore terms art. 6.4 ("Source: PandaScore"); Liquipedia CC BY-SA 3.0.
    /// Demo data is synthetic: it names no real source.
    /// </summary>
    public string Footer(string language, string? source) =>
        mode.IsDemo ? L(language, "esports.demo_footer") + " • " + L(language, "esports.footer_source_demo")
        : source == PandaScoreParser.Source ? L(language, "esports.footer_source_pandascore")
        : L(language, "esports.footer_source");

    public bool IsDemo => mode.IsDemo;

    /// <summary>Safe outgoing link, or none at all for demo data.</summary>
    public string? Link(string? url) => mode.IsDemo ? null : DiscordText.SafeUrl(url, AllowedLinkHosts);

    /// <summary>The "Match Page" target: verified HLTV → official → provider page → none. Demo data never links.</summary>
    public MatchPage? MatchPageFor(EsportsMatch match)
    {
        if (mode.IsDemo)
            return null;
        var links = match.Links ?? MatchLinks.None;
        if (links.ProviderMatchUrl is null && match.SourceUrl is not null)
            links = links with { ProviderMatchUrl = match.SourceUrl };
        return MatchLinkPolicy.Resolve(links, AllowedLinkHosts);
    }

    public string Demo(string language) => mode.IsDemo ? L(language, "esports.demo_label") + " " : "";

    public static string Format(int bestOf) => string.Create(CultureInfo.InvariantCulture, $"bo{bestOf}");

    public static string LocalTime(DateTimeOffset utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(utc, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private string Title(EsportsMatch match, string language) =>
        L(language, "esports.card.vs", Name(match.A, language), Name(match.B, language));

    private OutgoingMessage Card(EsportsMatch match, string language, string title, List<string> lines, List<EmbedField> extraFields, DateTimeOffset timestamp, uint color, MentionPolicy pings)
    {
        var fields = new List<EmbedField>
        {
            new(L(language, "esports.field.event"), DiscordText.Untrusted(match.Tournament.Name, 200), true),
        };
        if (match.BestOf is { } bo)
            fields.Add(new EmbedField(L(language, "esports.field.format"), Format(bo), true));
        fields.AddRange(extraFields);

        if (MatchPageFor(match) is { } page)
            lines.Add($"[{L(language, "esports.card.match_page")}]({page.Url})");

        return new OutgoingMessage(
            Content(pings),
            new MessageEmbed(Demo(language) + title, string.Join("\n", lines), null, fields, Footer(language, match.Key.Source), timestamp, color),
            pings);
    }

    private static string? Content(MentionPolicy pings) =>
        pings.Roles.Count == 0 ? null : string.Join(' ', pings.Roles.Select(DiscordText.RoleMention));

    private string L(string language, string key, params object?[] args) => localizer.Get(language, key, args);

    public static string SourceName => LiquipediaParser.Source;
}
