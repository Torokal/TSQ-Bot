using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// LFG for TSQ Gündem: this guild's ACTIVE listings only — Open or Full and not expired (also when the expiry worker has not
/// written that yet). A listing without a planned time is "looking for players now" (its creation time is never shown as an
/// event time); one whose planned time passed while it is still open says exactly that. Only the game name, the time, the
/// joined count of the capacity (maybe and waitlist never count) and the card link leave this class — no members, details
/// or voice channels. Each item carries its channel: the agenda shows it elsewhere only if that channel is open to everyone.
/// </summary>
public sealed class LfgAgendaSource(ToroDbContext db) : IAgendaSource
{
    public const int MaxListings = 200;

    public AgendaKind Kind => AgendaKind.Lfg;

    public ModuleId Module => LfgModule.ModuleIdTyped;

    public async Task<AgendaSourceResult> ReadAsync(AgendaQuery query, CancellationToken cancellationToken)
    {
        var now = query.Now;
        var listings = await db.Set<LfgListingEntity>().AsNoTracking()
            .Where(l => l.GuildId == query.Guild.Value && (l.Status == LfgStatus.Open || l.Status == LfgStatus.Full) && l.ExpiresAt > now &&
                        (l.EventAt == null || l.EventAt < query.ToUtc))
            .OrderBy(l => l.Id)
            .Take(MaxListings + 1)
            .Select(l => new { l.Id, l.ChannelId, l.MessageId, l.GameName, l.MaxPlayers, l.Status, l.EventAt })
            .ToListAsync(cancellationToken);
        var truncated = listings.Count > MaxListings;
        if (truncated)
            listings.RemoveAt(MaxListings);
        var ids = listings.Select(l => l.Id).ToList();
        var joined = await db.Set<LfgParticipantEntity>().AsNoTracking()
            .Where(p => ids.Contains(p.ListingId) && p.Response == LfgResponse.Joined)
            .GroupBy(p => p.ListingId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

        var items = listings.Select(l =>
        {
            var count = joined.GetValueOrDefault(l.Id);
            var status = l.EventAt is not { } at ? AgendaStatus.LookingNow : at <= now ? AgendaStatus.ListingStartPassed : AgendaStatus.Scheduled;
            return new AgendaItem(
                AgendaKind.Lfg,
                "listing:" + l.Id,
                l.GameName,
                null,
                l.EventAt,
                null,
                status,
                l.MessageId is { } message ? AgendaLink.ToCard(new ChannelId(l.ChannelId), new MessageId(message), AgendaLinkKind.Listing) : null,
                new AgendaSlots(count, l.MaxPlayers, l.Status == LfgStatus.Full || count >= l.MaxPlayers),
                new ChannelId(l.ChannelId));
        }).ToList();
        return new AgendaSourceResult(AgendaSourceState.Ready, items, Truncated: truncated);
    }
}
