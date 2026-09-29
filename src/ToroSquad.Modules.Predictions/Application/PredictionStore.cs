using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// Shared reads and the two lazy creations every economic path needs. <see cref="EnsureTournamentAsync"/> and
/// <see cref="EnsureWalletAsync"/> must run INSIDE a <see cref="PredictionWrites"/> transaction: there the write lock is
/// held, so "none yet → create" happens exactly once (and the partial unique index on the active tournament and the unique
/// wallet index are the database's own backstop). A wallet starts with the configured balance, booked as its
/// "initial:w{id}" ledger row — so restarts, repeated commands or leaving and rejoining the server never grant it again.
/// </summary>
public sealed class PredictionStore(ToroDbContext db, IOptions<PredictionsOptions> options)
{
    public DbSet<PredictionTournamentEntity> Tournaments => db.Set<PredictionTournamentEntity>();
    public DbSet<PredictionWalletEntity> Wallets => db.Set<PredictionWalletEntity>();
    public DbSet<PredictionEntity> Predictions => db.Set<PredictionEntity>();
    public DbSet<PredictionOutcomeEntity> Outcomes => db.Set<PredictionOutcomeEntity>();
    public DbSet<PredictionEntryEntity> Entries => db.Set<PredictionEntryEntity>();
    public DbSet<PredictionLedgerEntity> Ledger => db.Set<PredictionLedgerEntity>();
    public DbSet<PredictionDailyClaimEntity> DailyClaims => db.Set<PredictionDailyClaimEntity>();
    public DbSet<PredictionStandingEntity> Standings => db.Set<PredictionStandingEntity>();

    public ToroDbContext Db => db;

    public static string Key(string kind, char prefix, long id) => kind + ":" + prefix + id.ToString(CultureInfo.InvariantCulture);

    public Task<PredictionTournamentEntity?> ActiveTournamentAsync(GuildId guild, CancellationToken ct) =>
        Tournaments.FirstOrDefaultAsync(t => t.GuildId == guild.Value && t.Status == PredictionTournamentStatus.Active, ct);

    /// <summary>The active tournament, created as number 1 (or the next number) when the guild has none. Transaction only.</summary>
    public async Task<PredictionTournamentEntity> EnsureTournamentAsync(GuildId guild, DateTimeOffset now, CancellationToken ct)
    {
        if (await ActiveTournamentAsync(guild, ct) is { } active)
            return active;
        var last = await Tournaments.Where(t => t.GuildId == guild.Value).MaxAsync(t => (int?)t.Number, ct) ?? 0;
        var created = new PredictionTournamentEntity { GuildId = guild.Value, Number = last + 1, Status = PredictionTournamentStatus.Active, StartedAt = now };
        Tournaments.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>The member's wallet in <paramref name="tournament"/>, created with the starting balance on first use. Transaction only.</summary>
    public async Task<PredictionWalletEntity> EnsureWalletAsync(PredictionTournamentEntity tournament, UserId user, DateTimeOffset now, CancellationToken ct)
    {
        if (await Wallets.FirstOrDefaultAsync(w => w.TournamentId == tournament.Id && w.UserId == user.Value, ct) is { } wallet)
            return wallet;
        var initial = options.Value.InitialBalanceMinor;
        wallet = new PredictionWalletEntity
        {
            TournamentId = tournament.Id,
            GuildId = tournament.GuildId,
            UserId = user.Value,
            BalanceMinor = initial,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Wallets.Add(wallet);
        await db.SaveChangesAsync(ct);
        Ledger.Add(new PredictionLedgerEntity
        {
            GuildId = tournament.GuildId,
            TournamentId = tournament.Id,
            WalletId = wallet.Id,
            UserId = user.Value,
            Kind = PredictionLedgerKind.Initial,
            AmountMinor = initial,
            BalanceAfterMinor = initial,
            OperationKey = Key("initial", 'w', wallet.Id),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return wallet;
    }

    public void Book(PredictionWalletEntity wallet, PredictionLedgerKind kind, long amount, string operationKey, DateTimeOffset now, long? predictionId = null, long? entryId = null) =>
        Ledger.Add(new PredictionLedgerEntity
        {
            GuildId = wallet.GuildId,
            TournamentId = wallet.TournamentId,
            WalletId = wallet.Id,
            UserId = wallet.UserId,
            Kind = kind,
            AmountMinor = amount,
            BalanceAfterMinor = wallet.BalanceMinor,
            OperationKey = operationKey,
            PredictionId = predictionId,
            EntryId = entryId,
            CreatedAt = now,
        });

    public async Task<PredictionView?> ViewAsync(long id, CancellationToken ct)
    {
        var prediction = await Predictions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return prediction is null ? null : await ViewAsync(prediction, ct);
    }

    public async Task<PredictionView> ViewAsync(PredictionEntity p, CancellationToken ct)
    {
        var outcomes = await Outcomes.AsNoTracking().Where(o => o.PredictionId == p.Id).OrderBy(o => o.Position)
            .Select(o => new OutcomeView(o.Id, o.Position, o.Label, o.OddsX100)).ToListAsync(ct);
        var number = await Tournaments.AsNoTracking().Where(t => t.Id == p.TournamentId).Select(t => t.Number).FirstOrDefaultAsync(ct);
        return ToView(p, outcomes, number);
    }

    public static PredictionView ToView(PredictionEntity p, IReadOnlyList<OutcomeView> outcomes, int tournamentNumber) => new(
        p.Id, new GuildId(p.GuildId), p.TournamentId, tournamentNumber, new ChannelId(p.ChannelId), p.MessageId is { } m ? new MessageId(m) : null,
        new UserId(p.CreatorUserId), p.CreatorName, p.Title, p.Rules, p.LockAt, p.Status, p.LockReason, p.LockedAt, outcomes, p.EntryCount, p.StakeTotalMinor,
        p.WinningOutcomeId, p.WinnerCount, p.PayoutTotalMinor, p.CancelReason, p.RefundTotalMinor, p.SettledAt, p.CancelledAt, p.CardMissing, p.Version);

    /// <summary>Marks a state change the card must show (and bumps the concurrency token).</summary>
    public static void Touch(PredictionEntity prediction)
    {
        prediction.CardStale = prediction.MessageId is not null && !prediction.CardMissing;
        prediction.CardSyncAttempts = 0;
        prediction.Version++;
    }
}
