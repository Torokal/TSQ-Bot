using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ToroSquad.Modules.Quote.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Quote card rendering: layout invariants (never pixel-perfect snapshots) — a real PNG of the expected size, a greyscale
/// photo, text inside the text column and the canvas whatever the message, graceful fallbacks for a missing or broken avatar.
/// </summary>
public sealed class QuoteRendererTests
{
    private static readonly QuoteImageRenderer Renderer = new(QuoteFonts.Load());

    private static byte[] SolidPng(Color color, int width = 256, int height = 256)
    {
        using var image = new Image<Rgba32>(width, height, color);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static QuoteRenderModel Model(string text, string name = "PizzaVenk", string user = "pizzavenk", byte[]? avatar = null) =>
        new(text, name, user, avatar ?? SolidPng(Color.ParseHex("D0342C")));

    public static TheoryData<string, string, string> Messages => new()
    {
        { "tek", "PizzaVenk", "pizzavenk" },
        { "aminiza koim", "PizzaVenk", "pizzavenk" },
        { "bu çok uzun bir discord mesajıdır ve tek satıra sığmaz, bu yüzden birkaç satıra bölünmesi gerekir", "Uzun", "uzun" },
        { "ilk satır\nikinci satır\n\nüçüncü satır", "Çok Satır", "cok.satir" },
        { "Ğğ Üü Şş İı Öö Çç — ığdır İSTANBUL", "Şükrü Öztürk", "sukru" },
        { "emoji 😂🔥👍🏽 🇹🇷 👨‍👩‍👧‍👦 ve metin", "Emoji", "emoji" },
        { string.Join(' ', Enumerable.Repeat("dur bi dakika bunu kesin okumalısın çünkü", 120)), "Roman", "romanci" },
        { new string('a', 900), "Tek Kelime", "tekkelime" },
        { "https://example.com/" + new string('x', 200), "Link", "link" },
        { string.Join('\n', Enumerable.Repeat("satır", 80)), "Satır", "satir" },
        { "kısa", new string('Ş', 120), new string('u', 32) },
        { "kısa", "Çok uzun bir sunucu takma adı 😂😂😂 ile birlikte gelen isim", "cok_uzun_kullanici_adi_1234" },
    };

    [Fact]
    public void Card_is_a_greyscale_png_of_the_expected_size()
    {
        var card = Renderer.Render(Model("aminiza koim"));
        card.Png.AsSpan(0, 8).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        var info = Image.Identify(card.Png);
        (info.Width, info.Height).Should().Be((QuoteImageRenderer.Width, QuoteImageRenderer.Height));
        (card.Width, card.Height).Should().Be((1600, 800));
        info.Metadata.GetPngMetadata().ColorType.Should().Be(PngColorType.Grayscale);
        card.Png.Length.Should().BeLessThan(8 * 1024 * 1024, "well under Discord's upload limit");
    }

    [Fact]
    public void Avatar_is_drawn_in_black_and_white_and_fades_into_black()
    {
        var (canvas, layout) = Renderer.Compose(Model("aminiza koim", avatar: SolidPng(Color.ParseHex("E53935"), 300, 200)));
        using (canvas)
        {
            layout.AvatarUsed.Should().BeTrue();
            // Before encoding: every photo pixel is grey (R = G = B) although the avatar was saturated red; and it is not black.
            foreach (var (x, y) in new[] { (5, 5), (100, 400), (390, 790), (600, 300), (850, 100) })
            {
                var p = canvas[x, y];
                p.R.Should().Be(p.G, $"({x},{y})");
                p.G.Should().Be(p.B, $"({x},{y})");
            }

            canvas[100, 400].R.Should().BeGreaterThan(20, "the photo is visible on the left");
            // The fade: brightness only falls from left to right, and the right half is pure black outside the text.
            canvas[300, 400].R.Should().BeGreaterThanOrEqualTo(canvas[600, 400].R);
            canvas[600, 400].R.Should().BeGreaterThanOrEqualTo(canvas[800, 400].R);
            canvas[QuoteImageRenderer.FadeEnd + 1, 5].Should().Be(new Rgba32(0, 0, 0, 255));
            canvas[1599, 799].Should().Be(new Rgba32(0, 0, 0, 255));
        }
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void Text_stays_inside_the_text_column_and_the_canvas(string text, string name, string user)
    {
        var (canvas, layout) = Renderer.Compose(Model(text, name, user));
        canvas.Dispose();

        foreach (var (label, box) in new[] { ("message", layout.Message), ("author", layout.Author), ("username", layout.Username) })
        {
            // A little slack for italic overhang and anti-aliasing; nothing reaches into the photo or off the canvas.
            box.Left.Should().BeGreaterThanOrEqualTo(QuoteImageRenderer.TextLeft - 6, label);
            box.Right.Should().BeLessThanOrEqualTo(QuoteImageRenderer.TextRight + 6, label);
            box.Top.Should().BeGreaterThanOrEqualTo(20, label);
            box.Bottom.Should().BeLessThanOrEqualTo(QuoteImageRenderer.Height - 20, label);
        }

        layout.Message.Bottom.Should().BeLessThan(layout.Author.Top, "the author line sits below the message");
        layout.Author.Bottom.Should().BeLessThan(layout.Username.Top, "the username sits below the author");
        layout.MessageFontSize.Should().BeInRange(QuoteImageRenderer.MinFontSize, QuoteImageRenderer.MaxFontSize);
    }

    [Fact]
    public void Font_shrinks_with_length_and_only_the_longest_text_is_cut()
    {
        Layout("aminiza koim").MessageFontSize.Should().Be(QuoteImageRenderer.MaxFontSize);
        var medium = Layout(string.Join(' ', Enumerable.Repeat("bu çok uzun bir discord mesajıdır", 5)));
        medium.MessageFontSize.Should().BeLessThan(QuoteImageRenderer.MaxFontSize);
        medium.Truncated.Should().BeFalse();

        var huge = Layout(string.Join(' ', Enumerable.Repeat("dur bi dakika bunu kesin okumalısın çünkü", 120)));
        huge.MessageFontSize.Should().Be(QuoteImageRenderer.MinFontSize);
        huge.Truncated.Should().BeTrue();
        huge.MessageLines.Should().BeGreaterThan(5);

        static QuoteCardLayout Layout(string text)
        {
            var (canvas, layout) = Renderer.Compose(Model(text));
            canvas.Dispose();
            return layout;
        }
    }

    [Fact]
    public void Cut_text_ends_in_an_ellipsis_on_a_word_and_fits_the_line_budget()
    {
        var options = new TextOptions(QuoteFonts.Load().Sans.CreateFont(32)) { WrappingLength = 300, WordBreaking = WordBreaking.BreakWord };
        var text = string.Join(' ', Enumerable.Repeat("kelime", 200));
        var cut = QuoteImageRenderer.CutToLines(text, options, 3);
        cut.Should().EndWith("…").And.StartWith("kelime kelime");
        cut[..^1].Should().EndWith("kelime", "the cut lands after a whole word");
        TextMeasurer.CountLines(cut, options).Should().BeLessThanOrEqualTo(3);
        QuoteImageRenderer.CutToLines("kısa", options, 1).Should().Be("kısa");
        QuoteImageRenderer.CutToLines(new string('x', 500), options, 1).Should().EndWith("…", "one endless word is cut where it must be");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 })] // a PNG signature and nothing valid after it
    public void Missing_or_broken_avatar_falls_back_without_failing(byte[]? avatar)
    {
        var (canvas, layout) = Renderer.Compose(new QuoteRenderModel("avatar yok", "İlker", "ilker", avatar));
        using (canvas)
        {
            layout.AvatarUsed.Should().BeFalse();
            canvas[100, 100].R.Should().Be(canvas[100, 100].G).And.BeGreaterThan(0, "a dark neutral panel, not a hole");
        }

        Renderer.Render(new QuoteRenderModel("avatar yok", "İlker", "ilker", avatar)).Png.Should().NotBeEmpty();
    }

