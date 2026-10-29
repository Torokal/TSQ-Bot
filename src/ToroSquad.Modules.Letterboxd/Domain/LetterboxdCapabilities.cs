namespace ToroSquad.Modules.Letterboxd.Domain;

public enum CapabilityState
{
    Supported = 0,
    NotVerified = 1,
    Blocked = 2,
}

/// <summary>One user behaviour the module is asked to cover, with the source that covers it (or why none does).</summary>
public sealed record Capability(string Key, CapabilityState State, string ReasonKey);

/// <summary>
/// What this module can really see — fixed facts from the source check of 2026-10-29, shown by status and doctor so a
/// partial coverage is never presented as complete. Changing a state here needs new evidence (docs/letterboxd/TSQ_LETTERBOXD.md).
/// <list type="bullet">
/// <item>ReviewDiary — Supported: the member's public RSS feed carries diary entries and reviews (guid, link, rating, rewatch,
/// watched date, review HTML).</item>
/// <item>UnratedRewatch — Supported: a rewatch without rating and review is a feed item (observed: World War Z, 2026-10-21).</item>
/// <item>EntryEdits — Supported within the feed window: a changed rating, rewatch flag or review of an entry still in the feed
/// edits its card; an edit to an entry that has left the window is not seen.</item>
/// <item>StandaloneRatings — Blocked: a star rating given on a film page without logging an entry is not a feed item (the
/// official API access page describes the profile RSS as "new diary entries, reviews and lists"); the official API is by
/// approval and not granted for personal projects; automated reading of the HTML film/rating pages is not permitted by the
/// Terms of Use (6.11). No other source is used. A diary entry's own rating (EntryEdits) is a different feature.</item>
/// </list>
/// </summary>
public static class LetterboxdCapabilities
{
    public static readonly Capability ReviewDiary = new("ReviewDiary", CapabilityState.Supported, "letterboxd.capability.review_diary");
    public static readonly Capability UnratedRewatch = new("UnratedRewatch", CapabilityState.Supported, "letterboxd.capability.unrated_rewatch");
    public static readonly Capability EntryEdits = new("EntryEdits", CapabilityState.Supported, "letterboxd.capability.entry_edits");
    public static readonly Capability StandaloneRatings = new("StandaloneRatings", CapabilityState.Blocked, "letterboxd.capability.standalone_ratings");

    public static IReadOnlyList<Capability> All { get; } = [ReviewDiary, UnratedRewatch, EntryEdits, StandaloneRatings];

    /// <summary>True only when every requested behaviour is supported — currently false (standalone ratings).</summary>
    public static bool FullCoverage => All.All(c => c.State == CapabilityState.Supported);
}
