using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Birthday.Application;

/// <summary>
/// Runs <see cref="BirthdayReconciler"/>: once shortly after startup (catching up with whatever happened while the bot was
/// down), then every <see cref="BirthdayOptions.ReconciliationIntervalSeconds"/> and always just after local midnight.
/// Missing a tick costs nothing — the next pass reconciles to today's state.
/// </summary>
public sealed class BirthdayWorker(BirthdayReconciler reconciler, BirthdayHealth health, TimeProvider clock, ILogger<BirthdayWorker> logger) : BackgroundService
{
    /// <summary>Lets the Discord gateway log in and receive the guild before the first pass needs members and roles.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        health.WorkerStarted(clock.GetUtcNow());
        try
        {
            await Task.Delay(StartDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var reason = "startup";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await reconciler.RunAsync(reason, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                health.Failed(clock.GetUtcNow(), ex.GetType().Name);
                logger.LogError(ex, "birthday_reconciliation_failed reason={Reason}; retrying on the next pass", reason);
            }

            reason = "periodic";
            try
            {
                await Task.Delay(reconciler.NextDelay(), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
