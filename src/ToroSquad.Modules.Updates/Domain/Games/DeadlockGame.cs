using System.Text;

namespace ToroSquad.Modules.Updates.Domain.Games;

/// <summary>
/// Deadlock on Steam (AppID 1422450): Valve's own announcements of the game, read with the same public Steam news method
/// as every Steam game. Only the classifier and the reader of Valve's patch-notes layout are Deadlock's own.
/// <para>Valve also posts change logs on forums.playdeadlock.com, sometimes as later replies that never reach Steam. That
/// forum answers an automated client with a browser check (observed 2026-10-05) and is therefore not read.</para>
/// </summary>
public static class DeadlockGame
{
    public const string Key = "deadlock";
    public const string SteamAppId = "1422450";

    public static GameUpdateDefinition Definition { get; } =
        new(Key, "Deadlock", "Deadlock", "steam", SteamAppId, new DeadlockUpdateClassifier()) { Highlighter = new DeadlockHighlighter() };
}

/// <summary>What <see cref="DeadlockNotesReader"/> found in a post.</summary>
/// <param name="Sections">The first change lines per section ("General", "Heroes", …), bounded.</param>
/// <param name="ChangeCount">Every change line of the post.</param>
/// <param name="Words">
/// The words of the text outside the change lines and headings, lower case, bounded; <see cref="DeadlockNotesReader.SentenceEnd"/>
/// stands where a sentence or a line ends.
/// </param>
/// <param name="Pages">Pages of the game's own site the post links to ("cityneversleeps"), lower case.</param>
/// <param name="Summary">The first sentence-like line outside the change lines, for a post without a change list.</param>
/// <param name="HeadingCount">Heading lines of the post ("[ General ]", a markup heading), with or without change lines under them.</param>
public sealed record DeadlockNotes(IReadOnlyList<UpdateSection> Sections, int ChangeCount, IReadOnlyList<string> Words, IReadOnlyList<string> Pages, string? Summary,
    int HeadingCount = 0);

/// <summary>
/// Reads Valve's Deadlock announcements as Steam delivers them (BBCode, as observed on 39 posts from 2024-10 to 2026-10):
/// patch notes are "[ General ]" style section lines followed by lines that start with "- " — inside <c>[p]</c> blocks in
/// newer posts, on plain lines in older ones, with the brackets sometimes escaped — while announcements are prose with
/// images and links. A small forward scanner, not a pattern match over markup: known tags become line breaks or
/// disappear, images are left out, link targets are only looked at (never requested), everything unknown is text. Bounded
/// in input, lines, sections and words, and it never throws on broken markup.
/// </summary>
public static class DeadlockNotesReader
{
    public const int MaxInputLength = 400_000;
    public const int MaxLineLength = 2000;
    public const int MaxSections = 64;
    public const int MaxWords = 2000;
    public const int MaxPages = 8;
    public const int MinSummaryLength = 40;

    /// <summary>The entry of <see cref="DeadlockNotes.Words"/> that marks the end of a sentence or a line.</summary>
    public const string SentenceEnd = ".";

    private const int MaxTagLength = 300;

    /// <summary>The only site whose pages count as the game's own.</summary>
    public const string SiteHost = "www.playdeadlock.com";

    private static readonly HashSet<string> LineBreaks = new(StringComparer.Ordinal)
    {
        "p", "/p", "br", "list", "/list", "olist", "/olist", "/*", "quote", "/quote", "code", "/code", "table", "/table", "tr", "/tr", "td", "/td", "th", "/th",
        "/h1", "/h2", "/h3", "/h4", "/h5", "/h6", "hr",
    };

