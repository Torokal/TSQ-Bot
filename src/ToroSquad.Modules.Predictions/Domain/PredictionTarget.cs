using System.Globalization;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Predictions.Domain;

/// <summary>
/// Which prediction a management command means: its number as the card footer shows it (<c>#12</c> or <c>12</c>; also what
/// the autocomplete sends), a message link to its card (same guild only) or the card's message id. Numbers are small;
/// Discord ids are 17+ digits, so the two never overlap. Any other message is simply not found.
/// </summary>
public sealed partial record PredictionTarget(long? PredictionId, ulong? MessageId, ulong? LinkGuildId)
{
    public const int MaxInputLength = 120;
    private const int MaxIdDigits = 9;

    public static PredictionTarget? Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxInputLength)
            return null;

        if (Link().Match(text) is { Success: true } link &&
            ulong.TryParse(link.Groups["guild"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var guild) &&
            ulong.TryParse(link.Groups["message"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var linked))
            return new PredictionTarget(null, linked, guild);

        var digits = text.StartsWith('#') ? text[1..] : text;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
            return null;
        if (digits.Length <= MaxIdDigits)
        {
            var id = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            return id > 0 ? new PredictionTarget(id, null, null) : null;
        }

        return !text.StartsWith('#') && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var message)
            ? new PredictionTarget(null, message, null)
            : null;
    }

    [GeneratedRegex(@"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/channels/(?<guild>\d{1,20})/\d{1,20}/(?<message>\d{1,20})/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Link();
}
