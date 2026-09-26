using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class LiveModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "live_creator_state",
                columns: table => new
                {
                    CreatorKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SessionDetectedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    GraceSince = table.Column<long>(type: "INTEGER", nullable: true),
                    SessionEndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SessionPlatforms = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Announced = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotAnnouncedReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AnnouncementKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AnnouncementGuildId = table.Column<long>(type: "INTEGER", nullable: true),
                    AnnouncementChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    AnnouncementMessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    AnnouncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Replacements = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_creator_state", x => x.CreatorKey);
                });

            migrationBuilder.CreateTable(
                name: "live_platform_state",
                columns: table => new
                {
                    CreatorKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Platform = table.Column<int>(type: "INTEGER", nullable: false),
                    Login = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TitleChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AvatarUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    StatusObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    MetadataObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastLiveAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOfflineAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastEventId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_platform_state", x => new { x.CreatorKey, x.Platform });
                });

            migrationBuilder.CreateTable(
                name: "live_provider_state",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDetail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LastErrorAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_provider_state", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_creator_state");

            migrationBuilder.DropTable(
                name: "live_platform_state");

            migrationBuilder.DropTable(
                name: "live_provider_state");
        }
    }
}
