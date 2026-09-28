using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ToroSquad.Core;

namespace ToroSquad.Modules.Lfg.Domain;

/// <summary>
/// Lifecycle of a listing. <see cref="Open"/> and <see cref="Full"/> are active (Full reopens when someone leaves before
/// expiry); every other state is terminal. <see cref="Orphaned"/>: the listing message was deleted in Discord (or its
/// channel is gone), so nobody can interact with it any more — kept apart from <see cref="Closed"/> (an explicit decision
/// of the owner or a moderator) so history and diagnostics stay truthful.
/// </summary>
public enum LfgStatus
{
    Open = 0,
    Full = 1,
    Closed = 2,
    Expired = 3,
    Orphaned = 4,
}

/// <summary>
/// A member's answer to a listing. Only <see cref="Joined"/> players fill slots, count for Full, are pinged by event notices
/// and may use the voice action. <see cref="Maybe"/> is shown separately and never counts. <see cref="Waitlisted"/> asked
/// to join a full team: never counts, never pinged, no voice; it holds a durable place in a first-come first-served queue
/// and becomes Joined by itself when a slot frees up. The owner is always <see cref="Joined"/>.
/// </summary>
public enum LfgResponse
{
    Joined = 0,
    Maybe = 1,
    Waitlisted = 2,
}

/// <summary>
/// Progress of one optional event notice (30-minute reminder or start). Handled exactly once: <see cref="Queued"/> = staged
/// in the outbox together with this marker (one transaction); <see cref="Skipped"/> = consumed without a message (too
/// late, module disabled, guild not allowed) so a restart or a re-enabled module never sends it late.
/// </summary>
public enum LfgNoticeState
{
    Pending = 0,
    Queued = 1,
    Skipped = 2,
}

/// <summary>
/// One group-finder listing as the rest of the module sees it. Game-agnostic by design: the bot only knows that
/// <see cref="Owner"/> looks for <see cref="MaxPlayers"/> players for <see cref="GameName"/> and wrote <see cref="Details"/>
/// — it never interprets either text. <see cref="Players"/> are the Joined players (owner first, then join order),
/// <see cref="Maybe"/> the undecided ones, <see cref="Waitlist"/> the queue (first in line first); all are Discord user ids,
/// display names are never stored. <see cref="EventAt"/> is
/// when the activity starts (null = now); <see cref="ExpiresAt"/> is when the listing stops being usable (a separate
/// concept). <see cref="VoiceChannel"/> is an optional guild voice channel. <see cref="Version"/> changes with every stored state change (used to detect a card drawn from an older state).
/// </summary>
public sealed record LfgListingView(
    long Id,
    GuildId Guild,
    ChannelId Channel,
    MessageId? Message,
    UserId Owner,
    string GameName,
    string? Details,
    int MaxPlayers,
    LfgStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<UserId> Players,
    long Version = 0,
    IReadOnlyList<UserId>? Maybe = null,
    DateTimeOffset? EventAt = null,
    ChannelId? VoiceChannel = null,
    bool NotifyBeforeStart = false,
    bool NotifyAtStart = false,
    IReadOnlyList<UserId>? Waitlist = null)
{
    public bool IsActive => Status is LfgStatus.Open or LfgStatus.Full;

    public IReadOnlyList<UserId> MaybePlayers => Maybe ?? [];

    /// <summary>The waitlist in queue order: the first one takes the next free slot.</summary>
    public IReadOnlyList<UserId> WaitlistedPlayers => Waitlist ?? [];
}

public enum LfgDraftError
{
    None = 0,
    GameMissing = 1,
    GameTooShort = 2,
    GameTooLong = 3,
    DetailsTooLong = 4,
    PlayersOutOfRange = 5,
    DurationInvalid = 6,
    NoticeNeedsStart = 8,
    DateFormat = 10,
    DateNotInTimeZone = 11,
    DateAmbiguous = 12,
    DateNotInFuture = 13,
    DateTooFar = 14,
    TimeZoneInvalid = 15,

    /// <summary>Edit: the activity already started (or its start notice was handled), so its start can no longer move.</summary>
    StartLocked = 16,

    /// <summary>Edit: fewer slots than players who already joined.</summary>
    PlayersBelowJoined = 17,

    /// <summary>Edit: the new duration would end the listing in the past.</summary>
    ExpiryPassed = 18,
}

/// <summary>
/// When the activity starts: now (null <c>EventAt</c>) or an absolute instant (a date and time typed by the user, already
/// resolved in the guild's time zone). Nothing after this point (expiry, notices, card) knows how it was typed.
/// </summary>
public sealed record LfgStart
{
    private LfgStart(DateTimeOffset? at) => At = at;

    public static LfgStart Now { get; } = new((DateTimeOffset?)null);

    public DateTimeOffset? At { get; }

    public bool IsNow => At is null;

    public static LfgStart AtInstant(DateTimeOffset at) => new(at.ToUniversalTime());
}

