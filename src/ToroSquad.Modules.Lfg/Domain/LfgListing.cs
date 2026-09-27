using System.Text;
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
    ChannelId? VoiceChannel = null)
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

    public static (DateTimeOffset? At, LfgDraftError Error) Resolve(string text, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!DateTime.TryParseExact(text.Trim(), Formats.ToArray(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
            return (null, LfgDraftError.DateFormat);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
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

    /// <summary>Relative start choices (no date/time-zone parsing): 0 = now (the default).</summary>
    public static IReadOnlyList<int> StartChoicesMinutes { get; } = [0, 30, 60, 90, 120, 180, 240, 360, 480, 720, 1440];

    /// <summary>The optional reminder goes out this long before the start, and only before the start.</summary>
    public static readonly TimeSpan ReminderLead = TimeSpan.FromMinutes(30);

    /// <summary>A start notice is still sent this long after the start (bot briefly down); later it is skipped, never sent stale.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(5);

    private const int ZeroWidthJoiner = 0x200D;

    /// <summary>
    /// <paramref name="startMinutes"/> (a relative choice, 0 = now) and <paramref name="startAt"/> (a custom date, read in
    /// <paramref name="zone"/> relative to <paramref name="now"/>) are alternatives: giving both is refused here, not only
    /// in the command. A blank <paramref name="startAt"/> counts as not given.
    /// </summary>
    public static (LfgDraft? Draft, LfgDraftError Error) Validate(string? game, string? details, int players, int? durationMinutes, int maxPlayers, int defaultMinutes,
        int? startMinutes = null, bool notices = false, string? startAt = null, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var name = Normalize(game);
        if (name is null)
            return (null, LfgDraftError.GameMissing);
        var nameLength = Length(name);
        if (nameLength < GameNameMinLength)
            return (null, LfgDraftError.GameTooShort);
        if (nameLength > GameNameMaxLength)
            return (null, LfgDraftError.GameTooLong);

        var text = Normalize(details);
        if (text is not null && Length(text) > DetailsMaxLength)
            return (null, LfgDraftError.DetailsTooLong);

        if (players < MinPlayers || players > Math.Min(maxPlayers, HardMaxPlayers))
            return (null, LfgDraftError.PlayersOutOfRange);

        if (durationMinutes is { } minutes && !DurationChoicesMinutes.Contains(minutes))
            return (null, LfgDraftError.DurationInvalid);

        var custom = Normalize(startAt);
        if (startMinutes is not null && custom is not null)
            return (null, LfgDraftError.StartConflict);

        LfgStart begin;
        if (custom is not null)
        {
            if (zone is null || now is null)
                return (null, LfgDraftError.TimeZoneInvalid);
            var (at, dateError) = LfgEventDate.Resolve(custom, zone, now.Value);
            if (at is null)
                return (null, dateError);
            begin = LfgStart.AtInstant(at.Value);
        }
        else if (startMinutes is { } start)
        {
            if (!StartChoicesMinutes.Contains(start))
                return (null, LfgDraftError.StartInvalid);
            begin = LfgStart.After(TimeSpan.FromMinutes(start));
        }
        else
        {
            begin = LfgStart.Now;
        }

        if (notices && begin.IsNow)
            return (null, LfgDraftError.NoticeNeedsStart); // "now" has no reminder window and nothing to announce later

        return (new LfgDraft(name, text, players, TimeSpan.FromMinutes(durationMinutes ?? defaultMinutes), begin), LfgDraftError.None);
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
