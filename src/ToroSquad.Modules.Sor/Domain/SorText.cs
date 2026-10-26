using System.Text;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Sor.Domain;

/// <summary>Text handling for /sor: untrusted input made inert for the prompt, and answers split for Discord.</summary>
public static partial class SorText
{
    /// <summary>Discord embed descriptions hold 4096 characters; a part leaves room for a reopened code fence.</summary>
    public const int MaxPartLength = 3800;

    /// <summary>One answer is at most this many messages (the first one is the interaction response).</summary>
    public const int MaxParts = 3;

    public const string CutMarker = "\n…";

    /// <summary>
    /// Member text (questions, notes, names) as DATA inside the prompt: the block tags and the memory separator are defused so
    /// a message can neither close its block nor start a memory proposal. Control characters other than newlines and tabs
    /// are removed.
    /// </summary>
    public static string Inert(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\t' || !char.IsControl(c))
                builder.Append(c);
        }

        var cleaned = builder.ToString().Replace(SorAnswerFormat.Separator, "<< <HAFIZA> >>", StringComparison.Ordinal);
        return BlockTag().Replace(cleaned, m => "‹" + m.Value[1..^1] + "›");
    }

    /// <summary>A display name as a short, single-line, quoted-safe label.</summary>
    public static string Name(string? name)
    {
        var line = Inert(name).Replace('\n', ' ').Replace('"', '\'').Trim();
        return line.Length == 0 ? "?" : line.Length <= 64 ? line : line[..64];
    }

    /// <summary>The first raw user mention (&lt;@id&gt; or &lt;@!id&gt;) in a question, if any.</summary>
    public static ulong? FirstUserMention(string text)
    {
        var match = UserMention().Match(text);
        return match.Success && ulong.TryParse(match.Groups[1].ValueSpan, out var id) ? id : null;
    }

    /// <summary>
    /// Raw mentions replaced by readable labels before the text reaches the model or storage: the resolved member by their
    /// name, any other user, role or channel mention by a neutral word. No Discord id is sent to the model.
    /// </summary>
    public static string ReplaceMentions(string text, ulong? knownId, string? knownName)
    {
        var users = UserMention().Replace(text, m =>
            knownId is { } id && ulong.TryParse(m.Groups[1].ValueSpan, out var found) && found == id ? "@" + Name(knownName) : "@üye");
        return RoleOrChannelMention().Replace(users, m => m.Value[1] == '#' ? "#kanal" : "@rol");
    }

    /// <summary>A short excerpt of the question for the answer card (the full question is never repeated).</summary>
    public static string Excerpt(string question, int max = 90)
    {
        var line = Regex.Replace(question, @"\s+", " ").Trim();
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Splits an answer into at most <see cref="MaxParts"/> parts of at most <see cref="MaxPartLength"/> characters, at a
    /// paragraph or line break when possible. A code block cut by a split is closed and reopened (same language) in the next
    /// part; a split is moved before an open spoiler (||) so a spoiler is never cut. What does not fit ends with
    /// <see cref="CutMarker"/>.
    /// </summary>
    public static IReadOnlyList<string> Split(string answer, int maxPart = MaxPartLength, int maxParts = MaxParts)
    {
        var text = answer.Trim();
        var parts = new List<string>();
        string? reopen = null;
        while (text.Length > 0)
        {
            var prefix = reopen is null ? "" : reopen + "\n";
            var room = maxPart - prefix.Length - 4; // a closing fence may be added
            if (parts.Count == maxParts - 1 && prefix.Length + text.Length > maxPart)
            {
                var last = prefix + text[..Math.Max(0, room - CutMarker.Length)].TrimEnd();
                parts.Add(CloseOpen(last, out _) + CutMarker);
                break;
            }

            if (prefix.Length + text.Length <= maxPart)
            {
                parts.Add(CloseOpen(prefix + text, out _));
                break;
            }

            var cut = SplitPoint(text, room);
            var piece = prefix + text[..cut].TrimEnd();
            parts.Add(CloseOpen(piece, out reopen));
            text = text[cut..].TrimStart('\n');
        }

        return parts;
    }

    /// <summary>The best split index in the first <paramref name="room"/> characters: paragraph, line, space; never inside a spoiler.</summary>
    private static int SplitPoint(string text, int room)
    {
        var window = text[..Math.Min(room, text.Length)];
        var at = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (at < room / 2)
            at = window.LastIndexOf('\n');
        if (at < room / 2)
            at = window.LastIndexOf(' ');
        if (at <= 0)
            at = window.Length;

        // An odd number of "||" before the cut means a spoiler is open: cut before it instead (when that leaves something).
        var head = text[..at];
        if (CountOf(head, "||") % 2 == 1)
        {
            var open = head.LastIndexOf("||", StringComparison.Ordinal);
            if (open > 0)
                at = open;
        }

        return at;
    }

    /// <summary>Closes a code fence left open at the end of a part and says how to reopen it ("```lang").</summary>
    private static string CloseOpen(string part, out string? reopen)
    {
        reopen = null;
        var fences = Fence().Matches(part);
        if (fences.Count % 2 == 0)
            return part;
        reopen = fences[^1].Value.TrimEnd();
        return part.TrimEnd() + "\n```";
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [GeneratedRegex(@"</?\s*(baglam|bağlam|soru|sistem|system|assistant|user)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"<@!?(\d{1,20})>")]
    private static partial Regex UserMention();

    [GeneratedRegex(@"<(@&|#)\d{1,20}>")]
    private static partial Regex RoleOrChannelMention();

    [GeneratedRegex(@"```[^\s`]*", RegexOptions.Multiline)]
    private static partial Regex Fence();
}
