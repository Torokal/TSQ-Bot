namespace ToroSquad.Modules.Live.Domain;

/// <param name="ReconnectGrace">A session survives "every platform offline" for this long (OBS reconnects, flaps).</param>
/// <param name="Continuity">Two status observations further apart than this are not continuous (restart, outage, disabled).</param>
/// <param name="AnnounceExistingLiveOnBootstrap">Announce a stream that is already live at the very first observation of a channel.</param>
public sealed record LiveRules(TimeSpan ReconnectGrace, TimeSpan Continuity, bool AnnounceExistingLiveOnBootstrap);

public enum LiveEffectKind
{
    SessionStarted,
    PlatformJoined,
    TitleChanged,
    CategoryChanged,
    PlatformWentOffline,
    GraceEntered,
    ReconnectedWithinGrace,
    SessionEnded,
    FirstObservation,
    DuplicateIgnored,
    StaleIgnored,
}

/// <summary>What an observation or tick changed (drives structured logging; the card is re-rendered from state).</summary>
public sealed record LiveEffect(LiveEffectKind Kind, string CreatorKey, LivePlatform? Platform, string? Detail = null);

/// <summary>
/// The creator session state machine (pure; no I/O, no clock of its own). Per platform: Unknown → Offline ⇄ Live. Per
/// creator: Offline → Live ⇄ ReconnectGrace → Offline. Rules (docs/live/TSQ_LIVE.md):
/// <list type="bullet">
/// <item>A creator is live while ANY platform is live; the session ends only after every platform was offline for the
/// whole grace period AND each platform was confirmed offline by an observation made after the grace deadline (an outage
/// during the grace keeps the session — unknown is never offline).</item>
/// <item>The first platform opens the session; a second platform joins it (no second announcement).</item>
/// <item>Only a brand-new session can be announced, and only when the bot was watching: the very first observation of a
/// channel is a baseline (unless <see cref="LiveRules.AnnounceExistingLiveOnBootstrap"/>); after a gap in observation a
/// stream is announced when its provider start time is after the last statement that saw the channel offline (it started
/// while the bot was not looking), however long ago that was.</item>
/// <item>Statements older than the newest applied one are ignored (per platform, separately for status and metadata);
/// a repeated provider event id is a duplicate.</item>
/// </list>
/// </summary>
public static class LiveStateMachine
{
    public const int CategoryMax = 100;
    public const int AvatarUrlMax = 512;

    public static IReadOnlyList<LiveEffect> Apply(CreatorState creator, IReadOnlyList<PlatformState> platforms, LiveObservation o, LiveRules rules,
        bool announcementsAllowed, DateTimeOffset now)
    {
        var p = platforms.Single(x => x.Platform == o.Platform);
        var effects = new List<LiveEffect>();
        if (o.EventId is { } eventId && eventId == p.LastEventId)
            return [new(LiveEffectKind.DuplicateIgnored, creator.CreatorKey, o.Platform, eventId)];

        var applied = false;
        if (o.Title is not null || o.Category is not null)
        {
            if (p.MetadataObservedAt is { } m && o.ObservedAt < m)
            {
                if (o.Kind == ObservationKind.Metadata)
                    return [new(LiveEffectKind.StaleIgnored, creator.CreatorKey, o.Platform, "metadata")];
            }
            else
            {
                applied = true;
                p.MetadataObservedAt = o.ObservedAt;
                if (LiveText.NormalizeTitle(o.Title) is { } title && title != p.Title)
                {
                    var hadTitle = p.Title is not null;
                    p.Title = title;
                    p.TitleChangedAt = o.ObservedAt;
                    if (hadTitle)
                        effects.Add(new(LiveEffectKind.TitleChanged, creator.CreatorKey, o.Platform));
                }

                var category = string.IsNullOrWhiteSpace(o.Category) ? null : o.Category.Trim();
                if (category is { Length: > CategoryMax })
                    category = category[..CategoryMax];
                if (category is not null && category != p.Category)
                {
                    var hadCategory = p.Category is not null;
                    p.Category = category;
                    if (hadCategory)
                        effects.Add(new(LiveEffectKind.CategoryChanged, creator.CreatorKey, o.Platform));
                }
            }
        }

        if (o.AvatarUrl is { Length: > 0 and <= AvatarUrlMax } avatar)
            p.AvatarUrl = avatar;

        if (o.Kind == ObservationKind.Status)
        {
            if (p.StatusObservedAt is { } s && o.ObservedAt < s)
            {
                effects.Add(new(LiveEffectKind.StaleIgnored, creator.CreatorKey, o.Platform, "status"));
            }
            else
            {
                applied = true;
                var previous = p.StatusObservedAt;
                p.StatusObservedAt = o.ObservedAt;
                if (o.IsLive)
                    ApplyLive(creator, platforms, p, o, previous, rules, announcementsAllowed, now, effects);
                else
                    ApplyOffline(creator, platforms, p, o, effects);
            }
        }

        if (applied)
        {
            if (o.EventId is not null)
                p.LastEventId = o.EventId;
            p.UpdatedAt = now;
        }

        return effects;
    }

