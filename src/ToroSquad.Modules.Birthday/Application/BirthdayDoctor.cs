using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Persistence;

namespace ToroSquad.Modules.Birthday.Application;

public enum BirthdayCheckState
{
    Ok = 0,
    Warning = 1,
    Problem = 2,
    Info = 3,
}

public sealed record BirthdayDoctorCheck(string LabelKey, BirthdayCheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>
/// /tsq-admin modul:birthday islem:doctor — configuration, announcement channel and permissions, the role (exists, not managed, grants no
/// extra permission, Manage Roles, and the bot's highest role ABOVE it — Manage Roles alone is not enough), database, the
/// reconciliation loop and delivery mode. Reads the gateway cache and the database only; sends nothing, changes nothing.
/// </summary>
public sealed class BirthdayDoctor(
    ToroDbContext db,
    IModuleGate gate,
    IGuildGateway guilds,
    BirthdayHealth health,
    IOptions<BirthdayOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock)
{
    public async Task<(OperationResult Auth, IReadOnlyList<BirthdayDoctorCheck> Checks)> RunAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), []);

        var checks = new List<BirthdayDoctorCheck>();
        var guild = actor.GuildId;
        var o = options.Value;
        void Add(string label, BirthdayCheckState state, string detail, params object[] args) => checks.Add(new(label, state, detail, args));

        var problems = o.Validate();
        Add("birthday.doctor.config", problems.Count == 0 ? BirthdayCheckState.Ok : BirthdayCheckState.Problem,
            problems.Count == 0 ? "birthday.doctor.config_ok" : "birthday.doctor.config_invalid", problems.Count == 0 ? o.TimeZone : string.Join("; ", problems));

        var enabled = await gate.IsEnabledAsync(guild, BirthdayModule.ModuleIdTyped, ct);
        Add("birthday.doctor.module", enabled ? BirthdayCheckState.Ok : BirthdayCheckState.Warning, enabled ? "birthday.doctor.module_on" : "birthday.doctor.module_off");

        BirthdayGuildConfigEntity? config = null;
        try
        {
            config = await db.Set<BirthdayGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
            var registered = await db.Set<BirthdayRegistrationEntity>().CountAsync(r => r.GuildId == guild.Value, ct);
            Add("birthday.doctor.database", BirthdayCheckState.Ok, "birthday.doctor.database_ok", registered);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Add("birthday.doctor.database", BirthdayCheckState.Problem, "birthday.doctor.database_failed", ex.GetType().Name);
        }

        await ChannelAsync(guild, config, Add, ct);
        await RoleAsync(guild, o, Add, ct);
        await RoleWorkAsync(guild, Add, ct);
        Scheduler(o, Add);

        if (delivery.Value.Mode != DeliveryMode.Send)
            Add("birthday.doctor.delivery", BirthdayCheckState.Warning, "birthday.doctor.delivery_dry_run");

        return (OperationResult.Ok("birthday.doctor.done"), checks);
    }

    private async Task ChannelAsync(GuildId guild, BirthdayGuildConfigEntity? config, Action<string, BirthdayCheckState, string, object[]> add, CancellationToken ct)
    {
        if (config?.ChannelId is not { } channelId)
        {
            add("birthday.doctor.channel", BirthdayCheckState.Problem, "birthday.doctor.channel_missing", []);
            return;
        }

        var mention = "<#" + channelId.ToString(CultureInfo.InvariantCulture) + ">";
        var access = await guilds.GetBotChannelAccessAsync(guild, new ChannelId(channelId), ct);
        if (!access.Exists || !access.IsTextBased)
        {
            add("birthday.doctor.channel", BirthdayCheckState.Problem, "birthday.doctor.channel_gone", [mention]);
            return;
        }

        add("birthday.doctor.channel", BirthdayCheckState.Ok, "birthday.doctor.channel_ok", [mention]);
        foreach (var (perm, label) in new[] { (GuildPermission.ViewChannel, "birthday.doctor.perm_view"), (GuildPermission.SendMessages, "birthday.doctor.perm_send") })
        {
            var ok = access.Permissions.Grants(perm);
            add(label, ok ? BirthdayCheckState.Ok : BirthdayCheckState.Problem, ok ? "birthday.doctor.passed" : "birthday.doctor.perm_missing", [perm.ToString()]);
        }

        if (config.ChannelProblem is not null)
            add("birthday.doctor.channel", BirthdayCheckState.Problem, "birthday.doctor.channel_problem", [config.ChannelProblem]);
    }

    private async Task RoleAsync(GuildId guild, BirthdayOptions o, Action<string, BirthdayCheckState, string, object[]> add, CancellationToken ct)
    {
        if (o.Role is not { } roleId)
        {
            add("birthday.doctor.role", BirthdayCheckState.Warning, "birthday.doctor.role_disabled", []);
            return;
        }

        var snapshot = await guilds.GetRoleSnapshotAsync(guild, ct);
        if (snapshot is null)
        {
            add("birthday.doctor.role", BirthdayCheckState.Problem, "birthday.doctor.role_unverifiable", [roleId.ToString()]);
            return;
        }

        var role = snapshot.Find(roleId);
        if (role is null)
        {
            add("birthday.doctor.role", BirthdayCheckState.Problem, "birthday.doctor.role_missing", [roleId.ToString()]);
            return;
        }

        var verdict = SelfServiceRolePolicy.Evaluate(snapshot, roleId);
        var mention = DiscordText.RoleMention(roleId);
        add("birthday.doctor.role", BirthdayCheckState.Ok, "birthday.doctor.role_ok", [mention, roleId.ToString()]);

        var manage = snapshot.BotGuildPermissions.Grants(GuildPermission.ManageRoles);
        add("birthday.doctor.manage_roles", manage ? BirthdayCheckState.Ok : BirthdayCheckState.Problem, manage ? "birthday.doctor.passed" : "birthday.doctor.manage_roles_missing", []);

        var above = snapshot.BotHighestPosition > role.Position;
        add("birthday.doctor.hierarchy", above ? BirthdayCheckState.Ok : BirthdayCheckState.Problem, above ? "birthday.doctor.hierarchy_ok" : "birthday.doctor.hierarchy_failed",
            [snapshot.BotHighestPosition, role.Position]);

        var managed = role.IsManaged || role.IsEveryone;
        add("birthday.doctor.managed", managed ? BirthdayCheckState.Problem : BirthdayCheckState.Ok, managed ? "birthday.doctor.managed_failed" : "birthday.doctor.managed_ok", []);

        if (verdict.Problems.Contains(RoleSafetyProblem.GrantsGuildPermissions))
            add("birthday.doctor.role_permissions", BirthdayCheckState.Problem, "birthday.doctor.role_permissions_failed", [verdict.ExtraPermissions.ToString()]);
        if (verdict.Problems.Contains(RoleSafetyProblem.GrantsChannelPermissions))
            add("birthday.doctor.role_permissions", BirthdayCheckState.Info, "birthday.doctor.role_channel_allows", [verdict.ChannelsWithAllows.Count]);
    }

    private async Task RoleWorkAsync(GuildId guild, Action<string, BirthdayCheckState, string, object[]> add, CancellationToken ct)
    {
        var rows = await db.Set<BirthdayCelebrationEntity>().AsNoTracking()
            .Where(c => c.GuildId == guild.Value && (c.RoleState == BirthdayRoleState.Failed ||
                                                     ((c.RoleState == BirthdayRoleState.Pending || c.RoleState == BirthdayRoleState.Active) && c.RoleError != null)))
            .Select(c => c.RoleError).ToListAsync(ct);
        if (rows.Count > 0)
            add("birthday.doctor.role_work", BirthdayCheckState.Warning, "birthday.doctor.role_work_value", [rows.Count, string.Join(", ", rows.OfType<string>().Distinct().Take(3))]);
    }

    private void Scheduler(BirthdayOptions o, Action<string, BirthdayCheckState, string, object[]> add)
    {
        var now = clock.GetUtcNow();
        if (health.WorkerStartedAt is null)
            add("birthday.doctor.scheduler", BirthdayCheckState.Problem, "birthday.doctor.scheduler_not_running", []);
        else if (health.LastCompletedAt is not { } done)
            add("birthday.doctor.scheduler", BirthdayCheckState.Warning, "birthday.doctor.scheduler_no_pass", [DiscordText.Timestamp(health.WorkerStartedAt.Value, 'R')]);
        else if (!health.IsFresh(now, o.ReconciliationInterval))
            add("birthday.doctor.scheduler", BirthdayCheckState.Problem, "birthday.doctor.scheduler_stale", [DiscordText.Timestamp(done, 'R'), health.LastError ?? "-"]);
        else
            add("birthday.doctor.scheduler", health.ConsecutiveFailures > 0 ? BirthdayCheckState.Warning : BirthdayCheckState.Ok, "birthday.doctor.scheduler_ok",
                [DiscordText.Timestamp(done, 'R'), o.ReconciliationIntervalSeconds, health.ConsecutiveFailures]);
    }
}
