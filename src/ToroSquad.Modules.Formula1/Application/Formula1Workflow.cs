using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

public enum F1ResultApplyOutcome
{
    ProviderFailed = 0,
    NotPublishedYet = 1,
    Incomplete = 2,
    Finalised = 3,
    Corrected = 4,
    Unchanged = 5,
    UnknownSession = 6,
}

/// <summary>
/// Persists everything the notification rules depend on (docs/FORMULA1.md): shared session snapshots, provider id
/// mappings, lifecycle transitions, canonical results and standings. It never talks to Discord and never stages
/// notifications (that is <see cref="Formula1NotificationPlanner"/>), so every decision below survives restarts.
/// </summary>
public sealed class Formula1Workflow(ToroDbContext db, IOptions<Formula1Options> options, TimeProvider clock, ILogger<Formula1Workflow> logger)
{
    private static readonly int[] ResultRetryMinutes = [2, 3, 5, 5, 10, 10, 15];
    private static readonly int[] StandingsCheckMinutes = [5, 5, 10, 15, 20, 30];

    private DbSet<F1SessionSnapshotEntity> Sessions => db.Set<F1SessionSnapshotEntity>();

    // ---------------------------------------------------------------- schedule

    /// <summary>
    /// Upserts the schedule. A session first seen when it is already OVER (its planned end has passed: first install, new
    /// season appearing late, provider switch) becomes a silent baseline: never announced, not even its result.
    /// A session first seen after its scheduled start but before its planned end (delayed, or running) is NOT baseline:
    /// a provider-confirmed start is then still subject to the guild watermark and the start freshness window (so enabling
    /// mid-session never produces a stale "started"), and its result is delivered if it finalises after the guild's
    /// watermark. Fail closed: a session first seen after its planned end is baseline even if it is still running.
    /// Sessions missing from a later schedule are kept as they are — a provider omission is not a cancellation.
    /// </summary>
    public async Task<int> UpsertScheduleAsync(F1SeasonSchedule schedule, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var keys = schedule.Sessions.Select(s => s.Key).ToList();
        var existing = await Sessions.Where(s => keys.Contains(s.SessionKey)).ToDictionaryAsync(s => s.SessionKey, StringComparer.Ordinal, ct);
        var baselined = 0;
        foreach (var meeting in schedule.Meetings)
        {
            foreach (var session in meeting.Sessions)
            {
                if (!existing.TryGetValue(session.Key, out var row))
                {
                    row = new F1SessionSnapshotEntity
                    {
                        SessionKey = session.Key,
                        MeetingKey = session.MeetingKey,
                        Season = session.Season,
                        Round = session.Round,
                        SessionType = (int)session.Type,
                        State = (int)F1SessionState.Scheduled,
                        IsBaseline = session.PlannedEndUtc <= now,
                        FirstSeenAt = now,
                    };
                    Sessions.Add(row);
                    existing[session.Key] = row;
                    if (row.IsBaseline)
                        baselined++;
                }
                else if (row.ScheduledStartUtc != session.ScheduledStartUtc && (F1SessionState)row.State is F1SessionState.Scheduled or F1SessionState.Unknown)
                {
                    logger.LogInformation("F1 schedule: {Session} start moved {Old:u} -> {New:u}", row.SessionKey, row.ScheduledStartUtc, session.ScheduledStartUtc);
                }

                if ((F1SessionState)row.State is F1SessionState.Scheduled or F1SessionState.Unknown || row.ScheduledStartUtc == default)
                    row.ScheduledStartUtc = session.ScheduledStartUtc;
                row.ScheduledEndUtc = session.ScheduledEndUtc ?? row.ScheduledEndUtc;
                row.MeetingName = Clip(meeting.MeetingName, 200);
                row.CircuitName = Clip(meeting.CircuitName, 200);
                row.Country = meeting.Country is null ? null : Clip(meeting.Country, 100);
                row.Location = meeting.Location is null ? null : Clip(meeting.Location, 100);
                row.LastSeenAt = now;
                row.UpdatedAt = now;
            }
        }

        if (baselined > 0)
            logger.LogInformation("F1 bootstrap: {Count} already-finished session(s) of {Season} recorded as baseline (never announced)", baselined, schedule.Season);
        await db.SaveChangesAsync(ct);
        return keys.Count;
    }