/// <summary>A validated request to open a listing (normalized texts, bounded size, one of the offered durations, one start).</summary>
public sealed record LfgDraft(string GameName, string? Details, int MaxPlayers, TimeSpan Duration, LfgStart? Start = null)
{
    /// <summary>
    /// <c>EventAt</c> from the start (null = now); the listing stays usable <see cref="Duration"/> after the start:
    /// <c>ExpiresAt = (EventAt ?? now) + Duration</c>, whichever way the start was given.
    /// </summary>
    public (DateTimeOffset? EventAt, DateTimeOffset ExpiresAt) Schedule(DateTimeOffset now)
    {
        var eventAt = Start?.At;
        return (eventAt, (eventAt ?? now) + Duration);
    }
}

/// <summary>
/// Start dates typed by the user, read as wall-clock time in the guild's time zone. The only forms are a full date and time:
/// <c>GG.AA.YYYY SS:DD</c> and <c>GG.AA.YY SS:DD</c> (leading zeros optional), plus ISO <c>YYYY-AA-GG SS:DD</c> for
/// compatibility — no relative times, no words. Parsing is explicit (no culture, no machine setting): a two-digit year is the
/// year of that century nearest to the reference year (the guild's current year), never <c>Calendar.TwoDigitYearMax</c>. A
/// wall-clock time that does not exist (clocks jump forward) or exists twice (clocks go back) in the zone is refused
/// rather than guessed.
/// </summary>
public static partial class LfgEventDate
{
    /// <summary>At least this far ahead (clock skew, minute-precision input): no event "right now" through a date.</summary>
    public static readonly TimeSpan MinLead = TimeSpan.FromMinutes(1);

    /// <summary>At most this far ahead.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(365);

