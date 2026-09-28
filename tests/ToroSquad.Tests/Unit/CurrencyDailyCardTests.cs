using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The daily 09:00 card: one embed with USD, EUR and gram gold, each with its own source and time (they may come from
/// different providers), the same labelling rules as the single cards, and no mention of anyone.
/// </summary>
public sealed class CurrencyDailyCardTests
{
    private static readonly CurrencyCardRenderer Cards = new(CurrencyTestKit.Localizer());
    private static readonly DateTimeOffset Updated = new(2026, 9, 29, 8, 58, 12, TimeSpan.FromHours(3));
    private static readonly DateTimeOffset Bulletin = new(2026, 9, 28, 0, 0, 0, TimeSpan.FromHours(3));
    private static readonly DateTimeOffset Retrieved = new(2026, 9, 29, 5, 59, 0, TimeSpan.Zero);

    private static MarketQuoteResult Ok(MarketInstrument instrument, decimal buy, decimal sell, MarketSource source = MarketSource.Altinkaynak,
        bool fallback = false, bool stale = false) =>
        MarketQuoteResult.Ok(new MarketQuote(instrument, new MarketPrice(buy, sell, source == MarketSource.Tcmb ? Bulletin : Updated, source == MarketSource.Tcmb),
            source, fallback, stale, Retrieved));

    private static Dictionary<MarketInstrument, MarketQuoteResult> AllPrimary() => new()
    {
        [MarketInstrument.Usd] = Ok(MarketInstrument.Usd, 48.900m, 49.090m),
        [MarketInstrument.Eur] = Ok(MarketInstrument.Eur, 55.534m, 55.862m),
        [MarketInstrument.GramGold] = Ok(MarketInstrument.GramGold, 6493.80m, 6623.54m),
    };

    private static MessageEmbed Render(Dictionary<MarketInstrument, MarketQuoteResult> results, string lang = "tr")
    {
        var embed = Cards.RenderDaily(lang, results)!;
        var message = new OutgoingMessage(null, embed, MentionPolicy.None);
        DiscordLimits.Validate(message).Should().BeEmpty();
        message.Mentions.PingsAnything.Should().BeFalse();
        var text = embed.Title + embed.Description + string.Join("", embed.Fields.Select(f => f.Name + f.Value)) + embed.Footer;
        DiscordText.RawMentionPattern().IsMatch(text).Should().BeFalse("no @everyone, @here, role or user mention");
        return embed;
    }

    [Fact]
    public void One_embed_with_all_three_instruments_their_prices_sources_and_times()
    {
        var card = Render(AllPrimary());
        card.Title.Should().Be("💱 Günlük Döviz & Altın");
        card.Fields.Select(f => f.Name).Should().Equal("💵 Amerikan Doları", "💶 Euro", "🪙 Gram Altın");
        card.Footer.Should().BeNull("sources are per instrument, never one shared footer");
        var unix = Updated.ToUnixTimeSeconds();
        card.Fields[0].Value.Should().Be($"Alış: **48,900 ₺** · Satış: **49,090 ₺**\nKaynak: Altınkaynak · Güncellendi: <t:{unix}:R>");
        card.Fields[1].Value.Should().Be($"Alış: **55,534 ₺** · Satış: **55,862 ₺**\nKaynak: Altınkaynak · Güncellendi: <t:{unix}:R>");
        card.Fields[2].Value.Should().Be($"Alış: **6.493,80 ₺** · Satış: **6.623,54 ₺**\nKaynak: Altınkaynak · Güncellendi: <t:{unix}:R>");
        card.Fields.Should().OnlyContain(f => !f.Inline);
        card.Color.Should().Be(CurrencyCardRenderer.NormalColor);
    }

    [Fact]
    public void Mixed_providers_are_each_named_and_tcmb_keeps_its_indicative_note()
    {
        var card = Render(new Dictionary<MarketInstrument, MarketQuoteResult>
        {
            [MarketInstrument.Usd] = Ok(MarketInstrument.Usd, 48.7901m, 48.8780m, MarketSource.Tcmb, fallback: true),
            [MarketInstrument.Eur] = Ok(MarketInstrument.Eur, 55.534m, 55.862m),
            [MarketInstrument.GramGold] = Ok(MarketInstrument.GramGold, 6539.59m, 6540.36m, MarketSource.Truncgil, fallback: true),
        });
        card.Fields[0].Value.Should().Be("Alış: **48,7901 ₺** · Satış: **48,8780 ₺**\nKaynak: TCMB — Gösterge Kuru (yedek kaynak) · Bülten: 28.09.2026\n" +
                                         "TCMB günlük gösterge kurudur; anlık piyasa fiyatı değildir.");
        card.Fields[0].Value.Should().NotContain("<t:", "a date-only bulletin is not shown as a moment");
        card.Fields[1].Value.Should().Contain("Kaynak: Altınkaynak · Güncellendi: <t:").And.NotContain("yedek");
        card.Fields[2].Value.Should().Contain("Kaynak: Trunçgil (yedek kaynak) · Güncellendi: <t:").And.NotContain("TCMB");
    }

    [Fact]
    public void A_stale_instrument_is_marked_with_its_fetch_age()
    {
        var results = AllPrimary();
        results[MarketInstrument.Eur] = Ok(MarketInstrument.Eur, 55.534m, 55.862m, stale: true);
        var card = Render(results);
        card.Fields[1].Value.Should().StartWith($"⚠️ Son başarılı fiyat · son sorgu <t:{Retrieved.ToUnixTimeSeconds()}:R>\nAlış: **55,534 ₺**");
        card.Fields[0].Value.Should().NotContain("⚠️");
        card.Color.Should().Be(CurrencyCardRenderer.StaleColor);
    }

    [Fact]
    public void An_unavailable_instrument_says_so_and_the_others_are_still_shown()
    {
        var results = AllPrimary();
        results[MarketInstrument.GramGold] = MarketQuoteResult.Unavailable("TS-ABCDEFGH");
        var card = Render(results);
        card.Fields.Should().HaveCount(3);
        card.Fields[2].Should().Be(new EmbedField("🪙 Gram Altın", "Şu anda alınamadı"));
        card.Fields[0].Value.Should().Contain("48,900 ₺");
        card.Fields[1].Value.Should().Contain("55,534 ₺");
        string.Join("", card.Fields.Select(f => f.Value)).Should().NotContain("TS-", "no trace code in a public automatic card");
    }

    [Fact]
    public void Nothing_to_post_when_no_instrument_has_a_price()
    {
        var none = new Dictionary<MarketInstrument, MarketQuoteResult>
        {
            [MarketInstrument.Usd] = MarketQuoteResult.Unavailable("TS-A"),
            [MarketInstrument.Eur] = MarketQuoteResult.Unavailable("TS-B"),
            [MarketInstrument.GramGold] = MarketQuoteResult.Unavailable("TS-C"),
        };
        Cards.RenderDaily("tr", none).Should().BeNull();
        Cards.RenderDaily("tr", new Dictionary<MarketInstrument, MarketQuoteResult>()).Should().BeNull();
    }

    [Fact]
    public void English_guilds_get_the_english_daily_card()
    {
        var card = Render(AllPrimary(), "en");
        card.Title.Should().Be("💱 Daily Currency & Gold");
        card.Fields[0].Name.Should().Be("💵 US Dollar");
        card.Fields[0].Value.Should().StartWith("Buy: **48,900 ₺** · Sell: **49,090 ₺**\nSource: Altınkaynak · Updated: <t:");
    }

    [Fact]
    public void The_single_command_card_is_unchanged_by_the_daily_card()
    {
        var single = Cards.Render("tr", AllPrimary()[MarketInstrument.Usd]).Embed!;
        single.Title.Should().Be("💵 Amerikan Doları");
        single.Fields.Select(f => f.Name).Should().Equal("Alış", "Satış", "Güncellendi");
        single.Footer.Should().Be("Kaynak: Altınkaynak");
    }
}
