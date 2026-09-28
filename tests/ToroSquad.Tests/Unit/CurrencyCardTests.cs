using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The /dolar, /euro and /altın answers (one shared layout): public, tr-TR lira formatting, provider time as Discord
/// timestamps, the source in the footer, fallback / indicative / stale labelling, and the unavailable text.
/// </summary>
public sealed class CurrencyCardTests
{
    private static readonly CurrencyCardRenderer Cards = new(CurrencyTestKit.Localizer());
    private static readonly DateTimeOffset Updated = new(2026, 9, 28, 11, 51, 42, TimeSpan.FromHours(3));
    private static readonly DateTimeOffset Retrieved = CurrencyTestKit.T0;

    private static MarketQuote Quote(MarketInstrument instrument, decimal buy, decimal sell, MarketSource source = MarketSource.Altinkaynak,
        bool fallback = false, bool stale = false, bool dateOnly = false, DateTimeOffset? updated = null) =>
        new(instrument, new MarketPrice(buy, sell, updated ?? Updated, dateOnly), source, fallback, stale, Retrieved);

    private static MessageEmbed Card(MarketQuote quote, string lang = "tr")
    {
        var reply = Cards.Render(lang, MarketQuoteResult.Ok(quote));
        reply.Ephemeral.Should().BeFalse("everyone in the channel sees the price");
        reply.Text.Should().BeNull();
        DiscordLimits.Validate(new OutgoingMessage(null, reply.Embed, MentionPolicy.None)).Should().BeEmpty();
        return reply.Embed!;
    }

    private static string Field(MessageEmbed embed, string name) => embed.Fields.Single(f => f.Name == name).Value;

    [Fact]
    public void Dollar_card_shows_buy_sell_source_and_the_provider_time()
    {
        var card = Card(Quote(MarketInstrument.Usd, 48.820m, 49.030m));
        card.Title.Should().Be("💵 Amerikan Doları");
        Field(card, "Alış").Should().Be("48,820 ₺");
        Field(card, "Satış").Should().Be("49,030 ₺");
        card.Fields.Take(2).Should().OnlyContain(f => f.Inline);
        var unix = Updated.ToUnixTimeSeconds();
        Field(card, "Güncellendi").Should().Be($"<t:{unix}:R> · <t:{unix}:f>");
        card.Footer.Should().Be("Kaynak: Altınkaynak");
        card.Description.Should().BeNull("a normal primary answer carries no notes");
        card.Color.Should().Be(CurrencyCardRenderer.NormalColor);
        card.Timestamp.Should().BeNull("the provider time is shown as a Discord timestamp, never the request time");
    }

    [Fact]
    public void Euro_and_gold_cards_use_the_same_layout()
    {
        var euro = Card(Quote(MarketInstrument.Eur, 55.508m, 55.819m));
        euro.Title.Should().Be("💶 Euro");
        Field(euro, "Alış").Should().Be("55,508 ₺");
        Field(euro, "Satış").Should().Be("55,819 ₺");

        var gold = Card(Quote(MarketInstrument.GramGold, 6439.47m, 6574.16m));
        gold.Title.Should().Be("🪙 Gram Altın");
        Field(gold, "Alış").Should().Be("6.439,47 ₺");
        Field(gold, "Satış").Should().Be("6.574,16 ₺");

        var dollar = Card(Quote(MarketInstrument.Usd, 48.820m, 49.030m));
        euro.Fields.Select(f => f.Name).Should().Equal(dollar.Fields.Select(f => f.Name));
        gold.Fields.Select(f => f.Name).Should().Equal(dollar.Fields.Select(f => f.Name));
    }

    [Theory]
    [InlineData("48.820", "48,820 ₺")]
    [InlineData("6439.47", "6.439,47 ₺")]
    [InlineData("48.7901", "48,7901 ₺")]
    [InlineData("6540.3", "6.540,30 ₺")]
    [InlineData("49", "49,00 ₺")]
    [InlineData("213629.73", "213.629,73 ₺")]
    [InlineData("1234.567891", "1.234,5679 ₺")]
    public void Prices_are_formatted_tr_TR_keeping_the_provider_precision(string value, string expected) =>
        CurrencyCardRenderer.Price(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(expected);

    [Fact]
    public void Tcmb_fallback_is_labelled_as_an_indicative_rate_with_its_bulletin_date()
    {
        var bulletin = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.FromHours(3));
        var card = Card(Quote(MarketInstrument.Usd, 48.7901m, 48.8780m, MarketSource.Tcmb, fallback: true, dateOnly: true, updated: bulletin));
        card.Footer.Should().Be("Kaynak: TCMB — Gösterge Kuru");
        card.Description.Should().Be("Birincil veri kaynağına ulaşılamadı.\nTCMB'nin günlük gösterge kurudur; anlık piyasa fiyatı değildir.");
        Field(card, "Bülten tarihi").Should().Be("25.09.2026");
        card.Fields.Should().NotContain(f => f.Value.Contains("<t:", StringComparison.Ordinal), "a date-only bulletin is not shown as a moment in time");
        Field(card, "Alış").Should().Be("48,7901 ₺");
        card.Color.Should().Be(CurrencyCardRenderer.NormalColor, "a working fallback is not an error");
    }

    [Fact]
    public void Truncgil_fallback_names_its_source()
    {
        var card = Card(Quote(MarketInstrument.GramGold, 6539.59m, 6540.36m, MarketSource.Truncgil, fallback: true));
        card.Footer.Should().Be("Kaynak: Trunçgil");
        card.Description.Should().Be("Birincil veri kaynağına ulaşılamadı.");
    }

    [Fact]
    public void Stale_price_carries_the_warning_its_time_and_how_long_ago_it_was_received()
    {
        var card = Card(Quote(MarketInstrument.Usd, 48.820m, 49.030m, stale: true));
        card.Description.Should().Be("⚠️ Veri kaynağına şu anda ulaşılamıyor. Son başarılı fiyat gösteriliyor.");
        Field(card, "Güncellendi").Should().Contain($"<t:{Updated.ToUnixTimeSeconds()}:R>");
        Field(card, "Son başarılı sorgu").Should().Be($"<t:{Retrieved.ToUnixTimeSeconds()}:R>");
        card.Footer.Should().Be("Kaynak: Altınkaynak");
        card.Color.Should().Be(CurrencyCardRenderer.StaleColor);
    }

    [Fact]
    public void Unavailable_is_a_short_text_with_the_trace_code_and_no_detail()
    {
        var reply = Cards.Render("tr", MarketQuoteResult.Unavailable("TS-ABCDEFGH"));
        reply.Embed.Should().BeNull();
        reply.Ephemeral.Should().BeFalse();
        reply.Text.Should().Be("Döviz/altın verisine şu anda ulaşılamıyor. Lütfen kısa süre sonra tekrar deneyin.\nTakip kodu: `TS-ABCDEFGH`");
    }

    [Fact]
    public void English_guilds_get_the_english_card()
    {
        var card = Card(Quote(MarketInstrument.Usd, 48.820m, 49.030m, MarketSource.Tcmb, fallback: true, dateOnly: true), "en");
        card.Title.Should().Be("💵 US Dollar");
        Field(card, "Buy").Should().Be("48,820 ₺", "the lira amount keeps its Turkish format");
        card.Footer.Should().Be("Source: CBRT — Indicative Rate");
    }
}
