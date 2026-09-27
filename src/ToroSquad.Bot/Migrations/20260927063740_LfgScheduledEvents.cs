using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class LfgScheduledEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Response",
                table: "lfg_participant",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "EventAt",
                table: "lfg_listing",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyAtStart",
                table: "lfg_listing",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyBeforeStart",
                table: "lfg_listing",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ReminderHandledAt",
                table: "lfg_listing",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderState",
                table: "lfg_listing",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "StartNoticeHandledAt",
                table: "lfg_listing",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartNoticeState",
                table: "lfg_listing",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "VoiceChannelId",
                table: "lfg_listing",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_lfg_listing_Status_EventAt",
                table: "lfg_listing",
                columns: new[] { "Status", "EventAt" },
                filter: "\"EventAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_lfg_listing_Status_EventAt",
                table: "lfg_listing");

            migrationBuilder.DropColumn(
                name: "Response",
                table: "lfg_participant");

            migrationBuilder.DropColumn(
                name: "EventAt",
                table: "lfg_listing");

            migrationBuilder.DropColumn(
                name: "NotifyAtStart",
                table: "lfg_listing");

            migrationBuilder.DropColumn(
                name: "NotifyBeforeStart",
                table: "lfg_listing");

            migrationBuilder.DropColumn(
                name: "ReminderHandledAt",
                table: "lfg_listing");

            migrationBuilder.DropColumn(
                name: "ReminderState",
                table: "lfg_listing");

            migrationBuilder.DropColumn(
                name: "StartNoticeHandledAt",
                table: "lfg_listing");

            migrationBuilder.DropColumn(
                name: "StartNoticeState",
                table: "lfg_listing");

            migrationBuilder.DropColumn(
                name: "VoiceChannelId",
                table: "lfg_listing");
        }
    }
}
