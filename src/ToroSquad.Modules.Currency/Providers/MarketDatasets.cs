using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Providers;

/// <summary>
/// One provider response that the cache keeps as a unit: a single Altınkaynak Currency response answers both /dolar and
/// /euro, so the cache is keyed by dataset, not by command.
/// </summary>
public enum MarketDataset
{
    AltinkaynakCurrency = 0,
    AltinkaynakGold = 1,
    Tcmb = 2,
    Truncgil = 3,
}

/// <summary>Why a provider gave no usable price. Every one of them means "try the next provider".</summary>
public enum ProviderFailure
{
    Timeout = 0,
    Network = 1,
    HttpStatus = 2,
    EmptyResponse = 3,
    MalformedPayload = 4,
    InstrumentMissing = 5,
    InvalidPrice = 6,
    SchemaChanged = 7,
    Unexpected = 8,
}

public static class MarketDatasets
{
    /// <summary>Provider order per instrument: primary first, then fallbacks. The stale last-known-good comes after all of them.</summary>
    public static IReadOnlyList<MarketDataset> Chain(MarketInstrument instrument) => instrument switch
    {
        MarketInstrument.Usd or MarketInstrument.Eur => [MarketDataset.AltinkaynakCurrency, MarketDataset.Tcmb],
        MarketInstrument.GramGold => [MarketDataset.AltinkaynakGold, MarketDataset.Truncgil],
        _ => throw new ArgumentOutOfRangeException(nameof(instrument), instrument, null),
    };

    /// <summary>The instruments a dataset is read for (a whole-response failure applies to all of them).</summary>
    public static IReadOnlyList<MarketInstrument> Covers(MarketDataset dataset) => dataset switch
    {
        MarketDataset.AltinkaynakCurrency or MarketDataset.Tcmb => [MarketInstrument.Usd, MarketInstrument.Eur],
        MarketDataset.AltinkaynakGold or MarketDataset.Truncgil => [MarketInstrument.GramGold],
        _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null),
    };

    /// <summary>First in its instruments' chain (its clean answers are cached longest).</summary>
    public static bool IsPrimary(MarketDataset dataset) => Chain(Covers(dataset)[0])[0] == dataset;

    /// <summary>What is tried after this dataset fails (for logs).</summary>
    public static string Next(MarketDataset dataset)
    {
        var chain = Chain(Covers(dataset)[0]);
        var index = chain.ToList().IndexOf(dataset);
        return index + 1 < chain.Count ? chain[index + 1].ToString() : "last-known-good";
    }

    public static MarketSource Source(MarketDataset dataset) => dataset switch
    {
        MarketDataset.AltinkaynakCurrency or MarketDataset.AltinkaynakGold => MarketSource.Altinkaynak,
        MarketDataset.Tcmb => MarketSource.Tcmb,
        MarketDataset.Truncgil => MarketSource.Truncgil,
        _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null),
    };
}

/// <summary>
/// One fetch of one dataset: the prices it yielded and, for every other covered instrument, why not. A dataset can be
/// partly usable (USD valid, EUR missing): EUR then falls back on its own while USD is served.
/// </summary>
public sealed record DatasetResult(
    MarketDataset Dataset,
    IReadOnlyDictionary<MarketInstrument, MarketPrice> Prices,
    IReadOnlyDictionary<MarketInstrument, ProviderFailure> Failures,
    int? HttpStatus,
    DateTimeOffset RetrievedAt)
{
    public bool HasAnyPrice => Prices.Count > 0;

    public static DatasetResult Failed(MarketDataset dataset, ProviderFailure failure, DateTimeOffset at, int? httpStatus = null) =>
        new(dataset, new Dictionary<MarketInstrument, MarketPrice>(),
            MarketDatasets.Covers(dataset).ToDictionary(i => i, _ => failure), httpStatus, at);

    /// <summary>Builds a result from the parser's per-instrument outcomes (every covered instrument gets one).</summary>
    public static DatasetResult From(MarketDataset dataset, IReadOnlyDictionary<MarketInstrument, ParsedPrice> parsed, DateTimeOffset at)
    {
        var prices = new Dictionary<MarketInstrument, MarketPrice>();
        var failures = new Dictionary<MarketInstrument, ProviderFailure>();
        foreach (var instrument in MarketDatasets.Covers(dataset))
        {
            if (parsed.TryGetValue(instrument, out var p) && p.Price is { } price)
                prices[instrument] = price;
            else
                failures[instrument] = p?.Failure ?? ProviderFailure.InstrumentMissing;
        }

        return new DatasetResult(dataset, prices, failures, null, at);
    }

    public ProviderFailure FailureFor(MarketInstrument instrument) =>
        Failures.TryGetValue(instrument, out var failure) ? failure : ProviderFailure.InstrumentMissing;
}

/// <summary>A parser's verdict for one instrument: a validated price or the reason there is none.</summary>
public sealed record ParsedPrice(MarketPrice? Price, ProviderFailure? Failure)
{
    public static ParsedPrice Ok(MarketPrice price) => new(price, null);

    public static ParsedPrice Fail(ProviderFailure failure) => new(null, failure);

    /// <summary>Both sides strictly positive and not crossed (a sell below the buy means swapped or broken fields).</summary>
    public static ParsedPrice Validated(decimal buy, decimal sell, DateTimeOffset timestamp, bool dateOnly = false) =>
        buy <= 0 || sell <= 0 || sell < buy ? Fail(ProviderFailure.InvalidPrice) : Ok(new MarketPrice(buy, sell, timestamp, dateOnly));
}
