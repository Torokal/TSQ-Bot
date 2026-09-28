using System.Xml;
using System.Xml.Linq;
using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Providers;

/// <summary>
/// TCMB daily indicative exchange rates (www.tcmb.gov.tr/kurlar/today.xml): &lt;Tarih_Date Tarih="dd.MM.yyyy"&gt; with one
/// &lt;Currency CurrencyCode="USD"&gt; per currency and invariant dot-decimal ForexBuying/ForexSelling (contract checked
/// 2026-09-28). The bulletin carries a date, not a time, so the price is marked date-only. Parsed without DTDs or external
/// resolution.
/// </summary>
public static class TcmbParser
{
    public const string DateFormat = "dd.MM.yyyy";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = 1_000_000,
    };

    public static IReadOnlyDictionary<MarketInstrument, ParsedPrice> Parse(byte[] body, TimeZoneInfo turkey)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, Settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return All(ProviderFailure.MalformedPayload);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Tarih_Date" ||
            !ProviderFormats.TryParseTurkeyLocal((string?)root.Attribute("Tarih"), DateFormat, turkey, out var bulletinDay))
            return All(ProviderFailure.SchemaChanged);

        return new Dictionary<MarketInstrument, ParsedPrice>
        {
            [MarketInstrument.Usd] = Select(root, "USD", bulletinDay),
            [MarketInstrument.Eur] = Select(root, "EUR", bulletinDay),
        };
    }

    private static ParsedPrice Select(XElement root, string code, DateTimeOffset bulletinDay)
    {
        var matches = root.Elements("Currency").Where(e => string.Equals(((string?)e.Attribute("CurrencyCode"))?.Trim(), code, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
            return ParsedPrice.Fail(ProviderFailure.InstrumentMissing);
        if (matches.Count > 1)
            return ParsedPrice.Fail(ProviderFailure.SchemaChanged);

        var currency = matches[0];
        if (((string?)currency.Element("Unit"))?.Trim() != "1")
            return ParsedPrice.Fail(ProviderFailure.SchemaChanged); // a price per 100 units would be off by a factor
        if (!ProviderFormats.TryParseInvariantDecimal((string?)currency.Element("ForexBuying"), out var buy) ||
            !ProviderFormats.TryParseInvariantDecimal((string?)currency.Element("ForexSelling"), out var sell))
            return ParsedPrice.Fail(ProviderFailure.InvalidPrice);
        return ParsedPrice.Validated(buy, sell, bulletinDay, dateOnly: true);
    }

    private static Dictionary<MarketInstrument, ParsedPrice> All(ProviderFailure failure) =>
        MarketDatasets.Covers(MarketDataset.Tcmb).ToDictionary(i => i, _ => ParsedPrice.Fail(failure));
}
