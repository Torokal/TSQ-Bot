using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// TSQ Öngörü for TSQ Gündem: this guild's predictions that still take entries or are closed but not settled, whose closing
/// time falls in the window; an open one without a closing time is open now. Programme only — the title, the closing time
/// and open/closed leave this class; never the outcomes, the winner, entries, stakes, coins or who created it. A settled,
/// cancelled, abandoned or still-publishing prediction is not listed at all (the agenda never hints at a result). The card
/// is linked only while it is known to exist (not reported missing, not removed by the retention cleanup). Unlike an LFG
/// listing, an item carries no audience channel: predictions are published only in the one channel the bot's configuration
/// names for them, never in a channel a member picked, and the game is open to the whole guild.
/// </summary>
public sealed class PredictionsAgendaSource(ToroDbContext db) : IAgendaSource
{
    public const int MaxPredictions = 100;

    public AgendaKind Kind => AgendaKind.Predictions;

    public ModuleId Module => PredictionsModule.ModuleIdTyped;

    public async Task<AgendaSourceResult> ReadAsync(AgendaQuery query, CancellationToken cancellationToken)
    {
        var rows = await db.Set<PredictionEntity>().AsNoTracking()
            .Where(p => p.GuildId == query.Guild.Value && (p.Status == PredictionStatus.Open || p.Status == PredictionStatus.Locked) &&
                        (p.LockAt == null ? p.Status == PredictionStatus.Open : p.LockAt >= query.FromUtc && p.LockAt < query.ToUtc))
            .OrderBy(p => p.Id)
            .Take(MaxPredictions + 1)
            .Select(p => new { p.Id, p.ChannelId, p.MessageId, p.Title, p.LockAt, p.Status, p.CardMissing, p.CardRemovedAt })
            .ToListAsync(cancellationToken);
        var truncated = rows.Count > MaxPredictions;
        if (truncated)
            rows.RemoveAt(MaxPredictions);

        var items = rows.Select(p => new AgendaItem(
                AgendaKind.Predictions,
                "prediction:" + p.Id,
                p.Title,
                null,
                p.LockAt,
                null,
                p.Status == PredictionStatus.Open ? AgendaStatus.EntriesOpen : AgendaStatus.EntriesClosed,
                p.MessageId is { } message && !p.CardMissing && p.CardRemovedAt is null ? AgendaLink.ToCard(new ChannelId(p.ChannelId), new MessageId(message), AgendaLinkKind.Prediction) : null))
            .ToList();
        return new AgendaSourceResult(AgendaSourceState.Ready, items, Truncated: truncated);
    }
}