    public static DeadlockNotes Read(string? markup)
    {
        var sections = new List<(string? Heading, List<string> Items)>();
        var words = new List<string>();
        var pages = new List<string>();
        var changes = 0;
        var headings = 0;
        string? summary = null;
        if (string.IsNullOrEmpty(markup))
            return new DeadlockNotes([], 0, [], [], null);

        var line = new StringBuilder();
        var heading = false;
        void EndLine(bool nextIsHeading)
        {
            var text = UpdateText.OneLine(line.ToString());
            line.Clear();
            var wasHeading = heading;
            heading = nextIsHeading;
            if (text.Length == 0)
                return;
            if (wasHeading || SectionTitle(text) is not null)
            {
                headings++;
                if (sections.Count < MaxSections)
                    sections.Add((SectionTitle(text) ?? text, []));
                return;
            }

            if (ChangeText(text) is { } change)
            {
                changes++;
                if (sections.Count == 0)
                    sections.Add((null, []));
                if (sections[^1].Items.Count < UpdateHighlights.MaxItemsPerSection)
                    sections[^1].Items.Add(change);
                return;
            }

            summary ??= text.Length >= MinSummaryLength && !text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? text : null;
            foreach (var word in text.Split(' '))
            {
                AddPage(pages, word);
                if (words.Count >= MaxWords)
                    break;
                if (Word(word) is { Length: > 0 } clean)
                    words.Add(clean);
                if (EndsSentence(word))
                    words.Add(SentenceEnd);
            }

            if (words.Count > 0 && words.Count < MaxWords && words[^1] != SentenceEnd)
                words.Add(SentenceEnd); // a line is at least a sentence
        }

        var end = Math.Min(markup.Length, MaxInputLength);
        for (var i = 0; i < end; i++)
        {
            var ch = markup[i];
            if (ch == '\\' && i + 1 < end && markup[i + 1] is '[' or ']')
            {
                Append(line, markup[++i]); // an escaped bracket is text
                continue;
            }

            if (ch == '\n')
            {
                EndLine(false);
                continue;
            }

            if (ch == '[' && TryReadTag(markup, i, end, out var name, out var value, out var after))
            {
                i = after - 1;
                if (LineBreaks.Contains(name))
                {
                    EndLine(false);
                }
                else if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                {
                    EndLine(true);
                }
                else if (name == "*")
                {
                    EndLine(false);
                    line.Append("- ");
                }
                else if (name == "img" && value is null)
                {
                    // [img]address[/img]: the address is not text.
                    var close = markup.IndexOf("[/img]", after, end - after, StringComparison.OrdinalIgnoreCase);
                    if (close >= 0)
                        i = close + "[/img]".Length - 1;
                }
                else if (name == "url" && value is not null)
                {
                    AddPage(pages, value);
                }

                continue;
            }

            Append(line, ch);
        }

        EndLine(false);
        return new DeadlockNotes(sections.Where(s => s.Items.Count > 0).Select(s => new UpdateSection(s.Heading, s.Items)).ToList(), changes, words, pages, summary, headings);
    }

    /// <summary>"overhaul." / "unlocked!" / "next?)" end a sentence; "0.25s" or "v1.2" in the middle of a word does not.</summary>
    private static bool EndsSentence(string token)
    {
        var end = token.TrimEnd(')', '"', '\'', '’', ']', '*');
        return end.Length > 0 && end[^1] is '.' or '!' or '?';
    }

    private static void Append(StringBuilder line, char ch)
    {
        if (line.Length < MaxLineLength)
            line.Append(ch);
    }

    /// <summary>"[ General ]" → "General"; anything else is not a section line.</summary>
    private static string? SectionTitle(string text)
    {
        if (text.Length is < 3 or > 64 || text[0] != '[' || text[^1] != ']')
            return null;
        var inner = text[1..^1].Trim();
        return inner.Length > 0 && inner.IndexOfAny(['[', ']']) < 0 ? inner : null;
    }

    /// <summary>"- Base health reduced" → "Base health reduced"; a line that does not start with a list mark is not a change.</summary>
    private static string? ChangeText(string text)
    {
        if (text.Length < 3 || text[0] is not ('-' or '–' or '•') || text[1] != ' ')
            return null;
        var change = text[2..].TrimStart();
        return change.Length > 0 ? change : null;
    }

    private static string Word(string token)
    {
        var word = token.Trim().Trim('.', ',', ';', ':', '!', '?', '(', ')', '"', '*', '[', ']').ToLowerInvariant();
        return word.Replace('’', '\'');
    }

