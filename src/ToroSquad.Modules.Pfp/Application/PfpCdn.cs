using System.Globalization;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Pfp.Application;

/// <summary>
/// Discord CDN URLs for the four profile pictures (Discord "Image Formatting" reference, checked 2026-10-27), built only from
/// ids and hashes that Discord's API returned — never from user input. Why not the SDK's helpers: Discord.Net 3.20.1's
/// <c>ImageFormat.Auto</c> turns an animated (<c>a_</c>) hash into <c>.gif</c>, and Discord's reference warns that animated
/// images uploaded as WebP or AVIF do not convert cleanly to GIF and recommends animated WebP (<c>.webp?animated=true</c>);
/// the SDK's member-avatar and member-banner URL helpers are not public either. Animated pictures therefore become
/// <c>.webp?size=2048&amp;animated=true</c>, static ones <c>.png?size=2048</c>. 2048 is the requested size; Discord serves
/// the stored picture at most at its own resolution. The default avatar is not built here: its URL comes from the SDK
/// (<see cref="IsDefaultAvatarUrl"/> only checks it) and is shown as it is (its size is fixed by Discord).
/// </summary>
public static partial class PfpCdn
{
    public const string BaseUrl = "https://cdn.discordapp.com/";
    public const int Size = 2048;
    public const string AnimatedPrefix = "a_";

    public static string? UserAvatar(ulong userId, string? hash) => Build(Inv($"avatars/{userId}"), hash);

    public static string? MemberAvatar(ulong guildId, ulong userId, string? hash) => Build(Inv($"guilds/{guildId}/users/{userId}/avatars"), hash);

    public static string? UserBanner(ulong userId, string? hash) => Build(Inv($"banners/{userId}"), hash);

    public static string? MemberBanner(ulong guildId, ulong userId, string? hash) => Build(Inv($"guilds/{guildId}/users/{userId}/banners"), hash);

    public static bool IsAnimated(string hash) => hash.StartsWith(AnimatedPrefix, StringComparison.Ordinal);

    /// <summary>A hash as Discord returns it (hex, animated ones with <c>a_</c>); anything else is never put into a URL.</summary>
    public static bool IsValidHash(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    /// <summary>Discord's default avatars: <c>https://cdn.discordapp.com/embed/avatars/&lt;index&gt;.png</c>.</summary>
    public static bool IsDefaultAvatarUrl(string? url) => url is not null && DefaultAvatarPattern().IsMatch(url);

    /// <summary>Null when the hash is missing or not a Discord hash.</summary>
    private static string? Build(string path, string? hash)
    {
        if (!IsValidHash(hash))
            return null;
        return IsAnimated(hash!)
            ? Inv($"{BaseUrl}{path}/{hash}.webp?size={Size}&animated=true")
            : Inv($"{BaseUrl}{path}/{hash}.png?size={Size}");
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^(a_)?[0-9a-f]{32}$")]
    private static partial Regex HashPattern();

    [GeneratedRegex(@"^https://cdn\.discordapp\.com/embed/avatars/[0-9]\.png$")]
    private static partial Regex DefaultAvatarPattern();
}
