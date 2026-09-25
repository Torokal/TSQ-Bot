using Microsoft.Extensions.Options;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>State of one provider dataset: last good data + what happened on the last attempt.</summary>
public sealed record FeedState<T>(
    T? Data,
    DateTimeOffset? FetchedAt,
    ProviderOutcome? LastOutcome,
    string? LastDetail,
    DateTimeOffset? LastAttemptAt,
    int ConsecutiveFailures)
    where T : class
{
    public static FeedState<T> Empty { get; } = new(null, null, null, null, null, 0);

    public bool IsStale(DateTimeOffset now, TimeSpan staleAfter) => FetchedAt is null || now - FetchedAt > staleAfter;
}

/// <summary>
/// Shared, process-wide cache of PUBLIC match data: fetched once, filtered per guild. Commands and autocomplete read
/// only from here (fast, no provider calls on the interaction path).
/// </summary>
public sealed class EsportsCache(IOptions<EsportsOptions> options)
{
    private readonly object _gate = new();
    private FeedState<IReadOnlyList<EsportsMatch>> _matches = FeedState<IReadOnlyList<EsportsMatch>>.Empty;
    private FeedState<IReadOnlyList<EsportsEvent>> _events = FeedState<IReadOnlyList<EsportsEvent>>.Empty;
    private FeedState<RankingSnapshot> _rankings = FeedState<RankingSnapshot>.Empty;
    private TeamRankingResolver? _resolver;
    private IReadOnlyList<TeamRef> _teams = [];

    public FeedState<IReadOnlyList<EsportsMatch>> Matches
    {
        get { lock (_gate) return _matches; }
    }

    public FeedState<IReadOnlyList<EsportsEvent>> Events
    {
        get { lock (_gate) return _events; }
    }

    public FeedState<RankingSnapshot> Rankings
    {
        get { lock (_gate) return _rankings; }
    }

    /// <summary>Null when no VRS snapshot is available — VRS filters then fail closed.</summary>
    public TeamRankingResolver? Resolver
    {
        get { lock (_gate) return _resolver; }
    }

    public IReadOnlyList<TeamRef> Teams
    {
        get { lock (_gate) return _teams; }
    }

    public TimeSpan StaleAfter => TimeSpan.FromMinutes(options.Value.StaleAfterMinutes);

    public void UpdateMatches(ProviderResult<IReadOnlyList<EsportsMatch>> result, DateTimeOffset attemptAt)
    {
        lock (_gate)
        {
            _matches = Next(_matches, result, attemptAt);
            if (result.HasData)
                MergeTeams(result.Value!);
        }
    }

    public void UpdateEvents(ProviderResult<IReadOnlyList<EsportsEvent>> result, DateTimeOffset attemptAt)
    {
        lock (_gate)
            _events = Next(_events, result, attemptAt);
    }

    public void UpdateRankings(ProviderResult<RankingSnapshot> result, DateTimeOffset attemptAt)
    {
        lock (_gate)
        {
            _rankings = Next(_rankings, result, attemptAt);
            if (result.HasData)
                _resolver = new TeamRankingResolver(result.Value!, options.Value.TeamAliases);
        }
    }

    /// <summary>Restores last known data after a restart (marked with its original fetch time, so staleness is honest).</summary>
    public void Restore(IReadOnlyList<EsportsMatch>? matches, DateTimeOffset? matchesAt, IReadOnlyList<EsportsEvent>? events, DateTimeOffset? eventsAt,
        RankingSnapshot? rankings, IReadOnlyList<TeamRef> knownTeams)
    {
        lock (_gate)
        {
            if (matches is not null && matchesAt is not null)
                _matches = _matches with { Data = matches, FetchedAt = matchesAt };
            if (events is not null && eventsAt is not null)
                _events = _events with { Data = events, FetchedAt = eventsAt };
            if (rankings is not null)
            {
                _rankings = _rankings with { Data = rankings, FetchedAt = rankings.FetchedAt };
                _resolver = new TeamRankingResolver(rankings, options.Value.TeamAliases);
            }

            _teams = knownTeams.GroupBy(t => t.Key).Select(g => g.First()).ToList();
            if (matches is not null)
                MergeTeams(matches);
        }
    }

    /// <summary>Teams found by a catalog search (no match in the window): resolvable for filters, follows and labels.</summary>
    public void RememberTeams(IEnumerable<TeamRef> teams)
    {
        lock (_gate)
        {
            var merged = _teams.ToDictionary(t => t.Key);
            foreach (var team in teams)
                merged.TryAdd(team.Key, team);
            _teams = merged.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void MergeTeams(IReadOnlyList<EsportsMatch> matches)
    {
        var merged = _teams.ToDictionary(t => t.Key);
        foreach (var team in matches.SelectMany(m => m.Opponents).Where(o => o.Team is not null).Select(o => o.Team!))
            merged[team.Key] = team;
        _teams = merged.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static FeedState<T> Next<T>(FeedState<T> current, ProviderResult<T> result, DateTimeOffset attemptAt)
        where T : class =>
        result.HasData
            ? new FeedState<T>(result.Value, result.At, result.Outcome, result.Detail, attemptAt, 0)
            : current with
            {
                // Keep the last good data (it will turn stale) — a failure never replaces data with "nothing".
                LastOutcome = result.Outcome,
                LastDetail = result.Detail,
                LastAttemptAt = attemptAt,
                ConsecutiveFailures = current.ConsecutiveFailures + 1,
            };
}
