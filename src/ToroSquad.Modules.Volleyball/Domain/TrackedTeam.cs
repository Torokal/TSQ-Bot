namespace ToroSquad.Modules.Volleyball.Domain;

public enum TeamVerdict
{
    /// <summary>Definitely not the followed team.</summary>
    NoMatch = 0,

    /// <summary>Definitely the followed team (stable provider id and/or complete structured identity).</summary>
    Match = 1,

    /// <summary>Could be the followed team but the identity is incomplete or contradictory: treated as "not followed".</summary>
    Ambiguous = 2,
}

public enum FollowedSide
{
    Home = 0,
    Away = 1,
}

/// <summary>Why a match was accepted or rejected (logged; ambiguity and conflicts are also surfaced in doctor).</summary>
public enum MatchFilterReason
{
    Accepted = 0,
    NotInvolved = 1,
    AmbiguousIdentity = 2,
    BothSidesMatch = 3,
    ProviderIdConflict = 4,
}

public sealed record MatchFilterResult(MatchFilterReason Reason, FollowedSide? Side)
{
    public bool Accepted => Reason == MatchFilterReason.Accepted;
}

/// <summary>
/// The single team this module follows, as an explicit identity: country code + gender + level + national team, plus
/// optional stable provider team ids. Display names ("Turkey", "Türkiye", "Turkey W") are never used to decide.
/// </summary>
public sealed record TrackedTeamIdentity(string Key, string CountryCode, TeamGender Gender, TeamLevel Level, IReadOnlyCollection<string> ProviderTeamIds)
{
    public const string TurkeyWomenSeniorKey = "nt:TUR:women:senior";

    /// <summary>Filenin Sultanları: Türkiye women's senior national team.</summary>
    public static TrackedTeamIdentity TurkeyWomenSenior(IReadOnlyCollection<string>? providerTeamIds = null) =>
        new(TurkeyWomenSeniorKey, "TUR", TeamGender.Women, TeamLevel.Senior, providerTeamIds ?? []);

    /// <summary>
    /// Match: a configured stable provider id whose structured fields do not contradict the identity, or (without a
    /// configured id) complete structured fields: national team, this country, this gender, senior, no age limit.
    /// Anything of this country with missing/unknown gender, level or kind is <see cref="TeamVerdict.Ambiguous"/>.
    /// A configured provider id whose data contradicts the identity (e.g. says "men") is ambiguous, never trusted.
    /// </summary>
    public TeamVerdict Classify(VolleyballTeam team)
    {
        var sameCountry = string.Equals(team.CountryCode, CountryCode, StringComparison.OrdinalIgnoreCase);
        var idListed = team.ProviderTeamId is { } id && ProviderTeamIds.Contains(id, StringComparer.Ordinal);
        var contradicts = (team.CountryCode is not null && !sameCountry) ||
                          (team.Gender != TeamGender.Unknown && team.Gender != Gender) ||
                          (team.Level != TeamLevel.Unknown && team.Level != Level) ||
                          team.AgeLimit is not null ||
                          team.Kind == TeamKind.Club;
        if (idListed)
            return contradicts ? TeamVerdict.Ambiguous : TeamVerdict.Match;
        if (!sameCountry)
            return TeamVerdict.NoMatch;
        if (contradicts)
            return TeamVerdict.NoMatch; // e.g. Türkiye men, Türkiye U19 women, a Turkish club
        var complete = team.Gender == Gender && team.Level == Level && team.Kind == TeamKind.NationalTeam;
        return complete ? TeamVerdict.Match : TeamVerdict.Ambiguous;
    }

    /// <summary>Accept only when exactly one side is certainly the followed team and the other side is certainly not it.</summary>
    public MatchFilterResult Evaluate(VolleyballMatch match)
    {
        var home = Classify(match.HomeTeam);
        var away = Classify(match.AwayTeam);
        if (home == TeamVerdict.Match && away == TeamVerdict.Match)
            return new(MatchFilterReason.BothSidesMatch, null);
        if (home == TeamVerdict.Ambiguous || away == TeamVerdict.Ambiguous)
        {
            var listedButContradicting = IsListed(match.HomeTeam) || IsListed(match.AwayTeam);
            return new(listedButContradicting ? MatchFilterReason.ProviderIdConflict : MatchFilterReason.AmbiguousIdentity, null);
        }

        if (home == TeamVerdict.Match)
            return new(MatchFilterReason.Accepted, FollowedSide.Home);
        if (away == TeamVerdict.Match)
            return new(MatchFilterReason.Accepted, FollowedSide.Away);
        return new(MatchFilterReason.NotInvolved, null);
    }

    private bool IsListed(VolleyballTeam team) => team.ProviderTeamId is { } id && ProviderTeamIds.Contains(id, StringComparer.Ordinal);
}
