using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
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
    int? DailyClaimedCoins,
    DateTimeOffset NextDaily);

/// <summary>One leaderboard row (both boards), with the member's display name snapshot.</summary>
public sealed record StandingRow(int Rank, UserId User, long TotalMinor, int CorrectCount, int SettledCount, string? DisplayName = null);

/// <summary>One row of /ongoru tahminlerim.</summary>
public sealed record MyEntryRow(long PredictionId, string Title, string OutcomeLabel, int OddsX100, long StakeMinor, long PotentialPayoutMinor,
    PredictionEntryStatus Status, long? PayoutMinor, PredictionStatus PredictionStatus);

/// <summary>
/// What closing a tournament froze: its final numbers and both podiums with the names at closing (the announcement is
/// rendered from this, never from live wallets).
/// </summary>
public sealed record TournamentClosing(int Number, int NextNumber, int Participants, int Predictions, IReadOnlyList<StandingRow> Coins, IReadOnlyList<StandingRow> Correct);

/// <summary>What the end preview shows (eligible participants and the two current podiums).</summary>
public sealed record TournamentEndSummary(int Number, DateTimeOffset StartedAt, int Participants, int Predictions, int Settled, IReadOnlyList<StandingRow> Coins,
    IReadOnlyList<StandingRow> Correct);

