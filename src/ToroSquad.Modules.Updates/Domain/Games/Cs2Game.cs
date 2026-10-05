using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Updates.Domain.Games;

/// <summary>Counter-Strike 2 on Steam (AppID 730) — the first registered game.</summary>
public static class Cs2Game
{
    public const string Key = "cs2";
    public const string SteamAppId = "730";

    public static GameUpdateDefinition Definition { get; } = new(Key, "Counter-Strike 2", "CS2", "steam", SteamAppId, new Cs2UpdateClassifier());
}

/// <summary>
/// Which official Counter-Strike 2 announcements are game updates. Deliberately conservative: a wrong card is worse than a
/// missed one, so anything inconclusive is <see cref="UpdateClassification.Ambiguous"/> and never posted.
/// <para>Rules (checked against the official announcement feed, 150 posts from 2024-11 to 2026-10, on 2026-10-05):</para>
/// <list type="number">
/// <item>The exact titles Valve uses for patches — "Counter-Strike 2 Update" (108 of the 150 posts) and "Counter-Strike 2
/// Pre-Release Update" — are an update on their own.</item>
/// <item>Otherwise three independent signals are counted: an update-like title (ends with "update", or starts with "release
/// notes" / "patch notes"), Steam's own <c>patchnotes</c> tag, and the patch-notes shape of the text (a "[ SECTION ]" header
/// plus list items). Two or more make an update; exactly one is ambiguous; none is not an update.</item>
/// <item>A title that names an event, a tournament, an item release (stickers, cases, passes, …) or the workshop never becomes an update through rule 2:
/// with any signal it is ambiguous, without one it is not an update.</item>
/// </list>
/// The word "update" somewhere in a title is not a signal, and a single pair of square brackets is not a section.
/// </summary>
public sealed partial class Cs2UpdateClassifier : IGameUpdateClassifier
{
    public const string PatchNotesLabel = "patchnotes";

    private static readonly string[] StrongTitles = ["counter-strike 2 update", "counter-strike 2 pre-release update"];

    private static readonly string[] EventOrMarketingWords =
    [
        "major", "playoffs", "playoff", "champions", "champion", "finals", "tournament", "sticker", "stickers", "capsule", "capsules",
        "sale", "merch", "merchandise", "souvenir", "medal", "workshop", "pick'em", "pass", "case", "collection", "collections",
        "charm", "charms", "music kit", "music kits",
    ];

    public UpdateClassificationResult Classify(GameUpdateCandidate candidate)
    {
        var title = UpdateText.Normalize(candidate.Title);
        if (title.Length == 0)
            return new(UpdateClassification.Ambiguous, "no_title");
        if (StrongTitles.Contains(title, StringComparer.Ordinal))
            return new(UpdateClassification.Update, "strong_title");

        var updateTitle = IsUpdateLikeTitle(title);
        var tagged = candidate.Labels.Any(l => string.Equals(l, PatchNotesLabel, StringComparison.OrdinalIgnoreCase));
        var shaped = HasPatchNotesShape(candidate.Body);
        var signals = (updateTitle ? 1 : 0) + (tagged ? 1 : 0) + (shaped ? 1 : 0);

        if (EventOrMarketingWords.Any(w => UpdateText.ContainsWord(title, w)))
            return signals > 0 ? new(UpdateClassification.Ambiguous, "conflicting_signals") : new(UpdateClassification.NotUpdate, "event_or_marketing_title");

        if (signals >= 2)
            return new(UpdateClassification.Update, updateTitle && tagged ? "tagged_update_title" : updateTitle ? "update_title_with_patch_notes" : "tagged_patch_notes");
        if (updateTitle)
            return new(UpdateClassification.Ambiguous, "unsupported_update_title");
        if (tagged)
            return new(UpdateClassification.Ambiguous, "tagged_without_patch_notes");
        if (shaped)
            return new(UpdateClassification.Ambiguous, "patch_notes_shape_only");
        return new(UpdateClassification.NotUpdate, UpdateText.ContainsWord(title, "update") ? "update_word_only" : "no_update_signal");
    }

    /// <summary>"… Update" as the last word, or a title that starts with "Release Notes" / "Patch Notes".</summary>
    public static bool IsUpdateLikeTitle(string normalizedTitle) =>
        normalizedTitle == "update" || normalizedTitle.EndsWith(" update", StringComparison.Ordinal) ||
        normalizedTitle.StartsWith("release notes", StringComparison.Ordinal) || normalizedTitle.StartsWith("patch notes", StringComparison.Ordinal);

    /// <summary>
    /// Valve's patch-notes layout: at least one "[ SECTION ]" header in capitals (as in "[ MAPS ]", "[ MISC ]") and at least one
    /// list item. Lower-case markup tags such as [p], [list] or [url=…] are never a section.
    /// </summary>
    public static bool HasPatchNotesShape(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return false;
        try
        {
            return SectionHeader().IsMatch(body) && ListItem().IsMatch(body);
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // no evidence rather than a guess
        }
    }

    [GeneratedRegex(@"\[ [A-Z][A-Z0-9 &/'\-]{1,38} \]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"\[\*\]|^[ \t]*[-–•][ \t]+\S", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 250)]
    private static partial Regex ListItem();
}
