using ToroSquad.Core.Messaging;

namespace ToroSquad.Modules.Esports.Domain;

/// <summary>
/// Team logos are decorative and only come from the match provider's own team data (PandaScore
/// <c>dark_mode_image_url</c>, then <c>image_url</c>). Nothing is scraped, searched or guessed. The URL rules are the shared
/// <see cref="ThumbnailPolicy"/> restricted to the esports providers' image hosts. Anything else means "no logo" — never
/// an error.
/// </summary>
public static class TeamLogoPolicy
{
    public const int MaxLength = ThumbnailPolicy.MaxLength;

    /// <summary>Image hosts of the providers that supply logos (subdomains included, e.g. cdn-api.pandascore.co).</summary>
    public static readonly IReadOnlyCollection<string> AllowedHosts = ["pandascore.co"];

    /// <summary>The validated URL, or null when it is absent or not acceptable.</summary>
    public static string? Validate(string? url) => ThumbnailPolicy.Check(url, AllowedHosts, out _);

    /// <summary>
    /// Picks the first acceptable candidate (dark-mode logo first: Discord cards are usually shown on a dark theme).
    /// <paramref name="rejected"/> is true when a non-empty candidate was refused, so callers can log it.
    /// </summary>
    public static string? Choose(string? darkModeUrl, string? url, out bool rejected)
    {
        rejected = false;
        foreach (var candidate in new[] { darkModeUrl, url })
        {
            if (ThumbnailPolicy.Check(candidate, AllowedHosts, out var refused) is { } ok)
                return ok;
            rejected |= refused;
        }

        return null;
    }
}