    /// <summary>Time-driven part: repairs "live without a live platform" and ends a session whose grace period is over.</summary>
    public static IReadOnlyList<LiveEffect> Tick(CreatorState creator, IReadOnlyList<PlatformState> platforms, IReadOnlyCollection<LivePlatform> tracked,
        LiveRules rules, DateTimeOffset now)
    {
        var effects = new List<LiveEffect>();
        if (creator.Phase == CreatorPhase.Live && !platforms.Any(p => p.Status == PlatformStatus.Live))
        {
            creator.Phase = CreatorPhase.ReconnectGrace;
            creator.GraceSince = now;
            creator.UpdatedAt = now;
            effects.Add(new(LiveEffectKind.GraceEntered, creator.CreatorKey, null, "no_live_platform"));
        }

        if (creator.Phase == CreatorPhase.ReconnectGrace && creator.GraceSince is { } since)
        {
            var deadline = since + rules.ReconnectGrace;
            // Every TRACKED platform must have confirmed "offline" after the deadline; an unknown one (never described,
            // malformed answer, provider failure) is no evidence that the creator stopped streaming. No tracked platform at
            // all proves nothing either (no vacuous end). Untracked platforms (no credentials) are not required.
            var required = platforms.Where(p => tracked.Contains(p.Platform)).ToList();
            var allConfirmedOffline = required.Count > 0 &&
                                      required.All(p => p.Status == PlatformStatus.Offline && p.StatusObservedAt is { } observed && observed >= deadline);
            if (now >= deadline && allConfirmedOffline)
            {
                EndSession(creator, since, now);
                effects.Add(new(LiveEffectKind.SessionEnded, creator.CreatorKey, null));
            }
        }

        return effects;
    }

    private static void ApplyLive(CreatorState creator, IReadOnlyList<PlatformState> platforms, PlatformState p, LiveObservation o, DateTimeOffset? previous,
        LiveRules rules, bool announcementsAllowed, DateTimeOffset now, List<LiveEffect> effects)
    {
        var wasLive = p.Status == PlatformStatus.Live;
        var firstEver = p.Status == PlatformStatus.Unknown;
        var startedAt = o.StartedAt ?? (wasLive ? p.StartedAt : null) ?? o.ObservedAt;
        // The platform restarted between two observations (offline + live again unseen): same logic as a visible flap.
        // The provider's own stream identity (Twitch stream id) proves continuity: the same stream is always the same session.
        var sameStream = o.StreamId is not null && o.StreamId == p.StreamId;
        var restartedUnseen = wasLive && !sameStream && o.StartedAt is { } st && previous is { } prev && st > prev;
        p.Status = PlatformStatus.Live;
        p.LastLiveAt = o.ObservedAt;
        p.StartedAt = startedAt;
        if (o.StreamId is not null)
            p.StreamId = o.StreamId;

        var othersLive = platforms.Any(x => x.Platform != p.Platform && x.Status == PlatformStatus.Live);
        switch (creator.Phase)
        {
            case CreatorPhase.Live when restartedUnseen && !othersLive && startedAt >= previous!.Value + rules.ReconnectGrace:
                // Went offline after `previous` and came back later than the grace allows: that was a new stream.
                EndSession(creator, previous.Value, now);
                effects.Add(new(LiveEffectKind.SessionEnded, creator.CreatorKey, p.Platform, "restarted_after_grace"));
                StartSession(creator, p, o, startedAt, firstEver: false, previous, rules, announcementsAllowed, now, effects);
                break;

            case CreatorPhase.Live:
                creator.AddSessionPlatform(p.Platform);
                if (!wasLive)
                    effects.Add(new(LiveEffectKind.PlatformJoined, creator.CreatorKey, p.Platform));
                else if (restartedUnseen)
                    effects.Add(new(LiveEffectKind.ReconnectedWithinGrace, creator.CreatorKey, p.Platform, "restarted_unseen"));
                break;

            case CreatorPhase.ReconnectGrace when sameStream || (creator.GraceSince is { } since && startedAt < since + rules.ReconnectGrace):
                creator.Phase = CreatorPhase.Live;
                creator.GraceSince = null;
                creator.AddSessionPlatform(p.Platform);
                creator.UpdatedAt = now;
                effects.Add(new(LiveEffectKind.ReconnectedWithinGrace, creator.CreatorKey, p.Platform));
                break;

            case CreatorPhase.ReconnectGrace:
                // The stream started after the grace would have ended: the old session is over, this is a new one.
                EndSession(creator, creator.GraceSince ?? o.ObservedAt, now);
                effects.Add(new(LiveEffectKind.SessionEnded, creator.CreatorKey, p.Platform, "new_stream_after_grace"));
                StartSession(creator, p, o, startedAt, firstEver, previous, rules, announcementsAllowed, now, effects);
                break;

            case CreatorPhase.Offline when sameStream && creator.SessionNumber > 0 && creator.HasPlatformInSession(p.Platform):
                // The provider says it is the very stream of the last session (it came back after the grace): reopen that
                // session — its message is edited back to live — instead of announcing the same stream a second time.
                creator.Phase = CreatorPhase.Live;
                creator.SessionEndedAt = null;
                creator.UpdatedAt = now;
                effects.Add(new(LiveEffectKind.ReconnectedWithinGrace, creator.CreatorKey, p.Platform, "same_stream"));
                break;

            default:
                StartSession(creator, p, o, startedAt, firstEver, previous, rules, announcementsAllowed, now, effects);
                break;
        }
    }

