using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

public sealed record F1PlanReport(int GuildsConsidered, int Created, int Updated, int Suppressed, bool SkippedStale);

/// <summary>
/// Turns persisted session state into outbox rows for every eligible guild, in ONE database transaction. It never sends
/// anything itself: the durable outbox provides retries, the module gate, pause/channel checks, reconciliation,
/// duplicate protection, locked allowed_mentions and ping-free edits. Policies (docs/FORMULA1.md):
/// <list type="bullet">
/// <item>A "started" card needs a provider-stated first start (never the clock), still running, after the guild's
/// watermark and within <see cref="Formula1Options.StartFreshMinutes"/> of the provider's start time.</item>
/// <item>A result card needs a validated classification fetched recently (stale data creates nothing and edits nothing).</item>
/// <item>One message per guild + session + channel + kind; corrections and late standings edit it without a ping.</item>
/// <item>Baseline sessions (already over when first seen) are never announced; catch-up after downtime is bounded.</item>
/// </list>
/// </summary>
public sealed class Formula1NotificationPlanner(
    ToroDbContext db,
    INotificationOutbox outbox,
    IModuleGate gate,
    IGuildSettingsStore guildSettings,
    Formula1NotificationRenderer renderer,
    Formula1Cache cache,
    F1DataMode mode,
    DeploymentPolicy deployment,
    IF1ScheduleProvider schedule,
    IF1LifecycleProvider lifecycle,
    IF1ResultsProvider results,
    IF1StandingsProvider standings,
    IOptions<Formula1Options> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<Formula1NotificationPlanner> logger)
{
    public const string StartedPrefix = "started:";
    public const string ResultPrefix = "result:";

    // Low-spam V2 kinds. The logical key (guild + session/meeting + channel + kind) makes each of them one message at most.
    public const string WeekendScheduleKind = "weekend_schedule";
    public const string RaceReminderKind = "race_reminder";
    public const string SafetyCarPrefix = "safety_car:";
    public const string RedFlagPrefix = "red_flag:";
    public const string DisqualificationPrefix = "disqualification:";

    /// <summary>The race reminder is sent exactly this long before the scheduled race start.</summary>
    public static readonly TimeSpan RaceReminderLead = TimeSpan.FromMinutes(15);

    public static string KindStarted(F1SessionType type) => StartedPrefix + F1SessionTypes.Slug(type);

    public static string KindResult(F1SessionType type) => ResultPrefix + F1SessionTypes.Slug(type);

    public async Task<F1PlanReport> PlanAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PlanOnceAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // The dispatcher updated an outbox row we were staging into; planning is deterministic from DB state.
                logger.LogInformation("F1 planner conflict with dispatcher; recomputing (attempt {Attempt})", attempt + 1);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<F1PlanReport> PlanOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        if (cache.Schedule.IsStale(now, o.ScheduleStaleAfter))
        {
            logger.LogWarning("F1 planner skipped: schedule data is stale or missing (last success {At})", cache.Schedule.FetchedAt);
            return new F1PlanReport(0, 0, 0, 0, SkippedStale: true);
        }

        var fresh = TimeSpan.FromMinutes(o.StartFreshMinutes);
        var startFrom = now - fresh;
        var resultFrom = now - TimeSpan.FromHours(o.ResultCorrectionHours);
        var sessions = await db.Set<F1SessionSnapshotEntity>().AsNoTracking()
            .Where(s => !s.IsBaseline && ((s.StartedObservedAt != null && s.StartedObservedAt >= startFrom) ||
                                          (s.FinalisedObservedAt != null && s.FinalisedObservedAt >= resultFrom)))
            .ToListAsync(ct);

        // V2: the next days of the canonical schedule (weekend card, race reminder) and recent race-control incidents.
        var upcomingFrom = now - TimeSpan.FromDays(2);
        var upcomingTo = now + TimeSpan.FromDays(8);
        var upcoming = await db.Set<F1SessionSnapshotEntity>().AsNoTracking()
            .Where(s => s.ScheduledStartUtc >= upcomingFrom && s.ScheduledStartUtc <= upcomingTo)
            .ToListAsync(ct);
        var incidentFrom = now - TimeSpan.FromHours(Math.Max(o.DisqualificationFreshHours, 1));
        var incidentKeys = await db.Set<F1RaceControlEventEntity>().AsNoTracking().Where(e => e.OccurredAt >= incidentFrom)
            .Select(e => e.SessionKey).Distinct().ToListAsync(ct);
        List<F1SessionSnapshotEntity> incidentSessions = incidentKeys.Count == 0
            ? []
            : await db.Set<F1SessionSnapshotEntity>().AsNoTracking().Where(s => incidentKeys.Contains(s.SessionKey) && !s.IsBaseline).ToListAsync(ct);
        List<F1RaceControlEventEntity> incidents = incidentKeys.Count == 0
            ? []
            : await db.Set<F1RaceControlEventEntity>().AsNoTracking().Where(e => incidentKeys.Contains(e.SessionKey)).ToListAsync(ct);
        if (sessions.Count == 0 && upcoming.Count == 0 && incidents.Count == 0)
            return new F1PlanReport(0, 0, 0, 0, SkippedStale: false);

        var keys = sessions.Select(s => s.SessionKey).Concat(incidentSessions.Select(s => s.SessionKey)).Distinct().ToList();
        var resultRows = await db.Set<F1ResultSnapshotEntity>().AsNoTracking().Where(r => keys.Contains(r.SessionKey)).ToDictionaryAsync(r => r.SessionKey, StringComparer.Ordinal, ct);
        var snapshotIds = sessions.SelectMany(s => new[] { s.StandingsDriversSnapshotId, s.StandingsConstructorsSnapshotId }).OfType<long>().Distinct().ToList();
        var standingsRows = await db.Set<F1StandingsSnapshotEntity>().AsNoTracking().Where(s => snapshotIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var dryRun = delivery.Value.Mode != DeliveryMode.Send;
        var existingKeys = (await db.Outbox.AsNoTracking()
                .Where(x => x.ModuleId == Formula1Module.ModuleIdValue && keys.Contains(x.SourceKey))
                .Select(x => x.LogicalKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var configs = await db.Set<Formula1GuildConfigEntity>().AsNoTracking()
            .Where(c => c.ChannelId != null && !c.Paused && c.ChannelProblem == null)
            .ToListAsync(ct);

        int guilds = 0, created = 0, updated = 0, suppressed = 0;
        foreach (var config in configs)
        {
            var guild = new GuildId(config.GuildId);
            if (!deployment.IsGuildAllowed(guild) || !await gate.IsEnabledAsync(guild, Formula1Module.ModuleIdTyped, ct))
                continue;
            if (mode.IsDemo && !deployment.MayShowDemoData(guild))
                continue; // fixture data only ever reaches explicitly authorized test guilds
            guilds++;
            var channel = new ChannelId(config.ChannelId!.Value);
            var language = (await guildSettings.GetAsync(guild, ct)).Language;
            var newResults = 0;

            var v2 = new PlanV2(guild, channel, language, dryRun, now);
            var (scheduledCreated, scheduledUpdated) = await PlanScheduledAsync(config, v2, upcoming, o, ct);
            var (incidentCreated, incidentUpdated) = await PlanIncidentsAsync(config, v2, incidentSessions, incidents, resultRows, o, ct);
            created += scheduledCreated + incidentCreated;
            updated += scheduledUpdated + incidentUpdated;

            foreach (var s in sessions.OrderBy(s => s.ScheduledStartUtc))
            {
                var view = Formula1Workflow.ToView(s);
                var type = view.Session.Type;

                // ---- started
                if (s.StartedObservedAt is { } startedAt && Enabled(config, type, start: true) &&
                    (F1SessionState)s.State is F1SessionState.Started or F1SessionState.Suspended &&
                    startedAt >= config.WatermarkUtc && now - startedAt <= fresh)
                {
                    var pings = Pings(config, guild, start: true);
                    var outcome = await outbox.StageAsync(new NotificationRequest(guild, Formula1Module.ModuleIdTyped, s.SessionKey, channel, KindStarted(type),
                        renderer.Started(view, language, pings, lifecycle.AttributionKey), startedAt + fresh, dryRun), ct);
                    Count(outcome, ref created, ref updated);
                }

                // ---- result (+ standings section)
                if (s.FinalisedObservedAt is not { } finalisedAt || !resultRows.TryGetValue(s.SessionKey, out var row) || !Enabled(config, type, start: false))
                    continue;
                if (now - row.FetchedAt > o.ResultStaleAfter)
                    continue; // stale classification: no new message, no edit
                if (F1Json.Deserialize<F1SessionResult>(row.PayloadJson) is not { } parsed)
                    continue;

                var cached = new F1CachedResult(parsed, row.FetchedAt, row.FirstAvailableAt);
                var key = NotificationRequest.BuildLogicalKey(guild, Formula1Module.ModuleIdTyped, s.SessionKey, channel, KindResult(type), dryRun);
                var exists = existingKeys.Contains(key);
                var inCorrectionWindow = now - finalisedAt <= TimeSpan.FromHours(o.ResultCorrectionHours);
                var eligible = !exists && finalisedAt >= config.WatermarkUtc &&
                               view.Session.PlannedEndUtc >= now - TimeSpan.FromHours(o.ResultCatchUpHours);
                var standingsPart = Standings(s, config, standingsRows, now);
                if (eligible && newResults >= o.MaxCatchUpPerGuildPerRun)
                {
                    // Over the catch-up limit: recorded as already expired so it is never delivered later either.
                    await outbox.StageAsync(new NotificationRequest(guild, Formula1Module.ModuleIdTyped, s.SessionKey, channel, KindResult(type),
                        renderer.Result(view, cached, standingsPart, config.SpoilerMode, language, MentionPolicy.None, results.AttributionKey, o.CardStandingsRows),
                        now - TimeSpan.FromSeconds(1), dryRun), ct);
                    suppressed++;
                    continue;
                }

                if (eligible || (exists && inCorrectionWindow))
                {
                    var pings = Pings(config, guild, start: false);
                    var message = renderer.Result(view, cached, standingsPart, config.SpoilerMode, language, pings, results.AttributionKey, o.CardStandingsRows);
                    var outcome = await outbox.StageAsync(new NotificationRequest(guild, Formula1Module.ModuleIdTyped, s.SessionKey, channel, KindResult(type),
                        message, finalisedAt + TimeSpan.FromHours(Math.Max(1, o.ResultCatchUpHours)), dryRun), ct);
                    Count(outcome, ref created, ref updated);
                    if (eligible)
                        newResults++;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        if (created + updated + suppressed > 0)
            logger.LogInformation("F1 planner: guilds={Guilds} planned={Created} updated={Updated} suppressed={Suppressed}", guilds, created, updated, suppressed);
        return new F1PlanReport(guilds, created, updated, suppressed, SkippedStale: false);
    }

    private sealed record PlanV2(GuildId Guild, ChannelId Channel, string Language, bool DryRun, DateTimeOffset Now);

    /// <summary>
    /// Thursday weekend schedule (once per weekend) and the race reminder (once per race, start - 15 min). Both are
    /// schedule-based and strictly time-boxed: outside their window nothing is staged, and the outbox expiry is the end of
    /// the window, so a restart or downtime can never produce a late card. Windows opening before the guild watermark are
    /// never announced.
    /// </summary>
    private async Task<(int Created, int Updated)> PlanScheduledAsync(Formula1GuildConfigEntity config, PlanV2 p, List<F1SessionSnapshotEntity> upcoming,
        Formula1Options o, CancellationToken ct)
    {
        int created = 0, updated = 0;
        if (config.NotifyWeekendSchedule)
        {
            foreach (var meeting in upcoming.GroupBy(s => s.MeetingKey))
            {
                var rows = meeting.Where(s => (F1SessionState)s.State != F1SessionState.Cancelled).ToList();
                var race = rows.FirstOrDefault(s => (F1SessionType)s.SessionType == F1SessionType.Race);
                if (race is null || rows.Any(s => s.IsBaseline))
                    continue;
                var (from, until) = WeekendScheduleWindow(race.ScheduledStartUtc, o);
                var firstStart = rows.Min(s => s.ScheduledStartUtc);
                var expires = until < firstStart ? until : firstStart;
                if (p.Now < from || p.Now >= expires || from < config.WatermarkUtc)
                    continue;
                var message = renderer.WeekendSchedule(rows.Select(Formula1Workflow.ToView).ToList(), p.Language, schedule.AttributionKey);
                Count(await outbox.StageAsync(new NotificationRequest(p.Guild, Formula1Module.ModuleIdTyped, meeting.Key, p.Channel, WeekendScheduleKind, message,
                    expires, p.DryRun), ct), ref created, ref updated);
            }
        }

        if (config.NotifyRaceReminder)
        {
            foreach (var race in upcoming.Where(s => (F1SessionType)s.SessionType == F1SessionType.Race && !s.IsBaseline &&
                                                     (F1SessionState)s.State is F1SessionState.Scheduled or F1SessionState.Unknown))
            {
                var remindAt = race.ScheduledStartUtc - RaceReminderLead;
                var expires = remindAt + TimeSpan.FromMinutes(o.RaceReminderGraceMinutes);
                if (p.Now < remindAt || p.Now >= expires || remindAt < config.WatermarkUtc)
                    continue;
                var message = renderer.RaceReminder(Formula1Workflow.ToView(race), p.Language, schedule.AttributionKey);
                Count(await outbox.StageAsync(new NotificationRequest(p.Guild, Formula1Module.ModuleIdTyped, race.SessionKey, p.Channel, RaceReminderKind, message,
                    expires, p.DryRun), ct), ref created, ref updated);
            }
        }

        return (created, updated);
    }

    /// <summary>
    /// Safety Car, red flag and disqualification cards from the persisted race-control history: one card per real phase
    /// start (per disqualified car), only while fresh relative to the provider message time and after the watermark.
    /// </summary>
    private async Task<(int Created, int Updated)> PlanIncidentsAsync(Formula1GuildConfigEntity config, PlanV2 p, List<F1SessionSnapshotEntity> sessions,
        List<F1RaceControlEventEntity> rows, Dictionary<string, F1ResultSnapshotEntity> resultRows, Formula1Options o, CancellationToken ct)
    {
        int created = 0, updated = 0;
        if (!config.NotifySafetyCar && !config.NotifyRedFlag && !config.NotifyDisqualification)
            return (created, updated);
        var fresh = TimeSpan.FromMinutes(o.IncidentFreshMinutes);
        var dsqFresh = TimeSpan.FromHours(o.DisqualificationFreshHours);
        foreach (var session in sessions)
        {
            var view = Formula1Workflow.ToView(session);
            var history = rows.Where(r => r.SessionKey == session.SessionKey).Select(ToIncident).ToList();
            var staged = new List<(string Kind, F1RaceControlIncident Incident, TimeSpan Window, OutgoingMessage Message)>();

            if (config.NotifySafetyCar)
            {
                staged.AddRange(F1IncidentPhases.SafetyCarStarts(history).Select(start => (SafetyCarPrefix + Ticks(start), start, fresh,
                    renderer.SafetyCar(view, start, p.Language, lifecycle.AttributionKey))));
            }

            if (config.NotifyRedFlag)
            {
                staged.AddRange(F1IncidentPhases.RedFlagStarts(history).Select(start => (RedFlagPrefix + Ticks(start), start, fresh,
                    renderer.RedFlag(view, start, p.Language, lifecycle.AttributionKey))));
            }

            if (config.NotifyDisqualification)
            {
                var names = resultRows.TryGetValue(session.SessionKey, out var r) ? F1Json.Deserialize<F1SessionResult>(r.PayloadJson) : null;
                staged.AddRange(F1IncidentPhases.Disqualifications(history).Select(dsq => (
                    DisqualificationPrefix + dsq.DriverNumber!.Value.ToString(CultureInfo.InvariantCulture), dsq, dsqFresh,
                    renderer.Disqualification(view, dsq, names?.Entries.FirstOrDefault(e => e.DriverNumber == dsq.DriverNumber)?.DriverName, p.Language,
                        lifecycle.AttributionKey))));
            }

            foreach (var (kind, incident, window, message) in staged)
            {
                if (incident.OccurredAt < config.WatermarkUtc || p.Now - incident.OccurredAt > window)
                    continue; // before the watermark or no longer fresh: never announced late
                Count(await outbox.StageAsync(new NotificationRequest(p.Guild, Formula1Module.ModuleIdTyped, session.SessionKey, p.Channel, kind, message,
                    incident.OccurredAt + window, p.DryRun), ct), ref created, ref updated);
            }
        }

        return (created, updated);
    }

    private static string Ticks(F1RaceControlIncident i) => i.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture);

    private static F1RaceControlIncident ToIncident(F1RaceControlEventEntity e) =>
        new(e.Provider, e.SessionKey, (F1IncidentKind)e.Kind, e.OccurredAt, e.Lap, e.DriverNumber, e.DriverCode, e.Reason);

    /// <summary>
    /// The Thursday of the race week (the Monday-Sunday week, UTC, that contains the race day) between the configured hours.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset Until) WeekendScheduleWindow(DateTimeOffset raceStartUtc, Formula1Options o)
    {
        var raceDay = raceStartUtc.UtcDateTime.Date;
        var daysSinceMonday = ((int)raceDay.DayOfWeek + 6) % 7;
        var thursday = new DateTimeOffset(raceDay.AddDays(3 - daysSinceMonday), TimeSpan.Zero);
        return (thursday.AddHours(o.WeekendScheduleFromHourUtc), thursday.AddHours(o.WeekendScheduleUntilHourUtc));
    }

    private F1StandingsAttachment Standings(F1SessionSnapshotEntity s, Formula1GuildConfigEntity config, IReadOnlyDictionary<long, F1StandingsSnapshotEntity> rows, DateTimeOffset now)
    {
        if (!config.NotifyStandings || !F1SessionTypes.AwardsChampionshipPoints((F1SessionType)s.SessionType) || s.StandingsWatchUntil is null)
            return new(F1StandingsSection.None, null, null, null);
        F1StandingsSnapshot? Load(long? id) =>
            id is { } i && rows.TryGetValue(i, out var r) ? F1Json.Deserialize<F1StandingsSnapshot>(r.PayloadJson) : null;
        var drivers = Load(s.StandingsDriversSnapshotId);
        var constructors = Load(s.StandingsConstructorsSnapshotId);
        if (drivers is not null || constructors is not null)
            return new(F1StandingsSection.Attached, drivers, constructors, standings.AttributionKey);
        var open = !s.StandingsWindowClosed && s.StandingsWatchUntil >= now;
        return new(open ? F1StandingsSection.Pending : F1StandingsSection.NotUpdated, null, null, null);
    }

    public static bool Enabled(Formula1GuildConfigEntity c, F1SessionType type, bool start) => F1SessionTypes.Category(type) switch
    {
        F1SessionCategory.Practice => start ? c.NotifyPracticeStart : c.NotifyPracticeResults,
        F1SessionCategory.Sprint => start ? c.NotifySprintStart : c.NotifySprintResults,
        F1SessionCategory.Race => start ? c.NotifyRaceStart : c.NotifyRaceResults,
        F1SessionCategory.Qualifying => start ? c.NotifyQualifyingStart : c.NotifyQualifyingResults,
        F1SessionCategory.SprintQualifying => start ? c.NotifySprintQualifyingStart : c.NotifySprintQualifyingResults,
        _ => false,
    };

    /// <summary>Only the explicitly configured role, never @everyone (role id == guild id). Edits never ping (outbox).</summary>
    public static MentionPolicy Pings(Formula1GuildConfigEntity c, GuildId guild, bool start) =>
        c.PingRoleId is { } role && role != guild.Value && (start ? c.PingOnStarts : c.PingOnResults)
            ? new MentionPolicy([new RoleId(role)])
            : MentionPolicy.None;

    private static void Count(StageOutcome outcome, ref int created, ref int updated)
    {
        if (outcome == StageOutcome.Created) created++;
        else if (outcome is StageOutcome.UpdatedPending or StageOutcome.EditScheduled) updated++;
    }
}
