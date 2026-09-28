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

    /// <summary>When the listing stops being usable: (EventAt ?? CreatedAt) + duration. Never the event start itself.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the activity starts; null = it starts now (the original behaviour).</summary>
    public DateTimeOffset? EventAt { get; set; }

    /// <summary>Optional guild voice channel for the group (validated on creation; cleared if it disappears).</summary>
    public ulong? VoiceChannelId { get; set; }

    /// <summary>Explicit opt-ins of the creator: ping the Joined players 30 minutes before / at the start.</summary>
    public bool NotifyBeforeStart { get; set; }
    public bool NotifyAtStart { get; set; }

    /// <summary>Durable, handled-once markers of the two notices (see <see cref="LfgNoticeState"/>); no user list is kept here.</summary>
    public LfgNoticeState ReminderState { get; set; }
    public DateTimeOffset? ReminderHandledAt { get; set; }
    public LfgNoticeState StartNoticeState { get; set; }
    public DateTimeOffset? StartNoticeHandledAt { get; set; }

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

/// <summary>
/// A member of a listing. Primary key (ListingId, UserId): the same user can never be in a listing twice — a change of mind
/// (Joined, Maybe, Waitlisted) updates <see cref="Response"/> in place.
/// </summary>
public sealed class LfgParticipantEntity
{
    public long ListingId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>Joined (default; every row created before RSVP existed), Maybe or Waitlisted.</summary>
    public LfgResponse Response { get; set; }

    /// <summary>When the current response was given (orders the Joined and Maybe lists).</summary>
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>
    /// The place in the waitlist: set only while <see cref="LfgResponse.Waitlisted"/> (null otherwise), assigned under the
    /// listing's write lock as the highest live place + 1. The queue order is this stored number — the order in which the
    /// writes happened — never a timestamp, a name or an id.
    /// </summary>
    public long? WaitlistOrder { get; set; }
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
            e.Property(x => x.ReminderState).HasConversion<int>();
            e.Property(x => x.StartNoticeState).HasConversion<int>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasMany(x => x.Participants).WithOne().HasForeignKey(p => p.ListingId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Status, x.ExpiresAt }); // expiry sweep: active AND ExpiresAt <= now
            e.HasIndex(x => new { x.GuildId, x.OwnerUserId, x.Status }); // per-user active limit
            e.HasIndex(x => x.CardStale).HasFilter("\"CardStale\" = 1"); // pending card edits only
            e.HasIndex(x => new { x.Status, x.EventAt }).HasFilter("\"EventAt\" IS NOT NULL"); // due event notices of scheduled listings
        });
        modelBuilder.Entity<LfgParticipantEntity>(e =>
        {
            e.ToTable("lfg_participant");
            e.HasKey(x => new { x.ListingId, x.UserId });
            e.Property(x => x.Response).HasConversion<int>();
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
