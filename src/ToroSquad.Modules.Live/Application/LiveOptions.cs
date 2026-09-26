using ToroSquad.Core;
using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Modules.Live.Application;

/// <summary>
/// Section "Live" (TSQ Live). Off unless <see cref="Enabled"/>; the announcement channel is never guessed. Creators are
/// configuration (appsettings.json holds the V1 set; environment variables may override a login), never user input.
/// Provider credentials live in "Live:Twitch" / "Live:Kick" and are secrets (environment variables / user-secrets only).
/// </summary>
public sealed class LiveOptions
{
    public const string Section = "Live";

    public bool Enabled { get; set; }

    /// <summary>Discord channel for live announcements (required when enabled).</summary>
    public ulong DiscordChannelId { get; set; }

    /// <summary>Guild of <see cref="DiscordChannelId"/>. 0 = the single guild of Discord:AllowedGuildIds.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Official-API reconciliation per provider (one batched request each). Twitch/Kick limits: docs/live/TSQ_LIVE.md.</summary>
    public int ReconciliationIntervalSeconds { get; set; } = 30;

    /// <summary>A session survives "every platform offline" this long (OBS reconnect, API flap) — no new announcement.</summary>
    public int ReconnectGraceSeconds { get; set; } = 120;

    /// <summary>
    /// False (default): a stream that is already live when a channel is observed for the very first time is recorded as
    /// baseline and never announced (deploying mid-stream does not ping @everyone).
    /// </summary>
    public bool AnnounceExistingLiveOnBootstrap { get; set; }

    /// <summary>After a gap in observation (restart, outage), a stream is still announced when it started at most this long ago.</summary>
    public int LateAnnounceMinutes { get; set; } = 10;

    /// <summary>A first announcement not delivered within this time after detection is dropped (a late @everyone is worse than none).</summary>
    public int AnnouncementMaxDelayMinutes { get; set; } = 15;

    public Dictionary<string, CreatorSection> Creators { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class CreatorSection
    {
        public string? DisplayName { get; set; }
        public string? Twitch { get; set; }
        public string? Kick { get; set; }
    }

    public TimeSpan ReconciliationInterval => TimeSpan.FromSeconds(ReconciliationIntervalSeconds);
    public TimeSpan ReconnectGrace => TimeSpan.FromSeconds(ReconnectGraceSeconds);

    /// <summary>Observations further apart than this are not continuous: at least four reconciliations, at least three minutes.</summary>
    public TimeSpan Continuity => TimeSpan.FromSeconds(Math.Max(180, ReconciliationIntervalSeconds * 4));

    public LiveRules Rules => new(ReconnectGrace, Continuity, TimeSpan.FromMinutes(LateAnnounceMinutes), AnnounceExistingLiveOnBootstrap);

    /// <summary>The configured guild, else the single allowed guild; null when neither is known.</summary>
    public GuildId? ResolveGuild(DeploymentPolicy deployment) =>
        GuildId != 0 ? new GuildId(GuildId) : deployment.AllowedGuildIds is { Count: 1 } allowed ? new GuildId(allowed.First()) : null;

    public IReadOnlyList<TrackedCreator> TrackedCreators() =>
        Creators
            .Select(kv => (Key: kv.Key.Trim().ToLowerInvariant(), Section: kv.Value))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new TrackedCreator(x.Key,
                string.IsNullOrWhiteSpace(x.Section.DisplayName) ? x.Key.ToUpperInvariant() : x.Section.DisplayName.Trim(),
                Channels(x.Section)))
            .Where(c => c.Channels.Count > 0)
            .ToList();

    private static List<TrackedChannel> Channels(CreatorSection s)
    {
        var list = new List<TrackedChannel>();
        if (Login(s.Twitch) is { } twitch)
            list.Add(new TrackedChannel(LivePlatform.Twitch, twitch));
        if (Login(s.Kick) is { } kick)
            list.Add(new TrackedChannel(LivePlatform.Kick, kick));
        return list;
    }

    private static string? Login(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>Configuration problems (never secret values). Only checked in depth when the module is enabled.</summary>
    public IReadOnlyList<string> Validate(ulong[] allowedGuildIds)
    {
        var errors = new List<string>();
        void Range(string name, int value, int min, int max)
        {
            if (value < min || value > max)
                errors.Add($"Live:{name} must be {min}..{max}");
        }

        Range(nameof(ReconciliationIntervalSeconds), ReconciliationIntervalSeconds, 15, 300);
        Range(nameof(ReconnectGraceSeconds), ReconnectGraceSeconds, 30, 1800);
        Range(nameof(LateAnnounceMinutes), LateAnnounceMinutes, 0, 60);
        Range(nameof(AnnouncementMaxDelayMinutes), AnnouncementMaxDelayMinutes, 2, 120);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rawKey, section) in Creators)
        {
            var key = rawKey.Trim().ToLowerInvariant();
            if (key.Length is 0 or > 32 || !key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                errors.Add($"Live:Creators: key '{rawKey}' must be 1-32 chars of [a-z0-9_-]");
            if (!keys.Add(key))
                errors.Add($"Live:Creators: duplicate creator '{key}'");
            if (Login(section.Twitch) is { } t && !LivePlatforms.IsValidLogin(LivePlatform.Twitch, t))
                errors.Add($"Live:Creators:{rawKey}:Twitch is not a valid Twitch login");
            if (Login(section.Kick) is { } k && !LivePlatforms.IsValidLogin(LivePlatform.Kick, k))
                errors.Add($"Live:Creators:{rawKey}:Kick is not a valid Kick slug");
            if (section.DisplayName is { Length: > 32 })
                errors.Add($"Live:Creators:{rawKey}:DisplayName must be at most 32 characters");
        }

        foreach (var platform in LivePlatforms.All)
        {
            var owners = TrackedCreators().SelectMany(c => c.Channels.Where(ch => ch.Platform == platform).Select(ch => (c.Key, ch.Login)))
                .GroupBy(x => x.Login, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (owners.Count > 0)
                errors.Add($"Live:Creators: {platform.Name()} channel(s) {string.Join(", ", owners)} belong to more than one creator");
        }

        if (!Enabled)
            return errors;
        if (DiscordChannelId == 0)
            errors.Add("Live:DiscordChannelId must be set when Live:Enabled=true (the announcement channel is never guessed)");
        if (GuildId == 0 && allowedGuildIds.Length != 1)
            errors.Add("Live:GuildId must be set when Discord:AllowedGuildIds does not name exactly one guild");
        if (GuildId != 0 && allowedGuildIds.Length > 0 && !allowedGuildIds.Contains(GuildId))
            errors.Add("Live:GuildId is outside Discord:AllowedGuildIds");
        if (TrackedCreators().Count == 0)
            errors.Add("Live:Creators must list at least one creator with a Twitch or Kick channel");
        return errors;
    }
}
