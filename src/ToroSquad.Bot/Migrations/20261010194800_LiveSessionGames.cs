using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class LiveSessionGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CategoryTracking",
                table: "live_creator_state",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "live_session_category",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatorKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SessionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FirstPlatform = table.Column<int>(type: "INTEGER", nullable: false),
                    TwitchCategoryId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    KickCategoryId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_session_category", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_live_session_category_CreatorKey_SessionNumber_NameKey",
                table: "live_session_category",
                columns: new[] { "CreatorKey", "SessionNumber", "NameKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_session_category");

            migrationBuilder.DropColumn(
                name: "CategoryTracking",
                table: "live_creator_state");
        }
    }
}
