namespace ToroSquad.Modules.Formula1.Domain;

/// <summary>
/// Normalized session lifecycle (persisted as int). Only a lifecycle-capable provider moves a session out of
/// <see cref="Scheduled"/>: the clock passing the scheduled start never does.
/// </summary>
public enum F1SessionState
{
    Unknown = 0,
    Scheduled = 1,
    Started = 2,
    Suspended = 3,

    /// <summary>The provider said the session finished; the final classification is not available yet.</summary>
    FinishedPendingResults = 4,

    /// <summary>A valid final classification is available.</summary>
    Finalised = 5,
    Cancelled = 6,
}

/// <summary>Provider lifecycle signals after normalization (OpenF1: race_control category "SessionStatus").</summary>
public enum F1LifecycleSignal
{
    Started = 1,

    /// <summary>Session stopped (e.g. red flag). OpenF1: "SESSION ABORTED".</summary>
    Suspended = 2,
    Finished = 3,
    Cancelled = 4,
}

/// <summary>
/// One normalized lifecycle event. <see cref="OccurredAt"/> is the PROVIDER's timestamp (not when we received it);
/// ordering, deduplication and freshness are all decided on it. <see cref="QualifyingPhase"/> is the provider's segment
/// number for qualifying formats (a "finished" in phase 1/2 is only a segment end).
/// </summary>
public sealed record F1LifecycleEvent(
    string ProviderId,
    string ProviderSessionRef,
    F1LifecycleSignal Signal,
    DateTimeOffset OccurredAt,
    int? QualifyingPhase = null);

public enum F1TransitionKind
{
    /// <summary>Nothing changed (duplicate, stale/out-of-order, or not meaningful in the current state).</summary>
    Ignored = 0,
    FirstStart = 1,
    Resume = 2,
    Suspend = 3,

    /// <summary>A qualifying segment ended; the session continues.</summary>
    SegmentEnd = 4,
    Finish = 5,
    Cancel = 6,
}

public sealed record F1Transition(F1TransitionKind Kind, F1SessionState NewState, string Reason)
{
    public bool Changed => Kind != F1TransitionKind.Ignored;
}

/// <summary>
/// The session lifecycle state machine (pure). Guarantees, each covered by tests:
/// <list type="bullet">
/// <item>Only an explicit Started signal from Scheduled/Unknown is a logical start (<see cref="F1TransitionKind.FirstStart"/>);
/// a Started after Suspended is a <see cref="F1TransitionKind.Resume"/> and never a second start.</item>
/// <item>Events not newer than the last applied provider event are ignored (duplicates and out-of-order replays).</item>
/// <item>Finished means "results pending", never "results available"; <see cref="F1SessionState.Finalised"/> is only set by
/// the results workflow when a valid classification exists.</item>
/// <item>For qualifying formats only the final segment's end finishes the session; an unknown segment fails closed.</item>
/// <item>Finished/finalised/cancelled sessions are not re-opened by late lifecycle messages.</item>
/// </list>
/// </summary>
public static class F1LifecycleMachine
{
    public const int FinalQualifyingPhase = 3;

    public static F1Transition Apply(F1SessionType type, F1SessionState current, DateTimeOffset? lastEventAt, F1LifecycleEvent e)
    {
        if (lastEventAt is { } last && e.OccurredAt <= last)
            return Ignore(current, "not newer than the last applied event (duplicate or out of order)");

        if (current is F1SessionState.FinishedPendingResults or F1SessionState.Finalised or F1SessionState.Cancelled)
            return Ignore(current, "session already " + current);

        return e.Signal switch
        {
            F1LifecycleSignal.Started => current switch
            {
                F1SessionState.Scheduled or F1SessionState.Unknown => new(F1TransitionKind.FirstStart, F1SessionState.Started, "first start"),
                F1SessionState.Suspended => new(F1TransitionKind.Resume, F1SessionState.Started, "resumed after suspension"),
                // Started while started: next qualifying segment (or a replay) — the session continues, nothing new.
                _ => Ignore(current, "already started"),
            },
            F1LifecycleSignal.Suspended => current == F1SessionState.Started
                ? new(F1TransitionKind.Suspend, F1SessionState.Suspended, "suspended")
                : Ignore(current, "suspension outside a running session"),
            F1LifecycleSignal.Finished => Finish(type, current, e),
            F1LifecycleSignal.Cancelled => new(F1TransitionKind.Cancel, F1SessionState.Cancelled, "cancelled by provider"),
            _ => Ignore(current, "unknown signal"),
        };
    }

    private static F1Transition Finish(F1SessionType type, F1SessionState current, F1LifecycleEvent e)
    {
        if (F1SessionTypes.IsSegmented(type))
        {
            if (e.QualifyingPhase is null)
                return Ignore(current, "qualifying finish without segment number (fail closed)");
            if (e.QualifyingPhase < FinalQualifyingPhase)
                return new(F1TransitionKind.SegmentEnd, current, "segment " + e.QualifyingPhase + " ended");
        }

        // A finish without an observed start is still provider-stated (the start may have been missed while offline);
        // it can never produce a start notification because no start was recorded.
        return new(F1TransitionKind.Finish, F1SessionState.FinishedPendingResults, "finished; results pending");
    }

    private static F1Transition Ignore(F1SessionState current, string reason) => new(F1TransitionKind.Ignored, current, reason);
}
