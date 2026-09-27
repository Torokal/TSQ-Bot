using ToroSquad.Modules.Volleyball.Domain;

namespace ToroSquad.Modules.Volleyball.Providers;

/// <summary>
/// Outcome of one provider call. "Success" with an empty list is a legitimate answer and is kept strictly apart from every
/// failure kind — an outage never looks like "no matches", "cancelled" or "finished".
/// </summary>
public enum VbProviderOutcome
{
    Success = 0,
    NotConfigured = 1,
    Unauthorized = 2,
    RateLimited = 3,
    Timeout = 4,
    TransportError = 5,
    SchemaChanged = 6,
    Unavailable = 7,
}

/// <summary>Health of a provider as shown in doctor and /bot status (derived from recent outcomes, never guessed).</summary>
public enum VbProviderHealth
{
    Healthy = 0,
    Degraded = 1,
    RateLimited = 2,
    Unauthorized = 3,
    SchemaChanged = 4,
    Unavailable = 5,
    NotConfigured = 6,
}

/// <summary>What a provider can really deliver. A missing capability means "unavailable" — never filled with invented data.</summary>
[Flags]
public enum VbCapabilities
{
    None = 0,
    Fixtures = 1,
    Results = 2,
    LiveMatchState = 4,
    SetScores = 8,
    CurrentSetScore = 16,
    Broadcast = 32,
    TeamLogo = 64,
    Venue = 128,
}

public sealed record VbProviderResult<T>(VbProviderOutcome Outcome, T? Value, string? Detail, DateTimeOffset At, TimeSpan? RetryAfter = null)
{
    public bool Succeeded => Outcome == VbProviderOutcome.Success;

    public bool HasData => Succeeded && Value is not null;

    public static VbProviderResult<T> Ok(T value, DateTimeOffset at) => new(VbProviderOutcome.Success, value, null, at);

    public static VbProviderResult<T> Fail(VbProviderOutcome outcome, string detail, DateTimeOffset at, TimeSpan? retryAfter = null) =>
        new(outcome, default, detail, at, retryAfter);

    public VbProviderResult<TOut> WithoutValue<TOut>() => new(Outcome, default, Detail, At, RetryAfter);
}

/// <summary>Volleyball:Provider:Mode. Fixture is explicit synthetic TEST/DEMO data; Live is real network only (no fallback).</summary>
public enum VbProviderMode
{
    Fixture = 0,
    Live = 1,
}

public sealed record VbDataMode(VbProviderMode Mode)
{
    public bool IsDemo => Mode == VbProviderMode.Fixture;
}

/// <summary>Volleyball:Provider:Name. A new provider is one implementation of <see cref="IVolleyballDataProvider"/> plus one line of wiring.</summary>
public enum VbProviderName
{
    /// <summary>FIVB VIS web service (official; verified with real 2026 Türkiye women's data, docs/volleyball/PROVIDER_RESEARCH.md).</summary>
    FivbVis = 0,

    /// <summary>No provider: the module stays honest (NOT_CONFIGURED) and sends nothing.</summary>
    None = 1,
}

/// <summary>Volleyball:Provider:Name=None — no data source at all; nothing is ever inferred or sent.</summary>
public sealed class NoVolleyballProvider(TimeProvider clock) : IVolleyballDataProvider
{
    public string Id => "none";
    public string AttributionKey => "vb.source.none";
    public bool IsConfigured => false;
    public VbCapabilities Capabilities => VbCapabilities.None;

    public Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetMatchesAsync(TrackedTeamIdentity team, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) =>
        Task.FromResult(VbProviderResult<IReadOnlyList<VolleyballMatch>>.Fail(VbProviderOutcome.NotConfigured, "no volleyball provider configured", clock.GetUtcNow()));

    public Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetLiveStateAsync(IReadOnlyCollection<string> providerMatchIds, CancellationToken cancellationToken) =>
        Task.FromResult(VbProviderResult<IReadOnlyList<VolleyballMatch>>.Fail(VbProviderOutcome.NotConfigured, "no volleyball provider configured", clock.GetUtcNow()));
}

/// <summary>
/// One volleyball data source. Implementations normalize their payloads into <see cref="VolleyballMatch"/> and never talk
/// to Discord or the outbox (architecture-tested); the rest of the module only sees this interface, so replacing the
/// provider does not touch state, planning or cards.
/// </summary>
public interface IVolleyballDataProvider
{
    string Id { get; }

    /// <summary>Localization key of the source attribution shown to users.</summary>
    string AttributionKey { get; }

    bool IsConfigured { get; }

    VbCapabilities Capabilities { get; }

    /// <summary>
    /// Matches (fixtures and results) of the competitions/teams the provider can see in the UTC window, already normalized.
    /// The caller applies <see cref="TrackedTeamIdentity"/>; a provider should filter server-side where it can and must
    /// page through everything it requests (no silent truncation).
    /// </summary>
    Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetMatchesAsync(TrackedTeamIdentity team, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);

    /// <summary>Current state of the given matches (live polling). Unknown ids are simply absent from the result.</summary>
    Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetLiveStateAsync(IReadOnlyCollection<string> providerMatchIds, CancellationToken cancellationToken);
}

/// <summary>Token bucket per provider budget (requests per period), so documented limits can never be exceeded.</summary>
public sealed class VbRequestBudget(TimeProvider clock)
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
