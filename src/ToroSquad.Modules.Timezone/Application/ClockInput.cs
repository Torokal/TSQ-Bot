using System.Globalization;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Timezone.Application;

/// <summary>
/// The /saat input: a 24-hour time of day, <c>H:MM</c> or <c>HH:MM</c>, with <c>:</c> or <c>.</c> between hours and minutes
/// (<c>21:00</c>, <c>9:00</c>, <c>09:00</c>, <c>21.00</c>). Surrounding whitespace is ignored. Nothing else: no seconds, no
/// AM/PM, no <c>24:00</c>, no one-digit minutes, ASCII digits only — so the result never depends on the culture.
/// </summary>
public static partial class ClockInput
{
    public const int MaxInputLength = 10;
    public const string InvalidKey = "timezone.invalid_time";

    public static TimeOnly? Parse(string? input)
    {
        if (input is null || input.Length > MaxInputLength)
            return null;
        var match = Pattern().Match(input.Trim());
        if (!match.Success)
            return null;
        return new TimeOnly(
            int.Parse(match.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture));
    }

    /// <summary><c>[0-9]</c>, not <c>\d</c>: .NET's <c>\d</c> also matches non-ASCII digits.</summary>
    [GeneratedRegex(@"^(?<h>[01]?[0-9]|2[0-3])[:.](?<m>[0-5][0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
