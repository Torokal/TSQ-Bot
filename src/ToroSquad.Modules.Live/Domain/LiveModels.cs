using System.Text;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Live.Domain;

public enum LivePlatform
{
    Twitch = 1,
    Kick = 2,
}

public static partial class LivePlatforms
{
    /// <summary>Display and button order.</summary>
    public static IReadOnlyList<LivePlatform> All { get; } = [LivePlatform.Twitch, LivePlatform.Kick];

    public static string Key(this LivePlatform platform) => platform == LivePlatform.Twitch ? "twitch" : "kick";

    public static string Name(this LivePlatform platform) => platform == LivePlatform.Twitch ? "Twitch" : "Kick";

    /// <summary>Twitch logins and Kick slugs: lowercase letters, digits and underscores (Kick also allows '-').</summary>
    public static bool IsValidLogin(LivePlatform platform, string? login) =>
        login is { Length: >= 1 and <= 25 } && (platform == LivePlatform.Twitch ? TwitchLogin() : KickSlug()).IsMatch(login);

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchLogin();

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KickSlug();
}

/// <summary>One channel of a creator on one platform. <see cref="Login"/> is the lowercase login/slug.</summary>
public sealed record TrackedChannel(LivePlatform Platform, string Login)
{
    public string Url => Platform == LivePlatform.Twitch ? "https://www.twitch.tv/" + Login : "https://kick.com/" + Login;
}

/// <summary>
/// A logical creator: one person streaming on one or more platforms. A multistream is ONE session with ONE announcement.
/// </summary>
public sealed record TrackedCreator(string Key, string DisplayName, IReadOnlyList<TrackedChannel> Channels)
{
    public TrackedChannel? Channel(LivePlatform platform) => Channels.FirstOrDefault(c => c.Platform == platform);
}

public enum PlatformStatus
{
    /// <summary>Never observed (no trustworthy provider answer yet).</summary>
    Unknown = 0,
    Offline = 1,
    Live = 2,
}

public enum CreatorPhase
{
    Offline = 0,
    Live = 1,

    /// <summary>Every platform went offline; the session survives a quick comeback (OBS reconnect, provider flap).</summary>
    ReconnectGrace = 2,
}

public enum ObservationKind
{
    /// <summary>The provider stated whether the channel is live (optionally with title, category, start time).</summary>
    Status = 0,

    /// <summary>Title/category only (e.g. a metadata update event); says nothing about live/offline.</summary>
    Metadata = 1,
}

/// <summary>
/// One trustworthy provider statement about one channel. A failed or ambiguous request is never an observation —
/// "request failed" never means "offline". <see cref="ObservedAt"/> is when the statement was true (the time a poll
/// request was sent, or the provider's event time); older statements never overwrite newer ones.
/// </summary>
public sealed record LiveObservation(
    LivePlatform Platform,
    string Login,
    ObservationKind Kind,
    bool IsLive,
    DateTimeOffset ObservedAt,
    string? StreamId = null,
    DateTimeOffset? StartedAt = null,
    string? Title = null,
    string? Category = null,
    string? AvatarUrl = null,
    string? EventId = null);

/// <summary>Persisted per-creator session state (one row per creator).</summary>
public sealed class CreatorState
{
    public string CreatorKey { get; set; } = "";
    public CreatorPhase Phase { get; set; }

    /// <summary>Monotonic per creator; part of the announcement's outbox key (one message per session).</summary>
    public int SessionNumber { get; set; }
    public DateTimeOffset? SessionStartedAt { get; set; }
    public DateTimeOffset? SessionDetectedAt { get; set; }
    public DateTimeOffset? GraceSince { get; set; }
    public DateTimeOffset? SessionEndedAt { get; set; }

    /// <summary>Platforms that were live at some point of the current/last session (comma separated keys).</summary>
    public string SessionPlatforms { get; set; } = "";

    /// <summary>True only when a new session was allowed to announce (never for a baseline/catch-up session).</summary>
    public bool Announced { get; set; }

    /// <summary>Why the session was not announced: bootstrap, gap, delivery_off.</summary>
    public string? NotAnnouncedReason { get; set; }

    /// <summary>Outbox kind of the current announcement ("announce", or "announce-r1" after one mention-free replacement).</summary>
    public string? AnnouncementKind { get; set; }
    public ulong? AnnouncementGuildId { get; set; }
    public ulong? AnnouncementChannelId { get; set; }
    public ulong? AnnouncementMessageId { get; set; }
    public DateTimeOffset? AnnouncedAt { get; set; }
    public int Replacements { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool HasPlatformInSession(LivePlatform platform) =>
        SessionPlatforms.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(platform.Key(), StringComparer.Ordinal);

    public void AddSessionPlatform(LivePlatform platform)
    {
        if (!HasPlatformInSession(platform))
            SessionPlatforms = string.Join(',', LivePlatforms.All.Where(p => p == platform || HasPlatformInSession(p)).Select(p => p.Key()));
    }
}

/// <summary>Persisted per-channel state (one row per creator and platform).</summary>
public sealed class PlatformState
{
    public string CreatorKey { get; set; } = "";
    public LivePlatform Platform { get; set; }
    public string Login { get; set; } = "";
    public PlatformStatus Status { get; set; }
    public string? StreamId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset? TitleChangedAt { get; set; }
    public string? Category { get; set; }
    public string? AvatarUrl { get; set; }

    /// <summary>Time of the newest applied live/offline statement (ordering, continuity, grace confirmation).</summary>
    public DateTimeOffset? StatusObservedAt { get; set; }

    /// <summary>Time of the newest applied title/category statement (ordering of metadata).</summary>
    public DateTimeOffset? MetadataObservedAt { get; set; }
    public DateTimeOffset? LastLiveAt { get; set; }
    public DateTimeOffset? LastOfflineAt { get; set; }
    public string? LastEventId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class LiveText
{
    public const int TitleMax = 200;

    /// <summary>
    /// The comparable form of a stream title: control characters removed, whitespace runs collapsed, trimmed, bounded.
    /// Two titles that only differ in whitespace are the same title (no edit). Null/blank = "no title stated".
    /// </summary>
    public static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var sb = new StringBuilder(title.Length);
        var space = false;
        foreach (var ch in title)
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

        var normalized = sb.ToString().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0)
            return null;
        if (normalized.Length <= TitleMax)
            return normalized;
        var cut = char.IsHighSurrogate(normalized[TitleMax - 1]) ? TitleMax - 1 : TitleMax; // never split a surrogate pair
        return normalized[..cut];
    }
}
