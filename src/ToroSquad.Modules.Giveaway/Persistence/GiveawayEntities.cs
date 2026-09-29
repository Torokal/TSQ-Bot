using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Giveaway.Domain;

namespace ToroSquad.Modules.Giveaway.Persistence;

/// <summary>
/// One giveaway. The entrants are NOT copied here: they are the 🎉 reactions on the card, read from Discord when the
/// giveaway is drawn (so a removed reaction never counts). Only the result is stored: <see cref="EntrantCount"/> and the
/// winners (<see cref="GiveawayWinnerEntity"/>).
/// </summary>
public sealed class GiveawayEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }

    /// <summary>The card; null only between the insert and Discord confirming the post.</summary>
    public ulong? MessageId { get; set; }

    public ulong CreatorUserId { get; set; }

    /// <summary>The creator's display name when the giveaway was started (the card footer is plain text, not a mention).</summary>
    public string CreatorName { get; set; } = "";

    public string Prize { get; set; } = "";
    public string? Description { get; set; }
    public int WinnerCount { get; set; }
    public GiveawayStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>When it was drawn, cancelled or orphaned; null = automatic.</summary>
    public DateTimeOffset? EndedAt { get; set; }
    public ulong? EndedByUserId { get; set; }

    /// <summary>Valid entrants (unique, no bots) at the latest draw.</summary>
    public int? EntrantCount { get; set; }

    /// <summary>Number of rerolls; the winners of round N are the current ones.</summary>
    public int RerollCount { get; set; }

    /// <summary>Failed draw attempts (Discord could not be read); the next attempt waits until <see cref="NextDrawAttemptAt"/>.</summary>
    public int DrawAttempts { get; set; }
    public DateTimeOffset? NextDrawAttemptAt { get; set; }

    /// <summary>The card does not show the stored state yet; the worker edits it (bounded by <see cref="CardSyncAttempts"/>).</summary>
    public bool CardStale { get; set; }
    public int CardSyncAttempts { get; set; }

    /// <summary>Optimistic concurrency token, incremented by every state change (backstop behind the write transaction).</summary>
    public long Version { get; set; }
}

/// <summary>A winner of one draw: round 0 is the draw itself, round N the N-th reroll. Place 1 is 🥇.</summary>
public sealed class GiveawayWinnerEntity
{
    public long GiveawayId { get; set; }
    public int Round { get; set; }
    public int Place { get; set; }
    public ulong UserId { get; set; }
}

/// <summary>TSQ Giveaway tables (additive; no other module's table is touched): giveaway, giveaway_winner.</summary>
public sealed class GiveawayModelContributor : IModelContributor
{
    public string Name => "giveaway";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GiveawayEntity>(e =>
        {
            e.ToTable("giveaway");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.CreatorName).HasMaxLength(100);
            e.Property(x => x.Prize).HasMaxLength(200); // 100 characters, up to two UTF-16 units each
            e.Property(x => x.Description).HasMaxLength(1000); // 500 characters
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.Status, x.EndsAt }); // draw sweep: active AND EndsAt <= now
            e.HasIndex(x => new { x.GuildId, x.Status }); // per-guild active limit, autocomplete
            e.HasIndex(x => new { x.GuildId, x.MessageId }); // target by message link / id
            e.HasIndex(x => x.CardStale).HasFilter("\"CardStale\" = 1"); // pending card edits only
        });
        modelBuilder.Entity<GiveawayWinnerEntity>(e =>
        {
            e.ToTable("giveaway_winner");
            e.HasKey(x => new { x.GiveawayId, x.Round, x.Place });
            e.HasIndex(x => new { x.GiveawayId, x.Round, x.UserId }).IsUnique(); // never the same winner twice in one draw
            e.HasIndex(x => x.UserId); // privacy export/delete
            e.HasOne<GiveawayEntity>().WithMany().HasForeignKey(x => x.GiveawayId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
