namespace ToroSquad.Modules.Volleyball.Domain;

/// <summary>One finished set as recorded by TSQ (winner derived from the provider's points).</summary>
public sealed record SetResult(int Number, int HomePoints, int AwayPoints)
{
    public bool HomeWon => HomePoints > AwayPoints;
}

/// <summary>
/// The persisted, provider-independent progress of a match. Flags only ever move forward: a started match is never
/// "not started" again, a finished or cancelled match is never reopened, set counts never decrease.
/// </summary>
public sealed record MatchProgressState(
    VolleyballMatchStatus Status,
    int HomeSets,
    int AwaySets,
    IReadOnlyList<SetResult> Sets,
    bool Started,
    bool Finished,
    bool Postponed,
    bool Cancelled)
{
    public int CompletedSets => HomeSets + AwaySets;

    public static MatchProgressState Initial { get; } = new(VolleyballMatchStatus.Unknown, 0, 0, [], false, false, false, false);
}

public enum ProgressSignal
{
    Started = 0,
    SetCompleted = 1,
    Finished = 2,
    Postponed = 3,
    Cancelled = 4,
}

/// <summary>
/// A real state transition. <see cref="Announce"/> = false marks transitions that happened but must not produce a card
/// (missed while the bot was not watching, or superseded by a later transition in the same observation).
/// </summary>
public sealed record ProgressTransition(ProgressSignal Signal, int? SetNumber, bool Announce);

public enum ObservationProblem
{
    None = 0,

    /// <summary>The provider's own update time is too old: nothing is applied.</summary>
    Stale = 1,

    /// <summary>Scores are impossible or internally inconsistent: nothing is applied.</summary>
    Malformed = 2,

    /// <summary>Fewer sets / a set winner changed / "live" became "scheduled" again: ignored, state kept.</summary>
    Regression = 3,

    /// <summary>A finished or cancelled match reported as running again: ignored (never reopened).</summary>
    ReopenAfterEnd = 4,
}

public sealed record ProgressResult(MatchProgressState State, IReadOnlyList<ProgressTransition> Transitions, ObservationProblem Problem, string? Detail)
{
    public bool Changed { get; init; }
}

/// <summary>
/// The volleyball match state machine (pure, tested). It turns one provider observation into transitions relative to the
/// persisted state:
/// <list type="bullet">
/// <item>First observation = baseline: the current state is recorded, nothing is a transition (no historical replay).</item>
/// <item>"Started" only from a provider-stated live/finished status, once. A set is completed when the provider's set
/// count grows AND the set's points are known; each set number transitions at most once.</item>
/// <item>Count going down, a set changing winner, or "live" going back to "scheduled" is a regression: ignored.</item>
/// <item>Stale or malformed data changes nothing (if the data is not trustworthy enough to send, send nothing).</item>
/// <item><paramref name="continuous"/> = the previous observation is recent. Without continuity (restart, outage) started/set
/// transitions are recorded but not announced; only the final result remains announceable (bounded by the planner).</item>
/// <item>Several transitions in one observation: only the most recent is announced (final supersedes sets, the latest set
/// supersedes earlier ones and "started").</item>
/// </list>
/// </summary>
public static class MatchProgress
{
    public const int MaxSets = 5;
    public const int SetsToWin = 3;

    /// <summary>25 points (15 in the 5th set) with a 2-point lead; beyond the target the lead is exactly 2 (e.g. 26-24, 31-29).</summary>
    public static bool IsFinishedSet(int number, int home, int away)
    {
        var target = number == MaxSets ? 15 : 25;
        var winner = Math.Max(home, away);
        var loser = Math.Min(home, away);
        if (loser < 0 || winner > 99)
            return false;
        return winner == target ? loser <= target - 2 : winner > target && winner - loser == 2;
    }

