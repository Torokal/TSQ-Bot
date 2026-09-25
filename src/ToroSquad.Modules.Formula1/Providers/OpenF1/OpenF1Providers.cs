using System.Text.Json;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Modules.Formula1.Providers.OpenF1;

/// <summary>
/// OpenF1 session lifecycle over REST (reconciliation / catch-up). Live access requires OpenF1 credentials; without them
/// the provider is NOT_CONFIGURED and no lifecycle is ever reported (nothing is derived from the schedule instead).
/// The push channel is <see cref="OpenF1LiveClient"/>.
/// </summary>
public sealed class OpenF1LifecycleProvider(OpenF1Client client, OpenF1TokenProvider tokens, F1DataMode mode, TimeProvider clock) : IF1LifecycleProvider
{
    public string Id => OpenF1Parser.Source;
    public string AttributionKey => "f1.source.openf1";

    public bool IsConfigured => mode.IsDemo || tokens.IsConfigured;

    public Task<F1ProviderResult<IReadOnlyList<F1ProviderSession>>> GetSessionsAsync(int season, CancellationToken cancellationToken) =>
        client.GetSessionsAsync(season, cancellationToken);

    public async Task<F1ProviderResult<IReadOnlyList<F1LifecycleEvent>>> GetLifecycleEventsAsync(string providerSessionRef, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return F1ProviderResult<IReadOnlyList<F1LifecycleEvent>>.Fail(F1ProviderOutcome.NotConfigured, "OpenF1 live credentials not configured", clock.GetUtcNow());
        return await client.GetLifecycleEventsAsync(providerSessionRef, cancellationToken);
    }
}

/// <summary>
/// OpenF1 session classification (session_result joined with drivers). Works without credentials once a session's
/// data is historical (after OpenF1's live window); with credentials it can be read earlier.
/// </summary>
public sealed class OpenF1ResultsProvider(OpenF1Client client, OpenF1TokenProvider tokens, IOptions<OpenF1Options> options, F1DataMode mode) : IF1ResultsProvider
{
    public string Id => OpenF1Parser.Source;
    public string AttributionKey => "f1.source.openf1";
    public bool IsConfigured => true;

    public TimeSpan AvailabilityDelay => mode.IsDemo || tokens.IsConfigured
        ? TimeSpan.Zero
        : TimeSpan.FromMinutes(options.Value.LiveWindowAfterEndMinutes + 5);

    public Task<F1ProviderResult<IReadOnlyList<F1ProviderSession>>> GetSessionsAsync(int season, CancellationToken cancellationToken) =>
        client.GetSessionsAsync(season, cancellationToken);

    public async Task<F1ProviderResult<F1SessionResult>> GetResultAsync(F1Session session, string providerSessionRef, CancellationToken cancellationToken)
    {
        var rows = await client.GetSessionResultAsync(providerSessionRef, cancellationToken);
        if (!rows.Succeeded)
            return rows.WithoutValue<F1SessionResult>();
        if (rows.Value is null)
            return F1ProviderResult<F1SessionResult>.Ok(null, rows.At); // not published yet

        using var resultDoc = rows.Value;
        var drivers = await client.GetDriversAsync(providerSessionRef, cancellationToken);
        if (!drivers.Succeeded)
            return drivers.WithoutValue<F1SessionResult>(); // names unknown: retry later rather than publish numbers only
        using var driversDoc = drivers.Value;
        try
        {
            return F1ProviderResult<F1SessionResult>.Ok(OpenF1Parser.ParseSessionResult(resultDoc.RootElement, driversDoc?.RootElement, session), rows.At);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return F1ProviderResult<F1SessionResult>.Fail(F1ProviderOutcome.SchemaError, "unexpected payload: " + ex.Message, rows.At);
        }
    }
}
