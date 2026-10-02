using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions.Application;

public enum PredictionCardSyncOutcome
{
    NothingToDo = 0,
    Updated = 1,
    Deferred = 2,
    Retry = 3,
    GaveUp = 4,
    MessageMissing = 5,
}

/// <summary>
/// One process-wide gate for card edits: edits of the same card are applied in order, each drawing the state stored at that
/// moment, so an older snapshot can never land after a newer one. (Card edits are presentation only; the economy never
/// depends on this lock.)
/// </summary>
public sealed class PredictionCardGate
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// Makes a card show the stored state by EDITING it with the bot's own REST credentials — never a new message, never a ping
/// (edits go out with allowed_mentions none). State changes mark the card stale; <see cref="RequestAsync"/> edits it at once
/// unless the card was edited within <see cref="CoalesceWindow"/> (bursts of entries become one edit per window; the worker
/// picks the rest up), and the worker edits every card still stale. A card Discord reports as DELETED (Unknown Message /
/// Unknown Channel) is marked missing and, if still open, locked — stakes stay, the prediction can still be settled or
/// cancelled by number. Permission loss, 429 and 5xx/timeouts are never taken for deletion: they are retried a bounded
/// number of times, then left with a warning (the doctor shows them). A settled or cancelled prediction's card is removed
/// <see cref="PredictionsOptions.TerminalCardRetentionHours"/> after the settlement/cancellation was committed
/// (<see cref="RemoveExpiredCardsAsync"/>); a removed card is archived: never edited or replaced again.
/// </summary>
public sealed class PredictionCardSync(
    PredictionStore store,
    IMessageTransport transport,
    PredictionCards cards,
    IGuildSettingsStore settings,
    DeploymentPolicy deployment,
    PredictionCardGate cardGate,
    IOptions<PredictionsOptions> options,
    TimeProvider clock,
    ILogger<PredictionCardSync> logger)
{
    public const int MaxAttempts = 8;
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(10);
    private const int Batch = 25;

    /// <summary>Failed removals of one card (429, 5xx, timeouts, missing permissions) before it is left for the doctor.</summary>
    public const int MaxRemovalAttempts = 6;

    private const int RemovalBatch = 10;

    /// <summary>The wait after the n-th failed removal: 1 min, 5 min, 15 min, 1 h, then 6 h — never a fast loop.</summary>
    public static TimeSpan RemovalBackoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(6),
    };

    /// <summary>The one authoritative terminal instant: when the settlement or the cancellation was committed (never a card edit time).</summary>
    public static DateTimeOffset? TerminalAt(PredictionEntity p) => p.Status switch
    {
        PredictionStatus.Settled => p.SettledAt,
        PredictionStatus.Cancelled => p.CancelledAt,
        _ => null,
    };

    /// <summary>
    /// Removes the public cards of settled/cancelled predictions whose retention has passed, read from the database every
    /// pass (restart-safe, no timer per card). Only the Discord message: the prediction, outcomes, entries, ledger, wallets,
    /// statistics, tournament and automation links are untouched. Deleted or already gone (404) = archived for good; 429,
    /// 5xx, timeouts and permission problems are retried with a growing pause, then left for the doctor.
    /// </summary>
    public async Task<int> RemoveExpiredCardsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - options.Value.TerminalCardRetention;
        var ids = await store.Predictions.AsNoTracking()
            .Where(p => (p.Status == PredictionStatus.Settled || p.Status == PredictionStatus.Cancelled) && p.CardRemovedAt == null && p.MessageId != null &&
                        p.CardRemovalAttempts < MaxRemovalAttempts && (p.CardRemovalNextAt == null || p.CardRemovalNextAt <= now) &&
                        (p.Status == PredictionStatus.Settled ? p.SettledAt : p.CancelledAt) <= cutoff)
            .OrderBy(p => p.Id).Select(p => p.Id).Take(RemovalBatch).ToListAsync(ct);
        var removed = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await RemoveAsync(id, now, ct))
                    removed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Prediction {Prediction}: removing its card threw", id);
                store.Db.ChangeTracker.Clear();
                await FailRemovalAsync(id, now, ct);
            }
        }

        return removed;
    }

    private async Task<bool> RemoveAsync(long id, DateTimeOffset now, CancellationToken ct)
    {
        await cardGate.Gate.WaitAsync(ct);
        try
        {
            store.Db.ChangeTracker.Clear();
            // Checked again right before the call: still terminal, retention passed, not removed yet — and the message id read
            // here is the one recorded as removed below (never a replacement card's id).
            var p = await store.Predictions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null || p.CardRemovedAt is not null || p.MessageId is not { } messageId || TerminalAt(p) is not { } terminalAt ||
                now - terminalAt < options.Value.TerminalCardRetention || !deployment.IsGuildAllowed(new GuildId(p.GuildId)))
                return false;

            var outcome = p.CardMissing
                ? new SendOutcome.Permanent(PermanentFailureKind.UnknownMessage, "already reported gone")
                : await transport.DeleteAsync(new ChannelId(p.ChannelId), new MessageId(messageId), ct);
            if (outcome is SendOutcome.Sent or SendOutcome.Permanent { Kind: PermanentFailureKind.UnknownMessage or PermanentFailureKind.UnknownChannel })
            {
                var archived = await store.Predictions.Where(x => x.Id == id && x.MessageId == messageId && x.CardRemovedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CardRemovedAt, now).SetProperty(x => x.CardStale, false)
                        .SetProperty(x => x.CardRemovalNextAt, (DateTimeOffset?)null).SetProperty(x => x.Version, x => x.Version + 1), ct);
                logger.LogInformation("Prediction {Prediction}: its {Status} card was removed after the retention ({Result})", id, p.Status,
                    outcome is SendOutcome.Sent ? "deleted" : "already gone");
                return archived > 0;
            }

            var detail = outcome switch
            {
                SendOutcome.Permanent f => f.Kind + ": " + f.Reason,
                SendOutcome.Transient t => "transient: " + t.Reason,
                SendOutcome.RateLimited => "rate limited",
                _ => outcome.GetType().Name,
            };
            var attempts = await FailRemovalAsync(id, now, ct);
            logger.LogWarning("Prediction {Prediction}: removing its card failed ({Detail}); {Next}", id, detail,
                attempts >= MaxRemovalAttempts ? "giving up (doctor shows it)" : "retrying later");
            return false;
        }
        finally
        {
            cardGate.Gate.Release();
        }
    }

    private async Task<int> FailRemovalAsync(long id, DateTimeOffset now, CancellationToken ct)
    {
        var attempts = await store.Predictions.AsNoTracking().Where(x => x.Id == id).Select(x => x.CardRemovalAttempts).FirstOrDefaultAsync(ct) + 1;
        await store.Predictions.Where(x => x.Id == id && x.CardRemovedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CardRemovalAttempts, attempts).SetProperty(x => x.CardRemovalNextAt, now + RemovalBackoff(attempts)), ct);
        return attempts;
    }

    public async Task<int> SyncStaleAsync(CancellationToken ct)
    {
        var ids = await store.Predictions.AsNoTracking().Where(x => x.CardStale).OrderBy(x => x.Id).Select(x => x.Id).Take(Batch).ToListAsync(ct);
        var updated = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await SyncAsync(id, ct) == PredictionCardSyncOutcome.Updated)
                    updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Prediction {Prediction}: card update threw", id);
                store.Db.ChangeTracker.Clear();
                await store.Predictions.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CardSyncAttempts, x => x.CardSyncAttempts + 1)
                    .SetProperty(x => x.CardStale, x => x.CardSyncAttempts + 1 < MaxAttempts), ct);
            }
        }

        return updated;
    }

    /// <summary>After an entry: edit now, or leave it to the worker when the card was edited moments ago (coalescing).</summary>
    public async Task<PredictionCardSyncOutcome> RequestAsync(long id, CancellationToken ct)
    {
        var edited = await store.Predictions.AsNoTracking().Where(x => x.Id == id).Select(x => x.CardEditedAt).FirstOrDefaultAsync(ct);
        if (edited is { } at && clock.GetUtcNow() - at < CoalesceWindow)
            return PredictionCardSyncOutcome.Deferred;
        return await SafeSyncAsync(id, ct);
    }

    /// <summary>The state is stored; a failed edit only leaves the card to the worker (it stays marked stale).</summary>
    public async Task<PredictionCardSyncOutcome> SafeSyncAsync(long id, CancellationToken ct)
    {
        try
        {
            return await SyncAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Prediction {Prediction}: card update failed; the worker retries it", id);
            store.Db.ChangeTracker.Clear();
            return PredictionCardSyncOutcome.Retry;
        }
    }

    public async Task<PredictionCardSyncOutcome> SyncAsync(long id, CancellationToken ct)
    {
        await cardGate.Gate.WaitAsync(ct);
        try
        {
            return await SyncLockedAsync(id, ct);
        }
        finally
        {
            cardGate.Gate.Release();
        }
    }

    private async Task<PredictionCardSyncOutcome> SyncLockedAsync(long id, CancellationToken ct)
    {
        store.Db.ChangeTracker.Clear();
        var prediction = await store.Predictions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (prediction is null || !prediction.CardStale)
            return PredictionCardSyncOutcome.NothingToDo;
        if (prediction.MessageId is not { } messageId || prediction.CardMissing || prediction.CardRemovedAt is not null || !deployment.IsGuildAllowed(new GuildId(prediction.GuildId)))
        {
            prediction.CardStale = false;
            await SaveAsync(ct);
            return PredictionCardSyncOutcome.NothingToDo;
        }

        var view = await store.ViewAsync(prediction, ct);
        var language = (await settings.GetAsync(view.Guild, ct)).Language;
        var outcome = await transport.EditAsync(view.Channel, new MessageId(messageId), cards.Render(view, language), ct);
        switch (outcome)
        {
            case SendOutcome.Sent:
                prediction.CardStale = false;
                prediction.CardSyncAttempts = 0;
                prediction.CardEditedAt = clock.GetUtcNow();
                return await SaveAsync(ct) ? PredictionCardSyncOutcome.Updated : PredictionCardSyncOutcome.Retry;

            case SendOutcome.Permanent { Kind: PermanentFailureKind.UnknownMessage or PermanentFailureKind.UnknownChannel } missing:
                store.Db.ChangeTracker.Clear();
                await MarkMissingAsync(id, missing.Kind.ToString(), ct);
                return PredictionCardSyncOutcome.MessageMissing;

            default:
                prediction.CardSyncAttempts++;
                var detail = outcome switch
                {
                    SendOutcome.Permanent p => p.Kind + ": " + p.Reason,
                    SendOutcome.Transient t => "transient: " + t.Reason,
                    SendOutcome.Ambiguous a => "ambiguous: " + a.Reason,
                    SendOutcome.RateLimited => "rate limited",
                    _ => outcome.GetType().Name,
                };
                if (prediction.CardSyncAttempts >= MaxAttempts)
                {
                    prediction.CardStale = false;
                    logger.LogWarning("Prediction {Prediction}: giving up updating its card after {Attempts} attempts ({Detail})", id, prediction.CardSyncAttempts, detail);
                    await SaveAsync(ct);
                    return PredictionCardSyncOutcome.GaveUp;
                }

                logger.LogWarning("Prediction {Prediction}: card update failed, will retry ({Detail})", id, detail);
                await SaveAsync(ct);
                return PredictionCardSyncOutcome.Retry;
        }
    }

    /// <summary>
    /// Discord says the card (or its channel) is gone: no more edits, and an open prediction stops taking entries at once.
    /// Everything staked stays; the worker posts a replacement card to settle or cancel it from.
    /// </summary>
    public async Task MarkMissingAsync(long id, string cause, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var locked = await PredictionWrites.RunAsync(store.Db, async () =>
        {
            var row = await store.Predictions.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || row.CardMissing)
                return false;
            row.CardMissing = true;
            row.CardStale = false;
            row.CardEditedAt = null; // the replacement card is due at once
            row.Version++;
            var wasOpen = row.Status == PredictionStatus.Open;
            if (wasOpen)
            {
                row.Status = PredictionStatus.Locked;
                row.LockReason = PredictionLockReason.CardMissing;
                row.LockedAt = now;
            }

            await store.Db.SaveChangesAsync(ct);
            return wasOpen;
        }, ct);
        logger.LogWarning("Prediction {Prediction}: its card is gone ({Cause}); {Action}", id, cause,
            locked ? "entries stopped, stakes kept" : "no further card edits");
    }

    /// <summary>A concurrent state change wins (the card is stale again); retried next pass.</summary>
    private async Task<bool> SaveAsync(CancellationToken ct)
    {
        try
        {
            await store.Db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            store.Db.ChangeTracker.Clear();
            return false;
        }
    }
}
