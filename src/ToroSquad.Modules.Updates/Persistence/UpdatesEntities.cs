using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Modules.Updates.Persistence;

/// <summary>
/// Poll state of one provider + game (never per guild): baseline, backoff and the next poll time survive restarts.
/// </summary>
public sealed class UpdatesSourceStateEntity
{
    public string Provider { get; set; } = "";
    public string GameKey { get; set; } = "";
    public string ProviderGameId { get; set; } = "";

    /// <summary>When the first valid, non-empty answer was stored. Everything in that answer is baseline (never posted).</summary>
    public DateTimeOffset? BaselineAt { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int LastOutcome { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastDetail { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? NextPollAt { get; set; }

    /// <summary>The cache lifetime the source declared with its last answer (seconds), when it declared one.</summary>
    public int? SourceCacheSeconds { get; set; }

    public int LastItemCount { get; set; }
    public int LastSkippedCount { get; set; }
    public int LastNewCount { get; set; }
    public int LastUpdateCount { get; set; }
    public int LastAmbiguousCount { get; set; }

    /// <summary>When a post classified as an update was last seen for the first time (after the baseline).</summary>
    public DateTimeOffset? LastDiscoveredAt { get; set; }

    public DateTimeOffset? LastDeliveryStagedAt { get; set; }

    /// <summary>Ids published at or before this time were pruned; a post that old is baseline if it ever shows up again.</summary>
    public DateTimeOffset? PrunedThroughPublishedAt { get; set; }
}

/// <summary>
/// One provider post TSQ has seen (identity: provider + game + the provider's post id — never the title). Only what a card
/// needs is kept: title (pruned after Updates:TextRetentionDays), canonical link, publication time, and the classification
/// with its reason. The post text is never stored; <see cref="ContentHash"/> only tells whether it changed.
/// </summary>
public sealed class UpdatesItemEntity
{
    public string Provider { get; set; } = "";
    public string GameKey { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset? ContentChangedAt { get; set; }

    /// <summary>Present in the first valid answer (or older than the pruning watermark): never posted.</summary>
    public bool Baseline { get; set; }

    public int Classification { get; set; }
    public string ClassificationReason { get; set; } = "";
    public DateTimeOffset? ClassificationChangedAt { get; set; }
}

/// <summary>The one Updates channel of a guild and its delivery window. No user id is stored.</summary>
public sealed class UpdatesGuildConfigEntity
{
    public ulong GuildId { get; set; }
    public ulong? ChannelId { get; set; }
    public bool Paused { get; set; }

    /// <summary>Only updates first seen and published at or after this time can be posted live.</summary>
    public DateTimeOffset? LiveSince { get; set; }

    public DateTimeOffset? DryRunSince { get; set; }

    /// <summary>
    /// The mode this guild was last planned (or started) in. When the running mode differs, the window of the running mode
    /// starts now — so nothing seen under DryRun or while Off is ever posted live after a mode change.
    /// </summary>
    public int? PlannedMode { get; set; }

    public string? ChannelProblem { get; set; }
    public DateTimeOffset? ChannelProblemAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>"This guild follows this game." Off until an admin enables it; enabling starts its own window (no backlog).</summary>
public sealed class UpdatesSubscriptionEntity
{
    public ulong GuildId { get; set; }
    public string GameKey { get; set; } = "";
    public bool Enabled { get; set; }

    /// <summary>Only updates first seen and published at or after the last enabling are posted for this game.</summary>
    public DateTimeOffset? EnabledAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// "This guild got this post" — per kind (live and dry-run never count for each other). The channel is recorded (a correction
/// edits the same message) but is NOT part of the identity: changing the channel never posts an update again.
/// </summary>
public sealed class UpdatesDeliveryEntity
{
    public ulong GuildId { get; set; }
    public string Provider { get; set; } = "";
    public string GameKey { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Kind { get; set; } = "";
    public ulong ChannelId { get; set; }
    public DateTimeOffset StagedAt { get; set; }
}

/// <summary>TSQ Bot Updates tables (additive; no other module's table is touched).</summary>
public sealed class UpdatesModelContributor : IModelContributor
{
    public string Name => "updates";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UpdatesSourceStateEntity>(e =>
        {
            e.ToTable("updates_source_state");
            e.HasKey(x => new { x.Provider, x.GameKey });
            e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.GameKey).HasMaxLength(16);
            e.Property(x => x.ProviderGameId).HasMaxLength(32);
            e.Property(x => x.LastDetail).HasMaxLength(300);
        });
        modelBuilder.Entity<UpdatesItemEntity>(e =>
        {
            e.ToTable("updates_item");
            e.HasKey(x => new { x.Provider, x.GameKey, x.ExternalId });
            e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.GameKey).HasMaxLength(16);
            e.Property(x => x.ExternalId).HasMaxLength(64);
            e.Property(x => x.Url).HasMaxLength(300);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.ClassificationReason).HasMaxLength(48);
            e.HasIndex(x => x.FirstSeenAt);
        });
        modelBuilder.Entity<UpdatesGuildConfigEntity>(e =>
        {
            e.ToTable("updates_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.ChannelProblem).HasMaxLength(64);
        });
        modelBuilder.Entity<UpdatesSubscriptionEntity>(e =>
        {
            e.ToTable("updates_subscription");
            e.HasKey(x => new { x.GuildId, x.GameKey });
            e.Property(x => x.GameKey).HasMaxLength(16);
        });
        modelBuilder.Entity<UpdatesDeliveryEntity>(e =>
        {
            e.ToTable("updates_delivery");
            e.HasKey(x => new { x.GuildId, x.Provider, x.GameKey, x.ExternalId, x.Kind });
            e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.GameKey).HasMaxLength(16);
            e.Property(x => x.ExternalId).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(32);
        });
    }
}
