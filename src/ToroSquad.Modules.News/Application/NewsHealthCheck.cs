using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.News.Persistence;
using ToroSquad.Modules.News.Providers;

namespace ToroSquad.Modules.News.Application;

/// <summary>/bot status: the feed's last result and freshness (from the database; never a request).</summary>
public sealed class NewsHealthCheck(IServiceScopeFactory scopes, IOptions<NewsOptions> options, TimeProvider clock) : IModuleHealthCheck
{
    public ModuleId Module => NewsModule.ModuleIdTyped;

    public async Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (o.Mode == NewsMode.Off)
            return new ModuleHealthReport(Module, [new HealthEntry("news.health.feed", HealthState.NotConfigured, "news.health.off")]);

        await using var scope = scopes.CreateAsyncScope();
        var state = await scope.ServiceProvider.GetRequiredService<ToroDbContext>().Set<NewsFeedStateEntity>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == NewsPlanner.FeedKey, cancellationToken);
        if (state?.LastAttemptAt is null)
            return new ModuleHealthReport(Module, [new HealthEntry("news.health.feed", HealthState.Degraded, "news.health.no_data")]);
        var fresh = state.LastSuccessAt is { } ok && clock.GetUtcNow() - ok <= TimeSpan.FromHours(o.CatchUpHours);
        var entry = state.ConsecutiveFailures == 0 && fresh
            ? new HealthEntry("news.health.feed", HealthState.Healthy, "news.health.ok", [DiscordText.Timestamp(state.LastSuccessAt!.Value, 'R')])
            : new HealthEntry("news.health.feed", fresh ? HealthState.Degraded : HealthState.Unavailable, "news.health.failing",
                [((FeedOutcome)state.LastOutcome).ToString(), state.ConsecutiveFailures, state.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—"]);
        return new ModuleHealthReport(Module, [entry]);
    }
}
