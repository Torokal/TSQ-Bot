using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Modules.Esports.Application;

public enum CheckState
{
    Ok = 0,
    Warning = 1,
    Problem = 2,
    Info = 3,
}

public sealed record DoctorCheck(string LabelKey, CheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>
/// /esports-admin doctor — permissions, provider and delivery diagnosis for admins. No secrets, no stack traces.
/// Each missing Discord permission is listed separately; nothing is "fixed" by widening permissions automatically.
/// </summary>
public sealed class EsportsDoctor(
    ToroDbContext db,
    IModuleGate gate,
    IGuildGateway guilds,
    EsportsCache cache,
    IEsportsDataProvider provider,
    EsportsDataMode mode,
    DeploymentPolicy deployment,
    TimeProvider clock)
{
    public async Task<(OperationResult Auth, IReadOnlyList<DoctorCheck> Checks)> RunAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), []);

        var checks = new List<DoctorCheck>();
        var guild = actor.GuildId;
        var now = clock.GetUtcNow();

        var enabled = await gate.IsEnabledAsync(guild, EsportsModule.ModuleIdTyped, ct);
        checks.Add(new("doctor.module", enabled ? CheckState.Ok : CheckState.Warning, enabled ? "doctor.module_on" : "doctor.module_off", []));

        var config = await db.Set<EsportsGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        if (config?.ChannelId is not { } channelId)
        {
            checks.Add(new("doctor.channel", CheckState.Problem, "doctor.channel_missing", []));
        }
        else
        {
            var access = await guilds.GetBotChannelAccessAsync(guild, new ChannelId(channelId), ct);
            if (!access.Exists)
            {
                checks.Add(new("doctor.channel", CheckState.Problem, "doctor.channel_gone", [channelId]));
            }
            else
            {
                foreach (var perm in new[] { GuildPermission.ViewChannel, GuildPermission.SendMessages, GuildPermission.EmbedLinks })
                    checks.Add(new("doctor.perm", access.Permissions.Grants(perm) ? CheckState.Ok : CheckState.Problem, "doctor.perm_state", [perm.ToString()]));
                // Needed only for ambiguous-delivery reconciliation.
                checks.Add(new("doctor.perm", access.Permissions.Grants(GuildPermission.ReadMessageHistory) ? CheckState.Ok : CheckState.Warning,
                    "doctor.perm_history", [nameof(GuildPermission.ReadMessageHistory)]));
            }

            if (config.ChannelProblem is not null)
                checks.Add(new("doctor.channel", CheckState.Problem, "doctor.channel_problem", [config.ChannelProblem]));
        }

        if (config?.Paused == true)
            checks.Add(new("doctor.paused", CheckState.Warning, "doctor.paused_on", []));

        var snapshot = await guilds.GetRoleSnapshotAsync(guild, ct);
        var mappings = await db.Set<RoleMappingEntity>().AsNoTracking().Where(m => m.GuildId == guild.Value).ToListAsync(ct);
        foreach (var mapping in mappings)
        {
            if (snapshot is null)
                break;
            var (valid, pingWorks) = SelfServiceRolePolicy.EvaluateMentionTarget(snapshot, new RoleId(mapping.RoleId));
            if (!valid)
                checks.Add(new("doctor.role", CheckState.Problem, "doctor.role_missing", [mapping.RoleId]));
            else if (!pingWorks)
                checks.Add(new("doctor.role", CheckState.Warning, "doctor.role_not_mentionable", [mapping.RoleId]));
            if (mapping.SelfService)
            {
                var verdict = SelfServiceRolePolicy.Evaluate(snapshot, new RoleId(mapping.RoleId));
                checks.Add(new("doctor.selfservice", verdict.IsSafe ? CheckState.Ok : CheckState.Problem,
                    verdict.IsSafe ? "doctor.selfservice_ok" : "doctor.selfservice_unsafe", [mapping.RoleId, string.Join(", ", verdict.Problems)]));
            }
        }

        // Provider
        var providerState = !provider.IsConfigured
            ? CheckState.Problem
            : cache.Matches.IsStale(now, cache.StaleAfter) ? CheckState.Warning : CheckState.Ok;
        checks.Add(new("doctor.provider", providerState, "doctor.provider_state",
        [
            mode.IsDemo ? "FIXTURE/DEMO" : "LIVE",
            provider.IsConfigured ? "configured" : "NOT_CONFIGURED",
            cache.Matches.FetchedAt?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            cache.Matches.LastOutcome?.ToString() ?? "-",
        ]));
        if (mode.IsDemo && deployment.RealDiscordConnection && !deployment.IsTestGuild(guild))
            checks.Add(new("doctor.provider", CheckState.Warning, "doctor.demo_not_test_guild", []));
        checks.Add(new("doctor.live_status", CheckState.Info, (provider.Capabilities & ProviderCapability.VerifiedLiveStatus) != 0 ? "doctor.live_supported" : "doctor.live_unsupported", []));

        var vrs = cache.Rankings;
        if (config?.VrsTopN is not null && vrs.Data is null)
            checks.Add(new("doctor.vrs", CheckState.Problem, "doctor.vrs_missing_filter_blocks", []));
        else
            checks.Add(new("doctor.vrs", vrs.Data is null ? CheckState.Warning : CheckState.Ok, "doctor.vrs_state",
                [vrs.Data?.PublishedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-"]));

        // Delivery
        var since = now - TimeSpan.FromDays(1);
        var outboxRows = await db.Outbox.AsNoTracking()
            .Where(o => o.GuildId == guild.Value && o.ModuleId == EsportsModule.ModuleIdValue && o.UpdatedAt >= since)
            .Select(o => new { o.Status, o.LastError })
            .ToListAsync(ct);
        var unknown = outboxRows.Count(r => r.Status == OutboxStatus.DeliveryUnknown);
        var failed = outboxRows.Count(r => r.Status == OutboxStatus.Failed);
        var pending = outboxRows.Count(r => r.Status == OutboxStatus.Pending);
        var sent = outboxRows.Count(r => r.Status == OutboxStatus.Sent);
        checks.Add(new("doctor.delivery", unknown + failed > 0 ? CheckState.Warning : CheckState.Ok, "doctor.delivery_stats", [sent, pending, failed, unknown]));

        return (OperationResult.Ok("doctor.done"), checks);
    }
}

/// <summary>/privacy export & delete for the esports module (only the caller's rows, only this guild).</summary>
public sealed class EsportsUserData(ToroDbContext db, SubscriptionService subscriptions) : IUserDataContributor
{
    public ModuleId Module => EsportsModule.ModuleIdTyped;

    public async Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var follows = await db.Set<TeamFollowEntity>().AsNoTracking().Where(f => f.GuildId == guild.Value && f.UserId == user.Value).ToListAsync(cancellationToken);
        var pref = await db.Set<UserPreferenceEntity>().AsNoTracking().FirstOrDefaultAsync(p => p.GuildId == guild.Value && p.UserId == user.Value, cancellationToken);
        var grants = await db.Set<RoleGrantEntity>().AsNoTracking().Where(g => g.GuildId == guild.Value && g.UserId == user.Value).ToListAsync(cancellationToken);
        return new JsonObject
        {
            ["teamFollows"] = new JsonArray(follows.Select(f => (JsonNode)new JsonObject
            {
                ["teamKey"] = f.TeamKey,
                ["teamName"] = f.TeamName,
                ["createdAtUtc"] = f.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            }).ToArray()),
            ["preferences"] = pref is null ? null : new JsonObject { ["hideResults"] = pref.HideResults },
            ["roleGrants"] = new JsonArray(grants.Select(g => (JsonNode)new JsonObject
            {
                ["roleId"] = g.RoleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["state"] = g.State.ToString(),
                ["grantedByBot"] = g.GrantedByBot,
                ["hadRoleBefore"] = g.HadRoleBefore,
            }).ToArray()),
        };
    }

    public async Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var follows = await db.Set<TeamFollowEntity>().CountAsync(f => f.GuildId == guild.Value && f.UserId == user.Value, cancellationToken);
        var prefs = await db.Set<UserPreferenceEntity>().CountAsync(p => p.GuildId == guild.Value && p.UserId == user.Value, cancellationToken);
        var botRoles = await db.Set<RoleGrantEntity>().CountAsync(g => g.GuildId == guild.Value && g.UserId == user.Value && g.GrantedByBot, cancellationToken);
        var items = new List<DeletionPreviewItem>();
        if (follows > 0) items.Add(new("privacy.item.follows", follows));
        if (prefs > 0) items.Add(new("privacy.item.prefs", prefs));
        if (botRoles > 0) items.Add(new("privacy.item.bot_roles", botRoles));
        return items;
    }

    public async Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var n = await db.Set<TeamFollowEntity>().Where(f => f.GuildId == guild.Value && f.UserId == user.Value).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<UserPreferenceEntity>().Where(p => p.GuildId == guild.Value && p.UserId == user.Value).ExecuteDeleteAsync(cancellationToken);
        // With no follows left, reconciliation removes bot-granted roles and forgets pre-existing ones.
        var notes = await subscriptions.ReconcileRolesAsync(guild, user, null, cancellationToken);
        var remaining = await db.Set<RoleGrantEntity>().Where(g => g.GuildId == guild.Value && g.UserId == user.Value).ToListAsync(cancellationToken);
        return new DeletionReport(Module, n, remaining.Count > 0 ? [.. notes, "privacy.roles_pending"] : notes);
    }

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var g = guild.Value;
        var n = await db.Set<TeamFollowEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<UserPreferenceEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<RoleGrantEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<RoleMappingEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<EsportsFilterEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<EsportsGuildConfigEntity>().Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        return n;
    }
}

