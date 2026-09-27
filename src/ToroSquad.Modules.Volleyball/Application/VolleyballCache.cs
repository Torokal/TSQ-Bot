using System.Text.Json;
using System.Text.Json.Serialization;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

/// <summary>Outcome history of one provider dataset (fixtures or live state) — last success + last attempt.</summary>
public sealed record VbFeed(DateTimeOffset? FetchedAt, VbProviderOutcome? LastOutcome, string? LastDetail, DateTimeOffset? LastAttemptAt, int ConsecutiveFailures,
    TimeSpan? RetryAfter = null)
{
    public static VbFeed Empty { get; } = new(null, null, null, null, 0);

    public bool IsStale(DateTimeOffset now, TimeSpan staleAfter) => FetchedAt is null || now - FetchedAt > staleAfter;

    /// <summary>A failure keeps the last success time (the data turns stale) — a failure never becomes "no data".</summary>
    public VbFeed Next<T>(VbProviderResult<T> result, DateTimeOffset attemptAt) =>
        result.Succeeded
            ? new VbFeed(result.At, result.Outcome, result.Detail, attemptAt, 0)
            : this with { LastOutcome = result.Outcome, LastDetail = result.Detail, LastAttemptAt = attemptAt, ConsecutiveFailures = ConsecutiveFailures + 1, RetryAfter = result.RetryAfter };

    /// <summary>Health as shown to admins (never guessed: no attempt yet = not configured/unknown handled by the caller).</summary>
    public VbProviderHealth Health(DateTimeOffset now, TimeSpan staleAfter) => LastOutcome switch
    {
        null => VbProviderHealth.Unavailable,
        VbProviderOutcome.NotConfigured => VbProviderHealth.NotConfigured,
        VbProviderOutcome.Unauthorized => VbProviderHealth.Unauthorized,
        VbProviderOutcome.RateLimited => VbProviderHealth.RateLimited,
        VbProviderOutcome.SchemaChanged => VbProviderHealth.SchemaChanged,
        VbProviderOutcome.Success => IsStale(now, staleAfter) ? VbProviderHealth.Degraded : VbProviderHealth.Healthy,
        _ => FetchedAt is null || IsStale(now, staleAfter) ? VbProviderHealth.Unavailable : VbProviderHealth.Degraded,
    };
}

/// <summary>Command-facing view of one followed match (built from the persisted snapshot).</summary>
public sealed record VbMatchView(
    string MatchKey,
    string Provider,
    string CompetitionName,
    string? Stage,
    string? Round,
    DateTimeOffset? StartTimeUtc,
    FollowedSide FollowedSide,
    string HomeName,
    string? HomeCode,
    string AwayName,
    string? AwayCode,
    string? HomeLogoUrl,
    string? AwayLogoUrl,
    string? Venue,
    string? City,
    IReadOnlyList<string> Broadcasts,
    VolleyballMatchStatus Status,
    int HomeSets,
    int AwaySets,
    IReadOnlyList<SetResult> Sets,
    int? CurrentSet,
    int? CurrentSetHomePoints,
    int? CurrentSetAwayPoints,
    bool Started,
    bool Finished,
    bool Postponed,
    bool Cancelled,
    DateTimeOffset LastObservedAt)
{
    public string FollowedName => FollowedSide == FollowedSide.Home ? HomeName : AwayName;
    public string OpponentName => FollowedSide == FollowedSide.Home ? AwayName : HomeName;
    public string? OpponentCode => FollowedSide == FollowedSide.Home ? AwayCode : HomeCode;
    public int FollowedSets => FollowedSide == FollowedSide.Home ? HomeSets : AwaySets;
    public int OpponentSets => FollowedSide == FollowedSide.Home ? AwaySets : HomeSets;
}

/// <summary>
/// A recorded transition (persisted in the snapshot's EventsJson). <see cref="Since"/> = the previous trustworthy observation
/// of the match: the transition happened between the two (used to keep re-enabled guilds from receiving history).
/// </summary>
public sealed record VbEvent(string Kind, int? Set, DateTimeOffset At, bool Announce, DateTimeOffset? Since = null)
{
    public const string Started = "started";
    public const string SetCompleted = "set";
    public const string Final = "final";
    public const string Postponed = "postponed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Process-wide cache of PUBLIC volleyball data. Slash commands read ONLY from here (never a provider call on the
/// interaction path); the poller fills it and restores it from the database after a restart with the original times.
/// </summary>
public sealed class VolleyballCache
{
    private readonly Lock _gate = new();
    private VbFeed _fixtures = VbFeed.Empty;
    private VbFeed _live = VbFeed.Empty;
    private IReadOnlyList<VbMatchView> _matches = [];
    private int _ambiguous;

    public VbFeed Fixtures { get { lock (_gate) return _fixtures; } }
    public VbFeed Live { get { lock (_gate) return _live; } }
    public IReadOnlyList<VbMatchView> Matches { get { lock (_gate) return _matches; } }

    /// <summary>Matches rejected in the last discovery because the team identity was ambiguous/contradictory (doctor).</summary>
    public int AmbiguousRejected { get { lock (_gate) return _ambiguous; } }

    public void RecordFixtures<T>(VbProviderResult<T> result, DateTimeOffset attemptAt, int? ambiguousRejected = null)
    {
        lock (_gate)
        {
            _fixtures = _fixtures.Next(result, attemptAt);
            if (ambiguousRejected is { } a)
                _ambiguous = a;
        }
    }

    public void RecordLive<T>(VbProviderResult<T> result, DateTimeOffset attemptAt)
    {
        lock (_gate)
            _live = _live.Next(result, attemptAt);
    }

    public void Restore(VbFeed fixtures, VbFeed live)
    {
        lock (_gate)
        {
            _fixtures = fixtures;
            _live = live;
        }
    }

    public void SetMatches(IReadOnlyList<VbMatchView> matches)
    {
        lock (_gate)
            _matches = matches;
    }

    public IReadOnlyList<VbMatchView> MatchesOrdered() => Matches.OrderBy(m => m.StartTimeUtc ?? DateTimeOffset.MaxValue).ToList();
}

/// <summary>
/// What commands may know about the provider: attribution and capabilities. Commands get this instead of the provider
/// interface, so no interaction handler can ever call a provider (architecture-tested).
/// </summary>
public sealed record VbSources(string AttributionKey, VbCapabilities Capabilities, bool Configured)
{
    public static VbSources From(IVolleyballDataProvider provider) => new(provider.AttributionKey, provider.Capabilities, provider.IsConfigured);
}

/// <summary>JSON for persisted normalized payloads. A corrupt value reads as absent (refetched), never as data.</summary>
public static class VbJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
