using System.Text;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Providers;

/// <summary>What a forum post says, in the two shapes the module uses: the whole text (classifier input, content hash) and the card excerpt.</summary>
public sealed record ForumPostContent(string Text, UpdateHighlights Highlights);

/// <summary>
/// Turns a post's rendered HTML into normalized text and <see cref="UpdateHighlights"/>. Deterministic: the same post gives
/// the same text, so a cosmetic re-render of the HTML is not an edit.
/// <para>Sections are found the way Blizzard's posts write them (checked on real Development Notes and Client Update posts,
/// 2026-10-05): a bold paragraph or a heading followed by a list, or a list item with its own text and a nested list. A list
/// without a title is a section without a heading. Version and build come from a "1.60.1 Build 69977" style line when the
/// post has one; a post without one is still a post.</para>
/// </summary>
public static partial class ForumPostReader
{
    public static ForumPostContent Read(string? cookedHtml)
    {
        var blocks = ForumHtml.Read(cookedHtml);
        var text = new StringBuilder();
        var sections = new List<UpdateSection>();
        var changes = 0;
        string? pending = null;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ForumHeading heading:
                    text.Append("# ").Append(heading.Text).Append('\n');
                    pending = heading.Text;
                    break;
                case ForumParagraph paragraph:
                    text.Append(paragraph.Text).Append('\n');
                    pending = paragraph.Emphasized ? paragraph.Text : null;
                    break;
                case ForumList list:
                    Write(text, list.Items, 0);
                    changes += Leaves(list.Items);
                    AddSections(sections, pending, list.Items);
                    pending = null;
                    break;
            }
        }

        var normalized = text.ToString();
        var (version, build) = VersionAndBuild(normalized);
        return new ForumPostContent(normalized, UpdateHighlights.Create(version, build, changes, sections));
    }

    /// <summary>"1.60.1 Build 69977" → ("1.60.1", "69977"); a build without a version → (null, build); neither → (null, null).</summary>
    public static (string? Version, string? Build) VersionAndBuild(string text)
    {
        try
        {
            var full = VersionBuild().Match(text);
            if (full.Success)
                return (full.Groups["version"].Value, full.Groups["build"].Value);
            var only = BuildOnly().Match(text);
            return only.Success ? (null, only.Groups["build"].Value) : (null, null);
        }
        catch (RegexMatchTimeoutException)
        {
            return (null, null);
        }
    }

    private static void AddSections(List<UpdateSection> sections, string? heading, IReadOnlyList<ForumListItem> items)
    {
        // The list's own lines form ONE section under the list's heading, placed where its first line stands; an item with
        // its own text and lines underneath ("Classes" + a nested list) is a section of its own. So a heading never repeats.
        var loose = new List<string>();
        var looseAt = -1;
        void Loose(IEnumerable<string> lines)
        {
            var before = loose.Count;
            loose.AddRange(lines.Where(l => l.Length > 0));
            if (looseAt < 0 && loose.Count > before)
                looseAt = sections.Count;
        }

        foreach (var item in items)
        {
            var below = item.Children.Select(c => c.Text).Where(t => t.Length > 0).ToList();
            if (item.Text.Length > 0 && below.Count > 0)
                sections.Add(new UpdateSection(Title(item.Text), below));
            else if (item.Text.Length > 0)
                Loose([item.Text]); // also an item whose nested list has no text of its own
            else
                Loose(below);
        }

        if (loose.Count > 0)
            sections.Insert(looseAt, new UpdateSection(Title(heading), loose));
    }

    private static string? Title(string? heading) => heading?.TrimEnd(':', ' ') is { Length: > 0 } title ? title : null;

    private static int Leaves(IReadOnlyList<ForumListItem> items) => items.Sum(i => i.Children.Count == 0 ? 1 : Leaves(i.Children));

    private static void Write(StringBuilder text, IReadOnlyList<ForumListItem> items, int depth)
    {
        foreach (var item in items)
        {
            text.Append(' ', depth * 2).Append("- ").Append(item.Text).Append('\n');
            Write(text, item.Children, depth + 1);
        }
    }

    [GeneratedRegex(@"(?<![\d.])(?<version>\d{1,3}\.\d{1,3}(?:\.\d{1,3}){0,2})\s+Build\s+(?<build>\d{4,7})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex VersionBuild();

    [GeneratedRegex(@"(?<![A-Za-z])Build\s+(?<build>\d{4,7})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex BuildOnly();
}
