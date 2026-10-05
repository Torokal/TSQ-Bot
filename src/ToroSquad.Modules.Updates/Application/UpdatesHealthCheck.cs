using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Persistence;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>/bot status: each polled game's last source result and freshness (from the database; never a request).</summary>
public sealed class UpdatesHealthCheck(IServiceScopeFactory scopes, GameUpdateCatalog catalog, IOptions<UpdatesOptions> options, TimeProvider clock) : IModuleHealthCheck
{
    public ModuleId Module => UpdatesModule.ModuleIdTyped;

    public async Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (o.Mode == UpdatesMode.Off)
            return new ModuleHealthReport(Module, [new HealthEntry("updates.health.source", HealthState.NotConfigured, "updates.health.off")]);

        await using var scope = scopes.CreateAsyncScope();
        var states = await scope.ServiceProvider.GetRequiredService<ToroDbContext>().Set<UpdatesSourceStateEntity>().AsNoTracking()
            .OrderBy(s => s.GameKey).ToListAsync(cancellationToken);
        var entries = new List<HealthEntry>();
        var now = clock.GetUtcNow();
        foreach (var game in catalog.Games)
        {
            var state = states.FirstOrDefault(s => s.Provider == game.Provider && s.GameKey == game.Key);
            if (state?.LastAttemptAt is null)
                continue; // not followed by any guild yet: nothing is requested, nothing to report
            var name = game.ShortName;
            var fresh = state.LastSuccessAt is { } ok && now - ok <= TimeSpan.FromHours(o.CatchUpHours);
            entries.Add(state.ConsecutiveFailures == 0 && fresh
                ? new HealthEntry("updates.health.source", HealthState.Healthy, "updates.health.ok", [name, DiscordText.Timestamp(state.LastSuccessAt!.Value, 'R')])
                : new HealthEntry("updates.health.source", fresh ? HealthState.Degraded : HealthState.Unavailable, "updates.health.failing",
                    [name, UpdatesConfigService.OutcomeName(state), state.ConsecutiveFailures, state.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—"]));
        }

        if (entries.Count == 0)
            entries.Add(new HealthEntry("updates.health.source", HealthState.NotConfigured, "updates.health.no_data"));
        return new ModuleHealthReport(Module, entries);
    }
}
