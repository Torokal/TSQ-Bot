using System.Globalization;

namespace ToroSquad.Modules.Birthday.Domain;

/// <summary>
/// A birthday as day + month — deliberately no year (it is never asked for and never stored). 29 February is a valid
/// birthday; it only matches a real 29 February, so in other years it is not celebrated (never moved to 28 Feb or 1 Mar).
/// </summary>
public readonly record struct BirthdayDate
{
    private BirthdayDate(int day, int month)
    {
        Day = day;
        Month = month;
    }

    public int Day { get; }
    public int Month { get; }

    /// <summary>A leap year, so 29 February counts as a date that exists.</summary>
    private const int LeapYear = 2024;

    public static bool IsValid(int day, int month) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(LeapYear, month);

    public static BirthdayDate? Create(int day, int month) => IsValid(day, month) ? new BirthdayDate(day, month) : null;

    /// <summary>
    /// "14.03", "14/03" or "14-03" (day first; one or two digits each; one separator kind). Anything else — a year, words,
    /// mixed separators, a date that does not exist — is rejected.
    /// </summary>
    public static bool TryParse(string? input, out BirthdayDate date)
    {
        date = default;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 5)
            return false;
        var separator = text.IndexOfAny(['.', '/', '-']);
        if (separator is < 1 or > 2 || text.IndexOfAny(['.', '/', '-'], separator + 1) >= 0)
            return false;
        var dayText = text[..separator];
        var monthText = text[(separator + 1)..];
        if (monthText.Length is < 1 or > 2 || !dayText.All(char.IsAsciiDigit) || !monthText.All(char.IsAsciiDigit))
            return false;
        var day = int.Parse(dayText, CultureInfo.InvariantCulture);
        var month = int.Parse(monthText, CultureInfo.InvariantCulture);
        if (Create(day, month) is not { } parsed)
            return false;
        date = parsed;
        return true;
    }

    /// <summary>True only on this exact day and month — 29 February therefore only in leap years.</summary>
    public bool IsOn(DateOnly localDate) => localDate.Month == Month && localDate.Day == Day;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Day:00}.{Month:00}");
}

/// <summary>The calendar day in the celebration time zone — never the UTC date.</summary>
public static class BirthdayCalendar
{
    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>The instant <paramref name="day"/> starts in <paramref name="zone"/> (00:00 local, or the first valid minute after it).</summary>
    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(30); // a DST gap at midnight: the day starts when the clock resumes
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static DateTimeOffset EndOfDay(DateOnly day, TimeZoneInfo zone) => StartOfDay(day.AddDays(1), zone);
}
