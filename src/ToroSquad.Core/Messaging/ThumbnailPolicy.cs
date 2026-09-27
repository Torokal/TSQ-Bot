namespace ToroSquad.Core.Messaging;

/// <summary>
/// Shared rule for decorative provider images shown as an embed thumbnail (team logos, flags). A module passes the image
/// hosts of ITS providers; everything else means "no image" — never an error, never a hotlink to an arbitrary CDN.
/// Accepted: absolute HTTPS URL on an allowed host (subdomains included), default port, no credentials, no query, no
/// fragment, a raster image Discord can show (PNG/JPG/WebP/GIF), at most <see cref="MaxLength"/> characters.
/// </summary>
public static class ThumbnailPolicy
{
    public const int MaxLength = 512;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    /// <summary>
    /// The validated URL, or null. <paramref name="refused"/> is true when a non-empty candidate was rejected (callers
    /// log that as a provider warning without the URL itself).
    /// </summary>
    public static string? Check(string? url, IReadOnlyCollection<string> allowedHosts, out bool refused)
    {
        refused = false;
        if (string.IsNullOrWhiteSpace(url))
            return null;
        refused = true;
        if (allowedHosts.Count == 0 || url.Length > MaxLength || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (!allowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
            return null;
        if (!ImageExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return null;
        refused = false;
        return uri.AbsoluteUri;
    }
}
