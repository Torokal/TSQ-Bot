using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Providers;

/// <summary>What a forum post says, in the two shapes the module uses: the whole text (classifier input, content hash) and the card excerpt.</summary>
public sealed record ForumPostContent(string Text, UpdateHighlights Highlights);

/// <summary>One dated part of a running hotfix post: its date line ("November 9, 2026") and everything up to the next one.</summary>
public sealed record ForumDatedSection(DateOnly Date, string Label, ForumPostContent Content);

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
    /// <summary>Upper bound of dated sections read from one post.</summary>
    public const int MaxDatedSections = 64;

    private static readonly string[] Months = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[..12];

    public static ForumPostContent Read(string? cookedHtml) => Read(ForumHtml.Read(cookedHtml));

    /// <summary>
    /// The dated sections of a post written the way Blizzard writes its running hotfix thread (checked 2026-11-10): a bold
    /// paragraph or a heading that is nothing but a full date — "November 9, 2026" — opens the section of that day, which
    /// runs to the next such line. Each section is read on its own (its text starts with its date line). A post without a
    /// date line has no sections, and neither has a post with anything but headings before its first date line (an
    /// announcement that mentions a date); a heading before it ("Hotfixes") belongs to no section.
    /// </summary>
    public static IReadOnlyList<ForumDatedSection> DatedSections(string? cookedHtml)
    {
        var blocks = ForumHtml.Read(cookedHtml);
        var starts = new List<(int Index, DateOnly Date, string Label)>();
        for (var i = 0; i < blocks.Count && starts.Count < MaxDatedSections; i++)
        {
            var line = blocks[i] switch
            {
                ForumHeading heading => heading.Text,
                ForumParagraph { Emphasized: true } paragraph => paragraph.Text,
                _ => null,
            };
            if (line is not null && DateLine(line) is { } date)
                starts.Add((i, date.Date, date.Label));
        }

        // Only a post that IS dated sections: nothing but headings before the first date line. A date line in the middle of
        // an announcement does not cut the announcement apart.
        if (starts.Count == 0 || blocks.Take(starts[0].Index).Any(b => b is not ForumHeading))
            return [];
        var sections = new List<ForumDatedSection>(starts.Count);
        for (var s = 0; s < starts.Count; s++)
        {
            var (index, date, label) = starts[s];
            var end = s + 1 < starts.Count ? starts[s + 1].Index : blocks.Count;
            var part = new List<ForumBlock> { new ForumHeading(label) };
            part.AddRange(blocks.Skip(index + 1).Take(end - index - 1));
            sections.Add(new ForumDatedSection(date, label, Read(part)));
        }

        return sections;
    }

    /// <summary>"November 9, 2026" (also "Monday, November 9, 2026", a trailing colon) → the date and its label; anything else → null.</summary>
    public static (DateOnly Date, string Label)? DateLine(string text)
    {
        var line = text.Trim().TrimEnd(':').Trim();
        if (line.Length is 0 or > 40)
            return null;
        try
        {
            var match = DatePattern().Match(line);
            if (!match.Success)
                return null;
            var month = Array.FindIndex(Months, m => string.Equals(m, match.Groups["month"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
            var day = int.Parse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            var year = int.Parse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (month < 1 || year is < 2000 or > 2100 || day < 1 || day > DateTime.DaysInMonth(year, month))
                return null;
            var date = new DateOnly(year, month, day);
            return (date, date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static ForumPostContent Read(IReadOnlyList<ForumBlock> blocks)
    {
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

    [GeneratedRegex(@"^(?:(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday),?\s+)?(?<month>[a-z]{3,9})\s+(?<day>\d{1,2}),?\s+(?<year>\d{4})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<![\d.])(?<version>\d{1,3}\.\d{1,3}(?:\.\d{1,3}){0,2})\s+Build\s+(?<build>\d{4,7})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex VersionBuild();

    [GeneratedRegex(@"(?<![A-Za-z])Build\s+(?<build>\d{4,7})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex BuildOnly();
}
