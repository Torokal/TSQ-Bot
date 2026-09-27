using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Modules.Volleyball.Persistence;

/// <summary>Per-guild volleyball configuration. Absent row = not configured.</summary>
public sealed class VolleyballGuildConfigEntity
{
    public ulong GuildId { get; set; }
    public ulong? ChannelId { get; set; }
    public bool Paused { get; set; }

    /// <summary>Nothing that happened before this instant is ever announced (set on setup, enable, resume, channel change, re-enable of a type).</summary>
    public DateTimeOffset WatermarkUtc { get; set; }

    /// <summary>Optional notification role (never @everyone). Pings only on the first send, never on edits.</summary>
    public ulong? PingRoleId { get; set; }
    public bool PingOnReminder { get; set; } = true;
    public bool PingOnFinal { get; set; }

    public bool NotifyReminder { get; set; } = true;
    public bool NotifyStarted { get; set; } = true;
    public bool NotifySets { get; set; } = true;
    public bool NotifyFinal { get; set; } = true;
    public bool NotifyPostponedCancelled { get; set; } = true;

    public string? ChannelProblem { get; set; }
    public DateTimeOffset? ChannelProblemAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ulong UpdatedBy { get; set; }
}

/// <summary>
/// Shared (not per guild) state of one match of the followed team — enough to rebuild every decision after a restart.
/// Transition times are observation times of provider data (never the clock alone).
/// </summary>
public sealed class VbMatchSnapshotEntity
{
    public string MatchKey { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderMatchId { get; set; } = "";

    public string? CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public int? Season { get; set; }
    public string? Stage { get; set; }
    public string? Round { get; set; }
    public DateTimeOffset? StartTimeUtc { get; set; }

    /// <summary>0 = home, 1 = away (<see cref="Domain.FollowedSide"/>).</summary>
    public int FollowedSide { get; set; }
    public string HomeName { get; set; } = "";
    public string? HomeCode { get; set; }
    public string AwayName { get; set; } = "";
    public string? AwayCode { get; set; }
    public string? HomeLogoUrl { get; set; }
    public string? AwayLogoUrl { get; set; }
    public string? Venue { get; set; }
    public string? City { get; set; }

    /// <summary>JSON array of verified broadcaster names for THIS match (never carried over from another match).</summary>
    public string? BroadcastsJson { get; set; }

    // Progress (see Domain.MatchProgress): flags only move forward.
    public int Status { get; set; }
    public int HomeSets { get; set; }
    public int AwaySets { get; set; }
    public string SetsJson { get; set; } = "[]";
    public bool Started { get; set; }
    public bool Finished { get; set; }
    public bool Postponed { get; set; }
    public bool Cancelled { get; set; }

    // In-progress set (display only, never a notification).
    public int? CurrentSet { get; set; }
    public int? CurrentSetHomePoints { get; set; }
    public int? CurrentSetAwayPoints { get; set; }

    /// <summary>JSON array of recorded transitions (kind, set, observed-at, announce) — the planner's only input for live cards.</summary>
    public string EventsJson { get; set; } = "[]";

    /// <summary>First seen already started/ended: the state at that moment was recorded as baseline (never announced).</summary>
    public bool IsBaseline { get; set; }

    /// <summary>An observation not confirmed yet (new transition, or a correction seen <see cref="PendingCount"/> times in a row).</summary>
    public string? PendingJson { get; set; }
    public int PendingCount { get; set; }

    /// <summary>Last time the match appeared in a successful fixture listing (a vanished match gets no reminder).</summary>
    public DateTimeOffset? LastListedAt { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Time of the last trustworthy observation that was applied (continuity check after restarts/outages).</summary>
    public DateTimeOffset LastObservedAt { get; set; }
    public DateTimeOffset? LastProviderUpdateAt { get; set; }
    public string? LastProblem { get; set; }
    public DateTimeOffset? LastProblemAt { get; set; }
    public int Problems { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Provider health per dataset (fixtures, live) so doctor and /bot status survive restarts.</summary>
public sealed class VbProviderStateEntity
{
    public string Key { get; set; } = "";
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int LastOutcome { get; set; }
    public string? LastDetail { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? DataJson { get; set; }
}

public sealed class VolleyballModelContributor : IModelContributor
{
    public string Name => "volleyball";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VolleyballGuildConfigEntity>(e =>
        {
            e.ToTable("vb_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.GuildId).ValueGeneratedNever();
            e.Property(x => x.ChannelProblem).HasMaxLength(200);
        });
        modelBuilder.Entity<VbMatchSnapshotEntity>(e =>
        {
            e.ToTable("vb_match_snapshot");
            e.HasKey(x => x.MatchKey);
            e.Property(x => x.MatchKey).HasMaxLength(96);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderMatchId).HasMaxLength(64);
            e.Property(x => x.CompetitionId).HasMaxLength(64);
            e.Property(x => x.CompetitionName).HasMaxLength(200);
            e.Property(x => x.Stage).HasMaxLength(100);
            e.Property(x => x.Round).HasMaxLength(100);
            e.Property(x => x.HomeName).HasMaxLength(100);
            e.Property(x => x.HomeCode).HasMaxLength(8);
            e.Property(x => x.AwayName).HasMaxLength(100);
            e.Property(x => x.AwayCode).HasMaxLength(8);
            e.Property(x => x.HomeLogoUrl).HasMaxLength(512);
            e.Property(x => x.AwayLogoUrl).HasMaxLength(512);
            e.Property(x => x.Venue).HasMaxLength(200);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.BroadcastsJson).HasMaxLength(1000);
            e.Property(x => x.SetsJson).HasMaxLength(1000);
            e.Property(x => x.EventsJson).HasMaxLength(4000);
            e.Property(x => x.PendingJson).HasMaxLength(1000);
            e.Property(x => x.LastProblem).HasMaxLength(300);
            e.HasIndex(x => x.StartTimeUtc);
            e.HasIndex(x => new { x.Provider, x.ProviderMatchId }).IsUnique();
        });
        modelBuilder.Entity<VbProviderStateEntity>(e =>
        {
            e.ToTable("vb_provider_state");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.LastDetail).HasMaxLength(500);
        });
    }
}
