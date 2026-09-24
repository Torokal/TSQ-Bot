namespace ToroSquad.Modules.Esports.Domain;

public enum FilterDimension
{
    Team = 1,
    Tournament = 2,
    Tier = 3,
}

/// <summary>
/// Server-level notification filters (admins). Semantics (docs/ESPORTS_FILTERS.md):
/// <list type="bullet">
/// <item>Several values in the same dimension are OR-ed; different active dimensions are AND-ed.</item>
/// <item>Team: at least one of the two opponents is in the set.</item>
/// <item>VRS Top-N: at least one opponent is reliably matched to a VRS row with rank ≤ N. If VRS data is
/// unavailable, the filter FAILS CLOSED (no notification) and doctor reports why.</item>
/// <item>Empty filter set = all CS2 matches from the provider.</item>
/// </list>
/// Personal follows never widen this set.
/// </summary>
public sealed record GuildFilterSet(
    IReadOnlySet<string> TeamKeys,
    IReadOnlySet<string> TournamentKeys,
    IReadOnlySet<string> Tiers,
    int? VrsTopN)
{
    public static GuildFilterSet None { get; } = new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null);

    public bool IsEmpty => TeamKeys.Count == 0 && TournamentKeys.Count == 0 && Tiers.Count == 0 && VrsTopN is null;
}

public enum FilterVerdict
{
    Pass = 0,
    Reject = 1,

    /// <summary>A required input (VRS data) is missing: treated as reject, but reported separately.</summary>
    BlockedMissingData = 2,
}

public sealed record FilterDecision(FilterVerdict Verdict, IReadOnlyList<string> Reasons)
{
    public bool Passes => Verdict == FilterVerdict.Pass;
}

public static class MatchFilter
{
    public static FilterDecision Evaluate(EsportsMatch match, GuildFilterSet filters, TeamRankingResolver? rankings)
    {
        var reasons = new List<string>();

        if (filters.TeamKeys.Count > 0 && !match.Opponents.Any(o => o.Team is not null && filters.TeamKeys.Contains(o.Team.Key)))
            reasons.Add("team");

        if (filters.TournamentKeys.Count > 0 &&
            !filters.TournamentKeys.Contains(match.Tournament.Key) &&
            !(match.Tournament.ParentKey is not null && filters.TournamentKeys.Contains(match.Tournament.ParentKey)))
            reasons.Add("tournament");

        if (filters.Tiers.Count > 0 && (match.Tournament.Tier is null || !filters.Tiers.Contains(match.Tournament.Tier)))
            reasons.Add("tier");

        if (filters.VrsTopN is { } topN)
        {
            if (rankings is null)
                return new FilterDecision(FilterVerdict.BlockedMissingData, [.. reasons, "vrs_unavailable"]);

            var qualifies = match.Opponents
                .Where(o => o.Team is not null)
                .Select(o => rankings.Resolve(o.Team!))
                .Any(r => r.IsReliable && r.Entry!.Rank <= topN);
            if (!qualifies)
                reasons.Add("vrs");
        }

        return reasons.Count == 0 ? new FilterDecision(FilterVerdict.Pass, []) : new FilterDecision(FilterVerdict.Reject, reasons);
    }
}
