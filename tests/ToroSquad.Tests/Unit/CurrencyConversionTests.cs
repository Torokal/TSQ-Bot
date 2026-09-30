using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// /çevir arithmetic and card, offline: the side of the quote for every supported direction (asset → lira uses Buy,
/// lira → asset uses Sell — locked here against a swap), refusals before any quote, full precision until display,
/// tr-TR display rounding (banker's rounding, 2 decimals for money, up to 4 for grams).
/// </summary>
public sealed class CurrencyConversionTests
{
    private static readonly CurrencyCardRenderer Cards = new(CurrencyTestKit.Localizer());
    private static readonly DateTimeOffset Updated = new(2026, 9, 30, 8, 58, 12, TimeSpan.FromHours(3));

    private static MarketQuote Quote(MarketInstrument instrument, MarketSource source = MarketSource.Altinkaynak, bool fallback = false, bool stale = false)
    {
        var (buy, sell) = instrument switch
        {
            MarketInstrument.Usd => (48.870m, 49.090m),
            MarketInstrument.Eur => (55.500m, 55.800m),
            _ => (6493.80m, 6623.54m),
        };
        return new MarketQuote(instrument, new MarketPrice(buy, sell, Updated, source == MarketSource.Tcmb), source, fallback, stale, CurrencyTestKit.T0);
    }

    private static ConversionResult Convert(decimal amount, ConvertibleAsset from, ConvertibleAsset to, MarketQuote? quote = null)
    {
        CurrencyConversionService.Validate(amount, from, to, out var request).Should().Be(ConversionRefusal.None);
        return CurrencyConversionService.Convert(request!, quote ?? Quote(request!.Instrument));
    }

    // ---------------------------------------------------------------- side and arithmetic

    [Theory]
    [InlineData(ConvertibleAsset.Usd, ConvertibleAsset.Try, MarketInstrument.Usd, RateSide.Buy)]
    [InlineData(ConvertibleAsset.Try, ConvertibleAsset.Usd, MarketInstrument.Usd, RateSide.Sell)]
    [InlineData(ConvertibleAsset.Eur, ConvertibleAsset.Try, MarketInstrument.Eur, RateSide.Buy)]
    [InlineData(ConvertibleAsset.Try, ConvertibleAsset.Eur, MarketInstrument.Eur, RateSide.Sell)]
    [InlineData(ConvertibleAsset.GramGold, ConvertibleAsset.Try, MarketInstrument.GramGold, RateSide.Buy)]
    [InlineData(ConvertibleAsset.Try, ConvertibleAsset.GramGold, MarketInstrument.GramGold, RateSide.Sell)]
    public void Selling_an_asset_uses_buy_and_buying_it_uses_sell(ConvertibleAsset from, ConvertibleAsset to, MarketInstrument instrument, RateSide side)
    {
        CurrencyConversionService.Validate(100m, from, to, out var request).Should().Be(ConversionRefusal.None);
        request!.Instrument.Should().Be(instrument);
        request.Side.Should().Be(side);
    }

    [Fact]
    public void Usd_to_lira_multiplies_by_the_buy_rate()
    {
        var result = Convert(2500m, ConvertibleAsset.Usd, ConvertibleAsset.Try);
        result.Rate.Should().Be(48.870m, "Buy, not Sell (49.090)");
        result.Result.Should().Be(2500m * 48.870m).And.Be(122175m);
        result.Request.Side.Should().Be(RateSide.Buy);
    }

    [Fact]
    public void Lira_to_usd_divides_by_the_sell_rate()
    {
        var result = Convert(2500m, ConvertibleAsset.Try, ConvertibleAsset.Usd);
        result.Rate.Should().Be(49.090m, "Sell, not Buy (48.870)");
        result.Result.Should().Be(2500m / 49.090m);
        result.Result.Should().BeLessThan(2500m / 48.870m, "the dealer's side is the worse one for the user");
        result.Request.Side.Should().Be(RateSide.Sell);
    }

