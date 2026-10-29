using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class LetterboxdModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "letterboxd_delivery",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Account = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ViewingId = table.Column<long>(type: "INTEGER", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StagedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_letterboxd_delivery", x => new { x.GuildId, x.Account, x.ViewingId, x.DryRun });
                });

            migrationBuilder.CreateTable(
                name: "letterboxd_entry",
                columns: table => new
                {
                    Account = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ViewingId = table.Column<long>(type: "INTEGER", nullable: false),
                    GuidKind = table.Column<int>(type: "INTEGER", nullable: false),
                    FilmSlug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TmdbMovieId = table.Column<long>(type: "INTEGER", nullable: true),
                    TmdbTvId = table.Column<long>(type: "INTEGER", nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    FilmTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FilmYear = table.Column<int>(type: "INTEGER", nullable: true),
                    RatingHalves = table.Column<int>(type: "INTEGER", nullable: true),
                    Rewatch = table.Column<bool>(type: "INTEGER", nullable: true),
                    WatchedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReviewExcerpt = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReviewTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Spoilers = table.Column<bool>(type: "INTEGER", nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    HadReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MemberHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Baseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    EligibleAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_letterboxd_entry", x => new { x.Account, x.ViewingId });
                });

            migrationBuilder.CreateTable(
                name: "letterboxd_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Account = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    LiveSince = table.Column<long>(type: "INTEGER", nullable: true),
                    DryRunSince = table.Column<long>(type: "INTEGER", nullable: true),
                    ChannelProblem = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ChannelProblemAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_letterboxd_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "letterboxd_source_state",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastModified = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TtlMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    BaselineAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastMode = table.Column<int>(type: "INTEGER", nullable: true),
                    ModeSince = table.Column<long>(type: "INTEGER", nullable: true),
                    LastConflictCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PrunedBelowViewingId = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    LastHttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    LastDetail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    NextPollAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastNewCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OldestItemPublishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CoverageGapAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastDeliveryStagedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_letterboxd_source_state", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_letterboxd_entry_LastSeenAt",
                table: "letterboxd_entry",
                column: "LastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "letterboxd_delivery");

            migrationBuilder.DropTable(
                name: "letterboxd_entry");

            migrationBuilder.DropTable(
                name: "letterboxd_guild_config");

            migrationBuilder.DropTable(
                name: "letterboxd_source_state");
        }
    }
}
