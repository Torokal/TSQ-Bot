using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

/// <summary>
/// F1 for TSQ Ajanda: every session of the cached calendar (practice, sprint qualifying, sprint, qualifying, race — one item
/// per session, never an extra item per weekend), whatever the guild's notification switches say. Read from
/// <see cref="Formula1Cache"/> only. Results, standings and race control are never read: a running or red-flagged
/// session is only "in progress" (and only from current lifecycle data), a finished one is left out, never shown with a
/// result. Links: the weekend-schedule card (and for a race its reminder card) when the outbox really delivered it here.
/// </summary>
public sealed class Formula1AgendaSource(
    Formula1Cache cache,
    F1DataMode mode,
    DeploymentPolicy deployment,
    ToroDbContext db,
    IOptions<Formula1Options> options,
    ILocalizer localizer) : IAgendaSource
{
    /// <summary>A lifecycle state older than this is not shown as live (as /f1 does).</summary>
    public static readonly TimeSpan LiveStateMaxAge = TimeSpan.FromHours(16);

    public AgendaKind Kind => AgendaKind.Formula1;

    public ModuleId Module => Formula1Module.ModuleIdTyped;

    public async Task<AgendaSourceResult> ReadAsync(AgendaQuery query, CancellationToken cancellationToken)
    {
        if (mode.IsDemo && !deployment.MayShowDemoData(query.Guild))
            return AgendaSourceResult.NotConfigured();
        var feed = cache.Schedule;
        if (feed.Data is null || feed.FetchedAt is not { } fetchedAt)
            return feed.LastOutcome is null || feed.LastOutcome == F1ProviderOutcome.NotConfigured ? AgendaSourceResult.NotConfigured() : AgendaSourceResult.Failed();
        var stale = feed.IsStale(query.Now, options.Value.ScheduleStaleAfter);

        var sessions = cache.Sessions
            .Where(s => s.Session.Type != F1SessionType.Unknown && !s.IsBaseline)
            .Where(s => s.State is not (F1SessionState.FinishedPendingResults or F1SessionState.Finalised))
            .Where(s => InWindow(s, query))
            .ToList();
        var cards = await CardsAsync(query.Guild, sessions, cancellationToken);
        var items = sessions.Select(s => new AgendaItem(
                AgendaKind.Formula1,
                s.Session.Key,
                s.MeetingName + " — " + localizer.Get(query.Language, "f1.session." + F1SessionTypes.Slug(s.Session.Type)),
                s.CircuitName,
                s.Session.ScheduledStartUtc,
                null,
                Status(s, query.Now),
                Link(s, cards)))
            .ToList();
        var lastSeason = feed.Data.Count == 0 ? (int?)null : feed.Data.Max(x => x.Season);
        var covers = lastSeason is { } season ? new DateTimeOffset(season + 1, 1, 1, 0, 0, 0, TimeSpan.Zero) : (DateTimeOffset?)null;
        return new AgendaSourceResult(stale ? AgendaSourceState.Stale : AgendaSourceState.Ready, items, fetchedAt, covers, mode.IsDemo);
    }

    private static bool InWindow(F1SessionView s, AgendaQuery q)
    {
        var start = s.Session.ScheduledStartUtc;
        if (start >= q.FromUtc && start < q.ToUtc)
            return true;
        // A session that started before the window but is running now (current lifecycle data) belongs to today.
        return start < q.FromUtc && IsRunning(s, q.Now);
    }

    private static bool IsRunning(F1SessionView s, DateTimeOffset now) =>
        s.State is F1SessionState.Started or F1SessionState.Suspended && (s.LastLifecycleEventAt ?? s.StartedAt) is { } at && now - at <= LiveStateMaxAge;

    /// <summary>Suspended (red flag) is reported exactly like Started: "in progress", nothing more.</summary>
    private static AgendaStatus Status(F1SessionView s, DateTimeOffset now) => s.State switch
    {
        F1SessionState.Cancelled => AgendaStatus.Cancelled,
        F1SessionState.Started or F1SessionState.Suspended when IsRunning(s, now) => AgendaStatus.InProgress,
        _ => AgendaStatus.Scheduled,
    };

    private Task<IReadOnlyList<SentCard>> CardsAsync(GuildId guild, List<F1SessionView> sessions, CancellationToken ct) =>
        SentCards.FindAsync(db, guild, Formula1Module.ModuleIdValue,
            sessions.Select(s => s.Session.MeetingKey).Concat(sessions.Where(s => s.Session.Type == F1SessionType.Race).Select(s => s.Session.Key)).ToList(), ct);

    /// <summary>The race's reminder card, else the weekend-schedule card of its meeting — programme cards only (never a result card).</summary>
    private static AgendaLink? Link(F1SessionView s, IReadOnlyList<SentCard> cards)
    {
        var card = (s.Session.Type == F1SessionType.Race ? Latest(cards, s.Session.Key, "race_reminder") : null)
                   ?? Latest(cards, s.Session.MeetingKey, "weekend_schedule");
        return card is null ? null : AgendaLink.ToCard(card.Channel, card.Message);
    }

    private static SentCard? Latest(IReadOnlyList<SentCard> cards, string sourceKey, string kind) =>
        cards.Where(c => c.SourceKey == sourceKey && c.Kind == kind).OrderByDescending(c => c.SentAt).FirstOrDefault();
}
