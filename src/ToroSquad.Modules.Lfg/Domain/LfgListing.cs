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
/// A member's answer to a listing. Only <see cref="Joined"/> players fill slots, count for Full and are pinged by event
/// notices; <see cref="Maybe"/> is shown separately and never counts. The owner is always <see cref="Joined"/>.
/// </summary>
public enum LfgResponse
{
    Joined = 0,
    Maybe = 1,
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
/// <see cref="Maybe"/> the undecided ones; both are Discord user ids, display names are never stored. <see cref="EventAt"/> is
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
    bool NotifyAtStart = false)
{
    public bool IsActive => Status is LfgStatus.Open or LfgStatus.Full;

    public IReadOnlyList<UserId> MaybePlayers => Maybe ?? [];
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
    StartInvalid = 7,
    NoticeNeedsStart = 8,
    StartConflict = 9,
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
/// When the activity starts — exactly one source: now (null <c>EventAt</c>), a relative choice, or an absolute instant
/// (a custom date already resolved in the guild's time zone). Every source ends as the same <c>EventAt</c>; nothing after
/// this point (expiry, notices, card) knows or cares which one it was.
/// </summary>
public sealed record LfgStart
{
    private LfgStart(TimeSpan? delay, DateTimeOffset? at)
    {
        Delay = delay;
        At = at;
    }

    public static LfgStart Now { get; } = new(null, null);

    public TimeSpan? Delay { get; }

    public DateTimeOffset? At { get; }

    public bool IsNow => Delay is null && At is null;

    public static LfgStart After(TimeSpan delay) => delay > TimeSpan.Zero ? new LfgStart(delay, null) : Now;

    public static LfgStart AtInstant(DateTimeOffset at) => new(null, at.ToUniversalTime());

    public DateTimeOffset? EventAt(DateTimeOffset now) => Delay is { } delay ? now + delay : At;
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
        var eventAt = (Start ?? LfgStart.Now).EventAt(now);
        return (eventAt, (eventAt ?? now) + Duration);
    }
}

/// <summary>
/// Custom start dates typed by the user ("05.10.2026 21:30"), read as wall-clock time in the guild's time zone. Parsing is
/// explicit and culture-independent (never the machine locale). A wall-clock time that does not exist (clocks jump forward)
/// or exists twice (clocks go back) in that zone is refused rather than guessed.
/// </summary>
public static class LfgEventDate
{
    /// <summary>Main format GG.AA.YYYY SS:DD (leading zeros optional); ISO "yyyy-MM-dd HH:mm" is also accepted.</summary>
    public static IReadOnlyList<string> Formats { get; } = ["d.M.yyyy H:mm", "yyyy-M-d H:mm"];

    /// <summary>At least this far ahead (clock skew, minute-precision input): no event "right now" through a date.</summary>
    public static readonly TimeSpan MinLead = TimeSpan.FromMinutes(1);

    /// <summary>At most this far ahead.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(365);

