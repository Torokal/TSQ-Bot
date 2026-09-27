using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class VolleyballModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vb_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatermarkUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PingRoleId = table.Column<long>(type: "INTEGER", nullable: true),
                    PingOnReminder = table.Column<bool>(type: "INTEGER", nullable: false),
                    PingOnFinal = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyReminder = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyStarted = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifySets = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyFinal = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyPostponedCancelled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelProblem = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChannelProblemAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vb_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "vb_match_snapshot",
                columns: table => new
                {
                    MatchKey = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderMatchId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CompetitionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CompetitionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Round = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StartTimeUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    FollowedSide = table.Column<int>(type: "INTEGER", nullable: false),
                    HomeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    HomeCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    AwayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AwayCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    HomeLogoUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    AwayLogoUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Venue = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BroadcastsJson = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    HomeSets = table.Column<int>(type: "INTEGER", nullable: false),
                    AwaySets = table.Column<int>(type: "INTEGER", nullable: false),
                    SetsJson = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Started = table.Column<bool>(type: "INTEGER", nullable: false),
                    Finished = table.Column<bool>(type: "INTEGER", nullable: false),
                    Postponed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Cancelled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CurrentSet = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentSetHomePoints = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentSetAwayPoints = table.Column<int>(type: "INTEGER", nullable: true),
                    EventsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    PendingJson = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    PendingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastListedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastProviderUpdateAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastProblem = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LastProblemAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Problems = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vb_match_snapshot", x => x.MatchKey);
                });

            migrationBuilder.CreateTable(
                name: "vb_provider_state",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDetail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vb_provider_state", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vb_match_snapshot_Provider_ProviderMatchId",
                table: "vb_match_snapshot",
                columns: new[] { "Provider", "ProviderMatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vb_match_snapshot_StartTimeUtc",
                table: "vb_match_snapshot",
                column: "StartTimeUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vb_guild_config");

            migrationBuilder.DropTable(
                name: "vb_match_snapshot");

            migrationBuilder.DropTable(
                name: "vb_provider_state");
        }
    }
}