    [Fact]
    public void Eur_to_lira_uses_buy_and_lira_to_eur_uses_sell()
    {
        var toLira = Convert(2500m, ConvertibleAsset.Eur, ConvertibleAsset.Try);
        toLira.Rate.Should().Be(55.500m);
        toLira.Result.Should().Be(2500m * 55.500m);

        var toEur = Convert(2500m, ConvertibleAsset.Try, ConvertibleAsset.Eur);
        toEur.Rate.Should().Be(55.800m);
        toEur.Result.Should().Be(2500m / 55.800m);
    }

    [Fact]
    public void Gold_to_lira_uses_buy_and_lira_to_gold_uses_sell()
    {
        var toLira = Convert(5m, ConvertibleAsset.GramGold, ConvertibleAsset.Try);
        toLira.Rate.Should().Be(6493.80m);
        toLira.Result.Should().Be(5m * 6493.80m).And.Be(32469.00m);

        var toGold = Convert(50000m, ConvertibleAsset.Try, ConvertibleAsset.GramGold);
        toGold.Rate.Should().Be(6623.54m);
        toGold.Result.Should().Be(50000m / 6623.54m);
    }

    [Fact]
    public void Nothing_is_rounded_in_the_calculation()
    {
        var result = Convert(2500m, ConvertibleAsset.Try, ConvertibleAsset.Usd);
        result.Result.Scale.Should().BeGreaterThan(10, "full decimal precision; only the card rounds");
        (result.Result * 49.090m).Should().BeApproximately(2500m, 0.0000000000000000001m);
    }

    [Fact]
    public void A_quote_for_the_wrong_instrument_is_refused()
    {
        CurrencyConversionService.Validate(10m, ConvertibleAsset.Usd, ConvertibleAsset.Try, out var request);
        var act = () => CurrencyConversionService.Convert(request!, Quote(MarketInstrument.Eur));
        act.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------- refusals (no price needed)

    [Theory]
    [InlineData(ConvertibleAsset.Usd, ConvertibleAsset.Usd, ConversionRefusal.SameAsset)]
    [InlineData(ConvertibleAsset.Try, ConvertibleAsset.Try, ConversionRefusal.SameAsset)]
    [InlineData(ConvertibleAsset.Usd, ConvertibleAsset.Eur, ConversionRefusal.UnsupportedPair)]
    [InlineData(ConvertibleAsset.Eur, ConvertibleAsset.Usd, ConversionRefusal.UnsupportedPair)]
    [InlineData(ConvertibleAsset.GramGold, ConvertibleAsset.Usd, ConversionRefusal.UnsupportedPair)]
    [InlineData(ConvertibleAsset.Eur, ConvertibleAsset.GramGold, ConversionRefusal.UnsupportedPair)]
    [InlineData(ConvertibleAsset.Usd, ConvertibleAsset.GramGold, ConversionRefusal.UnsupportedPair)]
    [InlineData(ConvertibleAsset.GramGold, ConvertibleAsset.Eur, ConversionRefusal.UnsupportedPair)]
    public void Cross_and_same_pairs_are_refused(ConvertibleAsset from, ConvertibleAsset to, ConversionRefusal expected)
    {
        CurrencyConversionService.Validate(100m, from, to, out var request).Should().Be(expected);
        request.Should().BeNull();
    }

    [Theory]
    [InlineData("0", ConversionRefusal.AmountNotPositive)]
    [InlineData("-5", ConversionRefusal.AmountNotPositive)]
    [InlineData("0.0001", ConversionRefusal.None)]
    [InlineData("1000000000", ConversionRefusal.None)]
    [InlineData("1000000000.01", ConversionRefusal.AmountTooLarge)]
    public void Amount_must_be_positive_and_at_most_a_billion(string amount, ConversionRefusal expected) =>
        CurrencyConversionService.Validate(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), ConvertibleAsset.Usd, ConvertibleAsset.Try, out _)
            .Should().Be(expected);

