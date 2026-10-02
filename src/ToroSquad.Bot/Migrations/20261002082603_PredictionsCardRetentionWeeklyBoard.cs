using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class PredictionsCardRetentionWeeklyBoard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CardRemovalAttempts",
                table: "prediction",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "CardRemovalNextAt",
                table: "prediction",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CardRemovedAt",
                table: "prediction",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "prediction_weekly_board",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    WeekKey = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EvaluatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TournamentId = table.Column<long>(type: "INTEGER", nullable: true),
                    Participants = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_weekly_board", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_Status_CardRemovedAt",
                table: "prediction",
                columns: new[] { "Status", "CardRemovedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_weekly_board_GuildId_WeekKey",
                table: "prediction_weekly_board",
                columns: new[] { "GuildId", "WeekKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prediction_weekly_board");

            migrationBuilder.DropIndex(
                name: "IX_prediction_Status_CardRemovedAt",
                table: "prediction");

            migrationBuilder.DropColumn(
                name: "CardRemovalAttempts",
                table: "prediction");

            migrationBuilder.DropColumn(
                name: "CardRemovalNextAt",
                table: "prediction");

            migrationBuilder.DropColumn(
                name: "CardRemovedAt",
                table: "prediction");
        }
    }
}
