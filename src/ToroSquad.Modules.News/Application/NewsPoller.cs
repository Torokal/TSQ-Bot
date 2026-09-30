using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.News.Providers;

namespace ToroSquad.Modules.News.Application;

/// <summary>
/// The news loop. Nothing is requested while News:Mode is Off or while no guild would receive cards (module enabled,
/// channel set, not paused). Otherwise ONE shared feed request per interval — never per team, player or guild — where the
/// interval is the project default or the feed's own RSS &lt;ttl&gt;, whichever is longer. Failures back off exponentially
/// (429 honours Retry-After; 403 waits hours and is never worked around). The next poll time lives in the database, so a
/// restart neither resets a backoff nor causes an extra request. Rounds never overlap. The roster refresh is separate and
/// rare (News:Roster:RefreshHours). Any failure here stays in this module: other modules and the outbox are unaffected.
/// </summary>
public sealed class NewsPoller(
    IServiceScopeFactory scopes,
    HltvRssClient feed,
    LiquipediaRosterClient roster,
    IOptions<NewsOptions> options,
    TimeProvider clock,
    ILogger<NewsPoller> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);
    public static readonly TimeSpan BlockedBackoff = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _round = new(1, 1);
    private int _lastFailuresLogged = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Mode == NewsMode.Off)
        {
            logger.LogInformation("TSQ News is off (News:Mode=Off): no feed requests, no cards");
            return;
        }

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
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "news round failed; other modules are unaffected, retrying on the next tick");
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

    /// <summary>One tick: a feed round if one is due, then a roster refresh if one is due. Returns false when nothing ran.</summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        if (options.Value.Mode == NewsMode.Off || !await _round.WaitAsync(0, ct))
            return false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var planner = scope.ServiceProvider.GetRequiredService<NewsPlanner>();
            if (!await planner.HasActiveGuildAsync(ct))
                return false;
            var state = await planner.StateAsync(ct);
            var now = clock.GetUtcNow();
            var ran = false;
            if (state.NextPollAt is null || now >= state.NextPollAt)
            {
                var validators = state.BaselineAt is null ? null : new FeedValidators(state.ETag, state.LastModified);
                FeedFetchResult result;
                try
                {
                    result = await feed.FetchAsync(validators, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    result = FeedFetchResult.Fail(FeedOutcome.TransportError, null, ex.GetType().Name, clock.GetUtcNow());
                }

                var next = NextPoll(result, state.ConsecutiveFailures + (result.Succeeded ? 0 : 1), result.TtlMinutes ?? state.TtlMinutes);
                var summary = await planner.ApplyAsync(result, next, ct);
                Log(result, summary, state.ConsecutiveFailures, next);
                ran = true;
            }

            await using var rosterScope = scopes.CreateAsyncScope();
            var rosterPlanner = rosterScope.ServiceProvider.GetRequiredService<NewsPlanner>();
            var rosterState = await rosterPlanner.StateAsync(ct);
            if (options.Value.Roster.SyncFromLiquipedia && (rosterState.RosterNextAt is null || clock.GetUtcNow() >= rosterState.RosterNextAt))
            {
                await RefreshRosterAsync(rosterState, ct);
                await rosterScope.ServiceProvider.GetRequiredService<ToroDbContext>().SaveChangesAsync(ct);
                ran = true;
            }

            return ran;
        }
        finally
        {
            _round.Release();
        }
    }

    public DateTimeOffset NextPoll(FeedFetchResult result, int consecutiveFailures, int? ttlMinutes)
    {
        var now = clock.GetUtcNow();
        var interval = options.Value.PollInterval;
        if (ttlMinutes is { } ttl && TimeSpan.FromMinutes(ttl) > interval)
            interval = TimeSpan.FromMinutes(ttl); // the source asks clients to cache this long
        if (result.Succeeded)
            return now + interval;
        if (result.Outcome == FeedOutcome.Blocked)
            return now + BlockedBackoff;
        var backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, (long)(interval.Ticks * Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 8)))));
        if (result.RetryAfter is { } retryAfter && retryAfter > backoff)
            backoff = retryAfter;
        return now + backoff;
    }

    private async Task RefreshRosterAsync(Persistence.NewsFeedStateEntity state, CancellationToken ct)
    {
        var o = options.Value;
        RosterFetchResult result;
        try
        {
            result = await roster.FetchAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result = new RosterFetchResult(RosterOutcome.Failed, [], ex.GetType().Name);
        }

        var now = clock.GetUtcNow();
        state.RosterLastAttemptAt = now;
        state.RosterLastOutcome = (int)result.Outcome;
        if (result.Outcome == RosterOutcome.Ok)
        {
            var players = string.Join(',', result.Players);
            if (players != state.RosterPlayers)
                logger.LogInformation("news roster updated from Liquipedia: {Count} players", result.Players.Count);
            state.RosterPlayers = players.Length > 500 ? players[..500] : players;
            state.RosterVerifiedAt = now;
            state.RosterSource = "liquipedia";
            state.RosterDetail = null;
            state.RosterFailures = 0;
            state.RosterNextAt = now + TimeSpan.FromHours(o.Roster.RefreshHours);
            return;
        }

        // The last good roster is kept (never emptied); it simply ages until it is too old to be used.
        state.RosterFailures++;
        state.RosterDetail = result.Detail is { Length: > 300 } d ? d[..300] : result.Detail;
        var wait = result.RetryAfter ?? TimeSpan.FromHours(Math.Min(o.Roster.RefreshHours, 6 * state.RosterFailures));
        state.RosterNextAt = now + wait;
        logger.LogWarning("news roster refresh failed: {Outcome} {Detail} (attempt {Failures}; previous roster kept)", result.Outcome, result.Detail, state.RosterFailures);
    }

    private void Log(FeedFetchResult result, NewsRoundSummary summary, int failuresBefore, DateTimeOffset next)
    {
        if (result.Succeeded)
        {
            if (failuresBefore > 0)
                logger.LogInformation("news feed recovered after {Failures} failed attempt(s)", failuresBefore);
            if (summary.Staged > 0 || summary.Edits > 0)
                logger.LogInformation("news round: {Items} items, {New} new, {Relevant} about the team, {Staged} card(s) staged, {Edits} edit(s)",
                    summary.Items, summary.NewItems, summary.Relevant, summary.Staged, summary.Edits);
            else
                logger.LogDebug("news round: {Outcome} {Items} items, {New} new, nothing to post", result.Outcome, summary.Items, summary.NewItems);
            _lastFailuresLogged = -1;
            return;
        }

        var failures = failuresBefore + 1;
        if (failures == 1 || failures % 10 == 0 || _lastFailuresLogged < 0)
            logger.LogWarning("news feed request failed: {Outcome} {Detail} (attempt {Failures}; next try {Next:u}; not treated as 'no news')",
                result.Outcome, result.Detail, failures, next);
        _lastFailuresLogged = failures;
    }

    public override void Dispose()
    {
        _round.Dispose();
        base.Dispose();
    }
}
