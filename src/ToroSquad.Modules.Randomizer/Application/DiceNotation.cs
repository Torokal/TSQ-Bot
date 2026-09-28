using System.Globalization;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Randomizer.Application;

/// <summary>A valid dice request: <see cref="Count"/> dice with <see cref="Sides"/> sides each ("2d6").</summary>
public sealed record DiceSpec(int Count, int Sides)
{
    /// <summary>Canonical notation shown on the card, whatever the user typed ("2-6", "2D6" → "2d6").</summary>
    public string Notation => string.Create(CultureInfo.InvariantCulture, $"{Count}d{Sides}");
}

/// <summary>Either a <see cref="DiceSpec"/> or the localization key of the reason it was refused.</summary>
public sealed record DiceParseResult(DiceSpec? Spec, string? ErrorKey)
{
    public static DiceParseResult Ok(DiceSpec spec) => new(spec, null);

    public static DiceParseResult Fail(string errorKey) => new(null, errorKey);
}

/// <summary>
/// Parses the /zarat input: "&lt;count&gt;-&lt;sides&gt;" or the common dice notation "&lt;count&gt;d&lt;sides&gt;"
/// (d/D). Tolerant only where it is unambiguous — surrounding whitespace is ignored — and strict everywhere else: ASCII
/// digits only (no signs, no Unicode digits), exactly one separator, nothing before or after, no inner whitespace.
/// Out-of-range numbers of any length are refused by range (never an overflow): 1..20 dice, 2..10,000 sides — so
/// <c>Sides + 1</c> for the random draw can never overflow.
/// </summary>
public static partial class DiceNotation
{
    public const int MinCount = 1;
    public const int MaxCount = 20;
    public const int MinSides = 2;
    public const int MaxSides = 10_000;

    /// <summary>Longest input worth looking at; also the slash option's max length.</summary>
    public const int MaxInputLength = 32;

    public const string InvalidKey = "randomizer.dice.invalid";
    public const string TooFewKey = "randomizer.dice.too_few";
    public const string TooManyKey = "randomizer.dice.too_many";
    public const string TooFewSidesKey = "randomizer.dice.too_few_sides";
    public const string TooManySidesKey = "randomizer.dice.too_many_sides";

    public static DiceParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxInputLength)
            return DiceParseResult.Fail(InvalidKey);

        var match = Pattern().Match(input.Trim());
        if (!match.Success)
            return DiceParseResult.Fail(InvalidKey);

        var count = Number(match.Groups["count"].Value);
        var sides = Number(match.Groups["sides"].Value);
        if (count < MinCount)
            return DiceParseResult.Fail(TooFewKey);
        if (count > MaxCount)
            return DiceParseResult.Fail(TooManyKey);
        if (sides < MinSides)
            return DiceParseResult.Fail(TooFewSidesKey);
        if (sides > MaxSides)
            return DiceParseResult.Fail(TooManySidesKey);
        return DiceParseResult.Ok(new DiceSpec((int)count, (int)sides));
    }

    /// <summary>ASCII digits to a number; anything longer than 9 significant digits is simply "too large" (no overflow).</summary>
    private static long Number(string digits)
    {
        var significant = digits.TrimStart('0');
        if (significant.Length == 0)
            return 0;
        return significant.Length > 9 ? long.MaxValue : long.Parse(significant, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    // [0-9], not \d: \d also matches Arabic-Indic and other Unicode digits. \z, not $: $ also matches before a final "\n".
    [GeneratedRegex(@"\A(?<count>[0-9]+)[-dD](?<sides>[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
