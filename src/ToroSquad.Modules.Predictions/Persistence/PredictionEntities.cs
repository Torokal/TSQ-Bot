using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Persistence;

/// <summary>
/// One tournament of one guild. Exactly one is <see cref="PredictionTournamentStatus.Active"/> per guild (partial unique
/// index); every wallet, prediction and entry belongs to one. Closing freezes its rows as the archive (they are never
/// rewritten) and records the final numbers here and the podium in <see cref="PredictionStandingEntity"/>.
/// </summary>
public sealed class PredictionTournamentEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>1, 2, 3… per guild (what members see).</summary>
    public int Number { get; set; }

    public PredictionTournamentStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public ulong? ClosedByUserId { get; set; }
    public int? FinalParticipantCount { get; set; }
    public int? FinalPredictionCount { get; set; }
}

/// <summary>
/// A member's TSQ Coin in one tournament: <see cref="BalanceMinor"/> is spendable, <see cref="PendingMinor"/> the stakes of
/// entries not settled yet (their principal only, never a possible win). Correct/settled counts are the member's
/// prediction record in this tournament (cancelled entries are in neither). All amounts in units (1 coin = 100).
/// </summary>
public sealed class PredictionWalletEntity
{
    public long Id { get; set; }
    public long TournamentId { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public long BalanceMinor { get; set; }
    public long PendingMinor { get; set; }
    public int CorrectCount { get; set; }
    public int SettledCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A published question. Title, outcomes, odds and rules never change after publishing.</summary>
public sealed class PredictionEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public long TournamentId { get; set; }
    public ulong ChannelId { get; set; }

    /// <summary>The public card; null only while <see cref="PredictionStatus.Publishing"/>.</summary>
    public ulong? MessageId { get; set; }

    public ulong CreatorUserId { get; set; }

    /// <summary>The creator's display name when publishing (the card shows plain text, not a mention).</summary>
    public string CreatorName { get; set; } = "";

    public string Title { get; set; } = "";
    public string? Rules { get; set; }
    public DateTimeOffset? LockAt { get; set; }
    public PredictionStatus Status { get; set; }

    /// <summary>The draft this row was published from (unique): a repeated publish of one draft can never create a second prediction.</summary>
    public string PublishKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public ulong? LockedByUserId { get; set; }
    public PredictionLockReason? LockReason { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public ulong? SettledByUserId { get; set; }
    public long? WinningOutcomeId { get; set; }
    public int? WinnerCount { get; set; }
    public long? PayoutTotalMinor { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public ulong? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }
    public long? RefundTotalMinor { get; set; }

    /// <summary>Entries and their summed stakes (the card's numbers), kept in the same transaction as the entries.</summary>
    public int EntryCount { get; set; }
    public long StakeTotalMinor { get; set; }

    /// <summary>The card does not show the stored state yet; the card sync edits it (bounded by <see cref="CardSyncAttempts"/>).</summary>
    public bool CardStale { get; set; }
    public int CardSyncAttempts { get; set; }
    public DateTimeOffset? CardEditedAt { get; set; }

    /// <summary>Discord reported the card (or its channel) as deleted: no more edits; entries stopped.</summary>
    public bool CardMissing { get; set; }

    /// <summary>Searches for a card whose post was uncertain.</summary>
    public int PublishChecks { get; set; }

    /// <summary>Optimistic concurrency token, incremented by every state change (backstop behind the write transaction).</summary>
    public long Version { get; set; }
}

public sealed class PredictionOutcomeEntity
{
    public long Id { get; set; }
    public long PredictionId { get; set; }

    /// <summary>1-based, the order of the form and of the card.</summary>
    public int Position { get; set; }

    public string Label { get; set; } = "";
    public int OddsX100 { get; set; }
}

/// <summary>
/// One member's single entry in one prediction. The odds and the possible payout are snapshots taken when it was confirmed:
/// the settlement pays exactly <see cref="PotentialPayoutMinor"/>, whatever the configuration says later.
/// </summary>
public sealed class PredictionEntryEntity
{
    public long Id { get; set; }
    public long PredictionId { get; set; }
    public long OutcomeId { get; set; }
    public long TournamentId { get; set; }
    public long WalletId { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public long StakeMinor { get; set; }
    public int OddsX100 { get; set; }
    public long PotentialPayoutMinor { get; set; }
    public PredictionEntryStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public long? PayoutMinor { get; set; }
}

/// <summary>
/// The coin journal: every change of a wallet's spendable balance, with a unique <see cref="OperationKey"/> per economic
/// operation (initial:w1, daily:c7, stake:e3, payout:e3, refund:e3) so the same operation can never be booked twice.
/// </summary>
public sealed class PredictionLedgerEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public long TournamentId { get; set; }
    public long WalletId { get; set; }
    public ulong UserId { get; set; }
    public PredictionLedgerKind Kind { get; set; }
    public long AmountMinor { get; set; }
    public long BalanceAfterMinor { get; set; }
    public string OperationKey { get; set; } = "";
    public long? PredictionId { get; set; }
    public long? EntryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One daily reward: unique per guild + member + Türkiye calendar day (yyyymmdd), independent of tournaments — a tournament
/// ending the same day gives no second claim.
/// </summary>
public sealed class PredictionDailyClaimEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public int LocalDay { get; set; }
    public long AmountMinor { get; set; }
    public long TournamentId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>The podium of a closed tournament, with its final values (the closing announcement is rendered from these).</summary>
public sealed class PredictionStandingEntity
{
    public long TournamentId { get; set; }
    public int Rank { get; set; }
    public ulong UserId { get; set; }
    public long BalanceMinor { get; set; }
    public int CorrectCount { get; set; }
    public int SettledCount { get; set; }
}

/// <summary>
/// TSQ Öngörü tables (additive; no other module's table is touched). The database itself enforces the economic
/// invariants: one active tournament per guild, one wallet per tournament and member, one entry per prediction and member,
/// one daily claim per guild, member and day, one ledger row per economic operation, no negative balance, entries whose
/// outcome belongs to their prediction and whose wallet belongs to their prediction's tournament.
/// </summary>
public sealed class PredictionsModelContributor : IModelContributor
{
    public string Name => "predictions";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PredictionTournamentEntity>(e =>
        {
            e.ToTable("prediction_tournament");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.GuildId, x.Number }).IsUnique();
            e.HasIndex(x => x.GuildId).IsUnique().HasFilter("\"Status\" = 0").HasDatabaseName("IX_prediction_tournament_active");
        });

        modelBuilder.Entity<PredictionWalletEntity>(e =>
        {
            e.ToTable("prediction_wallet", t =>
            {
                t.HasCheckConstraint("CK_prediction_wallet_balance", "\"BalanceMinor\" >= 0");
                t.HasCheckConstraint("CK_prediction_wallet_pending", "\"PendingMinor\" >= 0");
                t.HasCheckConstraint("CK_prediction_wallet_counts", "\"CorrectCount\" >= 0 AND \"SettledCount\" >= \"CorrectCount\"");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasAlternateKey(x => new { x.Id, x.TournamentId });
            e.HasIndex(x => new { x.TournamentId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.UserId }); // privacy export/delete
            e.HasOne<PredictionTournamentEntity>().WithMany().HasForeignKey(x => x.TournamentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PredictionEntity>(e =>
        {
            e.ToTable("prediction", t =>
            {
                t.HasCheckConstraint("CK_prediction_totals", "\"EntryCount\" >= 0 AND \"StakeTotalMinor\" >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasAlternateKey(x => new { x.Id, x.TournamentId });
            e.Property(x => x.CreatorName).HasMaxLength(100);
            e.Property(x => x.Title).HasMaxLength(800); // 200 characters, up to four UTF-16 units each
            e.Property(x => x.Rules).HasMaxLength(4000);
            e.Property(x => x.CancelReason).HasMaxLength(1200);
            e.Property(x => x.PublishKey).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.LockReason).HasConversion<int?>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.PublishKey).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.Status }); // autocomplete, unresolved check
            e.HasIndex(x => new { x.TournamentId, x.Status });
            e.HasIndex(x => new { x.GuildId, x.MessageId }); // target by message link / id
            e.HasIndex(x => new { x.Status, x.LockAt }); // lock sweep
            e.HasIndex(x => x.CardStale).HasFilter("\"CardStale\" = 1"); // pending card edits only
            e.HasOne<PredictionTournamentEntity>().WithMany().HasForeignKey(x => x.TournamentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PredictionOutcomeEntity>(e =>
        {
            e.ToTable("prediction_outcome", t =>
            {
                t.HasCheckConstraint("CK_prediction_outcome_odds", $"\"OddsX100\" BETWEEN {Odds.MinX100} AND {Odds.MaxX100}");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasAlternateKey(x => new { x.Id, x.PredictionId });
            e.Property(x => x.Label).HasMaxLength(320);
            e.HasIndex(x => new { x.PredictionId, x.Position }).IsUnique();
            e.HasOne<PredictionEntity>().WithMany().HasForeignKey(x => x.PredictionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PredictionEntryEntity>(e =>
        {
            e.ToTable("prediction_entry", t =>
            {
                t.HasCheckConstraint("CK_prediction_entry_stake", $"\"StakeMinor\" >= {Coins.MinStakeMinor}");
                t.HasCheckConstraint("CK_prediction_entry_odds", $"\"OddsX100\" BETWEEN {Odds.MinX100} AND {Odds.MaxX100}");
                t.HasCheckConstraint("CK_prediction_entry_payout", "\"PotentialPayoutMinor\" >= \"StakeMinor\"");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.PredictionId, x.UserId }).IsUnique(); // one entry per member and prediction
            e.HasIndex(x => new { x.TournamentId, x.UserId });
            e.HasIndex(x => new { x.GuildId, x.UserId }); // privacy export/delete
            e.HasIndex(x => new { x.PredictionId, x.Status });
            // Scope: the outcome belongs to this prediction, the prediction and the wallet to this tournament.
            e.HasOne<PredictionOutcomeEntity>().WithMany().HasForeignKey(x => new { x.OutcomeId, x.PredictionId })
                .HasPrincipalKey(x => new { x.Id, x.PredictionId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PredictionEntity>().WithMany().HasForeignKey(x => new { x.PredictionId, x.TournamentId })
                .HasPrincipalKey(x => new { x.Id, x.TournamentId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PredictionWalletEntity>().WithMany().HasForeignKey(x => new { x.WalletId, x.TournamentId })
                .HasPrincipalKey(x => new { x.Id, x.TournamentId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PredictionLedgerEntity>(e =>
        {
            e.ToTable("prediction_ledger", t =>
            {
                t.HasCheckConstraint("CK_prediction_ledger_balance", "\"BalanceAfterMinor\" >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.OperationKey).HasMaxLength(64);
            e.HasIndex(x => x.OperationKey).IsUnique();
            e.HasIndex(x => x.WalletId);
            e.HasIndex(x => new { x.GuildId, x.UserId }); // privacy export/delete
            e.HasOne<PredictionWalletEntity>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PredictionDailyClaimEntity>(e =>
        {
            e.ToTable("prediction_daily_claim", t =>
            {
                t.HasCheckConstraint("CK_prediction_daily_claim_amount", "\"AmountMinor\" > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.GuildId, x.UserId, x.LocalDay }).IsUnique();
        });

        modelBuilder.Entity<PredictionStandingEntity>(e =>
        {
            e.ToTable("prediction_standing");
            e.HasKey(x => new { x.TournamentId, x.Rank });
            e.HasIndex(x => new { x.TournamentId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId); // privacy export/delete
            e.HasOne<PredictionTournamentEntity>().WithMany().HasForeignKey(x => x.TournamentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
