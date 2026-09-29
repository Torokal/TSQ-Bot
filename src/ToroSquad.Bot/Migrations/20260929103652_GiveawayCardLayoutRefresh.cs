using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToroSquad.Bot.Migrations
{
    /// <summary>
    /// Data only, no schema change: giveaway cards posted before the prize-led layout are marked for one redraw, so the
    /// worker edits them into the new layout (the same bot-token edit as any card update: no new message, no ping). Active,
    /// drawn and cancelled cards only; an orphaned card no longer exists. Runs once, like every migration.
    /// </summary>
    public partial class GiveawayCardLayoutRefresh : Migration
    {
        public const string RedrawExistingCards =
            "UPDATE giveaway SET CardStale = 1, CardSyncAttempts = 0 WHERE MessageId IS NOT NULL AND Status IN (0, 1, 2);";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RedrawExistingCards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: a redraw flag is cleared by the worker once the card shows the stored state.
        }
    }
}
