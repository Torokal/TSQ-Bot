using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class MatchLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CancelledObservedAt",
                table: "esports_match_snapshot",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PostponedObservedAt",
                table: "esports_match_snapshot",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RescheduledObservedAt",
                table: "esports_match_snapshot",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RescheduledToUtc",
                table: "esports_match_snapshot",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StartedObservedAt",
                table: "esports_match_snapshot",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelledObservedAt",
                table: "esports_match_snapshot");

            migrationBuilder.DropColumn(
                name: "PostponedObservedAt",
                table: "esports_match_snapshot");

            migrationBuilder.DropColumn(
                name: "RescheduledObservedAt",
                table: "esports_match_snapshot");

            migrationBuilder.DropColumn(
                name: "RescheduledToUtc",
                table: "esports_match_snapshot");

            migrationBuilder.DropColumn(
                name: "StartedObservedAt",
                table: "esports_match_snapshot");
        }
    }
}
