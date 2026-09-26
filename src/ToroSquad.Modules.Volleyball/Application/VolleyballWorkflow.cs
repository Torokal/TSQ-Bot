using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Persistence;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

public sealed record VbApplyReport(int Discovered, int Transitions, int Announceable, int Problems);

/// <summary>
/// Persists every decision the notification rules depend on (docs/volleyball/VOLLEYBALL.md): one shared snapshot per match
/// of the followed team, its progress (<see cref="MatchProgress"/>) and the recorded transitions. It never talks to Discord
/// and never stages notifications (that is <see cref="VolleyballNotificationPlanner"/>), so every decision survives restarts.
/// Only matches that passed the <see cref="TrackedTeamIdentity"/> filter reach this class.
/// </summary>
public sealed class VolleyballWorkflow(ToroDbContext db, IVolleyballDataProvider provider, IOptions<VolleyballOptions> options, TimeProvider clock, ILogger<VolleyballWorkflow> logger)
{
    private DbSet<VbMatchSnapshotEntity> AllMatches => db.Set<VbMatchSnapshotEntity>();

    /// <summary>Only the configured provider's rows: demo (fixture) data can never surface after switching to live data.</summary>
    private IQueryable<VbMatchSnapshotEntity> Matches => AllMatches.Where(m => m.Provider == provider.Id);

