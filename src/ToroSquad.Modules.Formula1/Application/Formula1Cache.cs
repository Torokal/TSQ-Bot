using System.Text.Json;
using System.Text.Json.Serialization;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

/// <summary>State of one provider dataset: last good data + what happened on the last attempt.</summary>
public sealed record F1Feed<T>(T? Data, DateTimeOffset? FetchedAt, F1ProviderOutcome? LastOutcome, string? LastDetail, DateTimeOffset? LastAttemptAt, int ConsecutiveFailures)
    where T : class
{
    public static F1Feed<T> Empty { get; } = new(null, null, null, null, null, 0);

    public bool IsStale(DateTimeOffset now, TimeSpan staleAfter) => FetchedAt is null || now - FetchedAt > staleAfter;

    /// <summary>A failed attempt keeps the last good data (it turns stale) — a failure never becomes "no data".</summary>
    public F1Feed<T> Next(F1ProviderResult<T> result, DateTimeOffset attemptAt) =>
        result.HasData
            ? new F1Feed<T>(result.Value, result.At, result.Outcome, result.Detail, attemptAt, 0)
            : this with { LastOutcome = result.Outcome, LastDetail = result.Detail, LastAttemptAt = attemptAt, ConsecutiveFailures = ConsecutiveFailures + 1 };
}

/// <summary>Command-facing view of one session (built from the persisted snapshot).</summary>
public sealed record F1SessionView(
    F1Session Session,
    string MeetingName,
    string CircuitName,
    string? Country,
    string? Location,
    F1SessionState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? FinalisedAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? LastLifecycleEventAt,
    bool IsBaseline);

/// <summary>A stored classification plus when it was fetched.</summary>
public sealed record F1CachedResult(F1SessionResult Result, DateTimeOffset FetchedAt, DateTimeOffset FirstAvailableAt);

/// <summary>Connection state of the live lifecycle stream (for /f1 now, doctor and health — never guessed).</summary>
public enum F1LiveState
{
    NotConfigured = 0,
    Idle = 1,
    Connecting = 2,
    Connected = 3,
    BackingOff = 4,
    AuthFailed = 5,

    /// <summary>Stop requested but the previous connection has not finished closing yet (no new connection meanwhile).</summary>
    Stopping = 6,
}

public sealed record F1LiveStatus(F1LiveState State, DateTimeOffset? LastConnectedAt, DateTimeOffset? LastDisconnectedAt, int Reconnects, string? LastError)
{
    public static F1LiveStatus Initial(bool configured) => new(configured ? F1LiveState.Idle : F1LiveState.NotConfigured, null, null, 0, null);
}

/// <summary>
/// Process-wide cache of PUBLIC Formula 1 data. Slash commands read ONLY from here (never a provider call on the
/// interaction path); the background poller fills it and restores it from the database after a restart with the original
/// fetch times, so freshness stays honest.
/// </summary>
public sealed class Formula1Cache
{
    private readonly Lock _gate = new();
    private F1Feed<IReadOnlyList<F1SeasonSchedule>> _schedule = F1Feed<IReadOnlyList<F1SeasonSchedule>>.Empty;
    private F1Feed<F1StandingsSnapshot> _drivers = F1Feed<F1StandingsSnapshot>.Empty;
    private F1Feed<F1StandingsSnapshot> _constructors = F1Feed<F1StandingsSnapshot>.Empty;
    private IReadOnlyList<F1SessionView> _sessions = [];
    private IReadOnlyDictionary<string, F1CachedResult> _results = new Dictionary<string, F1CachedResult>();
    private F1LiveStatus _live = F1LiveStatus.Initial(false);
    private F1Feed<object> _lifecycle = F1Feed<object>.Empty;
    private F1Feed<object> _resultsFeed = F1Feed<object>.Empty;

    public F1Feed<IReadOnlyList<F1SeasonSchedule>> Schedule { get { lock (_gate) return _schedule; } }
    public F1Feed<F1StandingsSnapshot> DriverStandings { get { lock (_gate) return _drivers; } }
    public F1Feed<F1StandingsSnapshot> ConstructorStandings { get { lock (_gate) return _constructors; } }
    public IReadOnlyList<F1SessionView> Sessions { get { lock (_gate) return _sessions; } }
    public IReadOnlyDictionary<string, F1CachedResult> Results { get { lock (_gate) return _results; } }
    public F1LiveStatus Live { get { lock (_gate) return _live; } }

