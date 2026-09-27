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
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Persistence;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

public sealed record VbPlanReport(int GuildsConsidered, int Created, int Updated, int Suppressed);

/// <summary>
/// Turns persisted match state into outbox rows for every eligible guild, in ONE database transaction. It never sends
/// anything itself: the durable outbox provides the unique logical key (guild + module + match + channel + kind, restart
/// safe), retries, the module gate, pause/channel checks, locked allowed_mentions and ping-free edits. Policies
/// (docs/volleyball/VOLLEYBALL.md):
/// <list type="bullet">
/// <item>Reminder: once, <see cref="VolleyballOptions.ReminderLeadMinutes"/> before the scheduled start, only while the match
/// has not started, only from fresh fixture data, never for a match that started before the guild's watermark.</item>
/// <item>Started / set: only transitions the workflow marked announceable (continuous observation), observed after the
/// guild's watermark and at most <see cref="VolleyballOptions.LiveFreshMinutes"/> ago. Sets never ping.</item>
/// <item>Final: once; after downtime only for matches that started within <see cref="VolleyballOptions.ResultCatchUpHours"/>
/// (and at most <see cref="VolleyballOptions.MaxCatchUpPerGuildPerRun"/> per run); corrections edit the same message.</item>
/// <item>Postponed / cancelled: once each, only for recent/upcoming matches. Never ping.</item>
/// </list>
/// </summary>
public sealed class VolleyballNotificationPlanner(
    ToroDbContext db,
    INotificationOutbox outbox,
    IModuleGate gate,
    IGuildSettingsStore guildSettings,
    VolleyballNotificationRenderer renderer,
    VolleyballCache cache,
    VbDataMode mode,
    DeploymentPolicy deployment,
    IVolleyballDataProvider provider,
    IOptions<VolleyballOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<VolleyballNotificationPlanner> logger)
{
    public const string ReminderPrefix = "reminder:";
    public const string StartedKind = "started";
    public const string SetPrefix = "set:";
    public const string FinalKind = "final";
    public const string PostponedKind = "postponed";
    public const string CancelledKind = "cancelled";

    /// <summary>One reminder per match and UTC start day: a same-day time change edits it, a postponement to another day gets a new one.</summary>
    public static string ReminderKind(DateTimeOffset start) => ReminderPrefix + start.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string SetKind(int set) => SetPrefix + set.ToString(CultureInfo.InvariantCulture);

    public async Task<VbPlanReport> PlanAsync(CancellationToken cancellationToken)
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
                logger.LogInformation("volleyball planner conflict with dispatcher; recomputing (attempt {Attempt})", attempt + 1);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<VbPlanReport> PlanOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        var horizon = now - TimeSpan.FromHours(Math.Max(Math.Max(o.FinalCorrectionHours, o.ResultCatchUpHours), o.StatusChangeRecentHours) + o.LiveTrailingHours);
        // Only the configured provider's rows: demo (fixture) rows never reach a guild after switching to live data.
        var rows = await db.Set<VbMatchSnapshotEntity>().AsNoTracking()
            .Where(m => m.Provider == provider.Id)
            .Where(m => m.UpdatedAt >= horizon || (m.StartTimeUtc != null && m.StartTimeUtc >= now - TimeSpan.FromHours(1) && m.StartTimeUtc <= now + TimeSpan.FromMinutes(o.ReminderLeadMinutes)))
            .ToListAsync(ct);
        if (rows.Count == 0)
            return new(0, 0, 0, 0);

        var fixturesFresh = !cache.Fixtures.IsStale(now, o.FixtureStaleAfter);
        var keys = rows.Select(r => r.MatchKey).ToList();
        var dryRun = delivery.Value.Mode != DeliveryMode.Send;
        var existing = (await db.Outbox.AsNoTracking()
                .Where(x => x.ModuleId == VolleyballModule.ModuleIdValue && keys.Contains(x.SourceKey))
                .Select(x => new { x.LogicalKey, x.Status, x.ExpiresAt })
                .ToListAsync(ct))
            .ToDictionary(x => x.LogicalKey, x => (x.Status, x.ExpiresAt), StringComparer.Ordinal);
        var configs = await db.Set<VolleyballGuildConfigEntity>().AsNoTracking()
            .Where(c => c.ChannelId != null && !c.Paused && c.ChannelProblem == null)
            .ToListAsync(ct);

        int guilds = 0, created = 0, updated = 0, suppressed = 0;
        foreach (var config in configs)
        {
            var guild = new GuildId(config.GuildId);
            if (!deployment.IsGuildAllowed(guild) || !await gate.IsEnabledAsync(guild, VolleyballModule.ModuleIdTyped, ct))
                continue;
            if (mode.IsDemo && !deployment.MayShowDemoData(guild))
                continue; // fixture data only ever reaches explicitly authorized test guilds
            guilds++;
            var channel = new ChannelId(config.ChannelId!.Value);
            var language = (await guildSettings.GetAsync(guild, ct)).Language;
            var catchUps = 0;

            async Task Stage(VbMatchSnapshotEntity row, string kind, OutgoingMessage message, DateTimeOffset expiresAt)
            {
                var outcome = await outbox.StageAsync(new NotificationRequest(guild, VolleyballModule.ModuleIdTyped, row.MatchKey, channel, kind, message, expiresAt, dryRun), ct);
                if (outcome == StageOutcome.Created)
                {
                    created++;
                    logger.LogInformation("volleyball notification_enqueued guild={Guild} match={Match} kind={Kind}", guild.Value, row.MatchKey, kind);
                }
                else if (outcome is StageOutcome.UpdatedPending or StageOutcome.EditScheduled)
                {
                    updated++;
                }
                else
                {
                    logger.LogDebug("volleyball notification_deduplicated guild={Guild} match={Match} kind={Kind} outcome={Outcome}", guild.Value, row.MatchKey, kind, outcome);
                }
            }

            (OutboxStatus Status, DateTimeOffset ExpiresAt)? Existing(VbMatchSnapshotEntity row, string kind) =>
                existing.TryGetValue(NotificationRequest.BuildLogicalKey(guild, VolleyballModule.ModuleIdTyped, row.MatchKey, channel, kind, dryRun), out var e) ? e : null;

            foreach (var row in rows.OrderBy(r => r.StartTimeUtc ?? DateTimeOffset.MaxValue))
            {
                var view = VolleyballWorkflow.ToView(row);
                var events = VolleyballWorkflow.Events(row);

                // ---- reminder (planned start, never a claim that the match started)
                // A postponed match that got a new date is Scheduled again (the sticky Postponed flag only records that it happened).
                if (config.NotifyReminder && fixturesFresh && row.StartTimeUtc is { } start && !row.Started && !row.Cancelled &&
                    (VolleyballMatchStatus)row.Status is VolleyballMatchStatus.Scheduled or VolleyballMatchStatus.Unknown &&
                    now >= start - TimeSpan.FromMinutes(o.ReminderLeadMinutes) && now < start && start >= config.WatermarkUtc &&
                    now - row.LastObservedAt <= o.FixtureStaleAfter &&
                    row.LastListedAt is { } listed && cache.Fixtures.FetchedAt is { } fetched && listed >= fetched - TimeSpan.FromSeconds(1))
                {
                    await Stage(row, ReminderKind(start), renderer.Reminder(view, language, Pings(config, guild, reminder: true), provider.AttributionKey),
                        start + TimeSpan.FromMinutes(10));
                }

                foreach (var e in events.Where(e => e.At >= config.WatermarkUtc))
                {
                    switch (e.Kind)
                    {
                        case VbEvent.Started when config.NotifyStarted && e.Announce && now - e.At <= o.LiveFresh && !row.Finished:
                            await Stage(row, StartedKind, renderer.Started(view, e.At, language, MentionPolicy.None, provider.AttributionKey), e.At + o.LiveFresh);
                            break;

                        case VbEvent.SetCompleted when config.NotifySets && e.Announce && e.Set is { } set && now - e.At <= o.LiveFresh &&
                                                       view.Sets.Any(s => s.Number == set):
                            await Stage(row, SetKind(set), renderer.SetFinished(view, set, e.At, language, MentionPolicy.None, provider.AttributionKey), e.At + o.LiveFresh);
                            break;

                        case VbEvent.Final when config.NotifyFinal && row.Finished:
                            {
                                // Non-continuous = the bot (or every guild) was not watching when the match ended. Such a final is
                                // catch-up: never for a guild that enabled/resumed after the last observation before the end
                                // (that would be history), and bounded per run.
                                var continuous = e.Since is { } since && e.At - since <= o.Continuity;
                                if (!continuous && (e.Since is null || e.Since < config.WatermarkUtc))
                                    break;
                                var row0 = Existing(row, FinalKind);
                                // A suppressed catch-up row (pending with an expiry in the past) is never revived.
                                var correctable = row0 is { } x && now - e.At <= TimeSpan.FromHours(o.FinalCorrectionHours) &&
                                                  (x.Status is OutboxStatus.Sent or OutboxStatus.InFlight or OutboxStatus.DeliveryUnknown ||
                                                   (x.Status == OutboxStatus.Pending && x.ExpiresAt > now));
                                var eligible = row0 is null && e.Announce &&
                                               (row.StartTimeUtc is not { } s || s >= now - TimeSpan.FromHours(o.ResultCatchUpHours + o.LiveTrailingHours)) &&
                                               now - e.At <= TimeSpan.FromHours(Math.Max(1, o.ResultCatchUpHours));
                                var pings = Pings(config, guild, reminder: false);
                                if (eligible && !continuous)
                                {
                                    if (catchUps >= o.MaxCatchUpPerGuildPerRun)
                                    {
                                        // Over the limit: recorded as already expired (same payload) so it is never delivered later.
                                        await Stage(row, FinalKind, renderer.Final(view, e.At, language, pings, provider.AttributionKey), now - TimeSpan.FromSeconds(1));
                                        suppressed++;
                                        break;
                                    }

                                    catchUps++;
                                }

                                if (eligible || correctable)
                                    await Stage(row, FinalKind, renderer.Final(view, e.At, language, pings, provider.AttributionKey),
                                        e.At + TimeSpan.FromHours(Math.Max(1, o.ResultCatchUpHours)));
                                break;
                            }
                        case VbEvent.Postponed when config.NotifyPostponedCancelled && e.Announce && Recent(row, now, o) && now - e.At <= TimeSpan.FromHours(o.StatusChangeRecentHours):
                            await Stage(row, PostponedKind, renderer.Postponed(view, e.At, language, provider.AttributionKey), e.At + TimeSpan.FromHours(o.StatusChangeRecentHours));
                            break;

                        case VbEvent.Cancelled when config.NotifyPostponedCancelled && e.Announce && Recent(row, now, o) && now - e.At <= TimeSpan.FromHours(o.StatusChangeRecentHours):
                            await Stage(row, CancelledKind, renderer.Cancelled(view, e.At, language, provider.AttributionKey), e.At + TimeSpan.FromHours(o.StatusChangeRecentHours));
                            break;
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        if (created + updated + suppressed > 0)
            logger.LogInformation("volleyball planner: guilds={Guilds} planned={Created} updated={Updated} suppressed={Suppressed}", guilds, created, updated, suppressed);
        return new(guilds, created, updated, suppressed);
    }

    /// <summary>Status-change cards only for matches that are upcoming or started recently (no news about old fixtures).</summary>
    private static bool Recent(VbMatchSnapshotEntity row, DateTimeOffset now, VolleyballOptions o) =>
        row.StartTimeUtc is not { } start || start >= now - TimeSpan.FromHours(o.StatusChangeRecentHours);

    /// <summary>Only the explicitly configured role, never @everyone (role id == guild id). Edits never ping (outbox).</summary>
    public static MentionPolicy Pings(VolleyballGuildConfigEntity c, GuildId guild, bool reminder) =>
        c.PingRoleId is { } role && role != guild.Value && (reminder ? c.PingOnReminder : c.PingOnFinal)
            ? new MentionPolicy([new RoleId(role)])
            : MentionPolicy.None;

    /// <summary>Whether a staged kind is still wanted by the guild's switches (checked again right before delivery).</summary>
    public static bool Enabled(VolleyballGuildConfigEntity c, string kind) =>
        kind.StartsWith(ReminderPrefix, StringComparison.Ordinal) ? c.NotifyReminder
        : kind == StartedKind ? c.NotifyStarted
        : kind.StartsWith(SetPrefix, StringComparison.Ordinal) ? c.NotifySets
        : kind == FinalKind ? c.NotifyFinal
        : kind is PostponedKind or CancelledKind && c.NotifyPostponedCancelled;
}
