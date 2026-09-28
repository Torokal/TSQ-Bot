using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// /privacy for TSQ LFG: the listings a user created (with their own game/details text) and the listings they joined, in
/// one guild, with their answer (Joined / Maybe / Waitlisted). Deletion removes both; listings the user only joined lose
/// them — a Joined player's slot goes to the first in that listing's waitlist in the same transaction (a full one reopens
/// only when nobody waits) — and their cards are redrawn by the worker. Event notices that list the user as a
/// pinged player are removed as well (they are otherwise pruned 24 h after delivery). Guild retention purges every LFG row
/// of the guild.
/// </summary>
public sealed class LfgUserData(ToroDbContext db, TimeProvider clock) : IUserDataContributor
{
    public ModuleId Module => LfgModule.ModuleIdTyped;

    private DbSet<LfgListingEntity> Listings => db.Set<LfgListingEntity>();
    private DbSet<LfgParticipantEntity> Participants => db.Set<LfgParticipantEntity>();

    public async Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var owned = await Listings.AsNoTracking().Where(x => x.GuildId == guild.Value && x.OwnerUserId == user.Value).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var joined = await (from p in Participants.AsNoTracking()
                            join l in Listings.AsNoTracking() on p.ListingId equals l.Id
                            where l.GuildId == guild.Value && p.UserId == user.Value && l.OwnerUserId != user.Value
                            orderby l.Id
                            select new { l.Id, l.GameName, p.Response, p.JoinedAt }).ToListAsync(cancellationToken);

        var listings = new JsonArray();
        foreach (var l in owned)
        {
            listings.Add(new JsonObject
            {
                ["id"] = l.Id,
                ["game"] = l.GameName,
                ["details"] = l.Details,
                ["maxPlayers"] = l.MaxPlayers,
                ["status"] = l.Status.ToString(),
                ["createdAtUtc"] = Iso(l.CreatedAt),
                ["eventAtUtc"] = l.EventAt is { } e ? Iso(e) : null,
                ["expiresAtUtc"] = Iso(l.ExpiresAt),
                ["notifyBeforeStart"] = l.NotifyBeforeStart,
                ["notifyAtStart"] = l.NotifyAtStart,
                ["closedAtUtc"] = l.ClosedAt is { } c ? Iso(c) : null,
            });
        }

        var participations = new JsonArray();
        foreach (var j in joined)
            participations.Add(new JsonObject { ["listingId"] = j.Id, ["game"] = j.GameName, ["response"] = j.Response.ToString(), ["respondedAtUtc"] = Iso(j.JoinedAt) });
        var notices = (await NoticesMentioningAsync(guild, user, cancellationToken)).Count;
        var closedAsModerator = await ClosedAsModerator(guild, user).CountAsync(cancellationToken);
        return new JsonObject
        {
            ["listings"] = listings,
            ["joinedListings"] = participations,
            ["eventNoticesPingingYou"] = notices,
            ["listingsYouClosedAsModerator"] = closedAsModerator,
        };
    }

    public async Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var owned = await Listings.CountAsync(x => x.GuildId == guild.Value && x.OwnerUserId == user.Value, cancellationToken);
        var joined = await JoinedElsewhere(guild, user).CountAsync(cancellationToken);
        var items = new List<DeletionPreviewItem>();
        if (owned > 0)
            items.Add(new DeletionPreviewItem("lfg.privacy.listings", owned));
        if (joined > 0)
            items.Add(new DeletionPreviewItem("lfg.privacy.joined", joined));
        var closed = await ClosedAsModerator(guild, user).CountAsync(cancellationToken);
        if (closed > 0)
            items.Add(new DeletionPreviewItem("lfg.privacy.closed", closed));
        return items;
    }

    public async Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deleted = 0;

        // Listings the user only joined (Joined, Maybe or waitlisted): remove them; a freed slot goes to the first in the
        // waitlist in this same transaction (the shared roster rule); the card is redrawn by the worker.
        var now = clock.GetUtcNow();
        var memberships = await JoinedElsewhere(guild, user).ToListAsync(cancellationToken);
        var affected = memberships.Select(p => p.ListingId).ToHashSet();
        foreach (var listing in await Listings.Where(x => affected.Contains(x.Id)).ToListAsync(cancellationToken))
        {
            var members = await Participants.Where(p => p.ListingId == listing.Id).ToListAsync(cancellationToken);
            var mine = members.Single(p => p.UserId == user.Value);
            Participants.Remove(mine);
            members.Remove(mine);
            LfgRoster.Rebalance(listing, members, now); // a closed, expired or orphaned listing stays untouched
            listing.CardStale = listing.MessageId is not null;
            listing.CardSyncAttempts = 0;
            listing.Version++;
        }

        deleted += memberships.Count;
        await db.SaveChangesAsync(cancellationToken);

        // Listings the user created (their own text): removed with all their players. A button on such a card then answers
        // "this listing no longer exists" and retires the buttons.
        var owned = Listings.Where(x => x.GuildId == guild.Value && x.OwnerUserId == user.Value);
        // Their notices not delivered yet stop too, like on a close: a deleted listing must not ping anyone afterwards.
        foreach (var id in await owned.Select(x => x.Id).ToListAsync(cancellationToken))
            await LfgNoticePlanner.CancelPendingAsync(db, id, "listing_deleted", now, cancellationToken);
        await Participants.Where(p => owned.Any(l => l.Id == p.ListingId)).ExecuteDeleteAsync(cancellationToken);
        deleted += await owned.ExecuteDeleteAsync(cancellationToken);

        // Listings of others the user closed as a moderator: keep the listing, forget who closed it.
        deleted += await ClosedAsModerator(guild, user).ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedByUserId, (ulong?)null), cancellationToken);

        // Event notices whose payload lists the user as a pinged player — except one Discord may be receiving right now
        // (in flight, or an uncertain delivery still being reconciled); those are pruned after they finish.
        var notices = await NoticesMentioningAsync(guild, user, cancellationToken);
        deleted += await db.Outbox.Where(o => notices.Contains(o.Id)).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new DeletionReport(Module, deleted, []);
    }

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var listings = Listings.Where(x => x.GuildId == guild.Value);
        var n = await Participants.Where(p => listings.Any(l => l.Id == p.ListingId)).ExecuteDeleteAsync(cancellationToken);
        n += await listings.ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<LfgGuildConfigEntity>().Where(c => c.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        return n;
    }

    private async Task<List<long>> NoticesMentioningAsync(GuildId guild, UserId user, CancellationToken ct)
    {
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.GuildId == guild.Value && o.ModuleId == LfgModule.ModuleIdValue && o.Status != OutboxStatus.InFlight &&
                        (o.Status != OutboxStatus.DeliveryUnknown || o.NextAttemptAt == null))
            .Select(o => new { o.Id, o.PayloadJson }).ToListAsync(ct);
        return rows.Where(r => PayloadSerializer.Deserialize(r.PayloadJson).Mentions.Users?.Contains(user) == true).Select(r => r.Id).ToList();
    }

    private IQueryable<LfgListingEntity> ClosedAsModerator(GuildId guild, UserId user) =>
        Listings.Where(x => x.GuildId == guild.Value && x.ClosedByUserId == user.Value && x.OwnerUserId != user.Value);

    private IQueryable<LfgParticipantEntity> JoinedElsewhere(GuildId guild, UserId user) =>
        Participants.Where(p => p.UserId == user.Value && Listings.Any(l => l.Id == p.ListingId && l.GuildId == guild.Value && l.OwnerUserId != user.Value));

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
