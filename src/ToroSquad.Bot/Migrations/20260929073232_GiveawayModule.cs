using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class GiveawayModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "giveaway",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatorUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatorName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Prize = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    WinnerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndsAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EndedByUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    EntrantCount = table.Column<int>(type: "INTEGER", nullable: true),
                    RerollCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DrawAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextDrawAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CardStale = table.Column<bool>(type: "INTEGER", nullable: false),
                    CardSyncAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_giveaway", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "giveaway_winner",
                columns: table => new
                {
                    GiveawayId = table.Column<long>(type: "INTEGER", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    Place = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_giveaway_winner", x => new { x.GiveawayId, x.Round, x.Place });
                    table.ForeignKey(
                        name: "FK_giveaway_winner_giveaway_GiveawayId",
                        column: x => x.GiveawayId,
                        principalTable: "giveaway",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_giveaway_CardStale",
                table: "giveaway",
                column: "CardStale",
                filter: "\"CardStale\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_giveaway_GuildId_MessageId",
                table: "giveaway",
                columns: new[] { "GuildId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_giveaway_GuildId_Status",
                table: "giveaway",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_giveaway_Status_EndsAt",
                table: "giveaway",
                columns: new[] { "Status", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_giveaway_winner_GiveawayId_Round_UserId",
                table: "giveaway_winner",
                columns: new[] { "GiveawayId", "Round", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_giveaway_winner_UserId",
                table: "giveaway_winner",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "giveaway_winner");

            migrationBuilder.DropTable(
                name: "giveaway");
        }
    }
}
