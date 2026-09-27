using System.Globalization;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

/// <summary>
/// Volleyball cards in TSQ's compact style, always from the followed team's perspective (Türkiye first, scores and set
/// points in the same order). Rules (tested): provider text is untrusted (mentions, markdown and links defused); nothing
/// the provider did not state is shown (no invented broadcaster, venue or winner); "the set is Türkiye's" wording only when
/// Türkiye actually won that set; demo data is labelled TEST/DEMO and names no real source; a logo is optional and only
/// from an allow-listed provider host (<see cref="ThumbnailPolicy"/>) — flags are plain Unicode text; rendering is a pure
/// function of persisted state, so an unchanged state never causes an edit.
/// </summary>
public sealed class VolleyballNotificationRenderer(ILocalizer localizer, VbDataMode mode, VbLogoHosts logoHosts)
{
    public const uint ReminderColor = 0x5865F2;
    public const uint LiveColor = 0xE30A17;
    public const uint WinColor = 0xF1C40F;
    public const uint NeutralResultColor = 0x99AAB5;
    public const uint PostponedColor = 0xF59F00;
    public const uint CancelledColor = 0xED4245;

    public bool IsDemo => mode.IsDemo;

    public OutgoingMessage Reminder(VbMatchView m, string language, MentionPolicy pings, string sourceKey)
    {
        var start = m.StartTimeUtc!.Value;
        var lines = new List<string>
        {
            L(language, "vb.card.reminder", DiscordText.Timestamp(start, 'R')),
            "🕒 " + DiscordText.Timestamp(start, 'F'),
        };
        lines.AddRange(Context(m, withVenue: true));
        return Message(Title(m, language, withScore: false), lines, pings, sourceKey, language, start, ReminderColor, m);
    }

    public OutgoingMessage Started(VbMatchView m, DateTimeOffset at, string language, MentionPolicy pings, string sourceKey)
    {
        var lines = new List<string> { L(language, "vb.card.started", DiscordText.Timestamp(at, 'R')) };
        lines.AddRange(Context(m, withVenue: false));
        return Message(Title(m, language, withScore: false), lines, pings, sourceKey, language, at, LiveColor, m);
    }

    /// <summary>Card for set <paramref name="setNumber"/>: the score and set list as they were right after that set.</summary>
    public OutgoingMessage SetFinished(VbMatchView m, int setNumber, DateTimeOffset at, string language, MentionPolicy pings, string sourceKey)
    {
        var sets = m.Sets.Where(s => s.Number <= setNumber).OrderBy(s => s.Number).ToList();
        var set = sets.Single(s => s.Number == setNumber);
        var followedWon = FollowedWon(m, set);
        var (followed, opponent) = Count(m, sets);
        var lines = new List<string>
        {
            // "The set is Türkiye's" only when Türkiye really won THIS set; otherwise a neutral line naming the winner.
            followedWon
                ? L(language, "vb.card.set_won", setNumber)
                : L(language, "vb.card.set_lost", setNumber, Name(m, followedSide: false, language)),
            "",
            SetLines(m, sets, language),
            "",
        };
        lines.AddRange(Context(m, withVenue: false));
        return Message(ScoreTitle(m, followed, opponent, language), lines, pings, sourceKey, language, at, followedWon ? LiveColor : NeutralResultColor, m);
    }

    public OutgoingMessage Final(VbMatchView m, DateTimeOffset at, string language, MentionPolicy pings, string sourceKey)
    {
        var followed = m.FollowedSets;
        var opponent = m.OpponentSets;
        var status = followed > opponent
            ? L(language, "vb.card.final_won")
            : followed < opponent ? L(language, "vb.card.final_lost", Name(m, followedSide: false, language)) : L(language, "vb.card.final_draw");
        var lines = new List<string> { status };
        if (m.Sets.Count > 0)
            lines.AddRange(["", SetLines(m, m.Sets.OrderBy(s => s.Number).ToList(), language)]);
        lines.Add("");
        lines.AddRange(Context(m, withVenue: false));
        return Message(ScoreTitle(m, followed, opponent, language), lines, pings, sourceKey, language, at, followed > opponent ? WinColor : NeutralResultColor, m);
    }

    public OutgoingMessage Postponed(VbMatchView m, DateTimeOffset at, string language, string sourceKey)
    {
        var lines = new List<string> { L(language, "vb.card.postponed") };
        if (m.StartTimeUtc is { } start)
            lines.Add(L(language, "vb.card.planned_time", DiscordText.Timestamp(start, 'F')));
        lines.AddRange(Context(m, withVenue: false));
        return Message(Title(m, language, withScore: false), lines, MentionPolicy.None, sourceKey, language, at, PostponedColor, m);
    }

    public OutgoingMessage Cancelled(VbMatchView m, DateTimeOffset at, string language, string sourceKey)
    {
        var lines = new List<string> { L(language, "vb.card.cancelled") };
        if (m.StartTimeUtc is { } start)
            lines.Add(L(language, "vb.card.planned_time", DiscordText.Timestamp(start, 'F')));
        lines.AddRange(Context(m, withVenue: false));
        return Message(Title(m, language, withScore: false), lines, MentionPolicy.None, sourceKey, language, at, CancelledColor, m);
    }