    /// <summary>
    /// Applies one trustworthy provider observation per match (fixtures refresh or live poll) fetched at
    /// <paramref name="observedAt"/>. First sighting = baseline (nothing already happened is ever announced). Matches missing
    /// from a later response are left as they are: a provider omission is not a cancellation.
    /// </summary>
    public async Task<VbApplyReport> ApplyAsync(IReadOnlyList<(VolleyballMatch Match, FollowedSide Side)> observed, DateTimeOffset observedAt, bool listed, CancellationToken ct)
    {
        if (observed.Count == 0)
            return new(0, 0, 0, 0);
        var o = options.Value;
        var keys = observed.Select(x => x.Match.MatchId).Distinct(StringComparer.Ordinal).ToList();
        var rows = await AllMatches.Where(m => keys.Contains(m.MatchKey)).ToDictionaryAsync(m => m.MatchKey, StringComparer.Ordinal, ct);
        int discovered = 0, transitions = 0, announceable = 0, problems = 0;
        var now = clock.GetUtcNow();

        foreach (var (match, side) in observed.GroupBy(x => x.Match.MatchId, StringComparer.Ordinal).Select(g => g.Last()))
        {
            var candidate = MarkStale(match, observedAt, o);
            if (!rows.TryGetValue(match.MatchId, out var row))
            {
                row = new VbMatchSnapshotEntity
                {
                    MatchKey = match.MatchId,
                    Provider = match.Provider,
                    ProviderMatchId = match.ProviderMatchId,
                    FirstSeenAt = now,
                    LastObservedAt = observedAt,
                    LastListedAt = listed ? observedAt : null,
                };
                var first = MatchProgress.Advance(null, candidate, continuous: false);
                if (first.Problem != ObservationProblem.None)
                {
                    // An untrustworthy first sighting is not stored at all (it would become a wrong baseline).
                    problems++;
                    logger.LogWarning("volleyball provider_state_conflict match={Match} first observation rejected: {Problem} {Detail}", match.MatchId, first.Problem, first.Detail);
                    continue;
                }

                UpdateMetadata(row, candidate, side, allowStartChange: true, listed);
                Store(row, first.State);
                row.IsBaseline = first.State.Started || first.State.Postponed || first.State.Cancelled;
                row.UpdatedAt = now;
                AllMatches.Add(row);
                rows[row.MatchKey] = row;
                discovered++;
                logger.LogInformation("volleyball match_discovered match={Match} start={Start:u} status={Status} competition={Competition}",
                    match.MatchId, match.StartTimeUtc, match.Status, match.CompetitionName);
                if (row.IsBaseline)
                    logger.LogInformation("volleyball baseline_established match={Match} status={Status} sets={Home}-{Away} (history is never announced)",
                        match.MatchId, first.State.Status, first.State.HomeSets, first.State.AwaySets);
                continue;
            }

            if (listed)
                row.LastListedAt = observedAt;
            var continuous = observedAt - row.LastObservedAt <= o.Continuity;
            var persisted = Load(row);
            var result = MatchProgress.Advance(persisted, candidate, continuous);
            var eventAt = observedAt;
            var previousObservation = row.LastObservedAt;
            if (result.Problem == ObservationProblem.Regression && TryAcceptCorrection(row, persisted, candidate, observedAt, now))
                continue;
            if (result.Problem != ObservationProblem.None)
            {
                problems++;
                row.Problems++;
                row.LastProblem = Clip($"{result.Problem}: {result.Detail}", 300);
                row.LastProblemAt = now;
                row.UpdatedAt = now;
                if (result.Problem == ObservationProblem.Stale)
                    logger.LogWarning("volleyball provider_stale match={Match} provider update {At:u}: nothing applied", match.MatchId, match.LastProviderUpdateUtc);
                else
                    logger.LogWarning("volleyball provider_state_conflict match={Match} {Problem}: {Detail} (ignored, state kept)", match.MatchId, result.Problem, result.Detail);
                continue; // untrustworthy: not even the continuity clock moves
            }

            var confirmed = result.Transitions.Count > 0 ? ConfirmedByPending(row, persisted, candidate, continuous) : null;
            if (confirmed is not null)
            {
                // Confirmed: judged as of the FIRST sighting (continuity, previous observation, time).
                result = MatchProgress.Advance(persisted, candidate, confirmed.Continuous);
                eventAt = confirmed.SeenAt;
                previousObservation = confirmed.PreviousObservedAt ?? previousObservation;
            }
            else if (result.Transitions.Count > 0)
            {
                // First sighting of a new state: remembered, applied only when the NEXT observation agrees (VIS was seen
                // answering "in set 1, 15-14" and "scheduled" alternately — a single forward jump is never announced).
                row.PendingJson = VbJson.Serialize(PendingObservation.Of(candidate, PendingObservation.Forward, observedAt, continuous, row.LastObservedAt));
                row.PendingCount = 1;
                row.LastObservedAt = observedAt;
                row.UpdatedAt = now;
                UpdateMetadata(row, candidate, side, allowStartChange: !persisted.Started, listed);
                continue;
            }

            UpdateMetadata(row, candidate, side, allowStartChange: !result.State.Started, listed);
            Store(row, result.State);
            if (result.Transitions.Count > 0 && result.Transitions.Any(t => !t.Announce && t.Signal is ProgressSignal.Started or ProgressSignal.SetCompleted) && confirmed is { Continuous: false })
                logger.LogInformation("volleyball continuity gap match={Match} last observation {Last:u}: started/set transitions recorded without cards", match.MatchId, previousObservation);
            row.LastObservedAt = observedAt;
            row.PendingJson = null;
            row.PendingCount = 0;
            row.UpdatedAt = now;
            if (result.Transitions.Count == 0)
                continue;

            var events = Events(row);
            foreach (var t in result.Transitions)
            {
                var kind = t.Signal switch
                {
                    ProgressSignal.Started => VbEvent.Started,
                    ProgressSignal.SetCompleted => VbEvent.SetCompleted,
                    ProgressSignal.Finished => VbEvent.Final,
                    ProgressSignal.Postponed => VbEvent.Postponed,
                    _ => VbEvent.Cancelled,
                };
                if (events.Any(e => e.Kind == kind && e.Set == t.SetNumber))
                    continue; // each transition exists once per match (postponed twice = one event)
                events.Add(new VbEvent(kind, t.SetNumber, eventAt, t.Announce, previousObservation));
                transitions++;
                if (t.Announce)
                    announceable++;
                logger.LogInformation("volleyball {Event} match={Match} set={Set} score={Home}-{Away} announce={Announce}",
                    EventLogName(kind), match.MatchId, t.SetNumber, result.State.HomeSets, result.State.AwaySets, t.Announce);
            }

            row.EventsJson = VbJson.Serialize(events);
        }
        await db.SaveChangesAsync(ct);
        return new(discovered, transitions, announceable, problems);
    }

