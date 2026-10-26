using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Domain;
using ToroSquad.Modules.Sor.Persistence;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>
/// /privacy for TSQ Sor, always the calling member in the current guild: export (settings, notes, conversations with their
/// turns, request metadata), preview, delete, and the guild purge after the bot left. Works while the module is disabled.
/// Delete removes the member's settings, notes, conversations, turns and request records (so their personal daily count goes
/// too); the guild's daily totals carry no member id and stay, so the guild limit is not renewed by a deletion. Other
/// members' conversations that used one of the member's shared notes are invalidated. Answers already posted on Discord,
/// and copies the provider or a backup may hold, are not removed by this.
/// </summary>
public sealed class SorUserData(ToroDbContext db, SorMemoryStore memory) : IUserDataContributor
{
    public ModuleId Module => SorModule.ModuleIdTyped;

    public async Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var settings = await db.Set<SorUserEntity>().AsNoTracking().FirstOrDefaultAsync(u => u.GuildId == guild.Value && u.UserId == user.Value, cancellationToken);
        var notes = new JsonArray();
        foreach (var m in await Notes(guild, user).AsNoTracking().OrderBy(m => m.Id).ToListAsync(cancellationToken))
        {
            notes.Add(new JsonObject
            {
                ["kind"] = SorMemoryKinds.Wire(m.Kind),
                ["text"] = m.Text,
                ["scope"] = m.Scope.ToString(),
                ["sourceChannelId"] = m.SourceChannelId?.ToString(CultureInfo.InvariantCulture),
                ["origin"] = m.Origin.ToString(),
                ["version"] = m.Version,
                ["createdAtUtc"] = Iso(m.CreatedAt),
                ["updatedAtUtc"] = Iso(m.UpdatedAt),
            });
        }

        var conversations = new JsonArray();
        foreach (var c in await Conversations(guild, user).AsNoTracking().OrderBy(c => c.Id).ToListAsync(cancellationToken))
        {
            var turns = new JsonArray();
            foreach (var t in await db.Set<SorTurnEntity>().AsNoTracking().Where(t => t.ConversationId == c.Id).OrderBy(t => t.Seq).ToListAsync(cancellationToken))
                turns.Add(new JsonObject { ["question"] = t.Question, ["answer"] = t.Answer, ["atUtc"] = Iso(t.CreatedAt) });
            conversations.Add(new JsonObject
            {
                ["channelId"] = c.ChannelId.ToString(CultureInfo.InvariantCulture),
                ["private"] = c.IsPrivate,
                ["state"] = c.State.ToString(),
                ["createdAtUtc"] = Iso(c.CreatedAt),
                ["lastActivityAtUtc"] = Iso(c.LastActivityAt),
                ["turns"] = turns,
            });
        }

        var requests = new JsonArray();
        foreach (var r in await Requests(guild, user).AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken))
        {
            requests.Add(new JsonObject
            {
                ["day"] = r.DayKey,
                ["state"] = r.State.ToString(),
                ["private"] = r.IsPrivate,
                ["inputTokens"] = r.InputTokens,
                ["outputTokens"] = r.OutputTokens,
                ["atUtc"] = Iso(r.CreatedAt),
            });
        }

        return new JsonObject
        {
            ["memoryEnabled"] = settings?.MemoryEnabled,
            ["noticeShownAtUtc"] = settings?.NoticeShownAt is { } shown ? Iso(shown) : null,
            ["notes"] = notes,
            ["conversations"] = conversations,
            ["requests"] = requests,
        };
    }

    public async Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var items = new List<DeletionPreviewItem>();
        var notes = await Notes(guild, user).CountAsync(cancellationToken);
        if (notes > 0)
            items.Add(new DeletionPreviewItem("sor.privacy.notes", notes));
        var conversations = await Conversations(guild, user).CountAsync(cancellationToken);
        if (conversations > 0)
            items.Add(new DeletionPreviewItem("sor.privacy.conversations", conversations));
        var requests = await Requests(guild, user).CountAsync(cancellationToken);
        if (requests > 0)
            items.Add(new DeletionPreviewItem("sor.privacy.requests", requests));
        return items;
    }

    public Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken) => SorWrites.RunAsync(db, async () =>
    {
        var noteIds = await Notes(guild, user).Select(m => m.Id).ToListAsync(cancellationToken);
        await memory.InvalidateAsync(noteIds, includeSources: true, keepOwner: null, cancellationToken);
        var deleted = await memory.DeleteConversationsAsync(await Conversations(guild, user).Select(c => c.Id).ToListAsync(cancellationToken), cancellationToken);
        deleted += await Notes(guild, user).ExecuteDeleteAsync(cancellationToken);
        deleted += await Requests(guild, user).ExecuteDeleteAsync(cancellationToken);
        deleted += await db.Set<SorUserEntity>().Where(u => u.GuildId == guild.Value && u.UserId == user.Value).ExecuteDeleteAsync(cancellationToken);
        return new DeletionReport(Module, deleted, []);
    }, cancellationToken);

    public Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken) => SorWrites.RunAsync(db, async () =>
    {
        var g = guild.Value;
        var n = await db.Set<SorTurnEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        var conversations = db.Set<SorConversationEntity>().Where(x => x.GuildId == g).Select(x => x.Id);
        n += await db.Set<SorMemoryUseEntity>().Where(x => conversations.Contains(x.ConversationId)).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<SorConversationEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<SorMemoryEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<SorRequestEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<SorUserEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<SorGuildUsageEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<SorGuildConfigEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        return n;
    }, cancellationToken);

    private IQueryable<SorMemoryEntity> Notes(GuildId guild, UserId user) => db.Set<SorMemoryEntity>().Where(m => m.GuildId == guild.Value && m.UserId == user.Value);

    private IQueryable<SorConversationEntity> Conversations(GuildId guild, UserId user) =>
        db.Set<SorConversationEntity>().Where(c => c.GuildId == guild.Value && c.OwnerUserId == user.Value);

    private IQueryable<SorRequestEntity> Requests(GuildId guild, UserId user) => db.Set<SorRequestEntity>().Where(r => r.GuildId == guild.Value && r.UserId == user.Value);

    private static string Iso(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
}
