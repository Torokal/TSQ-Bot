using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// One loop for all predictions (no timer per prediction), about every <see cref="Interval"/>: locks every open prediction
/// whose lock time has come — on the first pass after a restart or deploy that includes everything that passed while the
/// bot was down —, looks for cards whose post was uncertain, and edits the cards still marked stale (which also coalesces
/// bursts of entries into one edit). It removes settled/cancelled cards whose retention has passed and stages the weekly
/// leaderboard when its slot is due (both decided from the database, so a restart loses nothing). Now and then it checks
/// that open cards still exist and prunes delivered announcements. Entry safety never depends on this loop: every entry compares the current time with the lock time
/// inside its own transaction. Locking runs even while the module is disabled in a guild (a promised deadline is kept);
/// it changes no coins.
/// </summary>
public sealed class PredictionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PredictionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>Lets the Discord gateway log in before the first pass edits a card.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(10);

    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var predictions = scope.ServiceProvider.GetRequiredService<PredictionService>();
        await predictions.LockDueAsync(cancellationToken);
        await predictions.ReconcilePublishingAsync(cancellationToken);
        var cardSync = scope.ServiceProvider.GetRequiredService<PredictionCardSync>();
        await cardSync.SyncStaleAsync(cancellationToken);
        await cardSync.RemoveExpiredCardsAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<PredictionEconomy>().PublishWeeklyLeaderboardAsync(cancellationToken);

        var now = clock.GetUtcNow();
        if (now - _lastMaintenance >= MaintenanceInterval)
        {
            _lastMaintenance = now;
            await predictions.CheckOpenCardsAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<PredictionEconomy>().PruneAnnouncementsAsync(cancellationToken);
        }

        // A card that is definitely gone gets a replacement (management lives on the card).
        await predictions.RepostMissingCardsAsync(cancellationToken);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Prediction lock/card pass failed; retrying in {Interval}", Interval);
            }

            try
            {
                await Task.Delay(Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
