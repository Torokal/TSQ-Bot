using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ToroSquad.Modules.Letterboxd.Domain;

namespace ToroSquad.Modules.Letterboxd.Providers;

public enum FeedParseOutcome
{
    Ok = 0,

    /// <summary>A well-formed feed of the followed member without any item.</summary>
    Empty = 1,

    /// <summary>Not RSS, not well-formed, forbidden constructs (DTD), too deep, another member's feed, or every item unusable.</summary>
    Malformed = 2,
}

/// <summary>
/// Why items were left out (counted, never logged with their content). <see cref="Duplicate"/>: the same viewing id again
/// with identical content (one is kept); <see cref="Conflict"/>: the same viewing id with different content — every item
/// with that id is dropped (nothing is merged or picked).
/// </summary>
public sealed record SkippedItems(int NotAnEntry, int ForeignLink, int Invalid, int Duplicate, int Conflict = 0)
{
    public static SkippedItems None { get; } = new(0, 0, 0, 0);

    public int Total => NotAnEntry + ForeignLink + Invalid + Duplicate + Conflict;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"not an entry {NotAnEntry}, foreign link {ForeignLink}, invalid {Invalid}, duplicate {Duplicate}, conflict {Conflict}");
}

public sealed record FeedParseResult(FeedParseOutcome Outcome, IReadOnlyList<LetterboxdEntry> Items, int TotalItems, SkippedItems Skipped, int? TtlMinutes, string? Detail)
{
    public static FeedParseResult Malformed(string detail) => new(FeedParseOutcome.Malformed, [], 0, SkippedItems.None, null, detail);
}

/// <summary>
/// Reads a member's Letterboxd RSS 2.0 feed with a hardened <see cref="XmlReader"/>: DTDs prohibited, no resolver, bounded
/// characters, items and nesting depth. Only fields observed in the real feed are read (2026-10-29, 50 items): item
/// &lt;title&gt;, &lt;link&gt;, &lt;guid&gt; ("letterboxd-watch-N" / "letterboxd-review-N"), &lt;pubDate&gt;,
/// &lt;description&gt; (HTML: poster image + "Watched on …" or the review paragraphs), and in the
/// <c>https://letterboxd.com</c> namespace watchedDate, rewatch (Yes/No), filmTitle, filmYear, memberRating (absent when
/// the entry has no rating); tmdb:movieId / tmdb:tvId. The channel &lt;link&gt; must be the followed member's profile, and
/// every entry link must belong to that member. A missing rating stays null, a missing review stays null; an invalid value
/// skips the item (its stored state is kept), it is never guessed or rounded.
/// </summary>
public static partial class LetterboxdRssParser
{
    public const string LetterboxdNamespace = "https://letterboxd.com";
    public const string TmdbNamespace = "https://themoviedb.org";
    public const int MaxDepth = 16;
    public const int MaxItems = 200;
    public const int TitleMax = 200;
    public const string SpoilerTitleSuffix = "(contains spoilers)";

    /// <summary>A publication time later than this is treated as missing.</summary>
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

