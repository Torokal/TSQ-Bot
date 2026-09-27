using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>Outcome of a listing operation: the message to show, the listing as stored afterwards, whether its card should be redrawn.</summary>
public sealed record LfgResult(OperationResult Result, LfgListingView? Listing, bool RefreshCard);

/// <summary>
/// The LFG business rules: create, join, leave, close, expire. Every operation re-reads the listing and re-checks guild,
/// state, expiry, membership, capacity and permission server-side — the state of a button in Discord is never trusted.
/// Every state change runs in a write transaction that takes SQLite's write lock before reading (BEGIN IMMEDIATE), so
/// check-then-write (free slot, membership, active-listing limit) is serialized across connections; the (ListingId, UserId)
/// primary key and the listing's version token are the backstops.
/// </summary>
public sealed class LfgService(ToroDbContext db, IOptions<LfgOptions> options, TimeProvider clock, ILogger<LfgService> logger)
{
    /// <summary>Guild moderators (Discord's "Manage Messages", or Administrator) may close any listing.</summary>
    public const GuildPermission ModeratorPermission = GuildPermission.ManageMessages;

    private const int MaxWriteAttempts = 3;
    private const int ExpiryBatch = 100;
    private const int SqliteConstraint = 19;

    private DbSet<LfgListingEntity> Listings => db.Set<LfgListingEntity>();
    private DbSet<LfgParticipantEntity> Participants => db.Set<LfgParticipantEntity>();

    public async Task<LfgResult> CreateAsync(ActorContext actor, ChannelId channel, string? game, int players, string? details, int? durationMinutes, CancellationToken ct)
    {
        var o = options.Value;
        var (draft, error) = LfgRules.Validate(game, details, players, durationMinutes, o.MaxPlayersPerListing, o.DefaultExpirationMinutes);
        if (draft is null)
            return Refused(error switch
            {
                LfgDraftError.GameMissing or LfgDraftError.GameTooShort => No(OperationError.InvalidInput, "lfg.create.game_too_short", LfgRules.GameNameMinLength),
                LfgDraftError.GameTooLong => No(OperationError.InvalidInput, "lfg.create.game_too_long", LfgRules.GameNameMaxLength),
                LfgDraftError.DetailsTooLong => No(OperationError.InvalidInput, "lfg.create.details_too_long", LfgRules.DetailsMaxLength),
                LfgDraftError.PlayersOutOfRange => No(OperationError.InvalidInput, "lfg.create.players_range", LfgRules.MinPlayers, Math.Min(o.MaxPlayersPerListing, LfgRules.HardMaxPlayers)),
                _ => No(OperationError.InvalidInput, "lfg.create.duration_invalid"),
            });

        var config = await db.Set<LfgGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
        if (config?.ChannelId is { } only && only != channel.Value)
            return Refused(No(OperationError.InvalidInput, "lfg.create.wrong_channel", "<#" + only.ToString(CultureInfo.InvariantCulture) + ">"));

        var now = clock.GetUtcNow();
        var guild = actor.GuildId.Value;
        var owner = actor.UserId.Value;
        var id = await WriteAsync(async () =>
        {
            var active = await Listings.CountAsync(x => x.GuildId == guild && x.OwnerUserId == owner &&
                                                        (x.Status == LfgStatus.Open || x.Status == LfgStatus.Full) && x.ExpiresAt > now, ct);
            if (active >= o.MaxActiveListingsPerUser)
                return 0L;
            var listing = new LfgListingEntity
            {
                GuildId = guild,
                ChannelId = channel.Value,
                OwnerUserId = owner,
                GameName = draft.GameName,
                Details = draft.Details,
                MaxPlayers = draft.MaxPlayers,
                Status = LfgStatus.Open,
                CreatedAt = now,
                ExpiresAt = now + draft.Duration,
                Participants = [new LfgParticipantEntity { UserId = owner, JoinedAt = now }], // the owner is the first player
            };
            Listings.Add(listing);
            await db.SaveChangesAsync(ct);
            return listing.Id;
        }, ct);

        if (id == 0)
            return Refused(No(OperationError.Conflict, "lfg.create.limit", o.MaxActiveListingsPerUser));
        logger.LogInformation("LFG listing {Listing} created in guild {Guild} channel {Channel}: {Max} players, expires {ExpiresAt:O}",
            id, guild, channel, draft.MaxPlayers, now + draft.Duration);
        return new LfgResult(OperationResult.Ok("lfg.create.done"), await GetAsync(id, ct), RefreshCard: true);
    }

