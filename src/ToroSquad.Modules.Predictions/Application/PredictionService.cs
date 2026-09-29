using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// The answer to one step: a result (its message key is shown when there is no view) and optionally the message to show —
/// private unless <see cref="Public"/>.
/// </summary>
public sealed record PredictionReply(OperationResult Result, OutgoingMessage? View = null, bool Public = false)
{
    public static implicit operator PredictionReply(OperationResult result) => new(result);
}

/// <summary>
/// What the entry form needs (after every check passed): the outcomes to choose from and the available balance; for
/// ✏️ Tahminimi Değiştir also the active entry's outcome and stake (prefilled, and the form submits as a change).
/// </summary>
public sealed record EntryFormInfo(
    long PredictionId,
    long TournamentId,
    string Title,
    IReadOnlyList<OutcomeView> Outcomes,
    long AvailableMinor,
    long? SelectedOutcomeId = null,
    string? Amount = null,
    long? CurrentStakeMinor = null)
{
    public bool IsChange => CurrentStakeMinor is not null;
}

/// <summary>What 🎯 Tahmin Yap / ✏️ Tahminimi Değiştir lead to: a private answer (a refusal or the active entry) or the form.</summary>
public sealed record EntryStart(PredictionReply? Reply, EntryFormInfo? Form);

