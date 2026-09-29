using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Giveaway.Application;

/// <summary>
/// One loop for all giveaways (no timer per giveaway): about every 30 seconds it draws every active giveaway whose time is up
/// — on the first pass after a restart or deploy that includes everything that ended while the bot was down — then edits the
/// cards still marked stale; now and then it orphans rows whose card was never confirmed and prunes delivered winner
/// announcements. Giveaways are rows in the database, so nothing is lost across restarts; overlapping passes, a restart
/// mid-draw or a simultaneous /giveaway end cannot draw twice (the draw commits only while the row is still active).
/// Draws also happen while the module is disabled in a guild: a giveaway that was started is always finished.
/// </summary>
public sealed class GiveawayWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<GiveawayWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Lets the Discord gateway log in before the first pass reads reactions or edits a card.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(10);

    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var giveaways = scope.ServiceProvider.GetRequiredService<GiveawayService>();
        await giveaways.DrawDueAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<GiveawayCardSync>().SyncStaleAsync(cancellationToken);

        var now = clock.GetUtcNow();
        if (now - _lastMaintenance >= MaintenanceInterval)
        {
            _lastMaintenance = now;
            await giveaways.OrphanUnpostedAsync(cancellationToken);
            await giveaways.PruneAnnouncementsAsync(cancellationToken);
        }
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
                logger.LogError(ex, "Giveaway draw/card pass failed; retrying in {Interval}", Interval);
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