    /// <summary>The wall-clock date and time written in one of the <see cref="Formats"/> (no time zone, no range check).</summary>
    public static bool TryReadWallClock(string? text, out DateTime local)
    {
        local = default;
        if (string.IsNullOrWhiteSpace(text) || !DateTime.TryParseExact(text.Trim(), Formats.ToArray(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return false;
        local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return true;
    }

    public static (DateTimeOffset? At, LfgDraftError Error) Resolve(string text, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!TryReadWallClock(text, out var local))
            return (null, LfgDraftError.DateFormat);
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

    /// <summary>A relative start ("2 saat") reaches as far ahead as a custom date: 0 = now.</summary>
    public static readonly int MaxStartMinutes = (int)LfgEventDate.MaxAhead.TotalMinutes;

    /// <summary>The optional reminder goes out this long before the start, and only before the start.</summary>
    public static readonly TimeSpan ReminderLead = TimeSpan.FromMinutes(30);

    /// <summary>A start notice is still sent this long after the start (bot briefly down); later it is skipped, never sent stale.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(5);

    private const int ZeroWidthJoiner = 0x200D;

    /// <summary>
    /// <paramref name="startMinutes"/> (a relative start in minutes, 0 = now) and <paramref name="startAt"/> (a custom date, read in
    /// <paramref name="zone"/> relative to <paramref name="now"/>) are alternatives: giving both is refused here, not only
    /// in the command. A blank <paramref name="startAt"/> counts as not given.
    /// </summary>
    public static (LfgDraft? Draft, LfgDraftError Error) Validate(string? game, string? details, int players, int? durationMinutes, int maxPlayers, int defaultMinutes,
        int? startMinutes = null, bool notices = false, string? startAt = null, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var (name, text, basics) = ValidateTexts(game, details, players, maxPlayers);
        if (basics != LfgDraftError.None)
            return (null, basics);

        if (durationMinutes is { } minutes && !DurationChoicesMinutes.Contains(minutes))
            return (null, LfgDraftError.DurationInvalid);

        var (begin, startError) = ResolveStart(startMinutes, startAt, zone, now);
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
    /// One start from either a relative start (minutes, 0 = now) or a custom date read in <paramref name="zone"/> relative to
    /// <paramref name="now"/> (shared by create and edit). Giving both is refused. A blank date counts as not given.
    /// </summary>
    public static (LfgStart? Start, LfgDraftError Error) ResolveStart(int? startMinutes, string? startAt, TimeZoneInfo? zone, DateTimeOffset? now)
    {
        var custom = Normalize(startAt);
        if (startMinutes is not null && custom is not null)
            return (null, LfgDraftError.StartConflict);

        if (custom is not null)
        {
            if (zone is null || now is null)
                return (null, LfgDraftError.TimeZoneInvalid);
            var (at, dateError) = LfgEventDate.Resolve(custom, zone, now.Value);
            return at is null ? (null, dateError) : (LfgStart.AtInstant(at.Value), LfgDraftError.None);
        }

        if (startMinutes is { } start)
            return start < 0 || start > MaxStartMinutes ? (null, LfgDraftError.StartInvalid) : (LfgStart.After(TimeSpan.FromMinutes(start)), LfgDraftError.None);
        return (LfgStart.Now, LfgDraftError.None);
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

/// <summary>
/// What the creator typed into the form's start field: nothing (or "şimdi") = now; a relative start ("30 dk", "1,5 saat",
/// "2 saat", "1 gün") = minutes from now; anything else is a custom date for <see cref="LfgEventDate"/>. Deliberately not a
/// natural-language parser: a unit is required for a relative start, so "1.5.2026 21:00" can never be read as hours.
/// </summary>
public sealed record LfgStartText(int? Minutes, string? At)
{
    public static LfgStartText Now { get; } = new(null, null);

    /// <summary>Minutes of a relative start that is not a whole number of minutes (e.g. "1,33 saat").</summary>
    public const int Unusable = -1;

    private static readonly string[] NowWords = ["şimdi", "simdi", "hemen", "now"];

    public bool IsNow => Minutes is null && At is null;

    public static LfgStartText Parse(string? text)
    {
        var value = LfgRules.Normalize(text);
        if (value is null || NowWords.Contains(value.ToLowerInvariant()))
            return Now;
        var match = LfgFormText.RelativePattern().Match(value.ToLowerInvariant());
        if (!match.Success)
            return new LfgStartText(null, value);
        var minutes = LfgFormText.ToMinutes(match.Groups["n"].Value, match.Groups["unit"].Value, defaultUnitMinutes: 1);
        return minutes switch
        {
            null => new LfgStartText(Unusable, null),
            0 => Now,
            _ => new LfgStartText(minutes, null),
        };
    }
}

/// <summary>The form's number fields, read leniently but exactly: anything unreadable becomes a value the rules refuse.</summary>
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

    /// <summary>Empty → null (the default duration); "2", "2 saat", "1,5 saat", "90 dk" → minutes; else <see cref="Invalid"/>.</summary>
    public static int? DurationMinutes(string? text)
    {
        var value = LfgRules.Normalize(text);
        if (value is null)
            return null;
        var match = DurationPattern().Match(value.ToLowerInvariant());
        return match.Success ? ToMinutes(match.Groups["n"].Value, match.Groups["unit"].Value, defaultUnitMinutes: 60) ?? Invalid : Invalid;
    }

    /// <summary>Whole minutes of "number unit", or null when it is not a whole number of minutes (or absurdly large).</summary>
    internal static int? ToMinutes(string number, string unit, int defaultUnitMinutes)
    {
        if (!decimal.TryParse(number.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            return null;
        var factor = unit switch
        {
            "" => defaultUnitMinutes,
            "dk" or "dak" or "dakika" or "min" or "m" => 1,
            "sa" or "saat" or "s" or "h" => 60,
            _ => 1440, // gün / gun / g / d
        };
        var minutes = n * factor;
        return minutes != decimal.Truncate(minutes) || minutes > int.MaxValue ? null : (int)minutes;
    }

    [GeneratedRegex(@"^[0-9]{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlayersPattern();

    [GeneratedRegex(@"^(?<n>[0-9]{1,3}(?:[.,][0-9]{1,2})?)\s*(?<unit>dk|dak|dakika|min|m|sa|saat|s|h|)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"^(?<n>[0-9]{1,6}(?:[.,][0-9]{1,2})?)\s*(?<unit>dk|dak|dakika|min|m|sa|saat|s|h|gün|gun|g|d)\.?(?:\s*sonra)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex RelativePattern();
}
