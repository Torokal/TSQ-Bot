using System.Text.RegularExpressions;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>
/// Light, deterministic clean-up of the model's answer before it is posted — never a second AI call, never a rewrite of the
/// content: code fences are removed, anything before the title goes, the title becomes exactly
/// <see cref="SummaryPrompt.Title"/>, deeper headings become <c>##</c>, and <c>@everyone</c>/<c>@here</c> are defused (the
/// message is also sent without allowed mentions, so nothing in it can ping). Then <see cref="Split"/> keeps every part
/// within Discord's 2000 characters.
/// </summary>
public static partial class SummaryOutput
{
    /// <summary>
    /// The answer as Markdown, or null when there is nothing usable in it. <paramref name="cutOff"/>: the model hit the token
    /// limit (finish_reason "length"), so its last line is incomplete and is dropped.
    /// </summary>
    public static string? Normalize(string? raw, bool cutOff = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (cutOff)
            raw = raw.TrimEnd().LastIndexOf('\n') is var end and > 0 ? raw[..end] : "";

        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Where(l => !CodeFence().IsMatch(l))
            .Select(l => l.TrimEnd())
            .ToList();

        // Everything before the title (a preamble like "İşte özet:") goes; without a title, everything before the first section.
        var title = lines.FindIndex(l => TitleLine().IsMatch(l));
        if (title >= 0)
            lines.RemoveRange(0, title + 1);
        else if (lines.FindIndex(l => l.TrimStart().StartsWith('#')) is var first and >= 0)
            lines.RemoveRange(0, first);

        for (var i = 0; i < lines.Count; i++)
        {
            var heading = Heading().Match(lines[i]);
            if (heading.Success)
                lines[i] = "## " + heading.Groups["text"].Value.Trim();
        }

        var body = string.Join("\n", lines).Trim();
        body = ExtraBlankLines().Replace(body, "\n\n");
        body = MassMention().Replace(body, "@\u200B$1");
        return body.Length == 0 ? null : SummaryPrompt.Title + "\n\n" + body;
    }

    /// <summary>
    /// Parts of at most <paramref name="limit"/> characters, cut at a section (<c>##</c>), then a bullet, then a line, then a
    /// space — never inside a word unless one word alone is longer than the limit. The first part starts with the title.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int limit = DiscordLimits.ContentMax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 100);
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > limit)
        {
            var cut = LastBoundary(rest, limit, "\n## ") ?? LastBoundary(rest, limit, "\n- ") ?? LastBoundary(rest, limit, "\n")
                ?? LastBoundary(rest, limit, " ") ?? limit;
            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
            parts.Add(rest);
        return parts;
    }

    /// <summary>The start of the last <paramref name="separator"/> that leaves a non-trivial first part within the limit.</summary>
    private static int? LastBoundary(string text, int limit, string separator)
    {
        var at = text.LastIndexOf(separator, limit - 1, limit, StringComparison.Ordinal);
        return at > limit / 4 ? at : null;
    }

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex CodeFence();

    /// <summary>"# Son Mesajların Özeti", "📝 Son Mesajların Özeti", "**Son Mesajların Özeti**" and similar.</summary>
    [GeneratedRegex(@"^\s*(#{1,6}\s*)?[\p{So}\p{Sk}\p{Mn}\p{Cf}\p{Cs}\s*_]*Son\s+Mesajlar[ıi]n\s+[ÖöOo]zeti[\s*_:.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TitleLine();

    /// <summary>Any heading below the title: "# X", "### X" … → "## X".</summary>
    [GeneratedRegex(@"^\s*#{1,6}\s+(?<text>\S.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"@(everyone|here)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MassMention();
}
