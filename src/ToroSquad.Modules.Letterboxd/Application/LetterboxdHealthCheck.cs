using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Letterboxd.Domain;
using ToroSquad.Modules.Letterboxd.Persistence;
using ToroSquad.Modules.Letterboxd.Providers;

namespace ToroSquad.Modules.Letterboxd.Application;

/// <summary>/bot status: the feed's last result and freshness, plus the partial coverage (from the database; never a request).</summary>
public sealed class LetterboxdHealthCheck(IServiceScopeFactory scopes, IOptions<LetterboxdOptions> options, TimeProvider clock) : IModuleHealthCheck
{
    public ModuleId Module => LetterboxdModule.ModuleIdTyped;

    public async Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var coverage = new HealthEntry("letterboxd.health.coverage", LetterboxdCapabilities.FullCoverage ? HealthState.Healthy : HealthState.Degraded,
            LetterboxdCapabilities.FullCoverage ? "letterboxd.health.coverage_full" : "letterboxd.health.coverage_partial");
        if (o.Mode == LetterboxdMode.Off)
            return new ModuleHealthReport(Module, [new HealthEntry("letterboxd.health.feed", HealthState.NotConfigured, "letterboxd.health.off"), coverage]);

        await using var scope = scopes.CreateAsyncScope();
        var key = LetterboxdPlanner.StateKey(o.Account);
        var state = await scope.ServiceProvider.GetRequiredService<ToroDbContext>().Set<LetterboxdSourceStateEntity>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (state?.LastAttemptAt is null)
            return new ModuleHealthReport(Module, [new HealthEntry("letterboxd.health.feed", HealthState.Degraded, "letterboxd.health.no_data"), coverage]);
        var fresh = state.LastSuccessAt is { } ok && clock.GetUtcNow() - ok <= TimeSpan.FromHours(o.CatchUpHours);
        var entry = state.ConsecutiveFailures == 0 && fresh
            ? new HealthEntry("letterboxd.health.feed", HealthState.Healthy, "letterboxd.health.ok", [DiscordText.Timestamp(state.LastSuccessAt!.Value, 'R')])
            : new HealthEntry("letterboxd.health.feed", fresh ? HealthState.Degraded : HealthState.Unavailable, "letterboxd.health.failing",
                [((FeedOutcome)state.LastOutcome).ToString(), state.ConsecutiveFailures, state.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—"]);
        return new ModuleHealthReport(Module, [entry, coverage]);
    }
}
