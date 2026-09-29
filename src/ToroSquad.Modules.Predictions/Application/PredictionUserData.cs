using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// /privacy for TSQ Öngörü, one guild at a time. Export: the member's wallets per tournament, entries, coin journal, daily
/// claims, podium places, and the predictions they created, settled, cancelled or locked, and tournaments they closed.
/// <para>
/// Deletion (one write transaction, so no settlement can run in between): the member's entries, journal, daily claims,
/// wallets and podium rows are removed. A PENDING entry is removed together with its stake — it is never paid out or
/// refunded later (the wallet it would go to no longer exists) — and the prediction's live entry count and staked total
/// are reduced by it, so the card never shows coins of a deleted member (the card is redrawn). Settled and cancelled
/// predictions keep their historical totals (aggregates, no member data). Predictions the member created keep existing
/// with the creator's id and display name cleared; the member's id is cleared from settled/cancelled/locked-by and from
/// closed-by of tournaments. The leaderboards then simply no longer list the member.
/// </para>
/// <para>
/// Known consequence (reported, no new retention invented): with the wallet and the daily claim gone, the member starts again
/// with the starting balance and may claim today's daily reward again. Keeping an anti-abuse list of deleted ids would be a
/// new retention policy and needs an owner decision.
/// </para>
/// </summary>
public sealed class PredictionUserData(PredictionStore store) : IUserDataContributor
{
    public ModuleId Module => PredictionsModule.ModuleIdTyped;

    public async Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var g = guild.Value;
        var u = user.Value;
        string Time(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

        var wallets = new JsonArray();
        foreach (var w in await store.Wallets.AsNoTracking().Where(w => w.GuildId == g && w.UserId == u).OrderBy(w => w.Id).ToListAsync(cancellationToken))
        {
            var number = await store.Tournaments.AsNoTracking().Where(t => t.Id == w.TournamentId).Select(t => t.Number).FirstOrDefaultAsync(cancellationToken);
            wallets.Add(new JsonObject
            {
                ["tournament"] = number,
                ["balanceUnits"] = w.BalanceMinor,
                ["pendingUnits"] = w.PendingMinor,
                ["correct"] = w.CorrectCount,
                ["settled"] = w.SettledCount,
                ["createdAtUtc"] = Time(w.CreatedAt),
            });
        }

        var entries = new JsonArray();
        foreach (var e in await store.Entries.AsNoTracking().Where(e => e.GuildId == g && e.UserId == u).OrderBy(e => e.Id).ToListAsync(cancellationToken))
        {
            entries.Add(new JsonObject
            {
                ["prediction"] = e.PredictionId,
                ["outcome"] = e.OutcomeId,
                ["stakeUnits"] = e.StakeMinor,
                ["odds"] = Odds.Format(e.OddsX100),
                ["possiblePayoutUnits"] = e.PotentialPayoutMinor,
                ["status"] = e.Status.ToString(),
                ["payoutUnits"] = e.PayoutMinor,
                ["createdAtUtc"] = Time(e.CreatedAt),
            });
        }

        var ledger = new JsonArray();
        foreach (var l in await store.Ledger.AsNoTracking().Where(l => l.GuildId == g && l.UserId == u).OrderBy(l => l.Id).ToListAsync(cancellationToken))
        {
            ledger.Add(new JsonObject
            {
                ["kind"] = l.Kind.ToString(),
                ["amountUnits"] = l.AmountMinor,
                ["balanceAfterUnits"] = l.BalanceAfterMinor,
                ["prediction"] = l.PredictionId,
                ["atUtc"] = Time(l.CreatedAt),
            });
        }

        var daily = new JsonArray();
        foreach (var c in await store.DailyClaims.AsNoTracking().Where(c => c.GuildId == g && c.UserId == u).OrderBy(c => c.Id).ToListAsync(cancellationToken))
            daily.Add(new JsonObject { ["day"] = c.LocalDay, ["amountUnits"] = c.AmountMinor, ["atUtc"] = Time(c.ClaimedAt) });

        var podium = new JsonArray();
        foreach (var s in await Podium(g, u).AsNoTracking().ToListAsync(cancellationToken))
            podium.Add(new JsonObject { ["tournament"] = s.TournamentId, ["rank"] = s.Rank, ["balanceUnits"] = s.BalanceMinor, ["correct"] = s.CorrectCount });

        var created = new JsonArray();
        foreach (var p in await store.Predictions.AsNoTracking().Where(p => p.GuildId == g && p.CreatorUserId == u).OrderBy(p => p.Id)
                     .Select(p => new { p.Id, p.CreatorName, p.CreatedAt }).ToListAsync(cancellationToken))
            created.Add(new JsonObject { ["prediction"] = p.Id, ["displayName"] = p.CreatorName, ["createdAtUtc"] = Time(p.CreatedAt) });

        var managed = new JsonArray();
        foreach (var id in await Managed(g, u).AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(cancellationToken))
            managed.Add(id);

        var closed = new JsonArray();
        foreach (var number in await store.Tournaments.AsNoTracking().Where(t => t.GuildId == g && t.ClosedByUserId == u).Select(t => t.Number).ToListAsync(cancellationToken))
            closed.Add(number);

        return new JsonObject
        {
            ["note"] = "Amounts are in units: 1 TSQ Coin = 100 units.",
            ["wallets"] = wallets,
            ["entries"] = entries,
            ["coinJournal"] = ledger,
            ["dailyRewards"] = daily,
            ["podiumPlaces"] = podium,
            ["predictionsCreated"] = created,
            ["predictionsLockedSettledOrCancelled"] = managed,
            ["tournamentsClosed"] = closed,
        };
    }

