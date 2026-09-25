using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// Keeps the fixture (TEST/DEMO) anchor in the provider-state table (esports_provider_state) so a restart re-uses the same demo timeline. Fixture mode only;
/// a storage problem means "no stored anchor" (the demo timeline is then simply renewed), never a crash.
/// </summary>
public sealed class ProviderStateFixtureAnchorStore(IServiceScopeFactory scopes, ILogger<ProviderStateFixtureAnchorStore> logger) : IFixtureAnchorStore
{
    public const string Key = "fixture:anchor";

    public async Task<DateTimeOffset?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            return await db.Set<ProviderStateEntity>().AsNoTracking().Where(s => s.Key == Key).Select(s => s.LastSuccessAt).FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the fixture anchor; the demo timeline starts now");
            return null;
        }
    }

    public async Task SaveAsync(DateTimeOffset anchor, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            var set = db.Set<ProviderStateEntity>();
            var row = await set.FirstOrDefaultAsync(s => s.Key == Key, cancellationToken);
            if (row is null)
            {
                row = new ProviderStateEntity { Key = Key };
                set.Add(row);
            }

            row.LastSuccessAt = anchor;
            row.LastAttemptAt = anchor;
            row.LastDetail = "fixture timeline anchor (TEST/DEMO only)";
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not store the fixture anchor; a restart will renew the demo timeline");
        }
    }
}