    /// <summary>What an unconfirmed observation looked like (kind "forward" = a new transition, "correction" = a changed past).</summary>
    /// <summary>
    /// An unconfirmed observation. <see cref="SeenAt"/>, <see cref="Continuous"/> and <see cref="PreviousObservedAt"/> describe
    /// its FIRST sighting: a confirmation never makes an observation after an outage look continuous, and the transition time
    /// is when it was first seen.
    /// </summary>
    public sealed record PendingObservation(string Kind, VolleyballMatchStatus Status, int? HomeSets, int? AwaySets, IReadOnlyList<VolleyballSet> Sets,
        DateTimeOffset SeenAt = default, bool Continuous = false, DateTimeOffset? PreviousObservedAt = null)
    {
        public const string Forward = "forward";
        public const string Correction = "correction";

        public static PendingObservation Of(VolleyballMatch m, string kind = Forward, DateTimeOffset seenAt = default, bool continuous = false, DateTimeOffset? previous = null) =>
            new(kind, m.Status, m.HomeSets, m.AwaySets, m.Sets, seenAt, continuous, previous);

        /// <summary>Same observed state (the timing fields do not matter for "seen again").</summary>
        public bool SameState(PendingObservation other) => Kind == other.Kind && Status == other.Status && HomeSets == other.HomeSets && AwaySets == other.AwaySets && Sets.SequenceEqual(other.Sets);

        public VolleyballMatch Apply(VolleyballMatch m) => m with { Status = Status, HomeSets = HomeSets, AwaySets = AwaySets, Sets = Sets, IsStale = false };
    }

    /// <summary>
    /// True when the previous observation already showed this new state (or an earlier step of it) and the current one agrees
    /// with it — two consecutive consistent observations. A pending state contradicted by the next observation is dropped.
    /// </summary>
    private static PendingObservation? ConfirmedByPending(VbMatchSnapshotEntity row, MatchProgressState persisted, VolleyballMatch candidate, bool continuous)
    {
        if (!continuous || VbJson.Deserialize<PendingObservation>(row.PendingJson) is not { Kind: PendingObservation.Forward } pending)
            return null;
        var first = MatchProgress.Advance(persisted, pending.Apply(candidate), continuous: true);
        if (first.Problem != ObservationProblem.None || first.Transitions.Count == 0)
            return null;
        return MatchProgress.Advance(first.State, candidate, continuous: true).Problem == ObservationProblem.None ? pending : null;
    }

    /// <summary>
    /// A regression is normally ignored. If the provider keeps reporting the SAME different past for three consecutive
    /// observations (a scorer's correction, not flapping), the running state is corrected silently (no card, no replay of
    /// announced sets) so the match does not stay frozen until the end.
    /// </summary>
    private bool TryAcceptCorrection(VbMatchSnapshotEntity row, MatchProgressState persisted, VolleyballMatch candidate, DateTimeOffset observedAt, DateTimeOffset now)
    {
        if (MatchProgress.Correct(persisted, candidate) is not { } corrected)
            return false;
        var observation = PendingObservation.Of(candidate, PendingObservation.Correction, observedAt);
        row.PendingCount = VbJson.Deserialize<PendingObservation>(row.PendingJson) is { } previous && previous.SameState(observation) ? row.PendingCount + 1 : 1;
        row.PendingJson = VbJson.Serialize(observation);
        row.UpdatedAt = now;
        if (row.PendingCount < 3)
        {
            row.Problems++;
            row.LastProblem = Clip($"Regression: {candidate.HomeSets}-{candidate.AwaySets} differs from the recorded {persisted.HomeSets}-{persisted.AwaySets} ({row.PendingCount}/3)", 300);
            row.LastProblemAt = now;
            logger.LogWarning("volleyball provider_state_conflict match={Match} recorded {Home}-{Away}, provider now {NewHome}-{NewAway} ({Count}/3 before a correction is accepted)",
                row.MatchKey, persisted.HomeSets, persisted.AwaySets, candidate.HomeSets, candidate.AwaySets, row.PendingCount);
            return true;
        }

        Store(row, corrected);
        row.PendingJson = null;
        row.PendingCount = 0;
        row.LastObservedAt = observedAt;
        logger.LogWarning("volleyball provider_state_corrected match={Match} {Home}-{Away} -> {NewHome}-{NewAway} (no card; announced sets are not repeated)",
            row.MatchKey, persisted.HomeSets, persisted.AwaySets, corrected.HomeSets, corrected.AwaySets);
        return true;
    }

