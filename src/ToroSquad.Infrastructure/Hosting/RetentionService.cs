using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Privacy;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Infrastructure.Hosting;

/// <summary>
/// When the bot has been removed from a guild for longer than the retention period, all data stored for that guild
/// (core + every module) is deleted. Re-adding the bot before that simply cancels the pending purge.
/// </summary>
public sealed class RetentionService(IServiceScopeFactory scopes, IOptions<BotOptions> options, TimeProvider clock, ILogger<RetentionService> logger)
    : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var presence = scope.ServiceProvider.GetRequiredService<GuildPresenceStore>();
        var due = await presence.GetDueForPurgeAsync(TimeSpan.FromDays(options.Value.GuildDataRetentionDays), cancellationToken);
        foreach (var guild in due)
        {
            var total = await scope.ServiceProvider.GetRequiredService<CoreGuildDataPurger>().PurgeAsync(guild, cancellationToken);
            foreach (var contributor in scope.ServiceProvider.GetServices<IUserDataContributor>())
                total += await contributor.PurgeGuildAsync(guild, cancellationToken);
            await presence.MarkPurgedAsync(guild, cancellationToken);
            logger.LogInformation("Retention: purged {Count} records of departed guild {Guild}", total, guild);
        }

        return due.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention run failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(6), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
