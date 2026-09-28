using System.Globalization;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Modules.Randomizer.Application;

/// <summary>
/// What a Randomizer command answers: a public <see cref="Card"/>, or a short <see cref="Refusal"/> (invalid input) that only
/// the user who ran the command sees. Never both.
/// </summary>
public sealed record RandomizerReply(MessageEmbed? Card, string? Refusal)
{
    public static RandomizerReply Refused(string text) => new(null, text);
}

/// <summary>
/// The four commands end to end, without the Discord SDK (unit-tested): validate the input, draw from
/// <see cref="RandomizerService"/>, render the card. One compact layout for all four, in the TSQ card style (brand colour,
/// emoji title, short description, who it was for in the footer). User-typed text (the /sec options and the display name)
/// is defused before it is shown — mention syntax and markdown never render — and the reply is sent without any allowed
/// mentions anyway. Nothing here logs or stores a result.
/// </summary>
public sealed class RandomizerCards(RandomizerService randomizer, ILocalizer localizer)
{
    public const uint Color = 0xE8590C; // the TSQ brand colour, as on every other public card

    /// <summary>/sec shows the full option list up to this many options; beyond it only the count (the card stays small).</summary>
    public const int ListedOptionsMax = 10;

    /// <summary>...and only while the listed options stay this short in total.</summary>
    public const int ListedOptionsMaxLength = 1000;

    public const int DisplayNameMax = 64;

    public RandomizerReply Dice(string lang, string? input, string displayName)
    {
        var parsed = DiceNotation.Parse(input);
        if (parsed.Spec is not { } spec)
            return RandomizerReply.Refused(L(lang, parsed.ErrorKey!));

        var roll = randomizer.Roll(spec);
        var description = roll.Dice.Count == 1
            ? L(lang, "randomizer.dice.single", Number(roll.Dice[0]))
            : L(lang, "randomizer.dice.multi", string.Join(" ", roll.Dice.Select(d => "`" + Number(d) + "`")), Number(roll.Total));
        return Card(L(lang, "randomizer.dice.title", spec.Notation), description, L(lang, "randomizer.footer.rolled", Name(displayName)));
    }

    public RandomizerReply RandomNumber(string lang, long maximum, long? minimum, string displayName)
    {
        var created = NumberRanges.Create(maximum, minimum);
        if (created.Range is not { } range)
            return RandomizerReply.Refused(L(lang, created.ErrorKey!));

        var value = randomizer.Between(range);
        return Card(L(lang, "randomizer.number.title"),
            L(lang, "randomizer.number.body", Number(range.Minimum), Number(range.Maximum), Number(value)),
            L(lang, "randomizer.footer.picked", Name(displayName)));
    }

    public RandomizerReply Choose(string lang, string? input, string displayName)
    {
        var parsed = ChoiceList.Parse(input);
        if (parsed.Options is not { } options)
            return RandomizerReply.Refused(L(lang, parsed.ErrorKey!));

        var chosen = DiscordText.Untrusted(randomizer.Choose(options));
        var listed = string.Join(" · ", options.Select(o => DiscordText.Untrusted(o)));
        var description = options.Count <= ListedOptionsMax && listed.Length <= ListedOptionsMaxLength
            ? L(lang, "randomizer.choice.listed", listed, chosen)
            : L(lang, "randomizer.choice.counted", options.Count, chosen);
        return Card(L(lang, "randomizer.choice.title"), description, L(lang, "randomizer.footer.picked", Name(displayName)));
    }

    public RandomizerReply CoinFlip(string lang, string displayName)
    {
        var face = randomizer.Flip();
        return Card(L(lang, "randomizer.coin.title"), "**" + L(lang, FaceKey(face)) + "**", L(lang, "randomizer.footer.rolled", Name(displayName)));
    }

    public static string FaceKey(CoinFace face) => face == CoinFace.Yazi ? "randomizer.coin.yazi" : "randomizer.coin.tura";

    private static RandomizerReply Card(string title, string description, string footer) =>
        new(new MessageEmbed(title, description, null, [], footer, null, Color), null);

    /// <summary>The footer is plain text (no markdown, no mentions); still defused and bounded like every untrusted name.</summary>
    private static string Name(string displayName) => DiscordText.UntrustedPlain(displayName, DisplayNameMax);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats the template here rather than through <see cref="ILocalizer"/>'s arguments: the localizer would replace a
    /// string argument that happens to be a catalog key (an option typed as "help.title") with that key's text.
    /// </summary>
    private string L(string lang, string key, params object?[] args) =>
        args.Length == 0 ? localizer.Get(lang, key) : string.Format(CultureInfo.InvariantCulture, localizer.Get(lang, key), args);
}
