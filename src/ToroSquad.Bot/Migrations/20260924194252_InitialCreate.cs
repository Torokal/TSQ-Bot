using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "confirmation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_confirmation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "esports_filter",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Dimension = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_filter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "esports_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    NotifyReminders = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReminderLeadMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    NotifyResults = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpoilerMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatermarkUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    VrsTopN = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelProblem = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChannelProblemAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "esports_known_team",
                columns: table => new
                {
                    TeamKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ShortName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_known_team", x => x.TeamKey);
                });

            migrationBuilder.CreateTable(
                name: "esports_match_snapshot",
                columns: table => new
                {
                    MatchKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledStartUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    PreviousStartUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    StartChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_match_snapshot", x => x.MatchKey);
                });

            migrationBuilder.CreateTable(
                name: "esports_provider_state",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDetail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAllowedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_provider_state", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "esports_role_grant",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    RoleId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantedByBot = table.Column<bool>(type: "INTEGER", nullable: false),
                    HadRoleBefore = table.Column<bool>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_role_grant", x => new { x.GuildId, x.UserId, x.RoleId });
                });

            migrationBuilder.CreateTable(
                name: "esports_role_mapping",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    RoleId = table.Column<long>(type: "INTEGER", nullable: false),
                    TeamKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TeamName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PingOnReminder = table.Column<bool>(type: "INTEGER", nullable: false),
                    PingOnResult = table.Column<bool>(type: "INTEGER", nullable: false),
                    SelfService = table.Column<bool>(type: "INTEGER", nullable: false),
                    SelfServiceApprovedBy = table.Column<long>(type: "INTEGER", nullable: true),
                    SelfServiceApprovedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_role_mapping", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "esports_team_follow",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    TeamKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TeamName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_team_follow", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "esports_user_pref",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    HideResults = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_esports_user_pref", x => new { x.GuildId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "guild_module_state",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ModuleId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedBy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_module_state", x => new { x.GuildId, x.ModuleId });
                });

            migrationBuilder.CreateTable(
                name: "guild_presence",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LeftAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PurgedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_presence", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "guild_settings",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SetupCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guild_settings", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "managed_command",
                columns: table => new
                {
                    ApplicationId = table.Column<long>(type: "INTEGER", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CommandId = table.Column<long>(type: "INTEGER", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SyncedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_command", x => new { x.ApplicationId, x.Scope, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LogicalKey = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ModuleId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Marker = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    IsDryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DiscordMessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DeliveredPayloadHash = table.Column<string>(type: "TEXT", nullable: true),
                    EditPending = table.Column<bool>(type: "INTEGER", nullable: false),
                    EditAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ReconcileAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_confirmation_ExpiresAt",
                table: "confirmation",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_esports_filter_GuildId_Dimension_Value",
                table: "esports_filter",
                columns: new[] { "GuildId", "Dimension", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_esports_match_snapshot_ScheduledStartUtc",
                table: "esports_match_snapshot",
                column: "ScheduledStartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_esports_role_mapping_GuildId_RoleId_TeamKey",
                table: "esports_role_mapping",
                columns: new[] { "GuildId", "RoleId", "TeamKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_esports_team_follow_GuildId_TeamKey",
                table: "esports_team_follow",
                columns: new[] { "GuildId", "TeamKey" });

            migrationBuilder.CreateIndex(
                name: "IX_esports_team_follow_GuildId_UserId_TeamKey",
                table: "esports_team_follow",
                columns: new[] { "GuildId", "UserId", "TeamKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_GuildId_ModuleId",
                table: "outbox",
                columns: new[] { "GuildId", "ModuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_LogicalKey",
                table: "outbox",
                column: "LogicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_Status_NextAttemptAt",
                table: "outbox",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "confirmation");

            migrationBuilder.DropTable(
                name: "esports_filter");

            migrationBuilder.DropTable(
                name: "esports_guild_config");

            migrationBuilder.DropTable(
                name: "esports_known_team");

            migrationBuilder.DropTable(
                name: "esports_match_snapshot");

            migrationBuilder.DropTable(
                name: "esports_provider_state");

            migrationBuilder.DropTable(
                name: "esports_role_grant");

            migrationBuilder.DropTable(
                name: "esports_role_mapping");

            migrationBuilder.DropTable(
                name: "esports_team_follow");

            migrationBuilder.DropTable(
                name: "esports_user_pref");

            migrationBuilder.DropTable(
                name: "guild_module_state");

            migrationBuilder.DropTable(
                name: "guild_presence");

            migrationBuilder.DropTable(
                name: "guild_settings");

            migrationBuilder.DropTable(
                name: "managed_command");

            migrationBuilder.DropTable(
                name: "outbox");
        }
    }
}
