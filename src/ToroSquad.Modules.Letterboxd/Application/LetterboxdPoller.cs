using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Letterboxd.Providers;

namespace ToroSquad.Modules.Letterboxd.Application;

/// <summary>
/// The feed loop. Nothing is requested while Letterboxd:Mode is Off or while no guild would receive cards (module enabled,
/// channel set, not paused). Otherwise ONE feed request per interval for the followed account — never per guild — where
/// the interval is the project default or the feed's own RSS &lt;ttl&gt;, whichever is longer. Failures back off
/// exponentially (429 honours Retry-After; 403 waits hours and is never worked around). The next poll time lives in the
/// database, so a restart neither resets a backoff nor causes an extra request. Rounds never overlap. Any failure here stays
/// in this module: other modules and the outbox are unaffected.
/// </summary>
public sealed class LetterboxdPoller(
    IServiceScopeFactory scopes,
    LetterboxdRssClient feed,
    IOptions<LetterboxdOptions> options,
    TimeProvider clock,
    ILogger<LetterboxdPoller> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);
    public static readonly TimeSpan BlockedBackoff = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _round = new(1, 1);
    private int _lastFailuresLogged = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The mode and the instant it became active are durable before any request (an entry the feed dates after it is new,
        // however late the first successful read comes).
        await RecordModeAsync(stoppingToken);
        if (options.Value.Mode == LetterboxdMode.Off)
        {
            logger.LogInformation("TSQ Letterboxd is off (Letterboxd:Mode=Off): no feed requests, no cards");
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
                logger.LogError(ex, "letterboxd round failed; other modules are unaffected, retrying on the next tick");
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

    /// <summary>One tick: a feed round if one is due. Returns false when nothing was requested.</summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        if (options.Value.Mode == LetterboxdMode.Off)
        {
            await RecordModeAsync(ct);
            return false;
        }

        if (!await _round.WaitAsync(0, ct))
            return false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var planner = scope.ServiceProvider.GetRequiredService<LetterboxdPlanner>();
            await planner.RecordModeAsync(ct);
            if (!await planner.HasActiveGuildAsync(ct))
                return false;
            var state = await planner.StateAsync(ct);
            var now = clock.GetUtcNow();
            if (state.NextPollAt is { } due && now < due)
                return false;

            // Validators only after a baseline: a 304 is meaningful only against content we stored.
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

            var failuresBefore = state.ConsecutiveFailures; // the tracked row is updated by ApplyAsync
            var next = NextPoll(result, failuresBefore + (result.Succeeded ? 0 : 1), result.TtlMinutes ?? state.TtlMinutes);
            var summary = await planner.ApplyAsync(result, next, ct);
            Log(result, summary, failuresBefore, next);
            return true;
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
        if (result.Outcome is FeedOutcome.Blocked or FeedOutcome.NotFound)
            return now + BlockedBackoff;
        var backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, (long)(interval.Ticks * Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 8)))));
        if (result.RetryAfter is { } retryAfter && retryAfter > backoff)
            backoff = retryAfter;
        return now + backoff;
    }

    private async Task RecordModeAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LetterboxdPlanner>().RecordModeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "letterboxd: could not record the mode");
        }
    }

    private void Log(FeedFetchResult result, LetterboxdRoundSummary summary, int failuresBefore, DateTimeOffset next)
    {
        if (result.Succeeded)
        {
            if (failuresBefore > 0)
                logger.LogInformation("letterboxd feed recovered after {Failures} failed attempt(s)", failuresBefore);
            if (summary.Staged > 0 || summary.Edits > 0)
                logger.LogInformation("letterboxd round: {Items} entries, {New} new, {Staged} card(s) staged, {Edits} edit(s)",
                    summary.Items, summary.NewItems, summary.Staged, summary.Edits);
            else
                logger.LogDebug("letterboxd round: {Outcome} {Items} entries, {New} new, nothing to post", result.Outcome, summary.Items, summary.NewItems);
            _lastFailuresLogged = -1;
            return;
        }

        var failures = failuresBefore + 1;
        if (failures == 1 || failures % 10 == 0 || _lastFailuresLogged < 0)
            logger.LogWarning("letterboxd feed request failed: {Outcome} {Detail} (attempt {Failures}; next try {Next:u}; not treated as 'no entries')",
                result.Outcome, result.Detail, failures, next);
        _lastFailuresLogged = failures;
    }

    public override void Dispose()
    {
        _round.Dispose();
        base.Dispose();
    }
}