    /// <summary>Records the card message once Discord confirmed it (or a later button click reveals it). Never overwrites.</summary>
    public async Task AttachMessageAsync(long listingId, GuildId guild, ChannelId channel, MessageId message, CancellationToken ct) =>
        await Listings.Where(x => x.Id == listingId && x.GuildId == guild.Value && x.MessageId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MessageId, (ulong?)message.Value).SetProperty(x => x.ChannelId, channel.Value), ct);

    /// <summary>The card could not be posted at all: the listing never existed for anyone, so it is removed (frees the owner's slot).</summary>
    public async Task DiscardAsync(long listingId, CancellationToken ct)
    {
        await Participants.Where(p => p.ListingId == listingId).ExecuteDeleteAsync(ct);
        await Listings.Where(x => x.Id == listingId).ExecuteDeleteAsync(ct);
        logger.LogWarning("LFG listing {Listing} discarded: its card could not be posted", listingId);
    }

    public async Task<LfgResult> JoinAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = actor.UserId.Value;
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: false, ct))
                return (Expired(), true);
            if (Ended(listing) is { } ended)
                return (ended, true);

            var players = await Participants.Where(p => p.ListingId == listing.Id).Select(p => p.UserId).ToListAsync(ct);
            if (players.Contains(user))
                return (No(OperationError.Conflict, "lfg.join.already"), false);
            if (players.Count >= listing.MaxPlayers)
            {
                if (listing.Status != LfgStatus.Full)
                {
                    listing.Status = LfgStatus.Full;
                    listing.Version++;
                    await db.SaveChangesAsync(ct);
                }

                return (No(OperationError.Conflict, "lfg.join.full"), true); // the clicked card still offered a slot
            }

            Participants.Add(new LfgParticipantEntity { ListingId = listing.Id, UserId = user, JoinedAt = now });
            var full = players.Count + 1 >= listing.MaxPlayers;
            if (full)
                listing.Status = LfgStatus.Full;
            listing.Version++;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
            {
                // Unreachable while the write lock is held; the primary key still guarantees no duplicate participant.
                return (No(OperationError.Conflict, "lfg.join.already"), false);
            }

            if (full)
                logger.LogInformation("LFG listing {Listing} is full ({Players} players)", listing.Id, listing.MaxPlayers);
            return (OperationResult.Ok("lfg.join.done"), true);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, ct), refresh);
    }

    public async Task<LfgResult> LeaveAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = actor.UserId.Value;
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: false, ct))
                return (Expired(), true);
            if (Ended(listing) is { } ended)
                return (ended, true);
            if (listing.OwnerUserId == user)
                return (No(OperationError.InvalidInput, "lfg.leave.owner"), false);

            var participant = await Participants.FirstOrDefaultAsync(p => p.ListingId == listing.Id && p.UserId == user, ct);
            if (participant is null)
                return (No(OperationError.NotFound, "lfg.leave.not_member"), false);

            Participants.Remove(participant);
            if (listing.Status == LfgStatus.Full)
                listing.Status = LfgStatus.Open; // a slot is free again and the listing has not expired
            listing.Version++;
            await db.SaveChangesAsync(ct);
            return (OperationResult.Ok("lfg.leave.done"), true);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, ct), refresh);
    }

    /// <summary>First step of closing (before the confirmation is shown): same checks as <see cref="CloseAsync"/>, no change.</summary>
    public async Task<LfgResult> CheckCloseAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (!MayClose(actor, listing))
                return (No(OperationError.Forbidden, "lfg.close.forbidden"), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: false, ct))
                return (Expired(), true);
            if (Ended(listing) is { } ended)
                return (ended, true);
            return (OperationResult.Ok("lfg.close.question"), false);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, ct), refresh);
    }

    /// <summary>
    /// Owner or moderator closes the listing. Idempotent: closing a closed listing succeeds without a change. The message
    /// is kept (history); the card is marked stale so it is redrawn as closed with disabled buttons.
    /// </summary>
    public async Task<LfgResult> CloseAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (!MayClose(actor, listing))
                return (No(OperationError.Forbidden, "lfg.close.forbidden"), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: true, ct))
                return (Expired(), true);
            if (listing.Status is LfgStatus.Closed or LfgStatus.Orphaned)
                return (OperationResult.Ok("lfg.close.already"), false);
            if (listing.Status == LfgStatus.Expired)
                return (Expired(), false);

            listing.Status = LfgStatus.Closed;
            listing.ClosedAt = now;
            listing.ClosedByUserId = actor.UserId.Value;
            listing.CardStale = true; // the confirmation lives in another (ephemeral) message: the card is edited separately
            listing.CardSyncAttempts = 0;
            listing.Version++;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("LFG listing {Listing} closed by its {Who}", listing.Id, listing.OwnerUserId == actor.UserId.Value ? "owner" : "moderator");
            return (OperationResult.Ok("lfg.close.done"), true);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, ct), refresh);
    }

    /// <summary>
    /// Worker pass: every active listing whose expiry has passed (including all that expired while the bot was down)
    /// becomes Expired and is queued for a card edit. Returns the expired ids.
    /// </summary>
    public async Task<IReadOnlyList<long>> ExpireDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = await WriteAsync(async () =>
        {
            var due = await Listings
                .Where(x => (x.Status == LfgStatus.Open || x.Status == LfgStatus.Full) && x.ExpiresAt <= now)
                .OrderBy(x => x.ExpiresAt)
                .Take(ExpiryBatch)
                .ToListAsync(ct);
            foreach (var listing in due)
                Expire(listing, cardStale: true);
            await db.SaveChangesAsync(ct);
            return due.Select(x => (x.Id, x.GuildId)).ToList();
        }, ct);
        foreach (var (id, guild) in expired)
            logger.LogInformation("LFG listing {Listing} expired (guild {Guild})", id, guild);
        return expired.Select(x => x.Id).ToList();
    }

    /// <summary>An interactive card update failed: the worker takes over.</summary>
    public async Task MarkCardStaleAsync(long listingId, CancellationToken ct) =>
        await Listings.Where(x => x.Id == listingId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CardStale, true), ct);

    public async Task<LfgListingView?> GetAsync(long listingId, CancellationToken ct)
    {
        var listing = await Listings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == listingId, ct);
        if (listing is null)
            return null;
        var players = await Participants.AsNoTracking().Where(p => p.ListingId == listingId).ToListAsync(ct);
        return ToView(listing, players);
    }

    public static LfgListingView ToView(LfgListingEntity listing, IEnumerable<LfgParticipantEntity> players) => new(
        listing.Id,
        new GuildId(listing.GuildId),
        new ChannelId(listing.ChannelId),
        listing.MessageId is { } m ? new MessageId(m) : null,
        new UserId(listing.OwnerUserId),
        listing.GameName,
        listing.Details,
        listing.MaxPlayers,
        listing.Status,
        listing.CreatedAt,
        listing.ExpiresAt,
        listing.ClosedAt,
        players.OrderBy(p => p.UserId == listing.OwnerUserId ? 0 : 1).ThenBy(p => p.JoinedAt).ThenBy(p => p.UserId).Select(p => new UserId(p.UserId)).ToList(),
        listing.Version);

    private async Task<T> WriteAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            // SQLite: BeginTransaction = BEGIN IMMEDIATE (write lock first, other writers wait up to the busy timeout).
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await work();
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWriteAttempts)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>Another guild's listing is indistinguishable from a missing one.</summary>
    private Task<LfgListingEntity?> FindAsync(ActorContext actor, long listingId, CancellationToken ct) =>
        Listings.FirstOrDefaultAsync(x => x.Id == listingId && x.GuildId == actor.GuildId.Value, ct);

    private static bool MayClose(ActorContext actor, LfgListingEntity listing) =>
        listing.OwnerUserId == actor.UserId.Value || Authorize.Require(actor, new GuildId(listing.GuildId), ModeratorPermission).IsAllowed;

    /// <summary>Lazy expiry on interaction: a listing past its expiry is expired right here, even before the worker gets to it.</summary>
    private async Task<bool> ExpireIfDueAsync(LfgListingEntity listing, DateTimeOffset now, bool cardStale, CancellationToken ct)
    {
        if (listing.Status is not (LfgStatus.Open or LfgStatus.Full) || listing.ExpiresAt > now)
            return false;
        Expire(listing, cardStale);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("LFG listing {Listing} expired (guild {Guild})", listing.Id, listing.GuildId);
        return true;
    }

    private static void Expire(LfgListingEntity listing, bool cardStale)
    {
        listing.Status = LfgStatus.Expired;
        listing.ClosedAt = listing.ExpiresAt;
        if (cardStale)
        {
            listing.CardStale = true;
            listing.CardSyncAttempts = 0;
        }

        listing.Version++;
    }

    private static OperationResult? Ended(LfgListingEntity listing) => listing.Status switch
    {
        LfgStatus.Expired => Expired(),
        LfgStatus.Closed or LfgStatus.Orphaned => No(OperationError.Conflict, "lfg.closed"),
        _ => null,
    };

    private static OperationResult NotFound() => No(OperationError.NotFound, "lfg.not_found");

    private static OperationResult Expired() => No(OperationError.Expired, "lfg.expired");

    /// <summary>Expected refusals (full, already joined, …) are normal outcomes: no trace code and no log line per click.</summary>
    private static OperationResult No(OperationError error, string key, params object[] args) => new(false, key, args, error);

    private static LfgResult Refused(OperationResult result) => new(result, null, RefreshCard: false);
}
