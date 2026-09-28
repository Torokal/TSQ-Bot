using System.Text;

namespace ToroSquad.Modules.Randomizer.Application;

/// <summary>Either the distinct options (in the order given) or the localization key of the reason they were refused.</summary>
public sealed record ChoiceParseResult(IReadOnlyList<string>? Options, string? ErrorKey);

/// <summary>
/// Parses the /sec input into options. Separator: a comma — or, when the input contains a pipe, the pipe only (then commas
/// are part of an option: "Pizza, kola | Burger" is two options). One deterministic rule, no guessing between the two.
/// Each option is trimmed and inner whitespace (newlines included) collapses to one space, so an option can never spread
/// over several lines. Empty entries ("CS2,,Valheim") are dropped. Duplicates count once — case-insensitively, the first
/// spelling is kept — so a repeated option never gets twice the weight. The text itself is kept as typed; it is defused
/// for Discord only when shown (<see cref="RandomizerCards"/>).
/// </summary>
public static class ChoiceList
{
    public const int MinOptions = 2;
    public const int MaxOptions = 25;
    public const int MaxOptionLength = 100;

    /// <summary>Longest input accepted; also the slash option's max length (25 options of 40 characters, with separators).</summary>
    public const int MaxInputLength = 1000;

    public const char Comma = ',';
    public const char Pipe = '|';

    public const string TooFewKey = "randomizer.choice.too_few";
    public const string TooManyKey = "randomizer.choice.too_many";
    public const string TooLongKey = "randomizer.choice.too_long";
    public const string InputTooLongKey = "randomizer.choice.input_too_long";

    public static ChoiceParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new ChoiceParseResult(null, TooFewKey);
        if (input.Length > MaxInputLength)
            return new ChoiceParseResult(null, InputTooLongKey);

        var separator = input.Contains(Pipe, StringComparison.Ordinal) ? Pipe : Comma;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<string>();
        foreach (var raw in input.Split(separator))
        {
            var option = CollapseWhitespace(raw);
            if (option.Length == 0)
                continue;
            if (option.Length > MaxOptionLength)
                return new ChoiceParseResult(null, TooLongKey);
            if (seen.Add(option))
                options.Add(option);
        }

        if (options.Count < MinOptions)
            return new ChoiceParseResult(null, TooFewKey);
        if (options.Count > MaxOptions)
            return new ChoiceParseResult(null, TooManyKey);
        return new ChoiceParseResult(options, null);
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }
}
