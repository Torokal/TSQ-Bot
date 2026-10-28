using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Infrastructure.Delivery;

/// <summary>
/// A card the outbox really delivered: its channel and Discord message. <paramref name="Gone"/>: a later edit found the
/// message deleted or its channel gone (the outbox recorded it) — such a card is never linked.
/// </summary>
public sealed record SentCard(string SourceKey, string Kind, ChannelId Channel, MessageId Message, DateTimeOffset? SentAt, bool Gone = false);

/// <summary>
/// Read-only lookup of delivered cards (for links to them). Only rows of THIS guild and module, with status Sent, not a
/// dry run, and a known Discord message id count — pending, in-flight, failed, unknown, cancelled, expired or simulated rows
/// never do. Bounded by the given source keys (no history scan, no Discord call); nothing is written. What it returns is the
/// last known state: a message deleted without a later edit is not known here.
/// </summary>
public static class SentCards
{
    public const int MaxKeys = 200;

    /// <summary><c>LastError</c> the outbox writes when an edit finds the message deleted (OutboxProcessor).</summary>
    public const string DeletedMarker = "edit_target_deleted";

    /// <summary><c>LastError</c> prefix the outbox writes when an edit finds the channel gone (OutboxProcessor: <c>edit_failed {kind}: …</c>).</summary>
    public const string ChannelGonePrefix = "edit_failed UnknownChannel";

    public static async Task<IReadOnlyList<SentCard>> FindAsync(ToroDbContext db, GuildId guild, string moduleId, IReadOnlyCollection<string> sourceKeys,
        CancellationToken cancellationToken)
    {
        if (sourceKeys.Count == 0)
            return [];
        var keys = sourceKeys.Distinct(StringComparer.Ordinal).Take(MaxKeys).ToList();
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.GuildId == guild.Value && o.ModuleId == moduleId && o.Status == OutboxStatus.Sent && !o.IsDryRun &&
                        o.DiscordMessageId != null && keys.Contains(o.SourceKey))
            .Select(o => new { o.SourceKey, o.Kind, o.ChannelId, o.DiscordMessageId, o.SentAt, o.LastError })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new SentCard(r.SourceKey, r.Kind, new ChannelId(r.ChannelId), new MessageId(r.DiscordMessageId!.Value), r.SentAt,
            r.LastError is { } error && (error == DeletedMarker || error.StartsWith(ChannelGonePrefix, StringComparison.Ordinal)))).ToList();
    }

    /// <summary>
    /// The newest delivered card of one source key and kind family — or none when that newest card is known to be gone (an
    /// older card of the same family would show an outdated programme, so it is not used instead).
    /// </summary>
    public static SentCard? Latest(IEnumerable<SentCard> cards, string sourceKey, Func<string, bool> kind) =>
        cards.Where(c => c.SourceKey == sourceKey && kind(c.Kind))
            .OrderByDescending(c => c.SentAt)
            .ThenByDescending(c => c.Message.Value)
            .FirstOrDefault() is { Gone: false } card
            ? card
            : null;
}
