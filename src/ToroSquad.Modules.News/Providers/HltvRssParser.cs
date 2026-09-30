using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using ToroSquad.Modules.News.Domain;

namespace ToroSquad.Modules.News.Providers;

public enum FeedParseOutcome
{
    Ok = 0,

    /// <summary>A well-formed RSS document without any item.</summary>
    Empty = 1,

    /// <summary>Not RSS, not well-formed, forbidden constructs (DTD), too deep, or every item unusable.</summary>
    Malformed = 2,
}

/// <summary><paramref name="Skipped"/> items were unusable (no valid news link, no headline, inconsistent guid).</summary>
public sealed record FeedParseResult(FeedParseOutcome Outcome, IReadOnlyList<NewsArticle> Items, int Skipped, int? TtlMinutes, string? Detail)
{
    public static FeedParseResult Malformed(string detail) => new(FeedParseOutcome.Malformed, [], 0, null, detail);
}

/// <summary>
/// Reads the HLTV RSS 2.0 news feed with a hardened <see cref="XmlReader"/>: DTDs are prohibited (no entity expansion, no
/// external entities), no resolver (nothing is ever loaded from a reference), bounded characters and nesting depth. Only the
/// fields the feed was observed to carry are read: channel &lt;ttl&gt;, item &lt;title&gt;, &lt;link&gt;, &lt;guid&gt;,
/// &lt;pubDate&gt;, &lt;description&gt;. The item's &lt;media:content&gt; image is ignored (never fetched, never shown).
/// </summary>
public static partial class HltvRssParser
{
    public const int MaxDepth = 16;
    public const int MaxItems = 200;

    /// <summary>A publication time later than this is treated as missing (never shown, never used for catch-up).</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromHours(1);

    public static XmlReaderSettings Settings(long maxCharacters) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = maxCharacters,
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
        Async = false,
    };

    public static FeedParseResult Parse(Stream stream, DateTimeOffset now, long maxCharacters = 2_000_000)
    {
        try
        {
            using var reader = XmlReader.Create(stream, Settings(maxCharacters));
            if (!reader.ReadToFollowing("rss") || reader.Depth != 0)
                return FeedParseResult.Malformed("root element is not <rss>");
            if (!ReadToChild(reader, "channel"))
                return FeedParseResult.Malformed("no <channel>");

            int? ttl = null;
            var items = new List<NewsArticle>();
            var seen = new HashSet<long>();
            var total = 0;
            var skipped = 0;
            var channelDepth = reader.Depth;
            if (reader.IsEmptyElement)
                return FeedParseResult.Malformed("empty <channel>");
            while (reader.Read())
            {
                Guard(reader);
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == channelDepth)
                    break;
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != channelDepth + 1)
                    continue;
                if (reader.LocalName == "ttl" && reader.NamespaceURI.Length == 0)
                {
                    // Read up to (not past) the end tag: ReadElementContentAsString would swallow the next sibling.
                    var ttlText = reader.IsEmptyElement ? "" : ReadText(reader, reader.Depth);
                    ttl = int.TryParse(ttlText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is > 0 and <= 10080
                        ? minutes
                        : null;
                    continue;
                }

                if (reader.LocalName != "item" || reader.NamespaceURI.Length != 0)
                    continue;
                if (++total > MaxItems)
                    return FeedParseResult.Malformed($"more than {MaxItems} items");
                var fields = ReadItem(reader);
                if (ToArticle(fields, now) is { } article && seen.Add(article.ArticleId))
                    items.Add(article);
                else
                    skipped++;
            }

            if (total == 0)
                return new(FeedParseOutcome.Empty, [], 0, ttl, "feed has no items");
            if (items.Count == 0)
                return new(FeedParseOutcome.Malformed, [], skipped, ttl, $"none of the {total} items is usable");
            return new(FeedParseOutcome.Ok, items, skipped, ttl, skipped > 0 ? $"{skipped} of {total} items skipped" : null);
        }
        catch (XmlException ex)
        {
            return FeedParseResult.Malformed("XML: " + Short(ex.Message));
        }
        catch (InvalidDataException ex)
        {
            return FeedParseResult.Malformed(ex.Message);
        }
    }

    private static void Guard(XmlReader reader)
    {
        if (reader.Depth > MaxDepth)
            throw new InvalidDataException($"XML nesting deeper than {MaxDepth}");
    }

    private static bool ReadToChild(XmlReader reader, string name)
    {
        var depth = reader.Depth;
        if (reader.IsEmptyElement)
            return false;
        while (reader.Read())
        {
            Guard(reader);
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                return false;
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.LocalName == name && reader.NamespaceURI.Length == 0)
                return true;
        }

        return false;
    }

    private static Dictionary<string, string> ReadItem(XmlReader reader)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (reader.IsEmptyElement)
            return fields;
        var depth = reader.Depth;
        while (reader.Read())
        {
            Guard(reader);
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1 || reader.NamespaceURI.Length != 0)
                continue;
            var name = reader.LocalName;
            if (name is not ("title" or "link" or "guid" or "pubDate" or "description"))
                continue;
            var value = reader.IsEmptyElement ? "" : ReadText(reader, reader.Depth);
            fields.TryAdd(name, value);
        }

        return fields;
    }

    /// <summary>Text and CDATA of the element (nested markup is skipped, not interpreted).</summary>
    private static string ReadText(XmlReader reader, int depth)
    {
        var text = new System.Text.StringBuilder();
        while (reader.Read())
        {
            Guard(reader);
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                text.Append(reader.Value);
        }

        return text.ToString();
    }

    private static NewsArticle? ToArticle(Dictionary<string, string> fields, DateTimeOffset now)
    {
        if (!fields.TryGetValue("link", out var link) || !NewsUrl.TryParse(link, out var id, out var canonical))
            return null;
        var title = NewsText.Plain(fields.GetValueOrDefault("title"), NewsText.TitleMax);
        if (title.Length == 0)
            return null;
        // The guid is "hltvnews<id>" (observed). If it names another article the item is inconsistent: skip it.
        if (fields.TryGetValue("guid", out var guid) && GuidPattern().Match(guid.Trim()) is { Success: true } g &&
            long.TryParse(g.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var guidId) && guidId != id)
            return null;
        var published = ParseDate(fields.GetValueOrDefault("pubDate"), now);
        var description = NewsText.Plain(fields.GetValueOrDefault("description"), NewsText.MatchTextMax);
        return new NewsArticle(id, canonical, title, published, description);
    }

    /// <summary>RFC 822/1123 dates as used by RSS ("Wed, 30 Sep 2026 09:50:00 GMT"); missing, invalid or future → null.</summary>
    public static DateTimeOffset? ParseDate(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        string[] formats = ["r", "ddd, dd MMM yyyy HH:mm:ss 'GMT'", "ddd, d MMM yyyy HH:mm:ss 'GMT'", "ddd, dd MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm:ss zzz",
            "dd MMM yyyy HH:mm:ss 'GMT'", "ddd, dd MMM yyyy HH:mm 'GMT'"];
        if (!DateTimeOffset.TryParseExact(text.Replace("+0000", "+00:00", StringComparison.Ordinal), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var at))
            return null;
        at = at.ToUniversalTime();
        return at > now + FutureTolerance || at.Year < 2000 ? null : at;
    }

    private static string Short(string message) => message.Length > 160 ? message[..160] : message;

    [GeneratedRegex(@"^hltvnews([0-9]{1,10})$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();
}
