using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// /privacy for TSQ LFG: the listings a user created (with their own game/details text) and the listings they joined, in
/// one guild. Deletion removes both; listings the user only joined lose them as a player (a full one reopens) and their
/// cards are redrawn by the worker. Guild retention purges every LFG row of the guild.
/// </summary>
public sealed class LfgUserData(ToroDbContext db) : IUserDataContributor
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
                            select new { l.Id, l.GameName, p.JoinedAt }).ToListAsync(cancellationToken);

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
                ["expiresAtUtc"] = Iso(l.ExpiresAt),
                ["closedAtUtc"] = l.ClosedAt is { } c ? Iso(c) : null,
            });
        }

        var participations = new JsonArray();
        foreach (var j in joined)
            participations.Add(new JsonObject { ["listingId"] = j.Id, ["game"] = j.GameName, ["joinedAtUtc"] = Iso(j.JoinedAt) });
        return new JsonObject { ["listings"] = listings, ["joinedListings"] = participations };
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
        return items;
    }

    public async Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deleted = 0;

        // Listings the user only joined: remove them as a player; the card is redrawn without them.
        var memberships = await JoinedElsewhere(guild, user).ToListAsync(cancellationToken);
        var affected = memberships.Select(p => p.ListingId).ToHashSet();
        foreach (var listing in await Listings.Where(x => affected.Contains(x.Id)).ToListAsync(cancellationToken))
        {
            if (listing.Status == LfgStatus.Full)
                listing.Status = LfgStatus.Open;
            listing.CardStale = listing.MessageId is not null;
            listing.CardSyncAttempts = 0;
            listing.Version++;
        }

        Participants.RemoveRange(memberships);
        deleted += memberships.Count;
        await db.SaveChangesAsync(cancellationToken);

        // Listings the user created (their own text): removed with all their players. A button on such a card then answers
        // "this listing no longer exists" and retires the buttons.
        var owned = Listings.Where(x => x.GuildId == guild.Value && x.OwnerUserId == user.Value);
        await Participants.Where(p => owned.Any(l => l.Id == p.ListingId)).ExecuteDeleteAsync(cancellationToken);
        deleted += await owned.ExecuteDeleteAsync(cancellationToken);

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

    private IQueryable<LfgParticipantEntity> JoinedElsewhere(GuildId guild, UserId user) =>
        Participants.Where(p => p.UserId == user.Value && Listings.Any(l => l.Id == p.ListingId && l.GuildId == guild.Value && l.OwnerUserId != user.Value));

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
