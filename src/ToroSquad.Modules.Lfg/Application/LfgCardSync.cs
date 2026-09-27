using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

public enum LfgCardSyncOutcome
{
    NothingToDo = 0,
    Updated = 1,
    Retry = 2,
    GaveUp = 3,
    MessageMissing = 4,
}

/// <summary>
/// Keeps the card messages honest without user interaction. The card is the /ekip interaction response; its channel and
/// message ids are stored, and this class works on it with the bot's own REST credentials only (no interaction or
/// webhook token, so nothing expires):
/// <list type="bullet">
/// <item><see cref="SyncAsync"/> EDITS a card whose stored state Discord does not show yet
/// (<see cref="LfgListingEntity.CardStale"/>: expired, closed from a confirmation, a failed interactive redraw). It never
/// sends a new message, so there is nothing to deduplicate and no outbox row; edits never ping (allowed_mentions none).</item>
/// <item><see cref="VerifyAsync"/> reads one active card (a single GET) to notice a card deleted in Discord — the bot uses
/// only the Guilds intent, so it receives no message-delete events.</item>
/// </list>
/// A deleted message or channel ends the listing as <see cref="LfgStatus.Orphaned"/> at once (it no longer counts as
/// active and is never edited again). Everything else — permission loss, rate limits, 5xx — is retried a bounded number of
/// times and then dropped with a warning; the listing's own state is never changed by a failed edit.
/// </summary>
public sealed class LfgCardSync(
    ToroDbContext db,
    IMessageTransport transport,
    LfgCardRenderer renderer,
    IGuildSettingsStore settings,
    DeploymentPolicy deployment,
    TimeProvider clock,
    ILogger<LfgCardSync> logger)
{
    public const int MaxAttempts = 8;
    private const int Batch = 25;
    private const int VerifyBatch = 50;

    private DbSet<LfgListingEntity> Listings => db.Set<LfgListingEntity>();

    public async Task<int> SyncStaleAsync(CancellationToken ct)
    {
        var ids = await Listings.AsNoTracking().Where(x => x.CardStale).OrderBy(x => x.Id).Select(x => x.Id).Take(Batch).ToListAsync(ct);
        var updated = 0;
        foreach (var id in ids)
        {
            if (await SyncAsync(id, ct) == LfgCardSyncOutcome.Updated)
                updated++;
        }

        return updated;
    }

    public async Task<LfgCardSyncOutcome> SyncAsync(long listingId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var listing = await Listings.FirstOrDefaultAsync(x => x.Id == listingId, ct);
        if (listing is null || !listing.CardStale)
            return LfgCardSyncOutcome.NothingToDo;

        if (listing.MessageId is not { } messageId || !deployment.IsGuildAllowed(new GuildId(listing.GuildId)))
        {
            // No known message (the post was never confirmed) or a guild outside the allow-list: nothing may be edited.
            listing.CardStale = false;
            await SaveAsync(ct);
            return LfgCardSyncOutcome.NothingToDo;
        }

        var players = await db.Set<LfgParticipantEntity>().AsNoTracking().Where(p => p.ListingId == listingId).ToListAsync(ct);
        var language = (await settings.GetAsync(new GuildId(listing.GuildId), ct)).Language;
        var message = renderer.Render(LfgService.ToView(listing, players), language);
        var outcome = await transport.EditAsync(new ChannelId(listing.ChannelId), new MessageId(messageId), message, ct);

        switch (outcome)
        {
            case SendOutcome.Sent:
                listing.CardStale = false;
                listing.CardSyncAttempts = 0;
                return await SaveAsync(ct) ? LfgCardSyncOutcome.Updated : LfgCardSyncOutcome.Retry;

            case SendOutcome.Permanent { Kind: PermanentFailureKind.UnknownMessage or PermanentFailureKind.UnknownChannel } missing:
                await OrphanAsync(listing, missing.Kind.ToString(), ct);
                return LfgCardSyncOutcome.MessageMissing;

            default:
                listing.CardSyncAttempts++;
                var detail = outcome switch
                {
                    SendOutcome.Permanent p => p.Kind + ": " + p.Reason,
                    SendOutcome.Transient t => "transient: " + t.Reason,
                    SendOutcome.Ambiguous a => "ambiguous: " + a.Reason,
                    SendOutcome.RateLimited => "rate limited",
                    _ => outcome.GetType().Name,
                };
                if (listing.CardSyncAttempts >= MaxAttempts)
                {
                    listing.CardStale = false;
                    logger.LogWarning("LFG listing {Listing}: giving up updating its card after {Attempts} attempts ({Detail})", listing.Id, listing.CardSyncAttempts, detail);
                    await SaveAsync(ct);
                    return LfgCardSyncOutcome.GaveUp;
                }

                // The worker runs about once a minute: that spacing is the backoff, the attempt cap the upper bound.
                logger.LogWarning("LFG listing {Listing}: card update failed, will retry ({Detail})", listing.Id, detail);
                await SaveAsync(ct);
                return LfgCardSyncOutcome.Retry;
        }
    }

    /// <summary>Worker reconciliation (throttled by the worker): one read per active card, oldest listing first.</summary>
    public async Task<int> VerifyActiveCardsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = await Listings.AsNoTracking()
            .Where(x => (x.Status == LfgStatus.Open || x.Status == LfgStatus.Full) && x.ExpiresAt > now && x.MessageId != null)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(VerifyBatch).ToListAsync(ct);
        return await VerifyManyAsync(ids, ct);
    }

    /// <summary>
    /// Only when a user hits the active-listing limit: checks that user's active cards right away, so a card a moderator
    /// deleted does not block a new listing until the next reconciliation.
    /// </summary>
    public async Task<int> VerifyOwnerCardsAsync(GuildId guild, UserId owner, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = await Listings.AsNoTracking()
            .Where(x => x.GuildId == guild.Value && x.OwnerUserId == owner.Value && (x.Status == LfgStatus.Open || x.Status == LfgStatus.Full) &&
                        x.ExpiresAt > now && x.MessageId != null)
            .Select(x => x.Id).ToListAsync(ct);
        return await VerifyManyAsync(ids, ct);
    }

    /// <summary>Orphans an ACTIVE listing whose card is gone. Idempotent: anything else (or "could not tell") changes nothing.</summary>
    public async Task<LfgCardSyncOutcome> VerifyAsync(long listingId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var listing = await Listings.FirstOrDefaultAsync(x => x.Id == listingId, ct);
        if (listing is not { Status: LfgStatus.Open or LfgStatus.Full, MessageId: { } messageId } || !deployment.IsGuildAllowed(new GuildId(listing.GuildId)))
            return LfgCardSyncOutcome.NothingToDo;

        var presence = await transport.GetPresenceAsync(new ChannelId(listing.ChannelId), new MessageId(messageId), ct);
        if (presence != MessagePresence.Missing)
            return LfgCardSyncOutcome.NothingToDo;
        return await OrphanAsync(listing, "deleted", ct) ? LfgCardSyncOutcome.MessageMissing : LfgCardSyncOutcome.Retry;
    }

    private async Task<int> VerifyManyAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        var orphaned = 0;
        foreach (var id in ids)
        {
            if (await VerifyAsync(id, ct) == LfgCardSyncOutcome.MessageMissing)
                orphaned++;
        }

        return orphaned;
    }

    private async Task<bool> OrphanAsync(LfgListingEntity listing, string reason, CancellationToken ct)
    {
        logger.LogWarning("LFG listing {Listing}: card message is gone ({Reason}); listing orphaned (guild {Guild}, channel {Channel})",
            listing.Id, reason, listing.GuildId, listing.ChannelId);
        listing.Status = LfgStatus.Orphaned;
        listing.ClosedAt ??= clock.GetUtcNow();
        listing.CardStale = false;
        listing.CardSyncAttempts = 0;
        listing.Version++;
        if (!await SaveAsync(ct))
            return false;
        await LfgNoticePlanner.CancelPendingAsync(db, listing.Id, "listing_orphaned", clock.GetUtcNow(), ct);
        return true;
    }

    /// <summary>A concurrent state change wins (its card is stale again or was redrawn interactively); retried next pass.</summary>
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
