using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application.Automation;

/// <summary>How one provider call ended. Only <see cref="Ok"/> carries data; an empty list is data ("nothing scheduled"), never a failure.</summary>
public enum ProviderCallOutcome
{
    Ok = 0,
    NotConfigured = 1,

    /// <summary>401/403: the key is missing, wrong or not allowed — not retried in a loop.</summary>
    AuthFailed = 2,

    /// <summary>429: slow down (Retry-After when sent).</summary>
    RateLimited = 3,

    /// <summary>5xx, connection or TLS failure.</summary>
    Unavailable = 4,

    Timeout = 5,

    /// <summary>A 200 whose body is not what the documentation describes, or a 4xx other than the above.</summary>
    BadResponse = 6,
}

/// <summary>The provider's own usage headers (x-requests-remaining / -used / -last); null when a header was absent or unreadable.</summary>
public sealed record ProviderQuota(int? Remaining, int? Used, int? LastCost)
{
    public static ProviderQuota None { get; } = new(null, null, null);

    public bool Known => Remaining is not null;
}

/// <param name="MayHaveCost">A credit-consuming call whose cost is not known (timeout, dropped connection): count it as spent.</param>
public sealed record ProviderCall<T>(ProviderCallOutcome Outcome, T? Value, ProviderQuota Quota, int? HttpStatus = null, TimeSpan? RetryAfter = null, bool MayHaveCost = false)
    where T : class
{
    public bool Ok => Outcome == ProviderCallOutcome.Ok && Value is not null;
}

/// <summary>One competition of the provider's catalog.</summary>
public sealed record ProviderSport(string Key, string Title, bool Active);

/// <summary>One scheduled match of the free events list (planned kickoff only; the provider sends no live status).</summary>
public sealed record ProviderEvent(string Id, string SportKey, DateTimeOffset CommenceTime, string HomeTeam, string AwayTeam);

/// <summary>
/// The football data source of the automatic opener. Read-only; sends only sport keys, match ids, time bounds and fixed
/// market parameters — never a Discord user, role, message or wallet. Never throws for provider problems.
/// </summary>
public interface IFootballOddsProvider
{
    string Name { get; }

    bool IsConfigured { get; }

    /// <summary>The in-season competitions (free).</summary>
    Task<ProviderCall<IReadOnlyList<ProviderSport>>> GetSportsAsync(CancellationToken cancellationToken);

    /// <summary>Scheduled matches of one competition with a kickoff in [from, to] (free).</summary>
    Task<ProviderCall<IReadOnlyList<ProviderEvent>>> GetEventsAsync(string sportKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Full-time h2h odds of the given matches, one region, decimal (costs credits when anything is returned).</summary>
    Task<ProviderCall<IReadOnlyList<ProviderOddsEvent>>> GetOddsAsync(string sportKey, IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken);
}

/// <summary>The provider key (a secret), read once from configuration; the value is never logged, printed or returned.</summary>
public sealed class FootballOddsApiKey(string? value)
{
    public bool IsSet => !string.IsNullOrWhiteSpace(value);

    /// <summary>For the HTTP request only.</summary>
    public string Reveal() => value ?? "";
}
