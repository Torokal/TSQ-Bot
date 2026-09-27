using System.Globalization;
using SixLabors.Fonts;

namespace ToroSquad.Modules.Quote.Application;

/// <summary>
/// The card's typefaces, loaded from resources embedded in this assembly — never from fonts installed on the host, so a
/// card looks the same on a developer machine and in the font-less Linux container. Noto Sans covers Latin (Turkish
/// ğ ü ş ı ö ç İ), Greek and Cyrillic; Noto Emoji is the monochrome emoji fallback (it matches the black-and-white card).
/// SIL Open Font License 1.1 (Assets/Fonts/OFL-*.txt).
/// </summary>
public sealed class QuoteFonts
{
    public const string ResourcePrefix = "ToroSquad.Modules.Quote.Fonts.";
    public static readonly IReadOnlyList<string> Files = ["NotoSans-Regular.ttf", "NotoSans-Italic.ttf", "NotoEmoji-Regular.ttf"];

    private QuoteFonts(FontFamily sans, FontFamily emoji)
    {
        Sans = sans;
        Emoji = emoji;
    }

    /// <summary>Regular and Italic faces of one family.</summary>
    public FontFamily Sans { get; }

    public FontFamily Emoji { get; }

    public static QuoteFonts Load()
    {
        var collection = new FontCollection();
        FontFamily Add(string file)
        {
            using var stream = typeof(QuoteFonts).Assembly.GetManifestResourceStream(ResourcePrefix + file)
                ?? throw new InvalidOperationException($"Embedded font '{file}' is missing from {typeof(QuoteFonts).Assembly.GetName().Name}.");
            return collection.Add(stream, CultureInfo.InvariantCulture);
        }

        var sans = Add(Files[0]);
        Add(Files[1]); // same family, italic style
        var emoji = Add(Files[2]);
        return new QuoteFonts(sans, emoji);
    }
}
