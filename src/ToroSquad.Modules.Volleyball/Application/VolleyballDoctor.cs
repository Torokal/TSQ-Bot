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
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Persistence;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

public enum VbCheckState
{
    Ok = 0,
    Warning = 1,
    Problem = 2,
    Info = 3,
}

public sealed record VbDoctorCheck(string LabelKey, VbCheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>
/// /volleyball-admin doctor — actionable diagnostics from cached state only (no provider calls, never a secret): channel and
/// permissions, role, data mode, provider health (fixtures / live), data age, backoff, identity rejections, next match,
/// delivery statistics.
/// </summary>
public sealed class VolleyballDoctor(
    ToroDbContext db,
    IModuleGate gate,
    IGuildGateway guilds,
    VolleyballCache cache,
    IVolleyballDataProvider provider,
    VbDataMode mode,
    DeploymentPolicy deployment,
    IOptions<VolleyballOptions> options,
    TimeProvider clock)
{
    public async Task<(OperationResult Auth, IReadOnlyList<VbDoctorCheck> Checks)> RunAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), []);

        var checks = new List<VbDoctorCheck>();
        var guild = actor.GuildId;
        var now = clock.GetUtcNow();
        var o = options.Value;

        var enabled = await gate.IsEnabledAsync(guild, VolleyballModule.ModuleIdTyped, ct);
        checks.Add(new("vb.doctor.module", enabled ? VbCheckState.Ok : VbCheckState.Warning, enabled ? "vb.doctor.module_on" : "vb.doctor.module_off", []));

        var config = await db.Set<VolleyballGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        if (config?.ChannelId is not { } channelId)
        {
            checks.Add(new("vb.doctor.channel", VbCheckState.Problem, "vb.doctor.channel_missing", []));
        }
        else
        {
            var access = await guilds.GetBotChannelAccessAsync(guild, new ChannelId(channelId), ct);
            if (!access.Exists)
            {
                checks.Add(new("vb.doctor.channel", VbCheckState.Problem, "vb.doctor.channel_gone", [channelId]));
            }
            else
            {
                foreach (var perm in new[] { GuildPermission.ViewChannel, GuildPermission.SendMessages, GuildPermission.EmbedLinks })
                {
                    checks.Add(new("vb.doctor.perm", access.Permissions.Grants(perm) ? VbCheckState.Ok : VbCheckState.Problem,
                        access.Permissions.Grants(perm) ? "vb.doctor.perm_ok" : "vb.doctor.perm_missing", [perm.ToString()]));
                }
            }

            if (config.ChannelProblem is not null)
                checks.Add(new("vb.doctor.channel", VbCheckState.Problem, "vb.doctor.channel_problem", [config.ChannelProblem]));
        }

        if (config?.Paused == true)
            checks.Add(new("vb.doctor.paused", VbCheckState.Warning, "vb.doctor.paused_on", []));

        if (config?.PingRoleId is { } roleId)
        {
            var snapshot = await guilds.GetRoleSnapshotAsync(guild, ct);
            if (snapshot is not null)
            {
                var (valid, pingWorks) = SelfServiceRolePolicy.EvaluateMentionTarget(snapshot, new RoleId(roleId));
                checks.Add(new("vb.doctor.role", !valid ? VbCheckState.Problem : pingWorks ? VbCheckState.Ok : VbCheckState.Warning,
                    !valid ? "vb.doctor.role_missing" : pingWorks ? "vb.doctor.role_ok" : "vb.doctor.role_not_mentionable", [DiscordText.RoleMention(new RoleId(roleId))]));
            }
        }

        checks.Add(new("vb.doctor.scope", VbCheckState.Info, "vb.doctor.scope_value", []));
        checks.Add(new("vb.doctor.mode", mode.IsDemo ? VbCheckState.Info : VbCheckState.Ok, mode.IsDemo ? "vb.doctor.mode_fixture" : "vb.doctor.mode_live", []));
        if (mode.IsDemo && deployment.RealDiscordConnection && !deployment.IsTestGuild(guild))
            checks.Add(new("vb.doctor.mode", VbCheckState.Warning, "vb.doctor.demo_not_test_guild", []));

        if (!provider.IsConfigured)
        {
            checks.Add(new("vb.doctor.provider", VbCheckState.Problem, "vb.doctor.provider_not_configured", [provider.AttributionKey]));
        }
        else
        {
            checks.Add(Feed("vb.doctor.fixtures", cache.Fixtures, o.FixtureStaleAfter, now));
            checks.Add(provider.Capabilities.HasFlag(VbCapabilities.LiveMatchState)
                ? Feed("vb.doctor.live", cache.Live, TimeSpan.FromMinutes(o.LiveStaleAfterMinutes), now, idleOk: true)
                : new("vb.doctor.live", VbCheckState.Warning, "vb.doctor.live_unsupported", []));
            checks.Add(new("vb.doctor.capabilities", VbCheckState.Info, "vb.doctor.capabilities_value", [provider.Capabilities.ToString()]));
        }

        if (cache.AmbiguousRejected > 0)
            checks.Add(new("vb.doctor.identity", VbCheckState.Warning, "vb.doctor.identity_rejected", [cache.AmbiguousRejected]));

        var next = cache.MatchesOrdered().FirstOrDefault(m => m.StartTimeUtc > now && !m.Cancelled && !m.Finished);
        checks.Add(next is { StartTimeUtc: { } start }
            ? new("vb.doctor.next", VbCheckState.Info, "vb.doctor.next_value", [DiscordText.UntrustedPlain(next.OpponentName, 60), DiscordText.Timestamp(start, 'F')])
            : new("vb.doctor.next", VbCheckState.Info, "vb.doctor.next_none", []));

        var problems = await db.Set<VbMatchSnapshotEntity>().AsNoTracking()
            .Where(m => m.LastProblemAt != null && m.LastProblemAt >= now - TimeSpan.FromDays(1)).CountAsync(ct);
        if (problems > 0)
            checks.Add(new("vb.doctor.conflicts", VbCheckState.Warning, "vb.doctor.conflicts_value", [problems]));

        var since = now - TimeSpan.FromDays(1);
        var outboxRows = await db.Outbox.AsNoTracking()
            .Where(x => x.GuildId == guild.Value && x.ModuleId == VolleyballModule.ModuleIdValue && x.UpdatedAt >= since)
            .Select(x => x.Status)
            .ToListAsync(ct);
        var unknown = outboxRows.Count(s => s == OutboxStatus.DeliveryUnknown);
        var failed = outboxRows.Count(s => s == OutboxStatus.Failed);
        checks.Add(new("vb.doctor.delivery", unknown + failed > 0 ? VbCheckState.Warning : VbCheckState.Ok, "vb.doctor.delivery_stats",
            [outboxRows.Count(s => s == OutboxStatus.Sent), outboxRows.Count(s => s == OutboxStatus.Pending), failed, unknown]));

        return (OperationResult.Ok("vb.doctor.done"), checks);
    }

    private static VbDoctorCheck Feed(string label, VbFeed feed, TimeSpan staleAfter, DateTimeOffset now, bool idleOk = false)
    {
        if (feed.LastAttemptAt is null && feed.FetchedAt is null)
            return new(label, idleOk ? VbCheckState.Info : VbCheckState.Warning, idleOk ? "vb.doctor.feed_idle" : "vb.doctor.feed_none", []);
        var health = feed.Health(now, staleAfter);
        var state = health switch
        {
            VbProviderHealth.Healthy => VbCheckState.Ok,
            VbProviderHealth.Degraded when idleOk => VbCheckState.Info,
            VbProviderHealth.Degraded or VbProviderHealth.RateLimited => VbCheckState.Warning,
            _ => VbCheckState.Problem,
        };
        return new(label, state, "vb.doctor.feed_state",
            [health.ToString(), At(feed.FetchedAt), feed.LastOutcome?.ToString() ?? "-", feed.ConsecutiveFailures, Short(feed.LastDetail)]);
    }

    private static string At(DateTimeOffset? at) => at is { } a ? DiscordText.Timestamp(a, 'R') : "-";

    private static string Short(string? detail) => string.IsNullOrWhiteSpace(detail) ? "-" : DiscordText.Untrusted(detail, 120);
}

