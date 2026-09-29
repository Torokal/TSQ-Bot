using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class PredictionsModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prediction_daily_claim",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    LocalDay = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TournamentId = table.Column<long>(type: "INTEGER", nullable: false),
                    ClaimedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_daily_claim", x => x.Id);
                    table.CheckConstraint("CK_prediction_daily_claim_amount", "\"AmountMinor\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "prediction_tournament",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedByUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    FinalParticipantCount = table.Column<int>(type: "INTEGER", nullable: true),
                    FinalPredictionCount = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_tournament", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "prediction",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    TournamentId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatorUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatorName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 800, nullable: false),
                    Rules = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    LockAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LockedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LockedByUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    LockReason = table.Column<int>(type: "INTEGER", nullable: true),
                    SettledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SettledByUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    WinningOutcomeId = table.Column<long>(type: "INTEGER", nullable: true),
                    WinnerCount = table.Column<int>(type: "INTEGER", nullable: true),
                    PayoutTotalMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    CancelledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CancelledByUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", maxLength: 1200, nullable: true),
                    RefundTotalMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    EntryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StakeTotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CardStale = table.Column<bool>(type: "INTEGER", nullable: false),
                    CardSyncAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CardEditedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CardMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublishChecks = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction", x => x.Id);
                    table.UniqueConstraint("AK_prediction_Id_TournamentId", x => new { x.Id, x.TournamentId });
                    table.CheckConstraint("CK_prediction_totals", "\"EntryCount\" >= 0 AND \"StakeTotalMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_prediction_prediction_tournament_TournamentId",
                        column: x => x.TournamentId,
                        principalTable: "prediction_tournament",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "prediction_standing",
                columns: table => new
                {
                    TournamentId = table.Column<long>(type: "INTEGER", nullable: false),
                    Board = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BalanceMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrectCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SettledCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_standing", x => new { x.TournamentId, x.Board, x.Rank });
                    table.ForeignKey(
                        name: "FK_prediction_standing_prediction_tournament_TournamentId",
                        column: x => x.TournamentId,
                        principalTable: "prediction_tournament",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "prediction_wallet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TournamentId = table.Column<long>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    BalanceMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PendingMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrectCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SettledCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_wallet", x => x.Id);
                    table.UniqueConstraint("AK_prediction_wallet_Id_TournamentId", x => new { x.Id, x.TournamentId });
                    table.CheckConstraint("CK_prediction_wallet_balance", "\"BalanceMinor\" >= 0");
                    table.CheckConstraint("CK_prediction_wallet_counts", "\"CorrectCount\" >= 0 AND \"SettledCount\" >= \"CorrectCount\"");
                    table.CheckConstraint("CK_prediction_wallet_pending", "\"PendingMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_prediction_wallet_prediction_tournament_TournamentId",
                        column: x => x.TournamentId,
                        principalTable: "prediction_tournament",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "prediction_outcome",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PredictionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    OddsX100 = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_outcome", x => x.Id);
                    table.UniqueConstraint("AK_prediction_outcome_Id_PredictionId", x => new { x.Id, x.PredictionId });
                    table.CheckConstraint("CK_prediction_outcome_odds", "\"OddsX100\" BETWEEN 101 AND 100000");
                    table.ForeignKey(
                        name: "FK_prediction_outcome_prediction_PredictionId",
                        column: x => x.PredictionId,
                        principalTable: "prediction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "prediction_ledger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    TournamentId = table.Column<long>(type: "INTEGER", nullable: false),
                    WalletId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    BalanceAfterMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PredictionId = table.Column<long>(type: "INTEGER", nullable: true),
                    EntryId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_ledger", x => x.Id);
                    table.CheckConstraint("CK_prediction_ledger_balance", "\"BalanceAfterMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_prediction_ledger_prediction_wallet_WalletId",
                        column: x => x.WalletId,
                        principalTable: "prediction_wallet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "prediction_entry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PredictionId = table.Column<long>(type: "INTEGER", nullable: false),
                    OutcomeId = table.Column<long>(type: "INTEGER", nullable: false),
                    TournamentId = table.Column<long>(type: "INTEGER", nullable: false),
                    WalletId = table.Column<long>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    StakeMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    OddsX100 = table.Column<int>(type: "INTEGER", nullable: false),
                    PotentialPayoutMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SettledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PayoutMinor = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_entry", x => x.Id);
                    table.CheckConstraint("CK_prediction_entry_odds", "\"OddsX100\" BETWEEN 101 AND 100000");
                    table.CheckConstraint("CK_prediction_entry_payout", "\"PotentialPayoutMinor\" >= \"StakeMinor\"");
                    table.CheckConstraint("CK_prediction_entry_stake", "\"StakeMinor\" >= 100");
                    table.ForeignKey(
                        name: "FK_prediction_entry_prediction_PredictionId_TournamentId",
                        columns: x => new { x.PredictionId, x.TournamentId },
                        principalTable: "prediction",
                        principalColumns: new[] { "Id", "TournamentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_prediction_entry_prediction_outcome_OutcomeId_PredictionId",
                        columns: x => new { x.OutcomeId, x.PredictionId },
                        principalTable: "prediction_outcome",
                        principalColumns: new[] { "Id", "PredictionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_prediction_entry_prediction_wallet_WalletId_TournamentId",
                        columns: x => new { x.WalletId, x.TournamentId },
                        principalTable: "prediction_wallet",
                        principalColumns: new[] { "Id", "TournamentId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_CardStale",
                table: "prediction",
                column: "CardStale",
                filter: "\"CardStale\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_prediction_GuildId_MessageId",
                table: "prediction",
                columns: new[] { "GuildId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_GuildId_Status",
                table: "prediction",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_PublishKey",
                table: "prediction",
                column: "PublishKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prediction_Status_LockAt",
                table: "prediction",
                columns: new[] { "Status", "LockAt" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_TournamentId_Status",
                table: "prediction",
                columns: new[] { "TournamentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_daily_claim_GuildId_UserId_LocalDay",
                table: "prediction_daily_claim",
                columns: new[] { "GuildId", "UserId", "LocalDay" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prediction_entry_GuildId_UserId",
                table: "prediction_entry",
                columns: new[] { "GuildId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_entry_OutcomeId_PredictionId",
                table: "prediction_entry",
                columns: new[] { "OutcomeId", "PredictionId" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_entry_PredictionId_Status",
                table: "prediction_entry",
                columns: new[] { "PredictionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_entry_PredictionId_TournamentId",
                table: "prediction_entry",
                columns: new[] { "PredictionId", "TournamentId" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_entry_PredictionId_UserId",
                table: "prediction_entry",
                columns: new[] { "PredictionId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prediction_entry_TournamentId_UserId",
                table: "prediction_entry",
                columns: new[] { "TournamentId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_entry_WalletId_TournamentId",
                table: "prediction_entry",
                columns: new[] { "WalletId", "TournamentId" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_ledger_GuildId_UserId",
                table: "prediction_ledger",
                columns: new[] { "GuildId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_ledger_OperationKey",
                table: "prediction_ledger",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prediction_ledger_WalletId",
                table: "prediction_ledger",
                column: "WalletId");

            migrationBuilder.CreateIndex(
                name: "IX_prediction_outcome_PredictionId_Position",
                table: "prediction_outcome",
                columns: new[] { "PredictionId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prediction_standing_TournamentId_Board_UserId",
                table: "prediction_standing",
                columns: new[] { "TournamentId", "Board", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prediction_standing_UserId",
                table: "prediction_standing",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_prediction_tournament_active",
                table: "prediction_tournament",
                column: "GuildId",
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_prediction_tournament_GuildId_Number",
                table: "prediction_tournament",
                columns: new[] { "GuildId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prediction_wallet_GuildId_UserId",
                table: "prediction_wallet",
                columns: new[] { "GuildId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_wallet_TournamentId_UserId",
                table: "prediction_wallet",
                columns: new[] { "TournamentId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prediction_daily_claim");

            migrationBuilder.DropTable(
                name: "prediction_entry");

            migrationBuilder.DropTable(
                name: "prediction_ledger");

            migrationBuilder.DropTable(
                name: "prediction_standing");

            migrationBuilder.DropTable(
                name: "prediction_outcome");

            migrationBuilder.DropTable(
                name: "prediction_wallet");

            migrationBuilder.DropTable(
                name: "prediction");

            migrationBuilder.DropTable(
                name: "prediction_tournament");
        }
    }
}