    private static void ApplyOffline(CreatorState creator, IReadOnlyList<PlatformState> platforms, PlatformState p, LiveObservation o, List<LiveEffect> effects)
    {
        if (p.Status == PlatformStatus.Unknown)
        {
            p.Status = PlatformStatus.Offline;
            effects.Add(new(LiveEffectKind.FirstObservation, creator.CreatorKey, p.Platform, "offline"));
            return;
        }

        if (p.Status != PlatformStatus.Live)
            return; // still offline: nothing to do, nothing to log

        p.Status = PlatformStatus.Offline;
        p.LastOfflineAt = o.ObservedAt;
        if (creator.Phase != CreatorPhase.Live)
            return;
        if (platforms.Any(x => x.Platform != p.Platform && x.Status == PlatformStatus.Live))
        {
            effects.Add(new(LiveEffectKind.PlatformWentOffline, creator.CreatorKey, p.Platform));
            return;
        }

        creator.Phase = CreatorPhase.ReconnectGrace;
        creator.GraceSince = o.ObservedAt;
        creator.UpdatedAt = o.ObservedAt;
        effects.Add(new(LiveEffectKind.PlatformWentOffline, creator.CreatorKey, p.Platform));
        effects.Add(new(LiveEffectKind.GraceEntered, creator.CreatorKey, p.Platform));
    }

    private static void StartSession(CreatorState creator, PlatformState p, LiveObservation o, DateTimeOffset startedAt, bool firstEver, DateTimeOffset? previous,
        LiveRules rules, bool announcementsAllowed, DateTimeOffset now, List<LiveEffect> effects)
    {
        creator.SessionNumber++;
        creator.Phase = CreatorPhase.Live;
        creator.SessionStartedAt = startedAt;
        creator.SessionDetectedAt = o.ObservedAt;
        creator.GraceSince = null;
        creator.SessionEndedAt = null;
        creator.SessionPlatforms = "";
        creator.AddSessionPlatform(p.Platform);
        creator.AnnouncementKind = null;
        creator.AnnouncementGuildId = null;
        creator.AnnouncementChannelId = null;
        creator.AnnouncementMessageId = null;
        creator.AnnouncedAt = null;
        creator.Replacements = 0;
        creator.UpdatedAt = now;

        string? reason = null;
        if (firstEver)
        {
            if (!rules.AnnounceExistingLiveOnBootstrap)
                reason = "bootstrap";
        }
        else if (previous is null || o.ObservedAt - previous.Value > rules.Continuity)
        {
            // Not watched continuously (bot down, provider outage). The last trustworthy statement before the gap said
            // "offline" at `previous`: the stream is a genuinely new one iff the provider's start time lies after it — however
            // long ago that was (no age cutoff). Without a provable start time it is a baseline.
            if (o.StartedAt is not { } started || previous is not { } lastSeen || started <= lastSeen)
                reason = "gap";
        }

        if (reason is null && !announcementsAllowed)
            reason = "delivery_off";
        creator.Announced = reason is null;
        creator.NotAnnouncedReason = reason;
        effects.Add(new(LiveEffectKind.SessionStarted, creator.CreatorKey, p.Platform, reason ?? "announce"));
    }

    private static void EndSession(CreatorState creator, DateTimeOffset endedAt, DateTimeOffset now)
    {
        creator.Phase = CreatorPhase.Offline;
        creator.SessionEndedAt = endedAt;
        creator.GraceSince = null;
        creator.UpdatedAt = now;
    }
}
