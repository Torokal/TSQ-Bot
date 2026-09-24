using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Modules.Esports.Persistence;

/// <summary>Per-guild esports configuration. Absent row = not configured.</summary>
public sealed class EsportsGuildConfigEntity
{
    public ulong GuildId { get; set; }
    public ulong? ChannelId { get; set; }
    public bool NotifyReminders { get; set; } = true;
    public int ReminderLeadMinutes { get; set; } = 15;
    public bool NotifyResults { get; set; } = true;
    public bool SpoilerMode { get; set; }
    public bool Paused { get; set; }

    /// <summary>Nothing that became due before this instant is ever sent (set on enable/resume/first setup).</summary>
    public DateTimeOffset WatermarkUtc { get; set; }

    public int? VrsTopN { get; set; }
    public string? ChannelProblem { get; set; }
    public DateTimeOffset? ChannelProblemAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ulong UpdatedBy { get; set; }
}

public sealed class EsportsFilterEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public int Dimension { get; set; }
    public string Value { get; set; } = "";
    public string? Label { get; set; }
}

public sealed class TeamFollowEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string TeamKey { get; set; } = "";
    public string TeamName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UserPreferenceEntity
{
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public bool HideResults { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A role used for notifications. Mapping a role as a ping target and letting members self-assign it are separate
/// decisions: <see cref="SelfService"/> requires its own approval + safety evaluation.
/// </summary>
public sealed class RoleMappingEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong RoleId { get; set; }

    /// <summary>Team key, or empty for "all matches that pass the server filters".</summary>
    public string TeamKey { get; set; } = "";
    public string? TeamName { get; set; }
    public bool PingOnReminder { get; set; } = true;
    public bool PingOnResult { get; set; }
    public bool SelfService { get; set; }
    public ulong? SelfServiceApprovedBy { get; set; }
    public DateTimeOffset? SelfServiceApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum RoleGrantState
{
    PendingAdd = 0,
    Active = 1,
    PendingRemove = 2,
    Failed = 3,
}

/// <summary>
/// Tracks whether the BOT gave a member a role. <see cref="HadRoleBefore"/> = the member already had it for another
/// reason; such roles are never removed by the bot.
/// </summary>
public sealed class RoleGrantEntity
{
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public ulong RoleId { get; set; }
    public RoleGrantState State { get; set; }
    public bool GrantedByBot { get; set; }
    public bool HadRoleBefore { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Last known state of a public match (shared by all guilds — fetched once, filtered per guild).</summary>
public sealed class MatchSnapshotEntity
{
    public string MatchKey { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public int Status { get; set; }
    public DateTimeOffset? ScheduledStartUtc { get; set; }
    public DateTimeOffset? PreviousStartUtc { get; set; }
    public DateTimeOffset? StartChangedAt { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When the match content last changed (drives the "data as of" line, so unchanged data never causes edits).</summary>
    public DateTimeOffset LastChangedAt { get; set; }
    public DateTimeOffset? FinishedObservedAt { get; set; }

    /// <summary>Already finished when the bot first saw it (initial bootstrap) — never announced.</summary>
    public bool IsBaseline { get; set; }

    // Lifecycle transitions, each recorded once when first OBSERVED between two known provider states (never inferred
    // from the clock, never on first sight, never from Unknown). Shared by all guilds; per-guild delivery is idempotent.

    /// <summary>Scheduled/postponed → running (provider-stated).</summary>
    public DateTimeOffset? StartedObservedAt { get; set; }

    /// <summary>Scheduled → postponed (new date unknown).</summary>
    public DateTimeOffset? PostponedObservedAt { get; set; }

    /// <summary>Any known state → cancelled (not a forfeit).</summary>
    public DateTimeOffset? CancelledObservedAt { get; set; }

    /// <summary>Latest meaningful start-time change of a scheduled match (or a postponed match getting a date).</summary>
    public DateTimeOffset? RescheduledObservedAt { get; set; }

    public DateTimeOffset? RescheduledToUtc { get; set; }
}

/// <summary>Provider health + small cached datasets (events, rankings) so /bot status and commands survive restarts.</summary>
public sealed class ProviderStateEntity
{
    public string Key { get; set; } = "";
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int LastOutcome { get; set; }
    public string? LastDetail { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? NextAllowedAt { get; set; }
    public string? DataJson { get; set; }
}

public sealed class KnownTeamEntity
{
    public string TeamKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ShortName { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class EsportsModelContributor : IModelContributor
{
    public string Name => "esports";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EsportsGuildConfigEntity>(e =>
        {
            e.ToTable("esports_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.GuildId).ValueGeneratedNever();
            e.Property(x => x.ChannelProblem).HasMaxLength(200);
        });
        modelBuilder.Entity<EsportsFilterEntity>(e =>
        {
            e.ToTable("esports_filter");
            e.HasKey(x => x.Id);
            e.Property(x => x.Value).HasMaxLength(200);
            e.Property(x => x.Label).HasMaxLength(200);
            e.HasIndex(x => new { x.GuildId, x.Dimension, x.Value }).IsUnique();
        });
        modelBuilder.Entity<TeamFollowEntity>(e =>
        {
            e.ToTable("esports_team_follow");
            e.HasKey(x => x.Id);
            e.Property(x => x.TeamKey).HasMaxLength(200);
            e.Property(x => x.TeamName).HasMaxLength(200);
            e.HasIndex(x => new { x.GuildId, x.UserId, x.TeamKey }).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.TeamKey });
        });
        modelBuilder.Entity<UserPreferenceEntity>(e =>
        {
            e.ToTable("esports_user_pref");
            e.HasKey(x => new { x.GuildId, x.UserId });
        });
        modelBuilder.Entity<RoleMappingEntity>(e =>
        {
            e.ToTable("esports_role_mapping");
            e.HasKey(x => x.Id);
            e.Property(x => x.TeamKey).HasMaxLength(200);
            e.Property(x => x.TeamName).HasMaxLength(200);
            e.HasIndex(x => new { x.GuildId, x.RoleId, x.TeamKey }).IsUnique();
        });
        modelBuilder.Entity<RoleGrantEntity>(e =>
        {
            e.ToTable("esports_role_grant");
            e.HasKey(x => new { x.GuildId, x.UserId, x.RoleId });
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.LastError).HasMaxLength(200);
        });
        modelBuilder.Entity<MatchSnapshotEntity>(e =>
        {
            e.ToTable("esports_match_snapshot");
            e.HasKey(x => x.MatchKey);
            e.Property(x => x.MatchKey).HasMaxLength(200);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.HasIndex(x => x.ScheduledStartUtc);
        });
        modelBuilder.Entity<ProviderStateEntity>(e =>
        {
            e.ToTable("esports_provider_state");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.LastDetail).HasMaxLength(500);
        });
        modelBuilder.Entity<KnownTeamEntity>(e =>
        {
            e.ToTable("esports_known_team");
            e.HasKey(x => x.TeamKey);
            e.Property(x => x.TeamKey).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.ShortName).HasMaxLength(100);
        });
    }
}
