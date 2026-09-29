using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Predictions.Application.Automation;

/// <summary>
/// The automatic football opener's own loop, about once a minute (the provider is never called from the 10-second
/// prediction loop; discovery itself runs only every <see cref="AutoFootballOptions.DiscoveryIntervalMinutes"/>). Registered
/// only in the long-running host. Disabled mode makes a pass a database-only no-op; a failing pass is logged and the next
/// one runs normally — the manual predictions never depend on this loop.
/// </summary>
public sealed class AutoFootballWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AutoFootballWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Lets the gateway log in and the prediction worker reconcile first.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(45);

    public async Task<AutoRunSummary> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AutoFootballService>().RunOnceAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("auto_football pass failed: {Error}; the next pass runs as usual", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
