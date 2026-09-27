using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class LfgModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lfg_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfg_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "lfg_listing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    GameName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    MaxPlayers = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedByUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    CardStale = table.Column<bool>(type: "INTEGER", nullable: false),
                    CardSyncAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfg_listing", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lfg_participant",
                columns: table => new
                {
                    ListingId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    JoinedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfg_participant", x => new { x.ListingId, x.UserId });
                    table.ForeignKey(
                        name: "FK_lfg_participant_lfg_listing_ListingId",
                        column: x => x.ListingId,
                        principalTable: "lfg_listing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lfg_listing_CardStale",
                table: "lfg_listing",
                column: "CardStale",
                filter: "\"CardStale\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_lfg_listing_GuildId_OwnerUserId_Status",
                table: "lfg_listing",
                columns: new[] { "GuildId", "OwnerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_lfg_listing_Status_ExpiresAt",
                table: "lfg_listing",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_lfg_participant_UserId",
                table: "lfg_participant",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lfg_guild_config");

            migrationBuilder.DropTable(
                name: "lfg_participant");

            migrationBuilder.DropTable(
                name: "lfg_listing");
        }
    }
}
