using System.Globalization;

namespace ToroSquad.Modules.Updates.Domain.Games;

/// <summary>
/// Section "Updates:WowForever". Where World of Warcraft: Forever lives on Blizzard's forum: the forum categories whose
/// Blizzard posts are in scope, and the threads Blizzard keeps adding updates to. They change with the game's life (the beta
/// lived in category 349 with its Development Notes thread 2360696; the released game posts its hotfixes in the Bug Report
/// category and its announcements in General Discussion), so they are settings, not code.
/// <para>No list has a default value in the class on purpose: configuration binding appends to an array that already has
/// elements, so a configured list would be added to a default. Unset, the lists below apply.</para>
/// </summary>
public sealed class WowForeverSettings
{
    public const string Section = "Updates:WowForever";

    /// <summary>"WoW Forever Bug Report" — the category of Blizzard's running hotfix thread.</summary>
    public const int BugReportCategoryId = 352;

    /// <summary>"World of Warcraft: Forever General Discussion" — where Blizzard announces changes in their own threads.</summary>
    public const int GeneralDiscussionCategoryId = 347;

    /// <summary>"WoW: Forever Hotfixes" — Blizzard's running hotfix thread (one reply per hotfix day).</summary>
    public const int HotfixesTopicId = 2403040;

    /// <summary>Upper bound of categories (each one costs list pages in every round).</summary>
    public const int MaxCategories = 3;

    /// <summary>Upper bound of watched threads.</summary>
    public const int MaxWatchedTopics = 5;

    /// <summary>
    /// Forum categories whose Blizzard posts are in scope, each one by its own id: the forum lists a parent category's
    /// subcategories under the parent, but a thread belongs to exactly one category and only the listed ones count.
    /// </summary>
    public int[]? ForumCategoryIds { get; set; }

    /// <summary>Threads read post by post on every round (a running hotfix thread). Not set: the hotfix thread.</summary>
    public int[]? WatchedTopicIds { get; set; }

    /// <summary>The single category of the beta configuration; refused, so an old setting can never narrow the scope silently.</summary>
    public int? ForumCategoryId { get; set; }

    public IReadOnlyList<int> EffectiveCategoryIds =>
        (ForumCategoryIds ?? [BugReportCategoryId, GeneralDiscussionCategoryId]).Distinct().Order().ToList();

    public IReadOnlyList<int> EffectiveWatchedTopicIds => WatchedTopicIds ?? [HotfixesTopicId];

    /// <summary>
    /// The categories as the provider's game id ("347,352"): ascending, so the order of the configured list never looks like
    /// a move of the source.
    /// </summary>
    public string ProviderGameId => string.Join(',', EffectiveCategoryIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ForumCategoryId is not null)
            errors.Add(Section + ":ForumCategoryId is no longer read; list the categories in " + Section + ":ForumCategoryIds");
        var categories = ForumCategoryIds ?? [BugReportCategoryId, GeneralDiscussionCategoryId];
        if (categories.Length is 0 or > MaxCategories || categories.Any(id => id <= 0) || categories.Distinct().Count() != categories.Length)
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:ForumCategoryIds must be 1 to {MaxCategories} distinct positive category ids"));
        var watched = EffectiveWatchedTopicIds;
        if (watched.Count > MaxWatchedTopics || watched.Any(id => id <= 0) || watched.Distinct().Count() != watched.Count)
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:WatchedTopicIds must be at most {MaxWatchedTopics} distinct positive topic ids"));
        return errors;
    }
}

/// <summary>World of Warcraft: Forever, followed through Blizzard's posts on the official World of Warcraft forum.</summary>
public static class WowForeverGame
{
    public const string Key = "wow-forever";

    public static GameUpdateDefinition Create(WowForeverSettings settings) =>
        new(Key, "World of Warcraft: Forever", "WoW: Forever", "blizzard", settings.ProviderGameId, new WowForeverUpdateClassifier())
        {
            WatchedThreadIds = settings.EffectiveWatchedTopicIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(),
            // The categories are where the game lives today, not what a card is: its cards keep their identity when they move.
            OutboxScope = Key,
        };
}

