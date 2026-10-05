using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <inheritdoc />
    public partial class UpdatesGameChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ChannelId",
                table: "updates_subscription",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChannelProblem",
                table: "updates_subscription",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ChannelProblemAt",
                table: "updates_subscription",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChannelId",
                table: "updates_subscription");

            migrationBuilder.DropColumn(
                name: "ChannelProblem",
                table: "updates_subscription");

            migrationBuilder.DropColumn(
                name: "ChannelProblemAt",
                table: "updates_subscription");
        }
    }
}
