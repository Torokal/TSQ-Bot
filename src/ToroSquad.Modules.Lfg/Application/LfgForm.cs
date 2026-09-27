using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>The five texts of the listing form exactly as typed (create and edit use the same form).</summary>
public sealed record LfgFormValues(string? Game, string? Players, string? Details, string? Start, string? Duration)
{
    public static LfgFormValues Empty { get; } = new(null, null, null, null, null);
}

/// <summary>
/// The listing form shared by create (/ekip) and edit (✏️ Düzenle): field ids and limits, the mapping of the typed texts to
/// the existing application input (the business rules stay in <see cref="LfgRules"/> and <see cref="LfgService"/>), and the
/// prefill of an existing listing. Nothing here decides anything about a listing.
/// </summary>
public static class LfgForm
{
    /// <summary>Modal custom id = prefix + draft id (a random id, never a listing id or any state).</summary>
    public const string ModalPrefix = "tsq:lfg:form:";

    public const string GameField = "game";
    public const string PlayersField = "players";
    public const string DetailsField = "details";
    public const string StartField = "start";
    public const string DurationField = "duration";

    public const int PlayersMaxLength = 3;
    public const int StartMaxLength = 40;
    public const int DurationMaxLength = 12;

    /// <summary>How a stored start is shown in the form: the main custom date format, in the guild's time zone.</summary>
    public const string DateFormat = "dd.MM.yyyy HH:mm";

    public static LfgCreateInput ToCreateInput(LfgFormValues form, bool notifyBeforeStart, bool notifyAtStart, ChannelId? voice)
    {
        var start = LfgStartText.Parse(form.Start);
        return new LfgCreateInput(form.Game, LfgFormText.Players(form.Players), form.Details, LfgFormText.DurationMinutes(form.Duration),
            start.Minutes, notifyBeforeStart, notifyAtStart, voice, start.At);
    }

    /// <summary>
    /// The form of an existing listing: its texts, size, start (in the guild's time zone; empty for a listing that started
    /// "now") and duration (whole hours as "2", otherwise "90 dk"). The edit compares what comes back against this to tell
    /// an untouched start or duration from a new one.
    /// </summary>
    public static LfgFormValues Prefill(LfgListingView listing, TimeZoneInfo zone) => new(
        listing.GameName,
        listing.MaxPlayers.ToString(CultureInfo.InvariantCulture),
        listing.Details,
        listing.EventAt is { } at ? FormatDate(at, zone) : null,
        FormatDuration(Duration(listing)));

    /// <summary>How long the listing stays usable after its start (after its creation for a listing that started "now").</summary>
    public static TimeSpan Duration(LfgListingView listing) => listing.ExpiresAt - (listing.EventAt ?? listing.CreatedAt);

    public static string FormatDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string FormatDuration(TimeSpan duration)
    {
        var minutes = (int)Math.Round(duration.TotalMinutes);
        return minutes % 60 == 0
            ? (minutes / 60).ToString(CultureInfo.InvariantCulture)
            : minutes.ToString(CultureInfo.InvariantCulture) + " dk";
    }

    /// <summary>The same value as stored text (whitespace does not count; letter case does — "casual" → "Casual" is an edit).</summary>
    public static bool SameValue(string? a, string? b) => string.Equals(LfgRules.Normalize(a), LfgRules.Normalize(b), StringComparison.Ordinal);

    /// <summary>Same text for the form's purposes (whitespace and letter case do not count).</summary>
    public static bool SameText(string? a, string? b) =>
        string.Equals(LfgRules.Normalize(a), LfgRules.Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The start field was left as it was: the same text, the same wall-clock minute written another way
    /// ("5.10.2026 21:30" for "05.10.2026 21:30"), or "now" typed where the form showed no start.
    /// </summary>
    public static bool SameStart(string? typed, string? shown)
    {
        if (SameText(typed, shown))
            return true;
        if (LfgRules.Normalize(shown) is null)
            return LfgStartText.Parse(typed).IsNow;
        return LfgEventDate.TryReadWallClock(typed, out var a) && LfgEventDate.TryReadWallClock(shown, out var b) && a == b;
    }
}

/// <summary>What the settings step shows about a checked form (the listing it would open or become).</summary>
public sealed record LfgFormPreview(string GameName, string? Details, int MaxPlayers, LfgStart Start, DateTimeOffset? EventAt, TimeSpan Duration);

/// <summary>Result of checking a submitted form without storing anything.</summary>
public sealed record LfgFormCheck(OperationResult Result, LfgFormPreview? Preview);

/// <summary>The settings of a listing as the edit form found them (notice opt-ins and voice channel, as stored).</summary>
public sealed record LfgFormSettings(bool NotifyBeforeStart, bool NotifyAtStart, ChannelId? VoiceChannel);

/// <summary>
/// What the owner saves in the edit form: the texts plus the settings of the second step. <see cref="Opened"/> and
/// <see cref="OpenedSettings"/> are the listing as the form found it when opened: every field left as it was there stays as
/// stored NOW — a stale form never undoes a newer edit of a field it did not touch (and a time-zone change does not shift
/// an untouched start). Without them (no snapshot) every field is taken as typed.
/// </summary>
public sealed record LfgEditInput(LfgFormValues Form, bool NotifyBeforeStart, bool NotifyAtStart, ChannelId? VoiceChannel, LfgFormValues? Opened = null,
    LfgFormSettings? OpenedSettings = null);

/// <summary>
/// The edit form of a listing, opened only for its owner while it is active. <see cref="Voice"/> is the listing's voice
/// channel if it is still usable (a deleted one is not offered again).
/// </summary>
public sealed record LfgEditOpening(OperationResult Result, LfgListingView? Listing, LfgFormValues? Prefill, ChannelId? Voice = null);