    /// <summary>"1. Set: 25-21" lines in the followed team's order (points exactly as the provider stated them).</summary>
    public string SetLines(VbMatchView m, IReadOnlyList<SetResult> sets, string language) =>
        string.Join("\n", sets.Select(s =>
        {
            var (f, o) = m.FollowedSide == FollowedSide.Home ? (s.HomePoints, s.AwayPoints) : (s.AwayPoints, s.HomePoints);
            var line = L(language, "vb.card.set_line", s.Number, f.ToString(CultureInfo.InvariantCulture), o.ToString(CultureInfo.InvariantCulture));
            return f > o ? "**" + line + "**" : line;
        }));

    /// <summary>"🇹🇷 Türkiye vs İtalya 🇮🇹".</summary>
    public string Title(VbMatchView m, string language, bool withScore) =>
        withScore ? ScoreTitle(m, m.FollowedSets, m.OpponentSets, language) : Clip(Demo(language) + L(language, "vb.card.title_vs", Side(m, true, language), Side(m, false, language, flagFirst: false)), DiscordLimits.EmbedTitleMax);

    private string ScoreTitle(VbMatchView m, int followed, int opponent, string language) =>
        Clip(Demo(language) + L(language, "vb.card.title_score", Side(m, true, language), followed, opponent, Side(m, false, language, flagFirst: false)), DiscordLimits.EmbedTitleMax);

    private string Side(VbMatchView m, bool followedSide, string language, bool flagFirst = true)
    {
        var flag = Flag(m, followedSide);
        var name = Name(m, followedSide, language);
        return flag is null ? name : flagFirst ? flag + " " + name : name + " " + flag;
    }

    /// <summary>Localized country name for a known federation code, otherwise the provider's name (untrusted, plain).</summary>
    public string Name(VbMatchView m, bool followedSide, string language)
    {
        var home = followedSide == (m.FollowedSide == FollowedSide.Home);
        var code = home ? m.HomeCode : m.AwayCode;
        var provided = home ? m.HomeName : m.AwayName;
        if (code is not null && localizer.HasKey(language, "vb.country." + code.ToUpperInvariant()))
            return L(language, "vb.country." + code.ToUpperInvariant());
        return DiscordText.UntrustedPlain(provided, 60);
    }

    private static string? Flag(VbMatchView m, bool followedSide)
    {
        var home = followedSide == (m.FollowedSide == FollowedSide.Home);
        return CountryFlags.For(home ? m.HomeCode : m.AwayCode);
    }

    private static bool FollowedWon(VbMatchView m, SetResult set) => set.HomeWon == (m.FollowedSide == FollowedSide.Home);

    private static (int Followed, int Opponent) Count(VbMatchView m, IReadOnlyList<SetResult> sets)
    {
        var homeWins = sets.Count(s => s.HomeWon);
        var awayWins = sets.Count - homeWins;
        return m.FollowedSide == FollowedSide.Home ? (homeWins, awayWins) : (awayWins, homeWins);
    }

    /// <summary>Competition (+ stage/round), then venue and VERIFIED broadcasters when the provider stated them.</summary>
    private static IEnumerable<string> Context(VbMatchView m, bool withVenue)
    {
        var competition = DiscordText.Untrusted(m.CompetitionName, 120);
        var detail = new[] { m.Stage, m.Round }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => DiscordText.Untrusted(s, 60)).ToList();
        yield return "🏆 " + competition + (detail.Count > 0 ? " · " + string.Join(" · ", detail) : "");
        if (withVenue)
        {
            var place = new[] { m.Venue, m.City }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => DiscordText.Untrusted(s, 80)).ToList();
            if (place.Count > 0)
                yield return "📍 " + string.Join(", ", place);
        }

        if (m.Broadcasts.Count > 0)
            yield return "📺 " + string.Join(" · ", m.Broadcasts.Take(3).Select(b => DiscordText.Untrusted(b, 40)));
    }

    private OutgoingMessage Message(string title, List<string> lines, MentionPolicy pings, string sourceKey, string language, DateTimeOffset at, uint color, VbMatchView m) =>
        new(Content(pings),
            new MessageEmbed(title, Clip(string.Join("\n", lines).Trim(), 3000), null, [], Footer(language, sourceKey), at, color, Thumbnail(m)),
            pings);

    /// <summary>The followed team's logo, only when the provider supplied one on an allow-listed host. Demo: never.</summary>
    private string? Thumbnail(VbMatchView m)
    {
        if (mode.IsDemo)
            return null;
        var url = m.FollowedSide == FollowedSide.Home ? m.HomeLogoUrl : m.AwayLogoUrl;
        return ThumbnailPolicy.Check(url, logoHosts.Hosts, out _);
    }

    /// <summary>Attribution footer. Demo data is synthetic and names no real source.</summary>
    public string Footer(string language, string sourceKey) =>
        mode.IsDemo ? L(language, "vb.demo_footer") : L(language, "vb.footer_source", L(language, sourceKey));

    public string Demo(string language) => mode.IsDemo ? L(language, "vb.demo_label") + " " : "";

    private static string? Content(MentionPolicy pings) =>
        pings.Roles.Count == 0 ? null : string.Join(' ', pings.Roles.Select(DiscordText.RoleMention));

    public static string Clip(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text.LastIndexOf('\n', Math.Max(0, max - 2));
        return (cut > 0 ? text[..cut] : text[..(max - 1)]) + "…";
    }

    private string L(string language, string key, params object?[] args) => localizer.Get(language, key, args);
}

/// <summary>Image hosts of the configured provider whose team logos may be shown (empty = flags only).</summary>
public sealed record VbLogoHosts(IReadOnlyCollection<string> Hosts);