/// <summary>
/// Which Blizzard posts in the World of Warcraft: Forever categories are game updates. The provider has already
/// established the scope (a Blizzard-tracked post, in one of the game's own categories — other WoW versions and player posts
/// never get here); this decides the kind of post, from several signals:
/// <list type="number">
/// <item>A title that names something else (known issues, maintenance, restarts, feedback, podcasts, previews, …) is not an
/// update, and a title that announces a change for later ("Incoming", "Upcoming", "Planned") is not an applied one. These
/// first steps never look at the text, so a provider can skip downloading such posts.</item>
/// <item>A title that also names another World of Warcraft version is ambiguous (with an update kind) or not an update.</item>
/// <item>The post must open its thread, or belong to a watched thread (Blizzard adds each hotfix day to the hotfix thread
/// as a further post). A Blizzard reply anywhere else is ambiguous.</item>
/// <item>A title that names an update kind — Development Notes, Client Update, Patch Notes, Hotfix — is backed by the text:
/// a build number, a list of changes, or Blizzard saying it applied a change. A pointer such as "notes are posted" is
/// ambiguous.</item>
/// <item>A title without an update kind ("Dungeon Enemy XP Increased") is an update only when Blizzard says in its own words
/// that a change was applied ("we deployed a hotfix that increased …", "we've adjusted the character creation system"). A
/// change it only plans ("we will adjust", "we plan to make changes") is not; neither is a lone word such as "hotfix" or
/// "fixed". Quoted text never counts (the post reader leaves quotes out).</item>
/// </list>
/// The reason of an update is its kind: <c>development_notes</c>, <c>client_update</c>, <c>patch_notes</c>, <c>hotfix</c>
/// or <c>applied_change</c>. Anything ambiguous is never posted: a wrong card is worse than a missed one.
/// </summary>
public sealed class WowForeverUpdateClassifier : IGameUpdateClassifier
{
    /// <summary>A list this short is a remark, not a change list.</summary>
    public const int MinChangeCount = 2;

    private static readonly (string Phrase, string Kind)[] Kinds =
    [
        ("development notes", "development_notes"),
        ("client update", "client_update"),
        ("patch notes", "patch_notes"),
        ("update notes", "patch_notes"),
        ("release notes", "patch_notes"),
        ("hotfixes", "hotfix"),
        ("hotfix", "hotfix"),
    ];

    private static readonly string[] NotAnUpdate =
    [
        "known issues", "known issue", "maintenance", "restart", "restarts", "downtime", "service issue", "service issues", "outage",
        "feedback", "bug report", "bug reports", "podcast", "deep dive", "deep dives", "preview", "interview", "q&a", "survey",
        "account actions", "weekly", "roundup", "giveaway", "contest", "sale", "trailer", "livestream", "new realm", "new realms", "queue", "queues",
    ];

    private static readonly string[] Planned = ["incoming", "upcoming", "planned", "coming soon"];

    private static readonly string[] OtherVersions =
    [
        "midnight", "the war within", "cataclysm", "mists of pandaria", "pandaria", "season of discovery", "classic era", "anniversary", "hardcore", "retail",
    ];

    public UpdateClassificationResult Classify(GameUpdateCandidate candidate)
    {
        var title = UpdateText.Normalize(candidate.Title);
        if (title.Length == 0)
            return new(UpdateClassification.Ambiguous, "no_title");
        if (NotAnUpdate.Any(p => UpdateText.ContainsWord(title, p)))
            return new(UpdateClassification.NotUpdate, "excluded_topic");
        if (Planned.Any(p => UpdateText.ContainsWord(title, p)))
            return new(UpdateClassification.NotUpdate, "planned_change");
        var kind = Kinds.FirstOrDefault(k => UpdateText.ContainsWord(title, k.Phrase)).Kind;
        if (OtherVersions.Any(v => UpdateText.ContainsWord(title, v)))
            return kind is null ? new(UpdateClassification.NotUpdate, "other_version_title") : new(UpdateClassification.Ambiguous, "other_version_title");

        var watched = candidate.Labels.Contains(UpdateLabels.WatchedThread);
        if (!watched && !candidate.Labels.Contains(UpdateLabels.FirstPost))
            return new(UpdateClassification.Ambiguous, "reply_outside_watched_thread");
        if (candidate.Labels.Contains(UpdateLabels.ExcerptOnly))
            return new(UpdateClassification.Ambiguous, "content_not_loaded");

        var said = AppliedChange.Read(candidate.Body);
        if (kind is not null)
        {
            var highlights = candidate.Highlights;
            var backed = said.Applied || highlights is not null && (highlights.Build is not null || highlights.ChangeCount >= MinChangeCount);
            return backed ? new(UpdateClassification.Update, kind) : new(UpdateClassification.Ambiguous, "no_change_list");
        }

        if (said.Applied)
            return new(UpdateClassification.Update, "applied_change");
        return said.Planned ? new(UpdateClassification.NotUpdate, "planned_change") : new(UpdateClassification.NotUpdate, "untyped_title");
    }
}