/// <summary>
/// The prediction lifecycle. Create: form → private preview → publish (row in Publishing, card posted by the bot, row
/// Open) — never a second card for one draft. Enter: 🎯 Tahmin Yap → form (outcome + amount) → Submit = ONE write transaction
/// that re-checks everything (module, channel, tournament, status, deadline, outcome, existing entry, balance) and stores
/// the entry, the debit, the ledger row and the card numbers together — no second confirmation. While the prediction is
/// open (stored status AND lock time) the member may change the outcome and stake (only the difference moves) or withdraw
/// (the stake comes back), each one transaction too. Lock (card button or deadline
/// sweep), settle and cancel (card buttons with private confirmations) are single transactions too, each re-checking the
/// manager and the stored status first, so a prediction pays
/// out or refunds at most once. Discord is only called after a commit. Only ids, counts and amounts are logged.
/// </summary>
public sealed class PredictionService(
    PredictionStore store,
    PredictionGuards guards,
    PredictionTokens tokens,
    PredictionCards cards,
    PredictionCardSync cardSync,
    PredictionMessages messages,
    IMessageTransport transport,
    IGuildGateway guilds,
    IGuildSettingsStore settings,
    DeploymentPolicy deployment,
    IOptions<PredictionsOptions> options,
    TimeProvider clock,
    ILogger<PredictionService> logger)
{
    /// <summary>A card whose post was uncertain is searched for this long, then the prediction is abandoned (nobody could enter it).</summary>
    public static readonly TimeSpan PublishGrace = TimeSpan.FromMinutes(10);

    /// <summary>Uncertain posts are searched for only after this (the message may appear a moment later).</summary>
    public static readonly TimeSpan PublishRecheckAfter = TimeSpan.FromMinutes(1);

    /// <summary>A replacement for a deleted card is attempted at most this often.</summary>
    public static readonly TimeSpan RepostBackoff = TimeSpan.FromMinutes(10);

    private const int RecentMessages = 50;
    private const int SweepBatch = 50;

    private PredictionsOptions Options => options.Value;

    /// <summary>The odds a creation-form line without odds gets (named in the form).</summary>
    public string DefaultOddsText => Odds.Format(Options.DefaultOddsX100);

    // ---- create ----

    /// <summary>/ongoru yarat: every refusal that does not depend on the form, then a draft bound to this channel and tournament.</summary>
    public async Task<(OperationResult? Refusal, string? DraftId, PredictionFormValues Values)> OpenFormAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if (await PrecheckCreateAsync(actor, here, ct) is { } refusal)
            return (refusal, null, PredictionFormValues.Empty);
        var tournament = await PredictionWrites.RunAsync(store.Db, () => store.EnsureTournamentAsync(actor.GuildId, clock.GetUtcNow(), ct), ct);
        var id = tokens.Create(actor, new FormDraftStep(here, tournament.Id, PredictionFormValues.Empty), PredictionTokens.DraftLifetime);
        return (null, id, PredictionFormValues.Empty);
    }

    /// <summary>The draft's values for the Düzenle button (null: not the caller's valid draft).</summary>
    public FormDraftStep? Draft(string id, ActorContext actor) => tokens.Get<FormDraftStep>(id, actor);

    public void Discard(string? id, ActorContext actor) => tokens.Remove(id, actor);

    /// <summary>
    /// The form was submitted: the typed values are kept in the draft whatever happens; every problem is listed (field, line,
    /// reason) with the way back into the filled form, or the private preview of the card with Yayımla / Düzenle / Vazgeç.
    /// </summary>
    public async Task<PredictionReply> SubmitFormAsync(ActorContext actor, ChannelId here, string draftId, PredictionFormValues values, string creatorName, CancellationToken ct)
    {
        if (!tokens.Update<FormDraftStep>(draftId, actor, d => d with { Values = values }) || tokens.Get<FormDraftStep>(draftId, actor) is not { } draft)
            return OperationResult.Fail(OperationError.Expired, "predictions.form.expired");
        if (await PrecheckCreateAsync(actor, here, ct) is { } refusal)
            return refusal;
        if (draft.Channel != here)
            return OperationResult.Fail(OperationError.Expired, "predictions.form.expired");

        var language = await LanguageAsync(actor.GuildId, ct);
        var (check, view) = await CheckFormAsync(actor, draft, creatorName, ct);
        if (view is null)
            return new PredictionReply(OperationResult.Fail(OperationError.InvalidInput, "predictions.form.invalid"), messages.FormErrors(check.Errors, draftId, language));
        return new PredictionReply(OperationResult.Ok("predictions.form.preview"), messages.FormPreview(cards.Render(view, language, preview: true), check.Input!, draftId, language));
    }

    /// <summary>
    /// Yayımla: the draft is taken (a double click publishes once; the unique publish key is the database's backstop),
    /// everything is checked again — the lock time must still be in the future and the tournament must still be the one the
    /// form was opened in (an old draft never moves into a new tournament) — then the row is stored as Publishing and the
    /// card posted. A card Discord surely did not post removes the row and gives the draft back; an uncertain post is looked
    /// for among the latest messages and otherwise left to the worker — never posted twice.
    /// </summary>
    public async Task<PredictionReply> PublishAsync(ActorContext actor, ChannelId here, string draftId, string creatorName, CancellationToken ct)
    {
        if (tokens.Take<FormDraftStep>(draftId, actor) is not { } draft)
            return OperationResult.Fail(OperationError.Expired, "predictions.form.unavailable");
        if (await PrecheckCreateAsync(actor, here, ct) is { } refusal)
        {
            tokens.Return(draftId, actor, draft, PredictionTokens.DraftLifetime);
            return refusal;
        }

        if (draft.Channel != here)
            return OperationResult.Fail(OperationError.Expired, "predictions.form.expired");

        var language = await LanguageAsync(actor.GuildId, ct);
        var (check, view) = await CheckFormAsync(actor, draft, creatorName, ct);
        if (check.Input is not { } input || view is null)
        {
            tokens.Return(draftId, actor, draft, PredictionTokens.DraftLifetime);
            return new PredictionReply(OperationResult.Fail(OperationError.InvalidInput, "predictions.form.invalid"), messages.FormErrors(check.Errors, draftId, language));
        }

        var now = Truncate(clock.GetUtcNow());
        var stored = await PredictionWrites.RunAsync<(long Id, string? Error)>(store.Db, async () =>
        {
            var tournament = await store.ActiveTournamentAsync(actor.GuildId, ct);
            if (tournament is null || tournament.Id != draft.TournamentId)
                return (0L, "predictions.form.tournament_changed");
            if (await store.Predictions.AnyAsync(p => p.PublishKey == draftId, ct))
                return (0L, "predictions.form.unavailable");
            var prediction = new PredictionEntity
            {
                GuildId = actor.GuildId.Value,
                TournamentId = tournament.Id,
                ChannelId = here.Value,
                CreatorUserId = actor.UserId.Value,
                CreatorName = Cut(creatorName, PredictionCards.CreatorNameMax),
                Title = input.Title,
                Rules = input.Rules,
                LockAt = input.LockAt,
                Status = PredictionStatus.Publishing,
                PublishKey = draftId,
                CreatedAt = now,
            };
            store.Predictions.Add(prediction);
            await store.Db.SaveChangesAsync(ct);
            // The creator plays in this tournament too (a published prediction makes them leaderboard-eligible): their wallet.
            await store.EnsureWalletAsync(tournament, actor.UserId, now, ct, creatorName);
            for (var i = 0; i < input.Outcomes.Count; i++)
                store.Outcomes.Add(new PredictionOutcomeEntity { PredictionId = prediction.Id, Position = i + 1, Label = input.Outcomes[i].Label, OddsX100 = input.Outcomes[i].OddsX100 });
            await store.Db.SaveChangesAsync(ct);
            return (prediction.Id, null);
        }, ct);
        if (stored.Id == 0)
            return OperationResult.Fail(OperationError.Conflict, stored.Error!);

        // The card shows the state it opens in (the row becomes Open when Discord confirms the post); the worker's search for
        // an uncertain post renders exactly the same message, so their fingerprints match.
        var card = cards.Render((await store.ViewAsync(stored.Id, ct))! with { Status = PredictionStatus.Open }, language);
        switch (await PostCardAsync(here, card, now, ct))
        {
            case (CardPost.Posted, var message):
                await AttachAsync(stored.Id, message, ct);
                logger.LogInformation("prediction_published {Prediction} guild={Guild} channel={Channel} message={Message} tournament={Tournament} outcomes={Outcomes} lock={LockAt:O}",
                    stored.Id, actor.GuildId, here, message, draft.TournamentId, input.Outcomes.Count, input.LockAt);
                return OperationResult.Ok("predictions.publish.done", PredictionCards.Number(stored.Id), MessageLink(actor.GuildId, here, message));

            case (CardPost.Refused, _):
                await store.Outcomes.Where(o => o.PredictionId == stored.Id).ExecuteDeleteAsync(ct);
                await store.Predictions.Where(p => p.Id == stored.Id && p.Status == PredictionStatus.Publishing && p.MessageId == null).ExecuteDeleteAsync(ct);
                tokens.Return(draftId, actor, draft, PredictionTokens.DraftLifetime);
                logger.LogWarning("Prediction {Prediction} discarded: Discord refused its card (guild {Guild}, channel {Channel})", stored.Id, actor.GuildId, here);
                return OperationResult.Fail(OperationError.ProviderUnavailable, "predictions.publish.failed");

            default:
                logger.LogWarning("Prediction {Prediction}: posting its card was uncertain; the worker looks for it (guild {Guild}, channel {Channel})", stored.Id, actor.GuildId, here);
                return OperationResult.Fail(OperationError.ProviderUnavailable, "predictions.publish.uncertain", PredictionCards.Number(stored.Id));
        }
    }

    private async Task<OperationResult?> PrecheckCreateAsync(ActorContext actor, ChannelId here, CancellationToken ct)
    {
        if (await guards.CreatorAsync(actor, here, ct) is { } refusal)
            return refusal;
        var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, here, ct);
        if (!access.Exists || !access.IsTextBased)
            return OperationResult.Fail(OperationError.InvalidInput, "predictions.create.channel_type");
        return access.Permissions.Grants(PredictionRules.RequiredChannelPermissions)
            ? null
            : OperationResult.Fail(OperationError.Forbidden, "predictions.create.bot_permissions", (PredictionRules.RequiredChannelPermissions & ~access.Permissions).ToString());
    }

    /// <summary>The form checked against the active tournament's number, plus the card preview (null when the form is refused).</summary>
    private async Task<(PredictionFormCheck Check, PredictionView? View)> CheckFormAsync(ActorContext actor, FormDraftStep draft, string creatorName, CancellationToken ct)
    {
        if (!GuildTime.TryResolve(TurkeyCalendar.TimeZoneId, out var zone))
            return (new PredictionFormCheck(null, [FormError.Of("predictions.form.error.zone")]), null);
        var check = PredictionForm.Parse(draft.Values, Options.DefaultOddsX100, Options.MaxOutcomes, zone, clock.GetUtcNow());
        if (check.Input is not { } input)
            return (check, null);
        var number = await store.Tournaments.AsNoTracking().Where(t => t.Id == draft.TournamentId).Select(t => t.Number).FirstOrDefaultAsync(ct);
        var outcomes = input.Outcomes.Select((o, i) => new OutcomeView(i + 1, i + 1, o.Label, o.OddsX100)).ToList();
        var view = new PredictionView(0, actor.GuildId, draft.TournamentId, number, draft.Channel, null, actor.UserId, Cut(creatorName, PredictionCards.CreatorNameMax),
            input.Title, input.Rules, input.LockAt, PredictionStatus.Open, null, null, outcomes, 0, 0, null, null, null, null, null, null, null, false, 0);
        return cards.Fits(view)
            ? (check, view)
            : (new PredictionFormCheck(null, [FormError.Of("predictions.form.error.card_too_long")]), null);
    }

    private enum CardPost
    {
        Posted,
        Refused,
        Uncertain,
    }

    /// <summary>The card as a new bot message. An ambiguous send is looked for among the latest messages — never sent twice.</summary>
    private async Task<(CardPost Post, MessageId Message)> PostCardAsync(ChannelId channel, OutgoingMessage card, DateTimeOffset attempt, CancellationToken ct)
    {
        switch (await transport.SendAsync(channel, card, ct))
        {
            case SendOutcome.Sent sent:
                return (CardPost.Posted, sent.MessageId);
            case SendOutcome.Ambiguous:
                var probe = new DeliveryProbe(MessageFingerprint.Of(card), attempt, new HashSet<MessageId>());
                return await transport.FindRecentAsync(channel, probe, RecentMessages, ct) is ReconcileOutcome.Found found
                    ? (CardPost.Posted, found.MessageId)
                    : (CardPost.Uncertain, default);
            default:
                // 4xx, 429 and "not connected" on a create: Discord did not create the message.
                return (CardPost.Refused, default);
        }
    }

    /// <summary>Records the card of a Publishing row and opens it (a no-op for any other state or a card already recorded).</summary>
    private async Task AttachAsync(long id, MessageId message, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await store.Predictions.Where(p => p.Id == id && p.Status == PredictionStatus.Publishing && p.MessageId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.MessageId, (ulong?)message.Value).SetProperty(p => p.Status, PredictionStatus.Open)
                .SetProperty(p => p.OpenedAt, now).SetProperty(p => p.CardEditedAt, now).SetProperty(p => p.Version, p => p.Version + 1), ct);
    }

    /// <summary>
    /// A select on one of the bot's own cards whose post confirmation was lost: the card is recorded (and opened, when the
    /// row is still Publishing); an abandoned row gets its card recorded so it is redrawn as closed.
    /// </summary>
    public async Task AttachFromCardAsync(long id, GuildId guild, ChannelId channel, MessageId message, CancellationToken ct)
    {
        var row = await store.Predictions.AsNoTracking().Where(p => p.Id == id && p.GuildId == guild.Value && p.ChannelId == channel.Value)
            .Select(p => new { p.Status, p.MessageId }).FirstOrDefaultAsync(ct);
        if (row is null || row.MessageId is not null)
            return;
        if (row.Status == PredictionStatus.Publishing)
        {
            await AttachAsync(id, message, ct);
            logger.LogInformation("Prediction {Prediction}: card recorded from a click", id);
        }
        else if (row.Status == PredictionStatus.Abandoned)
        {
            await store.Predictions.Where(p => p.Id == id && p.MessageId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.MessageId, (ulong?)message.Value).SetProperty(p => p.CardStale, true).SetProperty(p => p.Version, p => p.Version + 1), ct);
            await cardSync.SafeSyncAsync(id, ct);
        }
    }

    // ---- enter, change, withdraw ----

    /// <summary>
    /// 🎯 Tahmin Yap (on the card, or 🎯 Tekrar Tahmin Yap on a private answer): every check first. A member with an ACTIVE
    /// entry gets it back, read from the database, with ✏️ Tahminimi Değiştir / ↩️ Tahminimi Geri Çek — the card stays the way
    /// back when a private answer is gone; otherwise the entry form opens. Nothing is debited or created here.
    /// </summary>
    public async Task<EntryStart> StartEntryAsync(ActorContext actor, ChannelId here, long predictionId, MessageId? card, CancellationToken ct)
    {
        var (prediction, refusal) = await OpenForEntriesAsync(actor, here, predictionId, card, "predictions.entry.closed", ct);
        if (prediction is null)
            return new EntryStart(refusal!, null);
        var language = await LanguageAsync(actor.GuildId, ct);
        if (await CurrentEntryAsync(prediction, actor, null, language, ct) is { } current)
            return new EntryStart(current, null);
        var available = await AvailableAsync(prediction.TournamentId, actor, ct);
        if (available < Coins.MinStakeMinor)
            return new EntryStart(OperationResult.Fail(OperationError.Conflict, "predictions.entry.no_coins"), null);
        return new EntryStart(null, new EntryFormInfo(predictionId, prediction.TournamentId, prediction.Title, await OutcomesAsync(predictionId, ct), available));
    }

    /// <summary>✏️ Tahminimi Değiştir: the form again, filled with the active entry's outcome and stake (nothing changes yet).</summary>
    public async Task<EntryStart> StartChangeAsync(ActorContext actor, ChannelId here, long predictionId, CancellationToken ct)
    {
        var (prediction, refusal) = await OpenForEntriesAsync(actor, here, predictionId, null, "predictions.change.locked", ct);
        if (prediction is null)
            return new EntryStart(refusal!, null);
        var entry = await store.Entries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.PredictionId == predictionId && e.UserId == actor.UserId.Value && e.Status == PredictionEntryStatus.Pending, ct);
        if (entry is null)
            return new EntryStart(OperationResult.Fail(OperationError.Conflict, "predictions.change.none"), null);
        var available = await AvailableAsync(prediction.TournamentId, actor, ct);
        return new EntryStart(null, new EntryFormInfo(predictionId, prediction.TournamentId, prediction.Title, await OutcomesAsync(predictionId, ct), available,
            entry.OutcomeId, Coins.FormatInput(entry.StakeMinor), entry.StakeMinor));
    }

    /// <summary>
    /// The entry form was submitted: THIS is the confirmation (no second step). Everything is checked again inside ONE write
    /// transaction (module, channel, tournament, status, deadline, outcome, existing entry, balance), then the entry, the
    /// debit, the ledger row and the card numbers are stored together. A member who already has an active entry — a
    /// repeated submit, a second open form — changes nothing and gets that entry back; one who withdrew enters again with the
    /// same row. The unique (prediction, member) index makes a second entry impossible even across processes.
    /// </summary>
    public async Task<PredictionReply> SubmitEntryAsync(ActorContext actor, ChannelId here, long predictionId, string? outcomeValue, string? amountText,
        string displayName, CancellationToken ct)
    {
        var (prediction, refusal) = await OpenForEntriesAsync(actor, here, predictionId, null, "predictions.entry.closed", ct);
        if (prediction is null)
            return refusal!;
        var language = await LanguageAsync(actor.GuildId, ct);
        var (outcomeId, amount, invalid) = ParseEntryInput(outcomeValue, amountText, language);
        if (invalid is not null)
            return invalid;

        var now = clock.GetUtcNow();
        EntryCommit result;
        try
        {
            result = await PredictionWrites.RunAsync(store.Db, async () =>
            {
                var (row, tournament, outcome, error) = await LoadForEntryAsync(actor, here, predictionId, outcomeId, "predictions.entry.closed", now, ct);
                if (error is not null)
                    return EntryCommit.Fail(error);
                var entry = await store.Entries.FirstOrDefaultAsync(e => e.PredictionId == row!.Id && e.UserId == actor.UserId.Value, ct);
                if (entry is { Status: PredictionEntryStatus.Pending })
                    return EntryCommit.Fail("predictions.entry.already");
                if (entry is not (null or { Status: PredictionEntryStatus.Withdrawn }))
                    return EntryCommit.Fail("predictions.entry.closed");

                var wallet = await store.EnsureWalletAsync(tournament!, actor.UserId, now, ct, displayName);
                if (amount > wallet.BalanceMinor)
                    return EntryCommit.Fail("predictions.entry.insufficient", Coins.Format(wallet.BalanceMinor, language));
                var payout = Coins.Payout(amount, outcome!.OddsX100);
                if (await ExceedsCeilingAsync(wallet, -amount, payout, null, ct))
                    return EntryCommit.Fail("predictions.entry.too_large");

                var again = entry is not null;
                if (entry is null)
                {
                    entry = new PredictionEntryEntity
                    {
                        PredictionId = row!.Id,
                        TournamentId = tournament!.Id,
                        WalletId = wallet.Id,
                        GuildId = actor.GuildId.Value,
                        UserId = actor.UserId.Value,
                        CreatedAt = now,
                    };
                    store.Entries.Add(entry);
                }
                else
                {
                    entry.Revision++;
                    entry.UpdatedAt = now;
                }

                entry.OutcomeId = outcome.Id;
                entry.StakeMinor = amount;
                entry.OddsX100 = outcome.OddsX100;
                entry.PotentialPayoutMinor = payout;
                entry.Status = PredictionEntryStatus.Pending;
                wallet.BalanceMinor = Coins.Subtract(wallet.BalanceMinor, amount);
                wallet.PendingMinor = Coins.Add(wallet.PendingMinor, amount);
                wallet.UpdatedAt = now;
                row!.EntryCount++;
                row.StakeTotalMinor = Coins.Add(row.StakeTotalMinor, amount);
                PredictionStore.Touch(row);
                await store.Db.SaveChangesAsync(ct);
                store.Book(wallet, PredictionLedgerKind.Stake, -amount, EntryKey("stake", entry, again), now, row.Id, entry.Id);
                await store.Db.SaveChangesAsync(ct);
                return new EntryCommit(null, [], entry, wallet.BalanceMinor, 0);
            }, ct);
        }
        catch (DbUpdateException ex) when (PredictionWrites.IsUniqueViolation(ex))
        {
            result = EntryCommit.Fail("predictions.entry.already");
        }

        if (result.Error == "predictions.entry.already" && await CurrentEntryAsync(prediction, actor, "predictions.entry.already", language, ct) is { } current)
            return current with { Result = OperationResult.Fail(OperationError.Conflict, "predictions.entry.already") };
        if (result.Error is not null)
            return OperationResult.Fail(OperationError.Conflict, result.Error, result.Args);

        var entryRow = result.Entry!;
        logger.LogInformation("prediction_entry {Prediction} guild={Guild} user={User} outcome={Outcome} stake={Stake} odds={Odds} revision={Revision}",
            entryRow.PredictionId, actor.GuildId, actor.UserId, entryRow.OutcomeId, entryRow.StakeMinor, entryRow.OddsX100, entryRow.Revision);
        await cardSync.RequestAsync(entryRow.PredictionId, ct);
        return await ReceiptAsync(entryRow, result.Balance, "predictions.entry.done", false, language, ct);
    }

    /// <summary>
    /// The change form was submitted (THIS is the confirmation): in ONE write transaction the active entry takes the new
    /// outcome and stake. Only the DIFFERENCE moves (100 → 150 debits 50, 150 → 100 returns 50, same stake nothing); the odds
    /// snapshot is the stored odds of the outcome now chosen. Not enough coins for the difference: nothing changes at all.
    /// The form carries absolute values, so a repeated submit changes nothing more.
    /// </summary>
    public async Task<PredictionReply> ChangeEntryAsync(ActorContext actor, ChannelId here, long predictionId, string? outcomeValue, string? amountText,
        CancellationToken ct)
    {
        var (prediction, refusal) = await OpenForEntriesAsync(actor, here, predictionId, null, "predictions.change.locked", ct);
        if (prediction is null)
            return refusal!;
        var language = await LanguageAsync(actor.GuildId, ct);
        var (outcomeId, amount, invalid) = ParseEntryInput(outcomeValue, amountText, language);
        if (invalid is not null)
            return invalid;

        var now = clock.GetUtcNow();
        var result = await PredictionWrites.RunAsync(store.Db, async () =>
        {
            var (row, _, outcome, error) = await LoadForEntryAsync(actor, here, predictionId, outcomeId, "predictions.change.locked", now, ct);
            if (error is not null)
                return EntryCommit.Fail(error);
            var entry = await store.Entries.FirstOrDefaultAsync(e => e.PredictionId == row!.Id && e.UserId == actor.UserId.Value, ct);
            if (entry is not { Status: PredictionEntryStatus.Pending })
                return EntryCommit.Fail("predictions.change.none");
            var wallet = await store.Wallets.FirstAsync(w => w.Id == entry.WalletId, ct);

            var delta = amount - entry.StakeMinor;
            if (delta > wallet.BalanceMinor)
                return EntryCommit.Fail("predictions.change.insufficient", Coins.Format(delta, language), Coins.Format(wallet.BalanceMinor, language));
            var odds = outcome!.Id == entry.OutcomeId ? entry.OddsX100 : outcome.OddsX100;
            var payout = Coins.Payout(amount, odds);
            if (await ExceedsCeilingAsync(wallet, -delta, payout, entry.Id, ct))
                return EntryCommit.Fail("predictions.entry.too_large");
            if (delta == 0 && outcome.Id == entry.OutcomeId)
                return new EntryCommit(null, [], entry, wallet.BalanceMinor, 0); // already exactly this: nothing to do

            entry.Revision++;
            entry.UpdatedAt = now;
            entry.OutcomeId = outcome.Id;
            entry.OddsX100 = odds;
            entry.StakeMinor = amount;
            entry.PotentialPayoutMinor = payout;
            if (delta != 0)
            {
                wallet.BalanceMinor = Coins.Subtract(wallet.BalanceMinor, delta);
                wallet.PendingMinor = Coins.Add(wallet.PendingMinor, delta);
                wallet.UpdatedAt = now;
                row!.StakeTotalMinor = Coins.Add(row.StakeTotalMinor, delta);
                store.Book(wallet, delta > 0 ? PredictionLedgerKind.StakeIncrease : PredictionLedgerKind.StakeDecrease, -delta,
                    EntryKey(delta > 0 ? "stake-up" : "stake-down", entry, true), now, row.Id, entry.Id);
            }

            PredictionStore.Touch(row!);
            await store.Db.SaveChangesAsync(ct);
            return new EntryCommit(null, [], entry, wallet.BalanceMinor, delta);
        }, ct);
        if (result.Error is not null)
            return OperationResult.Fail(OperationError.Conflict, result.Error, result.Args);

        var changed = result.Entry!;
        logger.LogInformation("prediction_entry_changed {Prediction} guild={Guild} user={User} outcome={Outcome} stake={Stake} delta={Delta} revision={Revision}",
            changed.PredictionId, actor.GuildId, actor.UserId, changed.OutcomeId, changed.StakeMinor, result.Delta, changed.Revision);
        if (result.Delta != 0)
            await cardSync.RequestAsync(changed.PredictionId, ct);
        return await ReceiptAsync(changed, result.Balance, "predictions.change.done", true, language, ct);
    }

    /// <summary>
    /// ↩️ Tahminimi Geri Çek (the click is the decision; no second question): in ONE write transaction the active entry is
    /// withdrawn and its STAKE — never a possible payout — returns to the wallet. The entry stays as history (the member still
    /// took part in this tournament) but no longer counts anywhere: not on the card, not in a settlement or a cancellation,
    /// not in the member's record. A second click finds nothing active and returns nothing.
    /// </summary>
    public async Task<PredictionReply> WithdrawEntryAsync(ActorContext actor, ChannelId here, long predictionId, CancellationToken ct)
    {
        var (prediction, refusal) = await OpenForEntriesAsync(actor, here, predictionId, null, "predictions.change.locked", ct);
        if (prediction is null)
            return refusal!;
        var language = await LanguageAsync(actor.GuildId, ct);
        var now = clock.GetUtcNow();
        var result = await PredictionWrites.RunAsync(store.Db, async () =>
        {
            var (row, _, _, error) = await LoadForEntryAsync(actor, here, predictionId, null, "predictions.change.locked", now, ct);
            if (error is not null)
                return EntryCommit.Fail(error);
            var entry = await store.Entries.FirstOrDefaultAsync(e => e.PredictionId == row!.Id && e.UserId == actor.UserId.Value, ct);
            if (entry is not { Status: PredictionEntryStatus.Pending })
                return EntryCommit.Fail("predictions.withdraw.none");
            var wallet = await store.Wallets.FirstAsync(w => w.Id == entry.WalletId, ct);

            entry.Status = PredictionEntryStatus.Withdrawn;
            entry.Revision++;
            entry.UpdatedAt = now;
            wallet.PendingMinor = Coins.Subtract(wallet.PendingMinor, entry.StakeMinor);
            wallet.BalanceMinor = Coins.Add(wallet.BalanceMinor, entry.StakeMinor);
            wallet.UpdatedAt = now;
            row!.EntryCount--;
            row.StakeTotalMinor = Coins.Subtract(row.StakeTotalMinor, entry.StakeMinor);
            PredictionStore.Touch(row);
            store.Book(wallet, PredictionLedgerKind.Withdrawal, entry.StakeMinor, EntryKey("withdraw", entry, true), now, row.Id, entry.Id);
            await store.Db.SaveChangesAsync(ct);
            return new EntryCommit(null, [], entry, wallet.BalanceMinor, -entry.StakeMinor);
        }, ct);
        if (result.Error is not null)
            return OperationResult.Fail(OperationError.Conflict, result.Error, result.Args);

        var withdrawn = result.Entry!;
        logger.LogInformation("prediction_entry_withdrawn {Prediction} guild={Guild} user={User} refund={Refund} revision={Revision}",
            withdrawn.PredictionId, actor.GuildId, actor.UserId, withdrawn.StakeMinor, withdrawn.Revision);
        await cardSync.RequestAsync(withdrawn.PredictionId, ct);
        return new PredictionReply(OperationResult.Ok("predictions.withdraw.done"),
            messages.Withdrawn(predictionId, withdrawn.StakeMinor, result.Balance, language));
    }

    private sealed record EntryCommit(string? Error, object[] Args, PredictionEntryEntity? Entry, long Balance, long Delta)
    {
        public static EntryCommit Fail(string key, params object[] args) => new(key, args, null, 0, 0);
    }

    /// <summary>The ledger key of an entry operation: the first stake keeps "stake:e3"; every later one carries the revision.</summary>
    private static string EntryKey(string kind, PredictionEntryEntity entry, bool revised) =>
        PredictionStore.Key(kind, 'e', entry.Id) + (revised ? ":r" + entry.Revision.ToString(CultureInfo.InvariantCulture) : "");

    /// <summary>Would the wallet's possible total (spendable + every possible payout) pass the coin ceiling after this change?</summary>
    private async Task<bool> ExceedsCeilingAsync(PredictionWalletEntity wallet, long balanceChange, long payout, long? replacedEntryId, CancellationToken ct)
    {
        var possible = await store.Entries.Where(e => e.WalletId == wallet.Id && e.Status == PredictionEntryStatus.Pending && e.Id != replacedEntryId)
            .SumAsync(e => (long?)e.PotentialPayoutMinor, ct) ?? 0;
        return (Int128)wallet.BalanceMinor + balanceChange + possible + payout > Coins.WalletCeilingMinor;
    }

    /// <summary>The outcome (a number from the select) and the amount, both required; a refusal names what is wrong.</summary>
    private static (long OutcomeId, long Amount, OperationResult? Refusal) ParseEntryInput(string? outcomeValue, string? amountText, string language)
    {
        if (!long.TryParse(outcomeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var outcomeId))
            return (0, 0, OperationResult.Fail(OperationError.InvalidInput, "predictions.entry.outcome_required"));
        var amount = Coins.ParseAmount(amountText);
        if (!amount.Ok)
            return (0, 0, OperationResult.Fail(OperationError.InvalidInput, "predictions.entry.amount_" + AmountErrorKey(amount.Error), Coins.Format(Coins.MinStakeMinor, language)));
        return (outcomeId, amount.Minor, null);
    }

    /// <summary>
    /// Inside the write transaction: the prediction (this guild and channel), open for entries at THIS moment — the stored
    /// status and the lock time itself, never the worker —, in the active tournament, and the chosen outcome of this prediction.
    /// </summary>
    private async Task<(PredictionEntity? Prediction, PredictionTournamentEntity? Tournament, PredictionOutcomeEntity? Outcome, string? Error)> LoadForEntryAsync(
        ActorContext actor, ChannelId here, long predictionId, long? outcomeId, string closedKey, DateTimeOffset now, CancellationToken ct)
    {
        var prediction = await store.Predictions.FirstOrDefaultAsync(p => p.Id == predictionId && p.GuildId == actor.GuildId.Value, ct);
        if (prediction is null || prediction.ChannelId != here.Value)
            return (null, null, null, "predictions.not_found");
        var tournament = await store.ActiveTournamentAsync(actor.GuildId, ct);
        if (!IsOpenForEntries(prediction, now) || tournament is null || tournament.Id != prediction.TournamentId)
            return (null, null, null, closedKey);
        if (outcomeId is not { } chosen)
            return (prediction, tournament, null, null);
        var outcome = await store.Outcomes.FirstOrDefaultAsync(o => o.Id == chosen && o.PredictionId == prediction.Id, ct);
        return outcome is null ? (null, null, null, "predictions.not_found") : (prediction, tournament, outcome, null);
    }

    /// <summary>Before any form or transaction: the prediction as reached (guild, channel, card), open for entries now, in the active tournament.</summary>
    private async Task<(PredictionEntity? Prediction, OperationResult? Refusal)> OpenForEntriesAsync(ActorContext actor, ChannelId here, long predictionId,
        MessageId? card, string closedKey, CancellationToken ct)
    {
        var (prediction, refusal) = await LoadInChannelAsync(actor, here, predictionId, card, ct);
        if (prediction is null)
            return (null, refusal);
        if (prediction.Status == PredictionStatus.Publishing)
            return (null, OperationResult.Fail(OperationError.Conflict, "predictions.entry.publishing"));
        var active = await store.Tournaments.AsNoTracking()
            .AnyAsync(t => t.Id == prediction.TournamentId && t.GuildId == actor.GuildId.Value && t.Status == PredictionTournamentStatus.Active, ct);
        return IsOpenForEntries(prediction, clock.GetUtcNow()) && active ? (prediction, null) : (null, OperationResult.Fail(OperationError.Conflict, closedKey));
    }

    private static bool IsOpenForEntries(PredictionEntity prediction, DateTimeOffset now) =>
        prediction.Status == PredictionStatus.Open && (prediction.LockAt is not { } lockAt || now < lockAt);

    /// <summary>The member's ACTIVE entry of this prediction as a private view with ✏️ / ↩️ (null: none).</summary>
    private async Task<PredictionReply?> CurrentEntryAsync(PredictionEntity prediction, ActorContext actor, string? note, string language, CancellationToken ct)
    {
        var entry = await store.Entries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.PredictionId == prediction.Id && e.UserId == actor.UserId.Value && e.Status == PredictionEntryStatus.Pending, ct);
        if (entry is null)
            return null;
        var label = await store.Outcomes.AsNoTracking().Where(o => o.Id == entry.OutcomeId).Select(o => o.Label).FirstAsync(ct);
        var balance = await store.Wallets.AsNoTracking().Where(w => w.Id == entry.WalletId).Select(w => w.BalanceMinor).FirstAsync(ct);
        return new PredictionReply(OperationResult.Ok("predictions.entry.current"),
            messages.CurrentEntry(prediction.Id, prediction.Title, label, entry.OddsX100, entry.StakeMinor, entry.PotentialPayoutMinor, balance, note, language));
    }

    private async Task<PredictionReply> ReceiptAsync(PredictionEntryEntity entry, long balance, string key, bool changed, string language, CancellationToken ct)
    {
        var title = await store.Predictions.AsNoTracking().Where(p => p.Id == entry.PredictionId).Select(p => p.Title).FirstAsync(ct);
        var label = await store.Outcomes.AsNoTracking().Where(o => o.Id == entry.OutcomeId).Select(o => o.Label).FirstAsync(ct);
        return new PredictionReply(OperationResult.Ok(key),
            messages.EntryReceipt(entry.PredictionId, title, label, entry.OddsX100, entry.StakeMinor, entry.PotentialPayoutMinor, balance, changed, language));
    }

    private Task<List<OutcomeView>> OutcomesAsync(long predictionId, CancellationToken ct) =>
        store.Outcomes.AsNoTracking().Where(o => o.PredictionId == predictionId).OrderBy(o => o.Position)
            .Select(o => new OutcomeView(o.Id, o.Position, o.Label, o.OddsX100)).ToListAsync(ct);

    /// <summary>The member's spendable coins in this tournament (the starting balance when they have no wallet yet).</summary>
    private async Task<long> AvailableAsync(long tournamentId, ActorContext actor, CancellationToken ct) =>
        await store.Wallets.AsNoTracking().Where(w => w.TournamentId == tournamentId && w.UserId == actor.UserId.Value)
            .Select(w => (long?)w.BalanceMinor).FirstOrDefaultAsync(ct) ?? Options.InitialBalanceMinor;

    // ---- manage from the card: lock ----

    /// <summary>🔒 Kilitle on the card: the private "are you sure" (nothing changes yet).</summary>
    public async Task<PredictionReply> PromptLockAsync(ActorContext actor, ChannelId here, long predictionId, MessageId? card, CancellationToken ct)
    {
        var (prediction, refusal) = await ManagedAsync(actor, here, predictionId, card, ct);
        if (prediction is null)
            return refusal!;
        if (prediction.Status != PredictionStatus.Open)
            return StateRefusal(prediction.Status, ManageAction.Lock);
        return new PredictionReply(OperationResult.Ok("predictions.lock.prompt"), messages.LockPrompt(prediction.Id, prediction.Title, await LanguageAsync(actor.GuildId, ct)));
    }

    /// <summary>The lock confirmation: re-checked, then Open → Locked once (a concurrent lock or result finds it changed).</summary>
    public async Task<OperationResult> LockAsync(ActorContext actor, ChannelId here, long predictionId, CancellationToken ct)
    {
        var (prediction, refusal) = await ManagedAsync(actor, here, predictionId, null, ct);
        if (prediction is null)
            return refusal!;
        var now = clock.GetUtcNow();
        var status = await PredictionWrites.RunAsync(store.Db, async () =>
        {
            var row = await store.Predictions.FirstOrDefaultAsync(p => p.Id == prediction.Id, ct);
            if (row is not { Status: PredictionStatus.Open })
                return row?.Status ?? PredictionStatus.Abandoned;
            var tournament = await store.ActiveTournamentAsync(actor.GuildId, ct);
            if (tournament is null || tournament.Id != row.TournamentId)
                return PredictionStatus.Abandoned;
            row.Status = PredictionStatus.Locked;
            row.LockReason = PredictionLockReason.Manual;
            row.LockedAt = now;
            row.LockedByUserId = actor.UserId.Value;
            PredictionStore.Touch(row);
            await store.Db.SaveChangesAsync(ct);
            return PredictionStatus.Open;
        }, ct);
        if (status != PredictionStatus.Open)
            return StateRefusal(status, ManageAction.Lock);

        logger.LogInformation("prediction_locked {Prediction} guild={Guild} by={User}", prediction.Id, actor.GuildId, actor.UserId);
        await cardSync.SafeSyncAsync(prediction.Id, ct);
        return OperationResult.Ok("predictions.lock.done", PredictionCards.Number(prediction.Id));
    }

    /// <summary>Worker: locks every open prediction whose lock time has come (those passed while the bot was down included).</summary>
    public async Task<int> LockDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await store.Predictions.AsNoTracking()
            .Where(p => p.Status == PredictionStatus.Open && p.LockAt != null && p.LockAt <= now)
            .OrderBy(p => p.LockAt).Select(p => new { p.Id, p.GuildId }).Take(SweepBatch).ToListAsync(ct);
        var locked = 0;
        foreach (var item in due.Where(x => deployment.IsGuildAllowed(new GuildId(x.GuildId))))
        {
            var changed = await store.Predictions.Where(p => p.Id == item.Id && p.Status == PredictionStatus.Open)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PredictionStatus.Locked).SetProperty(p => p.LockReason, PredictionLockReason.Deadline)
                    .SetProperty(p => p.LockedAt, p => p.LockAt).SetProperty(p => p.CardStale, p => p.MessageId != null && !p.CardMissing)
                    .SetProperty(p => p.CardSyncAttempts, 0).SetProperty(p => p.Version, p => p.Version + 1), ct);
            if (changed > 0)
            {
                locked++;
                logger.LogInformation("prediction_locked {Prediction} guild={Guild} by=deadline", item.Id, item.GuildId);
            }
        }

        return locked;
    }

    // ---- manage from the card: settle ----

    /// <summary>✅ Sonuçlandır on the card: the private outcome picker (nothing changes until the confirmation).</summary>
    public async Task<PredictionReply> StartSettleAsync(ActorContext actor, ChannelId here, long predictionId, MessageId? card, CancellationToken ct)
    {
        var (prediction, refusal) = await ManagedAsync(actor, here, predictionId, card, ct);
        if (prediction is null)
            return refusal!;
        if (prediction.Status is not (PredictionStatus.Open or PredictionStatus.Locked))
            return StateRefusal(prediction.Status, ManageAction.Settle);
        var view = await store.ViewAsync(prediction, ct);
        return new PredictionReply(OperationResult.Ok("predictions.settle.pick"), messages.SettlePicker(view, null, await LanguageAsync(actor.GuildId, ct)));
    }

    /// <summary>An outcome was picked: the preview (winner, winners/losers, total payout) with the confirmation button.</summary>
    public async Task<PredictionReply> PreviewSettleAsync(ActorContext actor, ChannelId here, long predictionId, string? outcomeValue, CancellationToken ct)
    {
        var (prediction, refusal) = await ManagedAsync(actor, here, predictionId, null, ct);
        if (prediction is null)
            return refusal!;
        if (prediction.Status is not (PredictionStatus.Open or PredictionStatus.Locked))
            return StateRefusal(prediction.Status, ManageAction.Settle);
        var view = await store.ViewAsync(prediction, ct);
        if (!long.TryParse(outcomeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var outcomeId) || view.Outcomes.All(o => o.Id != outcomeId))
            return NotFound();

        var entries = await store.Entries.AsNoTracking().Where(e => e.PredictionId == prediction.Id && e.Status == PredictionEntryStatus.Pending)
            .Select(e => new { e.OutcomeId, e.PotentialPayoutMinor }).ToListAsync(ct);
        var winners = entries.Where(e => e.OutcomeId == outcomeId).ToList();
        var payout = winners.Aggregate(0L, (sum, e) => Coins.Add(sum, e.PotentialPayoutMinor));
        return new PredictionReply(OperationResult.Ok("predictions.settle.preview"),
            messages.SettlePreview(view, outcomeId, winners.Count, entries.Count - winners.Count, payout, await LanguageAsync(actor.GuildId, ct)));
    }

    /// <summary>
    /// The settlement, in ONE transaction: re-checked (manager, status Open or Locked — an open prediction is closed by it —,
    /// the outcome belongs to it, active tournament), then every pending entry is decided: a winner is credited exactly its
    /// stored possible payout (stake × fixed odds, rounded down), a loser gets nothing more (its stake was debited when it
    /// entered); both count towards the member's settled predictions, winners once as correct. Nobody on the winning outcome
    /// is a valid result (no payout, no refund). The unique payout keys forbid a second payment of any entry.
    /// </summary>
    public async Task<PredictionReply> ConfirmSettleAsync(ActorContext actor, ChannelId here, long predictionId, long outcomeId, CancellationToken ct)
    {
        var (managed, refusal) = await ManagedAsync(actor, here, predictionId, null, ct);
        if (managed is null)
            return refusal!;

        var now = clock.GetUtcNow();
        var result = await PredictionWrites.RunAsync<(PredictionStatus Status, int Winners, long Payout, int Entries)>(store.Db, async () =>
        {
            var prediction = await store.Predictions.FirstOrDefaultAsync(p => p.Id == predictionId, ct);
            if (prediction is null)
                return (PredictionStatus.Abandoned, 0, 0L, 0);
            if (prediction.Status is not (PredictionStatus.Open or PredictionStatus.Locked))
                return (prediction.Status, 0, 0L, 0);
            var tournament = await store.ActiveTournamentAsync(actor.GuildId, ct);
            if (tournament is null || tournament.Id != prediction.TournamentId)
                return (PredictionStatus.Abandoned, 0, 0L, 0);
            if (!await store.Outcomes.AnyAsync(o => o.Id == outcomeId && o.PredictionId == prediction.Id, ct))
                return (PredictionStatus.Abandoned, 0, 0L, 0);

            var entries = await store.Entries.Where(e => e.PredictionId == prediction.Id && e.Status == PredictionEntryStatus.Pending).ToListAsync(ct);
            var walletIds = entries.Select(e => e.WalletId).Distinct().ToList();
            var wallets = await store.Wallets.Where(w => walletIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
            var winners = 0;
            var paid = 0L;
            foreach (var entry in entries)
            {
                var wallet = wallets[entry.WalletId];
                wallet.PendingMinor = Coins.Subtract(wallet.PendingMinor, entry.StakeMinor);
                wallet.SettledCount++;
                wallet.UpdatedAt = now;
                entry.SettledAt = now;
                if (entry.OutcomeId == outcomeId)
                {
                    entry.Status = PredictionEntryStatus.Won;
                    entry.PayoutMinor = entry.PotentialPayoutMinor;
                    wallet.BalanceMinor = Coins.Add(wallet.BalanceMinor, entry.PotentialPayoutMinor);
                    wallet.CorrectCount++;
                    store.Book(wallet, PredictionLedgerKind.Payout, entry.PotentialPayoutMinor, PredictionStore.Key("payout", 'e', entry.Id), now, prediction.Id, entry.Id);
                    winners++;
                    paid = Coins.Add(paid, entry.PotentialPayoutMinor);
                }
                else
                {
                    entry.Status = PredictionEntryStatus.Lost;
                    entry.PayoutMinor = 0;
                }
            }

            prediction.Status = PredictionStatus.Settled;
            prediction.WinningOutcomeId = outcomeId;
            prediction.WinnerCount = winners;
            prediction.PayoutTotalMinor = paid;
            prediction.SettledAt = now;
            prediction.SettledByUserId = actor.UserId.Value;
            prediction.LockedAt ??= now;
            PredictionStore.Touch(prediction);
            await store.Db.SaveChangesAsync(ct);
            return (PredictionStatus.Open, winners, paid, entries.Count);
        }, ct);
        if (result.Status != PredictionStatus.Open)
            return StateRefusal(result.Status, ManageAction.Settle);

        logger.LogInformation("prediction_settled {Prediction} guild={Guild} by={User} outcome={Outcome} entries={Entries} winners={Winners} payout={Payout}",
            predictionId, actor.GuildId, actor.UserId, outcomeId, result.Entries, result.Winners, result.Payout);
        await cardSync.SafeSyncAsync(predictionId, ct);
        var language = await LanguageAsync(actor.GuildId, ct);
        return result.Winners == 0
            ? OperationResult.Ok("predictions.settle.done_nobody", PredictionCards.Number(predictionId), result.Entries)
            : OperationResult.Ok("predictions.settle.done", PredictionCards.Number(predictionId), result.Winners, Coins.Format(result.Payout, language));
    }

    // ---- manage from the card: cancel ----

    /// <summary>↩️ İptal / İade on the card: may this member cancel it now? (null: open the reason form).</summary>
    public async Task<OperationResult?> StartCancelAsync(ActorContext actor, ChannelId here, long predictionId, MessageId? card, CancellationToken ct)
    {
        var (prediction, refusal) = await ManagedAsync(actor, here, predictionId, card, ct);
        if (prediction is null)
            return refusal!;
        return prediction.Status is PredictionStatus.Open or PredictionStatus.Locked ? null : StateRefusal(prediction.Status, ManageAction.Cancel);
    }

    /// <summary>The reason was submitted: the private preview (entries, total refund) with the confirmation button.</summary>
    public async Task<PredictionReply> PreviewCancelAsync(ActorContext actor, ChannelId here, long predictionId, string? reason, CancellationToken ct)
    {
        var (prediction, refusal) = await ManagedAsync(actor, here, predictionId, null, ct);
        if (prediction is null)
            return refusal!;
        if (prediction.Status is not (PredictionStatus.Open or PredictionStatus.Locked))
            return StateRefusal(prediction.Status, ManageAction.Cancel);
        var text = PredictionForm.Collapse(reason);
        var length = PredictionForm.Length(text);
        if (length < PredictionRules.CancelReasonMinLength || length > PredictionRules.CancelReasonMaxLength)
            return OperationResult.Fail(OperationError.InvalidInput, "predictions.cancel.reason_length", PredictionRules.CancelReasonMinLength, PredictionRules.CancelReasonMaxLength);
        var view = await store.ViewAsync(prediction, ct);
        var pending = await store.Entries.AsNoTracking().Where(e => e.PredictionId == prediction.Id && e.Status == PredictionEntryStatus.Pending)
            .Select(e => e.StakeMinor).ToListAsync(ct);
        var token = tokens.Create(actor, new CancelStep(prediction.Id, text), PredictionTokens.ConfirmLifetime);
        return new PredictionReply(OperationResult.Ok("predictions.cancel.preview"),
            messages.CancelPreview(view, text, pending.Count, pending.Aggregate(0L, Coins.Add), token, await LanguageAsync(actor.GuildId, ct)));
    }

    /// <summary>The cancellation, in ONE transaction: every pending STAKE is returned in full exactly once (never a possible payout); no statistics change.</summary>
    public async Task<PredictionReply> ConfirmCancelAsync(ActorContext actor, ChannelId here, string token, CancellationToken ct)
    {
        if (tokens.Take<CancelStep>(token, actor) is not { } step)
            return OperationResult.Fail(OperationError.Expired, "predictions.confirm.expired");
        var (managed, refusal) = await ManagedAsync(actor, here, step.PredictionId, null, ct);
        if (managed is null)
            return refusal!;

        var now = clock.GetUtcNow();
        var result = await PredictionWrites.RunAsync<(PredictionStatus Status, int Refunded, long Total)>(store.Db, async () =>
        {
            var prediction = await store.Predictions.FirstOrDefaultAsync(p => p.Id == step.PredictionId, ct);
            if (prediction is null)
                return (PredictionStatus.Abandoned, 0, 0L);
            if (prediction.Status is not (PredictionStatus.Open or PredictionStatus.Locked))
                return (prediction.Status, 0, 0L);
            var tournament = await store.ActiveTournamentAsync(actor.GuildId, ct);
            if (tournament is null || tournament.Id != prediction.TournamentId)
                return (PredictionStatus.Abandoned, 0, 0L);
            var entries = await store.Entries.Where(e => e.PredictionId == prediction.Id && e.Status == PredictionEntryStatus.Pending).ToListAsync(ct);
            var walletIds = entries.Select(e => e.WalletId).Distinct().ToList();
            var wallets = await store.Wallets.Where(w => walletIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
            var total = 0L;
            foreach (var entry in entries)
            {
                var wallet = wallets[entry.WalletId];
                wallet.PendingMinor = Coins.Subtract(wallet.PendingMinor, entry.StakeMinor);
                wallet.BalanceMinor = Coins.Add(wallet.BalanceMinor, entry.StakeMinor);
                wallet.UpdatedAt = now;
                entry.Status = PredictionEntryStatus.Refunded;
                entry.PayoutMinor = entry.StakeMinor;
                entry.SettledAt = now;
                store.Book(wallet, PredictionLedgerKind.Refund, entry.StakeMinor, PredictionStore.Key("refund", 'e', entry.Id), now, prediction.Id, entry.Id);
                total = Coins.Add(total, entry.StakeMinor);
            }

            prediction.Status = PredictionStatus.Cancelled;
            prediction.CancelReason = step.Reason;
            prediction.CancelledAt = now;
            prediction.CancelledByUserId = actor.UserId.Value;
            prediction.RefundTotalMinor = total;
            PredictionStore.Touch(prediction);
            await store.Db.SaveChangesAsync(ct);
            return (PredictionStatus.Open, entries.Count, total);
        }, ct);
        if (result.Status != PredictionStatus.Open)
            return StateRefusal(result.Status, ManageAction.Cancel);

        logger.LogInformation("prediction_cancelled {Prediction} guild={Guild} by={User} refunded={Refunded} total={Total}",
            step.PredictionId, actor.GuildId, actor.UserId, result.Refunded, result.Total);
        await cardSync.SafeSyncAsync(step.PredictionId, ct);
        return OperationResult.Ok("predictions.cancel.done", PredictionCards.Number(step.PredictionId), result.Refunded,
            Coins.Format(result.Total, await LanguageAsync(actor.GuildId, ct)));
    }

    // ---- worker: publishing, card presence ----

    /// <summary>
    /// Rows whose card post was uncertain (or the process stopped while posting): the card is looked for among the channel's
    /// latest messages; found → opened, never posted again. Not found within <see cref="PublishGrace"/> → abandoned (nobody
    /// could enter it, so there is nothing to refund) and the tournament is no longer blocked by it.
    /// </summary>
    public async Task<int> ReconcilePublishingAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var recheck = now - PublishRecheckAfter;
        var rows = await store.Predictions.AsNoTracking().Where(p => p.Status == PredictionStatus.Publishing && p.MessageId == null && p.CreatedAt < recheck)
            .OrderBy(p => p.Id).Take(SweepBatch).ToListAsync(ct);
        var resolved = 0;
        foreach (var row in rows.Where(r => deployment.IsGuildAllowed(new GuildId(r.GuildId))))
        {
            var view = await store.ViewAsync(row, ct);
            var card = cards.Render(view with { Status = PredictionStatus.Open }, await LanguageAsync(view.Guild, ct));
            var probe = new DeliveryProbe(MessageFingerprint.Of(card), row.CreatedAt, new HashSet<MessageId>());
            if (await transport.FindRecentAsync(view.Channel, probe, RecentMessages, ct) is ReconcileOutcome.Found found)
            {
                await AttachAsync(row.Id, found.MessageId, ct);
                logger.LogInformation("Prediction {Prediction}: its uncertain card was found and recorded", row.Id);
                resolved++;
                continue;
            }

            if (row.CreatedAt < now - PublishGrace)
            {
                var abandoned = await store.Predictions.Where(p => p.Id == row.Id && p.Status == PredictionStatus.Publishing && p.MessageId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PredictionStatus.Abandoned).SetProperty(p => p.CancelledAt, now)
                        .SetProperty(p => p.Version, p => p.Version + 1), ct);
                if (abandoned > 0)
                {
                    logger.LogWarning("Prediction {Prediction}: its card was never confirmed; abandoned without entries", row.Id);
                    resolved++;
                }
            }
            else
            {
                await store.Predictions.Where(p => p.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.PublishChecks, p => p.PublishChecks + 1), ct);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Without message events (Guilds intent only) a deleted card is noticed by reading it: open cards are checked now and
    /// then; only a definite "gone" counts (no access, 429, 5xx and timeouts answer Unknown and change nothing).
    /// </summary>
    public async Task<int> CheckOpenCardsAsync(CancellationToken ct)
    {
        var rows = await store.Predictions.AsNoTracking().Where(p => p.Status == PredictionStatus.Open && p.MessageId != null && !p.CardMissing)
            .OrderBy(p => p.Id).Select(p => new { p.Id, p.GuildId, p.ChannelId, p.MessageId }).Take(SweepBatch).ToListAsync(ct);
        var missing = 0;
        foreach (var row in rows.Where(r => deployment.IsGuildAllowed(new GuildId(r.GuildId))))
        {
            if (await transport.GetPresenceAsync(new ChannelId(row.ChannelId), new MessageId(row.MessageId!.Value), ct) != MessagePresence.Missing)
                continue;
            await cardSync.MarkMissingAsync(row.Id, "presence check", ct);
            missing++;
        }

        return missing;
    }

    /// <summary>
    /// Management lives on the card, so a prediction whose card is definitely gone (deleted message) gets a replacement card in
    /// its channel: locked (entries stopped when the card vanished), with ✅ Sonuçlandır and ↩️ İptal / İade — otherwise it
    /// could never be settled or cancelled and would block the tournament forever. At most one attempt per
    /// <see cref="RepostBackoff"/>; an uncertain post is looked for among the latest messages first — never a blind second card.
    /// </summary>
    public async Task<int> RepostMissingCardsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = now - RepostBackoff;
        var rows = await store.Predictions.AsNoTracking()
            .Where(p => p.CardMissing && (p.Status == PredictionStatus.Open || p.Status == PredictionStatus.Locked) && (p.CardEditedAt == null || p.CardEditedAt < due))
            .OrderBy(p => p.Id).Take(SweepBatch).ToListAsync(ct);
        var reposted = 0;
        foreach (var row in rows.Where(r => deployment.IsGuildAllowed(new GuildId(r.GuildId))))
        {
            await store.Predictions.Where(p => p.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CardEditedAt, now), ct); // the backoff, whatever happens
            var view = await store.ViewAsync(row, ct);
            var card = cards.Render(view with { Message = null }, await LanguageAsync(view.Guild, ct));
            if (await PostCardAsync(view.Channel, card, Truncate(now), ct) is not (CardPost.Posted, var message))
            {
                logger.LogWarning("Prediction {Prediction}: its replacement card could not be posted; retrying in {Backoff}", row.Id, RepostBackoff);
                continue;
            }

            var changed = await store.Predictions.Where(p => p.Id == row.Id && p.CardMissing)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.MessageId, (ulong?)message.Value).SetProperty(p => p.CardMissing, false)
                    .SetProperty(p => p.CardStale, false).SetProperty(p => p.CardSyncAttempts, 0).SetProperty(p => p.CardEditedAt, now)
                    .SetProperty(p => p.Version, p => p.Version + 1), ct);
            if (changed > 0)
            {
                reposted++;
                logger.LogWarning("Prediction {Prediction}: its card was gone; a replacement card {Message} was posted for managing it", row.Id, message);
            }
        }

        return reposted;
    }

    // ---- lookups ----

    public Task<PredictionView?> GetAsync(long id, CancellationToken ct) => store.ViewAsync(id, ct);

    private enum ManageAction
    {
        Lock,
        Settle,
        Cancel,
    }

    /// <summary>
    /// A prediction reached from its card or from a private confirmation, loaded from the database every time: THIS guild, THIS
    /// channel (the configured predictions channel and the prediction's own), the module enabled, and — for a click on the
    /// card itself — the card's own message. Another guild, another channel, a copied or forged custom id: "not found".
    /// </summary>
    private async Task<(PredictionEntity? Prediction, OperationResult? Refusal)> LoadInChannelAsync(ActorContext actor, ChannelId here, long id, MessageId? card,
        CancellationToken ct)
    {
        if ((guards.InPredictionsChannel(here) ?? await guards.EnabledAsync(actor.GuildId, ct)) is { } refusal)
            return (null, refusal);
        var prediction = await store.Predictions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.GuildId == actor.GuildId.Value, ct);
        if (prediction is null || prediction.ChannelId != here.Value || (card is { } message && prediction.MessageId is { } stored && stored != message.Value))
            return (null, NotFound());
        return (prediction, null);
    }

    /// <summary>
    /// <see cref="LoadInChannelAsync"/>, then the manager rule on the stored creator and THIS interaction's roles and permissions:
    /// its creator while holding the creator role, or Administrator / the server owner. Anyone else changes nothing.
    /// </summary>
    private async Task<(PredictionEntity? Prediction, OperationResult? Refusal)> ManagedAsync(ActorContext actor, ChannelId here, long id, MessageId? card,
        CancellationToken ct)
    {
        var (prediction, refusal) = await LoadInChannelAsync(actor, here, id, card, ct);
        if (prediction is null)
            return (null, refusal);
        return PredictionAccess.CanManage(actor, guards.CreatorRole, new UserId(prediction.CreatorUserId)) ? (prediction, null) : (null, PredictionGuards.NotManager());
    }

    private async Task<string> LanguageAsync(GuildId guild, CancellationToken ct) => (await settings.GetAsync(guild, ct)).Language;

    private static string AmountErrorKey(AmountError error) => error switch
    {
        AmountError.Empty => "empty",
        AmountError.TooManyDecimals => "decimals",
        AmountError.BelowMinimum => "minimum",
        _ => "format",
    };

    public static OperationResult NotFound() => OperationResult.Fail(OperationError.NotFound, "predictions.not_found");

    /// <summary>Why a prediction in <paramref name="status"/> cannot be locked/settled/cancelled (V1: a settled one is final).</summary>
    private static OperationResult StateRefusal(PredictionStatus status, ManageAction action) => OperationResult.Fail(OperationError.Conflict, status switch
    {
        PredictionStatus.Locked when action == ManageAction.Lock => "predictions.state.already_locked",
        PredictionStatus.Settled when action == ManageAction.Cancel => "predictions.state.settled_no_cancel",
        PredictionStatus.Settled => "predictions.state.already_settled",
        PredictionStatus.Cancelled => "predictions.state.cancelled",
        PredictionStatus.Publishing => "predictions.state.publishing",
        _ => "predictions.state.unavailable",
    });

    private static string MessageLink(GuildId guild, ChannelId channel, MessageId message) =>
        string.Create(CultureInfo.InvariantCulture, $"https://discord.com/channels/{guild.Value}/{channel.Value}/{message.Value}");

    /// <summary>Whole seconds: the card shows Discord timestamps, which have no sub-second part.</summary>
    private static DateTimeOffset Truncate(DateTimeOffset instant) => DateTimeOffset.FromUnixTimeSeconds(instant.ToUnixTimeSeconds());

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
