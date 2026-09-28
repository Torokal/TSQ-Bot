using System.Text.Json;
using System.Text.Json.Serialization;
using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Providers;

/// <summary>
/// Trunçgil Finance v4 (finans.truncgil.com/v4/today.json): one object with a global "Update_Date" (Türkiye local
/// "yyyy-MM-dd HH:mm:ss") and one entry per instrument; gram gold is "GRA" with Type "Gold", Name "GRAMALTIN" and JSON
/// numbers Buying/Selling (contract checked 2026-09-28). Both the key and the instrument type/name must match. Numbers are
/// read straight into decimal (never through a binary floating-point value).
/// </summary>
public static class TruncgilParser
{
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    public const string GramGoldType = "Gold";
    public const string GramGoldName = "GRAMALTIN";

    public static IReadOnlyDictionary<MarketInstrument, ParsedPrice> Parse(byte[] body, TimeZoneInfo turkey)
    {
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(body);
        }
        catch (JsonException)
        {
            payload = null;
        }

        return new Dictionary<MarketInstrument, ParsedPrice> { [MarketInstrument.GramGold] = GramGold(payload, turkey) };
    }

    private static ParsedPrice GramGold(Payload? payload, TimeZoneInfo turkey)
    {
        if (payload is null)
            return ParsedPrice.Fail(ProviderFailure.MalformedPayload);
        if (payload.GramGold is not { } gold)
            return ParsedPrice.Fail(ProviderFailure.InstrumentMissing);
        if (gold.Type != GramGoldType || gold.Name != GramGoldName)
            return ParsedPrice.Fail(ProviderFailure.SchemaChanged);
        if (gold.Buying is not { } buy || gold.Selling is not { } sell)
            return ParsedPrice.Fail(ProviderFailure.InvalidPrice);
        if (!ProviderFormats.TryParseTurkeyLocal(payload.UpdateDate, TimestampFormat, turkey, out var updated))
            return ParsedPrice.Fail(ProviderFailure.SchemaChanged);
        return ParsedPrice.Validated(buy, sell, updated);
    }

    internal sealed class Payload
    {
        [JsonPropertyName("Update_Date")]
        public string? UpdateDate { get; init; }

        [JsonPropertyName("GRA")]
        public Item? GramGold { get; init; }
    }

    internal sealed class Item
    {
        [JsonPropertyName("Buying")]
        public decimal? Buying { get; init; }

        [JsonPropertyName("Selling")]
        public decimal? Selling { get; init; }

        [JsonPropertyName("Type")]
        public string? Type { get; init; }

        [JsonPropertyName("Name")]
        public string? Name { get; init; }
    }
}
