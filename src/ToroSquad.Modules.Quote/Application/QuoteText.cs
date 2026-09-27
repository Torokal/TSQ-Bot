using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Quote.Application;

/// <summary>Display names for the mentions in one message (resolved by the Discord adapter from the message and the guild cache).</summary>
public sealed record QuoteMentionNames(
    IReadOnlyDictionary<ulong, string> Users,
    IReadOnlyDictionary<ulong, string> Roles,
    IReadOnlyDictionary<ulong, string> Channels)
{
    public static QuoteMentionNames Empty { get; } = new(new Dictionary<ulong, string>(), new Dictionary<ulong, string>(), new Dictionary<ulong, string>());
}

/// <summary>
/// Turns a message's raw Discord text into what a reader sees in the client, as plain text for the card: mentions become
/// names (<c>&lt;@id&gt;</c> → @Name, <c>&lt;#id&gt;</c> → #kanal, <c>&lt;@&amp;id&gt;</c> → @rol), formatting markers are
/// dropped (**x** → x) while the words stay, escaped characters stay literal, code keeps its exact text, newlines are
/// kept. Nothing is summarized or reworded. TSQ's <c>DiscordText</c> only defuses untrusted text for Discord; there is no
/// shared markdown-to-plain-text helper, so the rules live here, next to their only user.
/// </summary>
public static partial class QuoteText
{
    /// <summary>
    /// Upper bound of text handed to the layout. At the smallest font size the card shows far fewer characters than this,
    /// so the visible result is the same (it ends in "…" either way); the bound only keeps layout time predictable for
    /// 4,000-character messages. Not a design limit: the renderer decides what fits.
    /// </summary>
    public const int MaxLayoutLength = 1200;

    private const char EscapeBase = ''; // private use area: stand-ins for escaped ASCII markdown characters

    public static string Normalize(string? content, QuoteMentionNames names, CultureInfo culture, TimeZoneInfo zone)
    {
        if (string.IsNullOrEmpty(content))
            return "";
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        // Code keeps its exact text (Discord renders nothing inside it); everything else is plain markdown.
        var result = new StringBuilder(text.Length);
        var position = 0;
        foreach (Match code in CodePattern().Matches(text))
        {
            result.Append(Plain(text[position..code.Index], names, culture, zone));
            result.Append(code.Groups["fenced"].Success ? code.Groups["fenced"].Value.Trim('\n') : code.Groups["inline"].Value);
            position = code.Index + code.Length;
        }

        result.Append(Plain(text[position..], names, culture, zone));
        return Clean(result.ToString());
    }

