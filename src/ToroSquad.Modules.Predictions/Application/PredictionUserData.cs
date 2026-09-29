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
/// /privacy for TSQ Öngörü, one guild at a time. Export: the member's wallets per tournament (with the display name
/// snapshot), entries, coin journal, daily claims, podium places, and the predictions they created, settled, cancelled or
/// locked, and tournaments they closed.
/// <para>
/// Deletion does NOT reset the TSQ Öngörü economy: a member's balances, entries, coin journal, daily claims and podium
/// places are game records of a shared competition (every other member's ranking depends on them), and deleting them would
/// hand the member a fresh starting balance and a second daily reward — the "lose, reset, get 1000 again" exploit. They are
/// kept and reported as kept (preview line and a warning after deletion); the stored display-name snapshots (wallets,
/// podiums, predictions created) are removed, and cards show "—" as creator. A real erasure request is handled by the
/// operator outside the game. When the bot leaves a guild the whole module data of that guild is purged with the other
/// modules (<see cref="PurgeGuildAsync"/>).
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
                ["displayName"] = w.DisplayName,
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
        var names = await store.Wallets.CountAsync(w => w.GuildId == g && w.UserId == u && w.DisplayName != null, cancellationToken) +
                    await store.Predictions.CountAsync(p => p.GuildId == g && p.CreatorUserId == u && p.CreatorName != "", cancellationToken) +
                    await Podium(g, u).CountAsync(s => s.DisplayName != null, cancellationToken);
        if (names > 0)
            items.Add(new DeletionPreviewItem("predictions.privacy.names", names));
        var kept = await store.Wallets.CountAsync(w => w.GuildId == g && w.UserId == u, cancellationToken) +
                   await store.Entries.CountAsync(e => e.GuildId == g && e.UserId == u, cancellationToken) +
                   await store.DailyClaims.CountAsync(c => c.GuildId == g && c.UserId == u, cancellationToken);
        if (kept > 0)
            items.Add(new DeletionPreviewItem("predictions.privacy.kept", kept));
        return items;
    }

    public const string KeptWarning =
        "TSQ Öngörü: bakiye, katılım, coin hareketi ve günlük ödül kayıtları oyun bütünlüğü için silinmedi (TSQ Coin sıfırlanamaz); yalnızca kayıtlı görünen adların kaldırıldı.";

    public async Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var (g, u) = (guild.Value, user.Value);
        var changed = await PredictionWrites.RunAsync(store.Db, async () =>
        {
            var n = await store.Wallets.Where(w => w.GuildId == g && w.UserId == u && w.DisplayName != null)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.DisplayName, (string?)null), cancellationToken);
            n += await Podium(g, u).Where(s => s.DisplayName != null).ExecuteUpdateAsync(s => s.SetProperty(x => x.DisplayName, (string?)null), cancellationToken);
            n += await store.Predictions.Where(p => p.GuildId == g && p.CreatorUserId == u && p.CreatorName != "")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatorName, "")
                    .SetProperty(p => p.CardStale, p => p.MessageId != null && !p.CardMissing).SetProperty(p => p.Version, p => p.Version + 1), cancellationToken);
            return n;
        }, cancellationToken);
        var kept = await store.Wallets.AnyAsync(w => w.GuildId == g && w.UserId == u, cancellationToken) ||
                   await store.DailyClaims.AnyAsync(c => c.GuildId == g && c.UserId == u, cancellationToken);
        return new DeletionReport(Module, changed, kept ? [KeptWarning] : []);
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
