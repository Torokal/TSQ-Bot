using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Modules.Currency.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Support.CurrencyPayloads;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Döviz &amp; Altın provider parsers, offline: tr-TR and invariant numbers into decimal, Türkiye local times, instrument
/// selection (GA, never PGA), and every malformed shape turning into a provider failure instead of a price.
/// </summary>
public sealed class CurrencyParserTests
{
    private static readonly TimeZoneInfo Turkey = CurrencyTestKit.Turkey;

    private static DateTimeOffset TurkeyTime(int year, int month, int day, int hour, int minute, int second) =>
        new(year, month, day, hour, minute, second, TimeSpan.FromHours(3));

    // ---------------------------------------------------------------- numbers

    [Theory]
    [InlineData("48,820", "48.820")]
    [InlineData("6.439,47", "6439.47")]
    [InlineData("0,3072", "0.3072")]
    [InlineData("213.629,73", "213629.73")]
    [InlineData("1.234.567,8", "1234567.8")]
    [InlineData(" 49,030 ", "49.030")]
    public void Turkish_numbers_use_the_comma_as_decimal_separator(string text, string expected)
    {
        ProviderFormats.TryParseTurkishDecimal(text, out var value).Should().BeTrue();
        value.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Forty_eight_comma_eight_twenty_is_48_lira_not_48_thousand()
    {
        ProviderFormats.TryParseTurkishDecimal("48,820", out var value).Should().BeTrue();
        value.Should().Be(48.820m);
        value.Should().BeLessThan(49m);
        value.Scale.Should().Be(3, "the provider's precision is kept for display");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("48.820")] // no decimal comma: ambiguous (48.82 or 48 820) — rejected, never guessed
    [InlineData("6,439.47")] // en-US shape
    [InlineData("48,82,0")]
    [InlineData("1.23,45")]
    [InlineData("-48,820")]
    [InlineData("+48,820")]
    [InlineData("48,")]
    [InlineData(",5")]
    [InlineData("abc")]
    [InlineData("48,820 TL")]
    [InlineData("٤٨,٨٢٠")] // Arabic-Indic digits
    public void Malformed_turkish_numbers_are_rejected_not_read_as_zero(string? text)
    {
        ProviderFormats.TryParseTurkishDecimal(text, out var value).Should().BeFalse();
        value.Should().Be(0m);
    }

    [Theory]
    [InlineData("48.7901", "48.7901", true)]
    [InlineData("55.688", "55.688", true)]
    [InlineData("48,7901", null, false)]
    [InlineData("1,234.5", null, false)]
    [InlineData("-1.5", null, false)]
    [InlineData("", null, false)]
    public void Tcmb_numbers_are_invariant_dot_decimals(string text, string? expected, bool ok)
    {
        ProviderFormats.TryParseInvariantDecimal(text, out var value).Should().Be(ok);
        if (ok)
            value.Should().Be(decimal.Parse(expected!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Provider_times_are_turkey_local_not_utc()
    {
        ProviderFormats.TryParseTurkeyLocal("28.09.2026 11:51:42", AltinkaynakParser.TimestampFormat, Turkey, out var instant).Should().BeTrue();
        instant.Offset.Should().Be(TimeSpan.FromHours(3));
        instant.UtcDateTime.Should().Be(new DateTime(2026, 9, 28, 8, 51, 42, DateTimeKind.Utc), "not shifted by three hours");
        ProviderFormats.TryParseTurkeyLocal("2026-09-28T11:51:42", AltinkaynakParser.TimestampFormat, Turkey, out _).Should().BeFalse();
        ProviderFormats.TryParseTurkeyLocal("31.02.2026 11:51:42", AltinkaynakParser.TimestampFormat, Turkey, out _).Should().BeFalse();
    }

    // ---------------------------------------------------------------- Altınkaynak Currency

    [Fact]
    public void Altinkaynak_currency_finds_usd_and_eur_among_other_currencies()
    {
        var parsed = AltinkaynakParser.ParseCurrency(Bytes(AltinkaynakCurrency), Turkey);
        var usd = parsed[MarketInstrument.Usd].Price!;
        usd.Buy.Should().Be(48.820m);
        usd.Sell.Should().Be(49.030m);
        usd.SourceTimestamp.Should().Be(TurkeyTime(2026, 9, 28, 11, 51, 42));
        usd.SourceDateOnly.Should().BeFalse();

        var eur = parsed[MarketInstrument.Eur].Price!;
        eur.Buy.Should().Be(55.508m, "Alis is the buy side");
        eur.Sell.Should().Be(55.819m, "Satis is the sell side");
        parsed.Should().HaveCount(2, "CHF, JPY, KWD are not read");
    }

    [Fact]
    public void Missing_usd_or_eur_fails_only_that_instrument()
    {
        var withoutUsd = AltinkaynakCurrency.Replace("\"Kod\":\"USD\"", "\"Kod\":\"XXX\"", StringComparison.Ordinal);
        var parsed = AltinkaynakParser.ParseCurrency(Bytes(withoutUsd), Turkey);
        parsed[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.InstrumentMissing);
        parsed[MarketInstrument.Eur].Price.Should().NotBeNull();

        var withoutEur = AltinkaynakCurrency.Replace("\"Kod\":\"EUR\"", "\"Kod\":\"XXX\"", StringComparison.Ordinal);
        parsed = AltinkaynakParser.ParseCurrency(Bytes(withoutEur), Turkey);
        parsed[MarketInstrument.Eur].Failure.Should().Be(ProviderFailure.InstrumentMissing);
        parsed[MarketInstrument.Usd].Price.Should().NotBeNull();
    }

    [Theory]
    [InlineData("\"Alis\":\"0,000\",\"Satis\":\"0,000\"", ProviderFailure.InvalidPrice)] // zero
    [InlineData("\"Alis\":\"48,820\",\"Satis\":\"0,000\"", ProviderFailure.InvalidPrice)]
    [InlineData("\"Alis\":\"-48,820\",\"Satis\":\"49,030\"", ProviderFailure.InvalidPrice)] // negative
    [InlineData("\"Alis\":\"yok\",\"Satis\":\"49,030\"", ProviderFailure.InvalidPrice)] // invalid price string
    [InlineData("\"Alis\":\"48.820\",\"Satis\":\"49.030\"", ProviderFailure.InvalidPrice)] // format changed
    [InlineData("\"Alis\":\"49,030\",\"Satis\":\"48,820\"", ProviderFailure.InvalidPrice)] // buy/sell crossed
    [InlineData("\"Alis\":null,\"Satis\":\"49,030\"", ProviderFailure.InvalidPrice)]
    public void Bad_usd_prices_are_provider_failures(string prices, ProviderFailure expected)
    {
        var body = AltinkaynakCurrency.Replace("\"Alis\":\"48,820\",\"Satis\":\"49,030\"", prices, StringComparison.Ordinal);
        var parsed = AltinkaynakParser.ParseCurrency(Bytes(body), Turkey);
        parsed[MarketInstrument.Usd].Price.Should().BeNull();
        parsed[MarketInstrument.Usd].Failure.Should().Be(expected);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"Kod\":\"USD\"}")] // an object instead of the array
    [InlineData("[{\"Alis\":48.82,\"Satis\":49.03,\"Kod\":\"USD\"}]")] // numbers instead of tr-TR strings
    [InlineData("null")]
    public void Malformed_altinkaynak_json_fails_every_instrument(string body)
    {
        var parsed = AltinkaynakParser.ParseCurrency(Bytes(body), Turkey);
        parsed[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.MalformedPayload);
        parsed[MarketInstrument.Eur].Failure.Should().Be(ProviderFailure.MalformedPayload);
    }

    [Fact]
    public void Duplicate_codes_and_unreadable_times_are_schema_changes()
    {
        var duplicate = AltinkaynakCurrency.Replace("\"Kod\":\"CHF\"", "\"Kod\":\"USD\"", StringComparison.Ordinal);
        AltinkaynakParser.ParseCurrency(Bytes(duplicate), Turkey)[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.SchemaChanged);

        var badTime = AltinkaynakCurrency.Replace("28.09.2026 11:51:42", "2026-09-28T11:51:42Z", StringComparison.Ordinal);
        AltinkaynakParser.ParseCurrency(Bytes(badTime), Turkey)[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.SchemaChanged);
    }

    // ---------------------------------------------------------------- Altınkaynak Gold

    [Fact]
    public void Gram_gold_is_ga_never_pga()
    {
        var gold = AltinkaynakParser.ParseGold(Bytes(AltinkaynakGold), Turkey)[MarketInstrument.GramGold].Price!;
        gold.Buy.Should().Be(6439.47m);
        gold.Sell.Should().Be(6574.16m, "GA's sell price — PGA (same description) sells at 6630.00");
        gold.Sell.Should().NotBe(6630.00m);
        gold.SourceTimestamp.Should().Be(TurkeyTime(2026, 9, 28, 11, 51, 42));
    }

    [Fact]
    public void Without_ga_there_is_no_gram_gold_even_though_pga_is_there()
    {
        var withoutGa = AltinkaynakGold.Replace("\"Kod\":\"GA\"", "\"Kod\":\"GA_OLD\"", StringComparison.Ordinal);
        AltinkaynakParser.ParseGold(Bytes(withoutGa), Turkey)[MarketInstrument.GramGold].Failure.Should().Be(ProviderFailure.InstrumentMissing);
    }

    [Theory]
    [InlineData("Gram Altın ", true)]
    [InlineData("  Gram   Altın", true)]
    [InlineData("Gram Altın", true)]
    [InlineData("Çeyrek", false)]
    [InlineData("Gram Gümüş", false)]
    public void Ga_must_still_mean_gram_gold(string description, bool accepted)
    {
        var body = AltinkaynakGold.Replace("\"Aciklama\":\"Gram Altın \"", "\"Aciklama\":\"" + description + "\"", StringComparison.Ordinal);
        var parsed = AltinkaynakParser.ParseGold(Bytes(body), Turkey)[MarketInstrument.GramGold];
        if (accepted)
            parsed.Price!.Sell.Should().Be(6574.16m);
        else
            parsed.Failure.Should().Be(ProviderFailure.SchemaChanged, "GA meaning something else is never used silently");
    }

    [Fact]
    public void Malformed_gold_json_fails()
    {
        AltinkaynakParser.ParseGold(Bytes("[{"), Turkey)[MarketInstrument.GramGold].Failure.Should().Be(ProviderFailure.MalformedPayload);
        AltinkaynakParser.ParseGold(Bytes("[]"), Turkey)[MarketInstrument.GramGold].Failure.Should().Be(ProviderFailure.InstrumentMissing);
    }

    // ---------------------------------------------------------------- TCMB

    [Fact]
    public void Tcmb_reads_forex_buying_and_selling_as_a_date_only_bulletin()
    {
        var parsed = TcmbParser.Parse(Bytes(Tcmb), Turkey);
        var usd = parsed[MarketInstrument.Usd].Price!;
        usd.Buy.Should().Be(48.7901m, "ForexBuying");
        usd.Sell.Should().Be(48.8780m, "ForexSelling");
        usd.SourceDateOnly.Should().BeTrue("the bulletin has a date, not a time");
        usd.SourceTimestamp.Should().Be(TurkeyTime(2026, 9, 25, 0, 0, 0));

        var eur = parsed[MarketInstrument.Eur].Price!;
        eur.Buy.Should().Be(55.5878m);
        eur.Sell.Should().Be(55.6880m);
    }

    [Fact]
    public void Tcmb_failures_are_categorized()
    {
        TcmbParser.Parse(Bytes("<Tarih_Date Tarih=\"25.09.2026\"><Currency"), Turkey)[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.MalformedPayload);
        TcmbParser.Parse(Bytes("<html><body>maintenance</body></html>"), Turkey)[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.SchemaChanged);
        TcmbParser.Parse(Bytes(Tcmb.Replace("Tarih=\"25.09.2026\"", "Tarih=\"2026-09-25\"", StringComparison.Ordinal)), Turkey)[MarketInstrument.Eur].Failure
            .Should().Be(ProviderFailure.SchemaChanged);

        var noUsd = Tcmb.Replace("CurrencyCode=\"USD\"", "CurrencyCode=\"XXX\"", StringComparison.Ordinal);
        var parsed = TcmbParser.Parse(Bytes(noUsd), Turkey);
        parsed[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.InstrumentMissing);
        parsed[MarketInstrument.Eur].Price.Should().NotBeNull();

        var badNumber = Tcmb.Replace("<ForexBuying>48.7901</ForexBuying>", "<ForexBuying>48,7901</ForexBuying>", StringComparison.Ordinal);
        TcmbParser.Parse(Bytes(badNumber), Turkey)[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.InvalidPrice);

        var zero = Tcmb.Replace("<ForexBuying>55.5878</ForexBuying>", "<ForexBuying>0</ForexBuying>", StringComparison.Ordinal);
        TcmbParser.Parse(Bytes(zero), Turkey)[MarketInstrument.Eur].Failure.Should().Be(ProviderFailure.InvalidPrice);

        var per100 = Tcmb.Replace("<Unit>1</Unit><Isim>ABD DOLARI", "<Unit>100</Unit><Isim>ABD DOLARI", StringComparison.Ordinal);
        TcmbParser.Parse(Bytes(per100), Turkey)[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.SchemaChanged);
    }

    [Fact]
    public void Tcmb_xml_is_parsed_without_dtds_or_external_entities()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE Tarih_Date [ <!ENTITY x SYSTEM "file:///etc/passwd"> ]>
            <Tarih_Date Tarih="25.09.2026"><Currency CurrencyCode="USD"><Unit>1</Unit><ForexBuying>&x;</ForexBuying><ForexSelling>1.0</ForexSelling></Currency></Tarih_Date>
            """;
        TcmbParser.Parse(Bytes(xxe), Turkey)[MarketInstrument.Usd].Failure.Should().Be(ProviderFailure.MalformedPayload);
    }

    // ---------------------------------------------------------------- Trunçgil

    [Fact]
    public void Truncgil_gra_is_read_as_decimal_with_the_global_update_date()
    {
        var gold = TruncgilParser.Parse(Bytes(Truncgil), Turkey)[MarketInstrument.GramGold].Price!;
        gold.Buy.Should().Be(6539.59m);
        gold.Sell.Should().Be(6540.36m);
        gold.SourceTimestamp.Should().Be(TurkeyTime(2026, 9, 28, 11, 51, 1));
    }

    [Theory]
    [InlineData("\"GRA\":", "\"GRX\":", ProviderFailure.InstrumentMissing)]
    [InlineData("\"Name\":\"GRAMALTIN\"", "\"Name\":\"CEYREKALTIN\"", ProviderFailure.SchemaChanged)]
    [InlineData("\"Type\":\"Gold\",\"Name\":\"GRAMALTIN\"", "\"Type\":\"Currency\",\"Name\":\"GRAMALTIN\"", ProviderFailure.SchemaChanged)]
    [InlineData("\"Buying\":6539.59", "\"Buying\":0", ProviderFailure.InvalidPrice)]
    [InlineData("\"Buying\":6539.59", "\"Buying\":-6539.59", ProviderFailure.InvalidPrice)]
    [InlineData("\"Buying\":6539.59", "\"Buying\":null", ProviderFailure.InvalidPrice)]
    [InlineData("\"Buying\":6539.59", "\"Buying\":\"6539,59\"", ProviderFailure.MalformedPayload)]
    [InlineData("\"Update_Date\":\"2026-09-28 11:51:01\"", "\"Update_Date\":\"28.09.2026\"", ProviderFailure.SchemaChanged)]
    [InlineData("{\"Update_Date\"", "[{\"Update_Date\"", ProviderFailure.MalformedPayload)]
    public void Truncgil_failures_are_categorized(string find, string replace, ProviderFailure expected)
    {
        var body = Truncgil.Replace(find, replace, StringComparison.Ordinal);
        body.Should().NotBe(Truncgil);
        TruncgilParser.Parse(Bytes(body), Turkey)[MarketInstrument.GramGold].Failure.Should().Be(expected);
    }
}
