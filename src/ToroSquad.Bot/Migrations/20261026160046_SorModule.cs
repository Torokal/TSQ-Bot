using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class SorModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sor_conversation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    IsPrivate = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Stamp = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastActivityAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sor_conversation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sor_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    AllowedChannelIds = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    AllowedRoleIds = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    UserCooldownSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    UserDailyLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    GuildDailyLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxContextTurns = table.Column<int>(type: "INTEGER", nullable: true),
                    IdleMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    HistoryHours = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sor_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "sor_guild_usage",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    DayKey = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Requests = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    Timeouts = table.Column<int>(type: "INTEGER", nullable: false),
                    ParseFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    Discarded = table.Column<int>(type: "INTEGER", nullable: false),
                    UnknownUsage = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ReasoningTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CachedInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sor_guild_usage", x => new { x.GuildId, x.DayKey });
                });

            migrationBuilder.CreateTable(
                name: "sor_memory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceConversationId = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceInteractionId = table.Column<long>(type: "INTEGER", nullable: true),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Stamp = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sor_memory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sor_request",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InteractionId = table.Column<long>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    IsPrivate = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConversationId = table.Column<long>(type: "INTEGER", nullable: true),
                    DayKey = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Failure = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    TraceCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    ReasoningTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CachedInputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    PendingAnswer = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: true),
                    PendingMemory = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PendingContext = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sor_request", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sor_user",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NoticeShownAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Stamp = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sor_user", x => new { x.GuildId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "sor_memory_use",
                columns: table => new
                {
                    ConversationId = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sor_memory_use", x => new { x.ConversationId, x.MemoryId });
                    table.ForeignKey(
                        name: "FK_sor_memory_use_sor_conversation_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "sor_conversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sor_turn",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<long>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Seq = table.Column<int>(type: "INTEGER", nullable: false),
                    Question = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Answer = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sor_turn", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sor_turn_sor_conversation_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "sor_conversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sor_conversation_GuildId_OwnerUserId_ChannelId_IsPrivate_State",
                table: "sor_conversation",
                columns: new[] { "GuildId", "OwnerUserId", "ChannelId", "IsPrivate", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_sor_conversation_LastActivityAt",
                table: "sor_conversation",
                column: "LastActivityAt");

            migrationBuilder.CreateIndex(
                name: "IX_sor_memory_GuildId_UserId",
                table: "sor_memory",
                columns: new[] { "GuildId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_sor_memory_SourceConversationId",
                table: "sor_memory",
                column: "SourceConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_sor_memory_use_MemoryId",
                table: "sor_memory_use",
                column: "MemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_sor_request_CreatedAt",
                table: "sor_request",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_sor_request_GuildId_UserId_DayKey",
                table: "sor_request",
                columns: new[] { "GuildId", "UserId", "DayKey" });

            migrationBuilder.CreateIndex(
                name: "IX_sor_request_InteractionId",
                table: "sor_request",
                column: "InteractionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sor_request_State_UpdatedAt",
                table: "sor_request",
                columns: new[] { "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sor_turn_ConversationId_Seq",
                table: "sor_turn",
                columns: new[] { "ConversationId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sor_turn_GuildId_UserId",
                table: "sor_turn",
                columns: new[] { "GuildId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_sor_user_UserId",
                table: "sor_user",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sor_guild_config");

            migrationBuilder.DropTable(
                name: "sor_guild_usage");

            migrationBuilder.DropTable(
                name: "sor_memory");

            migrationBuilder.DropTable(
                name: "sor_memory_use");

            migrationBuilder.DropTable(
                name: "sor_request");

            migrationBuilder.DropTable(
                name: "sor_turn");

            migrationBuilder.DropTable(
                name: "sor_user");

            migrationBuilder.DropTable(
                name: "sor_conversation");
        }
    }
}
