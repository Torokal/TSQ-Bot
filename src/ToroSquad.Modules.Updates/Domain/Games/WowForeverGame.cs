using System.Globalization;

namespace ToroSquad.Modules.Updates.Domain.Games;

/// <summary>
/// Section "Updates:WowForever". Where World of Warcraft: Forever lives on Blizzard's forum today — the beta discussion
/// category and the Development Notes thread Blizzard keeps adding builds to. Both are expected to change when the game
/// leaves beta, so they are settings, not code.
/// </summary>
public sealed class WowForeverSettings
{
    public const string Section = "Updates:WowForever";

    /// <summary>Forum category whose Blizzard posts are in scope ("WoW: Forever Beta Discussion").</summary>
    public int ForumCategoryId { get; set; } = 349;

    /// <summary>The thread watched when nothing is configured ("WoW Forever Beta Development Notes").</summary>
    public const int DevelopmentNotesTopicId = 2360696;

    /// <summary>
    /// Threads watched post by post. Not set: the Development Notes thread. (No default value on purpose: configuration
    /// binding appends to an array that already has elements, so a configured list would be added to the default.)
    /// </summary>
    public int[]? WatchedTopicIds { get; set; }

    public IReadOnlyList<int> EffectiveWatchedTopicIds => WatchedTopicIds ?? [DevelopmentNotesTopicId];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ForumCategoryId <= 0)
            errors.Add(Section + ":ForumCategoryId must be a positive forum category id");
        var watched = EffectiveWatchedTopicIds;
        if (watched.Count > 5 || watched.Any(id => id <= 0) || watched.Distinct().Count() != watched.Count)
            errors.Add(Section + ":WatchedTopicIds must be at most 5 distinct positive topic ids");
        return errors;
    }
}

/// <summary>World of Warcraft: Forever, followed through Blizzard's posts on the official World of Warcraft forum.</summary>
public static class WowForeverGame
{
    public const string Key = "wow-forever";

    public static GameUpdateDefinition Create(WowForeverSettings settings) =>
        new(Key, "World of Warcraft: Forever", "WoW: Forever", "blizzard", settings.ForumCategoryId.ToString(CultureInfo.InvariantCulture), new WowForeverUpdateClassifier())
        {
            WatchedThreadIds = settings.EffectiveWatchedTopicIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(),
        };
}

/// <summary>
/// Which Blizzard posts in the World of Warcraft: Forever forum category are game updates. The provider has already
/// established the scope (a Blizzard-tracked post, in the game's own category — other WoW versions and player posts never
/// get here); this decides the kind of post, from several signals:
/// <list type="number">
/// <item>The thread title must name an update kind — Development Notes, Client Update, Patch Notes, Hotfix. A title that
/// names something else (known issues, maintenance, restarts, feedback, podcasts, previews, …) or no kind at all is not an
/// update. This first step never looks at the text, so a provider can skip downloading such posts.</item>
/// <item>A title that also names another World of Warcraft version is ambiguous.</item>
/// <item>The post must open its thread, or belong to a watched thread (Blizzard adds new builds to the Development Notes
/// thread as further posts). A Blizzard reply anywhere else is ambiguous.</item>
/// <item>The text must back the title: a build number, or a list of changes. A pointer such as "notes are posted" under an
/// update-like title is ambiguous.</item>
/// </list>
/// The reason of an update is its kind: <c>development_notes</c>, <c>client_update</c>, <c>patch_notes</c> or
/// <c>hotfix</c>. Anything ambiguous is never posted: a wrong card is worse than a missed one.
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
        "account actions", "weekly", "roundup", "giveaway", "contest", "sale", "trailer", "livestream",
    ];

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
        var kind = Kinds.FirstOrDefault(k => UpdateText.ContainsWord(title, k.Phrase)).Kind;
        if (kind is null)
            return new(UpdateClassification.NotUpdate, "untyped_title");

        if (OtherVersions.Any(v => UpdateText.ContainsWord(title, v)))
            return new(UpdateClassification.Ambiguous, "other_version_title");
        var watched = candidate.Labels.Contains(UpdateLabels.WatchedThread);
        if (!watched && !candidate.Labels.Contains(UpdateLabels.FirstPost))
            return new(UpdateClassification.Ambiguous, "reply_outside_watched_thread");
        if (candidate.Labels.Contains(UpdateLabels.ExcerptOnly))
            return new(UpdateClassification.Ambiguous, "content_not_loaded");

        var highlights = candidate.Highlights;
        var backed = highlights is not null && (highlights.Build is not null || highlights.ChangeCount >= MinChangeCount);
        return backed ? new(UpdateClassification.Update, kind) : new(UpdateClassification.Ambiguous, "no_change_list");
    }
}