    public static FeedParseResult Parse(Stream stream, string username, DateTimeOffset now, int reviewExcerptMax, long maxCharacters = 4_000_000)
    {
        try
        {
            using var reader = XmlReader.Create(stream, Settings(maxCharacters));
            if (!reader.ReadToFollowing("rss") || reader.Depth != 0)
                return FeedParseResult.Malformed("root element is not <rss>");
            if (!ReadToChild(reader, "channel"))
                return FeedParseResult.Malformed("no <channel>");
            if (reader.IsEmptyElement)
                return FeedParseResult.Malformed("empty <channel>");

            int? ttl = null;
            string? channelLink = null;
            var items = new List<LetterboxdEntry>();
            var seen = new Dictionary<long, LetterboxdEntry>();
            var conflicted = new HashSet<long>();
            int total = 0, notEntry = 0, foreign = 0, invalid = 0, duplicate = 0, conflict = 0;
            var channelDepth = reader.Depth;
            while (reader.Read())
            {
                Guard(reader);
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == channelDepth)
                    break;
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != channelDepth + 1 || reader.NamespaceURI.Length != 0)
                    continue;
                switch (reader.LocalName)
                {
                    case "link":
                        channelLink ??= reader.IsEmptyElement ? "" : ReadText(reader, reader.Depth);
                        continue;
                    case "ttl":
                        var ttlText = reader.IsEmptyElement ? "" : ReadText(reader, reader.Depth);
                        ttl = int.TryParse(ttlText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is > 0 and <= 10080 ? minutes : null;
                        continue;
                    case not "item":
                        continue;
                }

                if (++total > MaxItems)
                    return FeedParseResult.Malformed($"more than {MaxItems} items");
                var fields = ReadItem(reader);
                switch (ToEntry(fields, username, now, reviewExcerptMax, out var entry))
                {
                    case ItemVerdict.NotAnEntry:
                        notEntry++;
                        break;
                    case ItemVerdict.ForeignLink:
                        foreign++;
                        break;
                    case ItemVerdict.Invalid:
                        invalid++;
                        break;
                    default:
                        if (seen.TryAdd(entry!.ViewingId, entry))
                            items.Add(entry);
                        else if (seen[entry.ViewingId] == entry)
                            duplicate++;
                        else
                        {
                            conflict++;
                            conflicted.Add(entry.ViewingId);
                        }

                        break;
                }
            }

            // Account check BEFORE anything is used: another member's (or a redirected/login) feed is never a valid answer.
            if (!LetterboxdUrl.IsProfileOf(channelLink, username))
                return FeedParseResult.Malformed("channel link is not the followed member's profile");
            // A conflicting id drops the first item too: the feed contradicts itself about that entry.
            conflict += items.RemoveAll(i => conflicted.Contains(i.ViewingId));
            var skipped = new SkippedItems(notEntry, foreign, invalid, duplicate, conflict);
            if (total == 0)
                return new(FeedParseOutcome.Empty, [], 0, skipped, ttl, "feed has no items");
            if (items.Count == 0)
                return new(FeedParseOutcome.Malformed, [], total, skipped, ttl, $"none of the {total} items is a usable entry ({skipped})");
            return new(FeedParseOutcome.Ok, items, total, skipped, ttl, skipped.Total > 0 ? $"{skipped.Total} of {total} items skipped ({skipped})" : null);
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

    private enum ItemVerdict
    {
        Entry,
        NotAnEntry,
        ForeignLink,
        Invalid,
    }

    private static ItemVerdict ToEntry(Dictionary<string, string> fields, string username, DateTimeOffset now, int reviewExcerptMax, out LetterboxdEntry? entry)
    {
        entry = null;
        // Lists, likes and other activity carry other guids: they are never entries (and never cards).
        if (!fields.TryGetValue("guid", out var guid) || GuidPattern().Match(guid.Trim()) is not { Success: true } g ||
            !long.TryParse(g.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var viewingId) || viewingId <= 0)
            return ItemVerdict.NotAnEntry;
        var kind = g.Groups[1].Value == "review" ? EntryGuidKind.Review : EntryGuidKind.Watch;
        if (!LetterboxdUrl.TryParseEntry(fields.GetValueOrDefault("link"), username, out var slug, out var url))
            return ItemVerdict.ForeignLink;

        var filmTitle = Plain(fields.GetValueOrDefault("lb:filmTitle"), TitleMax);
        if (filmTitle.Length == 0)
            return ItemVerdict.Invalid;
        int? year = null;
        if (fields.TryGetValue("lb:filmYear", out var yearText))
        {
            if (!int.TryParse(yearText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) || y is < 1870 or > 2200)
                return ItemVerdict.Invalid;
            year = y;
        }

        int? rating = null;
        if (fields.TryGetValue("lb:memberRating", out var ratingText))
        {
            if (!EntryRating.TryParse(ratingText, out var halves))
                return ItemVerdict.Invalid;
            rating = halves;
        }

        bool? rewatch = fields.GetValueOrDefault("lb:rewatch")?.Trim() switch
        {
            "Yes" => true,
            "No" => false,
            _ => null, // absent or unknown: not a rewatch claim either way
        };
        DateOnly? watched = null;
        if (fields.TryGetValue("lb:watchedDate", out var watchedText))
        {
            if (!DateOnly.TryParseExact(watchedText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var w))
                return ItemVerdict.Invalid;
            watched = w;
        }

        var content = ReviewHtml.Read(fields.GetValueOrDefault("description"));
        string? review = null;
        var truncated = false;
        // Review text only for review guids: a watch item's description is the poster and "Watched on …".
        if (kind == EntryGuidKind.Review)
            review = ReviewHtml.Excerpt(content.Paragraphs, reviewExcerptMax, out truncated);
        bool? spoilers = content.SpoilerNotice || (fields.GetValueOrDefault("title")?.TrimEnd().EndsWith(SpoilerTitleSuffix, StringComparison.OrdinalIgnoreCase) ?? false)
            ? true
            : null;
        var view = new EntryView(url, filmTitle, year, rating, rewatch, watched, ParseDate(fields.GetValueOrDefault("pubDate"), now), review, truncated,
            review is null ? null : spoilers, LetterboxdUrl.Poster(content.FirstImageSource));
        entry = new LetterboxdEntry(viewingId, kind, slug, Id(fields.GetValueOrDefault("tmdb:movieId")), Id(fields.GetValueOrDefault("tmdb:tvId")), view);
        return ItemVerdict.Entry;
    }

    private static long? Id(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    /// <summary>RFC 822 dates as the feed writes them ("Thu, 22 Oct 2026 04:12:45 +1300"); missing, invalid or future → null.</summary>
    public static DateTimeOffset? ParseDate(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = OffsetPattern().Replace(value.Trim(), "$1$2:$3");
        string[] formats = ["ddd, d MMM yyyy HH:mm:ss zzz", "ddd, dd MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm:ss 'GMT'", "ddd, dd MMM yyyy HH:mm:ss 'GMT'", "r"];
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var at))
            return null;
        at = at.ToUniversalTime();
        return at > now + FutureTolerance || at.Year < 2000 ? null : at;
    }

