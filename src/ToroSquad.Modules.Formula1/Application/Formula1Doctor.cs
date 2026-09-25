using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

public enum F1CheckState
{
    Ok = 0,
    Warning = 1,
    Problem = 2,
    Info = 3,
}

public sealed record F1DoctorCheck(string LabelKey, F1CheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>
/// /f1-admin doctor — actionable diagnostics from cached state only (no provider calls, no secrets): channel and
/// permissions, role, each provider's freshness/outcome/backoff, live connection, delivery statistics.
/// </summary>
public sealed class Formula1Doctor(
    ToroDbContext db,
    IModuleGate gate,
    IGuildGateway guilds,
    Formula1Cache cache,
    IF1ScheduleProvider schedule,
    IF1LifecycleProvider lifecycle,
    IF1StandingsProvider standings,
    F1DataMode mode,
    DeploymentPolicy deployment,
    IOptions<Formula1Options> options,
    TimeProvider clock)
{
    public async Task<(OperationResult Auth, IReadOnlyList<F1DoctorCheck> Checks)> RunAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), []);

        var checks = new List<F1DoctorCheck>();
        var guild = actor.GuildId;
        var now = clock.GetUtcNow();
        var o = options.Value;

        var enabled = await gate.IsEnabledAsync(guild, Formula1Module.ModuleIdTyped, ct);
        checks.Add(new("f1.doctor.module", enabled ? F1CheckState.Ok : F1CheckState.Warning, enabled ? "f1.doctor.module_on" : "f1.doctor.module_off", []));

        var config = await db.Set<Formula1GuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        if (config?.ChannelId is not { } channelId)
        {
            checks.Add(new("f1.doctor.channel", F1CheckState.Problem, "f1.doctor.channel_missing", []));
        }
        else
        {
            var access = await guilds.GetBotChannelAccessAsync(guild, new ChannelId(channelId), ct);
            if (!access.Exists)
            {
                checks.Add(new("f1.doctor.channel", F1CheckState.Problem, "f1.doctor.channel_gone", [channelId]));
            }
            else
            {
                foreach (var perm in new[] { GuildPermission.ViewChannel, GuildPermission.SendMessages, GuildPermission.EmbedLinks })
                {
                    checks.Add(new("f1.doctor.perm", access.Permissions.Grants(perm) ? F1CheckState.Ok : F1CheckState.Problem,
                        access.Permissions.Grants(perm) ? "f1.doctor.perm_ok" : "f1.doctor.perm_missing", [perm.ToString()]));
                }

                checks.Add(new("f1.doctor.perm", access.Permissions.Grants(GuildPermission.ReadMessageHistory) ? F1CheckState.Ok : F1CheckState.Warning,
                    "f1.doctor.perm_history", [nameof(GuildPermission.ReadMessageHistory)]));
            }

            if (config.ChannelProblem is not null)
                checks.Add(new("f1.doctor.channel", F1CheckState.Problem, "f1.doctor.channel_problem", [config.ChannelProblem]));
        }

        if (config?.Paused == true)
            checks.Add(new("f1.doctor.paused", F1CheckState.Warning, "f1.doctor.paused_on", []));

        if (config?.PingRoleId is { } roleId)
        {
            var snapshot = await guilds.GetRoleSnapshotAsync(guild, ct);
            if (snapshot is not null)
            {
                var (valid, pingWorks) = SelfServiceRolePolicy.EvaluateMentionTarget(snapshot, new RoleId(roleId));
                checks.Add(new("f1.doctor.role", !valid ? F1CheckState.Problem : pingWorks ? F1CheckState.Ok : F1CheckState.Warning,
                    !valid ? "f1.doctor.role_missing" : pingWorks ? "f1.doctor.role_ok" : "f1.doctor.role_not_mentionable", [DiscordText.RoleMention(new RoleId(roleId))]));
            }
        }

        checks.Add(new("f1.doctor.mode", mode.IsDemo ? F1CheckState.Info : F1CheckState.Ok, mode.IsDemo ? "f1.doctor.mode_fixture" : "f1.doctor.mode_live", []));
        if (mode.IsDemo && deployment.RealDiscordConnection && !deployment.IsTestGuild(guild))
            checks.Add(new("f1.doctor.mode", F1CheckState.Warning, "f1.doctor.demo_not_test_guild", []));

        checks.Add(Feed("f1.doctor.schedule", schedule.AttributionKey, cache.Schedule, o.ScheduleStaleAfter, now));
        checks.Add(Feed("f1.doctor.standings_drivers", standings.AttributionKey, cache.DriverStandings, o.StandingsStaleAfter, now));
        checks.Add(Feed("f1.doctor.standings_constructors", standings.AttributionKey, cache.ConstructorStandings, o.StandingsStaleAfter, now));

        // Lifecycle: honest about missing live access — without it there are no start notifications at all.
        if (!lifecycle.IsConfigured)
        {
            checks.Add(new("f1.doctor.lifecycle", F1CheckState.Problem, "f1.doctor.lifecycle_not_configured", [lifecycle.AttributionKey]));
        }
        else
        {
            var live = cache.Live;
            var state = live.State switch
            {
                F1LiveState.AuthFailed => F1CheckState.Problem,
                F1LiveState.BackingOff or F1LiveState.Stopping => F1CheckState.Warning,
                _ => F1CheckState.Ok,
            };
            checks.Add(new("f1.doctor.lifecycle", state, "f1.doctor.lifecycle_state",
                [live.State.ToString(), At(live.LastConnectedAt), live.Reconnects, live.LastError ?? "-"]));
            var feed = cache.LifecycleFeed;
            if (feed.LastOutcome is { } outcome && outcome != F1ProviderOutcome.Success)
                checks.Add(new("f1.doctor.lifecycle", F1CheckState.Warning, "f1.doctor.last_call", [outcome.ToString(), feed.LastDetail ?? "-", At(feed.LastAttemptAt)]));
        }

        var resultsFeed = cache.ResultsFeed;
        checks.Add(resultsFeed.LastOutcome is null or F1ProviderOutcome.Success
            ? new("f1.doctor.results", F1CheckState.Ok, "f1.doctor.results_ok", [At(resultsFeed.FetchedAt)])
            : new("f1.doctor.results", resultsFeed.LastOutcome == F1ProviderOutcome.QuotaExceeded ? F1CheckState.Warning : F1CheckState.Problem,
                "f1.doctor.last_call", [resultsFeed.LastOutcome.ToString()!, resultsFeed.LastDetail ?? "-", At(resultsFeed.LastAttemptAt)]));

        var since = now - TimeSpan.FromDays(1);
        var outboxRows = await db.Outbox.AsNoTracking()
            .Where(x => x.GuildId == guild.Value && x.ModuleId == Formula1Module.ModuleIdValue && x.UpdatedAt >= since)
            .Select(x => x.Status)
            .ToListAsync(ct);
        var unknown = outboxRows.Count(s => s == OutboxStatus.DeliveryUnknown);
        var failed = outboxRows.Count(s => s == OutboxStatus.Failed);
        checks.Add(new("f1.doctor.delivery", unknown + failed > 0 ? F1CheckState.Warning : F1CheckState.Ok, "f1.doctor.delivery_stats",
            [outboxRows.Count(s => s == OutboxStatus.Sent), outboxRows.Count(s => s == OutboxStatus.Pending), failed, unknown]));

        return (OperationResult.Ok("f1.doctor.done"), checks);
    }

    private static F1DoctorCheck Feed<T>(string label, string sourceKey, F1Feed<T> feed, TimeSpan staleAfter, DateTimeOffset now)
        where T : class
    {
        if (feed.FetchedAt is null)
            return new(label, F1CheckState.Problem, "f1.doctor.feed_none", [sourceKey, feed.LastOutcome?.ToString() ?? "-", feed.LastDetail ?? "-"]);
        var stale = feed.IsStale(now, staleAfter);
        var failing = feed.LastOutcome is { } o && o is not (F1ProviderOutcome.Success or F1ProviderOutcome.Partial);
        return new(label, stale ? F1CheckState.Warning : failing ? F1CheckState.Warning : F1CheckState.Ok,
            stale ? "f1.doctor.feed_stale" : "f1.doctor.feed_ok",
            [sourceKey, At(feed.FetchedAt), feed.LastOutcome?.ToString() ?? "-", feed.ConsecutiveFailures]);
    }

    private static string At(DateTimeOffset? at) => at is { } a ? DiscordText.Timestamp(a, 'R') : "-";
}

