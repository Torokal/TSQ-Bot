using Microsoft.Extensions.Options;
using ToroSquad.Modules.Esports.Domain;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// One operator-curated external link set for a provider match, e.g.
/// <c>{ "Match": "pandascore:1234567", "Hltv": "https://www.hltv.org/matches/2376543/navi-vs-aurora-..." }</c>.
/// </summary>
public sealed class VerifiedMatchLink
{
    public string Match { get; set; } = "";
    public string? Hltv { get; set; }
    public string? Official { get; set; }
}

/// <summary>
/// Trusted, deterministic source of external match pages (section "Esports:VerifiedMatchLinks"). This is how a verified
/// HLTV match URL enters TSQ Bot today; an authorized provider field or an admin command can feed the same model later.
/// Nothing is fetched or guessed: invalid entries are rejected at startup (<see cref="Problems"/>).
/// </summary>
public sealed class MatchLinkCatalog
{
    private readonly Dictionary<string, MatchLinks> _links = new(StringComparer.Ordinal);

    public MatchLinkCatalog(IOptions<EsportsOptions> options)
    {
        foreach (var entry in options.Value.VerifiedMatchLinks)
        {
            if (!MatchKey.TryParse(entry.Match, out _))
                continue;
            _links[entry.Match] = new MatchLinks(
                MatchLinkPolicy.ValidHltvMatchUrl(entry.Hltv),
                MatchLinkPolicy.ValidGeneralUrl(entry.Official));
        }
    }

    public int Count => _links.Count;

    /// <summary>Attaches curated links; links a provider already supplied are kept unless the curated one is set.</summary>
    public EsportsMatch Apply(EsportsMatch match)
    {
        if (!_links.TryGetValue(match.Key.ToString(), out var curated))
            return match;
        var current = match.Links ?? MatchLinks.None;
        return match with
        {
            Links = new MatchLinks(
                curated.HltvMatchUrl ?? current.HltvMatchUrl,
                curated.OfficialMatchUrl ?? current.OfficialMatchUrl,
                current.ProviderMatchUrl),
        };
    }

    public static IEnumerable<string> Problems(IEnumerable<VerifiedMatchLink> entries)
    {
        foreach (var entry in entries)
        {
            if (!MatchKey.TryParse(entry.Match, out _))
                yield return $"Esports:VerifiedMatchLinks: '{Short(entry.Match)}' is not a match key like 'pandascore:1234567'";
            if (entry.Hltv is not null && MatchLinkPolicy.ValidHltvMatchUrl(entry.Hltv) is null)
                yield return $"Esports:VerifiedMatchLinks[{Short(entry.Match)}].Hltv must look like https://www.hltv.org/matches/<id>/<slug>";
            if (entry.Official is not null && MatchLinkPolicy.ValidGeneralUrl(entry.Official) is null)
                yield return $"Esports:VerifiedMatchLinks[{Short(entry.Match)}].Official must be a plain https URL";
        }
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..60];
}
