using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Modules.Formula1.Providers;

/// <summary>
/// Outcome of a provider call. "Success" with an empty/absent value is a legitimate answer and is kept strictly apart
/// from every failure kind — an outage never looks like "no sessions", "cancelled" or "no result".
/// </summary>
public enum F1ProviderOutcome
{
    Success = 0,
    Partial = 1,
    NotConfigured = 2,
    AuthFailed = 3,
    QuotaExceeded = 4,
    Timeout = 5,
    TransportError = 6,
    SchemaError = 7,
    Unavailable = 8,
}

public sealed record F1ProviderResult<T>(F1ProviderOutcome Outcome, T? Value, string? Detail, DateTimeOffset At, TimeSpan? RetryAfter = null)
{
    public bool Succeeded => Outcome is F1ProviderOutcome.Success or F1ProviderOutcome.Partial;

    public bool HasData => Succeeded && Value is not null;

    public static F1ProviderResult<T> Ok(T? value, DateTimeOffset at) => new(F1ProviderOutcome.Success, value, null, at);

    public static F1ProviderResult<T> Fail(F1ProviderOutcome outcome, string detail, DateTimeOffset at, TimeSpan? retryAfter = null) =>
        new(outcome, default, detail, at, retryAfter);

    public F1ProviderResult<TOut> WithoutValue<TOut>() => new(Outcome, default, Detail, At, RetryAfter);
}

/// <summary>Formula1:Provider:Mode. Fixture is explicit and labelled TEST/DEMO; Live is real network only (no fallback).</summary>
public enum F1ProviderMode
{
    Fixture = 0,
    Live = 1,
}

public sealed record F1DataMode(F1ProviderMode Mode)
{
    public bool IsDemo => Mode == F1ProviderMode.Fixture;
}

// Provider selection per capability. Each can be replaced independently (e.g. a future commercial provider) without
// touching commands, planning, persistence or rendering: those only see the interfaces below.
public enum F1ScheduleProviderName
{
    Jolpica = 0,
}

public enum F1LifecycleProviderName
{
    OpenF1 = 0,

    /// <summary>No lifecycle provider: start notifications are impossible (never faked from the clock).</summary>
    None = 1,
}

public enum F1ResultsProviderName
{
    OpenF1 = 0,
}

public enum F1StandingsProviderName
{
    Jolpica = 0,
}

/// <summary>A provider's own view of one session (used only to map it onto the normalized schedule).</summary>
public sealed record F1ProviderSession(
    string ProviderId,
    string Ref,
    int Season,
    F1SessionType Type,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    bool IsCancelled,
    string? MeetingName = null);

/// <summary>Season calendar (meetings and sessions with scheduled times).</summary>
public interface IF1ScheduleProvider
{
    string Id { get; }

    /// <summary>Localization key of the source attribution shown to users.</summary>
    string AttributionKey { get; }
    bool IsConfigured { get; }

    /// <summary>A season that the provider does not know (yet) is a success with zero meetings, not a failure.</summary>
    Task<F1ProviderResult<F1SeasonSchedule>> GetScheduleAsync(int season, CancellationToken cancellationToken);
}

/// <summary>Live session lifecycle (start / suspension / finish). Never inferred from the schedule.</summary>
public interface IF1LifecycleProvider
{
    string Id { get; }
    string AttributionKey { get; }

    /// <summary>False when live access (credentials/plan) is missing: lifecycle is then honestly unavailable.</summary>
    bool IsConfigured { get; }

    Task<F1ProviderResult<IReadOnlyList<F1ProviderSession>>> GetSessionsAsync(int season, CancellationToken cancellationToken);

    /// <summary>All lifecycle events the provider currently has for a session, oldest first (reconciliation after reconnects).</summary>
    Task<F1ProviderResult<IReadOnlyList<F1LifecycleEvent>>> GetLifecycleEventsAsync(string providerSessionRef, CancellationToken cancellationToken);
}

/// <summary>Session classifications.</summary>
public interface IF1ResultsProvider
{
    string Id { get; }
    string AttributionKey { get; }
    bool IsConfigured { get; }

    /// <summary>
    /// How long after a session's end the provider can serve its classification at all with the current access level
    /// (e.g. OpenF1 without live access: after its live window). Polling starts no earlier.
    /// </summary>
    TimeSpan AvailabilityDelay { get; }

    Task<F1ProviderResult<IReadOnlyList<F1ProviderSession>>> GetSessionsAsync(int season, CancellationToken cancellationToken);

    /// <summary>Success with a null value = not published yet (retry later). Never a guessed or partial classification.</summary>
    Task<F1ProviderResult<F1SessionResult>> GetResultAsync(F1Session session, string providerSessionRef, CancellationToken cancellationToken);
}

/// <summary>Championship standings (authoritative provider tables).</summary>
public interface IF1StandingsProvider
{
    string Id { get; }
    string AttributionKey { get; }
    bool IsConfigured { get; }

    Task<F1ProviderResult<F1StandingsSnapshot>> GetStandingsAsync(F1StandingsKind kind, int season, CancellationToken cancellationToken);
}

/// <summary>Raised by a live transport when the provider rejects the credentials (not retried aggressively).</summary>
public sealed class F1LiveAuthenticationException(string message) : Exception(message);

/// <summary>
/// One live connection to a streaming lifecycle source (OpenF1: MQTT over TLS). <see cref="RunConnectionAsync"/> connects,
/// subscribes, delivers normalized events and returns when the connection ends; reconnect/backoff is the listener's job.
/// A transport never talks to Discord.
/// </summary>
public interface IF1LiveTransport
{
    string Id { get; }
    bool IsConfigured { get; }

    Task RunConnectionAsync(Func<F1LifecycleEvent, CancellationToken, Task> onEvent, Action onConnected, CancellationToken cancellationToken);
}

/// <summary>Token bucket per provider budget (requests per period), so documented limits can never be exceeded by TSQ Bot.</summary>
public sealed class F1RequestBudget(TimeProvider clock)
{
    private readonly Dictionary<string, (double Tokens, DateTimeOffset At)> _buckets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public bool TryAcquire(string bucket, int capacity, TimeSpan period, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            capacity = Math.Max(1, capacity);
            var now = clock.GetUtcNow();
            var (tokens, at) = _buckets.GetValueOrDefault(bucket, (capacity, now));
            tokens = Math.Min(capacity, tokens + ((now - at) / period * capacity));
            if (tokens >= 1)
            {
                _buckets[bucket] = (tokens - 1, now);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            _buckets[bucket] = (tokens, now);
            retryAfter = period * ((1 - tokens) / capacity);
            return false;
        }
    }
}
