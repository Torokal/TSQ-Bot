using ToroSquad.Modules.Esports.Domain;

namespace ToroSquad.Modules.Esports.Providers;

/// <summary>What a provider can actually deliver. Anything false is shown to users as "unavailable".</summary>
[Flags]
public enum ProviderCapability
{
    None = 0,
    Fixtures = 1,
    Results = 2,
    Tournaments = 4,
    Teams = 8,
    VerifiedLiveStatus = 16,
    Rankings = 32,
}

/// <summary>
/// Outcome kinds a provider call can have. "Success with zero items" is a legitimate empty result and is kept
/// strictly apart from every failure kind — an API error must never look like "no matches".
/// </summary>
public enum ProviderOutcome
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

public sealed record ProviderResult<T>(
    ProviderOutcome Outcome,
    T? Value,
    string? Detail,
    DateTimeOffset At,
    TimeSpan? RetryAfter = null,
    IReadOnlyList<string>? Warnings = null)
{
    public bool HasData => (Outcome is ProviderOutcome.Success or ProviderOutcome.Partial) && Value is not null;

    public static ProviderResult<T> Ok(T value, DateTimeOffset at, IReadOnlyList<string>? warnings = null) =>
        new(ProviderOutcome.Success, value, null, at, null, warnings);

    public static ProviderResult<T> PartialData(T value, string detail, DateTimeOffset at, IReadOnlyList<string>? warnings = null) =>
        new(ProviderOutcome.Partial, value, detail, at, null, warnings);

    public static ProviderResult<T> Fail(ProviderOutcome outcome, string detail, DateTimeOffset at, TimeSpan? retryAfter = null) =>
        new(outcome, default, detail, at, retryAfter);

    public ProviderResult<TOut> WithoutValue<TOut>() => new(Outcome, default, Detail, At, RetryAfter, Warnings);
}

/// <summary>Time window for fixture/result queries (UTC).</summary>
public sealed record MatchWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc);

public interface IEsportsDataProvider
{
    string Id { get; }
    ProviderCapability Capabilities { get; }
    bool IsConfigured { get; }

    /// <summary>
    /// All matches whose scheduled date falls in the window — upcoming, started-but-unfinished and finished alike, so
    /// in-progress matches are never missed. Implementations paginate with a hard page limit.
    /// </summary>
    Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken);

    Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>A team from a provider's team catalog; <see cref="Location"/> (country code) helps tell same-named teams apart.</summary>
public sealed record TeamSearchHit(TeamRef Team, string? Location);

/// <summary>Optional: providers with a searchable team catalog (teams without a match in the poll window).</summary>
public interface ITeamSearchProvider
{
    Task<ProviderResult<IReadOnlyList<TeamSearchHit>>> SearchTeamsAsync(string query, CancellationToken cancellationToken);
}

public interface IRankingsProvider
{
    string Id { get; }
    bool IsConfigured { get; }
    Task<ProviderResult<RankingSnapshot>> GetLatestAsync(CancellationToken cancellationToken);
}
