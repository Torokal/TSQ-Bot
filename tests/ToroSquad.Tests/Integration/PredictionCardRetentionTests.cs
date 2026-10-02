using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The terminal card retention on the real SQLite database with the fake Discord transport and a fake clock: a settled or
/// cancelled prediction's card is removed exactly once, <see cref="PredictionsOptionsRetention"/> after the settlement or
/// cancellation was committed (never earlier, never for an open or locked one), restart-safe, 404 = done, transient
/// failures retried with a pause, permission problems bounded and shown by the doctor — and a removed card is archived:
/// no edit, no replacement card. Only the Discord message goes: entries, coins, statistics, boards and the tournament end
/// are untouched.
/// </summary>
public sealed class PredictionCardRetentionTests : IAsyncLifetime
{
    private static readonly TimeSpan PredictionsOptionsRetention = TimeSpan.FromHours(12);

    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await PredictionTestKit.CreateAsync();

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    /// <summary>A prediction with two entries (one winner), settled now; returns it with the members' balances after the payout.</summary>
    private async Task<(PredictionView Prediction, long Winner, long Loser)> SettledAsync()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(101), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(102), prediction, 2, "50")).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        await _kit.TickAsync(); // the final card edit
        return (prediction, (await _kit.WalletAsync(101))!.BalanceMinor, (await _kit.WalletAsync(102))!.BalanceMinor);
    }

    private bool CardShown(PredictionView prediction) => _kit.Transport.Messages.Any(m => m.Id == prediction.Message);

    [Fact]
    public async Task A_settled_card_stays_for_11_h_59_min_and_is_removed_once_at_12_h_with_the_history_kept()
    {
        var (prediction, winner, loser) = await SettledAsync();
        var settledAt = (await _kit.RowAsync(prediction.Id)).SettledAt!.Value;
        Shown(_kit.Card(prediction)).Embed!.Title.Should().NotBeNull("the final card was shown before anything else");

        await _kit.TickAsync(PredictionsOptionsRetention - TimeSpan.FromMinutes(1));
        CardShown(prediction).Should().BeTrue("11 h 59 min: not yet");
        _kit.Transport.DeleteCalls.Should().Be(0);

        _kit.Host.Clock.SetUtcNow(settledAt + PredictionsOptionsRetention); // exactly 12 h
        await _kit.TickAsync();
        CardShown(prediction).Should().BeFalse("exactly 12 h after the settlement: due");
        _kit.Transport.DeleteCalls.Should().Be(1);
        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.CardRemovedAt, row.MessageId, row.SettledAt).Should().Be((PredictionStatus.Settled, (DateTimeOffset?)(settledAt + PredictionsOptionsRetention),
            (ulong?)prediction.Message!.Value.Value, (DateTimeOffset?)settledAt), "only the message is gone; the row keeps its card id and settlement time");

        for (var i = 0; i < 3; i++)
            await _kit.TickAsync(TimeSpan.FromHours(1));
        _kit.Transport.DeleteCalls.Should().Be(1, "removed once; later passes do nothing");
        (await _kit.EntriesAsync(prediction.Id)).Select(e => e.Status).Should().Equal(PredictionEntryStatus.Won, PredictionEntryStatus.Lost);
        ((await _kit.WalletAsync(101))!.BalanceMinor, (await _kit.WalletAsync(102))!.BalanceMinor).Should().Be((winner, loser));
    }

    [Fact]
    public async Task A_cancelled_card_has_the_same_retention_from_its_cancellation()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(101), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        await _kit.TickAsync(TimeSpan.FromHours(3)); // created long before the cancellation: the clock starts at the cancellation
        (await _kit.CancelAsync(Creator(), prediction)).Result.Succeeded.Should().BeTrue();
        var cancelledAt = (await _kit.RowAsync(prediction.Id)).CancelledAt!.Value;

        await _kit.TickAsync(PredictionsOptionsRetention - TimeSpan.FromSeconds(1));
        CardShown(prediction).Should().BeTrue();
        await _kit.TickAsync(TimeSpan.FromSeconds(1));
        CardShown(prediction).Should().BeFalse();
        (await _kit.RowAsync(prediction.Id)).CardRemovedAt.Should().Be(cancelledAt + PredictionsOptionsRetention);
        (await _kit.EntriesAsync(prediction.Id)).Single().Status.Should().Be(PredictionEntryStatus.Refunded);
    }

    [Fact]
    public async Task Open_and_locked_cards_are_never_removed()
    {
        var open = await _kit.CreatePredictionAsync(title: "Açık kalan öngörü");
        var locked = await _kit.CreatePredictionAsync(title: "Kilitli öngörü");
        (await _kit.LockAsync(Creator(), locked)).Succeeded.Should().BeTrue();

        for (var i = 0; i < 4; i++)
            await _kit.TickAsync(TimeSpan.FromHours(6)); // 24 h
        CardShown(open).Should().BeTrue();
        CardShown(locked).Should().BeTrue();
        _kit.Transport.DeleteCalls.Should().Be(0);
        (await _kit.RowAsync(locked.Id)).CardRemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task After_a_restart_a_card_settled_15_h_ago_is_removed_on_the_first_pass()
    {
        var (prediction, _, _) = await SettledAsync();
        var settledAt = (await _kit.RowAsync(prediction.Id)).SettledAt!.Value;

        await using var restarted = await PredictionTestKit.CreateAsync(settledAt + TimeSpan.FromHours(15), directory: _kit.Host.Directory, transport: _kit.Transport);
        await restarted.TickAsync();
        CardShown(prediction).Should().BeFalse();
        _kit.Transport.DeleteCalls.Should().Be(1);
        (await restarted.RowAsync(prediction.Id)).CardRemovedAt.Should().Be(settledAt + TimeSpan.FromHours(15));
    }

    [Fact]
    public async Task Discord_404_counts_as_removed_and_is_not_tried_again()
    {
        var (prediction, _, _) = await SettledAsync();
        _kit.Transport.ScriptDelete(() => new SendOutcome.Permanent(PermanentFailureKind.UnknownMessage, "Unknown Message"));

        await _kit.TickAsync(PredictionsOptionsRetention);
        (await _kit.RowAsync(prediction.Id)).CardRemovedAt.Should().NotBeNull();
        await _kit.TickAsync(TimeSpan.FromHours(1));
        _kit.Transport.DeleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_timeout_or_5xx_is_retried_after_its_pause_and_then_removes_the_card_once()
    {
        var (prediction, _, _) = await SettledAsync();
        _kit.Transport.ScriptDelete(() => new SendOutcome.Transient("TimeoutException"));
        _kit.Transport.ScriptDelete(() => new SendOutcome.Transient("503"));

        await _kit.TickAsync(PredictionsOptionsRetention);
        var first = await _kit.RowAsync(prediction.Id);
        (first.CardRemovedAt, first.CardRemovalAttempts, first.CardRemovalNextAt).Should().Be(((DateTimeOffset?)null, 1, _kit.Host.Clock.GetUtcNow() + TimeSpan.FromMinutes(1)));

        await _kit.TickAsync(TimeSpan.FromSeconds(30));
        _kit.Transport.DeleteCalls.Should().Be(1, "not before its pause");
        await _kit.TickAsync(TimeSpan.FromSeconds(30));
        (await _kit.RowAsync(prediction.Id)).CardRemovalAttempts.Should().Be(2);
        await _kit.TickAsync(TimeSpan.FromMinutes(5));
        CardShown(prediction).Should().BeFalse();
        (_kit.Transport.DeleteCalls, (await _kit.RowAsync(prediction.Id)).CardRemovedAt).Should().Be((3, _kit.Host.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task A_permission_problem_is_tried_a_bounded_number_of_times_never_in_a_fast_loop_and_shown_by_the_doctor()
    {
        var (prediction, _, _) = await SettledAsync();
        for (var i = 0; i < PredictionCardSync.MaxRemovalAttempts + 2; i++)
            _kit.Transport.ScriptDelete(() => new SendOutcome.Permanent(PermanentFailureKind.MissingAccess, "Missing Access"));

        await _kit.TickAsync(PredictionsOptionsRetention);
        for (var i = 0; i < 30; i++)
            await _kit.TickAsync(TimeSpan.FromSeconds(10)); // five minutes of passes: one more attempt (after the 1-minute pause), then 5 minutes
        _kit.Transport.DeleteCalls.Should().Be(2);

        for (var i = 0; i < 12; i++)
            await _kit.TickAsync(TimeSpan.FromHours(6));
        _kit.Transport.DeleteCalls.Should().Be(PredictionCardSync.MaxRemovalAttempts, "bounded");
        var row = await _kit.RowAsync(prediction.Id);
        (row.CardRemovedAt, row.CardRemovalAttempts).Should().Be(((DateTimeOffset?)null, PredictionCardSync.MaxRemovalAttempts));
        CardShown(prediction).Should().BeTrue();

        var report = await _kit.Host.Services.GetServices<IModuleHealthCheck>().Single(c => c.Module.Value == "predictions").CheckAsync(Ct);
        report.Entries.Should().Contain(e => e.Component == "predictions.health.card_cleanup" && e.State == HealthState.Degraded);
    }

    [Fact]
    public async Task A_removed_card_is_archived_no_edit_and_no_replacement_card_ever()
    {
        var (prediction, _, _) = await SettledAsync();
        await _kit.TickAsync(PredictionsOptionsRetention);
        var sends = _kit.Transport.SendCalls;
        var edits = _kit.Transport.EditCalls;

        await _kit.Db(db => db.Set<PredictionEntity>().Where(p => p.Id == prediction.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CardStale, true)));
        for (var i = 0; i < 6; i++)
            await _kit.TickAsync(TimeSpan.FromMinutes(15)); // repair, presence checks, stale edits
        (_kit.Transport.SendCalls, _kit.Transport.EditCalls).Should().Be((sends, edits));
        var row = await _kit.RowAsync(prediction.Id);
        (row.CardMissing, row.CardStale, row.CardRemovedAt is not null).Should().Be((false, false, true));
    }

    [Fact]
    public async Task A_terminal_card_deleted_by_hand_before_12_h_keeps_todays_policy_no_replacement_and_the_cleanup_finishes_quietly()
    {
        var (prediction, _, _) = await SettledAsync();
        _kit.Transport.DeleteMessage(prediction.Message!.Value); // a moderator, 2 hours after the settlement
        await _kit.TickAsync(TimeSpan.FromHours(2));
        var sends = _kit.Transport.SendCalls;

        await _kit.TickAsync(TimeSpan.FromHours(10));
        _kit.Transport.SendCalls.Should().Be(sends, "a settled prediction never gets a replacement card");
        (await _kit.RowAsync(prediction.Id)).CardRemovedAt.Should().NotBeNull("404 = already gone");

        // An OPEN card deleted by hand still gets its replacement (unchanged repair).
        var open = await _kit.CreatePredictionAsync(title: "Açık öngörü");
        _kit.Transport.DeleteMessage(open.Message!.Value);
        await _kit.TickAsync(TimeSpan.FromMinutes(11));
        await _kit.TickAsync(TimeSpan.FromMinutes(11));
        (await _kit.RowAsync(open.Id)).Should().Match<PredictionEntity>(p => p.MessageId != open.Message!.Value.Value && !p.CardMissing && p.CardRemovedAt == null);
    }

    [Fact]
    public async Task Removal_racing_a_repeated_settlement_and_a_card_edit_changes_no_coins_and_deletes_once()
    {
        var (prediction, winner, loser) = await SettledAsync();
        var ledger = await _kit.CountAsync<PredictionLedgerEntity>();
        _kit.Host.Clock.Advance(PredictionsOptionsRetention);

        var results = await _kit.TogetherAsync(
            async () => { await _kit.Host.Services.GetRequiredService<PredictionWorker>().RunOnceAsync(Ct); return true; },
            async () => (await _kit.ConfirmSettleAsync(Creator(), prediction, 2)).Result.Succeeded,
            async () => await _kit.Host.InScopeAsync(sp => sp.GetRequiredService<PredictionCardSync>().SafeSyncAsync(prediction.Id, Ct)) is not PredictionCardSyncOutcome.Retry,
            async () => await _kit.Host.InScopeAsync(sp => sp.GetRequiredService<PredictionCardSync>().RemoveExpiredCardsAsync(Ct)) >= 0);
        results[1].Should().BeFalse("a settled prediction is final");

        (await _kit.CountAsync<PredictionLedgerEntity>()).Should().Be(ledger, "no second payout or refund");
        ((await _kit.WalletAsync(101))!.BalanceMinor, (await _kit.WalletAsync(102))!.BalanceMinor).Should().Be((winner, loser));
        _kit.Transport.DeleteCalls.Should().BeInRange(1, 2, "a second attempt can only meet 404");
        CardShown(prediction).Should().BeFalse();
        (await _kit.RowAsync(prediction.Id)).Should().Match<PredictionEntity>(p => p.Status == PredictionStatus.Settled && p.CardRemovedAt != null);
    }

    [Fact]
    public async Task A_removed_prediction_still_counts_in_my_entries_the_leaderboard_and_the_tournament_end()
    {
        var (prediction, _, _) = await SettledAsync();
        await _kit.TickAsync(PredictionsOptionsRetention);
        CardShown(prediction).Should().BeFalse();

        var mine = await _kit.Economy(e => e.MyEntriesAsync(Member(101), Commands, 0, Ct));
        mine.View!.Embed!.Description.Should().Contain("**#" + prediction.Id + "**").And.Contain("✅ Kazandı (+110 TSQ Coin)");
        var board = (await _kit.Economy(e => e.LeaderboardAsync(Member(99), Commands, Ct))).View!.Embed!;
        board.Fields[1].Value.Should().StartWith("🥇 <@101> — **1** doğru / 1 sonuçlanan (%100)");

        var token = await _kit.EndTokenAsync(Admin());
        (await _kit.ConfirmEndAsync(Admin(), token!)).Result.Succeeded.Should().BeTrue();
        var podium = await _kit.Db(db => db.Set<PredictionStandingEntity>().AsNoTracking().Where(s => s.Board == PredictionBoard.Correct).OrderBy(s => s.Rank).ToListAsync());
        podium.First().Should().Match<PredictionStandingEntity>(s => s.UserId == 101 && s.CorrectCount == 1 && s.SettledCount == 1);
    }
}