/// <summary>
/// The member side of TSQ Öngörü (commands channel only) and the tournament cycle. Wallets are created lazily — by the
/// first entry, published prediction or daily reward in a tournament, never by reading (wallet view, leaderboard) — each
/// with the starting balance exactly once; nothing here lets a member reset their own coins. The daily reward is one claim
/// per guild + member + Türkiye calendar day (not per tournament): the claim row, the amount, the credit and the ledger
/// row commit together, so a double click, two parallel commands or a retried interaction pay once and show the same
/// amount. Leaderboards, the tournament status, the end preview and the frozen podiums all start from ONE eligibility rule
/// (<see cref="PredictionStore.EligibleWallets"/>). Closing a tournament (/ongoru turnuva bitir) is one transaction: every
/// condition is re-checked (the confirmation names ONE tournament id and admin), both podiums are frozen, the tournament
/// closed, the next one opened and the announcement staged in the outbox from the frozen values. New balances are not
/// written over the old wallets: the next tournament simply has no wallets yet, so everyone starts again at the starting
/// balance, and the old tournament's wallets stay as its archive.
/// </summary>
public sealed class PredictionEconomy(
    PredictionStore store,
    PredictionGuards guards,
    PredictionTokens tokens,
    PredictionMessages messages,
    IPredictionRandom random,
    INotificationOutbox outbox,
    IGuildSettingsStore settings,
    IModuleStateStore modules,
    DeploymentPolicy deployment,
    IOptions<PredictionsOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<PredictionEconomy> logger)
{
    public const string KindTournamentClosed = "tournament-closed";
    public const string KindWeeklyLeaderboard = "weekly-leaderboard";

    /// <summary>A staged weekly leaderboard is delivered within this time or dropped (never last week's ranking days later).</summary>
    public static readonly TimeSpan WeeklyLifetime = TimeSpan.FromHours(6);

    /// <summary>The closing announcement is delivered within this time or dropped (never a stale result days later).</summary>
    public static readonly TimeSpan AnnouncementLifetime = TimeSpan.FromHours(12);

    /// <summary>Delivered/finished announcement rows (their payload names the podium) are removed after this.</summary>
    public static readonly TimeSpan AnnouncementRetention = TimeSpan.FromDays(2);

    private PredictionsOptions Options => options.Value;

    public static string SourceKey(long tournamentId) => "tournament:" + tournamentId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The weekly post's outbox source: the guild's week, never a tournament (a tournament change that week posts no second one).</summary>
    public static string WeeklySourceKey(int weekKey) => "weekly:" + weekKey.ToString(CultureInfo.InvariantCulture);

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

    public async Task<PredictionReply> ClaimDailyAsync(ActorContext actor, ChannelId here, string displayName, CancellationToken ct)
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
            var wallet = await store.EnsureWalletAsync(tournament, actor.UserId, now, ct, displayName);
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
    /// Both boards of THIS guild's active tournament, Top 10 each (<see cref="PredictionRanking"/>: eligible = at least one
    /// settled own entry; coins = live wealth; ordered and limited by SQLite), as the public card — plus, for the member who
    /// asked, a private note: their own rank on each board where they are NOT in the Top 10 (nothing when they are on both),
    /// or that they are not ranked yet (no invented rank). The weekly post uses the same boards without any personal note.
    /// </summary>
    public async Task<PredictionReply> LeaderboardAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        var language = await LanguageAsync(actor.GuildId, ct);
        var tournament = await store.Tournaments.AsNoTracking().FirstOrDefaultAsync(t => t.GuildId == actor.GuildId.Value && t.Status == PredictionTournamentStatus.Active, ct);
        var coins = tournament is null ? [] : await CoinBoardAsync(tournament.Id, PredictionRules.LeaderboardSize, ct);
        var correct = tournament is null ? [] : await CorrectBoardAsync(tournament.Id, PredictionRules.LeaderboardSize, ct);
        var mine = tournament is null ? null : await PersonalRankAsync(tournament.Id, actor.UserId, ct);
        var note = mine is null
            ? messages.NotRanked(language)
            : messages.PersonalRank(mine.Coin.Rank > PredictionRules.LeaderboardSize ? mine.Coin : null,
                mine.Correct.Rank > PredictionRules.LeaderboardSize ? mine.Correct : null, language);
        return new PredictionReply(OperationResult.Ok("predictions.leaderboard.title"), messages.Leaderboard(tournament?.Number ?? 1, coins, correct, language), Public: true,
            Private: note);
    }

    /// <summary>
    /// The automatic weekly leaderboard (worker; no interaction): when the week's slot is due (<see cref="WeeklySchedule.Due"/>),
    /// every guild with the module enabled is evaluated ONCE for that week — inside one write transaction the week row is
    /// re-checked, the CURRENT active tournament read (never created), and either the same boards as /ongoru liderlik (same
    /// queries, Top 10, same renderer) staged in the outbox for the commands channel, or — nobody eligible — nothing posted
    /// and the week marked skipped. Row and outbox commit together; the unique (guild, week) row and the outbox key make a
    /// restart, a second process or a retried send post it at most once. A manual /ongoru liderlik neither counts nor blocks.
    /// </summary>
    public async Task<int> PublishWeeklyLeaderboardAsync(CancellationToken ct)
    {
        var weekly = Options.WeeklyLeaderboard;
        if (!weekly.Enabled || weekly.Schedule() is not { } schedule)
            return 0;
        var now = clock.GetUtcNow();
        if (schedule.Due(now) is not { } slot)
            return 0;
        var week = schedule.WeekKey(slot);
        var staged = 0;
        foreach (var guild in await modules.GetGuildsWithModuleEnabledAsync(PredictionsModule.ModuleIdTyped, ct))
        {
            if (!deployment.IsGuildAllowed(guild) || await store.WeeklyBoards.AsNoTracking().AnyAsync(w => w.GuildId == guild.Value && w.WeekKey == week, ct))
                continue;
            var language = await LanguageAsync(guild, ct);
            var decided = await PredictionWrites.RunAsync<PredictionWeeklyBoardEntity?>(store.Db, async () =>
            {
                if (await store.WeeklyBoards.AnyAsync(w => w.GuildId == guild.Value && w.WeekKey == week, ct))
                    return null;
                var tournament = await store.ActiveTournamentAsync(guild, ct);
                var participants = tournament is null ? 0 : await store.EligibleWallets(tournament.Id).CountAsync(ct);
                var row = new PredictionWeeklyBoardEntity
                {
                    GuildId = guild.Value,
                    WeekKey = week,
                    ScheduledAt = slot,
                    EvaluatedAt = now,
                    Status = participants > 0 ? WeeklyBoardStatus.Staged : WeeklyBoardStatus.SkippedNoParticipants,
                    TournamentId = tournament?.Id,
                    Participants = participants,
                };
                store.WeeklyBoards.Add(row);
                if (tournament is not null && participants > 0)
                {
                    var coins = await CoinBoardAsync(tournament.Id, PredictionRules.LeaderboardSize, ct);
                    var correct = await CorrectBoardAsync(tournament.Id, PredictionRules.LeaderboardSize, ct);
                    await outbox.StageAsync(new NotificationRequest(guild, PredictionsModule.ModuleIdTyped, WeeklySourceKey(week), guards.CommandsChannel,
                        KindWeeklyLeaderboard, messages.Leaderboard(tournament.Number, coins, correct, language, weekly: true), now + WeeklyLifetime,
                        delivery.Value.Mode != DeliveryMode.Send), ct);
                }

                await store.Db.SaveChangesAsync(ct);
                return row;
            }, ct);
            if (decided is null)
                continue;
            if (decided.Status == WeeklyBoardStatus.Staged)
                staged++;
            logger.LogInformation("prediction_weekly_leaderboard guild={Guild} week={Week} status={Status} tournament={Tournament} participants={Participants}",
                guild, week, decided.Status, decided.TournamentId, decided.Participants);
        }

        return staged;
    }

    /// <summary>The coin board's first <paramref name="take"/> rows (<see cref="PredictionRanking.CoinOrder"/>, LIMIT in SQLite).</summary>
    public Task<IReadOnlyList<StandingRow>> CoinBoardAsync(long tournamentId, int take, CancellationToken ct) =>
        BoardAsync(PredictionRanking.CoinOrder(store.EligibleWallets(tournamentId).AsNoTracking()), take, ct);

    /// <summary>The correct board's first <paramref name="take"/> rows (<see cref="PredictionRanking.CorrectOrder"/>, LIMIT in SQLite).</summary>
    public Task<IReadOnlyList<StandingRow>> CorrectBoardAsync(long tournamentId, int take, CancellationToken ct) =>
        BoardAsync(PredictionRanking.CorrectOrder(store.EligibleWallets(tournamentId).AsNoTracking()), take, ct);

    private static async Task<IReadOnlyList<StandingRow>> BoardAsync(IOrderedQueryable<PredictionWalletEntity> ordered, int take, CancellationToken ct)
    {
        var rows = await ordered.Take(take).Select(w => new { w.UserId, Total = w.BalanceMinor + w.PendingMinor, w.CorrectCount, w.SettledCount, w.DisplayName }).ToListAsync(ct);
        return rows.Select((r, i) => new StandingRow(i + 1, new UserId(r.UserId), r.Total, r.CorrectCount, r.SettledCount, r.DisplayName)).ToList();
    }

    /// <summary>
    /// The member's own place on both boards in <paramref name="tournamentId"/>, or null when not eligible: one row read for
    /// the member, then per board ONE count of the eligible members before them (the same predicates as the board orders) —
    /// never the whole tournament in memory.
    /// </summary>
    public async Task<PersonalRank?> PersonalRankAsync(long tournamentId, UserId user, CancellationToken ct)
    {
        var eligible = store.EligibleWallets(tournamentId).AsNoTracking();
        var me = await eligible.Where(w => w.UserId == user.Value)
            .Select(w => new PredictionRanking.Standing(w.UserId, w.BalanceMinor + w.PendingMinor, w.CorrectCount, w.SettledCount, w.DisplayName)).FirstOrDefaultAsync(ct);
        if (me is null)
            return null;
        var coin = await eligible.CountAsync(PredictionRanking.CoinAbove(me), ct) + 1;
        var correct = await eligible.CountAsync(PredictionRanking.CorrectAbove(me), ct) + 1;
        return new PersonalRank(new StandingRow(coin, user, me.WealthMinor, me.CorrectCount, me.SettledCount, me.DisplayName),
            new StandingRow(correct, user, me.WealthMinor, me.CorrectCount, me.SettledCount, me.DisplayName));
    }

    public async Task<PredictionReply> TournamentStatusAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if ((guards.InCommandsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return refusal;
        var language = await LanguageAsync(actor.GuildId, ct);
        var tournament = await store.Tournaments.AsNoTracking().FirstOrDefaultAsync(t => t.GuildId == actor.GuildId.Value && t.Status == PredictionTournamentStatus.Active, ct);
        if (tournament is null)
            return new PredictionReply(OperationResult.Ok("predictions.tournament.not_started"), messages.TournamentNotStarted(language), Public: true);
        var participants = await store.EligibleWallets(tournament.Id).CountAsync(ct);
        var predictions = await store.Predictions.AsNoTracking().Where(p => p.TournamentId == tournament.Id && p.Status != PredictionStatus.Abandoned)
            .GroupBy(p => p.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var total = predictions.Sum(p => p.Count);
        var unresolved = predictions.Where(p => p.Key is PredictionStatus.Publishing or PredictionStatus.Open or PredictionStatus.Locked).Sum(p => p.Count);
        return new PredictionReply(OperationResult.Ok("predictions.tournament.title"),
            messages.TournamentStatus(tournament.Number, tournament.StartedAt, participants, total, unresolved, language), Public: true);
    }

    // ---- tournament end ----

    /// <summary>
    /// /ongoru turnuva bitir: administrators (or the owner) only — the creator role is not enough —, in the commands channel
    /// only. Unresolved predictions (publishing, open or locked) block it and are listed; no eligible participant means
    /// nothing to end; otherwise the private preview with a confirmation bound to this tournament id and this admin.
    /// Nothing changes here.
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
        if (await PendingStakesAsync(tournament.Id, ct))
            return OperationResult.Fail(OperationError.Conflict, "predictions.tournament.pending_stakes");
        var summary = await SummaryAsync(tournament, ct);
        if (summary.Participants == 0)
            return OperationResult.Fail(OperationError.Conflict, "predictions.tournament.no_participants");
        var token = tokens.Create(actor, new TournamentEndStep(tournament.Id), PredictionTokens.TournamentEndLifetime);
        return new PredictionReply(OperationResult.Ok("predictions.tournament.end_preview"), messages.TournamentEndPreview(summary, token, language));
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
            if (await PendingStakesAsync(tournament.Id, ct))
                return ("predictions.tournament.pending_stakes", [], null);
            var summary = await SummaryAsync(tournament, ct);
            if (summary.Participants == 0)
                return ("predictions.tournament.no_participants", [], null);

            foreach (var (board, rows) in new[] { (PredictionBoard.Coins, summary.Coins), (PredictionBoard.Correct, summary.Correct) })
            {
                foreach (var row in rows)
                {
                    store.Standings.Add(new PredictionStandingEntity
                    {
                        TournamentId = tournament.Id,
                        Board = board,
                        Rank = row.Rank,
                        UserId = row.User.Value,
                        DisplayName = row.DisplayName,
                        BalanceMinor = row.TotalMinor,
                        CorrectCount = row.CorrectCount,
                        SettledCount = row.SettledCount,
                    });
                }
            }

            tournament.Status = PredictionTournamentStatus.Closed;
            tournament.ClosedAt = now;
            tournament.ClosedByUserId = actor.UserId.Value;
            tournament.FinalParticipantCount = summary.Participants;
            tournament.FinalPredictionCount = summary.Predictions;
            await store.Db.SaveChangesAsync(ct);

            var next = new PredictionTournamentEntity { GuildId = tournament.GuildId, Number = tournament.Number + 1, Status = PredictionTournamentStatus.Active, StartedAt = now };
            store.Tournaments.Add(next);
            await store.Db.SaveChangesAsync(ct);

            var closing = new TournamentClosing(tournament.Number, next.Number, summary.Participants, summary.Predictions, summary.Coins, summary.Correct);
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

    /// <summary>The eligible participants, prediction counts and both podiums of <paramref name="tournament"/> (the preview and the frozen snapshot).</summary>
    private async Task<TournamentEndSummary> SummaryAsync(PredictionTournamentEntity tournament, CancellationToken ct)
    {
        var participants = await store.EligibleWallets(tournament.Id).CountAsync(ct);
        var predictions = await store.Predictions.CountAsync(p => p.TournamentId == tournament.Id && p.Status != PredictionStatus.Abandoned, ct);
        var settled = await store.Predictions.CountAsync(p => p.TournamentId == tournament.Id && p.Status == PredictionStatus.Settled, ct);
        return new TournamentEndSummary(tournament.Number, tournament.StartedAt, participants, predictions, settled,
            await CoinBoardAsync(tournament.Id, PredictionRules.PodiumSize, ct), await CorrectBoardAsync(tournament.Id, PredictionRules.PodiumSize, ct));
    }

    /// <summary>
    /// With nothing unresolved, no wallet can still hold a stake: the frozen podium is live wealth = balance. A pending stake
    /// here breaks that invariant — the end is refused (never a silently wrong final ranking) and logged for the doctor.
    /// </summary>
    private async Task<bool> PendingStakesAsync(long tournamentId, CancellationToken ct)
    {
        var count = await store.Wallets.CountAsync(w => w.TournamentId == tournamentId && w.PendingMinor != 0, ct);
        if (count > 0)
            logger.LogError("TSQ Öngörü tournament {Tournament}: {Count} wallet(s) still hold pending stakes with nothing unresolved; end refused", tournamentId, count);
        return count > 0;
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
