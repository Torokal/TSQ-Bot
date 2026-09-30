using System.Globalization;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Timezone.Application;

/// <summary>
/// The /saat <c>date</c> input: <c>DD.MM</c> or <c>DD.MM.YYYY</c> (one-digit day and month allowed: <c>5.7.2026</c>), ASCII
/// digits, <c>.</c> only — nothing else, so the result never depends on the culture. It must be a real calendar date (leap years
/// included). Without a year the caller's year is used — /saat passes the current year in the source zone — and the date is
/// never moved to another year because it has passed. Years are limited to <see cref="MinYear"/>–<see cref="MaxYear"/> so a
/// conversion can never leave the range <see cref="DateTimeOffset"/> supports.
/// </summary>
public static partial class DateInput
{
    public const int MaxInputLength = 10;
    public const int MinYear = 1900;
    public const int MaxYear = 2100;
    public const string InvalidKey = "timezone.invalid_date";

    public static DateOnly? Parse(string? input, int defaultYear)
    {
        if (input is null || input.Length > MaxInputLength)
            return null;
        var match = Pattern().Match(input.Trim());
        if (!match.Success)
            return null;

        var day = int.Parse(match.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var year = match.Groups["y"].Success ? int.Parse(match.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture) : defaultYear;
        if (year is < MinYear or > MaxYear || day > DateTime.DaysInMonth(year, month))
            return null;
        return new DateOnly(year, month, day);
    }

    /// <summary><c>[0-9]</c>, not <c>\d</c>: .NET's <c>\d</c> also matches non-ASCII digits.</summary>
    [GeneratedRegex(@"^(?<d>0?[1-9]|[12][0-9]|3[01])\.(?<m>0?[1-9]|1[0-2])(?:\.(?<y>[0-9]{4}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
