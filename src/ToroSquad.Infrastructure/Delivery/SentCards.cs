using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Infrastructure.Delivery;

/// <summary>A card the outbox really delivered: its channel and Discord message.</summary>
public sealed record SentCard(string SourceKey, string Kind, ChannelId Channel, MessageId Message, DateTimeOffset? SentAt);

/// <summary>
/// Read-only lookup of delivered cards (for links to them). Only rows of THIS guild and module, with status Sent, not a
/// dry run, and a known Discord message id count — pending, failed, unknown, cancelled, expired or simulated rows never do.
/// Bounded by the given source keys (no history scan); nothing is written.
/// </summary>
public static class SentCards
{
    public const int MaxKeys = 200;

    public static async Task<IReadOnlyList<SentCard>> FindAsync(ToroDbContext db, GuildId guild, string moduleId, IReadOnlyCollection<string> sourceKeys,
        CancellationToken cancellationToken)
    {
        if (sourceKeys.Count == 0)
            return [];
        var keys = sourceKeys.Distinct(StringComparer.Ordinal).Take(MaxKeys).ToList();
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.GuildId == guild.Value && o.ModuleId == moduleId && o.Status == OutboxStatus.Sent && !o.IsDryRun &&
                        o.DiscordMessageId != null && keys.Contains(o.SourceKey))
            .Select(o => new { o.SourceKey, o.Kind, o.ChannelId, o.DiscordMessageId, o.SentAt })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new SentCard(r.SourceKey, r.Kind, new ChannelId(r.ChannelId), new MessageId(r.DiscordMessageId!.Value), r.SentAt)).ToList();
    }
}
