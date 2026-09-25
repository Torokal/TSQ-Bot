using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>One team suggestion: display text (name, acronym and location to tell same-named teams apart) and its key.</summary>
public sealed record TeamSuggestion(string Display, string Key);

/// <summary>
/// Team picker for filters, follows and role mappings. Known teams (seen in match data) come first; from
/// <see cref="MinSearchLength"/> characters the provider's team catalog is searched too, so a team without a match in the
/// poll window (e.g. between tournaments) can still be picked. Searches are single-page, time-boxed (autocomplete must
/// answer within 3 s), cached (a shorter query whose result was complete is reused) and share the provider's request
/// budget; any failure simply means "known teams only". Picked hits are remembered in <see cref="EsportsCache"/> so the
/// chosen key resolves to a name for labels and follows.
/// </summary>
public sealed class TeamDirectory(EsportsCache cache, IEsportsDataProvider provider, TimeProvider clock, ILogger<TeamDirectory> logger)
{
    public const int MinSearchLength = 3;
    public const int MaxSuggestions = 25;

    /// <summary>Provider page size for a search: fewer hits than this means the result is complete.</summary>
    public const int SearchPageSize = 100;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<TeamSearchHit> Hits)> _memo = new(StringComparer.Ordinal);

    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <param name="typed">Already folded (see <see cref="TeamRankingResolver.Fold"/>).</param>
    public async Task<IReadOnlyList<TeamSuggestion>> SuggestAsync(string typed, CancellationToken cancellationToken)
    {
        var hits = typed.Length >= MinSearchLength && provider is ITeamSearchProvider search
            ? await SearchAsync(search, typed, cancellationToken)
            : [];
        if (hits.Count > 0)
            cache.RememberTeams(hits.Select(h => h.Team));

        var byKey = hits.GroupBy(h => h.Team.Key).ToDictionary(g => g.Key, g => g.First());
        var result = new List<TeamSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var team in cache.Teams.Where(t => Matches(typed, t.Name, t.ShortName)))
        {
            if (seen.Add(team.Key))
                result.Add(new(byKey.TryGetValue(team.Key, out var hit) ? Display(hit) : Display(team, null), team.Key));
        }

        foreach (var hit in hits.Where(h => seen.Add(h.Team.Key)))
            result.Add(new(Display(hit), hit.Team.Key));
        return result.Take(MaxSuggestions).ToList();
    }

    private async Task<IReadOnlyList<TeamSearchHit>> SearchAsync(ITeamSearchProvider search, string typed, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_memo.TryGetValue(typed, out var exact) && now - exact.At < CacheTtl)
            return exact.Hits;
        // A complete result for a shorter prefix already contains every longer match.
        for (var length = typed.Length - 1; length >= MinSearchLength; length--)
        {
            if (_memo.TryGetValue(typed[..length], out var prefix) && now - prefix.At < CacheTtl && prefix.Hits.Count < SearchPageSize)
                return prefix.Hits.Where(h => Matches(typed, h.Team.Name, h.Team.ShortName)).ToList();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchTimeout);
        try
        {
            var result = await search.SearchTeamsAsync(typed, timeout.Token);
            if (!result.HasData)
            {
                logger.LogInformation("Team search unavailable ({Outcome}); showing known teams only", result.Outcome);
                return [];
            }

            _memo[typed] = (now, result.Value!);
            return result.Value!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Team search timed out after {Timeout}; showing known teams only", SearchTimeout);
            return [];
        }
    }

    private static bool Matches(string typed, params string?[] candidates) =>
        typed.Length == 0 || candidates.Any(c => c is not null && TeamRankingResolver.Fold(c).Contains(typed, StringComparison.Ordinal));

    private static string Display(TeamSearchHit hit) => Display(hit.Team, hit.Location);

    private static string Display(TeamRef team, string? location)
    {
        var details = new[] { team.ShortName is { } s && s != team.Name ? s : null, location }.OfType<string>().ToList();
        return details.Count == 0 ? team.Name : $"{team.Name} ({string.Join(" · ", details)})";
    }
}