    public async Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var (g, u) = (guild.Value, user.Value);
        var items = new List<DeletionPreviewItem>();
        void Add(string key, int count)
        {
            if (count > 0)
                items.Add(new DeletionPreviewItem(key, count));
        }

        Add("predictions.privacy.wallets", await store.Wallets.CountAsync(w => w.GuildId == g && w.UserId == u, cancellationToken));
        Add("predictions.privacy.pending", await store.Entries.CountAsync(e => e.GuildId == g && e.UserId == u && e.Status == PredictionEntryStatus.Pending, cancellationToken));
        Add("predictions.privacy.entries", await store.Entries.CountAsync(e => e.GuildId == g && e.UserId == u && e.Status != PredictionEntryStatus.Pending, cancellationToken));
        Add("predictions.privacy.ledger", await store.Ledger.CountAsync(l => l.GuildId == g && l.UserId == u, cancellationToken));
        Add("predictions.privacy.daily", await store.DailyClaims.CountAsync(c => c.GuildId == g && c.UserId == u, cancellationToken));
        Add("predictions.privacy.created", await store.Predictions.CountAsync(p => p.GuildId == g && p.CreatorUserId == u, cancellationToken));
        return items;
    }

    public async Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var (g, u) = (guild.Value, user.Value);
        var deleted = await PredictionWrites.RunAsync(store.Db, async () =>
        {
            // Pending stakes leave the live totals of their predictions (and the card is redrawn).
            var pending = await store.Entries.Where(e => e.GuildId == g && e.UserId == u && e.Status == PredictionEntryStatus.Pending).ToListAsync(cancellationToken);
            foreach (var group in pending.GroupBy(e => e.PredictionId))
            {
                var prediction = await store.Predictions.FirstAsync(p => p.Id == group.Key, cancellationToken);
                prediction.EntryCount -= group.Count();
                prediction.StakeTotalMinor = Coins.Subtract(prediction.StakeTotalMinor, group.Aggregate(0L, (sum, e) => Coins.Add(sum, e.StakeMinor)));
                PredictionStore.Touch(prediction);
            }

            await store.Db.SaveChangesAsync(cancellationToken);
            var n = await store.Ledger.Where(l => l.GuildId == g && l.UserId == u).ExecuteDeleteAsync(cancellationToken);
            n += await store.Entries.Where(e => e.GuildId == g && e.UserId == u).ExecuteDeleteAsync(cancellationToken);
            n += await store.DailyClaims.Where(c => c.GuildId == g && c.UserId == u).ExecuteDeleteAsync(cancellationToken);
            n += await Podium(g, u).ExecuteDeleteAsync(cancellationToken);
            n += await store.Wallets.Where(w => w.GuildId == g && w.UserId == u).ExecuteDeleteAsync(cancellationToken);
            n += await store.Predictions.Where(p => p.GuildId == g && p.CreatorUserId == u)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatorUserId, 0UL).SetProperty(p => p.CreatorName, "")
                    .SetProperty(p => p.CardStale, p => p.MessageId != null && !p.CardMissing).SetProperty(p => p.Version, p => p.Version + 1), cancellationToken);
            n += await store.Predictions.Where(p => p.GuildId == g && p.SettledByUserId == u).ExecuteUpdateAsync(s => s.SetProperty(p => p.SettledByUserId, (ulong?)null), cancellationToken);
            n += await store.Predictions.Where(p => p.GuildId == g && p.CancelledByUserId == u).ExecuteUpdateAsync(s => s.SetProperty(p => p.CancelledByUserId, (ulong?)null), cancellationToken);
            n += await store.Predictions.Where(p => p.GuildId == g && p.LockedByUserId == u).ExecuteUpdateAsync(s => s.SetProperty(p => p.LockedByUserId, (ulong?)null), cancellationToken);
            n += await store.Tournaments.Where(t => t.GuildId == g && t.ClosedByUserId == u).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClosedByUserId, (ulong?)null), cancellationToken);
            return n;
        }, cancellationToken);
        return new DeletionReport(Module, deleted, []);
    }

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var g = guild.Value;
        return await PredictionWrites.RunAsync(store.Db, async () =>
        {
            var tournaments = store.Tournaments.Where(t => t.GuildId == g).Select(t => t.Id);
            var predictions = store.Predictions.Where(p => p.GuildId == g).Select(p => p.Id);
            var n = await store.Ledger.Where(l => l.GuildId == g).ExecuteDeleteAsync(cancellationToken);
            n += await store.Entries.Where(e => e.GuildId == g).ExecuteDeleteAsync(cancellationToken);
            n += await store.Outcomes.Where(o => predictions.Contains(o.PredictionId)).ExecuteDeleteAsync(cancellationToken);
            n += await store.Predictions.Where(p => p.GuildId == g).ExecuteDeleteAsync(cancellationToken);
            n += await store.DailyClaims.Where(c => c.GuildId == g).ExecuteDeleteAsync(cancellationToken);
            n += await store.Standings.Where(s => tournaments.Contains(s.TournamentId)).ExecuteDeleteAsync(cancellationToken);
            n += await store.Wallets.Where(w => w.GuildId == g).ExecuteDeleteAsync(cancellationToken);
            n += await store.Tournaments.Where(t => t.GuildId == g).ExecuteDeleteAsync(cancellationToken);
            return n;
        }, cancellationToken);
    }

    private IQueryable<PredictionStandingEntity> Podium(ulong guild, ulong user)
    {
        var tournaments = store.Tournaments.Where(t => t.GuildId == guild).Select(t => t.Id);
        return store.Standings.Where(s => s.UserId == user && tournaments.Contains(s.TournamentId));
    }

    private IQueryable<PredictionEntity> Managed(ulong guild, ulong user) =>
        store.Predictions.Where(p => p.GuildId == guild && (p.SettledByUserId == user || p.CancelledByUserId == user || p.LockedByUserId == user));
}
