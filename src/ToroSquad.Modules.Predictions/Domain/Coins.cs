using System.Globalization;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Predictions.Domain;

/// <summary>
/// TSQ Coin arithmetic. Amounts are <see cref="long"/> counts of the smallest unit (1 TSQ Coin = 100 units) and odds are
/// ×100 integers (1.10 = 110): no float or double anywhere. The one payout rule — total return = stake × odds, rounded
/// DOWN to the smallest unit — lives in <see cref="Payout"/> and is used by the preview, the receipt and the settlement
/// alike. Every product and sum is checked; a result that does not fit throws instead of wrapping around.
/// </summary>
public static partial class Coins
{
    public const long MinorPerCoin = 100;

    /// <summary>The smallest stake: one whole TSQ Coin.</summary>
    public const long MinStakeMinor = MinorPerCoin;

    /// <summary>
    /// The most a wallet may ever hold, counting every payout its pending entries could still bring (checked when an entry
    /// is confirmed). Far above any real play, and far enough below <see cref="long.MaxValue"/> that no settlement, daily
    /// reward or refund can overflow a balance.
    /// </summary>
    public const long WalletCeilingMinor = 1_000_000_000_000_000_000;

    /// <summary>Whole coins → units (checked).</summary>
    public static long FromCoins(long coins) => checked(coins * MinorPerCoin);

    /// <summary>
    /// Stake × odds / 100, rounded down to the smallest unit (the fraction below one unit is dropped, never rounded up).
    /// Computed in 128 bits; a result above <see cref="long.MaxValue"/> throws <see cref="OverflowException"/>.
    /// </summary>
    public static long Payout(long stakeMinor, int oddsX100)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stakeMinor);
        ArgumentOutOfRangeException.ThrowIfLessThan(oddsX100, Odds.MinX100);
        var product = (Int128)stakeMinor * oddsX100 / 100;
        return checked((long)product);
    }

    /// <summary>Net gain of a winning entry: total return − stake.</summary>
    public static long NetGain(long stakeMinor, int oddsX100) => checked(Payout(stakeMinor, oddsX100) - stakeMinor);

    public static long Add(long a, long b) => checked(a + b);

    public static long Subtract(long a, long b) => checked(a - b);

    /// <summary>
    /// A stake as typed: digits with at most two decimals after one "." or "," ("100", "12.5", "12,50"). No sign, no
    /// exponent, no thousands separators, no spaces inside — anything else is refused, never guessed.
    /// </summary>
    public static AmountParse ParseAmount(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return AmountParse.Fail(AmountError.Empty);
        var match = AmountPattern().Match(value);
        if (!match.Success)
            return AmountParse.Fail(TooManyDecimals().IsMatch(value) ? AmountError.TooManyDecimals : AmountError.Format);
        var whole = long.Parse(match.Groups["w"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var fraction = match.Groups["f"].Value.PadRight(2, '0');
        var minor = checked(whole * MinorPerCoin + (fraction.Length == 0 ? 0 : int.Parse(fraction, NumberStyles.None, CultureInfo.InvariantCulture)));
        return minor < MinStakeMinor ? AmountParse.Fail(AmountError.BelowMinimum) : new AmountParse(minor, AmountError.None);
    }

    /// <summary>
    /// "1047", "1047,50" (tr) / "1047.50" (en); numbers from 10 000 up are grouped ("12.500" / "12,500"). Whole amounts show no
    /// decimals; a fraction always shows two digits.
    /// </summary>
    public static string Format(long minor, string language)
    {
        var turkish = language == "tr";
        var negative = minor < 0;
        var magnitude = negative ? -(Int128)minor : minor;
        var whole = (long)(magnitude / MinorPerCoin);
        var fraction = (int)(magnitude % MinorPerCoin);
        var group = turkish ? "." : ",";
        var digits = whole.ToString(CultureInfo.InvariantCulture);
        if (whole >= 10_000)
            digits = Group(digits, group);
        var text = fraction == 0 ? digits : digits + (turkish ? "," : ".") + fraction.ToString("00", CultureInfo.InvariantCulture);
        return negative ? "-" + text : text;
    }

    /// <summary>An amount as the form accepts it back ("12500", "12.5" → "12.50"): no grouping, a dot, two decimals only when needed.</summary>
    public static string FormatInput(long minor)
    {
        var whole = (minor / MinorPerCoin).ToString(CultureInfo.InvariantCulture);
        var fraction = minor % MinorPerCoin;
        return fraction == 0 ? whole : whole + "." + fraction.ToString("00", CultureInfo.InvariantCulture);
    }

    private static string Group(string digits, string separator)
    {
        var parts = new List<string>();
        for (var end = digits.Length; end > 0; end -= 3)
            parts.Insert(0, digits[Math.Max(0, end - 3)..end]);
        return string.Join(separator, parts);
    }

    [GeneratedRegex(@"^(?<w>[0-9]{1,13})(?:[.,](?<f>[0-9]{1,2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"^[0-9]{1,13}[.,][0-9]{3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex TooManyDecimals();
}

public enum AmountError
{
    None = 0,
    Empty = 1,
    Format = 2,
    TooManyDecimals = 3,
    BelowMinimum = 4,
}

public sealed record AmountParse(long Minor, AmountError Error)
{
    public bool Ok => Error == AmountError.None;

    public static AmountParse Fail(AmountError error) => new(0, error);
}

/// <summary>
/// Fixed odds as ×100 integers. Accepted input: 1–4 digits with at most two decimals after "." or "," ("1.10", "1,10",
/// "3", "1000"). Range <see cref="MinX100"/>–<see cref="MaxX100"/>. Negative, zero, NaN, Infinity, exponents and more
/// decimals are refused, never rounded or replaced by the default.
/// </summary>
public static partial class Odds
{
    public const int MinX100 = 101;
    public const int MaxX100 = 100_000;

    public static OddsParse Parse(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return OddsParse.Fail(OddsError.Empty);
        var match = OddsPattern().Match(value);
        if (!match.Success)
            return OddsParse.Fail(TooManyDecimals().IsMatch(value) ? OddsError.TooManyDecimals : OddsError.Format);
        var whole = int.Parse(match.Groups["w"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var fraction = match.Groups["f"].Value.PadRight(2, '0');
        var x100 = whole * 100 + (fraction.Length == 0 ? 0 : int.Parse(fraction, NumberStyles.None, CultureInfo.InvariantCulture));
        return x100 is < MinX100 or > MaxX100 ? OddsParse.Fail(OddsError.OutOfRange) : new OddsParse(x100, OddsError.None);
    }

    /// <summary>"1.10" — always two decimals and a dot, as odds are written everywhere.</summary>
    public static string Format(int x100) =>
        (x100 / 100).ToString(CultureInfo.InvariantCulture) + "." + (x100 % 100).ToString("00", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<w>[0-9]{1,4})(?:[.,](?<f>[0-9]{1,2}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex OddsPattern();

    [GeneratedRegex(@"^[0-9]{1,4}[.,][0-9]{3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex TooManyDecimals();
}

public enum OddsError
{
    None = 0,
    Empty = 1,
    Format = 2,
    TooManyDecimals = 3,
    OutOfRange = 4,
}

public sealed record OddsParse(int X100, OddsError Error)
{
    public bool Ok => Error == OddsError.None;

    public static OddsParse Fail(OddsError error) => new(0, error);
}
