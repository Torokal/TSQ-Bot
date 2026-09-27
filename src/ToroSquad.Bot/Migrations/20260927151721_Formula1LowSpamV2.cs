using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class Formula1LowSpamV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotifyDisqualification",
                table: "f1_guild_config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyRaceReminder",
                table: "f1_guild_config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyRedFlag",
                table: "f1_guild_config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifySafetyCar",
                table: "f1_guild_config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyWeekendSchedule",
                table: "f1_guild_config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "f1_race_control_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Lap = table.Column<int>(type: "INTEGER", nullable: true),
                    DriverNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    DriverCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_f1_race_control_event", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_f1_race_control_event_SessionKey_Fingerprint",
                table: "f1_race_control_event",
                columns: new[] { "SessionKey", "Fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "f1_race_control_event");

            migrationBuilder.DropColumn(
                name: "NotifyDisqualification",
                table: "f1_guild_config");

            migrationBuilder.DropColumn(
                name: "NotifyRaceReminder",
                table: "f1_guild_config");

            migrationBuilder.DropColumn(
                name: "NotifyRedFlag",
                table: "f1_guild_config");

            migrationBuilder.DropColumn(
                name: "NotifySafetyCar",
                table: "f1_guild_config");

            migrationBuilder.DropColumn(
                name: "NotifyWeekendSchedule",
                table: "f1_guild_config");
        }
    }
}
