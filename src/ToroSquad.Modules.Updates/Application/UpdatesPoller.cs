using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>
/// The updates loop. Nothing is requested while Updates:Mode is Off, or for a game no guild would receive cards for (module
/// enabled, channel set, not paused, game enabled). Otherwise ONE request per game per interval — never per guild — where
/// the interval is the project default, the provider's own minimum or the cache lifetime the source declared with its
/// answer, whichever is longest (asking again earlier would only return the same cached answer). Failures back off exponentially (429 honours
/// Retry-After). The next poll time lives in the database, so a restart neither resets a backoff nor causes an extra
/// request. Rounds never overlap, and a failing game or provider never stops another game — or anything outside this module.
/// </summary>
public sealed class UpdatesPoller(
    IServiceScopeFactory scopes,
    GameUpdateCatalog catalog,
    IOptions<UpdatesOptions> options,
    TimeProvider clock,
    ILogger<UpdatesPoller> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    /// <summary>A declared cache lifetime longer than this is not believed (the poll interval never grows beyond it).</summary>
    public static readonly TimeSpan MaxSourceCacheWait = TimeSpan.FromHours(2);

    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _requestedAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failuresLogged = new(StringComparer.Ordinal);
    private bool _modeEntered;
    private bool _modeFailureLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Also when Off: a guild planned in another mode before must start a new window when that mode returns. Nothing
            // else happens before the mode is recorded — retried for as long as it takes.
            while (!await TryEnterModeAsync(stoppingToken))
                await Task.Delay(TickInterval, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (options.Value.Mode == UpdatesMode.Off)
        {
            logger.LogInformation("TSQ Bot Updates is off (Updates:Mode=Off): no provider requests, no cards");
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
                logger.LogError(ex, "updates round failed; other modules are unaffected, retrying on the next tick");
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

    /// <summary>
    /// The startup step, once per process: records the running mode (see <see cref="UpdatesWindow.Enter"/>) and any game that
    /// was configured to another place at its provider (see <see cref="UpdatesPlanner.EnterSourcesAsync"/>).
    /// </summary>
    public async Task EnterModeAsync(CancellationToken ct)
    {
        if (_modeEntered)
            return;
        await using var scope = scopes.CreateAsyncScope();
        var planner = scope.ServiceProvider.GetRequiredService<UpdatesPlanner>();
        await planner.EnterModeAsync(ct);
        await planner.EnterSourcesAsync(ct); // a game configured to another place at its provider: the move is recorded now
        _modeEntered = true;
    }

    /// <summary>As <see cref="EnterModeAsync"/>, but a failure is logged (once) and reported instead of thrown.</summary>
    public async Task<bool> TryEnterModeAsync(CancellationToken ct)
    {
        try
        {
            await EnterModeAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_modeFailureLogged)
                logger.LogError(ex, "updates mode could not be recorded; nothing is requested or planned until it is (retrying)");
            _modeFailureLogged = true;
            return false;
        }
    }

    /// <summary>One tick: a round for every game that is due. Returns the number of games requested.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (options.Value.Mode == UpdatesMode.Off || !await _round.WaitAsync(0, ct))
            return 0;
        try
        {
            await EnterModeAsync(ct);
            var requested = 0;
            foreach (var game in catalog.Games)
            {
                try
                {
                    if (await RoundAsync(game, ct))
                        requested++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One game's failure (provider, classifier, database) never takes the others down.
                    logger.LogError(ex, "updates round for {Game} failed; other games and modules are unaffected", game.Key);
                }
            }

            return requested;
        }
        finally
        {
            _round.Release();
        }
    }

    private async Task<bool> RoundAsync(GameUpdateDefinition game, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var planner = scope.ServiceProvider.GetRequiredService<UpdatesPlanner>();
        if (!await planner.HasActiveGuildAsync(game, ct))
            return false;
        var state = await planner.StateAsync(game, ct);
        var now = clock.GetUtcNow();
        if (state.NextPollAt is { } due && now < due)
            return false;
        // If the round could not be stored (so no next poll time either), still never ask more often than the game's own
        // interval — the same one the next poll time is computed from.
        var provider = catalog.ProviderOf(game);
        if (_requestedAt.TryGetValue(game.Key, out var last) && now - last < Interval(provider.MinimumPollInterval) && now >= last)
            return false;
        _requestedAt[game.Key] = now;

        var failuresBefore = state.ConsecutiveFailures;
        UpdateFetchResult result;
        try
        {
            // Reduced to this game's own posts first, so an answer without any is scheduled as the failure it is.
            var context = await planner.FetchContextAsync(game, ct);
            result = (await provider.FetchAsync(game, context, ct)).ForGame(game);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result = UpdateFetchResult.Fail(UpdateFetchOutcome.TransportError, null, ex.GetType().Name);
        }

        var next = NextPoll(result, failuresBefore + (result.Succeeded ? 0 : 1), provider.MinimumPollInterval);
        var summary = await planner.ApplyAsync(game, result, next, ct);
        Log(game, result, summary, failuresBefore, next);
        return true;
    }

    /// <summary>
    /// The time between two rounds of a game while its source answers: the module's interval or the provider's own minimum,
    /// whichever is longer. The one place this is decided — the next poll time, the backoff and the in-memory guard use it.
    /// </summary>
    public TimeSpan Interval(TimeSpan? providerMinimum)
    {
        var interval = options.Value.PollInterval;
        return providerMinimum is { } minimum && minimum > interval ? minimum : interval;
    }

    public DateTimeOffset NextPoll(UpdateFetchResult result, int consecutiveFailures, TimeSpan? providerMinimum = null)
    {
        var now = clock.GetUtcNow();
        var interval = Interval(providerMinimum);
        if (result.Succeeded)
        {
            if (result.CacheLifetime is { } lifetime && lifetime > interval)
                interval = lifetime < MaxSourceCacheWait ? lifetime : MaxSourceCacheWait;
            return now + interval;
        }

        var backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, (long)(interval.Ticks * Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 8)))));
        if (result.RetryAfter is { } retryAfter && retryAfter > backoff)
            backoff = retryAfter;
        return now + backoff;
    }

    private void Log(GameUpdateDefinition game, UpdateFetchResult result, UpdatesRoundSummary summary, int failuresBefore, DateTimeOffset next)
    {
        if (result.Succeeded)
        {
            if (failuresBefore > 0)
                logger.LogInformation("{Game} update source recovered after {Failures} failed attempt(s)", game.Key, failuresBefore);
            if (summary.Staged > 0 || summary.Edits > 0)
                logger.LogInformation("{Game} updates round: {Items} posts, {New} new, {Staged} card(s) staged, {Edits} edit(s)",
                    game.Key, summary.Items, summary.NewItems, summary.Staged, summary.Edits);
            else
                logger.LogDebug("{Game} updates round: {Outcome} {Items} posts, {New} new, nothing to post", game.Key, result.Outcome, summary.Items, summary.NewItems);
            _failuresLogged.TryRemove(game.Key, out _);
            return;
        }

        var failures = failuresBefore + 1;
        if (failures == 1 || failures % 10 == 0 || !_failuresLogged.ContainsKey(game.Key))
            logger.LogWarning("{Game} update source request failed: {Outcome} {Detail} (attempt {Failures}; next try {Next:u}; not treated as 'no updates')",
                game.Key, result.Outcome, result.Detail, failures, next);
        _failuresLogged[game.Key] = failures;
    }

    public override void Dispose()
    {
        _round.Dispose();
        base.Dispose();
    }
}
