using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Persistence;

namespace ToroSquad.Modules.Esports.Application;

public sealed record RoleMappingView(long Id, ulong RoleId, string TeamKey, string? TeamName, bool PingOnReminder, bool PingOnResult, bool SelfService);

/// <summary>
/// /esports-admin roles. Two distinct, separately authorized decisions:
/// (1) mapping an existing role as a notification PING target, and
/// (2) approving that role for member SELF-SERVICE (strict safety evaluation + hierarchy, re-checked at every grant).
/// The bot never creates roles or changes "mentionable" by itself.
/// </summary>
public sealed class RoleMappingService(ToroDbContext db, IGuildGateway guilds, EsportsCache cache, TimeProvider clock)
{
    public const int MaxMappings = 25;

    public async Task<IReadOnlyList<RoleMappingView>> ListAsync(GuildId guild, CancellationToken ct) =>
        (await db.Set<RoleMappingEntity>().AsNoTracking().Where(m => m.GuildId == guild.Value).OrderBy(m => m.Id).ToListAsync(ct))
        .Select(m => new RoleMappingView(m.Id, m.RoleId, m.TeamKey, m.TeamName, m.PingOnReminder, m.PingOnResult, m.SelfService))
        .ToList();

    public async Task<OperationResult> MapAsync(ActorContext actor, RoleId role, string? teamKey, bool pingOnReminder, bool pingOnResult, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.RoleSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        var snapshot = await guilds.GetRoleSnapshotAsync(actor.GuildId, ct);
        if (snapshot is null)
            return OperationResult.Fail(OperationError.ProviderUnavailable, "esports.roles.guild_unavailable");
        var info = snapshot.Find(role);
        if (info is null || info.IsEveryone)
            return OperationResult.Fail(OperationError.InvalidInput, "esports.roles.invalid_role");
        if (info.IsManaged)
            return OperationResult.Fail(OperationError.InvalidInput, "esports.roles.managed_role");
        if (!info.IsMentionable && (pingOnReminder || pingOnResult) && !actor.Has(GuildPermission.MentionEveryone))
            return OperationResult.Fail(OperationError.Forbidden, "esports.roles.mention_not_allowed");

        string? teamName = null;
        teamKey = string.IsNullOrWhiteSpace(teamKey) ? "" : teamKey.Trim();
        if (teamKey.Length > 0)
        {
            teamName = cache.Teams.FirstOrDefault(t => t.Key == teamKey)?.Name
                       ?? (await db.Set<KnownTeamEntity>().AsNoTracking().FirstOrDefaultAsync(t => t.TeamKey == teamKey, ct))?.Name;
            if (teamName is null)
                return OperationResult.Fail(OperationError.NotFound, "esports.team.unknown");
        }

        var set = db.Set<RoleMappingEntity>();
        var existing = await set.FirstOrDefaultAsync(m => m.GuildId == actor.GuildId.Value && m.RoleId == role.Value && m.TeamKey == teamKey, ct);
        if (existing is null)
        {
            if (await set.CountAsync(m => m.GuildId == actor.GuildId.Value, ct) >= MaxMappings)
                return OperationResult.Fail(OperationError.InvalidInput, "esports.roles.too_many", MaxMappings);
            existing = new RoleMappingEntity { GuildId = actor.GuildId.Value, RoleId = role.Value, TeamKey = teamKey, CreatedAt = clock.GetUtcNow() };
            set.Add(existing);
        }

        existing.TeamName = teamName;
        existing.PingOnReminder = pingOnReminder;
        existing.PingOnResult = pingOnResult;
        await db.SaveChangesAsync(ct);

        var (_, pingWorks) = SelfServiceRolePolicy.EvaluateMentionTarget(snapshot, role);
        return pingWorks ? OperationResult.Ok("esports.roles.mapped") : OperationResult.Ok("esports.roles.mapped_ping_warning");
    }

    public async Task<OperationResult> UnmapAsync(ActorContext actor, long mappingId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.RoleSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        // Guild-bound lookup: an id from another guild simply does not exist here.
        var deleted = await db.Set<RoleMappingEntity>().Where(m => m.Id == mappingId && m.GuildId == actor.GuildId.Value).ExecuteDeleteAsync(ct);
        return deleted == 0 ? OperationResult.Fail(OperationError.NotFound, "esports.roles.mapping_not_found") : OperationResult.Ok("esports.roles.unmapped");
    }

    public async Task<OperationResult> SetSelfServiceAsync(ActorContext actor, long mappingId, bool enable, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.RoleSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        var mapping = await db.Set<RoleMappingEntity>().FirstOrDefaultAsync(m => m.Id == mappingId && m.GuildId == actor.GuildId.Value, ct);
        if (mapping is null)
            return OperationResult.Fail(OperationError.NotFound, "esports.roles.mapping_not_found");

        if (enable)
        {
            var snapshot = await guilds.GetRoleSnapshotAsync(actor.GuildId, ct);
            if (snapshot is null)
                return OperationResult.Fail(OperationError.ProviderUnavailable, "esports.roles.guild_unavailable");
            var role = snapshot.Find(new RoleId(mapping.RoleId));
            if (role is null)
                return OperationResult.Fail(OperationError.NotFound, "esports.roles.invalid_role");
            var hierarchy = Authorize.RequireAboveRole(actor, role.Position);
            if (!hierarchy.IsAllowed)
                return OperationResult.Forbidden(hierarchy);
            var verdict = SelfServiceRolePolicy.Evaluate(snapshot, role.Id);
            if (!verdict.IsSafe)
                return OperationResult.Fail(OperationError.Unsafe, "esports.roles.unsafe", string.Join(", ", verdict.Problems));
            mapping.SelfService = true;
            mapping.SelfServiceApprovedBy = actor.UserId.Value;
            mapping.SelfServiceApprovedAt = clock.GetUtcNow();
        }
        else
        {
            mapping.SelfService = false;
        }

        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(enable ? "esports.roles.selfservice_on" : "esports.roles.selfservice_off");
    }

    /// <summary>Resolves a panel button (mapping id) → follow key, strictly within the clicking user's guild.</summary>
    public async Task<string?> PanelFollowKeyAsync(GuildId guild, long mappingId, CancellationToken ct)
    {
        var mapping = await db.Set<RoleMappingEntity>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == mappingId && m.GuildId == guild.Value && m.SelfService, ct);
        return mapping is null ? null : mapping.TeamKey.Length == 0 ? SubscriptionService.AllMatchesKey : mapping.TeamKey;
    }
}