    /// <summary>Remembers a link to a page of the game's own site (https, that host, one path segment); anything else is ignored.</summary>
    private static void AddPage(List<string> pages, string value)
    {
        if (pages.Count >= MaxPages || value.Length is < 12 or > 200)
            return;
        var address = value.Trim('"', '\'', '.', ',', ')', '(');
        if (!address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.Equals(uri.IdnHost, SiteHost, StringComparison.OrdinalIgnoreCase))
            return;
        var page = uri.AbsolutePath.Trim('/').ToLowerInvariant();
        if (page.Length is < 3 or > 40 || !page.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') || pages.Contains(page))
            return;
        pages.Add(page);
    }

    /// <summary>
    /// A markup tag starting at <paramref name="start"/>: its lower-case name ("/p", "*", "url"), the value after "=" or the
    /// attributes after a space, and the index after it. False when the bracket is text — "[ General ]", a bracket that
    /// never closes, a name that is not a tag name.
    /// </summary>
    private static bool TryReadTag(string markup, int start, int end, out string name, out string? value, out int after)
    {
        name = "";
        value = null;
        after = start + 1;
        var limit = Math.Min(end, start + MaxTagLength);
        var close = -1;
        for (var i = start + 1; i < limit; i++)
        {
            var ch = markup[i];
            if (ch == ']')
            {
                close = i;
                break;
            }

            if (ch is '[' or '\n')
                return false;
        }

        if (close < 0 || close == start + 1)
            return false;
        var inner = markup.AsSpan(start + 1, close - start - 1);
        var n = 0;
        if (inner[0] == '/')
            n++;
        if (n < inner.Length && inner[n] == '*')
        {
            n++;
        }
        else
        {
            var letters = n;
            while (n < inner.Length && char.IsAsciiLetterOrDigit(inner[n]))
                n++;
            if (n == letters || !char.IsAsciiLetter(inner[letters]))
                return false;
        }

        if (n < inner.Length && inner[n] is not ('=' or ' '))
            return false;
        name = inner[..n].ToString().ToLowerInvariant();
        if (n < inner.Length)
            value = inner[(n + 1)..].ToString().Trim().Trim('"');
        after = close + 1;
        return true;
    }
}

/// <summary>
/// The card excerpt of a Deadlock post: the first change lines per section and the size of the whole list, or — for an
/// announcement without a change list — its first sentence-like line. Bounded by <see cref="UpdateHighlights"/>.
/// </summary>
public sealed class DeadlockHighlighter : IGameUpdateHighlighter
{
    public UpdateHighlights? Read(string title, string text)
    {
        var notes = DeadlockNotesReader.Read(text);
        if (notes.ChangeCount > 0)
            return UpdateHighlights.Create(null, null, notes.ChangeCount, notes.Sections);
        return notes.Summary is null ? null : UpdateHighlights.Create(null, null, 0, [new UpdateSection(null, [notes.Summary])]);
    }
}

/// <summary>
/// Which of Valve's Deadlock announcements on Steam are game updates. The provider has established the scope (an official
/// announcement of AppID 1422450); this decides the kind of post. Hero reveals and other announcements share the feed, and
/// Steam's own "patchnotes" tag is only on the small updates — "Gameplay Update", "Matchmaking Update" and the big named
/// updates ("City Never Sleeps") carry none — so no single field decides. Signals, each read from the post itself:
/// <list type="bullet">
/// <item><b>tag</b>: Steam's <c>patchnotes</c> tag.</item>
/// <item><b>title</b>: the title names an update ("update", "patch", "hotfix", "changelog" as a word).</item>
/// <item><b>list</b>: at least <see cref="MinChangeLines"/> change lines in Valve's layout.</item>
/// <item><b>declared</b>: the text speaks of the release itself — "this update", "today's update", "today's … patch".</item>
/// <item><b>page</b>: the post links to a page of the game's own site, and is <b>named after it</b> when the title starts
/// with the page's name ("City Never Sleeps" → /cityneversleeps, "Old Gods, New Blood" → /oldgods).</item>
/// <item><b>declared change</b>: the declaration goes on to say what the release does — "today's update adds four new
/// heroes to matchmaking", "this update includes …".</item>
/// <item><b>change statements</b> under <b>headings</b>: sentences that state a change to the game as done — "has been
/// updated", "has received a major overhaul", "replaces the existing …", "is now", "no longer" — in a post with headed
/// sections ("The Hideout", "Map Update", "Character Select Screen").</item>
/// </list>
/// An update is: a tagged post with a title, a list or a declaration; an update title with a list or a declaration; a post
/// named after its own page that speaks of an update, or a declared release that links to a page of the site (unless it
/// says it is still to come); a declared change; or at least <see cref="MinChangeStatements"/> change statements in a post
/// with at least <see cref="MinHeadings"/> headings. Any other post with a signal is ambiguous, a post with none is not an
/// update. A hero reveal says who the hero is and that the hero can be played — none of that is a signal here, and there
/// is no list of hero names or update names.
/// <para>Checked on the 39 official posts of 2024-10 to 2026-10: all 27 updates are found — also the named ones without
/// "update" in the title ("City Never Sleeps", "Old Gods, New Blood", "Six New Heroes", "Holliday, Vyper, Calico, and The
/// Magnificent Sinclair") — and none of the 12 hero reveals is one. Anything ambiguous is never posted: a wrong card is
/// worse than a missed one.</para>
/// </summary>
public sealed class DeadlockUpdateClassifier : IGameUpdateClassifier
{
    public const string PatchNotesLabel = "patchnotes";

