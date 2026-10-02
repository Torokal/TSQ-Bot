using System.Linq.Expressions;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// THE leaderboard definition, shared by /ongoru liderlik, the member's own rank, the weekly post, the tournament status
/// count and the frozen end-of-tournament podium — one place for who is listed and in which order.
/// <list type="bullet">
/// <item><b>Eligible</b>: at least one of the member's OWN entries in this tournament was settled (won or lost) — the wallet's
/// <see cref="PredictionWalletEntity.SettledCount"/>, maintained in the settlement transaction. Creating predictions, open or
/// locked entries, withdrawn entries, cancelled (refunded) predictions, daily rewards or a wallet alone never count; a new
/// tournament starts from zero.</item>
/// <item><b>Live wealth</b> = spendable balance + the principal of entries not settled yet
/// (<see cref="PredictionWalletEntity.PendingMinor"/>: open and locked stakes, kept in the same transaction as every entry,
/// change, withdrawal, settlement and refund). Never a possible payout; a settled, cancelled or withdrawn stake is already
/// back in (or gone from) the balance, so nothing is counted twice.</item>
/// <item><b>Coin order</b>: live wealth ↓, correct ↓, user id ↑. <b>Correct order</b>: correct ↓, then — with at least one
/// correct — the smaller settled count (the higher success rate) first, live wealth ↓, user id ↑ (every eligible member has
/// something settled, so the old "nothing settled comes last" key is constant and was dropped).</item>
/// </list>
/// A member's rank is "how many eligible members come before me" + 1, counted by SQLite with exactly the predicates of
/// the orders above (ordinal ranks, never 14-14-16), so the public Top 10 and the personal rank can never disagree.
/// </summary>
public static class PredictionRanking
{
    public static IQueryable<PredictionWalletEntity> Eligible(IQueryable<PredictionWalletEntity> wallets, long tournamentId) =>
        wallets.Where(w => w.TournamentId == tournamentId && w.SettledCount > 0);

    public static IOrderedQueryable<PredictionWalletEntity> CoinOrder(IQueryable<PredictionWalletEntity> wallets) =>
        wallets.OrderByDescending(w => w.BalanceMinor + w.PendingMinor).ThenByDescending(w => w.CorrectCount).ThenBy(w => w.UserId);

    public static IOrderedQueryable<PredictionWalletEntity> CorrectOrder(IQueryable<PredictionWalletEntity> wallets) =>
        wallets.OrderByDescending(w => w.CorrectCount).ThenBy(w => w.CorrectCount == 0 ? 0 : w.SettledCount)
            .ThenByDescending(w => w.BalanceMinor + w.PendingMinor).ThenBy(w => w.UserId);

    /// <summary>The eligible members before <paramref name="me"/> on the coin board (exactly <see cref="CoinOrder"/>).</summary>
    public static Expression<Func<PredictionWalletEntity, bool>> CoinAbove(Standing me) =>
        w => w.BalanceMinor + w.PendingMinor > me.WealthMinor ||
             (w.BalanceMinor + w.PendingMinor == me.WealthMinor && (w.CorrectCount > me.CorrectCount || (w.CorrectCount == me.CorrectCount && w.UserId < me.UserId)));

    /// <summary>The eligible members before <paramref name="me"/> on the correct board (exactly <see cref="CorrectOrder"/>).</summary>
    public static Expression<Func<PredictionWalletEntity, bool>> CorrectAbove(Standing me)
    {
        // Same correct count: with 0 correct every rate is 0% (the settled count does not matter), otherwise fewer settled first.
        if (me.CorrectCount == 0)
        {
            return w => w.CorrectCount > 0 || (w.CorrectCount == 0 &&
                                               (w.BalanceMinor + w.PendingMinor > me.WealthMinor || (w.BalanceMinor + w.PendingMinor == me.WealthMinor && w.UserId < me.UserId)));
        }

        return w => w.CorrectCount > me.CorrectCount || (w.CorrectCount == me.CorrectCount &&
                                                         (w.SettledCount < me.SettledCount || (w.SettledCount == me.SettledCount &&
                                                             (w.BalanceMinor + w.PendingMinor > me.WealthMinor ||
                                                              (w.BalanceMinor + w.PendingMinor == me.WealthMinor && w.UserId < me.UserId)))));
    }

    /// <summary>The ranking values of one member (what both orders compare).</summary>
    public sealed record Standing(ulong UserId, long WealthMinor, int CorrectCount, int SettledCount, string? DisplayName);
}

/// <summary>The member's own place on both boards (1-based, ordinal), or nothing when not eligible.</summary>
public sealed record PersonalRank(StandingRow Coin, StandingRow Correct);
