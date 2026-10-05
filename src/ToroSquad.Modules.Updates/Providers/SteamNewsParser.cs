using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Providers;

/// <summary>
/// The only announcement links TSQ accepts from Steam and renders: the Steam news redirector for an official community
/// announcement of exactly this post id. The API answers with the store's CDN host; both hosts serve the same redirect to
/// the announcement on steamcommunity.com (checked 2026-10-05), and the card always shows the store.steampowered.com form.
/// Anything else — another host, another feed's post, a port, credentials, a different id — is refused. Nothing here
/// requests the page.
/// </summary>
public static partial class SteamNewsUrl
{
    public const string CanonicalHost = "store.steampowered.com";
    public const string CdnHost = "steamstore-a.akamaihd.net";
    public const string PathPrefix = "/news/externalpost/" + SteamNewsParser.AnnouncementFeed + "/";

    public static string Canonical(string gid) => "https://" + CanonicalHost + PathPrefix + gid;

    /// <summary>True when <paramref name="value"/> is the redirector link of the post <paramref name="gid"/>.</summary>
    public static bool TryCanonical(string? value, string gid, out string canonical)
    {
        canonical = "";
        if (!IsGid(gid) || !TryParse(value, out var linked) || linked != gid)
            return false;
        canonical = Canonical(gid);
        return true;
    }

    /// <summary>True for the exact form the card shows (the renderer's last check).</summary>
    public static bool IsCanonical(string? value) =>
        TryParse(value, out var gid) && string.Equals(value, Canonical(gid), StringComparison.Ordinal);

    public static bool IsGid(string? gid) => gid is { Length: > 0 and <= 20 } && gid.All(c => c is >= '0' and <= '9');

    private static bool TryParse(string? value, out string gid)
    {
        gid = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300)
            return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        if (!string.Equals(uri.IdnHost, CanonicalHost, StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.IdnHost, CdnHost, StringComparison.OrdinalIgnoreCase))
            return false;
        var match = PathPattern().Match(uri.AbsolutePath);
        if (!match.Success)
            return false;
        gid = match.Groups[1].Value;
        return true;
    }

    [GeneratedRegex(@"^/news/externalpost/steam_community_announcements/([0-9]{1,20})$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}

public enum SteamParseOutcome
{
    Ok = 0,
    Empty = 1,
    Malformed = 2,
    UnexpectedSchema = 3,
}

public sealed record SteamParseResult(SteamParseOutcome Outcome, IReadOnlyList<GameUpdateCandidate> Items, int Skipped, string? Detail);

/// <summary>
/// Reads an ISteamNews/GetNewsForApp v2 answer into normalized posts. Fields as observed on 2026-10-05:
/// <c>appnews { appid, newsitems [ { gid, title, url, is_external_url, author, contents, feedlabel, date, feedname, feed_type,
/// appid, tags? } ], count }</c>. The answer must be for the requested AppID; a post must be an official community
/// announcement of that app with a numeric id, a title and the Steam redirector link of that id — anything else is skipped
/// and counted, never guessed. <c>is_external_url</c> is true for Steam's own announcements too, so it is not used.
/// The post text is kept only as bounded classifier input.
/// </summary>
public static class SteamNewsParser
{
    /// <summary>The feed of a game's own (developer) announcements; press feeds such as "PC Gamer" are never read.</summary>
    public const string AnnouncementFeed = "steam_community_announcements";

    public const int MaxItems = 100;
    public const int MaxLabels = 16;
    public const int MaxDepth = 16;

    public static SteamParseResult Parse(ReadOnlyMemory<byte> utf8Json, GameUpdateDefinition game, DateTimeOffset now)
    {
        if (!uint.TryParse(game.ProviderGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
            return new(SteamParseOutcome.UnexpectedSchema, [], 0, "game has no numeric AppID");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            return new(SteamParseOutcome.Malformed, [], 0, "not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("appnews", out var news) || news.ValueKind != JsonValueKind.Object)
                return new(SteamParseOutcome.UnexpectedSchema, [], 0, "no appnews object");
            if (!news.TryGetProperty("appid", out var answered) || answered.ValueKind != JsonValueKind.Number || !answered.TryGetUInt32(out var answeredId))
                return new(SteamParseOutcome.UnexpectedSchema, [], 0, "appnews.appid missing");
            if (answeredId != appId)
                return new(SteamParseOutcome.UnexpectedSchema, [], 0, "answer is for another AppID");
            if (!news.TryGetProperty("newsitems", out var list) || list.ValueKind != JsonValueKind.Array)
                return new(SteamParseOutcome.UnexpectedSchema, [], 0, "appnews.newsitems is not a list");

            var items = new List<GameUpdateCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;
            var total = 0;
            foreach (var element in list.EnumerateArray())
            {
                if (++total > MaxItems)
                    break;
                GameUpdateCandidate? item;
                try
                {
                    item = TryItem(element, game, appId, now);
                }
                catch (InvalidOperationException)
                {
                    item = null; // a string that is not valid text: this post is unusable, the others are not
                }

                if (item is not null && seen.Add(item.ExternalId))
                    items.Add(item);
                else
                    skipped++;
            }

            if (total == 0)
                return new(SteamParseOutcome.Empty, [], 0, null);
            return items.Count == 0
                ? new(SteamParseOutcome.UnexpectedSchema, [], skipped, "no usable post in the answer")
                : new(SteamParseOutcome.Ok, items, skipped, null);
        }
    }

    private static GameUpdateCandidate? TryItem(JsonElement element, GameUpdateDefinition game, uint appId, DateTimeOffset now)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (Text(element, "feedname") != AnnouncementFeed)
            return null;
        if (element.TryGetProperty("appid", out var own) && (own.ValueKind != JsonValueKind.Number || !own.TryGetUInt32(out var ownId) || ownId != appId))
            return null;
        var gid = Text(element, "gid");
        if (!SteamNewsUrl.IsGid(gid))
            return null;
        var title = Plain(Text(element, "title"), GameUpdateCandidate.TitleMax);
        if (title.Length == 0)
            return null;
        if (!SteamNewsUrl.TryCanonical(Text(element, "url"), gid!, out var url))
            return null;

        DateTimeOffset? published = null;
        if (element.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.Number && date.TryGetInt64(out var seconds) &&
            seconds is > 0 and < 253_402_300_800)
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (at <= now + TimeSpan.FromHours(1)) // a time in the future is "unknown", never shown as the publication time
                published = at;
        }

        var labels = new List<string>();
        if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (labels.Count >= MaxLabels)
                    break;
                if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { Length: > 0 and <= 64 } value)
                    labels.Add(value);
            }
        }

        var body = Text(element, "contents") ?? "";
        if (body.Length > GameUpdateCandidate.BodyMax)
            body = body[..GameUpdateCandidate.BodyMax];
        return new GameUpdateCandidate(SteamNewsUpdateProvider.ProviderId, game.Key, gid!, title, url, published, labels, body);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>One line, no control characters, bounded.</summary>
    private static string Plain(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var chars = new List<char>(Math.Min(value.Length, max));
        var space = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = chars.Count > 0;
                continue;
            }

            if (chars.Count + (space ? 2 : 1) > max)
                break;
            if (space)
                chars.Add(' ');
            space = false;
            chars.Add(ch);
        }

        if (chars.Count > 0 && char.IsHighSurrogate(chars[^1]))
            chars.RemoveAt(chars.Count - 1);
        return new string(chars.ToArray());
    }
}
