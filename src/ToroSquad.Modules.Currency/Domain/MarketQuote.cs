namespace ToroSquad.Modules.Currency.Domain;

/// <summary>What a command asks for. A new instrument is one enum value plus its providers' selectors.</summary>
public enum MarketInstrument
{
    Usd = 0,
    Eur = 1,
    GramGold = 2,
}

/// <summary>Where a price came from. The card names the source; TCMB is labelled as an indicative rate, never as live.</summary>
public enum MarketSource
{
    Altinkaynak = 0,

    /// <summary>TCMB daily indicative (gösterge) rates: one bulletin per business day, not a live market price.</summary>
    Tcmb = 1,

    Truncgil = 2,
}

/// <summary>
/// One provider's buy/sell price for one instrument, in Turkish lira. <see cref="SourceTimestamp"/> is the provider's own
/// update time (never the time the bot asked); when the provider only publishes a date (TCMB bulletin),
/// <see cref="SourceDateOnly"/> is set and the timestamp is the start of that day in Türkiye.
/// </summary>
public sealed record MarketPrice(decimal Buy, decimal Sell, DateTimeOffset SourceTimestamp, bool SourceDateOnly = false);

/// <summary>
/// The normalized answer the cards are built from — no provider field names beyond this point.
/// <see cref="IsFallback"/>: not from the primary provider. <see cref="IsStale"/>: every provider failed and this is the last
/// good price, kept for a bounded time; <see cref="RetrievedAt"/> is when the bot last received it from the provider.
/// </summary>
public sealed record MarketQuote(
    MarketInstrument Instrument,
    MarketPrice Price,
    MarketSource Source,
    bool IsFallback,
    bool IsStale,
    DateTimeOffset RetrievedAt)
{
    public decimal Buy => Price.Buy;
    public decimal Sell => Price.Sell;
    public DateTimeOffset SourceTimestamp => Price.SourceTimestamp;
}

/// <summary>A quote, or the trace code of a failure the user can quote to an admin (shown instead of any error detail).</summary>
public sealed record MarketQuoteResult(MarketQuote? Quote, string? TraceCode)
{
    public static MarketQuoteResult Ok(MarketQuote quote) => new(quote, null);

    public static MarketQuoteResult Unavailable(string traceCode) => new(null, traceCode);
}
