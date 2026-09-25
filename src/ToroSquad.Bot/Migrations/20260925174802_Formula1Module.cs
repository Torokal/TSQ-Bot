using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class Formula1Module : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "f1_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatermarkUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SpoilerMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    PingRoleId = table.Column<long>(type: "INTEGER", nullable: true),
                    PingOnStarts = table.Column<bool>(type: "INTEGER", nullable: false),
                    PingOnResults = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyPracticeStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyPracticeResults = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifySprintStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifySprintResults = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyRaceStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyRaceResults = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyStandings = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyQualifyingStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyQualifyingResults = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifySprintQualifyingStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifySprintQualifyingResults = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelProblem = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChannelProblemAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_f1_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "f1_provider_state",
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
                    table.PrimaryKey("PK_f1_provider_state", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "f1_result_snapshot",
                columns: table => new
                {
                    SessionKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CanonicalHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    FirstAvailableAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Corrections = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_f1_result_snapshot", x => x.SessionKey);
                });

            migrationBuilder.CreateTable(
                name: "f1_session_snapshot",
                columns: table => new
                {
                    SessionKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MeetingKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionType = table.Column<int>(type: "INTEGER", nullable: false),
                    MeetingName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CircuitName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Country = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ScheduledStartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduledEndUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedRecordedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SuspendedObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResumeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FinishedObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinalisedObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CancelledObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastLifecycleEventAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LifecycleProvider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LifecycleProviderRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResultsProvider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ResultsProviderRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResultNextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResultAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultLastDetail = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StandingsBaselineDriversHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StandingsBaselineConstructorsHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StandingsWatchUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    StandingsNextCheckAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StandingsChecks = table.Column<int>(type: "INTEGER", nullable: false),
                    StandingsDriversSnapshotId = table.Column<long>(type: "INTEGER", nullable: true),
                    StandingsConstructorsSnapshotId = table.Column<long>(type: "INTEGER", nullable: true),
                    StandingsAttachedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StandingsWindowClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_f1_session_snapshot", x => x.SessionKey);
                });

            migrationBuilder.CreateTable(
                name: "f1_standings_snapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CanonicalHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastConfirmedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_f1_standings_snapshot", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_f1_session_snapshot_LifecycleProvider_LifecycleProviderRef",
                table: "f1_session_snapshot",
                columns: new[] { "LifecycleProvider", "LifecycleProviderRef" });

            migrationBuilder.CreateIndex(
                name: "IX_f1_session_snapshot_ScheduledStartUtc",
                table: "f1_session_snapshot",
                column: "ScheduledStartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_f1_session_snapshot_Season_Round",
                table: "f1_session_snapshot",
                columns: new[] { "Season", "Round" });

            migrationBuilder.CreateIndex(
                name: "IX_f1_standings_snapshot_Kind_Season_Id",
                table: "f1_standings_snapshot",
                columns: new[] { "Kind", "Season", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "f1_guild_config");

            migrationBuilder.DropTable(
                name: "f1_provider_state");

            migrationBuilder.DropTable(
                name: "f1_result_snapshot");

            migrationBuilder.DropTable(
                name: "f1_session_snapshot");

            migrationBuilder.DropTable(
                name: "f1_standings_snapshot");
        }
    }
}
