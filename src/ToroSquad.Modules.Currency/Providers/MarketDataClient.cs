using System.Net;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Providers;

/// <summary>Fetches one dataset. Never throws for provider problems: every failure comes back as a <see cref="DatasetResult"/>.</summary>
public interface IMarketDataSource
{
    Task<DatasetResult> FetchAsync(MarketDataset dataset, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only GETs of three fixed public endpoints through named <see cref="IHttpClientFactory"/> clients (base URLs from
/// configuration, never from users). Bounded in time (<see cref="CurrencyOptions.TimeoutSeconds"/>, on the injected clock)
/// and size (the clients' response buffer limit). Timeouts, network errors, non-success statuses, empty or unparsable
/// bodies and missing or invalid prices all become a <see cref="ProviderFailure"/>; the caller moves on to the next provider.
/// </summary>
public sealed class MarketDataClient : IMarketDataSource
{
    public const string AltinkaynakHttpClient = "currency-altinkaynak";
    public const string TcmbHttpClient = "currency-tcmb";
    public const string TruncgilHttpClient = "currency-truncgil";

    /// <summary>The largest response accepted (today: 2-10 KB each).</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    private readonly IHttpClientFactory _http;
    private readonly IOptions<CurrencyOptions> _options;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _turkey;

    public MarketDataClient(IHttpClientFactory http, IOptions<CurrencyOptions> options, TimeProvider clock)
    {
        _http = http;
        _options = options;
        _clock = clock;
        _turkey = ProviderFormats.TryTurkeyZone(out var zone)
            ? zone
            : throw new InvalidOperationException($"Time zone {ProviderFormats.TurkeyTimeZoneId} is not available on this host.");
    }

    public static (string Client, string Path) Endpoint(MarketDataset dataset) => dataset switch
    {
        MarketDataset.AltinkaynakCurrency => (AltinkaynakHttpClient, "Currency"),
        MarketDataset.AltinkaynakGold => (AltinkaynakHttpClient, "Gold"),
        MarketDataset.Tcmb => (TcmbHttpClient, "today.xml"),
        MarketDataset.Truncgil => (TruncgilHttpClient, "today.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null),
    };

    public async Task<DatasetResult> FetchAsync(MarketDataset dataset, CancellationToken cancellationToken)
    {
        var (client, path) = Endpoint(dataset);
        var at = _clock.GetUtcNow();
        using var deadline = new CancellationTokenSource(_options.Value.Timeout, _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        byte[] body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            using var response = await _http.CreateClient(client).SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                return DatasetResult.Failed(dataset, ProviderFailure.HttpStatus, at, (int)response.StatusCode);
            body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DatasetResult.Failed(dataset, ProviderFailure.Timeout, at);
        }
        catch (HttpRequestException ex)
        {
            // DNS, connect, TLS, reset, or a body over the client's buffer limit.
            return DatasetResult.Failed(dataset, ProviderFailure.Network, at, ex.StatusCode is { } status ? (int)status : null);
        }

        if (body.AsSpan().Trim(" \t\r\n"u8).IsEmpty)
            return DatasetResult.Failed(dataset, ProviderFailure.EmptyResponse, at, 200);

        var parsed = dataset switch
        {
            MarketDataset.AltinkaynakCurrency => AltinkaynakParser.ParseCurrency(body, _turkey),
            MarketDataset.AltinkaynakGold => AltinkaynakParser.ParseGold(body, _turkey),
            MarketDataset.Tcmb => TcmbParser.Parse(body, _turkey),
            MarketDataset.Truncgil => TruncgilParser.Parse(body, _turkey),
            _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null),
        };
        return DatasetResult.From(dataset, parsed, at) with { HttpStatus = 200 };
    }
}