    public static ProgressResult Advance(MatchProgressState? previous, VolleyballMatch observed, bool continuous)
    {
        var baseline = previous is null;
        var prev = previous ?? MatchProgressState.Initial;
        if (observed.IsStale)
            return Unchanged(prev, ObservationProblem.Stale, "provider data is stale");
        if (Validate(observed) is { } problem)
            return Unchanged(prev, ObservationProblem.Malformed, problem);

        if (prev.Finished || prev.Cancelled)
            return AfterEnd(prev, observed);

        return observed.Status switch
        {
            VolleyballMatchStatus.Unknown => Unchanged(prev, ObservationProblem.None, null),
            VolleyballMatchStatus.Scheduled => prev.Started
                ? Unchanged(prev, ObservationProblem.Regression, "live match reported as scheduled")
                : Apply(prev, prev with { Status = VolleyballMatchStatus.Scheduled }, []),
            VolleyballMatchStatus.Postponed => prev.Started
                ? Unchanged(prev, ObservationProblem.Regression, "started match reported as postponed")
                : Apply(prev, prev with { Status = VolleyballMatchStatus.Postponed, Postponed = true },
                    prev.Postponed || baseline ? [] : [new ProgressTransition(ProgressSignal.Postponed, null, true)]),
            VolleyballMatchStatus.Cancelled => Apply(prev, prev with { Status = VolleyballMatchStatus.Cancelled, Cancelled = true },
                baseline ? [] : [new ProgressTransition(ProgressSignal.Cancelled, null, true)]),
            _ => Running(prev, observed, continuous, baseline),
        };
    }

    /// <summary>Live, suspended or finished: set progress and the final result.</summary>
    private static ProgressResult Running(MatchProgressState prev, VolleyballMatch observed, bool continuous, bool baseline)
    {
        var home = observed.HomeSets!.Value;
        var away = observed.AwaySets!.Value;
        if (home < prev.HomeSets || away < prev.AwaySets)
            return Unchanged(prev, ObservationProblem.Regression, $"set count went back {prev.HomeSets}-{prev.AwaySets} -> {home}-{away}");

        var observedSets = CompletedSetsOf(observed);
        if (observedSets is not null)
        {
            foreach (var recorded in prev.Sets)
            {
                var now = observedSets.FirstOrDefault(s => s.Number == recorded.Number);
                if (now is not null && now.HomeWon != recorded.HomeWon)
                    return Unchanged(prev, ObservationProblem.Regression, $"set {recorded.Number} changed winner");
            }
        }

        var finished = observed.Status == VolleyballMatchStatus.Finished;
        var completed = home + away;
        var sets = observedSets ?? prev.Sets;
        var next = prev with
        {
            Status = observed.Status,
            HomeSets = home,
            AwaySets = away,
            Sets = sets,
            Started = true,
            Finished = finished,
        };
        if (baseline)
            return Apply(prev, next, []);

        var transitions = new List<ProgressTransition>();
        var newSets = sets.Where(s => prev.Sets.All(p => p.Number != s.Number)).OrderBy(s => s.Number).ToList();
        // The deciding set (a side reached SetsToWin) is never its own card: VIS passes through "set N finished" before
        // "finished", and the final card already shows that set — no "set" card followed a minute later by the final.
        var decided = Math.Max(home, away) >= SetsToWin;
        if (!prev.Started)
            transitions.Add(new(ProgressSignal.Started, null, continuous && !finished && completed == 0));
        foreach (var set in newSets)
            transitions.Add(new(ProgressSignal.SetCompleted, set.Number, continuous && !finished && !decided && set.Number == completed));
        if (finished)
            transitions.Add(new(ProgressSignal.Finished, null, true));
        return Apply(prev, next, transitions);
    }

    /// <summary>
    /// Accepts a provider's score CORRECTION of a running match (e.g. a set first credited to the wrong team, or a count that
    /// went back) after the workflow saw the same corrected state repeatedly. Only for a started, not ended match and only to
    /// another running/finished state — never back to "not started". Produces no transition: already sent cards stay, and
    /// set numbers that were announced are never announced again.
    /// </summary>
    public static MatchProgressState? Correct(MatchProgressState prev, VolleyballMatch observed)
    {
        if (!prev.Started || prev.Finished || prev.Cancelled || observed.IsStale || Validate(observed) is not null ||
            observed.Status is not (VolleyballMatchStatus.Live or VolleyballMatchStatus.Suspended or VolleyballMatchStatus.Finished))
            return null;
        // A correction that arrives together with "finished" is recorded as the running state; the next (identical) finished
        // observation then produces the normal, single final transition.
        return prev with
        {
            Status = observed.Status == VolleyballMatchStatus.Finished ? VolleyballMatchStatus.Live : observed.Status,
            HomeSets = observed.HomeSets!.Value,
            AwaySets = observed.AwaySets!.Value,
            Sets = CompletedSetsOf(observed) ?? [],
        };
    }

