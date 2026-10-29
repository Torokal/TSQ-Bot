using System.Net;
using System.Text;

namespace ToroSquad.Modules.Letterboxd.Domain;

/// <summary>What the item description contains, as plain text.</summary>
public sealed record ReviewContent(IReadOnlyList<string> Paragraphs, bool SpoilerNotice, string? FirstImageSource);

/// <summary>
/// Reads the HTML of a feed item's &lt;description&gt; into plain paragraphs with a small bounded scanner (no regex, no DOM,
/// nothing loaded): &lt;p&gt; and &lt;br&gt; separate text, &lt;img&gt; only yields its src (the poster), script/style/iframe
/// content is dropped, every other tag is removed and its text kept, entities are decoded. The feed's own spoiler notice
/// paragraph ("This review may contain spoilers.") is reported, not kept as review text.
/// </summary>
public static class ReviewHtml
{
    public const int MaxInputChars = 200_000;
    public const string SpoilerNotice = "This review may contain spoilers.";

    public static ReviewContent Read(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return new ReviewContent([], false, null);
        var text = html.Length > MaxInputChars ? html[..MaxInputChars] : html;
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        string? image = null;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch != '<')
            {
                current.Append(ch);
                i++;
                continue;
            }

            var end = text.IndexOf('>', i + 1);
            if (end < 0)
                break; // an unterminated tag ends the text
            var tag = text.AsSpan(i + 1, end - i - 1);
            var closing = tag.Length > 0 && tag[0] == '/';
            var name = TagName(closing ? tag[1..] : tag);
            i = end + 1;
            switch (name)
            {
                case "p" or "div" or "blockquote" or "li" or "ul" or "ol":
                    Flush(current, paragraphs);
                    break;
                case "br":
                    current.Append('\n');
                    break;
                case "img" when !closing:
                    image ??= Attribute(tag, "src");
                    break;
                case "script" or "style" or "iframe" when !closing:
                    var close = text.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                    i = close < 0 ? text.Length : close;
                    break;
            }
        }

        Flush(current, paragraphs);
        var notice = paragraphs.Count > 0 && string.Equals(paragraphs[0], SpoilerNotice, StringComparison.OrdinalIgnoreCase);
        return new ReviewContent(notice ? paragraphs.Skip(1).ToList() : paragraphs, notice, image);
    }

    /// <summary>
    /// The card excerpt: paragraphs joined by a blank line, at most <paramref name="max"/> characters, cut at a word boundary
    /// with "…" when longer (<paramref name="truncated"/>). Null when there is no text.
    /// </summary>
    public static string? Excerpt(IReadOnlyList<string> paragraphs, int max, out bool truncated)
    {
        truncated = false;
        var joined = string.Join("\n\n", paragraphs.Where(p => p.Length > 0));
        if (joined.Length == 0)
            return null;
        if (joined.Length <= max)
            return joined;
        truncated = true;
        var cut = joined.LastIndexOfAny([' ', '\n'], max - 1);
        return joined[..(cut > max / 2 ? cut : max - 1)].TrimEnd() + "…";
    }

    private static string TagName(ReadOnlySpan<char> tag)
    {
        var length = 0;
        while (length < tag.Length && length < 12 && char.IsAsciiLetterOrDigit(tag[length]))
            length++;
        return tag[..length].ToString().ToLowerInvariant();
    }

    private static string? Attribute(ReadOnlySpan<char> tag, string name)
    {
        var text = tag.ToString();
        var at = text.IndexOf(" " + name + "=", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;
        var start = at + name.Length + 2;
        if (start >= text.Length || text[start] is not ('"' or '\''))
            return null;
        var quote = text[start];
        var end = text.IndexOf(quote, start + 1);
        return end < 0 ? null : WebUtility.HtmlDecode(text[(start + 1)..end]);
    }

    private static void Flush(StringBuilder current, List<string> paragraphs)
    {
        if (current.Length == 0)
            return;
        var lines = WebUtility.HtmlDecode(current.ToString()).Split('\n')
            .Select(Collapse).Where(l => l.Length > 0);
        var paragraph = string.Join("\n", lines);
        current.Clear();
        if (paragraph.Length > 0)
            paragraphs.Add(paragraph);
    }

    private static string Collapse(string line)
    {
        var sb = new StringBuilder(line.Length);
        var space = false;
        foreach (var ch in line)
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

        return sb.ToString();
    }
}
