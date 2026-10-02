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

    /// <summary>The member's display name at their latest entry, prediction or daily reward (for the name-only closing announcement).</summary>
    public string? DisplayName { get; set; }

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

    /// <summary>The creator's display name when publishing (the card shows plain text, not a mention). Empty for an automatic one.</summary>
    public string CreatorName { get; set; } = "";

    /// <summary>Manual (a member's form) or AutoFootball (no human creator: <see cref="CreatorUserId"/> is 0).</summary>
    public PredictionOrigin Origin { get; set; }

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

    /// <summary>
    /// The retention cleanup removed the card of this settled/cancelled prediction (or found it already gone): its Discord
    /// message is archived for good — no edit, no replacement card. Only the message; the prediction and its history stay.
    /// </summary>
    public DateTimeOffset? CardRemovedAt { get; set; }

    /// <summary>Failed removal attempts (429/5xx/timeouts/permissions), bounded; the next one not before <see cref="CardRemovalNextAt"/>.</summary>
    public int CardRemovalAttempts { get; set; }

    public DateTimeOffset? CardRemovalNextAt { get; set; }

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
/// One member's single entry in one prediction (one row per member and prediction, reused after a withdrawal). The odds and
/// the possible payout are snapshots taken when it was submitted or last changed, from the stored outcome: the settlement
/// pays exactly <see cref="PotentialPayoutMinor"/>, whatever the configuration says later.
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

    /// <summary>
    /// 0 when created, +1 at every change, withdrawal or new entry after a withdrawal: part of those operations' ledger keys,
    /// so each is booked exactly once.
    /// </summary>
    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public long? PayoutMinor { get; set; }
}

/// <summary>
/// The coin journal: every change of a wallet's spendable balance, with a unique <see cref="OperationKey"/> per economic
/// operation (initial:w1, daily:c7, stake:e3, payout:e3, refund:e3; an entry's later operations carry its revision:
/// stake-up:e3:r1, stake-down:e3:r2, withdraw:e3:r3, stake:e3:r4) so the same operation can never be booked twice.
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

/// <summary>
/// One real match followed by the automatic opener, per guild, provider, provider match id, market and mode (Observe rows
/// are their own partition: they never link a prediction and never make Live think a match was published). The unique key
/// has no tournament: a match is opened at most once, whatever tournament is active. It keeps what the decision was based
/// on (competition, teams, planned kickoff — first and latest —, the chosen bookmaker's fixed odds with the raw prices and
/// their market time) and the job's progress (state, attempts, next attempt, the reason it was skipped or needs review).
/// </summary>
public sealed class PredictionAutoEventEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public AutomationMode Mode { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalEventId { get; set; } = "";
    public string MarketKind { get; set; } = "";
    public string CompetitionKey { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";

    /// <summary>The followed clubs in it ("GS", "FB", "BJK"; a derby has two) — one row, one prediction.</summary>
    public string TrackedTeams { get; set; } = "";

    /// <summary>The planned kickoff the prediction was built on (the latest one before publishing).</summary>
    public DateTimeOffset KickoffAt { get; set; }

    /// <summary>The provider's latest planned kickoff (differs from <see cref="KickoffAt"/> when it changed after publishing).</summary>
    public DateTimeOffset LatestKickoffAt { get; set; }

    public DateTimeOffset PublishAt { get; set; }
    public AutoEventState State { get; set; }
    public AutoBlockReason Reason { get; set; }
    public int OddsAttempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int DeliveryFailures { get; set; }
    public string? BookmakerKey { get; set; }
    public string? BookmakerTitle { get; set; }
    public DateTimeOffset? OddsUpdatedAt { get; set; }
    public DateTimeOffset? OddsFetchedAt { get; set; }
    public int? HomeOddsX100 { get; set; }
    public int? DrawOddsX100 { get; set; }
    public int? AwayOddsX100 { get; set; }

    /// <summary>The provider's decimal prices as received, "home|draw|away" (the card uses the ×100 values).</summary>
    public string? RawPrices { get; set; }

    public long? PredictionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Discovery passes in a row that did not return this match (it is never taken as "cancelled").</summary>
    public int MissingCount { get; set; }
}

/// <summary>
/// The provider's usage as last reported by its own headers, plus the job's pause and error state — persisted, so a restart,
/// a deploy, a new tournament or midnight never resets what was used. Credits are never assumed to come back on a date.
/// </summary>
public sealed class PredictionAutoProviderEntity
{
    public string Provider { get; set; } = "";
    public int? RemainingCredits { get; set; }
    public int? UsedCredits { get; set; }
    public int? LastCost { get; set; }
    public DateTimeOffset? MeasuredAt { get; set; }

    /// <summary>Credit-consuming calls since the last measurement whose cost is unknown (timeouts): counted as spent.</summary>
    public int UnmeasuredCalls { get; set; }