    /// <summary>Last lifecycle REST reconciliation outcome (value unused).</summary>
    public F1Feed<object> LifecycleFeed { get { lock (_gate) return _lifecycle; } }

    /// <summary>Last results-provider call outcome (value unused).</summary>
    public F1Feed<object> ResultsFeed { get { lock (_gate) return _resultsFeed; } }

    public F1Feed<F1StandingsSnapshot> Standings(F1StandingsKind kind) => kind == F1StandingsKind.Drivers ? DriverStandings : ConstructorStandings;

    public void SetSchedule(F1Feed<IReadOnlyList<F1SeasonSchedule>> feed)
    {
        lock (_gate)
            _schedule = feed;
    }

    public void UpdateStandings(F1StandingsKind kind, F1ProviderResult<F1StandingsSnapshot> result, DateTimeOffset attemptAt)
    {
        lock (_gate)
        {
            if (kind == F1StandingsKind.Drivers)
                _drivers = _drivers.Next(result, attemptAt);
            else
                _constructors = _constructors.Next(result, attemptAt);
        }
    }

    public void RestoreStandings(F1StandingsSnapshot snapshot, DateTimeOffset fetchedAt)
    {
        lock (_gate)
        {
            if (snapshot.Kind == F1StandingsKind.Drivers)
                _drivers = _drivers with { Data = snapshot, FetchedAt = fetchedAt };
            else
                _constructors = _constructors with { Data = snapshot, FetchedAt = fetchedAt };
        }
    }

    public void SetSessions(IReadOnlyList<F1SessionView> sessions, IReadOnlyDictionary<string, F1CachedResult> results)
    {
        lock (_gate)
        {
            _sessions = sessions;
            _results = results;
        }
    }

    public void SetLive(F1LiveStatus status)
    {
        lock (_gate)
            _live = status;
    }

    public void RecordLifecycleAttempt(F1ProviderOutcome outcome, string? detail, DateTimeOffset at) => Record(ref _lifecycle, outcome, detail, at);

    public void RecordResultsAttempt(F1ProviderOutcome outcome, string? detail, DateTimeOffset at) => Record(ref _resultsFeed, outcome, detail, at);

    private void Record(ref F1Feed<object> feed, F1ProviderOutcome outcome, string? detail, DateTimeOffset at)
    {
        lock (_gate)
        {
            var ok = outcome is F1ProviderOutcome.Success or F1ProviderOutcome.Partial;
            feed = ok
                ? new F1Feed<object>(new object(), at, outcome, detail, at, 0)
                : feed with { LastOutcome = outcome, LastDetail = detail, LastAttemptAt = at, ConsecutiveFailures = feed.ConsecutiveFailures + 1 };
        }
    }

    /// <summary>All known sessions of all loaded seasons, in time order.</summary>
    public IReadOnlyList<F1SessionView> SessionsOrdered() => Sessions.OrderBy(s => s.Session.ScheduledStartUtc).ToList();
}

/// <summary>
/// What commands may know about the providers: attribution keys and whether live lifecycle is available. Commands get
/// this instead of the provider interfaces, so no interaction handler can ever call a provider (architecture-tested).
/// </summary>
public sealed record F1Sources(string ScheduleKey, string LifecycleKey, string ResultsKey, string StandingsKey, bool LifecycleConfigured)
{
    public static F1Sources From(IF1ScheduleProvider schedule, IF1LifecycleProvider lifecycle, IF1ResultsProvider results, IF1StandingsProvider standings) =>
        new(schedule.AttributionKey, lifecycle.AttributionKey, results.AttributionKey, standings.AttributionKey, lifecycle.IsConfigured);
}

/// <summary>JSON for persisted normalized payloads (schedule, results, standings). Deterministic property order.</summary>
public static class F1Json
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default; // a corrupt row is treated as absent (it will be refetched), never as data
        }
    }
}
