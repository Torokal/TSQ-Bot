using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Modules.Esports.Application;

public sealed record PlanReport(int Matches, int GuildsConsidered, int Created, int Updated, int FilteredOut, int BlockedMissingData, bool SkippedStale, int SuppressedCatchUp = 0);

public static class MatchJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(EsportsMatch match) => JsonSerializer.Serialize(match, Options);

    public static EsportsMatch? Deserialize(string json) => JsonSerializer.Deserialize<EsportsMatch>(json, Options);

    public static string SerializeList<T>(IReadOnlyList<T> items) => JsonSerializer.Serialize(items, Options);

    public static List<T>? DeserializeList<T>(string json) => JsonSerializer.Deserialize<List<T>>(json, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}

/// <summary>
/// Turns one fresh provider snapshot into outbox rows for every eligible guild, in ONE database transaction:
/// shared match snapshots are updated and per-guild notifications staged together. Policies (docs/NOTIFICATIONS.md):
/// <list type="bullet">
/// <item>Stale data never creates notifications.</item>
/// <item>First-ever run: already-finished matches become a silent baseline (no backlog flood).</item>
/// <item>Per guild, nothing that became due before the guild's watermark (enable/resume time) is sent.</item>
/// <item>After a polling gap, result catch-up is limited in age and count.</item>
/// <item>Reminders are "planned start" reminders — never claims of a live match.</item>
/// <item>Lifecycle cards (started, postponed, rescheduled, cancelled) are sent once per guild+match+kind, only for
/// transitions observed between two known provider states after the guild's watermark, and only while fresh.</item>
/// <item>One message per guild+match+channel+kind; later changes edit that message without pinging.</item>
/// </list>
/// </summary>
public sealed class NotificationPlanner(
    ToroDbContext db,
    INotificationOutbox outbox,
    IModuleGate gate,
    IGuildSettingsStore guildSettings,
    NotificationRenderer renderer,
    EsportsCache cache,
    EsportsDataMode mode,
    DeploymentPolicy deployment,
    IOptions<EsportsOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<NotificationPlanner> logger)
{
    public const string KindReminder = "reminder";
    public const string KindResult = "result";
    public const string KindStarted = "started";
    public const string KindPostponed = "postponed";
    public const string KindCancelled = "cancelled";
    public const string KindRescheduledPrefix = "rescheduled-";

    /// <summary>One rescheduled card per distinct new start time (a later second reschedule is a new message).</summary>
    public static string KindRescheduled(DateTimeOffset newStartUtc) =>
        KindRescheduledPrefix + newStartUtc.UtcDateTime.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<PlanReport> PlanAsync(IReadOnlyList<EsportsMatch> matches, DateTimeOffset fetchedAt, bool afterGap, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PlanOnceAsync(matches, fetchedAt, afterGap, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // The dispatcher updated an outbox row we were staging into. Planning is deterministic from DB state:
                // drop our tracked changes and recompute, so one conflict never loses the whole poll for all guilds.
                logger.LogInformation("Planner conflict with dispatcher; recomputing (attempt {Attempt})", attempt + 1);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<PlanReport> PlanOnceAsync(IReadOnlyList<EsportsMatch> matches, DateTimeOffset fetchedAt, bool afterGap, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        if (now - fetchedAt > TimeSpan.FromMinutes(o.StaleAfterMinutes))
        {
            logger.LogWarning("Planner skipped: data fetched at {FetchedAt} is stale", fetchedAt);
            return new PlanReport(matches.Count, 0, 0, 0, 0, 0, SkippedStale: true);
        }

        var snapshots = await UpsertSnapshotsAsync(matches, now, cancellationToken);
        await UpsertKnownTeamsAsync(matches, now, cancellationToken);

        var dryRun = delivery.Value.Mode != DeliveryMode.Send;
        var sourceKeys = matches.Select(m => m.Key.ToString()).ToList();
        var existingKeys = (await db.Outbox.AsNoTracking()
                .Where(x => x.ModuleId == EsportsModule.ModuleIdValue && sourceKeys.Contains(x.SourceKey))
                .Select(x => x.LogicalKey)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var configs = await db.Set<EsportsGuildConfigEntity>()
            .Where(c => c.ChannelId != null && !c.Paused && c.ChannelProblem == null)
            .ToListAsync(cancellationToken);

        int guilds = 0, created = 0, updated = 0, filtered = 0, blocked = 0, suppressed = 0;
        foreach (var config in configs)
        {
            var guild = new GuildId(config.GuildId);
            if (!deployment.IsGuildAllowed(guild) || !await gate.IsEnabledAsync(guild, EsportsModule.ModuleIdTyped, cancellationToken))
                continue;
            if (mode.IsDemo && !deployment.MayShowDemoData(guild))
                continue; // fixture data only ever reaches explicitly authorized test guilds
            guilds++;

            var channel = new ChannelId(config.ChannelId!.Value);
            var filters = await LoadFiltersAsync(config, cancellationToken);
            var mappings = await db.Set<RoleMappingEntity>().AsNoTracking().Where(m => m.GuildId == config.GuildId).ToListAsync(cancellationToken);
            var settings = await guildSettings.GetAsync(guild, cancellationToken);
            var language = settings.Language;
            if (!GuildTime.TryResolve(settings.TimeZoneId, out var zone))
                GuildTime.TryResolve(GuildSettings.DefaultTimeZoneId, out zone);
            var catchUp = 0;

            foreach (var match in matches.OrderBy(m => m.ScheduledStartUtc))
            {
                var decision = MatchFilter.Evaluate(match, filters, cache.Resolver);
                if (!decision.Passes)
                {
                    if (decision.Verdict == FilterVerdict.BlockedMissingData) blocked++;
                    else filtered++;
                    continue;
                }

                var snapshot = snapshots[match.Key.ToString()];

                if (config.NotifyReminders && match.Status == MatchStatus.Scheduled && match.StartTimeExact && match.ScheduledStartUtc is { } start)
                {
                    var key = NotificationRequest.BuildLogicalKey(guild, EsportsModule.ModuleIdTyped, match.Key.ToString(), channel, KindReminder, dryRun);
                    var exists = existingKeys.Contains(key);
                    var trigger = start - TimeSpan.FromMinutes(config.ReminderLeadMinutes);
                    var withinWindow = now <= start + TimeSpan.FromMinutes(o.ReminderGraceMinutes);
                    // Matches starting before the guild enabled/resumed are never announced (no backlog).
                    var due = now >= trigger && withinWindow && start >= config.WatermarkUtc;
                    if ((exists && withinWindow) || (!exists && due))
                    {
                        var pings = Pings(mappings, match, reminder: true, guild);
                        var message = renderer.Reminder(match, language, pings, snapshot.LastChangedAt,
                            snapshot.StartChangedAt is not null ? snapshot.PreviousStartUtc : null, filters.TeamKeys);
                        var outcome = await outbox.StageAsync(new NotificationRequest(guild, EsportsModule.ModuleIdTyped, match.Key.ToString(), channel,
                            KindReminder, message, start + TimeSpan.FromMinutes(o.ReminderGraceMinutes), dryRun), cancellationToken);
                        Count(outcome, ref created, ref updated);
                    }
                }

                if (config.NotifyResults && match.Status == MatchStatus.Finished && snapshot.FinishedObservedAt is { } observed && !snapshot.IsBaseline)
                {
                    var key = NotificationRequest.BuildLogicalKey(guild, EsportsModule.ModuleIdTyped, match.Key.ToString(), channel, KindResult, dryRun);
                    var exists = existingKeys.Contains(key);
                    var inCorrectionWindow = now - observed <= TimeSpan.FromHours(o.ResultCorrectionHours);
                    var reference = match.ScheduledStartUtc ?? observed;
                    var eligible = !exists &&
                                   observed >= config.WatermarkUtc &&
                                   reference >= now - TimeSpan.FromHours(o.ResultCatchUpHours);
                    var due = eligible && (!afterGap || catchUp < o.MaxCatchUpPerGuildPerPoll);
                    if (eligible && !due)
                    {
                        // Over the catch-up limit after downtime: record it as already expired so it is never sent
                        // later either (otherwise the next normal poll would deliver the whole backlog).
                        var skipped = renderer.Result(match, language, config.SpoilerMode, MentionPolicy.None, snapshot.LastChangedAt, filters.TeamKeys);
                        await outbox.StageAsync(new NotificationRequest(guild, EsportsModule.ModuleIdTyped, match.Key.ToString(), channel,
                            KindResult, skipped, now - TimeSpan.FromSeconds(1), dryRun), cancellationToken);
                        suppressed++;
                    }
                    else if ((exists && inCorrectionWindow) || due)
                    {
                        var pings = Pings(mappings, match, reminder: false, guild);
                        var message = renderer.Result(match, language, config.SpoilerMode, pings, snapshot.LastChangedAt, filters.TeamKeys);
                        var outcome = await outbox.StageAsync(new NotificationRequest(guild, EsportsModule.ModuleIdTyped, match.Key.ToString(), channel,
                            KindResult, message, observed + TimeSpan.FromHours(6), dryRun), cancellationToken);
                        Count(outcome, ref created, ref updated);
                        if (!exists && afterGap)
                            catchUp++;
                    }
                }

                if (config.NotifyReminders)
                {
                    var lifecycle = await StageLifecycleAsync(match, snapshot, config, guild, channel, language, zone, mappings, filters.TeamKeys, existingKeys, dryRun, now, cancellationToken);
                    created += lifecycle.Created;
                    updated += lifecycle.Updated;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new PlanReport(matches.Count, guilds, created, updated, filtered, blocked, SkippedStale: false, suppressed);
    }

    private readonly record struct Staged(int Created, int Updated);

    /// <summary>
    /// Started / postponed / rescheduled / cancelled cards. Each needs (a) a transition recorded on the shared snapshot,
    /// (b) the match still being in that state, (c) the transition observed after the guild's watermark and (d) within
    /// the freshness window. Only "started" may ping (reminder role mappings); schedule changes never ping.
    /// </summary>
    private async Task<Staged> StageLifecycleAsync(EsportsMatch match, MatchSnapshotEntity snapshot, EsportsGuildConfigEntity config, GuildId guild,
        ChannelId channel, string language, TimeZoneInfo zone, IReadOnlyList<RoleMappingEntity> mappings, IReadOnlySet<string> followedTeams, HashSet<string> existingKeys, bool dryRun,
        DateTimeOffset now, CancellationToken ct)
    {
        int created = 0, updated = 0;
        var fresh = TimeSpan.FromMinutes(options.Value.LifecycleFreshMinutes);

        async Task StageAsync(string kind, DateTimeOffset? observed, bool stillTrue, Func<OutgoingMessage> render)
        {
            if (observed is not { } at || !stillTrue || at < config.WatermarkUtc)
                return;
            var key = NotificationRequest.BuildLogicalKey(guild, EsportsModule.ModuleIdTyped, match.Key.ToString(), channel, kind, dryRun);
            var inWindow = now - at <= fresh;
            if (!inWindow)
                return; // too old to announce, and an existing message is no longer corrected either
            var outcome = await outbox.StageAsync(new NotificationRequest(guild, EsportsModule.ModuleIdTyped, match.Key.ToString(), channel,
                kind, render(), at + fresh, dryRun), ct);
            if (!existingKeys.Contains(key) && outcome == StageOutcome.Created)
                created++;
            else if (outcome is StageOutcome.UpdatedPending or StageOutcome.EditScheduled)
                updated++;
        }

        await StageAsync(KindStarted, snapshot.StartedObservedAt, match.Status == MatchStatus.Live,
            () => renderer.Started(match, language, Pings(mappings, match, reminder: true, guild), snapshot.StartedObservedAt!.Value, followedTeams));
        await StageAsync(KindPostponed, snapshot.PostponedObservedAt, match.Status == MatchStatus.Postponed,
            () => renderer.Postponed(match, language, snapshot.PostponedObservedAt!.Value, followedTeams));
        await StageAsync(KindCancelled, snapshot.CancelledObservedAt, match.Status == MatchStatus.Cancelled,
            () => renderer.Cancelled(match, language, snapshot.CancelledObservedAt!.Value, followedTeams));
        if (snapshot.RescheduledToUtc is { } to)
        {
            await StageAsync(KindRescheduled(to), snapshot.RescheduledObservedAt,
                match.Status == MatchStatus.Scheduled && match.ScheduledStartUtc == to,
                () => renderer.Rescheduled(match, language, to, zone, snapshot.RescheduledObservedAt!.Value, followedTeams));
        }

        return new Staged(created, updated);
    }

    private static void Count(StageOutcome outcome, ref int created, ref int updated)
    {
        if (outcome == StageOutcome.Created) created++;
        else if (outcome is StageOutcome.UpdatedPending or StageOutcome.EditScheduled) updated++;
    }

    /// <summary>Only explicitly mapped roles whose scope matches this match; never @everyone (role id == guild id).</summary>
    private static MentionPolicy Pings(IReadOnlyList<RoleMappingEntity> mappings, EsportsMatch match, bool reminder, GuildId guild)
    {
        var roles = mappings
            .Where(m => reminder ? m.PingOnReminder : m.PingOnResult)
            .Where(m => m.TeamKey.Length == 0 || match.InvolvesTeam(m.TeamKey))
            .Where(m => m.RoleId != guild.Value)
            .Select(m => new RoleId(m.RoleId))
            .Distinct()
            .OrderBy(r => r.Value)
            .ToList();
        return roles.Count == 0 ? MentionPolicy.None : new MentionPolicy(roles);
    }

    public async Task<GuildFilterSet> LoadFiltersAsync(EsportsGuildConfigEntity config, CancellationToken cancellationToken)
    {
        var rows = await db.Set<EsportsFilterEntity>().AsNoTracking().Where(f => f.GuildId == config.GuildId).ToListAsync(cancellationToken);
        HashSet<string> Of(FilterDimension d) => rows.Where(r => r.Dimension == (int)d).Select(r => r.Value).ToHashSet(StringComparer.Ordinal);
        return new GuildFilterSet(Of(FilterDimension.Team), Of(FilterDimension.Tournament), Of(FilterDimension.Tier), config.VrsTopN);
    }

    private async Task<Dictionary<string, MatchSnapshotEntity>> UpsertSnapshotsAsync(IReadOnlyList<EsportsMatch> matches, DateTimeOffset now, CancellationToken ct)
    {
        var keys = matches.Select(m => m.Key.ToString()).ToList();
        var set = db.Set<MatchSnapshotEntity>();
        var existing = await set.Where(s => keys.Contains(s.MatchKey)).ToDictionaryAsync(s => s.MatchKey, StringComparer.Ordinal, ct);
        // Bootstrap is per provider: switching providers (e.g. Liquipedia → PandaScore) must not announce the new
        // provider's already-finished matches just because the database is not empty.
        var sourcePrefix = matches.Count > 0 ? matches[0].Key.Source + ":" : "";
        var bootstrap = existing.Count == 0 && !await set.AnyAsync(s => s.MatchKey.StartsWith(sourcePrefix), ct);
        if (bootstrap)
            logger.LogInformation("Esports planner bootstrap: {Count} matches recorded as baseline (no backlog notifications)", matches.Count);

        var threshold = TimeSpan.FromMinutes(options.Value.RescheduleThresholdMinutes);
        foreach (var match in matches)
        {
            var key = match.Key.ToString();
            var json = MatchJson.Serialize(match);
            var hash = MatchJson.Hash(json);
            MatchStatus? previous = null;
            if (existing.TryGetValue(key, out var known))
                previous = (MatchStatus)known.Status;
            if (!existing.TryGetValue(key, out var snapshot))
            {
                snapshot = new MatchSnapshotEntity
                {
                    MatchKey = key,
                    FirstSeenAt = now,
                    LastChangedAt = now,
                    IsBaseline = bootstrap && match.Status is MatchStatus.Finished or MatchStatus.Cancelled,
                    ScheduledStartUtc = match.ScheduledStartUtc,
                };
                set.Add(snapshot);
                existing[key] = snapshot;
            }
            else if (snapshot.ContentHash != hash)
            {
                snapshot.LastChangedAt = now;
            }

            var oldStartUtc = snapshot.ScheduledStartUtc;
            if (snapshot.ScheduledStartUtc is { } oldStart && match.ScheduledStartUtc is { } newStart && oldStart != newStart)
            {
                // Only a time change was observed — reported as such, never as "postponed"/"cancelled".
                snapshot.PreviousStartUtc = oldStart;
                snapshot.StartChangedAt = now;
            }

            RecordTransitions(snapshot, previous, match, oldStartUtc, threshold, now);

            if (match.Status == MatchStatus.Finished && snapshot.FinishedObservedAt is null)
                snapshot.FinishedObservedAt = now;

            snapshot.ScheduledStartUtc = match.ScheduledStartUtc;
            // Unknown never overwrites the last known state, so Unknown → Running later is still a real "started".
            if (match.Status != MatchStatus.Unknown)
                snapshot.Status = (int)match.Status;
            snapshot.PayloadJson = json;
            snapshot.ContentHash = hash;
            snapshot.LastSeenAt = now;
        }

        return existing;
    }

    /// <summary>
    /// Records lifecycle transitions between two KNOWN provider states. Nothing is recorded on first sight (no previous
    /// state), from/to Unknown, or from the clock. Reschedules need the provider's own "rescheduled" flag plus a move of
    /// at least the threshold (demo re-anchoring or small corrections are not announcements).
    /// </summary>
    public static void RecordTransitions(MatchSnapshotEntity snapshot, MatchStatus? previous, EsportsMatch match, DateTimeOffset? oldStartUtc, TimeSpan threshold, DateTimeOffset now)
    {
        if (previous is not { } prev || prev == MatchStatus.Unknown || match.Status == MatchStatus.Unknown)
            return;

        if (match.Status == MatchStatus.Live && prev is MatchStatus.Scheduled or MatchStatus.Postponed)
            snapshot.StartedObservedAt ??= now;
        if (match.Status == MatchStatus.Postponed && prev == MatchStatus.Scheduled)
            snapshot.PostponedObservedAt ??= now;
        if (match.Status == MatchStatus.Cancelled && prev is MatchStatus.Scheduled or MatchStatus.Postponed or MatchStatus.Live)
            snapshot.CancelledObservedAt ??= now;

        if (match.Status == MatchStatus.Scheduled && match.Rescheduled && match.ScheduledStartUtc is { } to && snapshot.RescheduledToUtc != to)
        {
            var fromPostponed = prev == MatchStatus.Postponed;
            var moved = prev == MatchStatus.Scheduled && oldStartUtc is { } from && (to - from).Duration() >= threshold;
            if (fromPostponed || moved)
            {
                snapshot.RescheduledObservedAt = now;
                snapshot.RescheduledToUtc = to;
            }
        }
    }

    private async Task UpsertKnownTeamsAsync(IReadOnlyList<EsportsMatch> matches, DateTimeOffset now, CancellationToken ct)
    {
        var teams = matches.SelectMany(m => m.Opponents).Where(o => o.Team is not null).Select(o => o.Team!)
            .GroupBy(t => t.Key).Select(g => g.First()).ToList();
        var keys = teams.Select(t => t.Key).ToList();
        var set = db.Set<KnownTeamEntity>();
        var existing = await set.Where(t => keys.Contains(t.TeamKey)).ToDictionaryAsync(t => t.TeamKey, StringComparer.Ordinal, ct);
        foreach (var team in teams)
        {
            if (!existing.TryGetValue(team.Key, out var row))
            {
                row = new KnownTeamEntity { TeamKey = team.Key };
                set.Add(row);
            }

            row.Name = team.Name;
            row.ShortName = team.ShortName;
            row.LastSeenAt = now;
        }
    }
}
