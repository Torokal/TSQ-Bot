using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class NewsModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "news_article",
                columns: table => new
                {
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ArticleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Baseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    Relevant = table.Column<bool>(type: "INTEGER", nullable: false),
                    MatchReason = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchEvidence = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_news_article", x => new { x.Source, x.ArticleId });
                });

            migrationBuilder.CreateTable(
                name: "news_delivery",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ArticleId = table.Column<long>(type: "INTEGER", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StagedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_news_delivery", x => new { x.GuildId, x.Source, x.ArticleId, x.DryRun });
                });

            migrationBuilder.CreateTable(
                name: "news_feed_state",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastModified = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TtlMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    BaselineAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PrunedBelowArticleId = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    LastHttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    LastDetail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    NextPollAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRelevantCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastNewCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDeliveryStagedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RosterPlayers = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RosterVerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RosterSource = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RosterLastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RosterLastOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    RosterDetail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    RosterFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    RosterNextAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_news_feed_state", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "news_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
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
                    table.PrimaryKey("PK_news_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_news_article_FirstSeenAt",
                table: "news_article",
                column: "FirstSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "news_article");

            migrationBuilder.DropTable(
                name: "news_delivery");

            migrationBuilder.DropTable(
                name: "news_feed_state");

            migrationBuilder.DropTable(
                name: "news_guild_config");
        }
    }
}
