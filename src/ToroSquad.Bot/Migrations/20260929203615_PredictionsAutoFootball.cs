using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class PredictionsAutoFootball : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "prediction",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "prediction_auto_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalEventId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MarketKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CompetitionKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HomeTeam = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AwayTeam = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TrackedTeams = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    KickoffAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LatestKickoffAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    OddsAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeliveryFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    BookmakerKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BookmakerTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OddsUpdatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    OddsFetchedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    HomeOddsX100 = table.Column<int>(type: "INTEGER", nullable: true),
                    DrawOddsX100 = table.Column<int>(type: "INTEGER", nullable: true),
                    AwayOddsX100 = table.Column<int>(type: "INTEGER", nullable: true),
                    RawPrices = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PredictionId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MissingCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_auto_event", x => x.Id);
                    table.CheckConstraint("CK_prediction_auto_event_counts", "\"OddsAttempts\" >= 0 AND \"DeliveryFailures\" >= 0 AND \"MissingCount\" >= 0");
                    table.CheckConstraint("CK_prediction_auto_event_observe", "\"Mode\" = 2 OR \"PredictionId\" IS NULL");
                    table.ForeignKey(
                        name: "FK_prediction_auto_event_prediction_PredictionId",
                        column: x => x.PredictionId,
                        principalTable: "prediction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "prediction_auto_provider",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RemainingCredits = table.Column<int>(type: "INTEGER", nullable: true),
                    UsedCredits = table.Column<int>(type: "INTEGER", nullable: true),
                    LastCost = table.Column<int>(type: "INTEGER", nullable: true),
                    MeasuredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UnmeasuredCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    CostlyCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    PausedUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    PauseReason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastErrorAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCatalogAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ActiveCompetitions = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LastDiscoveryAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_auto_provider", x => x.Provider);
                });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_auto_event_GuildId_Provider_ExternalEventId_MarketKind_Mode",
                table: "prediction_auto_event",
                columns: new[] { "GuildId", "Provider", "ExternalEventId", "MarketKind", "Mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_prediction_auto_event_Mode_State_NextAttemptAt",
                table: "prediction_auto_event",
                columns: new[] { "Mode", "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_auto_event_PredictionId",
                table: "prediction_auto_event",
                column: "PredictionId",
                unique: true,
                filter: "\"PredictionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prediction_auto_event");

            migrationBuilder.DropTable(
                name: "prediction_auto_provider");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "prediction");
        }
    }
}
