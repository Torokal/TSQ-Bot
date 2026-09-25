using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Formula1.Providers.Fixtures;

namespace ToroSquad.Modules.Formula1.Application;

/// <summary>
/// Keeps the fixture (TEST/DEMO) anchor in f1_provider_state so a restart continues the same demo weekend. Fixture mode
/// only; a storage problem means "no stored anchor" (the demo timeline is renewed), never a crash.
/// </summary>
public sealed class F1ProviderStateFixtureAnchorStore(IServiceScopeFactory scopes, ILogger<F1ProviderStateFixtureAnchorStore> logger) : IF1FixtureAnchorStore
{
    public const string Key = "fixture:anchor";

    public async Task<DateTimeOffset?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            return await db.Set<F1ProviderStateEntity>().AsNoTracking().Where(s => s.Key == Key).Select(s => s.LastSuccessAt).FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the F1 fixture anchor; the demo timeline starts now");
            return null;
        }
    }

    public async Task SaveAsync(DateTimeOffset anchor, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            var set = db.Set<F1ProviderStateEntity>();
            var row = await set.FirstOrDefaultAsync(s => s.Key == Key, cancellationToken);
            if (row is null)
            {
                row = new F1ProviderStateEntity { Key = Key };
                set.Add(row);
            }

            row.LastSuccessAt = anchor;
            row.LastAttemptAt = anchor;
            row.LastDetail = "fixture timeline anchor (TEST/DEMO only)";
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not store the F1 fixture anchor; a restart will renew the demo timeline");
        }
    }
}