/// <summary>Cheap health summary for /bot status (cached provider state only; never calls providers, never shows secrets).</summary>
public sealed class VolleyballHealthCheck(VolleyballCache cache, IVolleyballDataProvider provider, VbDataMode mode, IOptions<VolleyballOptions> options, TimeProvider clock)
    : IModuleHealthCheck
{
    public ModuleId Module => VolleyballModule.ModuleIdTyped;

    public Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        var entries = new List<HealthEntry>();
        if (!provider.IsConfigured)
        {
            entries.Add(new HealthEntry("vb.health.fixtures", HealthState.NotConfigured, "vb.health.not_configured", [provider.AttributionKey]));
            return Task.FromResult(new ModuleHealthReport(Module, entries));
        }

        var fixtures = cache.Fixtures;
        entries.Add(fixtures.FetchedAt is null
            ? new HealthEntry(mode.IsDemo ? "vb.health.fixtures_demo" : "vb.health.fixtures", HealthState.Unavailable, "vb.health.no_data_yet")
            : new HealthEntry(mode.IsDemo ? "vb.health.fixtures_demo" : "vb.health.fixtures", Map(fixtures.Health(now, o.FixtureStaleAfter)), "vb.health.data_age",
                [DiscordText.Timestamp(fixtures.FetchedAt.Value, 'R'), fixtures.Health(now, o.FixtureStaleAfter).ToString()]));
        entries.Add(!provider.Capabilities.HasFlag(VbCapabilities.LiveMatchState)
            ? new HealthEntry("vb.health.live", HealthState.NotConfigured, "vb.health.live_unsupported")
            : cache.Live.LastAttemptAt is null
                ? new HealthEntry("vb.health.live", HealthState.Healthy, "vb.health.live_idle")
                : new HealthEntry("vb.health.live", Map(cache.Live.Health(now, TimeSpan.FromMinutes(o.LiveStaleAfterMinutes))), "vb.health.data_age",
                    [cache.Live.FetchedAt is { } f ? DiscordText.Timestamp(f, 'R') : "-", cache.Live.Health(now, TimeSpan.FromMinutes(o.LiveStaleAfterMinutes)).ToString()]));
        var next = cache.MatchesOrdered().FirstOrDefault(m => m.StartTimeUtc > now && !m.Cancelled && !m.Finished);
        if (next?.StartTimeUtc is { } start)
            entries.Add(new HealthEntry("vb.health.next", HealthState.Healthy, "vb.health.next_value", [DiscordText.UntrustedPlain(next.OpponentName, 60), DiscordText.Timestamp(start, 'R')]));
        return Task.FromResult(new ModuleHealthReport(Module, entries));
    }

    private static HealthState Map(VbProviderHealth health) => health switch
    {
        VbProviderHealth.Healthy => HealthState.Healthy,
        VbProviderHealth.Degraded or VbProviderHealth.RateLimited => HealthState.Degraded,
        VbProviderHealth.NotConfigured => HealthState.NotConfigured,
        _ => HealthState.Unavailable,
    };
}

/// <summary>The volleyball module stores no per-user data; guild configuration is purged on retention.</summary>
public sealed class VolleyballUserData(ToroDbContext db) : IUserDataContributor
{
    public ModuleId Module => VolleyballModule.ModuleIdTyped;

    public Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new JsonObject { ["storesPersonalData"] = false });

    public Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeletionPreviewItem>>([]);

    public Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new DeletionReport(Module, 0, []));

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken) =>
        await db.Set<VolleyballGuildConfigEntity>().Where(c => c.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
}
