using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class BirthdayModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "birthday_announcement",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Celebrants = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_birthday_announcement", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "birthday_celebration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Announced = table.Column<bool>(type: "INTEGER", nullable: false),
                    RoleState = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleError = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RoleGrantedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RoleRemovedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_birthday_celebration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "birthday_guild_config",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    ChannelProblem = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ChannelProblemAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_birthday_guild_config", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "birthday_registration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Day = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_birthday_registration", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_birthday_announcement_GuildId_LocalDate",
                table: "birthday_announcement",
                columns: new[] { "GuildId", "LocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_birthday_celebration_GuildId_LocalDate",
                table: "birthday_celebration",
                columns: new[] { "GuildId", "LocalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_birthday_celebration_GuildId_UserId_Year",
                table: "birthday_celebration",
                columns: new[] { "GuildId", "UserId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_birthday_celebration_RoleState",
                table: "birthday_celebration",
                column: "RoleState",
                filter: "\"RoleState\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_birthday_registration_GuildId_Month_Day",
                table: "birthday_registration",
                columns: new[] { "GuildId", "Month", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_birthday_registration_GuildId_UserId",
                table: "birthday_registration",
                columns: new[] { "GuildId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_birthday_registration_UserId",
                table: "birthday_registration",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "birthday_announcement");

            migrationBuilder.DropTable(
                name: "birthday_celebration");

            migrationBuilder.DropTable(
                name: "birthday_guild_config");

            migrationBuilder.DropTable(
                name: "birthday_registration");
        }
    }
}
