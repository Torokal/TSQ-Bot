using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Modules.Live.Providers;

public enum LiveProviderOutcome
{
    Ok = 0,
    NotConfigured = 1,
    AuthFailed = 2,
    RateLimited = 3,
    Timeout = 4,
    TransportError = 5,
    SchemaError = 6,
}

/// <summary>
/// One provider answer. Only <see cref="LiveProviderOutcome.Ok"/> carries observations; every other outcome means
/// "state unknown" — the caller keeps the previous known state (never "offline"). <see cref="Warnings"/> lists channels
/// the provider did not describe (e.g. unknown slug): those get no observation either.
/// </summary>
public sealed record LiveProviderResult(
    LiveProviderOutcome Outcome,
    IReadOnlyList<LiveObservation> Observations,
    string? Detail,
    DateTimeOffset At,
    TimeSpan? RetryAfter = null,
    IReadOnlyList<string>? Warnings = null)
{
    public bool Succeeded => Outcome == LiveProviderOutcome.Ok;

    public static LiveProviderResult Ok(IReadOnlyList<LiveObservation> observations, DateTimeOffset at, IReadOnlyList<string>? warnings = null) =>
        new(LiveProviderOutcome.Ok, observations, null, at, null, warnings);

    public static LiveProviderResult Fail(LiveProviderOutcome outcome, string detail, DateTimeOffset at, TimeSpan? retryAfter = null) =>
        new(outcome, [], detail, at, retryAfter);
}

/// <summary>
/// Official-API status source of one platform. One call describes ALL requested channels (batch endpoints), so the number
/// of tracked channels never multiplies requests. Implementations never scrape pages and never log tokens.
/// </summary>
public interface ILiveStatusProvider
{
    LivePlatform Platform { get; }

    bool IsConfigured { get; }

    /// <summary>Auth state for doctor (cached; no request).</summary>
    LiveAuthState Auth { get; }

    Task<LiveProviderResult> GetStatusAsync(IReadOnlyCollection<string> logins, CancellationToken cancellationToken);
}

public sealed record LiveAuthState(LiveProviderOutcome? LastOutcome, DateTimeOffset? TokenValidUntil, DateTimeOffset? LastValidatedAt);
