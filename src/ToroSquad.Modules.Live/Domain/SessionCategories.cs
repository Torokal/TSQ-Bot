using System.Text;

namespace ToroSquad.Modules.Live.Domain;

/// <summary>
/// One game/category the bot observed during one creator session (persisted, one row per distinct category of the
/// session). The source platform is kept: which platform showed it first, and each platform's own category id.
/// </summary>
public sealed class SessionCategory
{
    public long Id { get; set; }
    public string CreatorKey { get; set; } = "";
    public int SessionNumber { get; set; }

    /// <summary>Insertion order inside the session (tie-break for equal first-seen times).</summary>
    public int Sequence { get; set; }

    /// <summary>Comparable form of the name (<see cref="SessionCategories.NameKey"/>); unique per session.</summary>
    public string NameKey { get; set; } = "";

    /// <summary>The name as the provider stated it when the category was first seen.</summary>
    public string Name { get; set; } = "";
    public LivePlatform FirstPlatform { get; set; }

    /// <summary>Twitch game id and Kick category id are separate id spaces: an equal number never means the same game.</summary>
    public string? TwitchCategoryId { get; set; }
    public string? KickCategoryId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }

    public string? PlatformId(LivePlatform platform) => platform == LivePlatform.Twitch ? TwitchCategoryId : KickCategoryId;

    public void SetPlatformId(LivePlatform platform, string id)
    {
        if (platform == LivePlatform.Twitch)
            TwitchCategoryId = id;
        else
            KickCategoryId = id;
    }
}

/// <summary>
/// The category history of a creator session (pure). It lists what the bot OBSERVED while the stream was live — the
/// providers are reconciled about every 30 seconds, so a category shown only briefly, or while the bot was down, can be
/// missing; nothing is guessed. Rules:
/// <list type="bullet">
/// <item>First-seen order, each category once: A → B → A → C → B is A, B, C. Returning to a category adds nothing.</item>
/// <item>Identity: the platform's own category id first (per platform — ids of different platforms are never compared),
/// otherwise the normalized name. Equal normalized names on different platforms are one entry; different names are never
/// merged (no fuzzy matching). A name-only entry learns the platform id when it is seen later.</item>
/// <item>Blank, placeholder or missing names never create an entry; no category is filtered by kind (Just Chatting, IRL
/// … are listed as the platform states them).</item>
/// </list>
/// </summary>
public static class SessionCategories
{
    public const int NameMax = 100;
    public const int IdMax = 64;

    /// <summary>Display form: control characters removed, whitespace collapsed, trimmed, bounded; null = not a category.</summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var sb = new StringBuilder(name.Length);
        var space = false;
        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (char.IsControl(ch))
                continue;
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }

        var clean = sb.ToString().Normalize(NormalizationForm.FormC);
        if (clean.Length == 0 || clean.ToUpperInvariant() is "UNKNOWN" or "NULL" or "UNDEFINED")
            return null;
        if (clean.Length <= NameMax)
            return clean;
        return clean[..(char.IsHighSurrogate(clean[NameMax - 1]) ? NameMax - 1 : NameMax)];
    }

    /// <summary>Comparable form of a cleaned name (case-insensitive, culture-invariant).</summary>
    public static string NameKey(string cleanName) => cleanName.ToUpperInvariant();

    /// <summary>A provider category id, or null when it is not a real id (blank, "0", oversized).</summary>
    public static string? CleanId(string? id)
    {
        var trimmed = id?.Trim();
        return trimmed is { Length: > 0 and <= IdMax } && trimmed != "0" ? trimmed : null;
    }

    /// <summary>
    /// Records one observed category into the session's list. Returns the new entry (to be persisted), or null when the
    /// category is already listed (possibly after learning the platform id) or is not a valid category.
    /// </summary>
    public static SessionCategory? Record(List<SessionCategory> session, string creatorKey, int sessionNumber, LivePlatform platform,
        string? categoryId, string? categoryName, DateTimeOffset seenAt)
    {
        var id = CleanId(categoryId);
        var name = CleanName(categoryName);
        var existing = id is null ? null : session.FirstOrDefault(c => c.PlatformId(platform) == id);
        if (existing is not null)
            return null; // same platform id: already listed (even if the provider renamed it meanwhile)
        if (name is null)
            return null; // no trustworthy name: never an entry
        var key = NameKey(name);
        existing = session.FirstOrDefault(c => c.NameKey == key);
        if (existing is not null)
        {
            if (id is not null && existing.PlatformId(platform) is null)
                existing.SetPlatformId(platform, id);
            return null;
        }

        var entry = new SessionCategory
        {
            CreatorKey = creatorKey,
            SessionNumber = sessionNumber,
            Sequence = session.Count == 0 ? 1 : session.Max(c => c.Sequence) + 1,
            NameKey = key,
            Name = name,
            FirstPlatform = platform,
            FirstSeenAt = seenAt,
        };
        if (id is not null)
            entry.SetPlatformId(platform, id);
        session.Add(entry);
        return entry;
    }

    /// <summary>First-seen order; equal times in insertion order (deterministic).</summary>
    public static IReadOnlyList<SessionCategory> Ordered(IEnumerable<SessionCategory> session) =>
        session.OrderBy(c => c.FirstSeenAt).ThenBy(c => c.Sequence).ToList();
}
