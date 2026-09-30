namespace ToroSquad.Modules.Currency.Domain;

/// <summary>What /çevir converts between. Turkish lira is the only side that is not a quoted instrument.</summary>
public enum ConvertibleAsset
{
    Try = 0,
    Usd = 1,
    Eur = 2,
    GramGold = 3,
}

/// <summary>Which side of the provider's quote a conversion used.</summary>
public enum RateSide
{
    /// <summary>The provider buys the asset from the user (user gives USD/EUR/gold, receives lira).</summary>
    Buy = 0,

    /// <summary>The provider sells the asset to the user (user gives lira, receives USD/EUR/gold).</summary>
    Sell = 1,
}

public enum ConversionRefusal
{
    None = 0,
    AmountNotPositive = 1,
    AmountTooLarge = 2,
    SameAsset = 3,
    UnsupportedPair = 4,
}

/// <summary>
/// A validated V1 conversion: always lira on one side. <see cref="Instrument"/> is the quote it needs,
/// <see cref="Side"/> the side of that quote.
/// </summary>
public sealed record ConversionRequest(decimal Amount, ConvertibleAsset From, ConvertibleAsset To, MarketInstrument Instrument, RateSide Side)
{
    /// <summary>Lira → asset: the result is divided by the rate; asset → lira: multiplied.</summary>
    public bool FromLira => From == ConvertibleAsset.Try;
}

/// <summary>
/// The full-precision result (nothing rounded here — only the card rounds for display) and the quote behind it, so the
/// card can show the rate, its side, its source and its age.
/// </summary>
public sealed record ConversionResult(ConversionRequest Request, decimal Result, decimal Rate, MarketQuote Quote);
