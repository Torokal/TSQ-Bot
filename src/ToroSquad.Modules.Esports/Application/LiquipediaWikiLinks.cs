using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// Automatic fallback HLTV link source: Liquipedia's free MediaWiki API, used only while LiquipediaDB enrichment is not
/// usable (no approved key, or its last attempt failed). Only for matches a server actually follows (server team filter)
/// that start soon or were played recently, only a few lookups per poll, and every result is cached in the database:
/// found links forever (for the match), misses with a growing re-check interval (editors may add the id later — a miss
/// is never permanent). A lookup = one search + one wikitext request (+ one acronym search/wikitext if the names found
/// nothing). A wrong link is worse than none (<see cref="WikiLinkMatcher"/>). Failures never touch match data, alerts or
/// already known links; HTTP 429 pauses all lookups until Retry-After. HLTV itself is never contacted.
/// </summary>
public sealed class LiquipediaWikiLinkSource(
    LiquipediaWikiClient client,
    LiquipediaHltvLinkSource lpdb,
    IEsportsDataProvider matchProvider,
    EsportsDataMode mode,
    IOptions<EsportsOptions> options,
    TimeProvider clock,
    ILogger<LiquipediaWikiLinkSource> logger)
{
    /// <summary>Provider-state row holding the persisted cache (live data only).</summary>
    public const string StateKey = "liquipedia-wiki:hltv-links";

    /// <summary>Cache entry per provider match: a found link, or a miss with its next re-check time.</summary>
    public sealed record Entry(string? Url, DateTimeOffset StartUtc, DateTimeOffset CheckedAt, DateTimeOffset NextCheckAt, int Misses);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public ProviderOutcome? LastOutcome { get; private set; }

    public bool Enabled
    {
        get
        {
            var o = options.Value;
            if (mode.Mode != ProviderMode.Live || !o.HltvLinksFromLiquipedia || !o.HltvLinksFromWikiApi || matchProvider.Id == LiquipediaParser.Source)
                return false;
            if (LiquipediaWikiClient.ConfigurationProblem(client.Options) is not null)
                return false;
            // LiquipediaDB first; the MediaWiki API only while LPDB is not usable.
            var lpdbUsable = lpdb.Enabled && lpdb.LastOutcome is null or ProviderOutcome.Success or ProviderOutcome.Partial;
            return !lpdbUsable;
        }
    }

    public int LinkCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Count(e => e.Url is not null);
        }
    }

    public Entry? Get(MatchKey key)
    {
        lock (_gate)
            return _entries.GetValueOrDefault(key.ToString());
    }

    /// <summary>
    /// Looks up due matches (bounded). Returns true when the cache changed and should be persisted. Never throws for
    /// provider problems; the caller still wraps it so a bug can never fail the match poll.
    /// </summary>
    public async Task<bool> RefreshAsync(IReadOnlyList<EsportsMatch> matches, IReadOnlySet<string> followedTeamKeys, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        if (!Enabled || followedTeamKeys.Count == 0 || now < _pausedUntil)
            return false;

        var changed = Prune(now);
        var due = matches
            .Where(m => m.A.IsTeam && m.B.IsTeam && m.ScheduledStartUtc is not null && m.Links?.HltvMatchUrl is null)
            .Where(m => followedTeamKeys.Contains(m.A.Team!.Key) || followedTeamKeys.Contains(m.B.Team!.Key))
            .Where(m => m.ScheduledStartUtc >= now - TimeSpan.FromHours(o.WikiLinkLookbackHours) && m.ScheduledStartUtc <= now + TimeSpan.FromHours(o.WikiLinkLookaheadHours))
            .Where(m => Get(m.Key) is not { } e || (e.Url is null && now >= e.NextCheckAt))
            .OrderBy(m => (m.ScheduledStartUtc!.Value - now).Duration())
            .Take(Math.Max(1, o.WikiLinkMaxLookupsPerPoll))
            .ToList();

        foreach (var match in due)
        {
            var (outcome, url, retryAfter) = await LookupAsync(match, cancellationToken);
            LastOutcome = outcome;
            now = clock.GetUtcNow();
            if (outcome is not (ProviderOutcome.Success or ProviderOutcome.Partial))
            {
                if (outcome == ProviderOutcome.QuotaExceeded)
                    _pausedUntil = now + (retryAfter ?? TimeSpan.FromMinutes(30));
                logger.LogInformation("HLTV link lookup (Liquipedia MediaWiki) unavailable: {Outcome}; known links kept, match alerts unaffected", outcome);
                Record(match, null, now, failure: true);
                return true; // stop this poll: never hammer a failing upstream
            }

            Record(match, url, now, failure: false);
            changed = true;
            if (url is not null)
                logger.LogInformation("HLTV link found via Liquipedia (MediaWiki API) for {Match}", match.Key);
        }

        return changed;
    }

    /// <summary>Adds a cached link (credited to Liquipedia); never replaces a link the match already has. Demo data never links.</summary>
    public EsportsMatch Apply(EsportsMatch match)
    {
        if (mode.IsDemo || match.Links?.HltvMatchUrl is not null || Get(match.Key)?.Url is not { } url || MatchLinkPolicy.ValidHltvMatchUrl(url) is null)
            return match;
        return match with { Links = (match.Links ?? MatchLinks.None) with { HltvMatchUrl = url, HltvVia = MatchLinks.ViaLiquipedia } };
    }

    public string Export()
    {
        lock (_gate)
            return JsonSerializer.Serialize(_entries, Json);
    }

    /// <summary>Restores the persisted cache after a restart (found links are reused, misses keep their re-check time).</summary>
    public void Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var restored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, Json);
            if (restored is null)
                return;
            var valid = restored
                .Where(kv => MatchKey.TryParse(kv.Key, out _) && (kv.Value.Url is null || MatchLinkPolicy.ValidHltvMatchUrl(kv.Value.Url) is not null))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            lock (_gate)
                _entries = valid;
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Ignoring unreadable HLTV link cache: {Error}", ex.Message);
        }
    }

    private async Task<(ProviderOutcome Outcome, string? Url, TimeSpan? RetryAfter)> LookupAsync(EsportsMatch match, CancellationToken ct)
    {
        var tolerance = TimeSpan.FromMinutes(options.Value.HltvLinkToleranceMinutes);
        var a = match.A.Team!;
        var b = match.B.Team!;
        var queries = new List<string>();
        if (Query(a.Name, b.Name) is { } byName)
            queries.Add(byName);
        if (Query(a.ShortName, b.ShortName) is { } byAcronym && !queries.Contains(byAcronym))
            queries.Add(byAcronym);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<WikiMatch>();
        foreach (var query in queries)
        {
            var search = await client.SearchAsync(query, 3, ct);
            if (!search.HasData)
                return (search.Outcome, null, search.RetryAfter);
            var titles = search.Value!.Where(seen.Add).ToList();
            if (titles.Count == 0)
                continue;
            var pages = await client.GetWikitextAsync(titles, ct);
            if (!pages.HasData)
                return (pages.Outcome, null, pages.RetryAfter);
            foreach (var (_, wikitext) in pages.Value!)
                candidates.AddRange(LiquipediaWikitext.ParseMatches(wikitext));
            if (WikiLinkMatcher.Find(match, candidates, tolerance) is { } url)
                return (ProviderOutcome.Success, url, null);
        }

        return (ProviderOutcome.Success, null, null);
    }

    /// <summary>CirrusSearch: pages whose source contains both team names (quotes and backslashes stripped).</summary>
    private static string? Query(string? x, string? y)
    {
        static string? Clean(string? v)
        {
            var s = new string((v ?? "").Where(ch => ch is not ('"' or '\\') && !char.IsControl(ch)).ToArray()).Trim();
            return s.Length >= 2 ? s : null;
        }

        return Clean(x) is { } cx && Clean(y) is { } cy ? $"insource:\"{cx}\" insource:\"{cy}\"" : null;
    }

    private void Record(EsportsMatch match, string? url, DateTimeOffset now, bool failure)
    {
        var o = options.Value;
        lock (_gate)
        {
            var key = match.Key.ToString();
            var previous = _entries.GetValueOrDefault(key);
            if (previous?.Url is not null)
                return; // a found link is never removed or replaced
            if (url is not null)
            {
                _entries[key] = new Entry(url, match.ScheduledStartUtc!.Value, now, now, previous?.Misses ?? 0);
                return;
            }

            var misses = failure ? previous?.Misses ?? 0 : (previous?.Misses ?? 0) + 1;
            var minutes = Math.Min(o.WikiLinkMaxRecheckMinutes, o.WikiLinkRecheckMinutes * Math.Pow(2, Math.Max(0, misses - 1)));
            _entries[key] = new Entry(null, match.ScheduledStartUtc!.Value, now, now + TimeSpan.FromMinutes(minutes), misses);
        }
    }

    private bool Prune(DateTimeOffset now)
    {
        lock (_gate)
        {
            var old = _entries.Where(kv => kv.Value.StartUtc < now - TimeSpan.FromDays(3)).Select(kv => kv.Key).ToList();
            foreach (var key in old)
                _entries.Remove(key);
            return old.Count > 0;
        }
    }
}
