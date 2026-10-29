using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Letterboxd.Domain;

namespace ToroSquad.Modules.Letterboxd.Persistence;

/// <summary>
/// One Letterboxd entry (diary entry or review) TSQ has seen. Identity: followed account + viewing id — never the film or its
/// title. Only what the card needs is kept: film title/year, this entry's rating, rewatch flag, watched date, publication
/// time, a bounded review excerpt (dropped after Letterboxd:TextRetentionDays), the spoiler flag, link and poster address.
/// No raw XML/HTML, no full review, no review history.
/// </summary>
public sealed class LetterboxdEntryEntity
{
    public string Account { get; set; } = "";
    public long ViewingId { get; set; }
    public int GuidKind { get; set; }
    public string FilmSlug { get; set; } = "";
    public long? TmdbMovieId { get; set; }
    public long? TmdbTvId { get; set; }
    public string Url { get; set; } = "";
    public string FilmTitle { get; set; } = "";
    public int? FilmYear { get; set; }
    public int? RatingHalves { get; set; }
    public bool? Rewatch { get; set; }
    public DateOnly? WatchedDate { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? ReviewExcerpt { get; set; }
    public bool ReviewTruncated { get; set; }
    public bool? Spoilers { get; set; }
    public string? PosterUrl { get; set; }

    /// <summary>The entry had review text when last seen (kept after the excerpt itself is pruned).</summary>
    public bool HadReview { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string ContentHash { get; set; } = "";
    public string MemberHash { get; set; } = "";
    public DateTimeOffset? ContentChangedAt { get; set; }

    /// <summary>
    /// History: present on the first read, or dated before the mode became active when first seen in its first read.
    /// Never sent as is (a later member action can still make it postable).
    /// </summary>
    public bool Baseline { get; set; }

    /// <summary>
    /// When the entry became worth a card: first seen after the baseline with a rating, rewatch or review, or a later change
    /// of what the member controls. Null = never sent. Observation time, not the member's action time.
    /// </summary>
    public DateTimeOffset? EligibleAt { get; set; }

    public EntryView View() => new(Url, FilmTitle, FilmYear, RatingHalves, Rewatch, WatchedDate, PublishedAt, ReviewExcerpt, ReviewTruncated, Spoilers, PosterUrl);
}

/// <summary>
/// "This guild got a card for this entry" — per mode (dry-run records never count as live). The channel is recorded (edits go
/// to the same message) but is NOT part of the identity: changing the channel never re-sends an entry.
/// </summary>
public sealed class LetterboxdDeliveryEntity
{
    public ulong GuildId { get; set; }
    public string Account { get; set; } = "";
    public long ViewingId { get; set; }
    public bool DryRun { get; set; }
    public ulong ChannelId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset StagedAt { get; set; }
}

/// <summary>Feed state of one followed account. Survives restarts: validators, backoff, baseline, window and coverage.</summary>
public sealed class LetterboxdSourceStateEntity
{
    public string Key { get; set; } = "";
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public int? TtlMinutes { get; set; }
    public DateTimeOffset? BaselineAt { get; set; }

    /// <summary>The effective mode the host last started with (recorded before any request, also Off).</summary>
    public int? LastMode { get; set; }

    /// <summary>
    /// When <see cref="LastMode"/> became active, persisted BEFORE the first request in that mode. Entries the feed dates at
    /// or after it are new even if the first successful read comes later; older ones are history of the previous mode.
    /// </summary>
    public DateTimeOffset? ModeSince { get; set; }

    /// <summary>Feed items of the last read whose viewing id contradicted the stored entry or another item (left unchanged).</summary>
    public int LastConflictCount { get; set; }
    public long PrunedBelowViewingId { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int LastOutcome { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastDetail { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? NextPollAt { get; set; }
    public int LastItemCount { get; set; }
    public int LastSkippedCount { get; set; }
    public int LastNewCount { get; set; }

    /// <summary>The largest number of items one answer carried (the feed window as observed; 50 on 2026-10-29).</summary>
    public int MaxItemCount { get; set; }

    public DateTimeOffset? OldestItemPublishedAt { get; set; }

    /// <summary>A full window without any known entry: entries may have been missed (the feed has no paging).</summary>
    public DateTimeOffset? CoverageGapAt { get; set; }

    public DateTimeOffset? LastDeliveryStagedAt { get; set; }
}

/// <summary>The Letterboxd channel of a guild and its window. No Discord user id is stored.</summary>
public sealed class LetterboxdGuildConfigEntity
{
    public ulong GuildId { get; set; }

    /// <summary>The account this guild's cards are about (set on configure); a changed Letterboxd:Username stops delivery.</summary>
    public string Account { get; set; } = "";

    public ulong? ChannelId { get; set; }
    public bool Paused { get; set; }

    /// <summary>Only entries that became eligible at or after this time can be sent live (configure, resume, enable, mode activation).</summary>
    public DateTimeOffset? LiveSince { get; set; }

    public DateTimeOffset? DryRunSince { get; set; }
    public string? ChannelProblem { get; set; }
    public DateTimeOffset? ChannelProblemAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>TSQ Letterboxd tables (additive; no other module's table is touched).</summary>
public sealed class LetterboxdModelContributor : IModelContributor
{
    public string Name => "letterboxd";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LetterboxdEntryEntity>(e =>
        {
            e.ToTable("letterboxd_entry");
            e.HasKey(x => new { x.Account, x.ViewingId });
            e.Property(x => x.Account).HasMaxLength(32);
            e.Property(x => x.FilmSlug).HasMaxLength(200);
            e.Property(x => x.Url).HasMaxLength(300);
            e.Property(x => x.FilmTitle).HasMaxLength(200);
            e.Property(x => x.ReviewExcerpt).HasMaxLength(1000);
            e.Property(x => x.PosterUrl).HasMaxLength(400);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.MemberHash).HasMaxLength(64);
            e.HasIndex(x => x.LastSeenAt);
        });
        modelBuilder.Entity<LetterboxdDeliveryEntity>(e =>
        {
            e.ToTable("letterboxd_delivery");
            e.HasKey(x => new { x.GuildId, x.Account, x.ViewingId, x.DryRun });
            e.Property(x => x.Account).HasMaxLength(32);
            e.Property(x => x.Kind).HasMaxLength(32);
        });
        modelBuilder.Entity<LetterboxdSourceStateEntity>(e =>
        {
            e.ToTable("letterboxd_source_state");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.ETag).HasMaxLength(200);
            e.Property(x => x.LastModified).HasMaxLength(100);
            e.Property(x => x.LastDetail).HasMaxLength(300);
        });
        modelBuilder.Entity<LetterboxdGuildConfigEntity>(e =>
        {
            e.ToTable("letterboxd_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.Account).HasMaxLength(32);
            e.Property(x => x.ChannelProblem).HasMaxLength(64);
        });
    }
}
