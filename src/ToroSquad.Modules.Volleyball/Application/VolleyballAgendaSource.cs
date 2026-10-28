using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

/// <summary>
/// Volleyball for TSQ Ajanda: exactly what the module follows (Filenin Sultanları — Türkiye women's senior national team),
/// from <see cref="VolleyballCache"/> only; nothing else is added. Sets, points and scores are never read; a finished match
/// is left out. A match without a confirmed date and time is not placed on a day (counted instead). "In progress" only
/// while the provider's live observation is recent. Links: the match's reminder or start card when the outbox really
/// delivered it here (never the set or final cards, which carry scores).
/// </summary>
public sealed class VolleyballAgendaSource(
    VolleyballCache cache,
    VbDataMode mode,
    VbSources sources,
    IVolleyballDataProvider provider,
    DeploymentPolicy deployment,
    ToroDbContext db,
    IOptions<VolleyballOptions> options) : IAgendaSource
{
    /// <summary>A live observation older than this does not prove the match is still running.</summary>
    public static readonly TimeSpan LiveObservationMaxAge = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan LiveLookBack = TimeSpan.FromHours(6);

    public AgendaKind Kind => AgendaKind.Volleyball;

    public ModuleId Module => VolleyballModule.ModuleIdTyped;

    public async Task<AgendaSourceResult> ReadAsync(AgendaQuery query, CancellationToken cancellationToken)
    {
        if (mode.IsDemo && !deployment.MayShowDemoData(query.Guild))
            return AgendaSourceResult.NotConfigured();
        if (!sources.Configured)
            return AgendaSourceResult.NotConfigured();
        var feed = cache.Fixtures;
        if (feed.FetchedAt is not { } fetchedAt)
            return feed.LastOutcome is null or VbProviderOutcome.NotConfigured ? AgendaSourceResult.NotConfigured() : AgendaSourceResult.Failed();
        var stale = feed.IsStale(query.Now, options.Value.FixtureStaleAfter);

        var undated = 0;
        var chosen = new List<VbMatchView>();
        foreach (var match in cache.Matches.Where(m => m.Provider == provider.Id))
        {
            if (match.Finished || match.Status == VolleyballMatchStatus.Finished)
                continue;
            if (match.StartTimeUtc is not { } start)
            {
                if (!match.Cancelled)
                    undated++;
                continue;
            }

            if ((start >= query.FromUtc && start < query.ToUtc) || (Running(match, query.Now) && start < query.FromUtc && start >= query.FromUtc - LiveLookBack))
                chosen.Add(match);
        }

        var cards = await SentCards.FindAsync(db, query.Guild, VolleyballModule.ModuleIdValue, chosen.Select(m => m.MatchKey).ToList(), cancellationToken);
        var items = chosen.Select(m => new AgendaItem(
                AgendaKind.Volleyball,
                m.MatchKey,
                m.HomeName + " — " + m.AwayName,
                string.Join(" · ", new[] { m.CompetitionName, m.Stage, m.Round }.Where(s => !string.IsNullOrWhiteSpace(s))),
                m.StartTimeUtc,
                null,
                Status(m, query.Now),
                Link(m, cards)))
            .ToList();
        return new AgendaSourceResult(stale ? AgendaSourceState.Stale : AgendaSourceState.Ready, items, fetchedAt,
            fetchedAt + TimeSpan.FromDays(options.Value.FixtureWindowAheadDays), mode.IsDemo, undated);
    }

    private static bool Running(VbMatchView m, DateTimeOffset now) =>
        m.Status is VolleyballMatchStatus.Live or VolleyballMatchStatus.Suspended && now - m.LastObservedAt <= LiveObservationMaxAge;

    private static AgendaStatus Status(VbMatchView m, DateTimeOffset now) => m switch
    {
        { Cancelled: true } or { Status: VolleyballMatchStatus.Cancelled } => AgendaStatus.Cancelled,
        { Postponed: true } or { Status: VolleyballMatchStatus.Postponed } => AgendaStatus.Postponed,
        _ when Running(m, now) => AgendaStatus.InProgress,
        _ => AgendaStatus.Scheduled,
    };

    /// <summary>The latest reminder card, else the start card (neither carries a score).</summary>
    private static AgendaLink? Link(VbMatchView m, IReadOnlyList<SentCard> cards)
    {
        var card = SentCards.Latest(cards, m.MatchKey, k => k.StartsWith("reminder:", StringComparison.Ordinal))
                   ?? SentCards.Latest(cards, m.MatchKey, k => k == "started");
        return card is null ? null : AgendaLink.ToCard(card.Channel, card.Message);
    }
}
