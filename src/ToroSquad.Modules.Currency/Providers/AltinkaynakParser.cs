using System.Text.Json;
using System.Text.Json.Serialization;
using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Providers;

/// <summary>
/// Altınkaynak public web service (static.altinkaynak.com/public/Currency and /Gold): a JSON array of
/// {"Alis","Satis","Kod","Aciklama","GuncellenmeZamani","IsMobile"} with tr-TR number strings and Türkiye local times
/// (contract checked 2026-09-28). Selection is by code only, and the code must be unique: USD, EUR, and for gram gold
/// explicitly "GA" whose description is "Gram Altın" — the Gold list also has "PGA" with the same description and a
/// different price, which is never taken.
/// </summary>
public static class AltinkaynakParser
{
    public const string TimestampFormat = "dd.MM.yyyy HH:mm:ss";
    public const string GramGoldCode = "GA";
    public const string GramGoldDescription = "Gram Altın";

    public static IReadOnlyDictionary<MarketInstrument, ParsedPrice> ParseCurrency(byte[] body, TimeZoneInfo turkey)
    {
        if (Deserialize(body) is not { } items)
            return All(MarketDataset.AltinkaynakCurrency, ProviderFailure.MalformedPayload);
        return new Dictionary<MarketInstrument, ParsedPrice>
        {
            [MarketInstrument.Usd] = Select(items, "USD", null, turkey),
            [MarketInstrument.Eur] = Select(items, "EUR", null, turkey),
        };
    }

    public static IReadOnlyDictionary<MarketInstrument, ParsedPrice> ParseGold(byte[] body, TimeZoneInfo turkey)
    {
        if (Deserialize(body) is not { } items)
            return All(MarketDataset.AltinkaynakGold, ProviderFailure.MalformedPayload);
        return new Dictionary<MarketInstrument, ParsedPrice>
        {
            [MarketInstrument.GramGold] = Select(items, GramGoldCode, GramGoldDescription, turkey),
        };
    }

    private static ParsedPrice Select(IReadOnlyList<Item?> items, string code, string? expectedDescription, TimeZoneInfo turkey)
    {
        var matches = items.Where(i => i is not null && string.Equals(i.Kod?.Trim(), code, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
            return ParsedPrice.Fail(ProviderFailure.InstrumentMissing);
        if (matches.Count > 1)
            return ParsedPrice.Fail(ProviderFailure.SchemaChanged); // which one would be meant is unknowable

        var item = matches[0]!;
        if (expectedDescription is not null && ProviderFormats.NormalizeSpaces(item.Aciklama) != expectedDescription)
            return ParsedPrice.Fail(ProviderFailure.SchemaChanged); // the code now means something else: never guessed
        if (!ProviderFormats.TryParseTurkishDecimal(item.Alis, out var buy) || !ProviderFormats.TryParseTurkishDecimal(item.Satis, out var sell))
            return ParsedPrice.Fail(ProviderFailure.InvalidPrice);
        if (!ProviderFormats.TryParseTurkeyLocal(item.GuncellenmeZamani, TimestampFormat, turkey, out var updated))
            return ParsedPrice.Fail(ProviderFailure.SchemaChanged);
        return ParsedPrice.Validated(buy, sell, updated);
    }

    private static List<Item?>? Deserialize(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Item?>>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<MarketInstrument, ParsedPrice> All(MarketDataset dataset, ProviderFailure failure) =>
        MarketDatasets.Covers(dataset).ToDictionary(i => i, _ => ParsedPrice.Fail(failure));

    /// <summary>One row of the response. Strings on purpose: the numbers are tr-TR text, parsed explicitly.</summary>
    internal sealed class Item
    {
        [JsonPropertyName("Alis")]
        public string? Alis { get; init; }

        [JsonPropertyName("Satis")]
        public string? Satis { get; init; }

        [JsonPropertyName("Kod")]
        public string? Kod { get; init; }

        [JsonPropertyName("Aciklama")]
        public string? Aciklama { get; init; }

        [JsonPropertyName("GuncellenmeZamani")]
        public string? GuncellenmeZamani { get; init; }
    }
}
