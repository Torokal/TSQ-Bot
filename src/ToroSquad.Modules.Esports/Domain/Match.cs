using System.Text.Json.Serialization;

namespace ToroSquad.Modules.Esports.Domain;

/// <summary>
/// Stable identity of a match: data source + the source's own match id. Team names are never used as identity.
/// </summary>
public readonly record struct MatchKey(string Source, string Id)
{
    public override string ToString() => $"{Source}:{Id}";

    public static bool TryParse(string value, out MatchKey key)
    {
        var i = value.LastIndexOf(':');
        key = i > 0 && i < value.Length - 1 ? new MatchKey(value[..i], value[(i + 1)..]) : default;
        return i > 0 && i < value.Length - 1;
    }
}

/// <summary>
/// Match lifecycle as supported by provider evidence. Reaching the scheduled start time is NOT evidence of
/// <see cref="Live"/> (running): only a provider that states it (PandaScore `running`) may set it; Liquipedia never does.
/// A rescheduled match stays <see cref="Scheduled"/> with <see cref="EsportsMatch.Rescheduled"/> set. <see cref="Unknown"/>
/// stays unknown — it is never turned into running/finished/cancelled.
/// </summary>
public enum MatchStatus
{
    Unknown = 0,
    Scheduled = 1,
    Live = 2,
    Finished = 3,
    Postponed = 4,
    Cancelled = 5,
}

public enum OpponentKind
{
    Unknown = 0,
    Team = 1,
    Tbd = 2,
}

/// <summary>Per-opponent outcome as stated by the source (Liquipedia opponent status S/W/L/D/FF/DQ).</summary>
public enum OpponentResult
{
    None = 0,
    Scored = 1,
    Win = 2,
    Loss = 3,
    Draw = 4,
    Forfeit = 5,
    Disqualified = 6,
}

public enum GameStatus
{
    Unknown = 0,
    Played = 1,
    NotPlayed = 2,
}

/// <summary>
/// Team reference as the source identifies it. <see cref="Key"/> is the source page/id, not a display name.
/// <see cref="LogoUrl"/> is an already validated provider logo (<see cref="TeamLogoPolicy"/>) or null. It is purely
/// decorative, so it is not stored in match snapshots and never makes a match count as "changed".
/// </summary>
public sealed record TeamRef(string Source, string Key, string Name, string? ShortName,
    [property: JsonIgnore] string? LogoUrl = null)
{
    public string Display => Name;
}

public sealed record MatchOpponent(OpponentKind Kind, TeamRef? Team, int? Score, OpponentResult Result)
{
    public static MatchOpponent Tbd { get; } = new(OpponentKind.Tbd, null, null, OpponentResult.None);
    public static MatchOpponent UnknownOpponent { get; } = new(OpponentKind.Unknown, null, null, OpponentResult.None);

    public bool IsTeam => Kind == OpponentKind.Team && Team is not null;
}

public sealed record MapGame(int Index, string? MapName, GameStatus Status, int? ScoreA, int? ScoreB, int? WinnerIndex);

public sealed record TournamentRef(string Source, string Key, string Name, string? Tier, string? TierType, string? PublisherTier, string? ParentKey);

public sealed record StreamLink(string Platform, string Url);

/// <summary>
/// External pages for a match, kept apart from data ingestion. <see cref="HltvMatchUrl"/> may only come from a trusted,
/// deterministic source (curated mapping, authorized provider/API) and must pass <see cref="MatchLinkPolicy"/> — TSQ Bot
/// never scrapes HLTV and never builds an HLTV URL from a guessed id.
/// </summary>
public sealed record MatchLinks(string? HltvMatchUrl = null, string? OfficialMatchUrl = null, string? ProviderMatchUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HltvVia = null)
{
    /// <summary><see cref="HltvVia"/> value: the HLTV link was found automatically in Liquipedia data (credited in the card footer).</summary>
    public const string ViaLiquipedia = "liquipedia";

    public static MatchLinks None { get; } = new();
}

/// <summary>
/// Provider-independent CS2 match. Everything that the source did not state stays null — we never invent scores,
/// winners or times. <see cref="SeriesScoreKnown"/> and <see cref="MapsComplete"/> are kept separate on purpose.
/// </summary>
public sealed record EsportsMatch(
    MatchKey Key,
    TournamentRef Tournament,
    DateTimeOffset? ScheduledStartUtc,
    bool StartTimeExact,
    int? BestOf,
    MatchStatus Status,
    string StatusEvidence,
    MatchOpponent A,
    MatchOpponent B,
    int? WinnerIndex,
    bool IsDraw,
    bool IsForfeit,
    IReadOnlyList<MapGame> Maps,
    string? Stage,
    string? SourceUrl,
    IReadOnlyList<StreamLink> Streams,
    DateTimeOffset? BeginAtUtc = null,
    DateTimeOffset? EndAtUtc = null,
    DateTimeOffset? OriginalScheduledStartUtc = null,
    bool Rescheduled = false,
    MatchLinks? Links = null)
{
    public IEnumerable<MatchOpponent> Opponents => [A, B];

    public bool InvolvesTeam(string teamKey) =>
        (A.Team?.Key == teamKey) || (B.Team?.Key == teamKey);

    public bool SeriesScoreKnown => A.Score is not null && B.Score is not null;

    /// <summary>
    /// True only when every map the series score implies is present with both scores. A finished BO3 that shows
    /// 2-1 but lists only two maps is "incomplete" — we then show the series score only.
    /// </summary>
    public bool MapsComplete
    {
        get
        {
            if (!SeriesScoreKnown)
                return false;
            var played = Maps.Where(m => m.Status == GameStatus.Played).ToList();
            if (played.Count != A.Score + B.Score)
                return false;
            return played.All(m => m.ScoreA is not null && m.ScoreB is not null && m.WinnerIndex is not null);
        }
    }

    public string? WinnerName => WinnerIndex switch
    {
        0 => A.Team?.Name,
        1 => B.Team?.Name,
        _ => null,
    };
}

/// <summary>A tournament/event listing.</summary>
public sealed record EsportsEvent(
    TournamentRef Tournament,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? Location,
    string? Type,
    double? PrizePoolUsd,
    int? Participants,
    string? SourceUrl);