    private static ProgressResult AfterEnd(MatchProgressState prev, VolleyballMatch observed)
    {
        if (prev.Cancelled || observed.Status != VolleyballMatchStatus.Finished)
        {
            return observed.Status is VolleyballMatchStatus.Live or VolleyballMatchStatus.Suspended or VolleyballMatchStatus.Scheduled
                ? Unchanged(prev, ObservationProblem.ReopenAfterEnd, $"ended match reported as {observed.Status}")
                : Unchanged(prev, ObservationProblem.None, null);
        }

        // A finished match may still receive a provider correction of its final score: recorded, never a new transition
        // (the planner edits the existing final card within its correction window).
        var home = observed.HomeSets!.Value;
        var away = observed.AwaySets!.Value;
        var corrected = prev with { HomeSets = home, AwaySets = away, Sets = CompletedSetsOf(observed) ?? prev.Sets };
        return Apply(prev, corrected, []);
    }

    /// <summary>
    /// Null when the observation is usable; otherwise why not. Official senior competitions are best of five (FIVB rules):
    /// each side 0..3 sets; a finished match is exactly 3-0, 3-1 or 3-2; sets 1–4 are won at 25 (5th set at 15) with a
    /// 2-point lead, and beyond the target only by exactly 2 (deuce). Running/finished matches need set counts; if set points
    /// are supplied they must cover exactly the completed sets and agree with the counts.
    /// </summary>
    public static string? Validate(VolleyballMatch m)
    {
        if (m.Status is not (VolleyballMatchStatus.Live or VolleyballMatchStatus.Suspended or VolleyballMatchStatus.Finished))
            return null;
        if (m.HomeSets is not { } home || m.AwaySets is not { } away)
            return "set counts missing";
        if (home < 0 || away < 0 || home > SetsToWin || away > SetsToWin || home + away > MaxSets || (home == SetsToWin && away == SetsToWin))
            return $"impossible set count {home}-{away}";
        if (m.Status == VolleyballMatchStatus.Finished && Math.Max(home, away) != SetsToWin)
            return $"finished at {home}-{away} (no side has {SetsToWin} sets)";
        if (m.Sets.Count == 0)
            return null; // counts only (provider without set points): no set transitions, see CompletedSetsOf

        var completed = CompletedSetsOf(m);
        if (completed is null)
            return "set points do not cover the completed sets";
        foreach (var s in completed)
        {
            if (!IsFinishedSet(s.Number, s.HomePoints, s.AwayPoints))
                return $"set {s.Number} score {s.HomePoints}-{s.AwayPoints} is not a finished set";
        }

        if (completed.Count(s => s.HomeWon) != home || completed.Count(s => !s.HomeWon) != away)
            return "set winners do not match the set count";
        return null;
    }

    /// <summary>
    /// Points of the completed sets (1..home+away), or null when the provider sent no set points or they do not cover
    /// exactly those sets. Sets beyond the count (the set in progress) are ignored.
    /// </summary>
    public static IReadOnlyList<SetResult>? CompletedSetsOf(VolleyballMatch m)
    {
        var count = (m.HomeSets ?? 0) + (m.AwaySets ?? 0);
        if (m.Sets.Count == 0)
            return null;
        var result = new List<SetResult>(count);
        for (var n = 1; n <= count; n++)
        {
            var matches = m.Sets.Where(s => s.Number == n).ToList();
            if (matches.Count != 1 || !matches[0].Completed)
                return null;
            result.Add(new SetResult(n, matches[0].HomePoints, matches[0].AwayPoints));
        }

        return result;
    }

    private static ProgressResult Unchanged(MatchProgressState state, ObservationProblem problem, string? detail) => new(state, [], problem, detail);

    private static ProgressResult Apply(MatchProgressState prev, MatchProgressState next, IReadOnlyList<ProgressTransition> transitions) =>
        new(next, transitions, ObservationProblem.None, null) { Changed = !Same(prev, next) || transitions.Count > 0 };

    private static bool Same(MatchProgressState a, MatchProgressState b) =>
        a.Status == b.Status && a.HomeSets == b.HomeSets && a.AwaySets == b.AwaySets && a.Started == b.Started && a.Finished == b.Finished &&
        a.Postponed == b.Postponed && a.Cancelled == b.Cancelled && a.Sets.SequenceEqual(b.Sets);
}
