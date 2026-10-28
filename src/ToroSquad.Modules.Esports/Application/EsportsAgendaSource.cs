using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// CS2 for TSQ Ajanda: the cached matches (<see cref="EsportsCache"/>) that pass THIS guild's server filters — team,
/// tournament, tier and VRS, with exactly the notification planner's rules (<see cref="MatchFilter"/>: OR inside a
/// dimension, AND across them, no filter = every match). A match the filter cannot judge (VRS data missing) is left out and
/// counted, never let through. Delivery settings (channel, pings, reminders, spoiler mode, pause) and members' personal
/// follows play no part. Scores, winners and maps are never read; a finished match is left out. Links: the match's
/// reminder or start card when the outbox really delivered it here (never the result card), else the validated match page.
/// </summary>
public sealed class EsportsAgendaSource(
    EsportsCache cache,
    EsportsDataMode mode,
    IEsportsDataProvider provider,
    DeploymentPolicy deployment,
    ToroDbContext db,
    NotificationRenderer renderer,
    IOptions<EsportsOptions> options) : IAgendaSource
{
    /// <summary>A live match that started this long before the window can still be running (today).</summary>
    public static readonly TimeSpan LiveLookBack = TimeSpan.FromHours(12);

    public AgendaKind Kind => AgendaKind.Esports;

    public ModuleId Module => EsportsModule.ModuleIdTyped;

    public async Task<AgendaSourceResult> ReadAsync(AgendaQuery query, CancellationToken cancellationToken)
    {
        if (mode.IsDemo && !deployment.MayShowDemoData(query.Guild))
            return AgendaSourceResult.NotConfigured();
        if (!provider.IsConfigured)
            return AgendaSourceResult.NotConfigured();
        var state = cache.Matches;
        if (state.Data is null || state.FetchedAt is not { } fetchedAt)
            return state.LastOutcome is null or ProviderOutcome.NotConfigured ? AgendaSourceResult.NotConfigured() : AgendaSourceResult.Failed();
        var stale = state.IsStale(query.Now, cache.StaleAfter);

        var filters = await FiltersAsync(query.Guild, cancellationToken);
        var resolver = cache.Resolver;
        var undated = 0;
        var unverifiable = 0;
        var chosen = new List<(EsportsMatch Match, DateOnly? Date)>();
        foreach (var match in state.Data)
        {
            if (match.Status == MatchStatus.Finished)
                continue;
            if (match.ScheduledStartUtc is not { } start)
            {
                if (match.Status != MatchStatus.Cancelled)
                    undated++;
                continue;
            }

            DateOnly? date = match.StartTimeExact ? null : DateOnly.FromDateTime(start.UtcDateTime);
            var inWindow = date is { } d
                ? d >= query.FromDate && d < query.ToDate
                : (start >= query.FromUtc && start < query.ToUtc) || (match.Status == MatchStatus.Live && start < query.FromUtc && start >= query.FromUtc - LiveLookBack);
            if (!inWindow)
                continue;
            var verdict = MatchFilter.Evaluate(match, filters, resolver);
            if (verdict.Verdict == FilterVerdict.BlockedMissingData)
            {
                unverifiable++;
                continue;
            }

            if (verdict.Passes)
                chosen.Add((match, date));
        }

        var cards = await SentCards.FindAsync(db, query.Guild, EsportsModule.ModuleIdValue, chosen.Select(c => c.Match.Key.ToString()).ToList(), cancellationToken);
        var items = chosen.Select(c => new AgendaItem(
                AgendaKind.Esports,
                c.Match.Key.ToString(),
                Side(c.Match.A) + " — " + Side(c.Match.B),
                c.Match.BestOf is { } bo and > 0 ? c.Match.Tournament.Name + " · BO" + bo : c.Match.Tournament.Name,
                c.Date is null ? c.Match.ScheduledStartUtc : null,
                c.Date,
                Status(c.Match.Status),
                Link(c.Match, cards)))
            .ToList();
        return new AgendaSourceResult(stale ? AgendaSourceState.Stale : AgendaSourceState.Ready, items, fetchedAt,
            fetchedAt + TimeSpan.FromHours(options.Value.FutureWindowHours), mode.IsDemo, undated, unverifiable);
    }

    /// <summary>The guild's server filters, loaded as the notification planner loads them (no row = no filter).</summary>
    private async Task<GuildFilterSet> FiltersAsync(GuildId guild, CancellationToken ct)
    {
        var config = await db.Set<EsportsGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        var rows = await db.Set<EsportsFilterEntity>().AsNoTracking().Where(f => f.GuildId == guild.Value).ToListAsync(ct);
        HashSet<string> Of(FilterDimension d) => rows.Where(r => r.Dimension == (int)d).Select(r => r.Value).ToHashSet(StringComparer.Ordinal);
        return new GuildFilterSet(Of(FilterDimension.Team), Of(FilterDimension.Tournament), Of(FilterDimension.Tier), config?.VrsTopN);
    }

    private static string Side(MatchOpponent side) => side.Team?.Name is { Length: > 0 } name ? name : "TBD";

    private static AgendaStatus Status(MatchStatus status) => status switch
    {
        MatchStatus.Live => AgendaStatus.InProgress,
        MatchStatus.Postponed => AgendaStatus.Postponed,
        MatchStatus.Cancelled => AgendaStatus.Cancelled,
        _ => AgendaStatus.Scheduled,
    };

    /// <summary>Reminder card, else start card (neither ever carries a score), else the validated match page.</summary>
    private AgendaLink? Link(EsportsMatch match, IReadOnlyList<SentCard> cards)
    {
        var key = match.Key.ToString();
        var card = cards.Where(c => c.SourceKey == key && c.Kind == "reminder").OrderByDescending(c => c.SentAt).FirstOrDefault()
                   ?? cards.Where(c => c.SourceKey == key && c.Kind == "started").OrderByDescending(c => c.SentAt).FirstOrDefault();
        if (card is not null)
            return AgendaLink.ToCard(card.Channel, card.Message);
        return renderer.MatchPageFor(match) is { } page ? AgendaLink.ToPage(page.Url) : null;
    }
}