/// <summary>
/// Whether a post says, in its author's own words, that a change was applied — or only that one is planned. Small,
/// deterministic rules over whole words of one sentence at a time:
/// <list type="bullet">
/// <item>Applied: "we" (or "we've" / "we have", one adverb allowed: "we just deployed") followed by an applied verb
/// (deployed, applied, adjusted, increased, …), or a passive "has been / have been / was / were" followed by one ("a hotfix
/// has been deployed").</item>
/// <item>A sentence that speaks of the future ("will", "plan", "intend", "upcoming", "soon", "next", "investigating", …) is
/// never applied; when it has "we" as the speaker it counts as planned.</item>
/// </list>
/// "Updated" is not an applied verb: "we've updated the post above" is a remark about the notes, not a change to the game.
/// Bounded: at most <see cref="MaxSentences"/> sentences of at most <see cref="MaxSentenceLength"/> characters are looked at.
/// </summary>
public static class AppliedChange
{
    public const int MaxSentences = 400;
    public const int MaxSentenceLength = 600;

    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal)
    {
        "deployed", "applied", "adjusted", "increased", "reduced", "decreased", "lowered", "raised", "implemented", "released", "enabled",
        "disabled", "removed", "added", "fixed", "changed", "corrected", "resolved", "reverted", "restored", "rebalanced", "tuned",
    };

    private static readonly HashSet<string> Subjects = new(StringComparer.Ordinal) { "we", "we've" };

    private static readonly HashSet<string> Passive = new(StringComparer.Ordinal) { "been", "was", "were" };

    private static readonly HashSet<string> Future = new(StringComparer.Ordinal)
    {
        "will", "won't", "plan", "plans", "planning", "planned", "intend", "intends", "intending", "upcoming", "soon", "next", "tomorrow",
        "investigating", "going", "considering", "expect", "expected", "hope",
    };

    private static readonly HashSet<string> Planners = new(StringComparer.Ordinal) { "will", "plan", "plans", "planning", "intend", "intends", "intending", "going" };

    public readonly record struct Statement(bool Applied, bool Planned);

    public static Statement Read(string? text)
    {
        var applied = false;
        var planned = false;
        var sentences = 0;
        // Split first (a line of a list is a sentence of its own), then normalize: normalizing folds line breaks into spaces.
        foreach (var raw in Sentences(text ?? ""))
        {
            if (++sentences > MaxSentences)
                break;
            var sentence = UpdateText.Normalize(raw.Length > MaxSentenceLength ? raw[..MaxSentenceLength] : raw);
            var words = Words(sentence);
            var future = words.Any(Future.Contains);
            var speaker = words.Any(Subjects.Contains);
            if (future)
            {
                planned |= speaker && words.Any(Planners.Contains);
                continue;
            }

            applied |= AppliedIn(words);
        }

        return new Statement(applied, planned);
    }

    private static bool AppliedIn(IReadOnlyList<string> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var at = -1;
            if (Subjects.Contains(words[i]))
                at = i + 1 < words.Count && words[i] == "we" && words[i + 1] == "have" ? i + 2 : i + 1;
            else if (Passive.Contains(words[i]))
                at = i + 1;
            if (at < 0 || at >= words.Count)
                continue;
            if (Verbs.Contains(words[at]) || at + 1 < words.Count && IsAdverb(words[at]) && Verbs.Contains(words[at + 1]))
                return true;
        }

        return false;
    }

    private static bool IsAdverb(string word) => word is "just" or "now" or "also" or "already" or "recently" or "today" or "since" || word.EndsWith("ly", StringComparison.Ordinal);

    private static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not ('.' or '!' or '?' or ';' or '\n' or '\r'))
                continue;
            if (i > start)
                yield return text[start..i];
            start = i + 1;
        }
    }

    /// <summary>Whole words: letters, digits and the apostrophe of "we've" (normalized text folds typographic apostrophes).</summary>
    private static List<string> Words(string sentence)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= sentence.Length; i++)
        {
            var part = i < sentence.Length && (char.IsLetterOrDigit(sentence[i]) || sentence[i] == '\'' && start >= 0);
            if (part)
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
                words.Add(sentence[start..i].TrimEnd('\''));
            start = -1;
        }

        return words;
    }
}
