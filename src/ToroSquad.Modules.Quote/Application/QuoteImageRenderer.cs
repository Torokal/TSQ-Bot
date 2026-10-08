using System.Globalization;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ToroSquad.Modules.Quote.Application;

/// <summary>What goes on a card: the quoted text (already plain, see <see cref="QuoteText"/>), who said it, and their avatar bytes if downloaded.</summary>
public sealed record QuoteRenderModel(string Text, string DisplayName, string Username, byte[]? Avatar);

/// <summary>Where things ended up — for tests and logs. Rectangles are ink bounds in canvas pixels.</summary>
public sealed record QuoteCardLayout(
    FontRectangle Message,
    FontRectangle Author,
    FontRectangle Username,
    float MessageFontSize,
    int MessageLines,
    bool Truncated,
    bool AvatarUsed);

public sealed record QuoteCard(byte[] Png, int Width, int Height, QuoteCardLayout Layout);

/// <summary>
/// Draws the quote card: a black canvas, the author's avatar on the left half in black and white (cropped to fill, slightly
/// darker and more contrasted) fading into black, and on the right the message in large white type with "— Name" and a grey
/// "@username" under it. No logo, no frame, no colour. Long text wraps; the font shrinks step by step down to a minimum,
/// and only then the last visible line ends in "…". Pure CPU work on managed code (ImageSharp): no GDI+, no host fonts.
/// </summary>
public interface IQuoteRenderer
{
    QuoteCard Render(QuoteRenderModel model);
}

public sealed class QuoteImageRenderer(QuoteFonts fonts) : IQuoteRenderer
{
    public const int Width = 1600;
    public const int Height = 800;

    /// <summary>The photo covers the left side up to where it has faded to black completely; the fade runs over its right part.</summary>
    public const int PhotoWidth = 860;
    public const int FadeStart = 400;
    public const int FadeEnd = PhotoWidth;

    /// <summary>The text column on the right.</summary>
    public const int TextLeft = 900;
    public const int TextRight = 1540;
    public const int TextWidth = TextRight - TextLeft;
    public const int VerticalMargin = 90;

    public const float MaxFontSize = 84;
    public const float MinFontSize = 32;
    public const float AuthorFontSize = 40;
    public const float UsernameFontSize = 28;
    public const float MessageLineSpacing = 1.15f;
    private const float AuthorGap = 44;
    private const float UsernameGap = 10;

    /// <summary>A Discord avatar is at most 1024 px; anything far larger is not an avatar and is not decoded.</summary>
    public const int MaxAvatarDimension = 4096;

    private static readonly Color MessageColor = Color.White;
    private static readonly Color AuthorColor = Color.ParseHex("EDEDED");
    private static readonly Color UsernameColor = Color.ParseHex("8E8E8E");
    private static readonly Color FallbackBackground = Color.ParseHex("1E1E1E");
    private static readonly Color FallbackInitial = Color.ParseHex("3C3C3C");