    /// <summary>
    /// Cuts at a grapheme boundary (never inside an emoji or a letter with combining marks) and marks the cut with "…".
    /// </summary>
    public static string LimitForLayout(string text, int maxLength = MaxLayoutLength)
    {
        if (text.Length <= maxLength)
            return text;
        var cut = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext() && enumerator.ElementIndex + ((string)enumerator.Current).Length <= maxLength - 1)
            cut = enumerator.ElementIndex + ((string)enumerator.Current).Length;
        return text[..cut].TrimEnd() + "…";
    }

    private static string Plain(string text, QuoteMentionNames names, CultureInfo culture, TimeZoneInfo zone)
    {
        if (text.Length == 0)
            return text;

        // 1. \* \_ … are literal characters, never formatting: park them outside the markdown rules.
        text = EscapePattern().Replace(text, m => ((char)(EscapeBase + m.Groups[1].Value[0])).ToString());

        // 2. Links and line-level markers.
        text = MaskedLinkPattern().Replace(text, "${text}");
        text = AutolinkPattern().Replace(text, "${url}");
        text = BlockQuoteAllPattern().Replace(text, "");
        text = LineMarkerPattern().Replace(text, "");

        // 3. Inline formatting, outermost first (***x*** → *x* → x).
        text = BoldPattern().Replace(text, "${t}");
        text = UnderlinePattern().Replace(text, "${t}");
        text = StrikePattern().Replace(text, "${t}");
        text = SpoilerPattern().Replace(text, "${t}");
        text = StarItalicPattern().Replace(text, "${t}");
        text = UnderscoreItalicPattern().Replace(text, "${t}");

        // 4. Discord tokens → what the client shows. Names are inserted after the markdown rules so a name like "__x__" stays intact.
        text = TokenPattern().Replace(text, m => Token(m, names, culture, zone));

        // 5. Escaped characters back as themselves.
        var restored = new StringBuilder(text.Length);
        foreach (var ch in text)
            restored.Append(ch is >= EscapeBase and < (char)(EscapeBase + 128) ? (char)(ch - EscapeBase) : ch);
        return restored.ToString();
    }

    private static string Token(Match m, QuoteMentionNames names, CultureInfo culture, TimeZoneInfo zone)
    {
        static ulong Id(Group g) => ulong.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

        if (m.Groups["user"].Success)
            return "@" + (names.Users.TryGetValue(Id(m.Groups["user"]), out var user) ? Name(user) : "unknown-user");
        if (m.Groups["role"].Success)
            return "@" + (names.Roles.TryGetValue(Id(m.Groups["role"]), out var role) ? Name(role) : "unknown-role");
        if (m.Groups["channel"].Success)
            return "#" + (names.Channels.TryGetValue(Id(m.Groups["channel"]), out var channel) ? Name(channel) : "unknown-channel");
        if (m.Groups["command"].Success)
            return "/" + m.Groups["command"].Value;
        if (m.Groups["emoji"].Success)
            return ":" + m.Groups["emoji"].Value + ":";
        if (m.Groups["unix"].Success && long.TryParse(m.Groups["unix"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var unix) &&
            unix is > -62_135_596_800 and < 253_402_300_800)
        {
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unix), zone);
            var format = m.Groups["style"].Value switch
            {
                "t" => "t",
                "T" => "T",
                "d" => "d",
                "D" => "D",
                _ => "g", // f, F, R (relative) and no style: a fixed date and time — "3 hours ago" would be wrong tomorrow
            };
            return local.ToString(format, culture);
        }

        return m.Value;
    }

    /// <summary>A name or other one-line text from Discord as plain text: one line, no control or bidi-override characters.</summary>
    public static string SingleLine(string? value) =>
        Sanitize(value).Replace('\n', ' ').Trim() is { Length: > 0 } clean ? clean : "?";

    private static string Name(string value) => SingleLine(value);

    /// <summary>
    /// Drops what cannot be drawn and must not steer drawing: control characters (tabs become spaces, newlines stay), bidi
    /// embedding/override/isolate controls, and lone UTF-16 surrogates (malformed text).
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(ch).Append(text[++i]);
                continue;
            }

            if (ch == '\t')
                sb.Append(' ');
            else if (ch == '\n' || (!char.IsControl(ch) && !IsBidiControl(ch) && !char.IsSurrogate(ch)))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string Clean(string text)
    {
        var lines = Sanitize(text).Split('\n').Select(l => l.TrimEnd());
        return BlankLinesPattern().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    /// <summary>Embedding/override/isolate controls: they would reorder the rendered text, and a card cannot show them anyway.</summary>
    private static bool IsBidiControl(char c) => c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩';

    [GeneratedRegex(@"```(?:[A-Za-z0-9_+\-.#]+\n)?(?<fenced>[\s\S]*?)```|``(?<inline>[^\n]+?)``|`(?<inline>[^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\\([\\*_~`|>#\-\[\]()<:@/])", RegexOptions.CultureInvariant)]
    private static partial Regex EscapePattern();

    [GeneratedRegex(@"\[(?<text>[^\[\]\n]+)\]\(<?(?<url>https?://[^\s)>]+)>?\)", RegexOptions.CultureInvariant)]
    private static partial Regex MaskedLinkPattern();

    [GeneratedRegex(@"<(?<url>https?://[^\s>]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex AutolinkPattern();

    /// <summary>"&gt;&gt;&gt; " quotes the rest of the message; only the marker goes.</summary>
    [GeneratedRegex(@"^>>> ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockQuoteAllPattern();

    /// <summary>Line starts Discord renders as layout, not text: headings (#, ##, ###), subtext (-#) and quotes (&gt;).</summary>
    [GeneratedRegex(@"^(?:#{1,3} |-# |> )", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LineMarkerPattern();

    [GeneratedRegex(@"\*\*(?=\S)(?<t>[\s\S]*?\S)\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"__(?=\S)(?<t>[\s\S]*?\S)__", RegexOptions.CultureInvariant)]
    private static partial Regex UnderlinePattern();

    [GeneratedRegex(@"~~(?=\S)(?<t>[\s\S]*?\S)~~", RegexOptions.CultureInvariant)]
    private static partial Regex StrikePattern();

    [GeneratedRegex(@"\|\|(?<t>[\s\S]+?)\|\|", RegexOptions.CultureInvariant)]
    private static partial Regex SpoilerPattern();

    /// <summary>Single * or _ only around a whole run (not inside words): "2*3*4" and snake_case_names keep their characters.</summary>
    [GeneratedRegex(@"(?<![\w*])\*(?=\S)(?<t>[^*\n]*?\S)\*(?![\w*])", RegexOptions.CultureInvariant)]
    private static partial Regex StarItalicPattern();

    [GeneratedRegex(@"(?<![\w_])_(?=\S)(?<t>[^_\n]*?\S)_(?![\w_])", RegexOptions.CultureInvariant)]
    private static partial Regex UnderscoreItalicPattern();

    [GeneratedRegex(@"<@!?(?<user>\d{1,20})>|<@&(?<role>\d{1,20})>|<#(?<channel>\d{1,20})>|</(?<command>[\w\- ]{1,100}):\d{1,20}>|<a?:(?<emoji>\w{1,32}):\d{1,20}>|<t:(?<unix>-?\d{1,13})(?::(?<style>[tTdDfFR]))?>", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLinesPattern();
}
