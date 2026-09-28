using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Modules.Currency.Providers;

namespace ToroSquad.Modules.Currency.Application;

/// <summary>How the last command was answered (for /bot status; no provider call behind it).</summary>
public enum QuoteOutcome
{
    Primary = 0,
    Fallback = 1,
    Stale = 2,
    Unavailable = 3,
}

public sealed record QuoteStatus(MarketInstrument Instrument, QuoteOutcome Outcome, MarketSource? Source, DateTimeOffset At);

/// <summary>
/// Command → quote. Walks the instrument's provider chain (<see cref="MarketDatasets.Chain"/>) over an in-memory cache of
/// whole provider responses, then the last good price, then gives up with a trace code:
/// <list type="bullet">
/// <item>Cache per dataset, not per command: one Altınkaynak Currency response answers /dolar and /euro.</item>
/// <item>A clean primary response is reused for <see cref="CurrencyOptions.FreshSeconds"/>; a fallback response, a partial
/// one and a failure for <see cref="CurrencyOptions.FallbackFreshSeconds"/> (so a failed provider is skipped at once, and
/// the primary is asked again soon).</item>
/// <item>Single flight: while a dataset is being fetched, every other request for it awaits the same fetch.</item>
/// <item>Every served price becomes the instrument's last-known-good; it is shown — marked stale — only when all providers
/// fail and it was received at most <see cref="CurrencyOptions.StaleMaxMinutes"/> ago.</item>
/// </list>
/// Nothing runs in the background and nothing is stored: without commands there is no network traffic. A cache hit
/// completes synchronously (the command can answer without deferring).
/// </summary>
public sealed partial class MarketQuoteService(IMarketDataSource source, IOptions<CurrencyOptions> options, TimeProvider clock, ILogger<MarketQuoteService> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<MarketDataset, CacheEntry> _cache = [];
    private readonly Dictionary<MarketDataset, Task<DatasetResult>> _inflight = [];
    private readonly Dictionary<MarketInstrument, MarketQuote> _lastGood = [];
    private QuoteStatus? _lastStatus;

    public QuoteStatus? LastStatus
    {
        get
        {
            lock (_gate)
                return _lastStatus;
        }
    }

    public async Task<MarketQuoteResult> GetQuoteAsync(MarketInstrument instrument, CancellationToken cancellationToken)
    {
        var chain = MarketDatasets.Chain(instrument);
        for (var i = 0; i < chain.Count; i++)
        {
            var result = await LoadAsync(chain[i], instrument).WaitAsync(cancellationToken);
            if (result.Prices.TryGetValue(instrument, out var price))
            {
                var quote = new MarketQuote(instrument, price, MarketDatasets.Source(chain[i]), IsFallback: i > 0, IsStale: false, result.RetrievedAt);
                lock (_gate)
                    _lastGood[instrument] = quote;
                Record(instrument, i == 0 ? QuoteOutcome.Primary : QuoteOutcome.Fallback, quote.Source);
                return MarketQuoteResult.Ok(quote);
            }

            LogProviderSkipped(logger, chain[i], instrument, result.FailureFor(instrument));
        }

        var now = clock.GetUtcNow();
        MarketQuote? last;
        lock (_gate)
            _lastGood.TryGetValue(instrument, out last);
        if (last is not null && now - last.RetrievedAt <= options.Value.StaleMax)
        {
            LogServingStale(logger, instrument, last.Source, (int)(now - last.RetrievedAt).TotalSeconds);
            Record(instrument, QuoteOutcome.Stale, last.Source);
            return MarketQuoteResult.Ok(last with { IsStale = true });
        }

        var trace = TraceCodes.New();
        LogUnavailable(logger, trace, instrument, last is null ? "none" : $"{(int)(now - last.RetrievedAt).TotalMinutes} min old");
        Record(instrument, QuoteOutcome.Unavailable, null);
        return MarketQuoteResult.Unavailable(trace);
    }

    /// <summary>The cached response while it is fresh, the running fetch if there is one, or a new fetch.</summary>
    private Task<DatasetResult> LoadAsync(MarketDataset dataset, MarketInstrument requestedBy)
    {
        TaskCompletionSource<DatasetResult> fetch;
        lock (_gate)
        {
            if (_cache.TryGetValue(dataset, out var entry) && clock.GetUtcNow() < entry.ExpiresAt)
                return Task.FromResult(entry.Result);
            if (_inflight.TryGetValue(dataset, out var running))
                return running;
            fetch = new TaskCompletionSource<DatasetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflight[dataset] = fetch.Task;
        }

        _ = FetchAsync(dataset, requestedBy, fetch);
        return fetch.Task;
    }

    private async Task FetchAsync(MarketDataset dataset, MarketInstrument requestedBy, TaskCompletionSource<DatasetResult> fetch)
    {
        DatasetResult? result = null;
        try
        {
            // Not the caller's token: other requests share this fetch. The client bounds it with its own timeout.
            result = await source.FetchAsync(dataset, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var trace = TraceCodes.New();
            LogFetchCrashed(logger, trace, dataset, ex.GetType().FullName ?? "?");
            result = DatasetResult.Failed(dataset, ProviderFailure.Unexpected, clock.GetUtcNow());
        }
        finally
        {
            result ??= DatasetResult.Failed(dataset, ProviderFailure.Unexpected, clock.GetUtcNow());
            var clean = result.Failures.Count == 0 && MarketDatasets.IsPrimary(dataset);
            lock (_gate)
            {
                _cache[dataset] = new CacheEntry(result, clock.GetUtcNow() + (clean ? options.Value.Fresh : options.Value.FallbackFresh));
                _inflight.Remove(dataset);
            }

            fetch.TrySetResult(result);
        }

        if (result.Failures.Count > 0)
        {
            var failures = string.Join(", ", result.Failures.Select(f => $"{f.Key}={f.Value}"));
            LogProviderFailed(logger, MarketDatasets.Source(dataset), dataset, requestedBy, failures, result.HttpStatus, MarketDatasets.Next(dataset));
        }
    }

    private void Record(MarketInstrument instrument, QuoteOutcome outcome, MarketSource? used)
    {
        var status = new QuoteStatus(instrument, outcome, used, clock.GetUtcNow());
        lock (_gate)
            _lastStatus = status;
    }

    private sealed record CacheEntry(DatasetResult Result, DateTimeOffset ExpiresAt);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Currency provider {Provider} ({Dataset}) failed: {Failures}; http={HttpStatus} requested={Instrument} fallback={Fallback}")]
    private static partial void LogProviderFailed(ILogger logger, MarketSource provider, MarketDataset dataset, MarketInstrument instrument,
        string failures, int? httpStatus, string fallback);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Currency: {Dataset} has no {Instrument} ({Failure}); trying the next source")]
    private static partial void LogProviderSkipped(ILogger logger, MarketDataset dataset, MarketInstrument instrument, ProviderFailure failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Currency: every provider failed for {Instrument}; serving the last good {Source} price ({AgeSeconds}s old) as stale")]
    private static partial void LogServingStale(ILogger logger, MarketInstrument instrument, MarketSource source, int ageSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Currency quote unavailable [{TraceCode}] {Instrument}: every provider failed; last good price: {LastGood}")]
    private static partial void LogUnavailable(ILogger logger, string traceCode, MarketInstrument instrument, string lastGood);

    [LoggerMessage(Level = LogLevel.Error, Message = "Currency fetch crashed [{TraceCode}] {Dataset}: {ExceptionType}")]
    private static partial void LogFetchCrashed(ILogger logger, string traceCode, MarketDataset dataset, string exceptionType);
}
