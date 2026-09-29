using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Giveaway.Persistence;

namespace ToroSquad.Modules.Giveaway.Application;

public enum GiveawayCardSyncOutcome
{
    NothingToDo = 0,
    Updated = 1,
    Retry = 2,
    GaveUp = 3,
    MessageMissing = 4,
}

/// <summary>
/// Makes the card show the stored state (drawn, rerolled, cancelled) by EDITING it with the bot's own REST credentials — never
/// a new message, never a ping (edits go out with allowed_mentions none). Called right after a state change and by the
/// worker for every card still marked <see cref="GiveawayEntity.CardStale"/> (a failed edit, a restart in between). A card
/// that is gone just stops being edited (the stored result stays); permission loss, rate limits and 5xx are retried a
/// bounded number of times, about once per worker pass, then dropped with a warning. The state itself never depends on it.
/// </summary>
public sealed class GiveawayCardSync(
    ToroDbContext db,
    IMessageTransport transport,
    GiveawayCards cards,
    IGuildSettingsStore settings,
    DeploymentPolicy deployment,
    ILogger<GiveawayCardSync> logger)
{
    public const int MaxAttempts = 8;
    private const int Batch = 25;

    private DbSet<GiveawayEntity> Giveaways => db.Set<GiveawayEntity>();

    public async Task<int> SyncStaleAsync(CancellationToken ct)
    {
        var ids = await Giveaways.AsNoTracking().Where(x => x.CardStale).OrderBy(x => x.Id).Select(x => x.Id).Take(Batch).ToListAsync(ct);
        var updated = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await SyncAsync(id, ct) == GiveawayCardSyncOutcome.Updated)
                    updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Giveaway {Giveaway}: card update threw", id);
                db.ChangeTracker.Clear();
                await Giveaways.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CardSyncAttempts, x => x.CardSyncAttempts + 1)
                    .SetProperty(x => x.CardStale, x => x.CardSyncAttempts + 1 < MaxAttempts), ct);
            }
        }

        return updated;
    }

    public async Task<GiveawayCardSyncOutcome> SyncAsync(long giveawayId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var giveaway = await Giveaways.FirstOrDefaultAsync(x => x.Id == giveawayId, ct);
        if (giveaway is null || !giveaway.CardStale)
            return GiveawayCardSyncOutcome.NothingToDo;

        if (giveaway.MessageId is not { } messageId || !deployment.IsGuildAllowed(new GuildId(giveaway.GuildId)))
        {
            giveaway.CardStale = false;
            await SaveAsync(ct);
            return GiveawayCardSyncOutcome.NothingToDo;
        }

        var winners = await GiveawayService.CurrentWinnersAsync(db, giveaway, ct);
        var language = (await settings.GetAsync(new GuildId(giveaway.GuildId), ct)).Language;
        var card = cards.Render(GiveawayService.ToView(giveaway, winners), language);
        var outcome = await transport.EditAsync(new ChannelId(giveaway.ChannelId), new MessageId(messageId), card, ct);

        switch (outcome)
        {
            case SendOutcome.Sent:
                giveaway.CardStale = false;
                giveaway.CardSyncAttempts = 0;
                return await SaveAsync(ct) ? GiveawayCardSyncOutcome.Updated : GiveawayCardSyncOutcome.Retry;

            case SendOutcome.Permanent { Kind: PermanentFailureKind.UnknownMessage or PermanentFailureKind.UnknownChannel } missing:
                logger.LogWarning("Giveaway {Giveaway}: card message is gone ({Kind}); its result stays stored", giveaway.Id, missing.Kind);
                giveaway.CardStale = false;
                giveaway.CardSyncAttempts = 0;
                await SaveAsync(ct);
                return GiveawayCardSyncOutcome.MessageMissing;

            default:
                giveaway.CardSyncAttempts++;
                var detail = outcome switch
                {
                    SendOutcome.Permanent p => p.Kind + ": " + p.Reason,
                    SendOutcome.Transient t => "transient: " + t.Reason,
                    SendOutcome.Ambiguous a => "ambiguous: " + a.Reason,
                    SendOutcome.RateLimited => "rate limited",
                    _ => outcome.GetType().Name,
                };
                if (giveaway.CardSyncAttempts >= MaxAttempts)
                {
                    giveaway.CardStale = false;
                    logger.LogWarning("Giveaway {Giveaway}: giving up updating its card after {Attempts} attempts ({Detail})", giveaway.Id, giveaway.CardSyncAttempts, detail);
                    await SaveAsync(ct);
                    return GiveawayCardSyncOutcome.GaveUp;
                }

                logger.LogWarning("Giveaway {Giveaway}: card update failed, will retry ({Detail})", giveaway.Id, detail);
                await SaveAsync(ct);
                return GiveawayCardSyncOutcome.Retry;
        }
    }

    /// <summary>A concurrent state change wins (the card is stale again); retried next pass.</summary>
    private async Task<bool> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