    [Fact]
    public void An_absurdly_large_image_is_not_decoded()
    {
        QuoteImageRenderer.DecodeAvatar(SolidPng(Color.Gray, QuoteImageRenderer.MaxAvatarDimension + 1, 2)).Should().BeNull();
        using var ok = QuoteImageRenderer.DecodeAvatar(SolidPng(Color.Gray, 1024, 1024));
        ok.Should().NotBeNull();
    }

    [Theory]
    [InlineData("z̵̧̛̖̗͎̦̭̝̖̝̗̱͓̳̈́̀͋͑̅̋͒͑͘͝a̸̧̢̛̦̖͍̜͈͍̹̙̮̺̍̿̓̈́̽̈́̋͝l̴͇̝̘̩̟̮͇̟̬̓̈́̌̓̂̿͋̀̕͝g̷̨̛̬̲̝̘̗͓̝̣̣̉̈́͐̑̿͑̔͘͝ở̶̢̨̛̯̳̦̫͖̜͚̈́̓̀̑̍̈́͂")]
    [InlineData("مرحبا بالعالم שלום עולם")]
    [InlineData("你好世界 こんにちは 안녕하세요")]
    [InlineData("\uD83D lone high surrogate and lone low \uDE00")]
    [InlineData("‮reversed‬ ⁦isolate⁩ \u0000\u0007")]
    [InlineData("👩🏽‍💻 🏳️‍🌈 🧑‍🤝‍🧑 1️⃣ ©️")]
    [InlineData(" ")]
    public void Unusual_unicode_never_throws(string text)
    {
        var card = Renderer.Render(new QuoteRenderModel(text, text, text, null));
        Image.Identify(card.Png).Width.Should().Be(QuoteImageRenderer.Width);
    }
}
