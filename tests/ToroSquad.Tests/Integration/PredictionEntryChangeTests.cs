using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// A member's own entry after the form submit, on the real SQLite database: ✏️ Tahminimi Değiştir (outcome and stake; only
/// the difference moves, the odds snapshot follows the stored outcome, not enough coins changes nothing), ↩️ Tahminimi
/// Geri Çek (the stake — never a possible payout — comes back; the entry stays as history, keeps the member eligible and
/// takes part in nothing else; entering again reuses the row), both only while the prediction is open by its stored status
/// AND its lock time, and every race against locks, settlements, cancellations, a second change or withdrawal and a new
/// entry — started together on separate connections, no sleeps — ending in one consistent state: no negative wallet, no
/// double refund or payout, never two active entries, every coin in the journal exactly once.
/// </summary>
public sealed class PredictionEntryChangeTests : IAsyncLifetime
{
    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await PredictionTestKit.CreateAsync();

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    private async Task<(long Balance, long Pending)> CoinsAsync(ulong user)
    {
        var wallet = (await _kit.WalletAsync(user))!;
        return (wallet.BalanceMinor / 100, wallet.PendingMinor / 100);
    }

    private async Task<PredictionEntryEntity> EntryAsync(PredictionView prediction, ulong user = 100) =>
        (await _kit.EntriesAsync(prediction.Id)).Single(e => e.UserId == user);

    /// <summary>
    /// The books balance: the wallet is exactly the sum of its journal (each operation once), pending is exactly the active
    /// stakes, no negative amount, and every prediction's card numbers are exactly its non-withdrawn entries.
    /// </summary>
    private async Task BalancedAsync(params ulong[] users)
    {
        foreach (var user in users)
        {
            var wallet = (await _kit.WalletAsync(user))!;
            var ledger = await _kit.LedgerAsync(user);
            var entries = await _kit.Db(db => db.Set<PredictionEntryEntity>().AsNoTracking().Where(e => e.UserId == user).ToListAsync());
            wallet.BalanceMinor.Should().BeGreaterThanOrEqualTo(0);
            ledger.Sum(l => l.AmountMinor).Should().Be(wallet.BalanceMinor, "every coin movement is booked exactly once");
            ledger.Select(l => l.OperationKey).Should().OnlyHaveUniqueItems();
            wallet.PendingMinor.Should().Be(entries.Where(e => e.Status == PredictionEntryStatus.Pending).Sum(e => e.StakeMinor), "pending = the active stakes");
            entries.Count(e => e.Status == PredictionEntryStatus.Pending).Should().BeLessThanOrEqualTo(entries.Select(e => e.PredictionId).Distinct().Count());
        }

        var predictions = await _kit.Db(db => db.Set<PredictionEntity>().AsNoTracking().ToListAsync());
        foreach (var prediction in predictions)
        {
            var entries = await _kit.EntriesAsync(prediction.Id);
            var counted = entries.Where(e => e.Status != PredictionEntryStatus.Withdrawn).ToList();
            (prediction.EntryCount, prediction.StakeTotalMinor).Should().Be((counted.Count, counted.Sum(e => e.StakeMinor)), "the card counts active (and later decided) entries only");
        }
    }

    private async Task<string> CardParticipationAsync(PredictionView prediction)
    {
        await _kit.TickAsync(TimeSpan.FromSeconds(11));
        return Shown(_kit.Card(prediction)).Embed!.Fields.Single(f => f.Name == "👥 Katılım").Value;
    }

    // ---- change ----

