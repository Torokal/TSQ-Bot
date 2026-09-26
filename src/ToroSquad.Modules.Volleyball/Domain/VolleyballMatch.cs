namespace ToroSquad.Modules.Volleyball.Domain;

public enum TeamGender
{
    Unknown = 0,
    Women = 1,
    Men = 2,
}

/// <summary>Senior = open-age national team. Anything age-limited (U17…U23, junior, youth) is <see cref="AgeGroup"/>.</summary>
public enum TeamLevel
{
    Unknown = 0,
    Senior = 1,
    AgeGroup = 2,
}

/// <summary>What kind of side this is. Club teams are never followed by this module.</summary>
public enum TeamKind
{
    Unknown = 0,
    NationalTeam = 1,
    Club = 2,
}

/// <summary>
/// A normalized team. <see cref="StableId"/> is TSQ's own key ("<c>&lt;provider&gt;:&lt;provider team id&gt;</c>" or the
/// provider-independent "<c>nt:TUR:women:senior</c>" when the identity is fully structured). Identity decisions use the
/// structured fields and provider ids — never the display name alone.
/// </summary>
public sealed record VolleyballTeam(
    string StableId,
    string? ProviderTeamId,
    string Name,
    string? CountryCode,
    TeamGender Gender,
    TeamLevel Level,
    TeamKind Kind,
    int? AgeLimit = null,
    string? LogoUrl = null);

/// <summary>
/// Normalized match status. <see cref="Scheduled"/> covers "not started yet" (including pre-match warm-up, which no
/// provider distinguishes reliably); <see cref="Live"/> is only ever set from provider data, never from the clock.
/// </summary>
public enum VolleyballMatchStatus
{
    Unknown = 0,
    Scheduled = 1,
    Live = 2,
    Suspended = 3,
    Finished = 4,
    Postponed = 5,
    Cancelled = 6,
}

/// <summary>Points of one set. <see cref="Completed"/> is decided by the provider's set count, not by the points alone.</summary>
public sealed record VolleyballSet(int Number, int HomePoints, int AwayPoints, bool Completed);

/// <summary>
/// Provider-independent view of one match. Absent values stay null (never invented): a provider that cannot supply
/// set points or broadcasts leaves them empty and says so through its capabilities.
/// </summary>
public sealed record VolleyballMatch(
    string MatchId,
    string Provider,
    string ProviderMatchId,
    string? CompetitionId,
    string CompetitionName,
    int? Season,
    string? Stage,
    string? Round,
    DateTimeOffset? StartTimeUtc,
    VolleyballTeam HomeTeam,
    VolleyballTeam AwayTeam,
    VolleyballMatchStatus Status,
    int? HomeSets,
    int? AwaySets,
    IReadOnlyList<VolleyballSet> Sets,
    int? CurrentSet,
    int? CurrentSetHomePoints,
    int? CurrentSetAwayPoints,
    string? Venue,
    string? City,
    IReadOnlyList<string> Broadcasts,
    DateTimeOffset? LastProviderUpdateUtc,
    bool IsStale = false)
{
    public static string Key(string provider, string providerMatchId) => provider + ":" + providerMatchId;

    public int CompletedSets => (HomeSets ?? 0) + (AwaySets ?? 0);
}