/// <summary>Cheap health summary for /bot status (reads cached provider state; never calls providers).</summary>
public sealed class EsportsHealthCheck(EsportsCache cache, IEsportsDataProvider provider, EsportsDataMode mode, TimeProvider clock) : IModuleHealthCheck
{
    public ModuleId Module => EsportsModule.ModuleIdTyped;

    public Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var entries = new List<HealthEntry>();
        if (!provider.IsConfigured)
        {
            entries.Add(new("health.component.data", HealthState.NotConfigured, "health.provider_not_configured"));
        }
        else
        {
            var m = cache.Matches;
            var state = m.FetchedAt is null ? HealthState.Unavailable : m.IsStale(now, cache.StaleAfter) ? HealthState.Degraded : HealthState.Healthy;
            entries.Add(new(mode.IsDemo ? "health.component.data_demo" : "health.component.data", state,
                m.FetchedAt is null ? "health.no_data_yet" : "health.data_age", m.FetchedAt is null ? null : [ToroSquad.Core.Messaging.DiscordText.Timestamp(m.FetchedAt.Value, 'R')]));
        }

        var r = cache.Rankings;
        entries.Add(new("VRS", r.Data is null ? HealthState.Unavailable : HealthState.Healthy,
            r.Data is null ? "health.vrs_missing" : "health.vrs_date", r.Data is null ? null : [r.Data.PublishedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)]));
        return Task.FromResult(new ModuleHealthReport(Module, entries));
    }
}