    /// <summary>Fewer lines than this are a remark or a list of something else (vote counts, links).</summary>
    public const int MinChangeLines = 3;

    /// <summary>A page name shorter than this is too little to recognize a title by.</summary>
    public const int MinPageNameLength = 6;

    /// <summary>How many words may stand between "this" / "today's" and "update" ("today's winter-themed visual update").</summary>
    public const int MaxDeclarationGap = 3;

    /// <summary>Fewer change statements than this do not make a post an update by themselves.</summary>
    public const int MinChangeStatements = 3;

    /// <summary>A post needs at least this many headings for its change statements to count as an update's sections.</summary>
    public const int MinHeadings = 2;

    private static readonly string[] UpdateWords = ["update", "updates", "patch", "hotfix", "hotfixes", "changelog"];

    /// <summary>What a declared release does, right after "this update" / "today's update".</summary>
    private static readonly HashSet<string> ReleaseVerbs = new(StringComparer.Ordinal)
    {
        "adds", "add", "introduces", "introduce", "includes", "include", "brings", "bring", "changes", "reworks", "updates", "fixes", "removes", "replaces",
        "revamps", "overhauls", "contains", "features",
    };

    /// <summary>"has been updated", "have been removed", …</summary>
    private static readonly HashSet<string> DoneChanges = new(StringComparer.Ordinal)
    {
        "updated", "added", "removed", "reworked", "redesigned", "changed", "replaced", "improved", "overhauled", "increased", "reduced", "fixed", "adjusted",
        "rebalanced", "revamped", "moved", "enabled", "disabled",
    };

    public UpdateClassificationResult Classify(GameUpdateCandidate candidate)
    {
        var title = UpdateText.Normalize(candidate.Title);
        if (title.Length == 0)
            return new(UpdateClassification.Ambiguous, "no_title");
        var notes = DeadlockNotesReader.Read(candidate.Body);

        var tagged = candidate.Labels.Any(l => string.Equals(l, PatchNotesLabel, StringComparison.OrdinalIgnoreCase));
        var updateTitle = UpdateWords.Any(w => UpdateText.ContainsWord(title, w));
        var list = notes.ChangeCount >= MinChangeLines;
        var (declared, declaredChange) = Declares(notes.Words);
        var named = notes.Words.Any(w => w is "update" or "updates") && NamedAfterPage(title, notes.Pages);
        var statements = ChangeStatements(notes.Words);

        if (tagged && (updateTitle || list || declared))
            return new(UpdateClassification.Update, "patch_notes");
        if (updateTitle && (list || declared))
            return new(UpdateClassification.Update, "titled_update");
        if (named || (declared && notes.Pages.Count > 0))
        {
            // An announcement of something that is not out yet is not an update, however it is named.
            return StillToCome(notes.Words) ? new(UpdateClassification.Ambiguous, "named_but_upcoming") : new(UpdateClassification.Update, "named_update");
        }

        if (declaredChange)
            return new(UpdateClassification.Update, "declared_update");
        if (statements >= MinChangeStatements && notes.HeadingCount >= MinHeadings)
            return new(UpdateClassification.Update, "substantial_update");

        if (tagged)
            return new(UpdateClassification.Ambiguous, "tagged_without_changes");
        if (updateTitle)
            return new(UpdateClassification.Ambiguous, "update_title_only");
        if (declared)
            return new(UpdateClassification.Ambiguous, "declared_without_changes");
        if (list)
            return new(UpdateClassification.Ambiguous, "list_only");
        if (statements > 0)
            return new(UpdateClassification.Ambiguous, "change_statements_only");
        return new(UpdateClassification.NotUpdate, "no_update_signal");
    }

