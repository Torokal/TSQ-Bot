using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Modules.Quote.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>TSQ Quote: what /quote message accepts, and how a message's Discord text becomes the card's plain text.</summary>
public sealed class QuoteParsingAndTextTests
{
    private const ulong Guild = 689812743242514448;
    private const ulong Channel = 1200000000000000001;
    private const ulong Message = 1300000000000000002;

    // ---- message reference ----

    [Theory]
    [InlineData("1300000000000000002")]
    [InlineData("  1300000000000000002  ")]
    [InlineData("81384788765712384")] // 17 digits: the oldest ids
    [InlineData("18446744073709551615")] // ulong.MaxValue, 20 digits
    public void A_raw_snowflake_is_a_bare_message_id(string input) =>
        QuoteReference.Parse(input).Should().BeOfType<QuoteReference.MessageOnly>()
            .Which.Message.Value.Should().Be(ulong.Parse(input.Trim(), CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("123")] // too short for a snowflake
    [InlineData("0000000000000000000")] // zero
    [InlineData("18446744073709551616")] // above ulong
    [InlineData("123456789012345678901")] // 21 digits
    [InlineData("-1300000000000000002")]
    [InlineData("+1300000000000000002")]
    [InlineData("1300000000000000002 1300000000000000003")]
    [InlineData("1 300 000 000 000 000 002")]
    [InlineData("１３０００００００００００００００００２")] // full-width digits
    public void Anything_else_that_is_not_a_link_is_invalid(string? input) =>
        QuoteReference.Parse(input).Should().BeOfType<QuoteReference.Invalid>();

    [Theory]
    [InlineData("https://discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002")]
    [InlineData("https://ptb.discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002")]
    [InlineData("https://canary.discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002")]
    [InlineData("https://discordapp.com/channels/689812743242514448/1200000000000000001/1300000000000000002")]
    [InlineData("https://DISCORD.com/channels/689812743242514448/1200000000000000001/1300000000000000002/")]
    [InlineData("https://discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002?foo=bar#x")]
    [InlineData("<https://discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002>")]
    public void A_discord_message_link_names_guild_channel_and_message(string input) =>
        QuoteReference.Parse(input).Should().Be(new QuoteReference.Link(new GuildId(Guild), new ChannelId(Channel), new MessageId(Message)));

    [Theory]
    [InlineData("http://discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002")] // not https
    [InlineData("https://discord.com:8443/channels/689812743242514448/1200000000000000001/1300000000000000002")] // port
    [InlineData("https://user@discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002")] // userinfo
    [InlineData("https://discord.com.evil.example/channels/689812743242514448/1200000000000000001/1300000000000000002")]
    [InlineData("https://evil-discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002")]
    [InlineData("https://discord.gg/channels/689812743242514448/1200000000000000001/1300000000000000002")]
    [InlineData("https://discord.com/channels/689812743242514448/1200000000000000001")] // channel link, no message
    [InlineData("https://discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002/extra")]
    [InlineData("https://discord.com/guilds/689812743242514448/1200000000000000001/1300000000000000002")]
    [InlineData("https://discord.com/channels/abc/1200000000000000001/1300000000000000002")]
    [InlineData("https://discord.com/channels/689812743242514448/12/1300000000000000002")]
    [InlineData("discord.com/channels/689812743242514448/1200000000000000001/1300000000000000002")] // no scheme
    [InlineData("javascript:alert(1)")]
    public void Malformed_or_foreign_links_are_invalid(string input) =>
        QuoteReference.Parse(input).Should().BeOfType<QuoteReference.Invalid>();

    [Fact]
    public void A_direct_message_link_is_recognized_as_such()
    {
        QuoteReference.Parse("https://discord.com/channels/@me/1200000000000000001/1300000000000000002")
            .Should().BeOfType<QuoteReference.DirectMessageLink>();
        QuoteReference.Parse(new string('1', QuoteReference.MaxInputLength + 1)).Should().BeOfType<QuoteReference.Invalid>();
    }

    // ---- message text ----

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private static readonly QuoteMentionNames Names = new(
        new Dictionary<ulong, string> { [111111111111111111] = "PizzaVenk", [222222222222222222] = "__alt__çizgi" },
        new Dictionary<ulong, string> { [333333333333333333] = "Moderatör" },
        new Dictionary<ulong, string> { [444444444444444444] = "genel-sohbet" });

    private static string N(string? text, QuoteMentionNames? names = null) => QuoteText.Normalize(text, names ?? Names, Tr, Istanbul);

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   \n\t  \n ", "")]
    [InlineData("**   **", "**   **")] // no text inside: nothing to strip, and still not empty
    public void Empty_or_blank_content_gives_empty_text(string? input, string expected) => N(input).Should().Be(expected);

    [Fact]
    public void Turkish_characters_and_emoji_are_kept_exactly()
    {
        const string text = "Ğğ Üü Şş İı Öö Çç — ığdır İSTANBUL 😂🔥 👍🏽 🇹🇷";
        N(text).Should().Be(text);
    }

    [Fact]
    public void Newlines_are_kept_and_outer_whitespace_trimmed()
    {
        N("  ilk satır  \r\nikinci satır\n\n\n\n\nson satır\t \n").Should().Be("ilk satır\nikinci satır\n\nson satır");
        N("a\rb").Should().Be("a\nb");
    }

    [Fact]
    public void Mentions_become_readable_names()
    {
        N("<@111111111111111111> ve <@!111111111111111111>: <@&333333333333333333> kanalı <#444444444444444444>'a baksın")
            .Should().Be("@PizzaVenk ve @PizzaVenk: @Moderatör kanalı #genel-sohbet'a baksın");
        // Unknown ids never leak as raw markup.
        N("<@999999999999999999> <@&999999999999999999> <#999999999999999999>").Should().Be("@unknown-user @unknown-role #unknown-channel");
        // A name is text, not markdown: "__alt__çizgi" keeps its underscores.
        N("selam <@222222222222222222>").Should().Be("selam @__alt__çizgi");
    }

    [Fact]
    public void Custom_emoji_commands_and_timestamps_read_like_the_client()
    {
        N("gg <:pog:123456789012345678> <a:dance:123456789012345678>").Should().Be("gg :pog: :dance:");
        N("</ekip:123456789012345678> yaz").Should().Be("/ekip yaz");
        // 2026-09-27 12:00 UTC = 15:00 in Istanbul; relative stamps become a fixed date (a card cannot say "3 hours ago" forever).
        N("<t:1790510400:d>").Should().Be("27.09.2026");
        N("<t:1790510400:t>").Should().Be("15:00");
        N("<t:1790510400:R>").Should().Be("27.09.2026 15:00");
    }

    [Theory]
    [InlineData("**hello**", "hello")]
    [InlineData("__altı çizili__", "altı çizili")]
    [InlineData("*italik* ve _italik_", "italik ve italik")]
    [InlineData("***kalın italik***", "kalın italik")]
    [InlineData("~~üstü çizili~~", "üstü çizili")]
    [InlineData("||spoiler||", "spoiler")]
    [InlineData("**__iç içe__**", "iç içe")]
    [InlineData("# Başlık\n## Alt başlık\n-# küçük not", "Başlık\nAlt başlık\nküçük not")]
    [InlineData("> alıntı\nnormal", "alıntı\nnormal")]
    [InlineData(">>> çok satırlı\nalıntı", "çok satırlı\nalıntı")]
    [InlineData("[TSQ](https://example.com)", "TSQ")]
    [InlineData("<https://example.com/a>", "https://example.com/a")]
    [InlineData("`kod`", "kod")]
    [InlineData("```cs\nvar x = **y**;\n```", "var x = **y**;")]
    [InlineData("``tek `tırnak` kod``", "tek `tırnak` kod")]
    public void Formatting_markers_go_and_the_words_stay(string input, string expected) => N(input).Should().Be(expected);

    [Theory]
    [InlineData("2*3*4 = 24", "2*3*4 = 24")]
    [InlineData("snake_case_name", "snake_case_name")]
    [InlineData("\\*yıldız\\* ve \\_alt\\_", "*yıldız* ve _alt_")]
    [InlineData("5 * 3 = 15 * 1", "5 * 3 = 15 * 1")]
    [InlineData("- madde bir\n- madde iki", "- madde bir\n- madde iki")]
    [InlineData("C# ve #etiket", "C# ve #etiket")]
    [InlineData("a > b", "a > b")]
    [InlineData("`<@111111111111111111>` kodda kalır", "<@111111111111111111> kodda kalır")]
    public void Text_that_only_looks_like_formatting_keeps_its_meaning(string input, string expected) => N(input).Should().Be(expected);

    [Fact]
    public void Control_and_bidi_override_characters_are_dropped()
    {
        N("a\u0000b‮c⁦d\u0007").Should().Be("abcd");
        N("<@1>", new QuoteMentionNames(new Dictionary<ulong, string> { [1] = "‮kötü\u0000" }, new Dictionary<ulong, string>(), new Dictionary<ulong, string>()))
            .Should().Be("@kötü");
    }

    [Fact]
    public void Layout_limit_cuts_between_graphemes_and_marks_the_cut()
    {
        QuoteText.LimitForLayout("kısa").Should().Be("kısa");
        var emoji = string.Concat(Enumerable.Repeat("👍🏽", 400)); // 4 UTF-16 units per grapheme
        var cut = QuoteText.LimitForLayout(emoji);
        cut.Length.Should().BeLessThanOrEqualTo(QuoteText.MaxLayoutLength);
        cut.Should().EndWith("…");
        cut[..^1].Should().Be(string.Concat(Enumerable.Repeat("👍🏽", cut[..^1].Length / 4)), "no emoji is split");
        QuoteText.LimitForLayout(new string('ş', 5000)).Length.Should().Be(QuoteText.MaxLayoutLength);
    }
}
