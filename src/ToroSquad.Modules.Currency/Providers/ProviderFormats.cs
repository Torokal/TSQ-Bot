using System.Globalization;
using System.Text.RegularExpressions;
using ToroSquad.Core.Guilds;

namespace ToroSquad.Modules.Currency.Providers;

/// <summary>
/// Number and time formats of the providers, fixed here so neither the host's locale nor its ICU data can change how a
/// price is read (the bot runs on Linux in production, on Windows in development).
/// </summary>
public static partial class ProviderFormats
{
    /// <summary>All providers publish Türkiye local wall-clock times without an offset.</summary>
    public const string TurkeyTimeZoneId = "Europe/Istanbul";

    /// <summary>
    /// tr-TR number separators ("6.439,47" = 6439.47). Spelled out instead of taken from the tr-TR culture object, so the
    /// parse is identical on every host; <see cref="TurkishNumber"/> fixes the accepted shape first.
    /// </summary>
    public static readonly NumberFormatInfo Turkish = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = [3],
        NegativeSign = "-",
    };

    /// <summary>Resolves Europe/Istanbul on every OS (IANA on Linux, ICU-mapped on Windows). Checked at startup.</summary>
    public static bool TryTurkeyZone(out TimeZoneInfo zone) => GuildTime.TryResolve(TurkeyTimeZoneId, out zone);

    /// <summary>
    /// Altınkaynak prices: tr-TR digits with a mandatory decimal part — "48,820" is 48.820 (never 48 820), "6.439,47" is
    /// 6439.47. A value without a decimal comma ("48.820") is rejected rather than guessed: it is exactly the ambiguous
    /// shape a format change would produce. Never returns 0 for bad input; the caller treats false as a provider failure.
    /// </summary>
    public static bool TryParseTurkishDecimal(string? text, out decimal value)
    {
        value = 0;
        var trimmed = text?.Trim();
        return trimmed is not null && TurkishNumber().IsMatch(trimmed) &&
               decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, Turkish, out value);
    }

    /// <summary>TCMB prices: invariant digits with a dot decimal separator ("48.7901"), no grouping.</summary>
    public static bool TryParseInvariantDecimal(string? text, out decimal value)
    {
        value = 0;
        var trimmed = text?.Trim();
        return trimmed is not null && InvariantNumber().IsMatch(trimmed) &&
               decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// A provider's offset-less local time read as Türkiye wall-clock time (not UTC): "28.09.2026 11:51:42" is 08:51:42 UTC.
    /// </summary>
    public static bool TryParseTurkeyLocal(string? text, string format, TimeZoneInfo turkey, out DateTimeOffset instant)
    {
        instant = default;
        if (text is null ||
            !DateTime.TryParseExact(text.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ||
            turkey.IsInvalidTime(local))
            return false;
        instant = new DateTimeOffset(local, turkey.GetUtcOffset(local));
        return true;
    }

    /// <summary>Collapses runs of whitespace and trims ("Gram Altın " → "Gram Altın").</summary>
    public static string NormalizeSpaces(string? text) => text is null ? "" : Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"^(?:[0-9]{1,3}(?:\.[0-9]{3})+|[0-9]+),[0-9]{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex TurkishNumber();

    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]{1,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex InvariantNumber();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
