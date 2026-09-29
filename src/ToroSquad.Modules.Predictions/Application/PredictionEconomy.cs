using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>A wallet as shown to its owner (a member without a wallet yet sees the starting balance; nothing is created by looking).</summary>
public sealed record WalletView(
    int TournamentNumber,
    long AvailableMinor,
    long PendingMinor,
    int CorrectCount,
    int SettledCount,
    int PendingEntries,
    bool Exists,
    int? DailyClaimedMinorCoins,
    DateTimeOffset NextDaily);

/// <summary>One leaderboard row (both boards).</summary>
public sealed record StandingRow(int Rank, UserId User, long TotalMinor, int CorrectCount, int SettledCount);

/// <summary>One row of /ongoru tahminlerim.</summary>
public sealed record MyEntryRow(long PredictionId, string Title, string OutcomeLabel, int OddsX100, long StakeMinor, long PotentialPayoutMinor,
    PredictionEntryStatus Status, long? PayoutMinor, PredictionStatus PredictionStatus);

/// <summary>What closing a tournament froze: its final numbers and podium (the announcement is rendered from this, never from live wallets).</summary>
public sealed record TournamentClosing(int Number, int NextNumber, int Participants, int Predictions, IReadOnlyList<StandingRow> Podium);

/// <summary>
/// The member side of TSQ Öngörü (commands channel only) and the tournament cycle. Wallets are created lazily — by the
/// first entry or daily reward in a tournament, never by reading (wallet view, leaderboard) — each with the starting
/// balance exactly once. The daily reward is one claim per guild + member + Türkiye calendar day (not per tournament): the
/// claim row, the amount, the credit and the ledger row commit together, so a double click, two parallel commands or a
/// retried interaction pay once and show the same amount. Closing a tournament is one transaction: every condition is
/// re-checked (the confirmation names ONE tournament id and admin), the podium is frozen, the tournament closed, the next
/// one opened and the announcement staged in the outbox from the frozen values. New balances are not written over the old
/// wallets: the next tournament simply has no wallets yet, so everyone starts again at the starting balance, and the old
/// tournament's wallets stay as its archive.
/// </summary>
public sealed class PredictionEconomy(
    PredictionStore store,
    PredictionGuards guards,
    PredictionTokens tokens,
    PredictionMessages messages,
    IPredictionRandom random,
    INotificationOutbox outbox,
    IGuildSettingsStore settings,
    IOptions<PredictionsOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<PredictionEconomy> logger)
{
    public const string KindTournamentClosed = "tournament-closed";

    /// <summary>The closing announcement is delivered within this time or dropped (never a stale result days later).</summary>
    public static readonly TimeSpan AnnouncementLifetime = TimeSpan.FromHours(12);

    /// <summary>Delivered/finished announcement rows (their payload names the podium) are removed after this.</summary>
    public static readonly TimeSpan AnnouncementRetention = TimeSpan.FromDays(2);

    private PredictionsOptions Options => options.Value;

    public static string SourceKey(long tournamentId) => "tournament:" + tournamentId.ToString(CultureInfo.InvariantCulture);

    // ---- wallet ----

    public async Task<PredictionReply> WalletAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        var zone = Zone();
        var now = clock.GetUtcNow();
        var tournament = await store.Tournaments.AsNoTracking().FirstOrDefaultAsync(t => t.GuildId == actor.GuildId.Value && t.Status == PredictionTournamentStatus.Active, ct);
        var wallet = tournament is null
            ? null
            : await store.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.TournamentId == tournament.Id && w.UserId == actor.UserId.Value, ct);
        var pending = wallet is null ? 0 : await store.Entries.AsNoTracking().CountAsync(e => e.WalletId == wallet.Id && e.Status == PredictionEntryStatus.Pending, ct);
        var today = TurkeyCalendar.DayKey(now, zone);
        var claimed = await store.DailyClaims.AsNoTracking().Where(c => c.GuildId == actor.GuildId.Value && c.UserId == actor.UserId.Value && c.LocalDay == today)
            .Select(c => (long?)c.AmountMinor).FirstOrDefaultAsync(ct);
        var view = new WalletView(tournament?.Number ?? 1, wallet?.BalanceMinor ?? Options.InitialBalanceMinor, wallet?.PendingMinor ?? 0, wallet?.CorrectCount ?? 0,
            wallet?.SettledCount ?? 0, pending, wallet is not null, claimed is { } c ? (int)(c / Coins.MinorPerCoin) : null, TurkeyCalendar.NextDayStart(now, zone));
        return new PredictionReply(OperationResult.Ok("predictions.wallet.title"), messages.Wallet(view, await LanguageAsync(actor.GuildId, ct)));
    }

    // ---- daily ----

    public async Task<PredictionReply> ClaimDailyAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        var zone = Zone();
        var now = clock.GetUtcNow();
        var today = TurkeyCalendar.DayKey(now, zone);
        var language = await LanguageAsync(actor.GuildId, ct);
        var next = DiscordText.Timestamp(TurkeyCalendar.NextDayStart(now, zone), 'R');

        var result = await PredictionWrites.RunAsync<(bool New, long Amount, long Balance, bool Ceiling)>(store.Db, async () =>
        {
            var existing = await store.DailyClaims.FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value && c.UserId == actor.UserId.Value && c.LocalDay == today, ct);
            if (existing is not null)
                return (false, existing.AmountMinor, 0, false);

            var tournament = await store.EnsureTournamentAsync(actor.GuildId, now, ct);
            var wallet = await store.EnsureWalletAsync(tournament, actor.UserId, now, ct);
            var amount = Coins.FromCoins(random.NextInclusive(Options.DailyMinCoins, Options.DailyMaxCoins));
            if ((Int128)wallet.BalanceMinor + wallet.PendingMinor + amount > Coins.WalletCeilingMinor)
                return (false, 0, 0, true);
            var claim = new PredictionDailyClaimEntity
            {
                GuildId = actor.GuildId.Value,
                UserId = actor.UserId.Value,
                LocalDay = today,
                AmountMinor = amount,
                TournamentId = tournament.Id,
                ClaimedAt = now,
            };
            store.DailyClaims.Add(claim);
            wallet.BalanceMinor = Coins.Add(wallet.BalanceMinor, amount);
            wallet.UpdatedAt = now;
            await store.Db.SaveChangesAsync(ct);
            store.Book(wallet, PredictionLedgerKind.Daily, amount, PredictionStore.Key("daily", 'c', claim.Id), now);
            await store.Db.SaveChangesAsync(ct);
            return (true, amount, wallet.BalanceMinor, false);
        }, ct);

        if (result.Ceiling)
            return OperationResult.Fail(OperationError.Conflict, "predictions.daily.ceiling");
        if (!result.New)
            return OperationResult.Fail(OperationError.Conflict, "predictions.daily.already", Coins.Format(result.Amount, language), next);
        logger.LogInformation("prediction_daily guild={Guild} user={User} day={Day} amount={Amount}", actor.GuildId, actor.UserId, today, result.Amount);
        return OperationResult.Ok("predictions.daily.done", Coins.Format(result.Amount, language), Coins.Format(result.Balance, language), next);
    }

    // ---- my entries ----

    public async Task<PredictionReply> MyEntriesAsync(ActorContext actor, ChannelId here, int page, CancellationToken ct)
    {
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        var language = await LanguageAsync(actor.GuildId, ct);
        var tournament = await store.Tournaments.AsNoTracking().FirstOrDefaultAsync(t => t.GuildId == actor.GuildId.Value && t.Status == PredictionTournamentStatus.Active, ct);
        if (tournament is null)
            return new PredictionReply(OperationResult.Ok("predictions.mine.empty"), messages.MyEntries(1, [], 0, 0, language));

        var query = store.Entries.AsNoTracking().Where(e => e.TournamentId == tournament.Id && e.UserId == actor.UserId.Value);
        var total = await query.CountAsync(ct);
        var pages = Math.Max(1, (total + PredictionRules.EntriesPerPage - 1) / PredictionRules.EntriesPerPage);
        page = Math.Clamp(page, 0, pages - 1);
        var rows = await query.OrderByDescending(e => e.Id).Skip(page * PredictionRules.EntriesPerPage).Take(PredictionRules.EntriesPerPage)
            .Join(store.Predictions.AsNoTracking(), e => e.PredictionId, p => p.Id, (e, p) => new { e, p.Title, p.Status })
            .Join(store.Outcomes.AsNoTracking(), x => x.e.OutcomeId, o => o.Id, (x, o) => new MyEntryRow(x.e.PredictionId, x.Title, o.Label, x.e.OddsX100,
                x.e.StakeMinor, x.e.PotentialPayoutMinor, x.e.Status, x.e.PayoutMinor, x.Status))
            .ToListAsync(ct);
        rows = [.. rows.OrderByDescending(r => r.PredictionId)];
        return new PredictionReply(OperationResult.Ok("predictions.mine.title"), messages.MyEntries(tournament.Number, rows, page, pages, language));
    }

    // ---- leaderboard / status ----

    /// <summary>
    /// Both boards, computed by SQLite (ORDER BY … LIMIT, never the whole table in memory) from THIS guild's active tournament:
    /// coins = spendable + pending stakes (never a possible win), ties by correct predictions, then user id; correct = settled
    /// correct predictions (one per prediction at most — one entry per prediction), ties by success rate, then total coins,
    /// then user id. Only members with at least one correct prediction are on the second board.
    /// </summary>
    public async Task<PredictionReply> LeaderboardAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        var language = await LanguageAsync(actor.GuildId, ct);
        var tournament = await store.Tournaments.AsNoTracking().FirstOrDefaultAsync(t => t.GuildId == actor.GuildId.Value && t.Status == PredictionTournamentStatus.Active, ct);
        var coins = tournament is null ? [] : await CoinBoardAsync(tournament.Id, PredictionRules.LeaderboardSize, ct);
        var correct = tournament is null ? [] : await CorrectBoardAsync(tournament.Id, PredictionRules.LeaderboardSize, ct);
        return new PredictionReply(OperationResult.Ok("predictions.leaderboard.title"), messages.Leaderboard(tournament?.Number ?? 1, coins, correct, language), Public: true);
    }

    public async Task<IReadOnlyList<StandingRow>> CoinBoardAsync(long tournamentId, int take, CancellationToken ct)
    {
        var rows = await store.Wallets.AsNoTracking().Where(w => w.TournamentId == tournamentId)
            .OrderByDescending(w => w.BalanceMinor + w.PendingMinor).ThenByDescending(w => w.CorrectCount).ThenBy(w => w.UserId)
            .Take(take).Select(w => new { w.UserId, Total = w.BalanceMinor + w.PendingMinor, w.CorrectCount, w.SettledCount }).ToListAsync(ct);
        return rows.Select((r, i) => new StandingRow(i + 1, new UserId(r.UserId), r.Total, r.CorrectCount, r.SettledCount)).ToList();
    }

    public async Task<IReadOnlyList<StandingRow>> CorrectBoardAsync(long tournamentId, int take, CancellationToken ct)
    {
        // Equal correct counts: the smaller settled count is the higher success rate (identical to comparing the rates).
        var rows = await store.Wallets.AsNoTracking().Where(w => w.TournamentId == tournamentId && w.CorrectCount > 0)
            .OrderByDescending(w => w.CorrectCount).ThenBy(w => w.SettledCount).ThenByDescending(w => w.BalanceMinor + w.PendingMinor).ThenBy(w => w.UserId)
            .Take(take).Select(w => new { w.UserId, Total = w.BalanceMinor + w.PendingMinor, w.CorrectCount, w.SettledCount }).ToListAsync(ct);
        return rows.Select((r, i) => new StandingRow(i + 1, new UserId(r.UserId), r.Total, r.CorrectCount, r.SettledCount)).ToList();
    }

    public async Task<PredictionReply> TournamentStatusAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        var language = await LanguageAsync(actor.GuildId, ct);
        var tournament = await store.Tournaments.AsNoTracking().FirstOrDefaultAsync(t => t.GuildId == actor.GuildId.Value && t.Status == PredictionTournamentStatus.Active, ct);
        if (tournament is null)
            return new PredictionReply(OperationResult.Ok("predictions.tournament.not_started"), messages.TournamentNotStarted(language), Public: true);
        var participants = await store.Wallets.AsNoTracking().CountAsync(w => w.TournamentId == tournament.Id, ct);
        var predictions = await store.Predictions.AsNoTracking().Where(p => p.TournamentId == tournament.Id && p.Status != PredictionStatus.Abandoned)
            .GroupBy(p => p.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var total = predictions.Sum(p => p.Count);
        var unresolved = predictions.Where(p => p.Key is PredictionStatus.Publishing or PredictionStatus.Open or PredictionStatus.Locked).Sum(p => p.Count);
        return new PredictionReply(OperationResult.Ok("predictions.tournament.title"),
            messages.TournamentStatus(tournament.Number, tournament.StartedAt, participants, total, unresolved, language), Public: true);
    }

    // ---- tournament end ----

    /// <summary>
    /// /ongoru turnuva bitir: administrators (or the owner) only, in the commands channel. Unresolved predictions (open,
    /// locked or still publishing) block it and are listed; otherwise the private preview (current podium, wallets that
    /// start over) with a confirmation bound to this tournament id and this admin.
    /// </summary>
    public async Task<PredictionReply> PreviewTournamentEndAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        if (!PredictionAccess.CanEndTournament(actor))
            return OperationResult.Fail(OperationError.Forbidden, "predictions.tournament.admin_only");
        var language = await LanguageAsync(actor.GuildId, ct);
        var tournament = await store.Tournaments.AsNoTracking().FirstOrDefaultAsync(t => t.GuildId == actor.GuildId.Value && t.Status == PredictionTournamentStatus.Active, ct);
        if (tournament is null)
            return OperationResult.Fail(OperationError.Conflict, "predictions.tournament.nothing_to_end");
        if (await UnresolvedAsync(tournament.Id, ct) is { Count: > 0 } open)
            return new PredictionReply(OperationResult.Fail(OperationError.Conflict, "predictions.tournament.unresolved"), messages.Unresolved(open, language));
        var participants = await store.Wallets.AsNoTracking().CountAsync(w => w.TournamentId == tournament.Id, ct);
        if (participants == 0)
            return OperationResult.Fail(OperationError.Conflict, "predictions.tournament.no_participants");
        var podium = await CoinBoardAsync(tournament.Id, PredictionRules.PodiumSize, ct);
        var token = tokens.Create(actor, new TournamentEndStep(tournament.Id), PredictionTokens.TournamentEndLifetime);
        return new PredictionReply(OperationResult.Ok("predictions.tournament.end_preview"),
            messages.TournamentEndPreview(tournament.Number, podium, participants, token, language));
    }

    public async Task<PredictionReply> ConfirmTournamentEndAsync(ActorContext actor, ChannelId here, string token, CancellationToken ct)
    {
        if (tokens.Take<TournamentEndStep>(token, actor) is not { } step)
            return OperationResult.Fail(OperationError.Expired, "predictions.confirm.expired");
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        if (!PredictionAccess.CanEndTournament(actor))
            return OperationResult.Fail(OperationError.Forbidden, "predictions.tournament.admin_only");

        var language = await LanguageAsync(actor.GuildId, ct);
        var now = clock.GetUtcNow();
        var result = await PredictionWrites.RunAsync<(string? Error, IReadOnlyList<UnresolvedPrediction> Open, TournamentClosing? Closing)>(store.Db, async () =>
        {
            var tournament = await store.ActiveTournamentAsync(actor.GuildId, ct);
            if (tournament is null || tournament.Id != step.TournamentId)
                return ("predictions.tournament.stale", [], null);
            if (await UnresolvedAsync(tournament.Id, ct) is { Count: > 0 } open)
                return ("predictions.tournament.unresolved", open, null);
            var participants = await store.Wallets.CountAsync(w => w.TournamentId == tournament.Id, ct);
            if (participants == 0)
                return ("predictions.tournament.no_participants", [], null);
            var predictions = await store.Predictions.CountAsync(p => p.TournamentId == tournament.Id && p.Status != PredictionStatus.Abandoned, ct);

            var podium = await CoinBoardAsync(tournament.Id, PredictionRules.PodiumSize, ct);
            foreach (var row in podium)
            {
                store.Standings.Add(new PredictionStandingEntity
                {
                    TournamentId = tournament.Id,
                    Rank = row.Rank,
                    UserId = row.User.Value,
                    BalanceMinor = row.TotalMinor,
                    CorrectCount = row.CorrectCount,
                    SettledCount = row.SettledCount,
                });
            }

            tournament.Status = PredictionTournamentStatus.Closed;
            tournament.ClosedAt = now;
            tournament.ClosedByUserId = actor.UserId.Value;
            tournament.FinalParticipantCount = participants;
            tournament.FinalPredictionCount = predictions;
            await store.Db.SaveChangesAsync(ct);

            var next = new PredictionTournamentEntity { GuildId = tournament.GuildId, Number = tournament.Number + 1, Status = PredictionTournamentStatus.Active, StartedAt = now };
            store.Tournaments.Add(next);
            await store.Db.SaveChangesAsync(ct);

            var closing = new TournamentClosing(tournament.Number, next.Number, participants, predictions, podium);
            await outbox.StageAsync(new NotificationRequest(actor.GuildId, PredictionsModule.ModuleIdTyped, SourceKey(tournament.Id), guards.CommandsChannel,
                KindTournamentClosed, messages.TournamentAnnouncement(closing, language), now + AnnouncementLifetime, delivery.Value.Mode != DeliveryMode.Send), ct);
            await store.Db.SaveChangesAsync(ct);
            return (null, [], closing);
        }, ct);

        if (result.Closing is not { } done)
        {
            return result.Open.Count > 0
                ? new PredictionReply(OperationResult.Fail(OperationError.Conflict, result.Error!), messages.Unresolved(result.Open, language))
                : OperationResult.Fail(OperationError.Conflict, result.Error!);
        }

        logger.LogInformation("tournament_closed {Tournament} guild={Guild} by={User} number={Number} participants={Participants} predictions={Predictions}",
            step.TournamentId, actor.GuildId, actor.UserId, done.Number, done.Participants, done.Predictions);
        return OperationResult.Ok("predictions.tournament.ended", done.Number, done.NextNumber);
    }

    public sealed record UnresolvedPrediction(long Id, string Title, PredictionStatus Status, GuildId Guild, ChannelId Channel, MessageId? Message);

    private async Task<IReadOnlyList<UnresolvedPrediction>> UnresolvedAsync(long tournamentId, CancellationToken ct)
    {
        var rows = await store.Predictions.AsNoTracking()
            .Where(p => p.TournamentId == tournamentId && (p.Status == PredictionStatus.Publishing || p.Status == PredictionStatus.Open || p.Status == PredictionStatus.Locked))
            .OrderBy(p => p.Id).Select(p => new { p.Id, p.Title, p.Status, p.GuildId, p.ChannelId, p.MessageId }).ToListAsync(ct);
        return rows.Select(r => new UnresolvedPrediction(r.Id, r.Title, r.Status, new GuildId(r.GuildId), new ChannelId(r.ChannelId),
            r.MessageId is { } m ? new MessageId(m) : null)).ToList();
    }

    /// <summary>Removes finished announcement rows after <see cref="AnnouncementRetention"/> (their payload lists user ids).</summary>
    public Task<int> PruneAnnouncementsAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - AnnouncementRetention;
        return store.Db.Outbox.Where(o => o.ModuleId == PredictionsModule.ModuleIdValue && o.UpdatedAt < cutoff &&
                                          (o.Status == OutboxStatus.Cancelled || o.Status == OutboxStatus.Expired || o.Status == OutboxStatus.Failed ||
                                           o.Status == OutboxStatus.Simulated || (o.Status == OutboxStatus.Sent && !o.EditPending) ||
                                           (o.Status == OutboxStatus.DeliveryUnknown && o.NextAttemptAt == null)))
            .ExecuteDeleteAsync(ct);
    }

    private static TimeZoneInfo Zone() =>
        GuildTime.TryResolve(TurkeyCalendar.TimeZoneId, out var zone) ? zone : throw new InvalidOperationException("Europe/Istanbul is not available on this host.");

    private async Task<string> LanguageAsync(GuildId guild, CancellationToken ct) => (await settings.GetAsync(guild, ct)).Language;
}