/// <summary>Cheap health summary for /bot status (cached provider state only; never calls providers).</summary>
public sealed class Formula1HealthCheck(Formula1Cache cache, IF1LifecycleProvider lifecycle, F1DataMode mode, IOptions<Formula1Options> options, TimeProvider clock) : IModuleHealthCheck
{
    public ModuleId Module => Formula1Module.ModuleIdTyped;

    public Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        var entries = new List<HealthEntry>
        {
            Entry(mode.IsDemo ? "f1.health.schedule_demo" : "f1.health.schedule", cache.Schedule, o.ScheduleStaleAfter, now),
            Entry("f1.health.standings", cache.DriverStandings, o.StandingsStaleAfter, now),
        };
        entries.Add(!lifecycle.IsConfigured
            ? new HealthEntry("f1.health.lifecycle", HealthState.NotConfigured, "f1.health.lifecycle_not_configured")
            : new HealthEntry("f1.health.lifecycle", cache.Live.State switch
            {
                F1LiveState.AuthFailed => HealthState.Unavailable,
                F1LiveState.BackingOff or F1LiveState.Stopping => HealthState.Degraded,
                _ => HealthState.Healthy,
            }, "f1.health.lifecycle_state", [cache.Live.State.ToString()]));
        return Task.FromResult(new ModuleHealthReport(Module, entries));
    }

    private static HealthEntry Entry<T>(string component, F1Feed<T> feed, TimeSpan staleAfter, DateTimeOffset now)
        where T : class =>
        feed.FetchedAt is null
            ? new HealthEntry(component, HealthState.Unavailable, "f1.health.no_data_yet")
            : new HealthEntry(component, feed.IsStale(now, staleAfter) ? HealthState.Degraded : HealthState.Healthy, "f1.health.data_age",
                [DiscordText.Timestamp(feed.FetchedAt.Value, 'R')]);
}

/// <summary>Formula 1 stores no per-user data; guild configuration is purged on retention.</summary>
public sealed class Formula1UserData(ToroDbContext db) : IUserDataContributor
{
    public ModuleId Module => Formula1Module.ModuleIdTyped;

    public Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new JsonObject { ["storesPersonalData"] = false });

    public Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeletionPreviewItem>>([]);

    public Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new DeletionReport(Module, 0, []));

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken) =>
        await db.Set<Formula1GuildConfigEntity>().Where(c => c.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
}
