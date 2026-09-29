using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Giveaway.Persistence;

namespace ToroSquad.Modules.Giveaway.Application;

/// <summary>
/// /privacy for TSQ Giveaway: the giveaways a member started (their id and display name at the time) and the draws they won,
/// in one guild, and the ones they ended or cancelled as an admin. Entrants are never stored (they are the reactions in
/// Discord). Deletion removes the member's wins and clears their name and id from giveaways they started, ended or
/// cancelled (the giveaways themselves, and their draws, stay). Winner announcements
/// are ordinary channel messages; their outbox rows are pruned 24 hours after delivery.
/// </summary>
public sealed class GiveawayUserData(ToroDbContext db) : IUserDataContributor
{
    public ModuleId Module => GiveawayModule.ModuleIdTyped;

    public async Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var started = new JsonArray();
        foreach (var g in await Started(guild, user).AsNoTracking().OrderBy(g => g.Id).ToListAsync(cancellationToken))
        {
            started.Add(new JsonObject
            {
                ["giveaway"] = g.Id,
                ["displayName"] = g.CreatorName,
                ["createdAtUtc"] = g.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        var won = new JsonArray();
        foreach (var w in await Won(guild, user).AsNoTracking().OrderBy(w => w.GiveawayId).ThenBy(w => w.Round).ToListAsync(cancellationToken))
            won.Add(new JsonObject { ["giveaway"] = w.GiveawayId, ["round"] = w.Round, ["place"] = w.Place });

        var ended = new JsonArray();
        foreach (var id in await Ended(guild, user).AsNoTracking().OrderBy(g => g.Id).Select(g => g.Id).ToListAsync(cancellationToken))
            ended.Add(id);

        return new JsonObject { ["started"] = started, ["won"] = won, ["endedOrCancelled"] = ended };
    }

    public async Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var items = new List<DeletionPreviewItem>();
        var started = await Started(guild, user).CountAsync(cancellationToken);
        if (started > 0)
            items.Add(new DeletionPreviewItem("giveaway.privacy.started", started));
        var won = await Won(guild, user).CountAsync(cancellationToken);
        if (won > 0)
            items.Add(new DeletionPreviewItem("giveaway.privacy.won", won));
        var ended = await Ended(guild, user).CountAsync(cancellationToken);
        if (ended > 0)
            items.Add(new DeletionPreviewItem("giveaway.privacy.ended", ended));
        return items;
    }

    public async Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var deleted = await Won(guild, user).ExecuteDeleteAsync(cancellationToken);
        deleted += await Started(guild, user).ExecuteUpdateAsync(s => s.SetProperty(g => g.CreatorUserId, 0UL).SetProperty(g => g.CreatorName, ""), cancellationToken);
        deleted += await Ended(guild, user).ExecuteUpdateAsync(s => s.SetProperty(g => g.EndedByUserId, (ulong?)null), cancellationToken);
        return new DeletionReport(Module, deleted, []);
    }

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var ids = db.Set<GiveawayEntity>().Where(g => g.GuildId == guild.Value).Select(g => g.Id);
        var n = await db.Set<GiveawayWinnerEntity>().Where(w => ids.Contains(w.GiveawayId)).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<GiveawayEntity>().Where(g => g.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        return n;
    }

    private IQueryable<GiveawayEntity> Started(GuildId guild, UserId user) =>
        db.Set<GiveawayEntity>().Where(g => g.GuildId == guild.Value && g.CreatorUserId == user.Value);

    /// <summary>Giveaways the member (an admin) ended or cancelled by command.</summary>
    private IQueryable<GiveawayEntity> Ended(GuildId guild, UserId user) =>
        db.Set<GiveawayEntity>().Where(g => g.GuildId == guild.Value && g.EndedByUserId == user.Value);

    private IQueryable<GiveawayWinnerEntity> Won(GuildId guild, UserId user)
    {
        var ids = db.Set<GiveawayEntity>().Where(g => g.GuildId == guild.Value).Select(g => g.Id);
        return db.Set<GiveawayWinnerEntity>().Where(w => w.UserId == user.Value && ids.Contains(w.GiveawayId));
    }
}
