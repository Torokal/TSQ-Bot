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
/// Redraws cards whose stored state Discord does not show yet (<see cref="LfgListingEntity.CardStale"/>: expired by the
/// worker, closed from a confirmation, a failed interactive update). The card is the interaction response of /ekip; this
/// class only ever EDITS that existing message through <see cref="IMessageTransport"/> — it never sends a new one, so there
/// is nothing to deduplicate and no outbox row. Edits never ping (transport rule + allowed_mentions none). A deleted
/// message or channel ends the listing as <see cref="LfgStatus.Orphaned"/> instead of being retried forever; other failures
/// are retried by the worker a bounded number of times.
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
                logger.LogWarning("LFG listing {Listing}: card message is gone ({Kind}); listing orphaned (guild {Guild}, channel {Channel})",
                    listing.Id, missing.Kind, listing.GuildId, listing.ChannelId);
                listing.Status = LfgStatus.Orphaned;
                listing.ClosedAt ??= clock.GetUtcNow();
                listing.CardStale = false;
                listing.Version++;
                await SaveAsync(ct);
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

                logger.LogWarning("LFG listing {Listing}: card update failed, will retry ({Detail})", listing.Id, detail);
                await SaveAsync(ct);
                return LfgCardSyncOutcome.Retry;
        }
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
