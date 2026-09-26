using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// Finds the HLTV match page for a match from another provider (PandaScore) among Liquipedia matches, which carry the
/// editor-entered HLTV link. A wrong link is worse than none, so a link is attached only when EXACTLY ONE distinct valid
/// HLTV URL belongs to a Liquipedia match with the same two teams (order-insensitive, names compared with the VRS
/// normalization: case/accents/"team", "esports", "gaming"… ignored) whose start time is within the tolerance.
/// Zero or several candidates → no link. Nothing is fetched from HLTV.
/// </summary>
public static class HltvLinkMatcher
{
    public static string? Find(EsportsMatch target, IReadOnlyList<EsportsMatch> candidates, TimeSpan tolerance)
    {
        if (!target.A.IsTeam || !target.B.IsTeam || target.ScheduledStartUtc is not { } start)
            return null;
        var a = Key(target.A.Team!);
        var b = Key(target.B.Team!);
        if (a is null || b is null || a == b)
            return null;

        var urls = candidates
            .Where(c => c.A.IsTeam && c.B.IsTeam && c.ScheduledStartUtc is { } cs && (cs - start).Duration() <= tolerance)
            .Where(c => SameTeams(a, b, Key(c.A.Team!), Key(c.B.Team!)))
            .Select(c => MatchLinkPolicy.ValidHltvMatchUrl(c.Links?.HltvMatchUrl))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return urls.Count == 1 ? urls[0] : null;
    }

    private static string? Key(TeamRef team)
    {
        var normalized = TeamRankingResolver.Normalize(team.Name);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool SameTeams(string a, string b, string? c, string? d) =>
        (a == c && b == d) || (a == d && b == c);
}

/// <summary>
/// Liquipedia as an OPTIONAL link source only (match data stays with the selected provider, which never depends on it).
/// Refreshed at most every <see cref="EsportsOptions.LinkPollMinutes"/> within Liquipedia's request budget; the result is
/// cached (in memory and, through the poller, in the database with its fetch time) so a restart re-uses it instead of
/// re-requesting the same data — the API terms ask to cache results as long as possible. On failure the last good
/// candidates are kept (links never disappear because of an outage) and nothing else changes. Disabled when Liquipedia is
/// itself the match provider (its links are native then) or not configured (live mode needs an approved key); the
/// operator-curated <see cref="EsportsOptions.VerifiedMatchLinks"/> work either way.
/// </summary>
public sealed class LiquipediaHltvLinkSource(
    LiquipediaClient client,
    IEsportsDataProvider matchProvider,
    EsportsDataMode mode,
    IOptions<EsportsOptions> options,
    TimeProvider clock,
    ILogger<LiquipediaHltvLinkSource> logger)
{
    private readonly LiquipediaProvider _liquipedia = new(client, mode);
    private IReadOnlyList<EsportsMatch> _candidates = [];
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public bool Enabled =>
        options.Value.HltvLinksFromLiquipedia &&
        matchProvider.Id != LiquipediaParser.Source &&
        _liquipedia.IsConfigured;

    public int CandidateCount => _candidates.Count;

    public IReadOnlyList<EsportsMatch> Candidates => _candidates;

    public ProviderOutcome? LastOutcome { get; private set; }

    /// <summary>Provider-state key of the persisted cache; fixture and live data never share it.</summary>
    public string StateKey => mode.Mode == ProviderMode.Live ? "liquipedia:hltv-links" : "liquipedia-fixture:hltv-links";

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(5, options.Value.LinkPollMinutes));

    /// <summary>
    /// Fetches when due. Returns the attempt's result (its value = the new candidates) so the caller can persist it, or
    /// null when nothing was requested (disabled or not due yet).
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<EsportsMatch>>?> RefreshIfDueAsync(MatchWindow window, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (!Enabled || now < _next)
            return null;
        _next = now + Interval;

        var result = await _liquipedia.GetMatchesAsync(window, cancellationToken);
        LastOutcome = result.Outcome;
        if (!result.HasData)
        {
            logger.LogInformation("HLTV link source (Liquipedia) unavailable: {Outcome}; keeping {Count} known links", result.Outcome, _candidates.Count);
            return result;
        }

        _candidates = result.Value!.Where(m => m.Links?.HltvMatchUrl is not null).ToList();
        return result with { Value = _candidates };
    }

    /// <summary>
    /// Restores the persisted cache after a restart: its candidates are used right away and the next request waits for
    /// the normal interval after the last attempt (a restart never causes an extra request).
    /// </summary>
    public void Restore(IReadOnlyList<EsportsMatch>? candidates, DateTimeOffset? lastAttemptAt)
    {
        if (candidates is { Count: > 0 })
            _candidates = candidates.Where(m => MatchLinkPolicy.ValidHltvMatchUrl(m.Links?.HltvMatchUrl) is not null).ToList();
        if (lastAttemptAt is { } at && at + Interval > _next)
            _next = at + Interval;
    }

    /// <summary>Adds a verified HLTV link when exactly one candidate matches; never replaces a link the match already has.</summary>
    public EsportsMatch Apply(EsportsMatch match)
    {
        if (!Enabled || match.Links?.HltvMatchUrl is not null || _candidates.Count == 0)
            return match;
        var url = HltvLinkMatcher.Find(match, _candidates, TimeSpan.FromMinutes(options.Value.HltvLinkToleranceMinutes));
        return url is null ? match : match with { Links = (match.Links ?? MatchLinks.None) with { HltvMatchUrl = url, HltvVia = MatchLinks.ViaLiquipedia } };
    }
}