    // ---------------------------------------------------------------- provider id mapping

    /// <summary>
    /// Maps provider sessions onto snapshots (season + type + start within tolerance, unique in BOTH directions).
    /// Ambiguous or missing matches stay unmapped: such a session gets no lifecycle and no result (fail closed).
    /// Provider-stated cancellation is applied for lifecycle providers.
    /// </summary>
    public async Task<int> MapProviderSessionsAsync(string providerId, IReadOnlyList<F1ProviderSession> providerSessions, bool lifecycle, CancellationToken ct)
    {
        if (providerSessions.Count == 0)
            return 0;
        var seasons = providerSessions.Select(p => p.Season).Distinct().ToList();
        var rows = await Sessions.Where(s => seasons.Contains(s.Season)).ToListAsync(ct);
        var schedule = rows.Select(ToSession).ToList();
        var tolerance = TimeSpan.FromHours(options.Value.SessionMatchToleranceHours);

        var matches = providerSessions
            .Select(p => (Provider: p, Session: F1SessionMatcher.Match(schedule, p.Season, p.Type, p.StartUtc, tolerance)))
            .Where(m => m.Session is not null)
            .GroupBy(m => m.Session!.Key)
            .ToList();

        var mapped = 0;
        var now = clock.GetUtcNow();
        foreach (var group in matches)
        {
            if (group.Count() != 1)
            {
                logger.LogWarning("F1 mapping: {Session} matches {Count} {Provider} sessions — left unmapped", group.Key, group.Count(), providerId);
                continue;
            }

            var p = group.Single().Provider;
            var row = rows.Single(r => r.SessionKey == group.Key);
            var currentProvider = lifecycle ? row.LifecycleProvider : row.ResultsProvider;
            var currentRef = lifecycle ? row.LifecycleProviderRef : row.ResultsProviderRef;
            if (currentProvider != providerId || currentRef != p.Ref)
            {
                if (currentRef is not null && currentProvider == providerId)
                    logger.LogWarning("F1 mapping: {Session} {Provider} id changed {Old} -> {New}", row.SessionKey, providerId, currentRef, p.Ref);
                if (lifecycle)
                {
                    row.LifecycleProvider = providerId;
                    row.LifecycleProviderRef = p.Ref;
                }
                else
                {
                    row.ResultsProvider = providerId;
                    row.ResultsProviderRef = p.Ref;
                }

                mapped++;
            }

            if (p.EndUtc is { } end && row.ScheduledEndUtc is null && end > row.ScheduledStartUtc)
                row.ScheduledEndUtc = end;

            if (lifecycle && p.IsCancelled && (F1SessionState)row.State is F1SessionState.Scheduled or F1SessionState.Unknown)
            {
                row.State = (int)F1SessionState.Cancelled;
                row.CancelledObservedAt ??= now;
                logger.LogInformation("F1 lifecycle: {Session} cancelled by {Provider}", row.SessionKey, providerId);
            }

            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return mapped;
    }

    // ---------------------------------------------------------------- lifecycle

    public sealed record AppliedTransition(string SessionKey, F1TransitionKind Kind, F1SessionState NewState, DateTimeOffset OccurredAt);

    /// <summary>
    /// Applies provider lifecycle events through <see cref="F1LifecycleMachine"/>, oldest first. Unmapped provider ids are
    /// ignored (never guessed); duplicates and out-of-order replays are suppressed by the last applied provider timestamp.
    /// </summary>
    public async Task<IReadOnlyList<AppliedTransition>> ApplyLifecycleAsync(string providerId, IReadOnlyCollection<F1LifecycleEvent> events, CancellationToken ct)
    {
        var applied = new List<AppliedTransition>();
        if (events.Count == 0)
            return applied;
        var refs = events.Select(e => e.ProviderSessionRef).Distinct().ToList();
        var rows = await Sessions.Where(s => s.LifecycleProvider == providerId && refs.Contains(s.LifecycleProviderRef!)).ToListAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var group in events.GroupBy(e => e.ProviderSessionRef))
        {
            var row = rows.FirstOrDefault(r => r.LifecycleProviderRef == group.Key);
            if (row is null)
            {
                logger.LogDebug("F1 lifecycle: event for unmapped {Provider} session {Ref} ignored", providerId, group.Key);
                continue;
            }

            foreach (var e in group.OrderBy(e => e.OccurredAt))
            {
                if (row.LastLifecycleEventAt is { } last && e.OccurredAt <= last)
                {
                    logger.LogDebug("F1 lifecycle: duplicate/out-of-order {Signal} for {Session} suppressed", e.Signal, row.SessionKey);
                    continue;
                }

                var type = (F1SessionType)row.SessionType;
                var t = F1LifecycleMachine.Apply(type, (F1SessionState)row.State, row.LastLifecycleEventAt, e);
                row.LastLifecycleEventAt = e.OccurredAt;
                switch (t.Kind)
                {
                    case F1TransitionKind.FirstStart:
                        row.StartedObservedAt ??= e.OccurredAt;
                        row.StartedRecordedAt ??= now;
                        break;
                    case F1TransitionKind.Resume:
                        row.ResumeCount++;
                        break;
                    case F1TransitionKind.Suspend:
                        row.SuspendedObservedAt = e.OccurredAt;
                        break;
                    case F1TransitionKind.Finish:
                        row.FinishedObservedAt ??= e.OccurredAt;
                        row.ResultNextAttemptAt = null; // due now (subject to the results provider's availability delay)
                        row.ResultAttempts = 0;
                        break;
                    case F1TransitionKind.Cancel:
                        row.CancelledObservedAt ??= e.OccurredAt;
                        break;
                }

                if (t.Changed)
                {
                    row.State = (int)t.NewState;
                    row.UpdatedAt = now;
                    applied.Add(new AppliedTransition(row.SessionKey, t.Kind, t.NewState, e.OccurredAt));
                    logger.LogInformation("F1 lifecycle: {Session} {Kind} -> {State} (provider time {At:u}, {Reason})", row.SessionKey, t.Kind, t.NewState, e.OccurredAt, t.Reason);
                }
                else
                {
                    logger.LogDebug("F1 lifecycle: {Signal} for {Session} ignored: {Reason}", e.Signal, row.SessionKey, t.Reason);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return applied;
    }

    // ---------------------------------------------------------------- results

    /// <summary>
    /// Records one results-provider answer for a session. Only a complete, valid classification finalises a session;
    /// failures, "not published yet" and incomplete data schedule a bounded retry and change nothing else.
    /// </summary>
    public async Task<F1ResultApplyOutcome> ApplyResultAsync(string sessionKey, F1ProviderResult<F1SessionResult> result, CancellationToken ct)
    {
        var row = await Sessions.FirstOrDefaultAsync(s => s.SessionKey == sessionKey, ct);
        if (row is null)
            return F1ResultApplyOutcome.UnknownSession;
        var now = clock.GetUtcNow();
        var o = options.Value;

        if (!result.Succeeded || result.Value is null)
        {
            row.ResultAttempts++;
            var wait = Backoff(ResultRetryMinutes, row.ResultAttempts);
            if (result.RetryAfter is { } ra && ra > wait)
                wait = ra;
            row.ResultNextAttemptAt = now + wait;
            row.ResultLastDetail = Clip(result.Succeeded ? "not published yet" : result.Outcome + ": " + result.Detail, 200);
            await db.SaveChangesAsync(ct);
            return result.Succeeded ? F1ResultApplyOutcome.NotPublishedYet : F1ResultApplyOutcome.ProviderFailed;
        }

        var value = result.Value;
        if (value.SessionKey != row.SessionKey || value.Type != (F1SessionType)row.SessionType)
            throw new InvalidOperationException("Result does not belong to session " + row.SessionKey);
        if (F1ResultValidator.Problem(value, o.MinResultEntries) is { } problem)
        {
            row.ResultAttempts++;
            row.ResultNextAttemptAt = now + Backoff(ResultRetryMinutes, row.ResultAttempts);
            row.ResultLastDetail = Clip("incomplete: " + problem, 200);
            logger.LogInformation("F1 results: {Session} classification not publishable yet ({Problem})", row.SessionKey, problem);
            await db.SaveChangesAsync(ct);
            return F1ResultApplyOutcome.Incomplete;
        }

        var hash = value.CanonicalHash();
        var json = F1Json.Serialize(value);
        var set = db.Set<F1ResultSnapshotEntity>();
        var stored = await set.FirstOrDefaultAsync(r => r.SessionKey == sessionKey, ct);
        F1ResultApplyOutcome outcome;
        if (stored is null)
        {
            set.Add(new F1ResultSnapshotEntity
            {
                SessionKey = sessionKey,
                Provider = value.Source,
                CanonicalHash = hash,
                PayloadJson = json,
                FirstAvailableAt = now,
                LastChangedAt = now,
                FetchedAt = now,
            });
            row.FinalisedObservedAt ??= now;
            if ((F1SessionState)row.State != F1SessionState.Cancelled)
                row.State = (int)F1SessionState.Finalised;
            outcome = F1ResultApplyOutcome.Finalised;
            logger.LogInformation("F1 results: {Session} finalised ({Entries} entries, {Source})", row.SessionKey, value.Entries.Count, value.Source);

            if (F1SessionTypes.AwardsChampionshipPoints((F1SessionType)row.SessionType) && !row.IsBaseline)
            {
                await CaptureStandingsBaselineAsync(row, ct);
                row.StandingsWatchUntil = now + TimeSpan.FromMinutes(o.StandingsSettleWindowMinutes);
                row.StandingsNextCheckAt = now;
                row.StandingsChecks = 0;
            }
        }
        else
        {
            stored.FetchedAt = now;
            if (stored.CanonicalHash != hash)
            {
                stored.CanonicalHash = hash;
                stored.PayloadJson = json;
                stored.Provider = value.Source;
                stored.LastChangedAt = now;
                stored.Corrections++;
                outcome = F1ResultApplyOutcome.Corrected;
                logger.LogInformation("F1 results: correction detected for {Session} (correction #{Count})", row.SessionKey, stored.Corrections);
            }
            else
            {
                outcome = F1ResultApplyOutcome.Unchanged;
            }
        }

        row.ResultAttempts = 0;
        row.ResultLastDetail = null;
        var finalisedAt = row.FinalisedObservedAt ?? now;
        var inCorrectionWindow = now - finalisedAt < TimeSpan.FromHours(o.ResultCorrectionHours);
        // Corrections: every 20 min for the first 3 h, then hourly, until the correction window closes.
        row.ResultNextAttemptAt = !row.IsBaseline && inCorrectionWindow
            ? now + (now - finalisedAt < TimeSpan.FromHours(3) ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(1))
            : null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    // ---------------------------------------------------------------- standings

    public sealed record StandingsApplied(long SnapshotId, bool Changed, IReadOnlyList<string> AttachedTo);

    /// <summary>
    /// Stores a provider standings table (a new row only when its canonical hash changes) and attaches it to every
    /// sprint/race result whose settle window is open and whose pre-session baseline differs. Points are never computed.
    /// </summary>
    public async Task<StandingsApplied> ApplyStandingsAsync(F1StandingsSnapshot snapshot, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = snapshot.CanonicalHash();
        var set = db.Set<F1StandingsSnapshotEntity>();
        var latest = await set.Where(s => s.Kind == (int)snapshot.Kind && s.Season == snapshot.Season).OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        var changed = latest is null || latest.CanonicalHash != hash;
        if (changed)
        {
            latest = new F1StandingsSnapshotEntity
            {
                Kind = (int)snapshot.Kind,
                Season = snapshot.Season,
                Round = snapshot.Round,
                Provider = snapshot.Source,
                CanonicalHash = hash,
                PayloadJson = F1Json.Serialize(snapshot),
                FetchedAt = now,
                LastConfirmedAt = now,
            };
            set.Add(latest);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("F1 standings: {Kind} {Season} changed (after round {Round})", snapshot.Kind, snapshot.Season, snapshot.Round);
        }
        else
        {
            latest!.LastConfirmedAt = now;
        }

        var attached = new List<string>();
        var watching = await Sessions.Where(s => s.Season == snapshot.Season && s.StandingsWatchUntil != null && !s.StandingsWindowClosed).ToListAsync(ct);
        foreach (var row in watching.Where(r => r.StandingsWatchUntil >= now && snapshot.Count > 0))
        {
            // A baseline may be missing if this session finalised before one could be proven (e.g. after downtime).
            await CaptureStandingsBaselineAsync(row, ct);
            var baseline = snapshot.Kind == F1StandingsKind.Drivers ? row.StandingsBaselineDriversHash : row.StandingsBaselineConstructorsHash;
            if (baseline is null || baseline == hash)
                continue; // unknown baseline fails closed; unchanged means the provider has not updated yet
            if (await AttributionProblemAsync(row, snapshot, latest, ct) is { } problem)
            {
                logger.LogInformation("F1 standings: {Kind} change not attached to {Session}: {Problem}", snapshot.Kind, row.SessionKey, problem);
                continue;
            }

            var current = snapshot.Kind == F1StandingsKind.Drivers ? row.StandingsDriversSnapshotId : row.StandingsConstructorsSnapshotId;
            if (current == latest.Id)
                continue;
            if (snapshot.Kind == F1StandingsKind.Drivers)
                row.StandingsDriversSnapshotId = latest.Id;
            else
                row.StandingsConstructorsSnapshotId = latest.Id;
            row.StandingsAttachedAt = now;
            row.UpdatedAt = now;
            attached.Add(row.SessionKey);
            logger.LogInformation("F1 standings: {Kind} update attached to {Session} result", snapshot.Kind, row.SessionKey);
        }

        await db.SaveChangesAsync(ct);
        return new StandingsApplied(latest.Id, changed, attached);
    }

    /// <summary>
    /// Before this instant no points of the session can be in any standings table: the earlier of the observed first start
    /// and the scheduled start (a session never scores before it starts; an early start moves the cutoff earlier).
    /// </summary>
    public static DateTimeOffset BaselineCutoff(F1SessionSnapshotEntity row) =>
        row.StartedObservedAt is { } started && started < row.ScheduledStartUtc ? started : row.ScheduledStartUtc;

    /// <summary>
    /// Records the standings as they PROVABLY were before the session: the last table this bot fetched before
    /// <see cref="BaselineCutoff"/>. Never "the latest table right now" — after downtime that may already be the
    /// post-session table, which would hide the change. No provable pre-session table → baseline stays unknown (fail closed).
    /// Captured once; never overwritten.
    /// </summary>
    public async Task CaptureStandingsBaselineAsync(F1SessionSnapshotEntity row, CancellationToken ct)
    {
        var cutoff = BaselineCutoff(row);
        row.StandingsBaselineDriversHash ??= (await TableBeforeAsync(F1StandingsKind.Drivers, row.Season, cutoff, ct))?.CanonicalHash;
        row.StandingsBaselineConstructorsHash ??= (await TableBeforeAsync(F1StandingsKind.Constructors, row.Season, cutoff, ct))?.CanonicalHash;
    }

    /// <summary>The last table of that kind/season this bot had fetched strictly before <paramref name="before"/>.</summary>
    private Task<F1StandingsSnapshotEntity?> TableBeforeAsync(F1StandingsKind kind, int season, DateTimeOffset before, CancellationToken ct) =>
        db.Set<F1StandingsSnapshotEntity>().AsNoTracking()
            .Where(s => s.Kind == (int)kind && s.Season == season && s.FetchedAt < before)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Why a changed table can NOT be shown as "the standings after this session" (null = it can). Rules, all fail closed:
    /// <list type="bullet">
    /// <item>it must have been fetched after the session's cutoff and cover at least this round (a late correction of an
    /// earlier round is not this session's update);</item>
    /// <item>sprint: it must have been fetched before the same round's race could have started (otherwise it may already
    /// contain the race);</item>
    /// <item>race of a sprint weekend: the round number cannot tell post-sprint from post-race, so the post-sprint table
    /// must have been observed (changed, covering this round) between the sprint's end and the race's cutoff — only then is
    /// the pre-race baseline known to include the sprint and a further change provably contains the race.</item>
    /// </list>
    /// </summary>
    private async Task<string?> AttributionProblemAsync(F1SessionSnapshotEntity row, F1StandingsSnapshot snapshot, F1StandingsSnapshotEntity candidate, CancellationToken ct)
    {
        var cutoff = BaselineCutoff(row);
        if (candidate.FetchedAt < cutoff)
            return "table predates the session";
        if (snapshot.Round is not { } published || published < row.Round)
            return $"table covers round {snapshot.Round?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} only (session round {row.Round})";

        var sameRound = await Sessions.AsNoTracking()
            .Where(s => s.Season == row.Season && s.Round == row.Round && s.SessionKey != row.SessionKey &&
                        (s.SessionType == (int)F1SessionType.Sprint || s.SessionType == (int)F1SessionType.Race))
            .ToListAsync(ct);
        var type = (F1SessionType)row.SessionType;
        if (type == F1SessionType.Sprint && sameRound.FirstOrDefault(s => s.SessionType == (int)F1SessionType.Race) is { } race &&
            candidate.FetchedAt >= BaselineCutoff(race))
            return "fetched after the same round's race could have started (may include the race)";

        if (type == F1SessionType.Race && sameRound.FirstOrDefault(s => s.SessionType == (int)F1SessionType.Sprint) is { } sprint)
        {
            var sprintEnd = sprint.FinishedObservedAt ?? ToSession(sprint).PlannedEndUtc;
            var preSprint = await TableBeforeAsync(snapshot.Kind, row.Season, BaselineCutoff(sprint), ct);
            var postSprintSeen = preSprint is not null && await db.Set<F1StandingsSnapshotEntity>().AsNoTracking().AnyAsync(s =>
                s.Kind == (int)snapshot.Kind && s.Season == row.Season && s.FetchedAt > sprintEnd && s.FetchedAt < cutoff &&
                s.Round >= row.Round && s.CanonicalHash != preSprint.CanonicalHash, ct);
            if (!postSprintSeen)
                return "sprint weekend: the post-sprint table was not observed before the race, so post-sprint and post-race cannot be told apart";
        }

        return null;
    }

    /// <summary>Baselines for sprint/race sessions that are about to start (before any points can change).</summary>
    public async Task CaptureUpcomingBaselinesAsync(IEnumerable<string> sessionKeys, CancellationToken ct)
    {
        var keys = sessionKeys.ToList();
        var rows = await Sessions.Where(s => keys.Contains(s.SessionKey) && !s.IsBaseline &&
                                             (s.StandingsBaselineDriversHash == null || s.StandingsBaselineConstructorsHash == null)).ToListAsync(ct);
        foreach (var row in rows.Where(r => F1SessionTypes.AwardsChampionshipPoints((F1SessionType)r.SessionType)))
            await CaptureStandingsBaselineAsync(row, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Advances the settle-window schedule after a standings check and closes expired windows.</summary>
    public async Task<IReadOnlyList<string>> AdvanceStandingsWatchAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var closed = new List<string>();
        var rows = await Sessions.Where(s => s.StandingsWatchUntil != null && !s.StandingsWindowClosed).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (row.StandingsWatchUntil < now)
            {
                row.StandingsWindowClosed = true;
                row.StandingsNextCheckAt = null;
                closed.Add(row.SessionKey);
                logger.LogInformation("F1 standings: settle window for {Session} expired ({State})", row.SessionKey,
                    row.StandingsDriversSnapshotId is null && row.StandingsConstructorsSnapshotId is null ? "no provider update seen" : "standings attached");
                continue;
            }

            if (row.StandingsNextCheckAt is null || row.StandingsNextCheckAt <= now)
            {
                row.StandingsChecks++;
                row.StandingsNextCheckAt = now + Backoff(StandingsCheckMinutes, row.StandingsChecks);
            }
        }

        await db.SaveChangesAsync(ct);
        return closed;
    }

    // ---------------------------------------------------------------- queries for the poller

    public async Task<IReadOnlyList<F1SessionSnapshotEntity>> ActiveLifecycleSessionsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        var from = now - TimeSpan.FromHours(16);
        var to = now + TimeSpan.FromMinutes(o.LifecycleLeadMinutes);
        var rows = await Sessions.AsNoTracking().Where(s => s.ScheduledStartUtc >= from && s.ScheduledStartUtc <= to).ToListAsync(ct);
        return rows.Where(r =>
        {
            var state = (F1SessionState)r.State;
            if (state is not (F1SessionState.Scheduled or F1SessionState.Unknown or F1SessionState.Started or F1SessionState.Suspended))
                return false;
            var end = ToSession(r).PlannedEndUtc + TimeSpan.FromHours(o.LifecycleTrailingHours);
            return now <= end || (state is F1SessionState.Started or F1SessionState.Suspended && now <= r.ScheduledStartUtc + TimeSpan.FromHours(16));
        }).ToList();
    }

    /// <summary>Sessions whose results should be requested now (bounded; baseline sessions of the last days for display only).</summary>
    public async Task<IReadOnlyList<F1SessionSnapshotEntity>> ResultsDueAsync(TimeSpan availabilityDelay, bool lifecycleAvailable, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        var from = now - TimeSpan.FromHours(Math.Max(o.ResultsMaxWaitHours, o.ResultCorrectionHours) + 48);
        var rows = await Sessions.AsNoTracking().Where(s => s.ScheduledStartUtc >= from && s.ScheduledStartUtc <= now).ToListAsync(ct);
        return rows.Where(r =>
        {
            if (r.ResultNextAttemptAt is { } next && next > now)
                return false;
            var state = (F1SessionState)r.State;
            var plannedEnd = ToSession(r).PlannedEndUtc;
            if (state == F1SessionState.Cancelled)
                return false;
            if (state == F1SessionState.Finalised)
                return !r.IsBaseline && r.FinalisedObservedAt is { } f && now - f < TimeSpan.FromHours(o.ResultCorrectionHours) && r.ResultNextAttemptAt is not null;
            if (r.IsBaseline)
                return now - plannedEnd < TimeSpan.FromDays(4) && r.ResultAttempts < 6; // display only, never announced
            if (now - plannedEnd > TimeSpan.FromHours(o.ResultsMaxWaitHours))
                return false; // give up: no result is better than a wrong one
            // While a lifecycle provider reports the session running, wait for its finish (unless it went silent for hours).
            if (lifecycleAvailable && state is F1SessionState.Started or F1SessionState.Suspended && now < plannedEnd + TimeSpan.FromHours(3))
                return false;
            var readyAt = (r.FinishedObservedAt ?? plannedEnd) + availabilityDelay;
            return now >= readyAt;
        }).OrderBy(r => r.ScheduledStartUtc).ToList();
    }

    public async Task<bool> StandingsCheckDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await Sessions.AnyAsync(s => s.StandingsWatchUntil != null && !s.StandingsWindowClosed &&
                                            (s.StandingsNextCheckAt == null || s.StandingsNextCheckAt <= now || s.StandingsWatchUntil < now), ct);
    }

    public async Task<(IReadOnlyList<F1SessionView> Views, IReadOnlyDictionary<string, F1CachedResult> Results)> LoadViewsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var fromSeason = now.Year - 1;
        var rows = await Sessions.AsNoTracking().Where(s => s.Season >= fromSeason).ToListAsync(ct);
        var keys = rows.Select(r => r.SessionKey).ToList();
        var results = await db.Set<F1ResultSnapshotEntity>().AsNoTracking().Where(r => keys.Contains(r.SessionKey)).ToListAsync(ct);
        var views = rows.Select(ToView).ToList();
        var dict = new Dictionary<string, F1CachedResult>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (F1Json.Deserialize<F1SessionResult>(r.PayloadJson) is { } parsed)
                dict[r.SessionKey] = new F1CachedResult(parsed, r.FetchedAt, r.FirstAvailableAt);
        }

        return (views, dict);
    }

    public static F1SessionView ToView(F1SessionSnapshotEntity r) =>
        new(ToSession(r), r.MeetingName, r.CircuitName, r.Country, r.Location, (F1SessionState)r.State,
            r.StartedObservedAt, r.FinishedObservedAt, r.FinalisedObservedAt, r.CancelledObservedAt, r.LastLifecycleEventAt, r.IsBaseline);

    public static F1Session ToSession(F1SessionSnapshotEntity r) =>
        new(r.Season, r.Round, (F1SessionType)r.SessionType, r.ScheduledStartUtc, r.ScheduledEndUtc);

    private static TimeSpan Backoff(int[] minutes, int attempt) =>
        TimeSpan.FromMinutes(minutes[Math.Clamp(attempt - 1, 0, minutes.Length - 1)]);

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
