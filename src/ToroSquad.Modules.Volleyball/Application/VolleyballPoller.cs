using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Persistence;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

/// <summary>
/// The only place the volleyball provider is called (commands never do). ONE shared fetch per tick for all guilds — the
/// number of servers never multiplies provider requests. State-aware cadence:
/// <list type="bullet">
/// <item>Fixture/result discovery every <see cref="VolleyballOptions.FixtureRefreshHours"/> h, every
/// <see cref="VolleyballOptions.FixtureRefreshNearMinutes"/> min when a match is within 48 h.</item>
/// <item>Live state only in a match window (from <see cref="VolleyballOptions.LiveLeadMinutes"/> before the start until the
/// provider says finished, at most <see cref="VolleyballOptions.LiveTrailingHours"/>), every
/// <see cref="VolleyballOptions.LivePollSeconds"/> s. No live polling at all on days without a match.</item>
/// </list>
/// Failures back off exponentially (honouring Retry-After) and are recorded; an outage is never "no matches", "cancelled"
/// or "finished", and there is never a fallback to fixture data. Nothing runs while no guild has the module enabled.
/// </summary>
public sealed class VolleyballPoller(
    IServiceScopeFactory scopes,
    IVolleyballDataProvider provider,
    TrackedTeamIdentity team,
    VolleyballCache cache,
    DeploymentPolicy deployment,
    IOptions<VolleyballOptions> options,
    TimeProvider clock,
    ILogger<VolleyballPoller> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private DateTimeOffset _nextFixtures = DateTimeOffset.MinValue;
    private DateTimeOffset _nextLive = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPlan = DateTimeOffset.MinValue;
    private int _fixtureFailures;
    private int _liveFailures;

    public string FixturesKey => $"fixtures:{provider.Id}";

    public string LiveKey => $"live:{provider.Id}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WarmUpAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "volleyball cache warm-up failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "volleyball polling tick failed");
            }

            try
            {
                await Task.Delay(TickInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        if (!await AnyGuildEnabledAsync(sp, ct))
            return;

        var workflow = sp.GetRequiredService<VolleyballWorkflow>();
        var changed = false;
        if (clock.GetUtcNow() >= _nextFixtures)
            changed |= await Step("fixtures", () => RefreshFixturesAsync(sp, ct));
        if (clock.GetUtcNow() >= _nextLive)
            changed |= await Step("live", () => RefreshLiveAsync(sp, ct));

        // The reminder is time-driven: planning runs at least once a minute even without new data.
        if (changed || clock.GetUtcNow() >= _nextPlan)
        {
            await sp.GetRequiredService<VolleyballNotificationPlanner>().PlanAsync(ct);
            cache.SetMatches(await workflow.LoadViewsAsync(ct));
            _nextPlan = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
        }
    }

    private async Task<bool> Step(string name, Func<Task<bool>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "volleyball {Step} step failed", name);
            return false;
        }
    }

    private async Task<bool> AnyGuildEnabledAsync(IServiceProvider sp, CancellationToken ct)
    {
        var guilds = await sp.GetRequiredService<IModuleStateStore>().GetGuildsWithModuleEnabledAsync(VolleyballModule.ModuleIdTyped, ct);
        return guilds.Any(g => deployment.IsGuildAllowed(g));
    }

    // ---------------------------------------------------------------- fixtures

    public async Task<bool> RefreshFixturesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var workflow = sp.GetRequiredService<VolleyballWorkflow>();
        var result = await SafeAsync(() => provider.GetMatchesAsync(team, now - TimeSpan.FromDays(o.FixtureWindowPastDays), now + TimeSpan.FromDays(o.FixtureWindowAheadDays), ct), now);
        // Scheduled BEFORE anything else can throw: a failing database step must never turn into a request every tick.
        _fixtureFailures = result.Succeeded ? 0 : _fixtureFailures + 1;
        _nextFixtures = Next(result, TimeSpan.FromMinutes(o.FixtureRefreshNearMinutes), _fixtureFailures);
        ShareRateLimit(result);
        var applied = false;
        int? ambiguous = null;
        if (result.HasData)
        {
            var (accepted, rejectedAmbiguous) = Filter(result.Value!);
            ambiguous = rejectedAmbiguous;
            var report = await workflow.ApplyAsync(accepted, result.At, listed: true, ct);
            applied = report.Transitions > 0 || report.Discovered > 0;
        }
        else
        {
            logger.LogWarning("volleyball fixtures: {Outcome} {Detail}", result.Outcome, result.Detail);
            if (result.Outcome == VbProviderOutcome.RateLimited)
                logger.LogWarning("volleyball provider_rate_limited dataset=fixtures retry_after={RetryAfter}", result.RetryAfter);
            else if (result.Outcome == VbProviderOutcome.SchemaChanged)
                logger.LogError("volleyball provider_schema_error dataset=fixtures {Detail}", result.Detail);
        }

        cache.RecordFixtures(result, now, ambiguous);
        await SaveStateAsync(sp, FixturesKey, result, ct);

        // Hourly while a match is within 48 h or started without a result yet (a final VIS publishes late is found soon).
        var next = await workflow.NextStartAsync(ct);
        var near = (next is { } s && s - now <= TimeSpan.FromHours(48)) || await workflow.AwaitingResultAsync(ct);
        _nextFixtures = Next(result, near ? TimeSpan.FromMinutes(o.FixtureRefreshNearMinutes) : TimeSpan.FromHours(o.FixtureRefreshHours), _fixtureFailures);
        ShareRateLimit(result);
        return applied;
    }

    // ---------------------------------------------------------------- live

    public async Task<bool> RefreshLiveAsync(IServiceProvider sp, CancellationToken ct)
    {
        var o = options.Value;
        var workflow = sp.GetRequiredService<VolleyballWorkflow>();
        var candidates = await workflow.LiveCandidatesAsync(ct);
        var now = clock.GetUtcNow();
        if (candidates.Count == 0)
        {
            _nextLive = now + TickInterval;
            return false;
        }

        if (!provider.Capabilities.HasFlag(VbCapabilities.LiveMatchState))
        {
            // Honest: without a live source there are no started/set cards; the final comes from the fixture refresh.
            _nextLive = now + TimeSpan.FromMinutes(5);
            return false;
        }

        var ids = candidates.Where(c => c.Provider == provider.Id).Select(c => c.ProviderMatchId).ToList();
        if (ids.Count == 0)
        {
            _nextLive = now + TickInterval;
            return false;
        }

        var result = await SafeAsync(() => provider.GetLiveStateAsync(ids, ct), now);
        _liveFailures = result.Succeeded ? 0 : _liveFailures + 1;
        _nextLive = Next(result, TimeSpan.FromSeconds(o.LivePollSeconds), _liveFailures);
        ShareRateLimit(result);
        var changed = false;
        if (result.HasData)
        {
            var (accepted, _) = Filter(result.Value!);
            var report = await workflow.ApplyAsync(accepted, result.At, listed: false, ct);
            changed = report.Transitions > 0;
        }
        else
        {
            logger.LogWarning("volleyball live: {Outcome} {Detail}", result.Outcome, result.Detail);
            if (result.Outcome == VbProviderOutcome.RateLimited)
                logger.LogWarning("volleyball provider_rate_limited dataset=live retry_after={RetryAfter}", result.RetryAfter);
            else if (result.Outcome == VbProviderOutcome.SchemaChanged)
                logger.LogError("volleyball provider_schema_error dataset=live {Detail}", result.Detail);
        }

        cache.RecordLive(result, now);
        await SaveStateAsync(sp, LiveKey, result, ct);
        return changed;
    }

    /// <summary>
    /// Keeps only matches of the followed team (identity rules in <see cref="TrackedTeamIdentity"/>). Ambiguous or
    /// contradictory identities are rejected and counted — a wrong team's match must never produce a card.
    /// </summary>
    public (IReadOnlyList<(VolleyballMatch Match, FollowedSide Side)> Accepted, int Ambiguous) Filter(IReadOnlyList<VolleyballMatch> matches)
    {
        var accepted = new List<(VolleyballMatch, FollowedSide)>();
        var ambiguous = 0;
        foreach (var match in matches)
        {
            var verdict = team.Evaluate(match);
            if (verdict.Accepted)
            {
                accepted.Add((match, verdict.Side!.Value));
                continue;
            }

            if (verdict.Reason is MatchFilterReason.AmbiguousIdentity or MatchFilterReason.ProviderIdConflict or MatchFilterReason.BothSidesMatch)
            {
                ambiguous++;
                logger.LogWarning("volleyball ambiguous_team_identity match={Match} reason={Reason}: rejected (never announced)", match.MatchId, verdict.Reason);
            }
        }

        return (accepted, ambiguous);
    }

    // ---------------------------------------------------------------- warm-up / state

    /// <summary>Restores provider health and the command cache after a restart (with the original times — freshness stays honest).</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var states = await db.Set<VbProviderStateEntity>().AsNoTracking().Where(s => s.Key == FixturesKey || s.Key == LiveKey).ToListAsync(ct);
        VbFeed Feed(string key) => states.FirstOrDefault(s => s.Key == key) is { } s
            ? new VbFeed(s.LastSuccessAt, (VbProviderOutcome)s.LastOutcome, s.LastDetail, s.LastAttemptAt, s.ConsecutiveFailures)
            : VbFeed.Empty;
        cache.Restore(Feed(FixturesKey), Feed(LiveKey));
        cache.SetMatches(await scope.ServiceProvider.GetRequiredService<VolleyballWorkflow>().LoadViewsAsync(ct));
    }

    private async Task<VbProviderResult<T>> SafeAsync<T>(Func<Task<VbProviderResult<T>>> call, DateTimeOffset now)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider bug is recorded as a transport error — never as "no data".
            logger.LogError(ex, "volleyball provider call threw");
            return VbProviderResult<T>.Fail(VbProviderOutcome.TransportError, ex.GetType().Name, now);
        }
    }

    /// <summary>A provider-stated Retry-After pauses BOTH datasets (same provider, same limit).</summary>
    private void ShareRateLimit<T>(VbProviderResult<T> result)
    {
        if (result.Outcome != VbProviderOutcome.RateLimited || result.RetryAfter is not { } wait)
            return;
        var until = clock.GetUtcNow() + wait;
        if (_nextFixtures < until)
            _nextFixtures = until;
        if (_nextLive < until)
            _nextLive = until;
    }

    private DateTimeOffset Next<T>(VbProviderResult<T> result, TimeSpan interval, int failures)
    {
        var now = clock.GetUtcNow();
        if (result.Succeeded)
            return now + interval;
        if (result.Outcome is VbProviderOutcome.NotConfigured or VbProviderOutcome.Unauthorized)
            return now + TimeSpan.FromMinutes(30); // configuration problems are not retried aggressively
#pragma warning disable CA5394 // jitter, not security relevant
        var jitter = 0.85 + (Random.Shared.NextDouble() * 0.3);
#pragma warning restore CA5394
        var backoff = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(failures, 7)) * jitter));
        if (backoff < interval)
            backoff = interval;
        return now + (result.RetryAfter is { } ra && ra > backoff ? ra : backoff);
    }

    private async Task SaveStateAsync<T>(IServiceProvider sp, string key, VbProviderResult<T> result, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ToroDbContext>();
        var set = db.Set<VbProviderStateEntity>();
        var row = await set.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new VbProviderStateEntity { Key = key };
            set.Add(row);
        }

        row.LastAttemptAt = clock.GetUtcNow();
        row.LastOutcome = (int)result.Outcome;
        row.LastDetail = result.Detail is { Length: > 480 } d ? d[..480] : result.Detail;
        if (result.Succeeded)
        {
            row.LastSuccessAt = result.At;
            row.ConsecutiveFailures = 0;
        }
        else
        {
            row.ConsecutiveFailures++;
        }

        await db.SaveChangesAsync(ct);
    }
}
