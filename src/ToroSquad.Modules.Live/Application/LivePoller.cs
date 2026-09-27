using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Domain;
using ToroSquad.Modules.Live.Persistence;
using ToroSquad.Modules.Live.Providers;

namespace ToroSquad.Modules.Live.Application;

/// <summary>
/// Official-API reconciliation, the backbone of TSQ Live: every <see cref="LiveOptions.ReconciliationIntervalSeconds"/> one
/// batched request per configured provider (Twitch and Kick independently and concurrently — one provider's outage never
/// delays or changes the other's state). It catches every online/offline transition and title change, and a missed one is
/// corrected on the next round. Failures back off exponentially (honouring Retry-After / Ratelimit-Reset) and keep the
/// previous known state. Between rounds a short tick lets the coordinator end sessions whose grace expired. Nothing runs
/// while <see cref="LiveOptions.Enabled"/> is false. Logs: failures and recoveries at Warning/Information once per streak,
/// routine rounds at Debug only (no "still offline" spam).
/// </summary>
public sealed class LivePoller(
    IServiceScopeFactory scopes,
    IEnumerable<ILiveStatusProvider> providers,
    LiveCoordinator coordinator,
    LiveHealth health,
    IOptions<LiveOptions> options,
    TimeProvider clock,
    ILogger<LivePoller> logger) : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<ILiveStatusProvider> _providers = providers.OrderBy(p => p.Platform).ToList();
    private readonly Dictionary<LivePlatform, DateTimeOffset> _next = [];
    private readonly Dictionary<LivePlatform, string> _lastWarnings = [];

    public static string ProviderKey(LivePlatform platform) => platform.Key();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("TSQ Live is disabled (Live:Enabled=false): no provider requests, no announcements");
            try
            {
                await coordinator.PauseTrackingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "live tracking pause failed");
            }

            return;
        }

        try
        {
            await WarmUpAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "live warm-up failed");
        }

        foreach (var provider in _providers.Where(p => !p.IsConfigured))
            logger.LogWarning("live {Platform}: credentials not set — this platform is NOT tracked (state unknown, never offline)", provider.Platform);

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
                logger.LogError(ex, "live tick failed");
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

    /// <summary>One round: due providers are asked concurrently, their answers applied in ONE coordinator transaction.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var creators = o.TrackedCreators();
        var due = _providers.Where(p => p.IsConfigured && now >= _next.GetValueOrDefault(p.Platform, DateTimeOffset.MinValue)).ToList();
        var results = await Task.WhenAll(due.Select(p => PollAsync(p, creators, ct)));

        var observations = new List<LiveObservation>();
        foreach (var (provider, result) in due.Zip(results))
        {
            if (result.Succeeded)
                observations.AddRange(result.Observations);
            await RecordAsync(provider, result, now, ct);
        }

        await coordinator.ApplyAsync(observations, ct);
    }

    private async Task<LiveProviderResult> PollAsync(ILiveStatusProvider provider, IReadOnlyList<TrackedCreator> creators, CancellationToken ct)
    {
        var logins = creators.Select(c => c.Channel(provider.Platform)?.Login).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        try
        {
            return await provider.GetStatusAsync(logins, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A provider bug is a transport error — never "offline".
            logger.LogError(ex, "live {Platform} provider threw", provider.Platform);
            return LiveProviderResult.Fail(LiveProviderOutcome.TransportError, ex.GetType().Name, clock.GetUtcNow());
        }
    }

    private async Task RecordAsync(ILiveStatusProvider provider, LiveProviderResult result, DateTimeOffset attemptAt, CancellationToken ct)
    {
        var platform = provider.Platform;
        var previous = health.Feed(platform);
        var feed = health.Record(platform, result, attemptAt);
        _next[platform] = Next(result, feed.ConsecutiveFailures);

        if (result.Succeeded)
        {
            if (previous.ConsecutiveFailures > 0)
                logger.LogInformation("live {Platform} provider reconciliation recovered after {Failures} failed attempt(s)", platform, previous.ConsecutiveFailures);
            else
                logger.LogDebug("live {Platform} provider reconciliation completed ({Count} channel statements)", platform, result.Observations.Count);
            var warnings = string.Join("; ", result.Warnings ?? []);
            if (warnings != _lastWarnings.GetValueOrDefault(platform, ""))
            {
                if (warnings.Length > 0)
                    logger.LogWarning("live {Platform} provider did not describe every channel: {Warnings}", platform, warnings);
                _lastWarnings[platform] = warnings;
            }
        }
        else if (feed.ConsecutiveFailures == 1 || feed.ConsecutiveFailures % 20 == 0)
        {
            logger.LogWarning("live {Platform} provider request failed: {Outcome} {Detail} (attempt {Failures}; previous state kept, next try {Next:u})",
                platform, result.Outcome, result.Detail, feed.ConsecutiveFailures, _next[platform]);
        }
        else
        {
            logger.LogDebug("live {Platform} provider request failed again: {Outcome}", platform, result.Outcome);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var set = db.Set<LiveProviderStateEntity>();
        var key = ProviderKey(platform);
        var row = await set.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new LiveProviderStateEntity { Key = key };
            set.Add(row);
        }

        row.LastAttemptAt = feed.LastAttemptAt;
        row.LastSuccessAt = feed.LastSuccessAt;
        row.LastOutcome = (int)(feed.LastOutcome ?? LiveProviderOutcome.Ok);
        row.LastDetail = feed.LastDetail is { Length: > 300 } d ? d[..300] : feed.LastDetail;
        row.LastErrorAt = feed.LastErrorAt;
        row.ConsecutiveFailures = feed.ConsecutiveFailures;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Restores provider health after a restart (original times — freshness stays honest).</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var rows = await db.Set<LiveProviderStateEntity>().AsNoTracking().ToListAsync(ct);
        foreach (var platform in LivePlatforms.All)
        {
            if (rows.FirstOrDefault(r => r.Key == ProviderKey(platform)) is { } r)
                health.Restore(platform, new LiveFeed(r.LastAttemptAt, r.LastSuccessAt, (LiveProviderOutcome)r.LastOutcome, r.LastDetail, r.LastErrorAt, r.ConsecutiveFailures, []));
        }
    }

    private DateTimeOffset Next(LiveProviderResult result, int failures)
    {
        var now = clock.GetUtcNow();
        var interval = options.Value.ReconciliationInterval;
        if (result.Succeeded)
            return now + interval;
        if (result.Outcome is LiveProviderOutcome.NotConfigured or LiveProviderOutcome.AuthFailed)
            return now + TimeSpan.FromMinutes(5); // credentials problems are not retried aggressively
#pragma warning disable CA5394 // jitter, not security relevant
        var jitter = 0.85 + (Random.Shared.NextDouble() * 0.3);
#pragma warning restore CA5394
        var backoff = TimeSpan.FromSeconds(Math.Min(900, interval.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 6)) * jitter));
        if (backoff < interval)
            backoff = interval;
        return now + (result.RetryAfter is { } ra && ra > backoff ? ra : backoff);
    }
}
