using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// The only place provider APIs are called. Public match data is fetched ONCE per cycle for all guilds and cached;
/// guild-specific filtering happens in the planner. Failures back off exponentially (and honour Retry-After); a
/// provider outage never takes down the bot or other modules.
/// </summary>
public sealed class EsportsPoller(
    IServiceScopeFactory scopes,
    IEsportsDataProvider matchProvider,
    IRankingsProvider rankingsProvider,
    EsportsCache cache,
    MatchLinkCatalog links,
    LiquipediaHltvLinkSource hltvLinks,
    LiquipediaWikiLinkSource wikiLinks,
    IOptions<EsportsOptions> options,
    TimeProvider clock,
    ILogger<EsportsPoller> logger) : BackgroundService
{
    // Provider-specific keys: switching providers never mixes their cached data.
    public string MatchesKey => matchProvider.Id + ":matches";
    public string EventsKey => matchProvider.Id + ":events";
    public const string RankingsKey = "valve:vrs";

    private DateTimeOffset _nextMatches = DateTimeOffset.MinValue;
    private DateTimeOffset _nextEvents = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRankings = DateTimeOffset.MinValue;
    private DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WarmUpAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Esports cache warm-up failed");
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
                logger.LogError(ex, "Esports polling tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (now >= _nextRankings)
            await RefreshRankingsAsync(ct);
        if (now >= _nextMatches)
            await RefreshMatchesAsync(ct);
        if (now >= _nextEvents)
            await RefreshEventsAsync(ct);
        if (now >= _nextMaintenance)
            await MaintenanceAsync(ct);
    }

    public async Task<PlanReport?> RefreshMatchesAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var previousFetch = cache.Matches.FetchedAt;
        var window = new MatchWindow(now - TimeSpan.FromHours(o.PastWindowHours), now + TimeSpan.FromHours(o.FutureWindowHours));
        var result = await SafeAsync(() => matchProvider.GetMatchesAsync(window, ct), now);
        ProviderResult<IReadOnlyList<EsportsMatch>>? linkResult = null;
        var wikiChanged = false;
        if (result.HasData)
        {
            // External match pages, all automatic: LiquipediaDB's HLTV links (approved key), otherwise the free Liquipedia
            // MediaWiki API for followed teams (unique match only). Never HLTV itself. Link sources can never fail the poll.
            try
            {
                linkResult = await hltvLinks.RefreshIfDueAsync(window, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "HLTV link source refresh threw; keeping known links");
            }

            var enriched = result.Value!.Select(links.Apply).Select(hltvLinks.Apply).ToList();
            try
            {
                if (wikiLinks.Enabled)
                    wikiChanged = await wikiLinks.RefreshAsync(enriched, await FollowedTeamKeysAsync(ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "HLTV link lookup (Liquipedia MediaWiki) threw; keeping known links");
            }

            result = result with { Value = enriched.Select(wikiLinks.Apply).ToList() };
        }
        cache.UpdateMatches(result, now);
        _nextMatches = NextAttempt(result, TimeSpan.FromMinutes(o.MatchPollMinutes), cache.Matches.ConsecutiveFailures);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        await SaveStateAsync(db, MatchesKey, result, null, ct);
        if (linkResult is not null)
            await SaveStateAsync(db, hltvLinks.StateKey, linkResult, linkResult.HasData ? MatchJson.SerializeList(hltvLinks.Candidates) : null, ct);
        if (wikiChanged)
        {
            var outcome = wikiLinks.LastOutcome ?? ProviderOutcome.Success;
            var wikiResult = outcome is ProviderOutcome.Success or ProviderOutcome.Partial
                ? ProviderResult<string>.Ok("", now)
                : ProviderResult<string>.Fail(outcome, "lookup failed", now);
            await SaveStateAsync(db, LiquipediaWikiLinkSource.StateKey, wikiResult, wikiLinks.Export(), ct, alwaysWriteData: true);
        }
        if (!result.HasData)
        {
            logger.LogWarning("Esports matches fetch: {Outcome} {Detail}", result.Outcome, result.Detail);
            return null;
        }

        var afterGap = previousFetch is null || now - previousFetch > TimeSpan.FromMinutes(o.MatchPollMinutes * 3);
        var planner = scope.ServiceProvider.GetRequiredService<NotificationPlanner>();
        var report = await planner.PlanAsync(result.Value!, result.At, afterGap, ct);
        logger.LogInformation("Esports poll: {Matches} matches ({Outcome}); guilds={Guilds} new={Created} updated={Updated} filtered={Filtered} blocked={Blocked}",
            report.Matches, result.Outcome, report.GuildsConsidered, report.Created, report.Updated, report.FilteredOut, report.BlockedMissingData);
        return report;
    }

    public async Task RefreshEventsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var result = await SafeAsync(() => matchProvider.GetEventsAsync(today.AddDays(-7), today.AddDays(45), ct), now);
        cache.UpdateEvents(result, now);
        _nextEvents = NextAttempt(result, TimeSpan.FromHours(options.Value.EventPollHours), cache.Events.ConsecutiveFailures);
        await using var scope = scopes.CreateAsyncScope();
        await SaveStateAsync(scope.ServiceProvider.GetRequiredService<ToroDbContext>(), EventsKey, result,
            result.HasData ? MatchJson.SerializeList(result.Value!) : null, ct);
    }

    public async Task RefreshRankingsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var result = await SafeAsync(() => rankingsProvider.GetLatestAsync(ct), now);
        cache.UpdateRankings(result, now);
        _nextRankings = NextAttempt(result, TimeSpan.FromHours(options.Value.RankingPollHours), cache.Rankings.ConsecutiveFailures);
        await using var scope = scopes.CreateAsyncScope();
        await SaveStateAsync(scope.ServiceProvider.GetRequiredService<ToroDbContext>(), RankingsKey, result,
            result.HasData ? MatchJson.Serialize(result.Value!) : null, ct);
    }

    /// <summary>Restore last known data after restart (with its real fetch time — staleness stays honest).</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var states = await db.Set<ProviderStateEntity>().AsNoTracking().ToDictionaryAsync(s => s.Key, ct);
        var horizon = clock.GetUtcNow() - TimeSpan.FromHours(options.Value.PastWindowHours);
        var prefix = matchProvider.Id + ":";
        var snapshots = await db.Set<MatchSnapshotEntity>().AsNoTracking()
            .Where(s => s.MatchKey.StartsWith(prefix) && (s.ScheduledStartUtc == null || s.ScheduledStartUtc >= horizon))
            .Select(s => s.PayloadJson).ToListAsync(ct);
        var matches = snapshots.Select(MatchJson.Deserialize).OfType<EsportsMatch>().ToList();
        var teams = await db.Set<KnownTeamEntity>().AsNoTracking().ToListAsync(ct);

        List<EsportsEvent>? events = null;
        if (states.TryGetValue(EventsKey, out var ev) && ev.DataJson is not null)
            events = MatchJson.DeserializeList<EsportsEvent>(ev.DataJson);
        RankingSnapshot? rankings = null;
        if (states.TryGetValue(RankingsKey, out var rk) && rk.DataJson is not null)
            rankings = MatchJson.Deserialize<RankingSnapshot>(rk.DataJson);

        if (states.TryGetValue(hltvLinks.StateKey, out var lk))
            hltvLinks.Restore(lk.DataJson is null ? null : MatchJson.DeserializeList<EsportsMatch>(lk.DataJson), lk.LastAttemptAt);
        if (states.TryGetValue(LiquipediaWikiLinkSource.StateKey, out var wk))
            wikiLinks.Restore(wk.DataJson);

        cache.Restore(matches.Count > 0 ? matches : null, states.GetValueOrDefault(MatchesKey)?.LastSuccessAt,
            events, ev?.LastSuccessAt, rankings,
            teams.Where(t => IsProviderTeam(t.TeamKey)).Select(t => new TeamRef(matchProvider.Id, t.TeamKey, t.Name, t.ShortName)).ToList());
    }

    /// <summary>Teams in the team filter of servers that receive esports notifications (the only matches worth a link lookup).</summary>
    private async Task<IReadOnlySet<string>> FollowedTeamKeysAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var active = db.Set<EsportsGuildConfigEntity>().Where(c => c.ChannelId != null && !c.Paused).Select(c => c.GuildId);
        var keys = await db.Set<EsportsFilterEntity>()
            .Where(f => f.Dimension == (int)FilterDimension.Team && active.Contains(f.GuildId))
            .Select(f => f.Value)
            .Distinct()
            .ToListAsync(ct);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    private bool IsProviderTeam(string teamKey)
    {
        var pandaKey = teamKey.StartsWith("ps-team:", StringComparison.Ordinal);
        return matchProvider.Id == Providers.PandaScore.PandaScoreParser.Source ? pandaKey : !pandaKey;
    }

    private async Task MaintenanceAsync(CancellationToken ct)
    {
        _nextMaintenance = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(14);
        await db.Set<MatchSnapshotEntity>().Where(s => s.LastSeenAt < cutoff).ExecuteDeleteAsync(ct);
        await scope.ServiceProvider.GetRequiredService<SubscriptionService>().RetryPendingAsync(maxAttempts: 5, ct);
    }

    private async Task<ProviderResult<T>> SafeAsync<T>(Func<Task<ProviderResult<T>>> call, DateTimeOffset now)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider bug must never crash polling; it is recorded as a transport error, not as "no data".
            logger.LogError(ex, "Provider call threw");
            return ProviderResult<T>.Fail(ProviderOutcome.TransportError, ex.GetType().Name, now);
        }
    }

    private DateTimeOffset NextAttempt<T>(ProviderResult<T> result, TimeSpan interval, int failures)
    {
        var now = clock.GetUtcNow();
        if (result.HasData)
            return now + interval;
        if (result.Outcome == ProviderOutcome.NotConfigured)
            return now + TimeSpan.FromMinutes(15);
        var backoff = TimeSpan.FromTicks(Math.Min(interval.Ticks * (long)Math.Pow(2, Math.Min(failures, 5)), TimeSpan.FromHours(2).Ticks));
        if (backoff < interval) backoff = interval;
        return now + (result.RetryAfter is { } ra && ra > backoff ? ra : backoff);
    }

    private async Task SaveStateAsync<T>(ToroDbContext db, string key, ProviderResult<T> result, string? dataJson, CancellationToken ct, bool alwaysWriteData = false)
    {
        var set = db.Set<ProviderStateEntity>();
        var row = await set.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new ProviderStateEntity { Key = key };
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
            if (alwaysWriteData && dataJson is not null)
                row.DataJson = dataJson; // a cache (misses included) is kept even when the last attempt failed
        }

        await db.SaveChangesAsync(ct);
    }
}
