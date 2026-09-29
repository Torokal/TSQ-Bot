using System.Globalization;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Predictions.Domain;

public enum LockDateError
{
    None = 0,
    DateFormat = 1,
    NotInFuture = 2,
    TooFar = 3,
    NotInTimeZone = 4,
    Ambiguous = 5,
    TimeFormat = 6,
    DateMissing = 7,
    TimeMissing = 8,
}

/// <summary>
/// The automatic lock time as typed in two fields: the Türkiye date <c>GG.AA.YYYY</c> (ISO <c>YYYY-AA-GG</c> too) and the
/// Türkiye time <c>SS:DD</c> (<c>SS.DD</c> too; leading zeros optional). Both empty = locked by hand; one without the other is
/// refused, never guessed. Parsed explicitly — no culture, no machine time zone, no relative words, no two-digit year —
/// then converted to UTC with the Europe/Istanbul zone. A wall-clock time that does not exist or exists twice in the zone
/// is refused rather than guessed; so is anything not at least <see cref="MinLead"/> ahead (checked again when the
/// prediction is published).
/// </summary>
public static partial class PredictionLockDate
{
    public static readonly TimeSpan MinLead = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(365);

    /// <summary>The lock time of the two fields: none when both are empty, otherwise the instant or every problem found.</summary>
    public static (DateTimeOffset? At, IReadOnlyList<LockDateError> Errors) Resolve(string? date, string? time, TimeZoneInfo zone, DateTimeOffset now)
    {
        var (d, t) = (date?.Trim() ?? "", time?.Trim() ?? "");
        if (d.Length == 0 && t.Length == 0)
            return (null, []);
        if (t.Length == 0)
            return (null, [LockDateError.TimeMissing]);
        if (d.Length == 0)
            return (null, [LockDateError.DateMissing]);

        var errors = new List<LockDateError>();
        if (!TryReadDate(d, out var day))
            errors.Add(LockDateError.DateFormat);
        if (!TryReadTime(t, out var clock))
            errors.Add(LockDateError.TimeFormat);
        if (errors.Count > 0)
            return (null, errors);

        var (at, error) = Resolve(day.ToDateTime(clock), zone, now);
        return error == LockDateError.None ? (at, []) : (null, [error]);
    }

    /// <summary>A Türkiye wall-clock time → UTC, refused when it is not a real, single, future instant within a year.</summary>
    public static (DateTimeOffset? At, LockDateError Error) Resolve(DateTime local, TimeZoneInfo zone, DateTimeOffset now)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
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

    public static bool TryReadDate(string? text, out DateOnly date)
    {
        date = default;
        var value = text?.Trim() ?? "";
        var match = DatePattern().Match(value);
        if (!match.Success)
            match = IsoDatePattern().Match(value);
        if (!match.Success)
            return false;

        int Part(string name) => int.Parse(match.Groups[name].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var (year, month, day) = (Part("y"), Part("m"), Part("d"));
        if (year is < 2000 or > 9998 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    public static bool TryReadTime(string? text, out TimeOnly time)
    {
        time = default;
        var match = TimePattern().Match(text?.Trim() ?? "");
        if (!match.Success)
            return false;
        var hour = int.Parse(match.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["min"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59)
            return false;
        time = new TimeOnly(hour, minute);
        return true;
    }

    [GeneratedRegex(@"^(?<d>[0-9]{1,2})\.(?<m>[0-9]{1,2})\.(?<y>[0-9]{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^(?<y>[0-9]{4})-(?<m>[0-9]{1,2})-(?<d>[0-9]{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDatePattern();

    [GeneratedRegex(@"^(?<h>[0-9]{1,2})[:.](?<min>[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();
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
