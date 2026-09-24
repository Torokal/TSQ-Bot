using System.Globalization;
using System.Text;

namespace ToroSquad.Modules.Esports.Domain;

public sealed record RankingEntry(int Rank, int Points, string TeamName, IReadOnlyList<string> Roster);

/// <summary>
/// Valve Regional Standings (VRS) snapshot. <see cref="PublishedOn"/> is the standings date from Valve's file name,
/// <see cref="FetchedAt"/> when we downloaded it. VRS is shown as VRS — never renamed to "stars" or anything else.
/// </summary>
public sealed record RankingSnapshot(
    string Source,
    string Region,
    DateOnly PublishedOn,
    DateTimeOffset FetchedAt,
    string SourceUrl,
    IReadOnlyList<RankingEntry> Entries);

public enum TeamMatchKind
{
    NotFound = 0,
    Exact = 1,
    Alias = 2,
    Normalized = 3,
    Ambiguous = 4,
}

public sealed record TeamRankingMatch(TeamMatchKind Kind, RankingEntry? Entry, IReadOnlyList<RankingEntry> Candidates)
{
    /// <summary>Only unambiguous matches may be used for decisions (filters) — see docs/ESPORTS_FILTERS.md.</summary>
    public bool IsReliable => (Kind is TeamMatchKind.Exact or TeamMatchKind.Alias or TeamMatchKind.Normalized) && Entry is not null;
}

/// <summary>
/// Links a provider team (e.g. Liquipedia "Team Spirit") to a VRS row (e.g. "Spirit") using explicit, documented
/// rules. Anything uncertain is reported as <see cref="TeamMatchKind.Ambiguous"/> or NotFound — never guessed.
/// Order: 1) exact name/short-name, 2) configured alias, 3) normalized name (suffix/prefix stripping) if unique.
/// </summary>
public sealed class TeamRankingResolver
{
    private static readonly string[] StripWords = ["team", "esports", "esport", "gaming", "clan", "club", "gg"];

    private readonly RankingSnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly Dictionary<string, List<RankingEntry>> _exact;
    private readonly Dictionary<string, List<RankingEntry>> _normalized;

    /// <param name="aliases">team key (provider page) → exact VRS team name, from configuration.</param>
    public TeamRankingResolver(RankingSnapshot snapshot, IReadOnlyDictionary<string, string> aliases)
    {
        _snapshot = snapshot;
        _aliases = aliases;
        _exact = snapshot.Entries.GroupBy(e => Fold(e.TeamName)).ToDictionary(g => g.Key, g => g.ToList());
        _normalized = snapshot.Entries.GroupBy(e => Normalize(e.TeamName)).ToDictionary(g => g.Key, g => g.ToList());
    }

    public RankingSnapshot Snapshot => _snapshot;

    public TeamRankingMatch Resolve(TeamRef team)
    {
        foreach (var candidate in new[] { team.Name, team.ShortName }.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (_exact.TryGetValue(Fold(candidate!), out var hits))
                return hits.Count == 1 ? new(TeamMatchKind.Exact, hits[0], hits) : new(TeamMatchKind.Ambiguous, null, hits);
        }

        if (_aliases.TryGetValue(team.Key, out var aliasName))
        {
            if (_exact.TryGetValue(Fold(aliasName), out var hits) && hits.Count == 1)
                return new(TeamMatchKind.Alias, hits[0], hits);
            return new(TeamMatchKind.NotFound, null, []);
        }

        var normalizedHits = new[] { team.Name, team.ShortName }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => Normalize(n!))
            .Where(n => n.Length > 0)
            .Distinct()
            .SelectMany(n => _normalized.TryGetValue(n, out var list) ? list : [])
            .Distinct()
            .ToList();
        return normalizedHits.Count switch
        {
            0 => new(TeamMatchKind.NotFound, null, []),
            1 => new(TeamMatchKind.Normalized, normalizedHits[0], normalizedHits),
            _ => new(TeamMatchKind.Ambiguous, null, normalizedHits),
        };
    }

    /// <summary>Case/diacritic-insensitive comparison key (culture-invariant, so Turkish İ/ı do not break matching).</summary>
    public static string Fold(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.ToLowerInvariant(ch switch { 'ı' => 'i', 'İ' => 'i', _ => ch }));
        }

        return sb.ToString();
    }

    public static string Normalize(string value)
    {
        var words = Fold(value)
            .Split([' ', '-', '_', '.', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StripWords.Contains(w))
            .ToArray();
        return string.Join(' ', words);
    }
}