    /// <summary>
    /// Matches that need live polling now: around the scheduled start, started and not finished yet, or with an unconfirmed
    /// observation (confirmed within one live interval instead of waiting for the next fixture refresh).
    /// </summary>
    public async Task<IReadOnlyList<VbMatchSnapshotEntity>> LiveCandidatesAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var from = now - TimeSpan.FromHours(o.LiveTrailingHours);
        var to = now + TimeSpan.FromMinutes(o.LiveLeadMinutes);
        var recent = now - TimeSpan.FromDays(2);
        return await Matches.AsNoTracking()
            .Where(m => !m.Finished && !m.Cancelled &&
                        ((m.StartTimeUtc != null && m.StartTimeUtc >= from && m.StartTimeUtc <= to) || (m.PendingJson != null && m.UpdatedAt >= recent)))
            .ToListAsync(ct);
    }

    /// <summary>True while a started, unfinished match may still produce its result (fixture refresh stays hourly meanwhile).</summary>
    public async Task<bool> AwaitingResultAsync(CancellationToken ct)
    {
        var since = clock.GetUtcNow() - TimeSpan.FromHours(12);
        var now = clock.GetUtcNow();
        return await Matches.AsNoTracking().AnyAsync(m => !m.Finished && !m.Cancelled && m.StartTimeUtc != null && m.StartTimeUtc >= since && m.StartTimeUtc <= now, ct);
    }

    /// <summary>The next scheduled start of any unfinished match (drives the fixture refresh cadence).</summary>
    public async Task<DateTimeOffset?> NextStartAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await Matches.AsNoTracking()
            .Where(m => !m.Finished && !m.Cancelled && m.StartTimeUtc != null && m.StartTimeUtc >= now)
            .OrderBy(m => m.StartTimeUtc)
            .Select(m => m.StartTimeUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<VbMatchView>> LoadViewsAsync(CancellationToken ct)
    {
        var from = clock.GetUtcNow() - TimeSpan.FromDays(45);
        var rows = await Matches.AsNoTracking().Where(m => m.StartTimeUtc == null || m.StartTimeUtc >= from).ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    public static VbMatchView ToView(VbMatchSnapshotEntity r) => new(
        r.MatchKey, r.Provider, r.CompetitionName, r.Stage, r.Round, r.StartTimeUtc, (FollowedSide)r.FollowedSide,
        r.HomeName, r.HomeCode, r.AwayName, r.AwayCode, r.HomeLogoUrl, r.AwayLogoUrl, r.Venue, r.City,
        VbJson.Deserialize<List<string>>(r.BroadcastsJson) ?? [], (VolleyballMatchStatus)r.Status, r.HomeSets, r.AwaySets,
        VbJson.Deserialize<List<SetResult>>(r.SetsJson) ?? [], r.CurrentSet, r.CurrentSetHomePoints, r.CurrentSetAwayPoints,
        r.Started, r.Finished, r.Postponed, r.Cancelled, r.LastObservedAt);

    public static List<VbEvent> Events(VbMatchSnapshotEntity row) => VbJson.Deserialize<List<VbEvent>>(row.EventsJson) ?? [];

    public static MatchProgressState Load(VbMatchSnapshotEntity r) => new(
        (VolleyballMatchStatus)r.Status, r.HomeSets, r.AwaySets, VbJson.Deserialize<List<SetResult>>(r.SetsJson) ?? [],
        r.Started, r.Finished, r.Postponed, r.Cancelled);

    private static void Store(VbMatchSnapshotEntity row, MatchProgressState s)
    {
        row.Status = (int)s.Status;
        row.HomeSets = s.HomeSets;
        row.AwaySets = s.AwaySets;
        row.SetsJson = VbJson.Serialize(s.Sets);
        row.Started = s.Started;
        row.Finished = s.Finished;
        row.Postponed = s.Postponed;
        row.Cancelled = s.Cancelled;
    }

    /// <summary>
    /// Descriptive data from the newest trustworthy observation. The start time only changes while the match has not started.
    /// Broadcasts are replaced by exactly what the provider states for THIS match (an empty list clears them — never carried
    /// over). The in-progress set is display-only and cleared once the match is no longer live.
    /// </summary>
    private static void UpdateMetadata(VbMatchSnapshotEntity row, VolleyballMatch m, FollowedSide side, bool allowStartChange, bool listed)
    {
        row.CompetitionId = Clip(m.CompetitionId, 64);
        row.CompetitionName = Clip(m.CompetitionName, 200) ?? "";
        row.Season = m.Season;
        row.Stage = Clip(m.Stage, 100);
        row.Round = Clip(m.Round, 100);
        // A start that is no longer confirmed in the fixture listing (e.g. back to "time TBC") is cleared: no reminder at an old time.
        if (allowStartChange && (m.StartTimeUtc is not null || listed))
            row.StartTimeUtc = m.StartTimeUtc;
        row.FollowedSide = (int)side;
        row.HomeName = Clip(m.HomeTeam.Name, 100) ?? "";
        row.HomeCode = Clip(m.HomeTeam.CountryCode, 8);
        row.AwayName = Clip(m.AwayTeam.Name, 100) ?? "";
        row.AwayCode = Clip(m.AwayTeam.CountryCode, 8);
        row.HomeLogoUrl = m.HomeTeam.LogoUrl;
        row.AwayLogoUrl = m.AwayTeam.LogoUrl;
        row.Venue = Clip(m.Venue, 200);
        row.City = Clip(m.City, 100);
        row.BroadcastsJson = m.Broadcasts.Count == 0 ? null : Clip(VbJson.Serialize(m.Broadcasts.Take(5).ToList()), 1000);
        var live = m.Status is VolleyballMatchStatus.Live or VolleyballMatchStatus.Suspended;
        row.CurrentSet = live ? m.CurrentSet : null;
        row.CurrentSetHomePoints = live ? m.CurrentSetHomePoints : null;
        row.CurrentSetAwayPoints = live ? m.CurrentSetAwayPoints : null;
        row.LastProviderUpdateAt = m.LastProviderUpdateUtc ?? row.LastProviderUpdateAt;
    }

    /// <summary>
    /// A running match whose provider-stated update time is older than <see cref="VolleyballOptions.LiveStaleAfterMinutes"/>
    /// is stale (the provider froze): it must not create live transitions.
    /// </summary>
    private static VolleyballMatch MarkStale(VolleyballMatch m, DateTimeOffset observedAt, VolleyballOptions o) =>
        m.Status is VolleyballMatchStatus.Live or VolleyballMatchStatus.Suspended &&
        m.LastProviderUpdateUtc is { } at && observedAt - at > TimeSpan.FromMinutes(o.LiveStaleAfterMinutes)
            ? m with { IsStale = true }
            : m;

    private static string EventLogName(string kind) => kind switch
    {
        VbEvent.Started => "match_started",
        VbEvent.SetCompleted => "set_completed",
        VbEvent.Final => "match_finished",
        VbEvent.Postponed => "match_postponed",
        _ => "match_cancelled",
    };

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
