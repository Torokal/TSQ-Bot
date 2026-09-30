using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Modules.News.Persistence;

/// <summary>
/// One feed item TSQ has seen (identity: source + HLTV article id). Only what the card needs is kept: the headline (pruned
/// after News:TextRetentionDays), the canonical link and the publication time. No description, no image, no article text.
/// </summary>
public sealed class NewsArticleEntity
{
    public string Source { get; set; } = "";
    public long ArticleId { get; set; }
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset? ContentChangedAt { get; set; }

    /// <summary>Present when the feed was first read successfully (or older than the pruning watermark): never delivered.</summary>
    public bool Baseline { get; set; }

    public bool Relevant { get; set; }
    public int MatchReason { get; set; }
    public string? MatchEvidence { get; set; }
}

/// <summary>
/// "This guild got this article" — per mode (dry-run rows never count as live). The channel is recorded (edits go to the
/// same message) but is NOT part of the identity: changing the channel never re-sends an article.
/// </summary>
public sealed class NewsDeliveryEntity
{
    public ulong GuildId { get; set; }
    public string Source { get; set; } = "";
    public long ArticleId { get; set; }
    public bool DryRun { get; set; }
    public ulong ChannelId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset StagedAt { get; set; }
}

/// <summary>Feed and roster state (one row per source). Survives restarts: validators, backoff and the next poll time.</summary>
public sealed class NewsFeedStateEntity
{
    public string Key { get; set; } = "";
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public int? TtlMinutes { get; set; }
    public DateTimeOffset? BaselineAt { get; set; }
    public long PrunedBelowArticleId { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int LastOutcome { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastDetail { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? NextPollAt { get; set; }
    public int LastItemCount { get; set; }
    public int LastSkippedCount { get; set; }
    public int LastRelevantCount { get; set; }
    public int LastNewCount { get; set; }
    public DateTimeOffset? LastDeliveryStagedAt { get; set; }

    public string? RosterPlayers { get; set; }
    public DateTimeOffset? RosterVerifiedAt { get; set; }
    public string? RosterSource { get; set; }
    public DateTimeOffset? RosterLastAttemptAt { get; set; }
    public int RosterLastOutcome { get; set; }
    public string? RosterDetail { get; set; }
    public int RosterFailures { get; set; }
    public DateTimeOffset? RosterNextAt { get; set; }
}

/// <summary>The news channel of a guild. No user id is stored.</summary>
public sealed class NewsGuildConfigEntity
{
    public ulong GuildId { get; set; }
    public ulong? ChannelId { get; set; }
    public bool Paused { get; set; }

    /// <summary>Only articles first seen at or after this time can be sent live (set when the channel is configured or on resume).</summary>
    public DateTimeOffset? LiveSince { get; set; }

    public DateTimeOffset? DryRunSince { get; set; }
    public string? ChannelProblem { get; set; }
    public DateTimeOffset? ChannelProblemAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>TSQ News tables (additive; no other module's table is touched).</summary>
public sealed class NewsModelContributor : IModelContributor
{
    public string Name => "news";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NewsArticleEntity>(e =>
        {
            e.ToTable("news_article");
            e.HasKey(x => new { x.Source, x.ArticleId });
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Url).HasMaxLength(300);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.MatchEvidence).HasMaxLength(200);
            e.HasIndex(x => x.FirstSeenAt);
        });
        modelBuilder.Entity<NewsDeliveryEntity>(e =>
        {
            e.ToTable("news_delivery");
            e.HasKey(x => new { x.GuildId, x.Source, x.ArticleId, x.DryRun });
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Kind).HasMaxLength(32);
        });
        modelBuilder.Entity<NewsFeedStateEntity>(e =>
        {
            e.ToTable("news_feed_state");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.ETag).HasMaxLength(200);
            e.Property(x => x.LastModified).HasMaxLength(100);
            e.Property(x => x.LastDetail).HasMaxLength(300);
            e.Property(x => x.RosterPlayers).HasMaxLength(500);
            e.Property(x => x.RosterSource).HasMaxLength(32);
            e.Property(x => x.RosterDetail).HasMaxLength(300);
        });
        modelBuilder.Entity<NewsGuildConfigEntity>(e =>
        {
            e.ToTable("news_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.ChannelProblem).HasMaxLength(64);
        });
    }
}