    [Fact]
    public async Task A_change_form_opens_prefilled_with_the_active_entry()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.OpenChangeAsync(Member(), prediction)).Reply!.Result.MessageKey.Should().Be("predictions.change.none");
        (await _kit.EnterAsync(Member(), prediction, 3, "150")).Result.MessageKey.Should().Be("predictions.entry.done");

        var form = (await _kit.OpenChangeAsync(Member(), prediction)).Form!;
        (form.IsChange, form.SelectedOutcomeId, form.Amount, form.CurrentStakeMinor, form.AvailableMinor)
            .Should().Be((true, prediction.Outcomes[2].Id, "150", 15_000L, 85_000L));
    }

    [Fact]
    public async Task A_raised_stake_debits_only_the_difference()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();

        var receipt = await _kit.ChangeAsync(Member(), prediction, 1, "150");
        receipt.Result.MessageKey.Should().Be("predictions.change.done");
        receipt.View!.Embed!.Title.Should().Be("✏️ Tahminin güncellendi!");
        receipt.View.Embed.Description.Should().Contain("🪙 Yatırdığın: 150 TSQ Coin").And.Contain("💰 Olası toplam dönüş: 165 TSQ Coin")
            .And.Contain("👛 Kullanılabilir bakiyen: 850 TSQ Coin");
        receipt.View.Buttons!.Select(b => b.Label).Should().Equal("✏️ Tahminimi Değiştir", "↩️ Tahminimi Geri Çek");
        (await CoinsAsync(100)).Should().Be((850L, 150L));
        var entry = await EntryAsync(prediction);
        (entry.StakeMinor, entry.OddsX100, entry.PotentialPayoutMinor, entry.Revision).Should().Be((15_000L, 110, 16_500L, 1));
        (await _kit.LedgerAsync(100)).Select(l => (l.Kind, l.AmountMinor, l.OperationKey)).Should().Equal(
            (PredictionLedgerKind.Initial, 100_000L, "initial:w" + entry.WalletId), (PredictionLedgerKind.Stake, -10_000L, "stake:e" + entry.Id),
            (PredictionLedgerKind.StakeIncrease, -5_000L, "stake-up:e" + entry.Id + ":r1"));
        await BalancedAsync(100);
    }

    [Fact]
    public async Task A_lowered_stake_returns_only_the_difference()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(), prediction, 1, "150")).Result.Succeeded.Should().BeTrue();

        (await _kit.ChangeAsync(Member(), prediction, 1, "100")).Result.MessageKey.Should().Be("predictions.change.done");
        (await CoinsAsync(100)).Should().Be((900L, 100L));
        (await _kit.LedgerAsync(100)).Last().Should().Match<PredictionLedgerEntity>(l => l.Kind == PredictionLedgerKind.StakeDecrease && l.AmountMinor == 5_000);
        await BalancedAsync(100);
    }

    [Fact]
    public async Task Another_outcome_at_the_same_stake_moves_no_coin_and_takes_the_stored_odds_of_the_new_outcome()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        var ledger = (await _kit.LedgerAsync(100)).Count;

        (await _kit.ChangeAsync(Member(), prediction, 3, "100")).Result.MessageKey.Should().Be("predictions.change.done");
        (await CoinsAsync(100)).Should().Be((900L, 100L), "same stake: the wallet does not change");
        var entry = await EntryAsync(prediction);
        (entry.OutcomeId, entry.OddsX100, entry.PotentialPayoutMinor).Should().Be((prediction.Outcomes[2].Id, 310, 31_000L), "the odds are the stored outcome's");
        (await _kit.LedgerAsync(100)).Should().HaveCount(ledger, "no coin moved, nothing to book");

        // The form sends an outcome number only; one of another prediction is refused and changes nothing.
        var other = await _kit.CreatePredictionAsync(title: "Başka bir öngörü başlığı");
        (await _kit.Service(s => s.ChangeEntryAsync(Member(), Predictions, prediction.Id, Id(other.Outcomes[0].Id), "100", Ct))).Result.MessageKey
            .Should().Be("predictions.not_found");
        (await EntryAsync(prediction)).OutcomeId.Should().Be(prediction.Outcomes[2].Id);

        // The same change delivered again (or "changing" to what it already is) moves nothing more.
        (await _kit.SubmitChangeAsync(Member(), prediction, 3, "100")).Result.MessageKey.Should().Be("predictions.change.done");
        (await EntryAsync(prediction)).Revision.Should().Be(1);
        await BalancedAsync(100);
    }

    [Fact]
    public async Task Not_enough_coins_for_the_difference_changes_nothing_at_all()
    {
        var p1 = await _kit.CreatePredictionAsync();
        var p2 = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        (await _kit.EnterAsync(Member(), p1, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(), p2, 1, "880")).Result.Succeeded.Should().BeTrue(); // 20 left
        var before = await EntryAsync(p1);
        var ledger = (await _kit.LedgerAsync(100)).Count;

        var refused = await _kit.ChangeAsync(Member(), p1, 3, "150");
        refused.Result.MessageKey.Should().Be("predictions.change.insufficient");
        var catalog = Unit.PredictionDomainTests.Localizer();
        catalog.Get("tr", refused.Result.MessageKey, [.. refused.Result.Args])
            .Should().Be("Bu değişiklik için 50 TSQ Coin daha gerekiyor, ancak kullanılabilir bakiyen 20 TSQ Coin.");
        (await EntryAsync(p1)).Should().BeEquivalentTo(before, "not even the outcome changes");
        (await CoinsAsync(100)).Should().Be((20L, 980L));
        (await _kit.LedgerAsync(100)).Should().HaveCount(ledger);
        (await _kit.ChangeAsync(Member(), p1, 3, "120")).Result.MessageKey.Should().Be("predictions.change.done", "exactly the balance is enough");
        await BalancedAsync(100);
    }

    [Fact]
    public async Task Change_and_withdraw_are_refused_once_the_prediction_is_locked_settled_or_cancelled()
    {
        var locked = await _kit.CreatePredictionAsync();
        var settled = await _kit.CreatePredictionAsync(title: "Sonuçlanacak öngörü başlığı");
        var cancelled = await _kit.CreatePredictionAsync(title: "İptal edilecek öngörü başlığı");
        foreach (var p in new[] { locked, settled, cancelled })
            (await _kit.EnterAsync(Member(), p, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.LockAsync(Creator(), locked)).Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Admin(), settled, 2)).Result.Succeeded.Should().BeTrue();
        (await _kit.CancelAsync(Creator(), cancelled)).Result.Succeeded.Should().BeTrue();
        var coins = await CoinsAsync(100);

        foreach (var p in new[] { locked, settled, cancelled })
        {
            (await _kit.OpenChangeAsync(Member(), p)).Reply!.Result.MessageKey.Should().Be("predictions.change.locked");
            (await _kit.SubmitChangeAsync(Member(), p, 2, "50")).Result.MessageKey.Should().Be("predictions.change.locked");
            (await _kit.WithdrawAsync(Member(), p)).Result.MessageKey.Should().Be("predictions.change.locked");
        }

        Unit.PredictionDomainTests.Localizer().Get("tr", "predictions.change.locked")
            .Should().Be("Bu öngörü artık kilitlendiği için tahminini değiştiremez veya geri çekemezsin.");
        (await CoinsAsync(100)).Should().Be(coins);
        (await EntryAsync(locked)).Revision.Should().Be(0);
        await BalancedAsync(100);
    }

    [Fact]
    public async Task At_the_lock_time_change_and_withdraw_are_refused_before_any_worker_pass()
    {
        var prediction = await _kit.CreatePredictionAsync(lockAt: "24.09.2026 16:00"); // one hour after T0
        (await _kit.EnterAsync(Member(), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(59));
        (await _kit.OpenChangeAsync(Member(), prediction)).Form.Should().NotBeNull();

        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open, "no worker pass has run");
        (await _kit.SubmitChangeAsync(Member(), prediction, 2, "150")).Result.MessageKey.Should().Be("predictions.change.locked");
        (await _kit.WithdrawAsync(Member(), prediction)).Result.MessageKey.Should().Be("predictions.change.locked");
        (await CoinsAsync(100)).Should().Be((900L, 100L));
    }

    // ---- withdraw ----

    [Fact]
    public async Task Withdrawing_returns_the_stake_never_the_possible_payout_and_a_second_click_returns_nothing()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(), prediction, 3, "100")).Result.Succeeded.Should().BeTrue(); // @3.10: a possible 310

        var withdrawn = await _kit.WithdrawAsync(Member(), prediction);
        withdrawn.Result.MessageKey.Should().Be("predictions.withdraw.done");
        withdrawn.View!.Content.Should().Be("↩️ **Tahminin geri çekildi.**\n\n🪙 100 TSQ Coin bakiyene iade edildi.\n👛 Yeni bakiyen: 1000 TSQ Coin");
        withdrawn.View.Buttons!.Select(b => (b.Label, b.CustomId)).Should().Equal(("🎯 Tekrar Tahmin Yap", PredictionMessages.AgainPrefix + Id(prediction.Id)));
        withdrawn.Public.Should().BeFalse();
        (await CoinsAsync(100)).Should().Be((1000L, 0L), "+100, not +310");
        var entry = await EntryAsync(prediction);
        (entry.Status, entry.Revision).Should().Be((PredictionEntryStatus.Withdrawn, 1));
        (await _kit.LedgerAsync(100)).Last().Should().Match<PredictionLedgerEntity>(l =>
            l.Kind == PredictionLedgerKind.Withdrawal && l.AmountMinor == 10_000 && l.OperationKey == "withdraw:e" + entry.Id + ":r1");

        (await _kit.WithdrawAsync(Member(), prediction)).Result.MessageKey.Should().Be("predictions.withdraw.none");
        (await CoinsAsync(100)).Should().Be((1000L, 0L));
        (await _kit.LedgerAsync(100)).Count(l => l.Kind == PredictionLedgerKind.Withdrawal).Should().Be(1);
        await BalancedAsync(100);
    }

    [Fact]
    public async Task After_withdrawing_the_member_can_enter_again_with_the_same_row_and_never_two_active_entries()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        var first = await EntryAsync(prediction);
        (await _kit.WithdrawAsync(Member(), prediction)).Result.Succeeded.Should().BeTrue();

        var start = await _kit.OpenEntryAsync(Member(), prediction);
        start.Form.Should().NotBeNull("with nothing active, 🎯 Tahmin Yap opens the form again");
        (await _kit.Service(s => s.StartEntryAsync(Member(), Predictions, prediction.Id, null, Ct))).Form.Should().NotBeNull("🎯 Tekrar Tahmin Yap too");
        (await _kit.SubmitEntryAsync(Member(), prediction, 2, "200")).Result.MessageKey.Should().Be("predictions.entry.done");

        var again = (await _kit.EntriesAsync(prediction.Id)).Should().ContainSingle("one row per member and prediction").Subject;
        (again.Id, again.Status, again.OutcomeId, again.StakeMinor, again.OddsX100, again.Revision)
            .Should().Be((first.Id, PredictionEntryStatus.Pending, prediction.Outcomes[1].Id, 20_000L, 230, 2));
        (await CoinsAsync(100)).Should().Be((800L, 200L));
        (await _kit.LedgerAsync(100)).Last().OperationKey.Should().Be("stake:e" + first.Id + ":r2");
        await BalancedAsync(100);
    }

    [Fact]
    public async Task A_withdrawn_entry_gets_no_payout_and_no_cancellation_refund_and_counts_in_no_record()
    {
        var won = await _kit.CreatePredictionAsync();
        var cancelled = await _kit.CreatePredictionAsync(title: "İptal edilecek öngörü başlığı");
        foreach (var p in new[] { won, cancelled })
        {
            (await _kit.EnterAsync(Member(1), p, 1, "100")).Result.Succeeded.Should().BeTrue();
            (await _kit.EnterAsync(Member(2), p, 1, "100")).Result.Succeeded.Should().BeTrue();
            (await _kit.WithdrawAsync(Member(1), p)).Result.Succeeded.Should().BeTrue();
        }

        var settled = await _kit.SettleAsync(Admin(), won, 1);
        settled.Result.MessageKey.Should().Be("predictions.settle.done");
        settled.Result.Args[1].Should().Be(1, "one winner: the withdrawn entry is not one");
        (await _kit.CancelAsync(Creator(), cancelled)).Result.Args[1].Should().Be(1, "one refund: the withdrawn entry is not one");

        var one = (await _kit.WalletAsync(1))!;
        (one.BalanceMinor, one.PendingMinor, one.SettledCount, one.CorrectCount).Should().Be((100_000L, 0L, 0, 0), "no payout, no refund, no record");
        (await EntryAsync(won, 1)).Status.Should().Be(PredictionEntryStatus.Withdrawn);
        (await EntryAsync(cancelled, 1)).Status.Should().Be(PredictionEntryStatus.Withdrawn);
        (await _kit.LedgerAsync(1)).Should().NotContain(l => l.Kind == PredictionLedgerKind.Payout || l.Kind == PredictionLedgerKind.Refund);
        var two = (await _kit.WalletAsync(2))!;
        (two.BalanceMinor, two.SettledCount, two.CorrectCount).Should().Be((101_000L, 1, 1), "90 + 110 payout (won) + 100 refund (cancelled) … = 1010");
        await BalancedAsync(1, 2);
    }

    [Fact]
    public async Task A_withdrawn_entry_makes_nobody_eligible_and_withdrawn_stakes_are_not_wealth()
    {
        await _kit.ParticipateAsync(2); // member 2: one settled entry (lost 1 coin) — eligible, 999
        var p1 = await _kit.CreatePredictionAsync();
        var p2 = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        (await _kit.EnterAsync(Member(1), p1, 1, "300")).Result.Succeeded.Should().BeTrue();
        (await _kit.WithdrawAsync(Member(1), p1)).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), p1, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.WithdrawAsync(Member(2), p1)).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), p2, 1, "200")).Result.Succeeded.Should().BeTrue(); // available 799 + active 200 (+100 withdrawn history)

        var board = await _kit.Economy(e => e.LeaderboardAsync(Member(50), Commands, Ct));
        var coins = board.View!.Embed!.Fields[0].Value;
        coins.Should().NotContain("<@1>", "a withdrawn entry is no settled prediction");
        coins.Should().Be("🥇 <@2> — **999 TSQ Coin**", "799 available + 200 active; the withdrawn 100 is not counted twice");
        (await _kit.Economy(e => e.LeaderboardAsync(Member(1), Commands, Ct))).Private!.Content.Should().StartWith("Henüz liderlik sıralamasında değilsin.");

        var mine = await _kit.Economy(e => e.MyEntriesAsync(Member(2), Commands, 0, Ct));
        mine.View!.Embed!.Description.Should().Contain("→ ↩️ Geri çekildi").And.Contain("→ 🟢 Aktif (kazanırsa 220 TSQ Coin)");
    }

    [Fact]
    public async Task The_card_counts_only_active_entries_without_a_new_public_message()
    {
        var prediction = await _kit.CreatePredictionAsync();
        foreach (var user in new ulong[] { 1, 2, 3 })
            (await _kit.EnterAsync(Member(user), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await CardParticipationAsync(prediction)).Should().Be("3 katılımcı\n🪙 300 TSQ Coin yatırıldı");

        (await _kit.WithdrawAsync(Member(3), prediction)).Result.Succeeded.Should().BeTrue();
        (await CardParticipationAsync(prediction)).Should().Be("2 katılımcı\n🪙 200 TSQ Coin yatırıldı");
        (await _kit.ChangeAsync(Member(2), prediction, 2, "100")).Result.Succeeded.Should().BeTrue();
        (await CardParticipationAsync(prediction)).Should().Be("2 katılımcı\n🪙 200 TSQ Coin yatırıldı", "another outcome, same stake");
        (await _kit.ChangeAsync(Member(1), prediction, 1, "150")).Result.Succeeded.Should().BeTrue();
        (await CardParticipationAsync(prediction)).Should().Be("2 katılımcı\n🪙 250 TSQ Coin yatırıldı");
        (await _kit.EnterAsync(Member(3), prediction, 3, "50")).Result.Succeeded.Should().BeTrue();
        (await CardParticipationAsync(prediction)).Should().Be("3 katılımcı\n🪙 300 TSQ Coin yatırıldı");

        _kit.Transport.Messages.Should().ContainSingle("changes and withdrawals edit the card; nothing new is posted");
        await BalancedAsync(1, 2, 3);
    }

    // ---- races (real SQLite, separate connections, started together) ----

    [Fact]
    public async Task Change_or_withdraw_racing_a_lock_either_finishes_before_it_or_is_refused()
    {
        var a = await _kit.CreatePredictionAsync();
        var b = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        (await _kit.EnterAsync(Member(1), a, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), b, 1, "100")).Result.Succeeded.Should().BeTrue();

        var changeRace = await _kit.TogetherAsync<object>(async () => await _kit.SubmitChangeAsync(Member(1), a, 2, "150"), async () => await _kit.Service(s => s.LockAsync(Creator(), Predictions, a.Id, Ct)));
        var withdrawRace = await _kit.TogetherAsync<object>(async () => await _kit.WithdrawAsync(Member(2), b), async () => await _kit.Service(s => s.LockAsync(Creator(), Predictions, b.Id, Ct)));

        (((OperationResult)changeRace[1]).MessageKey, ((OperationResult)withdrawRace[1]).MessageKey).Should().Be(("predictions.lock.done", "predictions.lock.done"));
        var change = ((PredictionReply)changeRace[0]).Result.MessageKey;
        change.Should().BeOneOf("predictions.change.done", "predictions.change.locked");
        (await CoinsAsync(1)).Should().Be(change == "predictions.change.done" ? (850L, 150L) : (900L, 100L));
        var withdraw = ((PredictionReply)withdrawRace[0]).Result.MessageKey;
        withdraw.Should().BeOneOf("predictions.withdraw.done", "predictions.change.locked");
        (await CoinsAsync(2)).Should().Be(withdraw == "predictions.withdraw.done" ? (1000L, 0L) : (900L, 100L));
        await BalancedAsync(1, 2);
    }

    [Fact]
    public async Task Change_or_withdraw_racing_a_settlement_never_pays_and_refunds_the_same_stake()
    {
        var a = await _kit.CreatePredictionAsync();
        var b = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        (await _kit.EnterAsync(Member(1), a, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), b, 1, "100")).Result.Succeeded.Should().BeTrue();

        var changeRace = await _kit.TogetherAsync(() => _kit.SubmitChangeAsync(Member(1), a, 1, "200"), () => _kit.ConfirmSettleAsync(Admin(), a, 1));
        var withdrawRace = await _kit.TogetherAsync(() => _kit.WithdrawAsync(Member(2), b), () => _kit.ConfirmSettleAsync(Admin(), b, 1));

        changeRace[1].Result.MessageKey.Should().Be("predictions.settle.done");
        (await CoinsAsync(1)).Should().Be(changeRace[0].Result.MessageKey == "predictions.change.done" ? (1020L, 0L) : (1010L, 0L), "paid on the stake that was active when settled");
        withdrawRace[1].Result.MessageKey.Should().BeOneOf("predictions.settle.done", "predictions.settle.done_nobody");
        var ledger = await _kit.LedgerAsync(2);
        (ledger.Count(l => l.Kind == PredictionLedgerKind.Withdrawal) + ledger.Count(l => l.Kind == PredictionLedgerKind.Payout)).Should().Be(1, "either the refund or the payout, never both");
        (await CoinsAsync(2)).Should().Be(withdrawRace[0].Result.MessageKey == "predictions.withdraw.done" ? (1000L, 0L) : (1010L, 0L));
        await BalancedAsync(1, 2);
    }

    [Fact]
    public async Task Change_or_withdraw_racing_a_cancellation_returns_the_stake_exactly_once()
    {
        var a = await _kit.CreatePredictionAsync();
        var b = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        (await _kit.EnterAsync(Member(1), a, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), b, 1, "100")).Result.Succeeded.Should().BeTrue();
        var tokenA = (await _kit.CancelTokenAsync(Creator(), a))!;
        var tokenB = (await _kit.CancelTokenAsync(Creator(), b))!;

        var changeRace = await _kit.TogetherAsync(() => _kit.SubmitChangeAsync(Member(1), a, 2, "250"), () => _kit.Service(s => s.ConfirmCancelAsync(Creator(), Predictions, tokenA, Ct)));
        var withdrawRace = await _kit.TogetherAsync(() => _kit.WithdrawAsync(Member(2), b), () => _kit.Service(s => s.ConfirmCancelAsync(Creator(), Predictions, tokenB, Ct)));

        changeRace[1].Result.MessageKey.Should().Be("predictions.cancel.done");
        withdrawRace[1].Result.MessageKey.Should().Be("predictions.cancel.done");
        (await CoinsAsync(1)).Should().Be((1000L, 0L), "whichever came first, the whole stake is back once");
        (await CoinsAsync(2)).Should().Be((1000L, 0L));
        var ledger = await _kit.LedgerAsync(2);
        (ledger.Count(l => l.Kind == PredictionLedgerKind.Withdrawal) + ledger.Count(l => l.Kind == PredictionLedgerKind.Refund)).Should().Be(1);
        await BalancedAsync(1, 2);
    }

    [Fact]
    public async Task Two_simultaneous_withdrawals_or_changes_act_once()
    {
        var a = await _kit.CreatePredictionAsync();
        var b = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        (await _kit.EnterAsync(Member(1), a, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), b, 1, "100")).Result.Succeeded.Should().BeTrue();

        var withdrawals = await _kit.TogetherAsync(() => _kit.WithdrawAsync(Member(1), a), () => _kit.WithdrawAsync(Member(1), a));
        withdrawals.Select(r => r.Result.MessageKey).Should().BeEquivalentTo("predictions.withdraw.done", "predictions.withdraw.none");
        (await CoinsAsync(1)).Should().Be((1000L, 0L), "one refund");

        var changes = await _kit.TogetherAsync(() => _kit.SubmitChangeAsync(Member(2), b, 2, "150"), () => _kit.SubmitChangeAsync(Member(2), b, 2, "150"));
        changes.Should().OnlyContain(r => r.Result.MessageKey == "predictions.change.done");
        (await CoinsAsync(2)).Should().Be((850L, 150L), "the difference is taken once");
        (await _kit.LedgerAsync(2)).Count(l => l.Kind == PredictionLedgerKind.StakeIncrease).Should().Be(1);

        var different = await _kit.TogetherAsync(() => _kit.SubmitChangeAsync(Member(2), b, 1, "300"), () => _kit.SubmitChangeAsync(Member(2), b, 3, "50"));
        different.Should().OnlyContain(r => r.Result.MessageKey == "predictions.change.done");
        var entry = await EntryAsync(b, 2);
        (entry.StakeMinor, entry.OutcomeId).Should().BeOneOf((30_000L, b.Outcomes[0].Id), (5_000L, b.Outcomes[2].Id));
        await BalancedAsync(1, 2);
    }

    [Fact]
    public async Task A_change_racing_a_new_entry_attempt_never_makes_a_second_active_entry()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();

        var results = await _kit.TogetherAsync(() => _kit.SubmitChangeAsync(Member(), prediction, 2, "150"), () => _kit.SubmitEntryAsync(Member(), prediction, 3, "400"));
        results[0].Result.MessageKey.Should().Be("predictions.change.done");
        results[1].Result.MessageKey.Should().Be("predictions.entry.already");
        (await _kit.EntriesAsync(prediction.Id)).Should().ContainSingle();
        (await CoinsAsync(100)).Should().Be((850L, 150L));

        var withdrawRace = await _kit.TogetherAsync(() => _kit.WithdrawAsync(Member(), prediction), () => _kit.SubmitEntryAsync(Member(), prediction, 3, "400"));
        withdrawRace[0].Result.MessageKey.Should().Be("predictions.withdraw.done");
        var row = (await _kit.EntriesAsync(prediction.Id)).Should().ContainSingle().Subject;
        (await CoinsAsync(100)).Should().Be(row.Status == PredictionEntryStatus.Pending ? (600L, 400L) : (1000L, 0L), "entering again after the withdrawal is fine; never two");
        await BalancedAsync(100);
    }
}
