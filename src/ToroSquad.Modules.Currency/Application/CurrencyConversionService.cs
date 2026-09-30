using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Application;

/// <summary>
/// /çevir arithmetic, on normalized quotes only (no provider types), in decimal, without early rounding. V1 converts
/// between Turkish lira and one of USD, EUR or gram gold — never across (USD → EUR etc. is refused). The side is the one a
/// dealer would actually apply:
/// <list type="bullet">
/// <item>asset → lira (the user sells the asset): the provider's <b>buy</b> price, lira = amount × Buy;</item>
/// <item>lira → asset (the user buys the asset): the provider's <b>sell</b> price, asset = amount ÷ Sell.</item>
/// </list>
/// </summary>
public static class CurrencyConversionService
{
    /// <summary>Largest amount accepted (Discord enforces the same bound on the option).</summary>
    public const decimal MaxAmount = 1_000_000_000m;

    /// <summary>Stable choice values of the <c>kaynak</c> / <c>hedef</c> options.</summary>
    public static readonly IReadOnlyDictionary<string, ConvertibleAsset> ChoiceValues = new Dictionary<string, ConvertibleAsset>(StringComparer.Ordinal)
    {
        ["TRY"] = ConvertibleAsset.Try,
        ["USD"] = ConvertibleAsset.Usd,
        ["EUR"] = ConvertibleAsset.Eur,
        ["GRAM_GOLD"] = ConvertibleAsset.GramGold,
    };

    /// <summary>Checks everything that needs no price: amount bounds, same asset, supported pair. Refusals fetch nothing.</summary>
    public static ConversionRefusal Validate(decimal amount, ConvertibleAsset from, ConvertibleAsset to, out ConversionRequest? request)
    {
        request = null;
        if (amount <= 0)
            return ConversionRefusal.AmountNotPositive;
        if (amount > MaxAmount)
            return ConversionRefusal.AmountTooLarge;
        if (from == to)
            return ConversionRefusal.SameAsset;

        if (from == ConvertibleAsset.Try && Instrument(to) is { } bought)
            request = new ConversionRequest(amount, from, to, bought, RateSide.Sell);
        else if (to == ConvertibleAsset.Try && Instrument(from) is { } sold)
            request = new ConversionRequest(amount, from, to, sold, RateSide.Buy);
        return request is null ? ConversionRefusal.UnsupportedPair : ConversionRefusal.None;
    }

    /// <summary>The conversion at full decimal precision with the side the request names.</summary>
    public static ConversionResult Convert(ConversionRequest request, MarketQuote quote)
    {
        if (quote.Instrument != request.Instrument)
            throw new ArgumentException($"Quote for {quote.Instrument}, conversion needs {request.Instrument}.", nameof(quote));

        var rate = request.Side == RateSide.Buy ? quote.Buy : quote.Sell;
        var result = request.FromLira ? request.Amount / rate : request.Amount * rate;
        return new ConversionResult(request, result, rate, quote);
    }

    public static MarketInstrument? Instrument(ConvertibleAsset asset) => asset switch
    {
        ConvertibleAsset.Usd => MarketInstrument.Usd,
        ConvertibleAsset.Eur => MarketInstrument.Eur,
        ConvertibleAsset.GramGold => MarketInstrument.GramGold,
        _ => null,
    };
}
