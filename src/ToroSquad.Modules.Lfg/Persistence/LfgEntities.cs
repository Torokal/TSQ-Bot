using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Persistence;

/// <summary>
/// One listing. <see cref="GameName"/> and <see cref="Details"/> are the user's own free text (never interpreted); there is
/// deliberately no game-specific column. The owner is also a row in <see cref="LfgParticipantEntity"/>.
/// </summary>
public sealed class LfgListingEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }

    /// <summary>The card message; null only until Discord confirmed the interaction response (a button click backfills it).</summary>
    public ulong? MessageId { get; set; }
    public ulong OwnerUserId { get; set; }
    public string GameName { get; set; } = "";
    public string? Details { get; set; }
    public int MaxPlayers { get; set; }
    public LfgStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the listing stopped being active (closed, expired = its expiry time, orphaned).</summary>
    public DateTimeOffset? ClosedAt { get; set; }
    public ulong? ClosedByUserId { get; set; }

    /// <summary>
    /// The card in Discord does not show the stored state yet (expired by the worker, closed from a confirmation, or an
    /// interactive update failed). The worker edits it and clears the flag; bounded by <see cref="CardSyncAttempts"/>.
    /// </summary>
    public bool CardStale { get; set; }
    public int CardSyncAttempts { get; set; }

    /// <summary>Optimistic concurrency token, incremented by every state change (backstop behind the write transaction).</summary>
    public long Version { get; set; }

    public List<LfgParticipantEntity> Participants { get; set; } = [];
}

/// <summary>A member of a listing. Primary key (ListingId, UserId): the same user can never be in a listing twice.</summary>
public sealed class LfgParticipantEntity
{
    public long ListingId { get; set; }
    public ulong UserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>Per-guild LFG settings. Absent row / null channel = /ekip works in any channel.</summary>
public sealed class LfgGuildConfigEntity
{
    public ulong GuildId { get; set; }
    public ulong? ChannelId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ulong UpdatedBy { get; set; }
}

/// <summary>TSQ LFG tables (additive; no other module's table is touched): lfg_listing, lfg_participant, lfg_guild_config.</summary>
public sealed class LfgModelContributor : IModelContributor
{
    public string Name => "lfg";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LfgListingEntity>(e =>
        {
            e.ToTable("lfg_listing");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.GameName).HasMaxLength(100); // 50 characters, up to two UTF-16 units each
            e.Property(x => x.Details).HasMaxLength(400); // 200 characters
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasMany(x => x.Participants).WithOne().HasForeignKey(p => p.ListingId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Status, x.ExpiresAt }); // expiry sweep: active AND ExpiresAt <= now
            e.HasIndex(x => new { x.GuildId, x.OwnerUserId, x.Status }); // per-user active limit
            e.HasIndex(x => x.CardStale).HasFilter("\"CardStale\" = 1"); // pending card edits only
        });
        modelBuilder.Entity<LfgParticipantEntity>(e =>
        {
            e.ToTable("lfg_participant");
            e.HasKey(x => new { x.ListingId, x.UserId });
            e.HasIndex(x => x.UserId); // privacy export/delete
        });
        modelBuilder.Entity<LfgGuildConfigEntity>(e =>
        {
            e.ToTable("lfg_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.GuildId).ValueGeneratedNever();
        });
    }
}
