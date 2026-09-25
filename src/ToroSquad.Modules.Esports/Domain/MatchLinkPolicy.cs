using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Esports.Domain;

public enum MatchPageKind
{
    None = 0,
    Hltv = 1,
    Official = 2,
    Provider = 3,
}

/// <summary>The one link shown as "Maç Sayfası" / "Match Page", and where it points.</summary>
public sealed record MatchPage(MatchPageKind Kind, string Url);

/// <summary>
/// URL safety for external match pages. Validation is purely syntactic: pages are never fetched (fetching HLTV to
/// "check" a link would be scraping). Order of preference: verified HLTV → official organizer page → provider page → none.
/// </summary>
public static partial class MatchLinkPolicy
{
    public const string HltvHost = "www.hltv.org";

    /// <summary>
    /// Returns the normalized HLTV match URL (https://www.hltv.org/matches/&lt;id&gt;/&lt;slug&gt;, no query/fragment) or null.
    /// Anything else — other schemes, hosts (including look-alikes and hltv.org in the path/query), ports, credentials,
    /// non-match paths — is rejected.
    /// </summary>
    public static string? ValidHltvMatchUrl(string? url)
    {
        if (!TryParseHttps(url, out var uri) || !string.Equals(uri.IdnHost, HltvHost, StringComparison.OrdinalIgnoreCase))
            return null;
        var path = uri.AbsolutePath;
        return HltvMatchPath().IsMatch(path) ? $"https://{HltvHost}{path}" : null;
    }

    /// <summary>A safe https page for official/provider links (no credentials, default port, no control characters).</summary>
    public static string? ValidGeneralUrl(string? url, IReadOnlyCollection<string>? allowedHosts = null)
    {
        if (!TryParseHttps(url, out var uri))
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _))
            return null;
        if (allowedHosts is not null && !allowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
            return null;
        return uri.AbsoluteUri;
    }

    /// <summary>Resolves the single match page to show; unsafe candidates are skipped, never shown.</summary>
    public static MatchPage? Resolve(MatchLinks? links, IReadOnlyCollection<string>? providerHosts = null)
    {
        if (links is null)
            return null;
        if (ValidHltvMatchUrl(links.HltvMatchUrl) is { } hltv)
            return new MatchPage(MatchPageKind.Hltv, hltv);
        if (ValidGeneralUrl(links.OfficialMatchUrl) is { } official)
            return new MatchPage(MatchPageKind.Official, official);
        if (ValidGeneralUrl(links.ProviderMatchUrl, providerHosts) is { } provider)
            return new MatchPage(MatchPageKind.Provider, provider);
        return null;
    }

    private static bool TryParseHttps(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 512 || url.Any(char.IsControl) || url.Any(char.IsWhiteSpace))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo) || !parsed.IsDefaultPort)
            return false;
        uri = parsed;
        return true;
    }

    // /matches/<numeric id>/<slug of lowercase letters, digits and hyphens>
    [GeneratedRegex(@"^/matches/[0-9]{1,10}/[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HltvMatchPath();
}
