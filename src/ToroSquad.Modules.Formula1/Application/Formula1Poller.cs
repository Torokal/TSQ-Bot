using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

/// <summary>
/// The only place Formula 1 providers are called (commands never do). State-aware cadence:
/// <list type="bullet">
/// <item>Schedule every 6 h (hourly when a session is within 48 h); standings every 6 h, plus bounded settle-window checks
/// after a sprint/race.</item>
/// <item>Lifecycle only around sessions: the live stream is opened shortly before a scheduled start and closed afterwards;
/// REST reconciliation is a low-rate safety net (and runs right after every reconnect).</item>
/// <item>Results after a provider-stated finish (or, without lifecycle access, after the planned end), with bounded backoff;
/// then low-rate correction checks during the correction window.</item>
/// </list>
/// Failures back off (honouring Retry-After) and are recorded; a provider outage never stops the bot or other modules and
/// is never interpreted as "no sessions", "cancelled" or "finished". Nothing runs while no guild has the module enabled.
/// </summary>
public sealed class Formula1Poller(
    IServiceScopeFactory scopes,
    IF1ScheduleProvider schedule,
    IF1LifecycleProvider lifecycle,
    IF1ResultsProvider results,
    IF1StandingsProvider standings,
    Formula1LiveListener listener,
    Formula1Cache cache,
    DeploymentPolicy deployment,
    IOptions<Formula1Options> options,
    TimeProvider clock,
    ILogger<Formula1Poller> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private const int MaxResultCallsPerTick = 3;

    private readonly Dictionary<int, F1SeasonSchedule> _seasons = [];
    private DateTimeOffset _nextSchedule = DateTimeOffset.MinValue;
    private DateTimeOffset _nextStandings = DateTimeOffset.MinValue;
    private DateTimeOffset _nextReconcile = DateTimeOffset.MinValue;
    private DateTimeOffset _nextLifecycleMapping = DateTimeOffset.MinValue;
    private DateTimeOffset _nextResultsMapping = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPlan = DateTimeOffset.MinValue;
    private int _scheduleFailures;
    private int _standingsFailures;

    public string ScheduleKey(int season) => $"schedule:{schedule.Id}:{season}";

    public string StandingsKey(F1StandingsKind kind) => $"standings:{standings.Id}:{kind.ToString().ToLowerInvariant()}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WarmUpAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "F1 cache warm-up failed");
        }

        try
        {
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
                    logger.LogError(ex, "F1 polling tick failed");
                }

                // Wake early when the live stream delivers an event (low latency without busy polling).
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wake.CancelAfter(TickInterval);
                try
                {
                    await listener.Events.WaitToReadAsync(wake.Token);
                    if (!stoppingToken.IsCancellationRequested)
                        await Task.Delay(TimeSpan.FromMilliseconds(250), clock, stoppingToken); // let a burst arrive together
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // tick interval elapsed
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await listener.StopAsync(); // clean close on host shutdown
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        if (!await AnyGuildEnabledAsync(sp, ct))
        {
            if (listener.IsRunning)
                await listener.StopAsync();
            while (listener.Events.TryRead(out _))
            {
                // nobody to notify: drop leftovers so the loop does not keep waking up
            }

            return;
        }

        var workflow = sp.GetRequiredService<Formula1Workflow>();
        var now = clock.GetUtcNow();
        var changed = false;

        if (now >= _nextSchedule)
            changed |= await Step("schedule", () => RefreshScheduleAsync(sp, ct));
        if (now >= _nextStandings || await workflow.StandingsCheckDueAsync(ct))
            changed |= await Step("standings", () => RefreshStandingsAsync(sp, ct));
        changed |= await Step("lifecycle", () => LifecycleAsync(sp, ct));
        changed |= await Step("results", () => ResultsAsync(sp, ct));

        if (changed || clock.GetUtcNow() >= _nextPlan)
        {
            await sp.GetRequiredService<Formula1NotificationPlanner>().PlanAsync(ct);
            await RefreshViewsAsync(workflow, ct);
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
            // One failing step (or provider bug) never blocks the others.
            logger.LogError(ex, "F1 {Step} step failed", name);
            return false;
        }
    }

    private async Task<bool> AnyGuildEnabledAsync(IServiceProvider sp, CancellationToken ct)
    {
        var guilds = await sp.GetRequiredService<IModuleStateStore>().GetGuildsWithModuleEnabledAsync(Formula1Module.ModuleIdTyped, ct);
        return guilds.Any(g => deployment.IsGuildAllowed(g));
    }

    // ---------------------------------------------------------------- schedule

    public async Task<bool> RefreshScheduleAsync(IServiceProvider sp, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        var workflow = sp.GetRequiredService<Formula1Workflow>();
        var seasons = new List<int> { now.Year };
        // The next season is loaded once the current one has no future sessions left (or in December): no year is hard-coded.
        var current = _seasons.GetValueOrDefault(now.Year);
        if (now.Month == 12 || (current is not null && !current.Sessions.Any(s => s.ScheduledStartUtc > now)))
            seasons.Add(now.Year + 1);

        var ok = true;
        F1ProviderResult<F1SeasonSchedule>? last = null;
        foreach (var season in seasons)
        {
            var result = await SafeAsync(() => schedule.GetScheduleAsync(season, ct), now);
            last = result;
            await SaveStateAsync(sp, ScheduleKey(season), result, result.HasData ? F1Json.Serialize(result.Value!) : null, ct);
            if (!result.HasData)
            {
                ok = false;
                logger.LogWarning("F1 schedule {Season}: {Outcome} {Detail}", season, result.Outcome, result.Detail);
                continue;
            }

            _seasons[season] = result.Value!;
            await workflow.UpsertScheduleAsync(result.Value!, ct);
        }

        var feed = cache.Schedule;
        var ordered = _seasons.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        cache.SetSchedule(ok
            ? new F1Feed<IReadOnlyList<F1SeasonSchedule>>(ordered, now, F1ProviderOutcome.Success, null, now, 0)
            : feed with { Data = ordered.Count > 0 ? ordered : feed.Data, LastOutcome = last?.Outcome, LastDetail = last?.Detail, LastAttemptAt = now, ConsecutiveFailures = feed.ConsecutiveFailures + 1 });

        var weekend = _seasons.Values.SelectMany(s => s.Sessions).Any(s => s.ScheduledStartUtc > now && s.ScheduledStartUtc - now < TimeSpan.FromHours(48));
        var interval = weekend ? TimeSpan.FromMinutes(o.ScheduleRefreshWeekendMinutes) : TimeSpan.FromHours(o.ScheduleRefreshHours);
        _scheduleFailures = ok ? 0 : _scheduleFailures + 1;
        _nextSchedule = Next(ok, interval, _scheduleFailures, last);
        return ok;
    }

    // ---------------------------------------------------------------- standings

    public async Task<bool> RefreshStandingsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var workflow = sp.GetRequiredService<Formula1Workflow>();
        var changedCards = false;
        var ok = true;
        F1ProviderResult<F1StandingsSnapshot>? last = null;
        foreach (var kind in new[] { F1StandingsKind.Drivers, F1StandingsKind.Constructors })
        {
            var result = await SafeAsync(() => standings.GetStandingsAsync(kind, now.Year, ct), now);
            if (result.HasData && result.Value!.Count == 0)
            {
                // New season without standings yet: show last season's final table (labelled with its season).
                var previous = await SafeAsync(() => standings.GetStandingsAsync(kind, now.Year - 1, ct), now);
                if (previous.HasData && previous.Value!.Count > 0)
                    result = previous;
            }

            last = result;
            cache.UpdateStandings(kind, result, now);
            await SaveStateAsync(sp, StandingsKey(kind), result, null, ct);
            if (!result.HasData)
            {
                ok = false;
                logger.LogWarning("F1 standings {Kind}: {Outcome} {Detail}", kind, result.Outcome, result.Detail);
                continue;
            }

            var applied = await workflow.ApplyStandingsAsync(result.Value!, ct);
            changedCards |= applied.AttachedTo.Count > 0;
        }

        var closed = await workflow.AdvanceStandingsWatchAsync(ct);
        _standingsFailures = ok ? 0 : _standingsFailures + 1;
        _nextStandings = Next(ok, TimeSpan.FromHours(options.Value.StandingsRefreshHours), _standingsFailures, last);
        return changedCards || closed.Count > 0;
    }

    // ---------------------------------------------------------------- lifecycle

    public async Task<bool> LifecycleAsync(IServiceProvider sp, CancellationToken ct)
    {
        var workflow = sp.GetRequiredService<Formula1Workflow>();
        var active = await workflow.ActiveLifecycleSessionsAsync(ct);
        var changed = false;
        if (active.Count == 0)
        {
            if (listener.IsRunning)
                await listener.StopAsync();
            changed |= await ProcessLiveEventsAsync(sp, ct); // drain anything that arrived before the stop
            return changed;
        }

        await workflow.CaptureUpcomingBaselinesAsync(active.Select(a => a.SessionKey), ct);
        if (!lifecycle.IsConfigured)
        {
            cache.RecordLifecycleAttempt(F1ProviderOutcome.NotConfigured, "live lifecycle not configured", clock.GetUtcNow());
            return false; // honest: no lifecycle, no start notifications — nothing is inferred from the schedule
        }

        var now = clock.GetUtcNow();
        if (active.Any(a => a.LifecycleProvider != lifecycle.Id || a.LifecycleProviderRef is null) && now >= _nextLifecycleMapping)
        {
            foreach (var season in active.Select(a => a.Season).Distinct())
            {
                var sessions = await SafeAsync(() => lifecycle.GetSessionsAsync(season, ct), now);
                cache.RecordLifecycleAttempt(sessions.Outcome, sessions.Detail, now);
                if (sessions.HasData)
                    await workflow.MapProviderSessionsAsync(lifecycle.Id, sessions.Value!, lifecycle: true, ct);
            }

            _nextLifecycleMapping = now + TimeSpan.FromMinutes(5);
            active = await workflow.ActiveLifecycleSessionsAsync(ct);
        }

        if (listener.IsConfigured)
            listener.EnsureRunning(ct);
        changed |= await ProcessLiveEventsAsync(sp, ct);

        var reconnected = listener.ConsumeReconnectSignal();
        if (now >= _nextReconcile || reconnected)
        {
            foreach (var session in active.Where(a => a.LifecycleProvider == lifecycle.Id && a.LifecycleProviderRef is not null))
            {
                var events = await SafeAsync(() => lifecycle.GetLifecycleEventsAsync(session.LifecycleProviderRef!, ct), now);
                cache.RecordLifecycleAttempt(events.Outcome, events.Detail, now);
                if (!events.HasData)
                {
                    logger.LogWarning("F1 lifecycle reconcile {Session}: {Outcome} {Detail}", session.SessionKey, events.Outcome, events.Detail);
                    continue; // unknown stays unknown: a failure is never "not started" or "finished"
                }

                changed |= (await workflow.ApplyLifecycleAsync(lifecycle.Id, events.Value!, ct)).Count > 0;
            }

            var connected = listener.Status.State == F1LiveState.Connected;
            _nextReconcile = now + TimeSpan.FromSeconds(connected ? options.Value.LifecycleReconcileSeconds : options.Value.LifecycleReconcileFallbackSeconds);
        }

        return changed;
    }

    /// <summary>Applies queued live events (listener) to persisted state.</summary>
    public async Task<bool> ProcessLiveEventsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var batch = new List<F1LifecycleEvent>();
        while (batch.Count < 1000 && listener.Events.TryRead(out var e))
            batch.Add(e);
        if (batch.Count == 0)
            return false;
        var workflow = sp.GetRequiredService<Formula1Workflow>();
        var changed = false;
        foreach (var provider in batch.GroupBy(e => e.ProviderId))
            changed |= (await workflow.ApplyLifecycleAsync(provider.Key, provider.ToList(), ct)).Count > 0;
        return changed;
    }

    // ---------------------------------------------------------------- results

    public async Task<bool> ResultsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var workflow = sp.GetRequiredService<Formula1Workflow>();
        var due = await workflow.ResultsDueAsync(results.AvailabilityDelay, lifecycle.IsConfigured, ct);
        if (due.Count == 0)
            return false;
        var now = clock.GetUtcNow();
        if (due.Any(d => d.ResultsProvider != results.Id || d.ResultsProviderRef is null) && now >= _nextResultsMapping)
        {
            foreach (var season in due.Select(d => d.Season).Distinct())
            {
                var sessions = await SafeAsync(() => results.GetSessionsAsync(season, ct), now);
                cache.RecordResultsAttempt(sessions.Outcome, sessions.Detail, now);
                if (sessions.HasData)
                    await workflow.MapProviderSessionsAsync(results.Id, sessions.Value!, lifecycle: false, ct);
            }

            _nextResultsMapping = now + TimeSpan.FromMinutes(10);
            due = await workflow.ResultsDueAsync(results.AvailabilityDelay, lifecycle.IsConfigured, ct);
        }

        var changed = false;
        foreach (var session in due.Where(d => d.ResultsProvider == results.Id && d.ResultsProviderRef is not null).Take(MaxResultCallsPerTick))
        {
            var normalized = Formula1Workflow.ToSession(session);
            var result = await SafeAsync(() => results.GetResultAsync(normalized, session.ResultsProviderRef!, ct), now);
            cache.RecordResultsAttempt(result.Outcome, result.Detail, now);
            var outcome = await workflow.ApplyResultAsync(session.SessionKey, result, ct);
            if (outcome is F1ResultApplyOutcome.ProviderFailed)
                logger.LogWarning("F1 results {Session}: {Outcome} {Detail}", session.SessionKey, result.Outcome, result.Detail);
            changed |= outcome is F1ResultApplyOutcome.Finalised or F1ResultApplyOutcome.Corrected;
            // A fresh fetch also keeps existing result cards editable (freshness), so plan on every successful fetch.
            changed |= outcome == F1ResultApplyOutcome.Unchanged;
        }

        return changed;
    }

    // ---------------------------------------------------------------- warm-up / state

    /// <summary>Restores cached data after a restart (with the original fetch times — staleness stays honest).</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var prefix = $"schedule:{schedule.Id}:";
        var states = await db.Set<F1ProviderStateEntity>().AsNoTracking().ToListAsync(ct);
        DateTimeOffset? scheduleAt = null;
        foreach (var state in states.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal) && s.DataJson is not null))
        {
            if (F1Json.Deserialize<F1SeasonSchedule>(state.DataJson!) is { } season)
            {
                _seasons[season.Season] = season;
                if (season.Season == clock.GetUtcNow().Year || scheduleAt is null)
                    scheduleAt = state.LastSuccessAt;
            }
        }

        if (_seasons.Count > 0)
            cache.SetSchedule(new F1Feed<IReadOnlyList<F1SeasonSchedule>>(_seasons.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList(), scheduleAt, null, null, null, 0));

        foreach (var kind in new[] { F1StandingsKind.Drivers, F1StandingsKind.Constructors })
        {
            var row = await db.Set<F1StandingsSnapshotEntity>().AsNoTracking().Where(s => s.Kind == (int)kind)
                .OrderByDescending(s => s.Season).ThenByDescending(s => s.Id).FirstOrDefaultAsync(ct);
            if (row is not null && F1Json.Deserialize<F1StandingsSnapshot>(row.PayloadJson) is { } snapshot)
                cache.RestoreStandings(snapshot, states.FirstOrDefault(s => s.Key == StandingsKey(kind))?.LastSuccessAt ?? row.LastConfirmedAt);
        }

        await RefreshViewsAsync(scope.ServiceProvider.GetRequiredService<Formula1Workflow>(), ct);
    }

    private async Task RefreshViewsAsync(Formula1Workflow workflow, CancellationToken ct)
    {
        var (views, stored) = await workflow.LoadViewsAsync(ct);
        cache.SetSessions(views, stored);
    }

    private async Task<F1ProviderResult<T>> SafeAsync<T>(Func<Task<F1ProviderResult<T>>> call, DateTimeOffset now)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider bug is recorded as a transport error — never as "no data".
            logger.LogError(ex, "F1 provider call threw");
            return F1ProviderResult<T>.Fail(F1ProviderOutcome.TransportError, ex.GetType().Name, now);
        }
    }

    private DateTimeOffset Next<T>(bool ok, TimeSpan interval, int failures, F1ProviderResult<T>? last)
    {
        var now = clock.GetUtcNow();
        if (ok)
            return now + interval;
        if (last?.Outcome is F1ProviderOutcome.NotConfigured or F1ProviderOutcome.AuthFailed)
            return now + TimeSpan.FromMinutes(30); // configuration problems are not retried aggressively
        var backoff = TimeSpan.FromMinutes(Math.Min(360, 2 * Math.Pow(2, Math.Min(failures, 8))));
        if (backoff > interval)
            backoff = interval;
        return now + (last?.RetryAfter is { } ra && ra > backoff ? ra : backoff);
    }

    private async Task SaveStateAsync<T>(IServiceProvider sp, string key, F1ProviderResult<T> result, string? dataJson, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ToroDbContext>();
        var set = db.Set<F1ProviderStateEntity>();
        var row = await set.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new F1ProviderStateEntity { Key = key };
            set.Add(row);
        }

        var now = clock.GetUtcNow();
        row.LastAttemptAt = now;
        row.LastOutcome = (int)result.Outcome;
        row.LastDetail = result.Detail is { Length: > 480 } d ? d[..480] : result.Detail;
        if (result.HasData)
        {
            row.LastSuccessAt = result.At;
            row.ConsecutiveFailures = 0;
            if (dataJson is not null)
                row.DataJson = dataJson;
        }
        else
        {
            row.ConsecutiveFailures++;
        }

        await db.SaveChangesAsync(ct);
    }
}
