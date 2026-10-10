using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Domain;
using ToroSquad.Modules.Live.Providers;

namespace ToroSquad.Modules.Live.Application;

/// <summary>
/// The single entry point that changes TSQ Live state. Observations (from any source) and time ticks are applied one at a
/// time (process-wide lock; the bot is single-instance), and state + outbox rows are committed in ONE SQLite transaction —
/// a crash can never leave "session announced" without its announcement row or the other way round. A provider failure
/// is never an observation, so it can never end a session or mark a channel offline.
/// </summary>
#pragma warning disable CA1001 // process-lifetime singleton; the semaphore needs no disposal
public sealed class LiveCoordinator(
    IServiceScopeFactory scopes,
    IEnumerable<ILiveStatusProvider> providers,
    IOptions<LiveOptions> options,
    DeploymentPolicy deployment,
    TimeProvider clock,
    ILogger<LiveCoordinator> logger)
{
#pragma warning disable CA2213 // lives as long as the process
    private readonly SemaphoreSlim _gate = new(1, 1);
#pragma warning restore CA2213
    private readonly IReadOnlyList<ILiveStatusProvider> _providers = providers.ToList();

    /// <summary>Platforms whose provider is configured: only these must confirm "offline" before a session ends.</summary>
    public IReadOnlyCollection<LivePlatform> TrackedPlatforms => _providers.Where(p => p.IsConfigured).Select(p => p.Platform).ToList();

    public Task<IReadOnlyList<LiveEffect>> TickAsync(CancellationToken ct) => ApplyAsync([], ct);

    /// <summary>
    /// Called when the host starts with Live:Enabled=false. Nothing is observed while tracking is switched off, so what is
    /// known becomes stale: every channel returns to "never observed" (its next observation is a baseline, never an
    /// announcement) and open sessions are closed. Unlike a crash or deploy, a deliberate switch-off therefore never leads
    /// to an announcement of a stream that started meanwhile.
    /// </summary>
    public async Task PauseTrackingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            var now = clock.GetUtcNow();
            var platforms = await db.Set<PlatformState>().Where(p => p.Status != PlatformStatus.Unknown).ToListAsync(ct);
            var creators = await db.Set<CreatorState>().Where(c => c.Phase != CreatorPhase.Offline).ToListAsync(ct);
            foreach (var creator in creators)
            {
                creator.SessionEndedAt = creator.GraceSince
                                         ?? platforms.Where(p => p.CreatorKey == creator.CreatorKey).Max(p => p.LastLiveAt)
                                         ?? now;
                creator.Phase = CreatorPhase.Offline;
                creator.GraceSince = null;
                creator.UpdatedAt = now;
            }

            foreach (var row in platforms)
                ResetChannel(row, row.Login, now, keepMetadata: true); // the last title still labels the closed card
            await db.SaveChangesAsync(ct);
            if (platforms.Count + creators.Count > 0)
                logger.LogInformation("live tracking switched off: {Channels} channel(s) will be re-baselined, {Sessions} open session(s) closed", platforms.Count, creators.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LiveEffect>> ApplyAsync(IReadOnlyList<LiveObservation> observations, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var effects = await ApplyOnceAsync(scope.ServiceProvider, observations, ct);
                    Log(effects);
                    return effects;
                }
                catch (DbUpdateConcurrencyException) when (attempt < 3)
                {
                    // The dispatcher updated the announcement's outbox row meanwhile: nothing was saved; recompute from the DB.
                    logger.LogDebug("live coordinator conflict with the outbox dispatcher; retrying (attempt {Attempt})", attempt + 1);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<LiveEffect>> ApplyOnceAsync(IServiceProvider sp, IReadOnlyList<LiveObservation> observations, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var db = sp.GetRequiredService<ToroDbContext>();
        var creators = o.TrackedCreators();
        var states = await db.Set<CreatorState>().ToListAsync(ct);
        var platforms = await db.Set<PlatformState>().ToListAsync(ct);
        var categories = await LoadCurrentCategoriesAsync(db, states, ct);
        var effects = new List<LiveEffect>();

        foreach (var creator in creators)
        {
            if (states.All(s => s.CreatorKey != creator.Key))
            {
                var state = new CreatorState { CreatorKey = creator.Key, UpdatedAt = now };
                db.Add(state);
                states.Add(state);
            }

            foreach (var channel in creator.Channels)
            {
                var row = platforms.FirstOrDefault(p => p.CreatorKey == creator.Key && p.Platform == channel.Platform);
                if (row is null)
                {
                    row = new PlatformState { CreatorKey = creator.Key, Platform = channel.Platform, Login = channel.Login, UpdatedAt = now };
                    db.Add(row);
                    platforms.Add(row);
                }
                else if (row.Login != channel.Login)
                {
                    // Another channel is configured now: its first observation is a fresh baseline (never announced).
                    ResetChannel(row, channel.Login, now);
                }
            }
        }

        var guild = o.ResolveGuild(deployment);
        ChannelId? channelId = o.DiscordChannelId != 0 ? new ChannelId(o.DiscordChannelId) : null;
        var announcementsAllowed = o.Enabled && guild is { } g && channelId is not null && deployment.IsGuildAllowed(g) &&
                                   await sp.GetRequiredService<IModuleGate>().IsEnabledAsync(g, LiveModule.ModuleIdTyped, ct);

        // Bring every current card up to date first: a session closed while nothing was planned (tracking switched off)
        // gets its "ended" edit before a new session of the same creator takes its place below.
        var planner = sp.GetRequiredService<LiveAnnouncementPlanner>();
        await planner.PlanAsync(creators, states, platforms, categories, guild, channelId, ct);

        var rules = o.Rules;
        foreach (var observation in observations)
        {
            var creator = creators.FirstOrDefault(c => c.Channel(observation.Platform)?.Login == observation.Login);
            if (creator is null)
                continue; // not a tracked channel (never happens for our own requests)
            var state = states.Single(s => s.CreatorKey == creator.Key);
            effects.AddRange(LiveStateMachine.Apply(state, Channels(platforms, creator), observation, rules, announcementsAllowed, now));
            RecordCategory(db, categories, state, platforms.First(p => p.CreatorKey == creator.Key && p.Platform == observation.Platform), observation);
        }

        var tracked = TrackedPlatforms;
        foreach (var creator in creators)
            effects.AddRange(LiveStateMachine.Tick(states.Single(s => s.CreatorKey == creator.Key), Channels(platforms, creator), tracked, rules, now));

        await planner.PlanAsync(creators, states, platforms, categories, guild, channelId, ct);
        await db.SaveChangesAsync(ct);
        await PruneCategoriesAsync(db, states, effects, ct);
        return effects;
    }

    /// <summary>How many sessions per creator keep their category history (older rows are deleted when a session starts).</summary>
    public const int CategoryHistorySessions = 50;

    /// <summary>The category rows of each creator's CURRENT session (tracked: additions are saved with the state).</summary>
    private static async Task<List<SessionCategory>> LoadCurrentCategoriesAsync(ToroDbContext db, List<CreatorState> states, CancellationToken ct)
    {
        var categories = new List<SessionCategory>();
        foreach (var state in states.Where(s => s.SessionNumber > 0))
        {
            var key = state.CreatorKey;
            var session = state.SessionNumber;
            categories.AddRange(await db.Set<SessionCategory>().Where(c => c.CreatorKey == key && c.SessionNumber == session).ToListAsync(ct));
        }

        return categories;
    }

    /// <summary>
    /// Adds the category of a live statement to the creator's current session (first seen first, each once). Only a
    /// statement that was just applied as the newest one for a channel that is live counts: a stale, failed, malformed or
    /// offline answer never adds, changes or removes anything. The starting category of a session is recorded by the very
    /// observation that opened it; a reconnect inside the grace keeps the same session and therefore the same list.
    /// </summary>
    private static void RecordCategory(ToroDbContext db, List<SessionCategory> categories, CreatorState state, PlatformState platform, LiveObservation o)
    {
        if (o.Category is null && o.CategoryId is null)
            return;
        var applied = o.Kind == ObservationKind.Status ? platform.StatusObservedAt == o.ObservedAt : platform.MetadataObservedAt == o.ObservedAt;
        if (!applied || state.Phase != CreatorPhase.Live || platform.Status != PlatformStatus.Live || state.SessionNumber == 0)
            return;
        var session = categories.Where(c => c.CreatorKey == state.CreatorKey && c.SessionNumber == state.SessionNumber).ToList();
        if (SessionCategories.Record(session, state.CreatorKey, state.SessionNumber, o.Platform, o.CategoryId, o.Category, o.ObservedAt) is { } added)
        {
            categories.Add(added);
            db.Add(added);
        }
    }

    /// <summary>Bounded growth: when a session starts, history older than <see cref="CategoryHistorySessions"/> sessions goes.</summary>
    private async Task PruneCategoriesAsync(ToroDbContext db, List<CreatorState> states, List<LiveEffect> effects, CancellationToken ct)
    {
        foreach (var key in effects.Where(e => e.Kind == LiveEffectKind.SessionStarted).Select(e => e.CreatorKey).Distinct(StringComparer.Ordinal))
        {
            var oldest = states.Single(s => s.CreatorKey == key).SessionNumber - CategoryHistorySessions;
            if (oldest <= 0)
                continue;
            try
            {
                await db.Set<SessionCategory>().Where(c => c.CreatorKey == key && c.SessionNumber <= oldest).ExecuteDeleteAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "live category history cleanup failed creator={Creator}", key);
            }
        }
    }

    private static List<PlatformState> Channels(List<PlatformState> platforms, TrackedCreator creator) =>
        platforms.Where(p => p.CreatorKey == creator.Key && creator.Channel(p.Platform) is not null).ToList();

    private static void ResetChannel(PlatformState row, string login, DateTimeOffset now, bool keepMetadata = false)
    {
        row.Login = login;
        row.Status = PlatformStatus.Unknown;
        row.StreamId = null;
        row.StartedAt = null;
        row.StatusObservedAt = null;
        row.LastEventId = null;
        row.UpdatedAt = now;
        if (keepMetadata)
            return;
        row.Title = null;
        row.TitleChangedAt = null;
        row.Category = null;
        row.AvatarUrl = null;
        row.MetadataObservedAt = null;
    }

    private void Log(IReadOnlyList<LiveEffect> effects)
    {
        foreach (var e in effects)
        {
            switch (e.Kind)
            {
                case LiveEffectKind.SessionStarted:
                    logger.LogInformation("live creator session started creator={Creator} platform={Platform} decision={Decision}", e.CreatorKey, e.Platform, e.Detail);
                    break;
                case LiveEffectKind.PlatformJoined:
                    logger.LogInformation("live platform joined active session creator={Creator} platform={Platform} (card edit, no mention)", e.CreatorKey, e.Platform);
                    break;
                case LiveEffectKind.TitleChanged:
                    logger.LogInformation("live title changed creator={Creator} platform={Platform} (card edit, no mention)", e.CreatorKey, e.Platform);
                    break;
                case LiveEffectKind.PlatformWentOffline:
                    logger.LogInformation("live platform went offline creator={Creator} platform={Platform}", e.CreatorKey, e.Platform);
                    break;
                case LiveEffectKind.GraceEntered:
                    logger.LogInformation("live reconnect grace entered creator={Creator} ({Detail})", e.CreatorKey, e.Detail ?? "all platforms offline");
                    break;
                case LiveEffectKind.ReconnectedWithinGrace when e.Detail is null or "same_stream":
                    logger.LogInformation("live reconnect within grace creator={Creator} platform={Platform}: same session, no new announcement", e.CreatorKey, e.Platform);
                    break;
                case LiveEffectKind.SessionEnded:
                    logger.LogInformation("live session ended creator={Creator} ({Detail})", e.CreatorKey, e.Detail ?? "grace expired, all platforms offline");
                    break;
                case LiveEffectKind.DuplicateIgnored:
                    logger.LogInformation("live duplicate event ignored creator={Creator} platform={Platform}", e.CreatorKey, e.Platform);
                    break;
                case LiveEffectKind.StaleIgnored:
                    logger.LogInformation("live stale {Detail} statement ignored creator={Creator} platform={Platform}", e.Detail, e.CreatorKey, e.Platform);
                    break;
                default:
                    logger.LogDebug("live {Effect} creator={Creator} platform={Platform} {Detail}", e.Kind, e.CreatorKey, e.Platform, e.Detail);
                    break;
            }
        }
    }
}
#pragma warning restore CA1001
