namespace ToroSquad.Modules.Esports.Domain;

/// <summary>
/// Team logos are decorative and only come from the match provider's own team data (PandaScore
/// <c>dark_mode_image_url</c>, then <c>image_url</c>). Nothing is scraped, searched or guessed. A URL is used only when it
/// is an absolute HTTPS URL on the provider's image host, without credentials, query or fragment, pointing to a raster
/// image Discord can show as a thumbnail. Anything else means "no logo" — never an error.
/// </summary>
public static class TeamLogoPolicy
{
    public const int MaxLength = 512;

    /// <summary>Image hosts of the providers that supply logos (subdomains included, e.g. cdn-api.pandascore.co).</summary>
    public static readonly IReadOnlyCollection<string> AllowedHosts = ["pandascore.co"];

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    /// <summary>The validated URL, or null when it is absent or not acceptable.</summary>
    public static string? Validate(string? url) => Check(url, out _);

    /// <summary>
    /// Picks the first acceptable candidate (dark-mode logo first: Discord cards are usually shown on a dark theme).
    /// <paramref name="rejected"/> is true when a non-empty candidate was refused, so callers can log it.
    /// </summary>
    public static string? Choose(string? darkModeUrl, string? url, out bool rejected)
    {
        rejected = false;
        foreach (var candidate in new[] { darkModeUrl, url })
        {
            if (Check(candidate, out var refused) is { } ok)
                return ok;
            rejected |= refused;
        }

        return null;
    }

    private static string? Check(string? url, out bool refused)
    {
        refused = false;
        if (string.IsNullOrWhiteSpace(url))
            return null;
        refused = true;
        if (url.Length > MaxLength || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (!AllowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
            return null;
        if (!ImageExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return null;
        refused = false;
        return uri.AbsoluteUri;
    }
}