    /// <summary>
    /// "this update", "today's update", "today's winter-themed visual update", "this patch" — and whether such a declaration
    /// goes on to say what the release does ("today's update adds …"). Never across the end of a sentence.
    /// </summary>
    private static (bool Declared, bool DeclaredChange) Declares(IReadOnlyList<string> words)
    {
        var declared = false;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i] is not ("this" or "today's" or "todays"))
                continue;
            for (var j = i + 1; j < words.Count && j <= i + 1 + MaxDeclarationGap && words[j] != DeadlockNotesReader.SentenceEnd; j++)
            {
                if (words[j] is not ("update" or "patch"))
                    continue;
                declared = true;
                if (j + 1 < words.Count && ReleaseVerbs.Contains(words[j + 1]))
                    return (true, true);
                break;
            }
        }

        return (declared, false);
    }

    /// <summary>
    /// How many sentences state a change to the game as done: "has been updated", "have been removed", "has received",
    /// "replaces the", "is now", "can now", "now has", "no longer". "Available to play now" is not one of them.
    /// </summary>
    private static int ChangeStatements(IReadOnlyList<string> words)
    {
        var count = 0;
        var found = false;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (word == DeadlockNotesReader.SentenceEnd)
            {
                found = false;
                continue;
            }

            if (found || i + 1 >= words.Count)
                continue;
            var next = words[i + 1];
            var hit = word switch
            {
                "has" or "have" when next == "received" => true,
                "has" or "have" when next == "been" && i + 2 < words.Count =>
                    DoneChanges.Contains(words[i + 2]) || (words[i + 2].EndsWith("ly", StringComparison.Ordinal) && i + 3 < words.Count && DoneChanges.Contains(words[i + 3])),
                "replaces" or "replace" => next == "the",
                "is" or "are" or "can" or "will" => next == "now",
                "now" => next is "has" or "have" or "grants" or "grant" or "deals" or "deal" or "does",
                "no" => next == "longer",
                _ => false,
            };
            if (!hit)
                continue;
            found = true;
            count++;
        }

        return count;
    }

    /// <summary>
    /// "coming soon": the post announces, it does not ship. Deliberately only this — a real update's letter also speaks of
    /// what "will be released" later ("City Never Sleeps" does).
    /// </summary>
    private static bool StillToCome(IReadOnlyList<string> words)
    {
        for (var i = 0; i + 1 < words.Count; i++)
        {
            if (words[i] == "coming" && words[i + 1] == "soon")
                return true;
        }

        return false;
    }

    /// <summary>True when the title starts with the name of a linked page ("old gods, new blood" and the page "oldgods").</summary>
    private static bool NamedAfterPage(string normalizedTitle, IReadOnlyList<string> pages)
    {
        if (pages.Count == 0)
            return false;
        var letters = new string(normalizedTitle.Where(char.IsAsciiLetterOrDigit).ToArray());
        return pages.Select(p => p.Replace("-", "", StringComparison.Ordinal))
            .Any(page => page.Length >= MinPageNameLength && letters.StartsWith(page, StringComparison.Ordinal));
    }
}