    [Fact]
    public void Refusal_texts_are_clear_and_name_the_supported_pairs()
    {
        Cards.RenderRefusal("tr", ConversionRefusal.AmountNotPositive).Should().Be("Miktar 0'dan büyük olmalıdır.");
        Cards.RenderRefusal("tr", ConversionRefusal.AmountTooLarge).Should().Be("Miktar en fazla 1.000.000.000 olabilir.");
        Cards.RenderRefusal("tr", ConversionRefusal.UnsupportedPair).Should()
            .Be("Bu dönüşüm şu anda desteklenmiyor.\nDesteklenen dönüşümler: TRY ↔ USD, TRY ↔ EUR, TRY ↔ Gram Altın.");
        Cards.RenderRefusal("tr", ConversionRefusal.SameAsset).Should().StartWith("Kaynak ve hedef aynı olamaz.\nDesteklenen dönüşümler:");
        Cards.RenderRefusal("en", ConversionRefusal.AmountNotPositive).Should().Be("The amount must be greater than 0.");
    }

    // ---------------------------------------------------------------- display

    [Theory]
    [InlineData("2500", ConvertibleAsset.Usd, "2.500,00 USD")]
    [InlineData("2500", ConvertibleAsset.Eur, "2.500,00 EUR")]
    [InlineData("122175", ConvertibleAsset.Try, "122.175,00 ₺")]
    [InlineData("50.926869016092890608", ConvertibleAsset.Usd, "50,93 USD")]
    [InlineData("0.125", ConvertibleAsset.Usd, "0,12 USD")] // banker's rounding: ,125 → ,12
    [InlineData("0.135", ConvertibleAsset.Usd, "0,14 USD")] // ,135 → ,14
    [InlineData("7.5488122", ConvertibleAsset.GramGold, "7,5488 g")]
    [InlineData("5", ConvertibleAsset.GramGold, "5,00 g")]
    [InlineData("1.5", ConvertibleAsset.GramGold, "1,50 g")]
    [InlineData("0.00125", ConvertibleAsset.GramGold, "0,0012 g")] // banker's rounding at 4 decimals
    [InlineData("0.00005", ConvertibleAsset.GramGold, "<0,0001 g")] // positive, rounds to zero: never "0,00 g"
    [InlineData("0.00001", ConvertibleAsset.GramGold, "<0,0001 g")]
    [InlineData("0.0001", ConvertibleAsset.GramGold, "0,0001 g")]
    [InlineData("0.001", ConvertibleAsset.Usd, "<0,01 USD")]
    [InlineData("0.001", ConvertibleAsset.Eur, "<0,01 EUR")]
    [InlineData("0.001", ConvertibleAsset.Try, "<0,01 ₺")]
    [InlineData("0.005", ConvertibleAsset.Usd, "<0,01 USD")] // banker's rounding would give 0,00
    [InlineData("0.01", ConvertibleAsset.Usd, "0,01 USD")]
    [InlineData("0", ConvertibleAsset.Try, "0,00 ₺")] // a real zero stays a zero
    [InlineData("0", ConvertibleAsset.GramGold, "0,00 g")]
    public void Amounts_are_tr_TR_with_fixed_display_precision(string value, ConvertibleAsset asset, string expected) =>
        CurrencyCardRenderer.Amount(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), asset).Should().Be(expected);

    [Fact]
    public void The_rate_keeps_the_provider_precision()
    {
        CurrencyCardRenderer.Price(48.870m).Should().Be("48,870 ₺");
        CurrencyCardRenderer.Price(6493.80m).Should().Be("6.493,80 ₺");
    }

    [Fact]
    public void Usd_to_lira_card()
    {
        var reply = Cards.RenderConversion("tr", Convert(2500m, ConvertibleAsset.Usd, ConvertibleAsset.Try));
        reply.Ephemeral.Should().BeFalse();
        var card = reply.Embed!;
        card.Title.Should().Be("💱 Döviz Çevirici");
        card.Description.Should().Be("**2.500,00 USD**\n≈ **122.175,00 ₺**");
        card.Fields.Select(f => (f.Name, f.Value)).Take(2).Should().Equal(("Kullanılan kur", "48,870 ₺"), ("Kur türü", "Alış"));
        card.Fields[2].Name.Should().Be("Güncellendi");
        card.Fields[2].Value.Should().StartWith($"<t:{Updated.ToUnixTimeSeconds()}:R>");
        card.Footer.Should().Be("Kaynak: Altınkaynak");
        ToroSquad.Core.Messaging.DiscordLimits.Validate(new ToroSquad.Core.Messaging.OutgoingMessage(null, card, ToroSquad.Core.Messaging.MentionPolicy.None)).Should().BeEmpty();
    }

    [Fact]
    public void Lira_to_usd_card()
    {
        var card = Cards.RenderConversion("tr", Convert(2500m, ConvertibleAsset.Try, ConvertibleAsset.Usd)).Embed!;
        card.Description.Should().Be("**2.500,00 ₺**\n≈ **50,93 USD**");
        card.Fields.Select(f => (f.Name, f.Value)).Take(2).Should().Equal(("Kullanılan kur", "49,090 ₺"), ("Kur türü", "Satış"));
    }

    [Fact]
    public void Gold_cards_show_the_gram_price()
    {
        var toLira = Cards.RenderConversion("tr", Convert(5m, ConvertibleAsset.GramGold, ConvertibleAsset.Try)).Embed!;
        toLira.Description.Should().Be("**5,00 g**\n≈ **32.469,00 ₺**");
        toLira.Fields.Select(f => (f.Name, f.Value)).Take(2).Should().Equal(("Gram fiyatı", "6.493,80 ₺"), ("Kur türü", "Alış"));

        var toGold = Cards.RenderConversion("tr", Convert(50000m, ConvertibleAsset.Try, ConvertibleAsset.GramGold)).Embed!;
        toGold.Description.Should().Be("**50.000,00 ₺**\n≈ **7,5488 g**");
        toGold.Fields.Select(f => (f.Name, f.Value)).Take(2).Should().Equal(("Gram fiyatı", "6.623,54 ₺"), ("Kur türü", "Satış"));
    }

    [Fact]
    public void Tcmb_fallback_stale_and_truncgil_are_labelled()
    {
        var tcmb = Cards.RenderConversion("tr", Convert(2500m, ConvertibleAsset.Usd, ConvertibleAsset.Try, Quote(MarketInstrument.Usd, MarketSource.Tcmb, fallback: true))).Embed!;
        tcmb.Footer.Should().Be("Kaynak: TCMB — Gösterge Kuru");
        tcmb.Description.Should().EndWith("\n\nBirincil veri kaynağına ulaşılamadı.\nTCMB günlük gösterge kurudur; anlık piyasa fiyatı değildir.");
        tcmb.Fields[2].Name.Should().Be("Bülten tarihi");

        var truncgil = Cards.RenderConversion("tr", Convert(50000m, ConvertibleAsset.Try, ConvertibleAsset.GramGold, Quote(MarketInstrument.GramGold, MarketSource.Truncgil, fallback: true))).Embed!;
        truncgil.Footer.Should().Be("Kaynak: Trunçgil");
        truncgil.Description.Should().EndWith("\n\nBirincil veri kaynağına ulaşılamadı.");

        var stale = Cards.RenderConversion("tr", Convert(2500m, ConvertibleAsset.Usd, ConvertibleAsset.Try, Quote(MarketInstrument.Usd, stale: true))).Embed!;
        stale.Description.Should().EndWith("\n\n⚠️ Son başarılı fiyat kullanılıyor.");
        stale.Fields.Should().Contain(f => f.Name == "Son başarılı sorgu");
        stale.Color.Should().Be(CurrencyCardRenderer.StaleColor);
    }
}