    public QuoteCard Render(QuoteRenderModel model)
    {
        var (canvas, layout) = Compose(model);
        using (canvas)
        {
            using var png = new MemoryStream();
            // Every pixel of the card is a shade of grey, so 8-bit greyscale PNG is lossless here and a third of the size of RGB.
            canvas.SaveAsPng(png, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit8 });
            return new QuoteCard(png.ToArray(), Width, Height, layout);
        }
    }

    /// <summary>The card as pixels, before encoding (the caller disposes the image).</summary>
    public (Image<Rgba32> Canvas, QuoteCardLayout Layout) Compose(QuoteRenderModel model)
    {
        var canvas = new Image<Rgba32>(Width, Height, Color.Black);
        var avatarUsed = DrawPhoto(canvas, model);

        var author = Line("— " + QuoteText.SingleLine(model.DisplayName), fonts.Sans.CreateFont(AuthorFontSize, FontStyle.Italic));
        var username = Line("@" + QuoteText.SingleLine(model.Username), fonts.Sans.CreateFont(UsernameFontSize, FontStyle.Regular));
        var authorAdvance = TextMeasurer.MeasureAdvance(author.Text, author.Options);
        var usernameAdvance = TextMeasurer.MeasureAdvance(username.Text, username.Options);
        var authorBlock = authorAdvance.Height + UsernameGap + usernameAdvance.Height;

        var text = QuoteText.LimitForLayout(QuoteText.Sanitize(model.Text));
        var message = FitMessage(text, Height - (2 * VerticalMargin) - AuthorGap - authorBlock);

        // One block, centred vertically; lines stay left-aligned inside it, and the block sits in the middle of the column
        // (a short quote lands in the centre like a poster, a long one uses the whole column).
        var blockWidth = Math.Min(TextWidth, Math.Max(message.Advance.Width, Math.Max(authorAdvance.Width, usernameAdvance.Width)));
        var left = TextLeft + ((TextWidth - blockWidth) / 2);
        var top = (Height - (message.Advance.Height + AuthorGap + authorBlock)) / 2;

        message.Options.Origin = new PointF(left, top);
        author.Options.Origin = new PointF(left, top + message.Advance.Height + AuthorGap);
        username.Options.Origin = new PointF(left, author.Options.Origin.Y + authorAdvance.Height + UsernameGap);

        canvas.Mutate(c => c
            .DrawText(message.Options, message.Text, MessageColor)
            .DrawText(author.Options, author.Text, AuthorColor)
            .DrawText(username.Options, username.Text, UsernameColor));

        var layout = new QuoteCardLayout(
            TextMeasurer.MeasureBounds(message.Text, message.Options),
            TextMeasurer.MeasureBounds(author.Text, author.Options),
            TextMeasurer.MeasureBounds(username.Text, username.Options),
            message.Options.Font.Size,
            message.Lines,
            message.Truncated,
            avatarUsed);
        return (canvas, layout);
    }

    private sealed record TextLine(string Text, RichTextOptions Options);

    private sealed record FittedMessage(string Text, RichTextOptions Options, FontRectangle Advance, int Lines, bool Truncated);

    /// <summary>A single line (author, username): a name longer than the column ends in "…" instead of wrapping.</summary>
    private TextLine Line(string text, Font font)
    {
        var options = Options(font, 1f);
        return new(CutToLines(text, options, 1), options);
    }

    /// <summary>Largest size whose wrapped text fits the space; at the minimum size, as many lines as fit, the last ending in "…".</summary>
    private FittedMessage FitMessage(string text, float maxHeight)
    {
        for (var size = MaxFontSize; size > MinFontSize; size -= size > 56 ? 6 : 4)
        {
            var options = Options(fonts.Sans.CreateFont(size, FontStyle.Regular), MessageLineSpacing);
            var advance = TextMeasurer.MeasureAdvance(text, options);
            if (advance.Height <= maxHeight)
                return new(text, options, advance, TextMeasurer.CountLines(text, options), false);
        }

        var smallest = Options(fonts.Sans.CreateFont(MinFontSize, FontStyle.Regular), MessageLineSpacing);
        var lines = TextMeasurer.CountLines(text, smallest);
        var lineHeight = TextMeasurer.MeasureAdvance(text, smallest).Height / Math.Max(1, lines);
        var fit = Math.Max(1, (int)Math.Floor(maxHeight / lineHeight));
        var shown = lines <= fit ? text : CutToLines(text, smallest, fit);
        return new(shown, smallest, TextMeasurer.MeasureAdvance(shown, smallest), Math.Min(lines, fit), lines > fit);
    }

    /// <summary>
    /// The longest start of <paramref name="text"/> that, with "…" appended, wraps into at most <paramref name="maxLines"/>
    /// lines. Cuts between graphemes (never inside an emoji or a letter with its accents), preferably after a word.
    /// </summary>
    public static string CutToLines(string text, TextOptions options, int maxLines)
    {
        if (TextMeasurer.CountLines(text, options) <= maxLines)
            return text;

        var boundaries = new List<int>(); // end offsets of each grapheme
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            boundaries.Add(enumerator.ElementIndex + ((string)enumerator.Current).Length);

        string Candidate(int graphemes) => text[..(graphemes == 0 ? 0 : boundaries[graphemes - 1])].TrimEnd() + "…";

        int low = 0, high = boundaries.Count - 1; // invariant: Candidate(low) fits
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (TextMeasurer.CountLines(Candidate(mid), options) <= maxLines)
                low = mid;
            else
                high = mid - 1;
        }

        // End on a whole word when one ended recently; a single endless word is cut where it has to be.
        var cut = boundaries.Count == 0 || low == 0 ? 0 : boundaries[low - 1];
        var space = text.LastIndexOfAny([' ', '\n'], Math.Max(0, cut - 1));
        if (space > 0 && cut - space <= 16)
            cut = space;
        return text[..cut].TrimEnd() + "…";
    }

    private RichTextOptions Options(Font font, float lineSpacing) => new(font)
    {
        WrappingLength = TextWidth,
        WordBreaking = WordBreaking.BreakWord, // a 60-character link or "aaaaaaaa…" still wraps inside the column
        LineSpacing = lineSpacing,
        FallbackFontFamilies = [fonts.Emoji],
    };

    /// <summary>The left half: the avatar in black and white, or a dark panel with the author's initial; then the fade to black.</summary>
    private bool DrawPhoto(Image<Rgba32> canvas, QuoteRenderModel model)
    {
        using var photo = DecodeAvatar(model.Avatar);
        if (photo is not null)
        {
            photo.Mutate(p => p
                .Resize(new ResizeOptions { Size = new Size(PhotoWidth, Height), Mode = ResizeMode.Crop, Position = AnchorPositionMode.Center })
                .Grayscale()
                .Contrast(1.12f)
                .Brightness(0.82f));
            canvas.Mutate(c => c.DrawImage(photo, new Point(0, 0), 1f));
        }
        else
        {
            DrawFallback(canvas, QuoteText.SingleLine(model.DisplayName));
        }

        canvas.Mutate(c => c
            .Fill(new LinearGradientBrush(new PointF(FadeStart, 0), new PointF(FadeEnd, 0), GradientRepetitionMode.None,
                    new ColorStop(0f, Color.Black.WithAlpha(0f)), new ColorStop(0.55f, Color.Black.WithAlpha(0.75f)), new ColorStop(1f, Color.Black)),
                new RectangleF(FadeStart, 0, FadeEnd - FadeStart, Height))
            .Fill(Color.Black, new RectangleF(FadeEnd, 0, Width - FadeEnd, Height)));
        return photo is not null;
    }

    private void DrawFallback(Image<Rgba32> canvas, string displayName)
    {
        var initial = StringInfo.GetNextTextElement(displayName.Trim()) is { Length: > 0 } first
            ? first.ToUpper(CultureInfo.GetCultureInfo("tr-TR"))
            : "?";
        var options = new RichTextOptions(fonts.Sans.CreateFont(340, FontStyle.Regular))
        {
            FallbackFontFamilies = [fonts.Emoji],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Origin = new PointF(FadeStart - 70, Height / 2f),
        };
        canvas.Mutate(c => c
            .Fill(FallbackBackground, new RectangleF(0, 0, PhotoWidth, Height))
            .DrawText(options, initial, FallbackInitial));
    }

    /// <summary>
    /// The only image formats an avatar is ever decoded from — what Discord's CDN serves (PNG, JPEG, WebP, GIF). Every other
    /// decoder of the library (TIFF/BigTIFF, BMP, TGA, PBM, QOI…) is not registered here, so bytes in such a format are "not
    /// an image" before any of their parsing code runs. This closes the reachable part of the ImageSharp 3.1.12 advisories
    /// suppressed in Directory.Build.props (the TIFF reader); see SECURITY.md.
    /// </summary>
    private static readonly Configuration AvatarFormats = new(
        new PngConfigurationModule(), new JpegConfigurationModule(), new WebpConfigurationModule(), new GifConfigurationModule());

    /// <summary>First frame of a real PNG/JPEG/WebP/GIF image of sane size, or null (missing, another format, corrupt, absurd dimensions).</summary>
    public static Image<Rgba32>? DecodeAvatar(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
            return null;
        try
        {
            var options = new DecoderOptions { Configuration = AvatarFormats, MaxFrames = 1 };
            var info = Image.Identify(options, bytes);
            if (info.Width is < 1 or > MaxAvatarDimension || info.Height is < 1 or > MaxAvatarDimension)
                return null;
            return Image.Load<Rgba32>(options, bytes);
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            return null;
        }
    }
}
