using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class UpdatesModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "updates_delivery",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    GameKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    StagedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_updates_delivery", x => new { x.GuildId, x.Provider, x.GameKey, x.ExternalId, x.Kind });
                });

            migrationBuilder.CreateTable(
                name: "updates_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    LiveSince = table.Column<long>(type: "INTEGER", nullable: true),
                    DryRunSince = table.Column<long>(type: "INTEGER", nullable: true),
                    PlannedMode = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelProblem = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ChannelProblemAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_updates_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "updates_item",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    GameKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Baseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    Classification = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassificationReason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    ClassificationChangedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_updates_item", x => new { x.Provider, x.GameKey, x.ExternalId });
                });

            migrationBuilder.CreateTable(
                name: "updates_source_state",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    GameKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ProviderGameId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BaselineAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    LastHttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    LastDetail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    NextPollAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceCacheSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    LastItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastNewCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUpdateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAmbiguousCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDiscoveredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastDeliveryStagedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PrunedThroughPublishedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_updates_source_state", x => new { x.Provider, x.GameKey });
                });

            migrationBuilder.CreateTable(
                name: "updates_subscription",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    GameKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnabledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_updates_subscription", x => new { x.GuildId, x.GameKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_updates_item_FirstSeenAt",
                table: "updates_item",
                column: "FirstSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "updates_delivery");

            migrationBuilder.DropTable(
                name: "updates_guild_config");

            migrationBuilder.DropTable(
                name: "updates_item");

            migrationBuilder.DropTable(
                name: "updates_source_state");

            migrationBuilder.DropTable(
                name: "updates_subscription");
        }
    }
}
