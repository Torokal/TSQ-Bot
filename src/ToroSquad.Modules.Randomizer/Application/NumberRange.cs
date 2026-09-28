namespace ToroSquad.Modules.Randomizer.Application;

/// <summary>An inclusive range for /randomsayi: <see cref="Minimum"/> ≤ result ≤ <see cref="Maximum"/>.</summary>
public sealed record NumberRange(int Minimum, int Maximum);

/// <summary>Either a <see cref="NumberRange"/> or the localization key of the reason it was refused.</summary>
public sealed record NumberRangeResult(NumberRange? Range, string? ErrorKey);

/// <summary>
/// /randomsayi bounds. Both ends are inclusive and may be negative; minimum defaults to 1; minimum == maximum is allowed
/// (the result is that number). The supported span is ±1,000,000,000: inside Discord's integer option range (±2^53) and far
/// enough from int.MaxValue that the exclusive upper bound of the random draw (<c>Maximum + 1</c>) never overflows. The
/// slash options declare the same bounds, and the values are checked here again (never trust the client).
/// </summary>
public static class NumberRanges
{
    public const int DefaultMinimum = 1;
    public const int Limit = 1_000_000_000;

    public const string OutOfRangeKey = "randomizer.number.out_of_range";
    public const string MinAboveMaxKey = "randomizer.number.min_above_max";

    public static NumberRangeResult Create(long maximum, long? minimum)
    {
        var min = minimum ?? DefaultMinimum;
        if (min is < -Limit or > Limit || maximum is < -Limit or > Limit)
            return new NumberRangeResult(null, OutOfRangeKey);
        if (min > maximum)
            return new NumberRangeResult(null, MinAboveMaxKey);
        return new NumberRangeResult(new NumberRange((int)min, (int)maximum), null);
    }
}
