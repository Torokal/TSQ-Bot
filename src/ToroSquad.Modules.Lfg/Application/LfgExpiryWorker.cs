using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// One loop for all listings (no timer per listing): about once a minute it expires every active listing whose time is up —
/// on the first pass after a restart that includes everything that expired while the bot was down — and redraws stale
/// cards; every <see cref="VerifyInterval"/> it also checks that active cards still exist (one read each, orphaning deleted
/// ones). Listings are state in the database, so nothing is lost across deploys. Expiry also runs while the module is
/// disabled in a guild: it only retires cards that would otherwise keep looking joinable.
/// </summary>
public sealed class LfgExpiryWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<LfgExpiryWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>Lets the Discord gateway log in before the first pass tries to edit a card.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    /// <summary>Each active card is read at most this often to notice one deleted in Discord (no message-delete events).</summary>
    public static readonly TimeSpan VerifyInterval = TimeSpan.FromMinutes(5);

    private DateTimeOffset _lastVerify = DateTimeOffset.MinValue;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LfgService>().ExpireDueAsync(cancellationToken);
        var cards = scope.ServiceProvider.GetRequiredService<LfgCardSync>();
        await cards.SyncStaleAsync(cancellationToken);

        var now = clock.GetUtcNow();
        if (now - _lastVerify >= VerifyInterval)
        {
            _lastVerify = now;
            await cards.VerifyActiveCardsAsync(cancellationToken);
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
                logger.LogError(ex, "LFG expiry/card recovery pass failed; retrying in {Interval}", Interval);
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
