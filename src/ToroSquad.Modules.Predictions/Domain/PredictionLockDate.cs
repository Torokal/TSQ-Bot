using System.Globalization;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Predictions.Domain;

public enum LockDateError
{
    None = 0,
    Format = 1,
    NotInFuture = 2,
    TooFar = 3,
    NotInTimeZone = 4,
    Ambiguous = 5,
}

/// <summary>
/// The automatic lock time as typed: Türkiye wall-clock time <c>GG.AA.YYYY SS:DD</c> (leading zeros optional), plus ISO
/// <c>YYYY-AA-GG SS:DD</c>. Parsed explicitly — no culture, no machine time zone, no relative words, no two-digit year —
/// then converted to UTC with the Europe/Istanbul zone. A wall-clock time that does not exist or exists twice in the zone
/// is refused rather than guessed; so is anything not at least <see cref="MinLead"/> ahead (checked again when the
/// prediction is published).
/// </summary>
public static partial class PredictionLockDate
{
    public static readonly TimeSpan MinLead = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(365);

    public static bool TryReadWallClock(string? text, out DateTime local)
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
        var (year, month, day, hour, minute) = (Part("y"), Part("m"), Part("d"), Part("h"), Part("min"));
        if (year is < 2000 or > 9998 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
            return false;
        local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return true;
    }

    public static (DateTimeOffset? At, LockDateError Error) Resolve(string text, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!TryReadWallClock(text, out var local))
            return (null, LockDateError.Format);
        var year = TimeZoneInfo.ConvertTime(now, zone).Year;
        // Far outside the one-year window: refused before any time-zone arithmetic could overflow.
        if (local.Year < year - 1)
            return (null, LockDateError.NotInFuture);
        if (local.Year > year + 1)
            return (null, LockDateError.TooFar);
        if (zone.IsInvalidTime(local))
            return (null, LockDateError.NotInTimeZone);
        if (zone.IsAmbiguousTime(local))
            return (null, LockDateError.Ambiguous);

        var at = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        if (at < now + MinLead)
            return (null, LockDateError.NotInFuture);
        if (at > now + MaxAhead)
            return (null, LockDateError.TooFar);
        return (at, LockDateError.None);
    }

    /// <summary>"05.10.2026 20:00" in the zone (for prefilling the form again).</summary>
    public static string Format(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<d>[0-9]{1,2})\.(?<m>[0-9]{1,2})\.(?<y>[0-9]{4})\s+(?<h>[0-9]{1,2}):(?<min>[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^(?<y>[0-9]{4})-(?<m>[0-9]{1,2})-(?<d>[0-9]{1,2})[\sT]+(?<h>[0-9]{1,2}):(?<min>[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoPattern();
}

/// <summary>
/// Europe/Istanbul calendar helpers: the daily reward's day (a new claim at 00:00 Türkiye time, not a rolling 24 hours) and
/// its next start. Uses the host's IANA/ICU zone data, never the machine's local time.
/// </summary>
public static class TurkeyCalendar
{
    public const string TimeZoneId = "Europe/Istanbul";

    /// <summary>The Türkiye calendar day of <paramref name="instant"/> as yyyymmdd (the daily claim key).</summary>
    public static int DayKey(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return local.Year * 10_000 + local.Month * 100 + local.Day;
    }

    /// <summary>The instant the next Türkiye calendar day starts (the next daily claim).</summary>
    public static DateTimeOffset NextDayStart(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var midnight = local.Date.AddDays(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
    }
}
