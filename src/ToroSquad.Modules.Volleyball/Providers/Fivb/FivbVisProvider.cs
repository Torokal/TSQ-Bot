using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Volleyball.Domain;

namespace ToroSquad.Modules.Volleyball.Providers.Fivb;

/// <summary>
/// FIVB VIS adapter. Discovery = ONE request for all senior women's national-team tournaments in the date window (VIS has
/// no pagination: the whole filtered list is returned, and a count mismatch is treated as a schema problem instead of
/// silently truncating). Live = only the followed matches (<c>NoMatches</c>), incremental through VIS's <c>Version</c>
/// ("no changes" returns the unchanged last state, so continuity is kept), with a periodic full refresh as a safety net.
/// VIS offers no broadcaster, logo, postponed or cancelled data: those capabilities are absent, never invented.
/// </summary>
public sealed class FivbVisProvider(FivbVisClient client, IOptions<FivbVisOptions> options, VbDataMode mode, ILogger<FivbVisProvider> logger) : IVolleyballDataProvider
{
    private readonly Lock _gate = new();

    /// <summary>Last known state per followed match, with its VIS item version and when that version last CHANGED.</summary>
    private readonly Dictionary<string, (VolleyballMatch Match, long? Version, DateTimeOffset ChangedAt)> _live = new(StringComparer.Ordinal);
    private long? _liveVersion;
    private int _liveCalls;

    /// <summary>"fivb" for real data; synthetic fixture data gets its own id so its rows never surface in live mode.</summary>
    public string Id => mode.IsDemo ? FivbVisParser.DemoProviderId : FivbVisParser.ProviderId;

    public string AttributionKey => "vb.source.fivb";

    /// <summary>Public data needs no credentials (the optional application id only identifies TSQ to FIVB).</summary>
    public bool IsConfigured => true;

    public VbCapabilities Capabilities =>
        VbCapabilities.Fixtures | VbCapabilities.Results | VbCapabilities.LiveMatchState | VbCapabilities.SetScores | VbCapabilities.CurrentSetScore | VbCapabilities.Venue;

    public async Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetMatchesAsync(TrackedTeamIdentity team, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var request = FivbVisClient.MatchListRequest(DateOnly.FromDateTime(fromUtc.UtcDateTime), DateOnly.FromDateTime(toUtc.UtcDateTime), FivbVisParser.SeniorTournamentTypes);
        var response = await client.GetAsync(request, cancellationToken);
        if (!response.HasData)
            return response.WithoutValue<IReadOnlyList<VolleyballMatch>>();
        using var document = response.Value!;
        if (Parse(document, out var list, out var problem) is { } failure)
            return failure.WithoutValue<IReadOnlyList<VolleyballMatch>>() with { At = response.At };
        if (problem is not null)
            logger.LogWarning("volleyball provider_schema_error provider=fivb {Problem} (items rejected {Rejected}/{Total})", problem, list.ItemsRejected, list.ItemsTotal);

        // Cheap pre-filter on the country code; the poller applies the full identity rules (gender, level, kind, ids).
        IReadOnlyList<VolleyballMatch> mine = list.Matches
            .Where(m => string.Equals(m.HomeTeam.CountryCode, team.CountryCode, StringComparison.Ordinal) ||
                        string.Equals(m.AwayTeam.CountryCode, team.CountryCode, StringComparison.Ordinal))
            .ToList();
        return VbProviderResult<IReadOnlyList<VolleyballMatch>>.Ok(mine, response.At);
    }

    public async Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetLiveStateAsync(IReadOnlyCollection<string> providerMatchIds, CancellationToken cancellationToken)
    {
        var ids = providerMatchIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return VbProviderResult<IReadOnlyList<VolleyballMatch>>.Ok([], DateTimeOffset.MinValue);

        long? version;
        lock (_gate)
        {
            _liveCalls++;
            var known = ids.All(_live.ContainsKey);
            version = known && _liveVersion is not null && _liveCalls % options.Value.FullLiveRefreshEvery != 0 ? _liveVersion : null;
        }

        var response = await client.GetAsync(FivbVisClient.LiveRequest(ids, version), cancellationToken);
        if (!response.HasData)
            return response.WithoutValue<IReadOnlyList<VolleyballMatch>>();
        using var document = response.Value!;
        if (Parse(document, out var list, out var problem) is { } failure)
            return failure.WithoutValue<IReadOnlyList<VolleyballMatch>>() with { At = response.At };
        if (problem is not null)
        {
            // A live item we cannot read must not silently look like "no change": fail this poll (it is retried).
            logger.LogWarning("volleyball provider_schema_error provider=fivb live {Problem}", problem);
            return VbProviderResult<IReadOnlyList<VolleyballMatch>>.Fail(VbProviderOutcome.SchemaChanged, problem, response.At);
        }

        lock (_gate)
        {
            if (version is null)
            {
                // Full answer: a followed match VIS no longer returns is forgotten (its version history is kept otherwise, so a
                // periodic full refresh does not hide a frozen feed).
                foreach (var id in ids.Where(id => list.Matches.All(m => m.ProviderMatchId != id)))
                    _live.Remove(id);
            }

            foreach (var match in list.Matches.Where(m => ids.Contains(m.ProviderMatchId, StringComparer.Ordinal)))
            {
                // The provider's own update time = when this match's VIS version last changed. A live match whose version
                // stops changing for LiveStaleAfterMinutes is a frozen feed (stale), not a quiet match.
                long? itemVersion = list.ItemVersions.TryGetValue(match.ProviderMatchId, out var iv) ? iv : null;
                var changedAt = _live.TryGetValue(match.ProviderMatchId, out var known) && itemVersion is not null && known.Version == itemVersion
                    ? known.ChangedAt
                    : response.At;
                _live[match.ProviderMatchId] = (match, itemVersion, changedAt);
            }

            _liveVersion = list.Version ?? _liveVersion;
            foreach (var stale in _live.Keys.Where(k => !ids.Contains(k, StringComparer.Ordinal)).ToList())
                _live.Remove(stale); // keep only what is still being followed

            // "No changes" (incremental answer) = the last full state is still current.
            IReadOnlyList<VolleyballMatch> current = ids.Where(_live.ContainsKey).Select(id => _live[id].Match with { LastProviderUpdateUtc = _live[id].ChangedAt }).ToList();
            return VbProviderResult<IReadOnlyList<VolleyballMatch>>.Ok(current, response.At);
        }
    }

    private VbProviderResult<object>? Parse(JsonDocument document, out FivbMatchList list, out string? itemProblem)
    {
        list = FivbVisParser.Parse(document, FivbVisParser.SeniorTypeValues, out var problem, Id);
        itemProblem = null;
        if (problem is not null && (list.ItemsTotal == 0 || list.Matches.Count == 0))
            return VbProviderResult<object>.Fail(VbProviderOutcome.SchemaChanged, problem!, DateTimeOffset.MinValue);
        if (document.RootElement.TryGetProperty("nbItems", out var nb) && nb.TryGetInt32(out var count) && count != list.ItemsTotal)
            return VbProviderResult<object>.Fail(VbProviderOutcome.SchemaChanged, $"nbItems {count} but {list.ItemsTotal} items (truncated response)", DateTimeOffset.MinValue);
        itemProblem = problem;
        return null;
    }
}