    /// <summary>Plain single-line text: control characters and repeated whitespace removed, bounded.</summary>
    public static string Plain(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in System.Net.WebUtility.HtmlDecode(value))
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }

        var result = sb.ToString();
        return result.Length <= max ? result : result[..max];
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

    /// <summary>The item's known fields: plain RSS names, "lb:" for the Letterboxd namespace, "tmdb:" for TMDB. First wins.</summary>
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
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1)
                continue;
            var key = reader.NamespaceURI switch
            {
                "" when reader.LocalName is "title" or "link" or "guid" or "pubDate" or "description" => reader.LocalName,
                LetterboxdNamespace when reader.LocalName is "watchedDate" or "rewatch" or "filmTitle" or "filmYear" or "memberRating" => "lb:" + reader.LocalName,
                TmdbNamespace when reader.LocalName is "movieId" or "tvId" => "tmdb:" + reader.LocalName,
                _ => null,
            };
            if (key is null)
                continue;
            var value = reader.IsEmptyElement ? "" : ReadText(reader, reader.Depth);
            fields.TryAdd(key, value);
        }

        return fields;
    }

    /// <summary>Text and CDATA of the element (nested markup is skipped, not interpreted).</summary>
    private static string ReadText(XmlReader reader, int depth)
    {
        var text = new StringBuilder();
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

    private static string Short(string message) => message.Length > 160 ? message[..160] : message;

    [GeneratedRegex(@"^letterboxd-(watch|review)-([0-9]{1,15})$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"([+-])([0-9]{2})([0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();
}