    public int CostlyCalls { get; set; }
    public DateTimeOffset? PausedUntil { get; set; }
    public string? PauseReason { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastCatalogAt { get; set; }

    /// <summary>The allow-listed competitions the catalog reported as in season (comma separated).</summary>
    public string? ActiveCompetitions { get; set; }

    public DateTimeOffset? LastDiscoveryAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum WeeklyBoardStatus
{
    /// <summary>The leaderboard was staged in the outbox (delivery, retries and expiry are the outbox's).</summary>
    Staged = 0,

    /// <summary>Nobody was eligible in the active tournament (or none was active): no message, the week is done.</summary>
    SkippedNoParticipants = 1,
}

/// <summary>
/// One automatic weekly leaderboard decision per guild and week (unique): the week was evaluated once — staged or skipped —
/// and is never evaluated again, whatever tournament is active, restarts, two processes or a manual /ongoru liderlik.
/// The message itself lives in the outbox (pruned after delivery); this row is the lasting dedup.
/// </summary>
public sealed class PredictionWeeklyBoardEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>yyyyww of the slot's local date (<see cref="WeeklySchedule.WeekKey"/>).</summary>
    public int WeekKey { get; set; }

    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
    public WeeklyBoardStatus Status { get; set; }

    /// <summary>The tournament active at evaluation (null: none) and its eligible participants then.</summary>
    public long? TournamentId { get; set; }

    public int Participants { get; set; }
}

/// <summary>The two boards of a tournament podium.</summary>
public enum PredictionBoard
{
    Coins = 0,
    Correct = 1,
}

/// <summary>
/// The podiums of a closed tournament (most coins and most correct predictions), with their final values and the members'
/// display names at closing — the closing announcement is rendered from these, never from live wallets.
/// </summary>
public sealed class PredictionStandingEntity
{
    public long TournamentId { get; set; }
    public PredictionBoard Board { get; set; }
    public int Rank { get; set; }
    public ulong UserId { get; set; }
    public string? DisplayName { get; set; }
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
            e.Property(x => x.DisplayName).HasMaxLength(100);
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
            e.Property(x => x.Origin).HasConversion<int>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.PublishKey).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.Status }); // autocomplete, unresolved check
            e.HasIndex(x => new { x.TournamentId, x.Status });
            e.HasIndex(x => new { x.GuildId, x.MessageId }); // target by message link / id
            e.HasIndex(x => new { x.Status, x.LockAt }); // lock sweep
            e.HasIndex(x => x.CardStale).HasFilter("\"CardStale\" = 1"); // pending card edits only
            e.HasIndex(x => new { x.Status, x.CardRemovedAt }); // terminal card cleanup sweep
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
            e.HasIndex(x => new { x.PredictionId, x.UserId }).IsUnique(); // one entry (at most one active bet) per member and prediction
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

        modelBuilder.Entity<PredictionAutoEventEntity>(e =>
        {
            e.ToTable("prediction_auto_event", t =>
            {
                // Observe rows never point at a prediction; only a Live row can.
                t.HasCheckConstraint("CK_prediction_auto_event_observe", "\"Mode\" = 2 OR \"PredictionId\" IS NULL");
                t.HasCheckConstraint("CK_prediction_auto_event_counts", "\"OddsAttempts\" >= 0 AND \"DeliveryFailures\" >= 0 AND \"MissingCount\" >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Mode).HasConversion<int>();
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.Reason).HasConversion<int>();
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ExternalEventId).HasMaxLength(64);
            e.Property(x => x.MarketKind).HasMaxLength(16);
            e.Property(x => x.CompetitionKey).HasMaxLength(64);
            e.Property(x => x.HomeTeam).HasMaxLength(200);
            e.Property(x => x.AwayTeam).HasMaxLength(200);
            e.Property(x => x.TrackedTeams).HasMaxLength(16);
            e.Property(x => x.BookmakerKey).HasMaxLength(64);
            e.Property(x => x.BookmakerTitle).HasMaxLength(200);
            e.Property(x => x.RawPrices).HasMaxLength(100);
            // A real match is opened at most once per guild, provider, market and mode — whatever the TSQ tournament.
            e.HasIndex(x => new { x.GuildId, x.Provider, x.ExternalEventId, x.MarketKind, x.Mode }).IsUnique();
            e.HasIndex(x => x.PredictionId).IsUnique().HasFilter("\"PredictionId\" IS NOT NULL");
            e.HasIndex(x => new { x.Mode, x.State, x.NextAttemptAt });
            e.HasOne<PredictionEntity>().WithMany().HasForeignKey(x => x.PredictionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PredictionAutoProviderEntity>(e =>
        {
            e.ToTable("prediction_auto_provider");
            e.HasKey(x => x.Provider);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.PauseReason).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(64);
            e.Property(x => x.ActiveCompetitions).HasMaxLength(512);
        });

        modelBuilder.Entity<PredictionWeeklyBoardEntity>(e =>
        {
            e.ToTable("prediction_weekly_board");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.GuildId, x.WeekKey }).IsUnique(); // at most one automatic weekly leaderboard per guild and week
        });

        modelBuilder.Entity<PredictionStandingEntity>(e =>
        {
            e.ToTable("prediction_standing");
            e.HasKey(x => new { x.TournamentId, x.Board, x.Rank });
            e.Property(x => x.Board).HasConversion<int>();
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.HasIndex(x => new { x.TournamentId, x.Board, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId); // privacy export/delete
            e.HasOne<PredictionTournamentEntity>().WithMany().HasForeignKey(x => x.TournamentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
