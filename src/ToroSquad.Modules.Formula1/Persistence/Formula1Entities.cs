using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Modules.Formula1.Persistence;

/// <summary>Per-guild Formula 1 configuration. Absent row = not configured.</summary>
public sealed class Formula1GuildConfigEntity
{
    public ulong GuildId { get; set; }
    public ulong? ChannelId { get; set; }
    public bool Paused { get; set; }

    /// <summary>Nothing that happened before this instant is ever announced (set on first setup, enable, resume, re-enable).</summary>
    public DateTimeOffset WatermarkUtc { get; set; }

    public bool SpoilerMode { get; set; }

    /// <summary>Optional notification role (never @everyone). Pings only on the first send, never on edits.</summary>
    public ulong? PingRoleId { get; set; }
    public bool PingOnStarts { get; set; } = true;
    public bool PingOnResults { get; set; }

    public bool NotifyPracticeStart { get; set; } = true;
    public bool NotifyPracticeResults { get; set; } = true;
    public bool NotifySprintStart { get; set; } = true;
    public bool NotifySprintResults { get; set; } = true;
    public bool NotifyRaceStart { get; set; } = true;
    public bool NotifyRaceResults { get; set; } = true;

    /// <summary>Championship standings section on sprint/race result cards.</summary>
    public bool NotifyStandings { get; set; } = true;

    // Modelled now so enabling them later needs no schema change; default off.
    public bool NotifyQualifyingStart { get; set; }
    public bool NotifyQualifyingResults { get; set; }
    public bool NotifySprintQualifyingStart { get; set; }
    public bool NotifySprintQualifyingResults { get; set; }

    public string? ChannelProblem { get; set; }
    public DateTimeOffset? ChannelProblemAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ulong UpdatedBy { get; set; }
}

/// <summary>
/// Shared (not per guild) lifecycle state of one real-world session — enough to rebuild everything after a restart.
/// Every *ObservedAt value is recorded once, from provider-stated events only (never from the clock).
/// </summary>
public sealed class F1SessionSnapshotEntity
{
    public string SessionKey { get; set; } = "";
    public string MeetingKey { get; set; } = "";
    public int Season { get; set; }
    public int Round { get; set; }
    public int SessionType { get; set; }

    public string MeetingName { get; set; } = "";
    public string CircuitName { get; set; } = "";
    public string? Country { get; set; }
    public string? Location { get; set; }

    public DateTimeOffset ScheduledStartUtc { get; set; }
    public DateTimeOffset? ScheduledEndUtc { get; set; }

    public int State { get; set; }

    /// <summary>Provider timestamp of the FIRST logical start. A resume after a suspension never changes it.</summary>
    public DateTimeOffset? StartedObservedAt { get; set; }

    /// <summary>When the first start was recorded locally (diagnostics: how late the start signal arrived).</summary>
    public DateTimeOffset? StartedRecordedAt { get; set; }
    public DateTimeOffset? SuspendedObservedAt { get; set; }
    public int ResumeCount { get; set; }
    public DateTimeOffset? FinishedObservedAt { get; set; }

    /// <summary>When a valid final classification first became available.</summary>
    public DateTimeOffset? FinalisedObservedAt { get; set; }
    public DateTimeOffset? CancelledObservedAt { get; set; }

    /// <summary>Newest applied provider event (duplicates and out-of-order replays at or before it are ignored).</summary>
    public DateTimeOffset? LastLifecycleEventAt { get; set; }

    // Provider id mappings (providers never share ids; mapped conservatively, see F1SessionMatcher).
    public string? LifecycleProvider { get; set; }
    public string? LifecycleProviderRef { get; set; }
    public string? ResultsProvider { get; set; }
    public string? ResultsProviderRef { get; set; }

    /// <summary>Already over when the module first saw it (bootstrap): never announced.</summary>
    public bool IsBaseline { get; set; }

    // Bounded results polling (persisted so a restart neither resets nor multiplies the retries).
    public DateTimeOffset? ResultNextAttemptAt { get; set; }
    public int ResultAttempts { get; set; }
    public string? ResultLastDetail { get; set; }