    /// <summary>
    /// The wall-clock date and time written in one of the accepted forms (no time zone, no range check);
    /// <paramref name="referenceYear"/> resolves a two-digit year.
    /// </summary>
    public static bool TryReadWallClock(string? text, int referenceYear, out DateTime local)
    {
        local = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return false;
        var match = DatePattern().Match(value);
        if (!match.Success)
            match = IsoPattern().Match(value);
        if (!match.Success)
            return false;

        int Part(string name) => int.Parse(match.Groups[name].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var year = match.Groups["y"].Value.Length == 2 ? TwoDigitYear(Part("y"), referenceYear) : Part("y");
        var (month, day, hour, minute) = (Part("m"), Part("d"), Part("h"), Part("min"));
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
            return false;
        local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>"26" → the year ending in 26 nearest to <paramref name="referenceYear"/> (2026 in 2026, 2126 never).</summary>
    public static int TwoDigitYear(int twoDigits, int referenceYear)
    {
        var year = referenceYear - referenceYear % 100 + twoDigits;
        if (year < referenceYear - 50)
            year += 100;
        else if (year > referenceYear + 49)
            year -= 100;
        return year;
    }

    public static (DateTimeOffset? At, LfgDraftError Error) Resolve(string text, TimeZoneInfo zone, DateTimeOffset now)
    {
        var year = TimeZoneInfo.ConvertTime(now, zone).Year;
        if (!TryReadWallClock(text, year, out var local))
            return (null, LfgDraftError.DateFormat);
        // Far outside the one-year window (e.g. year 1 or 9999): refused before any time-zone arithmetic could overflow.
        if (local.Year < year - 1)
            return (null, LfgDraftError.DateNotInFuture);
        if (local.Year > year + 1)
            return (null, LfgDraftError.DateTooFar);
        if (zone.IsInvalidTime(local))
            return (null, LfgDraftError.DateNotInTimeZone);
        if (zone.IsAmbiguousTime(local))
            return (null, LfgDraftError.DateAmbiguous);

        var at = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        if (at < now + MinLead)
            return (null, LfgDraftError.DateNotInFuture);
        if (at > now + MaxAhead)
            return (null, LfgDraftError.DateTooFar);
        return (at, LfgDraftError.None);
    }

    [GeneratedRegex(@"^(?<d>[0-9]{1,2})\.(?<m>[0-9]{1,2})\.(?<y>[0-9]{4}|[0-9]{2})\s+(?<h>[0-9]{1,2}):(?<min>[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^(?<y>[0-9]{4})-(?<m>[0-9]{1,2})-(?<d>[0-9]{1,2})\s+(?<h>[0-9]{1,2}):(?<min>[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoPattern();
}

/// <summary>
/// Input rules shared by the slash command (Discord's own option bounds) and the server-side check (never trust the client).
/// Lengths count Unicode characters (runes), like Discord's option limits, not UTF-16 units.
/// </summary>
public static class LfgRules
{
    public const int GameNameMinLength = 2;
    public const int GameNameMaxLength = 50;

    /// <summary>One short line for the card: mode, rank, roles, plan… — enough for any game, never a wall of text.</summary>
    public const int DetailsMaxLength = 200;

    public const int MinPlayers = 2;

    /// <summary>Upper bound of the slash option. Lfg:MaxPlayersPerListing can only lower it.</summary>
    public const int HardMaxPlayers = 50;

    /// <summary>The optional durations offered by /ekip (no free minute input); no choice = the configured default.</summary>
    public static IReadOnlyList<int> DurationChoicesMinutes { get; } = [60, 120, 180];

    /// <summary>The optional reminder goes out this long before the start, and only before the start.</summary>
    public static readonly TimeSpan ReminderLead = TimeSpan.FromMinutes(30);

    /// <summary>A start notice is still sent this long after the start (bot briefly down); later it is skipped, never sent stale.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(5);

    private const int ZeroWidthJoiner = 0x200D;

    /// <summary>
    /// <paramref name="startAt"/>: empty = now, otherwise a date and time read in <paramref name="zone"/> relative to
    /// <paramref name="now"/>.
    /// </summary>
    public static (LfgDraft? Draft, LfgDraftError Error) Validate(string? game, string? details, int players, int? durationMinutes, int maxPlayers, int defaultMinutes,
        bool notices = false, string? startAt = null, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var (name, text, basics) = ValidateTexts(game, details, players, maxPlayers);
        if (basics != LfgDraftError.None)
            return (null, basics);

        if (durationMinutes is { } minutes && !DurationChoicesMinutes.Contains(minutes))
            return (null, LfgDraftError.DurationInvalid);

        var (begin, startError) = ResolveStart(startAt, zone, now);
        if (begin is null)
            return (null, startError);

        if (notices && begin.IsNow)
            return (null, LfgDraftError.NoticeNeedsStart); // "now" has no reminder window and nothing to announce later

        return (new LfgDraft(name!, text, players, TimeSpan.FromMinutes(durationMinutes ?? defaultMinutes), begin), LfgDraftError.None);
    }

    /// <summary>The creator's texts and the team size (shared by create and edit).</summary>
    public static (string? Game, string? Details, LfgDraftError Error) ValidateTexts(string? game, string? details, int players, int maxPlayers)
    {
        var name = Normalize(game);
        if (name is null)
            return (null, null, LfgDraftError.GameMissing);
        var nameLength = Length(name);
        if (nameLength < GameNameMinLength)
            return (null, null, LfgDraftError.GameTooShort);
        if (nameLength > GameNameMaxLength)
            return (null, null, LfgDraftError.GameTooLong);

        var text = Normalize(details);
        if (text is not null && Length(text) > DetailsMaxLength)
            return (null, null, LfgDraftError.DetailsTooLong);

        if (players < MinPlayers || players > Math.Min(maxPlayers, HardMaxPlayers))
            return (null, null, LfgDraftError.PlayersOutOfRange);
        return (name, text, LfgDraftError.None);
    }

    /// <summary>
    /// The start (shared by create and edit): empty = now, otherwise a date and time read in <paramref name="zone"/> relative
    /// to <paramref name="now"/>.
    /// </summary>
    public static (LfgStart? Start, LfgDraftError Error) ResolveStart(string? startAt, TimeZoneInfo? zone, DateTimeOffset? now)
    {
        var custom = Normalize(startAt);
        if (custom is null)
            return (LfgStart.Now, LfgDraftError.None);
        if (zone is null || now is null)
            return (null, LfgDraftError.TimeZoneInvalid);
        var (at, dateError) = LfgEventDate.Resolve(custom, zone, now.Value);
        return at is null ? (null, dateError) : (LfgStart.AtInstant(at.Value), LfgDraftError.None);
    }

    /// <summary>Drops control/format characters, collapses whitespace runs to one space and trims; null when nothing is left.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                space = sb.Length > 0;
                continue;
            }

            // Format characters (bidi overrides, zero-width spaces) can disguise text; the zero-width joiner stays for emoji.
            if (Rune.IsControl(rune) || (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format && rune.Value != ZeroWidthJoiner))
                continue;
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(rune.ToString());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public static int Length(string value) => value.EnumerateRunes().Count();
}

/// <summary>The form's number fields, read exactly: anything unreadable becomes a value the rules refuse.</summary>
public static partial class LfgFormText
{
    /// <summary>Returned for unreadable input; always outside every allowed range.</summary>
    public const int Invalid = -1;

    /// <summary>"6" → 6; anything else (empty, "altı", "6 kişi") → <see cref="Invalid"/>.</summary>
    public static int Players(string? text)
    {
        var value = LfgRules.Normalize(text);
        return value is not null && PlayersPattern().IsMatch(value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : Invalid;
    }

    /// <summary>
    /// The duration as the settings step stores it (<see cref="LfgRules.DurationChoicesMinutes"/> written as whole hours,
    /// e.g. "2", or a stored non-hour duration as "90 dk"): empty → null (the default duration); else minutes or
    /// <see cref="Invalid"/>.
    /// </summary>
    public static int? DurationMinutes(string? text)
    {
        var value = LfgRules.Normalize(text);
        if (value is null)
            return null;
        var match = DurationPattern().Match(value);
        if (!match.Success)
            return Invalid;
        var n = int.Parse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        return match.Groups["dk"].Success ? n : n * 60;
    }

    [GeneratedRegex(@"^[0-9]{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlayersPattern();

    [GeneratedRegex(@"^(?<n>[0-9]{1,4})(?<dk> dk)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