    // Championship standings workflow (sprint and race only).
    public string? StandingsBaselineDriversHash { get; set; }
    public string? StandingsBaselineConstructorsHash { get; set; }
    public DateTimeOffset? StandingsWatchUntil { get; set; }
    public DateTimeOffset? StandingsNextCheckAt { get; set; }
    public int StandingsChecks { get; set; }
    public long? StandingsDriversSnapshotId { get; set; }
    public long? StandingsConstructorsSnapshotId { get; set; }
    public DateTimeOffset? StandingsAttachedAt { get; set; }
    public bool StandingsWindowClosed { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Latest canonical classification of a session (normalized JSON + deterministic hash).</summary>
public sealed class F1ResultSnapshotEntity
{
    public string SessionKey { get; set; } = "";
    public string Provider { get; set; } = "";
    public string CanonicalHash { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset FirstAvailableAt { get; set; }
    public DateTimeOffset LastChangedAt { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public int Corrections { get; set; }
}

/// <summary>One distinct standings table (a new row only when the canonical hash changes).</summary>
public sealed class F1StandingsSnapshotEntity
{
    public long Id { get; set; }
    public int Kind { get; set; }
    public int Season { get; set; }
    public int? Round { get; set; }
    public string Provider { get; set; } = "";
    public string CanonicalHash { get; set; } = "";
    public string PayloadJson { get; set; } = "";

    /// <summary>When this table was first fetched.</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>Last time the provider still returned exactly this table.</summary>
    public DateTimeOffset LastConfirmedAt { get; set; }
}

/// <summary>Provider health + cached datasets (season schedule) so commands and /bot status survive restarts.</summary>
public sealed class F1ProviderStateEntity
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

public sealed class Formula1ModelContributor : IModelContributor
{
    public string Name => "formula1";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Formula1GuildConfigEntity>(e =>
        {
            e.ToTable("f1_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.GuildId).ValueGeneratedNever();
            e.Property(x => x.ChannelProblem).HasMaxLength(200);
        });
        modelBuilder.Entity<F1SessionSnapshotEntity>(e =>
        {
            e.ToTable("f1_session_snapshot");
            e.HasKey(x => x.SessionKey);
            e.Property(x => x.SessionKey).HasMaxLength(32);
            e.Property(x => x.MeetingKey).HasMaxLength(16);
            e.Property(x => x.MeetingName).HasMaxLength(200);
            e.Property(x => x.CircuitName).HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.Location).HasMaxLength(100);
            e.Property(x => x.LifecycleProvider).HasMaxLength(32);
            e.Property(x => x.LifecycleProviderRef).HasMaxLength(64);
            e.Property(x => x.ResultsProvider).HasMaxLength(32);
            e.Property(x => x.ResultsProviderRef).HasMaxLength(64);
            e.Property(x => x.ResultLastDetail).HasMaxLength(200);
            e.Property(x => x.StandingsBaselineDriversHash).HasMaxLength(64);
            e.Property(x => x.StandingsBaselineConstructorsHash).HasMaxLength(64);
            e.HasIndex(x => x.ScheduledStartUtc);
            e.HasIndex(x => new { x.Season, x.Round });
            e.HasIndex(x => new { x.LifecycleProvider, x.LifecycleProviderRef });
        });
        modelBuilder.Entity<F1ResultSnapshotEntity>(e =>
        {
            e.ToTable("f1_result_snapshot");
            e.HasKey(x => x.SessionKey);
            e.Property(x => x.SessionKey).HasMaxLength(32);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.CanonicalHash).HasMaxLength(64);
        });
        modelBuilder.Entity<F1StandingsSnapshotEntity>(e =>
        {
            e.ToTable("f1_standings_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.CanonicalHash).HasMaxLength(64);
            e.HasIndex(x => new { x.Kind, x.Season, x.Id });
        });
        modelBuilder.Entity<F1ProviderStateEntity>(e =>
        {
            e.ToTable("f1_provider_state");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.LastDetail).HasMaxLength(500);
        });
    }
}
