using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;

namespace ToroSquad.Modules.Esports.Application;

public sealed record SubscriptionOutcome(OperationResult Result, IReadOnlyList<string> NoteKeys);

public sealed record FollowView(string TeamKey, string TeamName, IReadOnlyList<ulong> RoleIds);

/// <summary>
/// Personal team follows and the self-service notification roles they imply. Users only ever change their own
/// follows. Roles are reconciled against a desired state:
/// <list type="bullet">
/// <item>desired = self-service mappings whose team the user follows ("*" = the all-matches mapping).</item>
/// <item>Adding: skipped if the member already had the role (recorded as pre-existing, never removed later).</item>
/// <item>Removing: only roles the bot itself granted, and only when no remaining follow still needs that role.</item>
/// <item>DB state is written as Pending before calling Discord and only marked Active/removed on success.</item>
/// <item>Role safety (permissions, private-channel access, managed, hierarchy) is re-evaluated at every grant.</item>
/// </list>
/// </summary>
public sealed class SubscriptionService(ToroDbContext db, IGuildGateway guilds, EsportsCache cache, TimeProvider clock, ILogger<SubscriptionService> logger)
{
    public const string AllMatchesKey = "*";
    public const int MaxFollowsPerUser = 25;

    public async Task<SubscriptionOutcome> FollowAsync(ActorContext actor, string teamKey, CancellationToken ct)
    {
        var team = await ResolveTeamAsync(actor.GuildId, teamKey, ct);
        if (team is null)
            return new(OperationResult.Fail(OperationError.NotFound, "esports.team.unknown"), []);

        var follows = db.Set<TeamFollowEntity>();
        if (await follows.AnyAsync(f => f.GuildId == actor.GuildId.Value && f.UserId == actor.UserId.Value && f.TeamKey == team.Value.Key, ct))
            return new(OperationResult.Ok("esports.follow.already", team.Value.Name), []);
        if (await follows.CountAsync(f => f.GuildId == actor.GuildId.Value && f.UserId == actor.UserId.Value, ct) >= MaxFollowsPerUser)
            return new(OperationResult.Fail(OperationError.InvalidInput, "esports.follow.too_many", MaxFollowsPerUser), []);

        follows.Add(new TeamFollowEntity
        {
            GuildId = actor.GuildId.Value,
            UserId = actor.UserId.Value,
            TeamKey = team.Value.Key,
            TeamName = team.Value.Name,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);

        var notes = await ReconcileRolesAsync(actor.GuildId, actor.UserId, actor.RoleIds, ct);
        var hasRole = await db.Set<RoleMappingEntity>().AnyAsync(m => m.GuildId == actor.GuildId.Value && m.SelfService &&
            (m.TeamKey == team.Value.Key || (team.Value.Key == AllMatchesKey && m.TeamKey == "")), ct);
        if (!hasRole)
            notes.Add("esports.follow.no_role_note");
        return new(OperationResult.Ok("esports.follow.done", team.Value.Name), notes);
    }

    public async Task<SubscriptionOutcome> UnfollowAsync(ActorContext actor, string teamKey, CancellationToken ct)
    {
        var row = await db.Set<TeamFollowEntity>().FirstOrDefaultAsync(f =>
            f.GuildId == actor.GuildId.Value && f.UserId == actor.UserId.Value && f.TeamKey == teamKey, ct);
        if (row is null)
            return new(OperationResult.Ok("esports.unfollow.not_following"), []);
        db.Remove(row);
        await db.SaveChangesAsync(ct);
        var notes = await ReconcileRolesAsync(actor.GuildId, actor.UserId, actor.RoleIds, ct);
        return new(OperationResult.Ok("esports.unfollow.done", row.TeamName), notes);
    }

    public async Task<SubscriptionOutcome> ToggleAsync(ActorContext actor, string teamKey, CancellationToken ct) =>
        await db.Set<TeamFollowEntity>().AnyAsync(f => f.GuildId == actor.GuildId.Value && f.UserId == actor.UserId.Value && f.TeamKey == teamKey, ct)
            ? await UnfollowAsync(actor, teamKey, ct)
            : await FollowAsync(actor, teamKey, ct);

    public async Task<IReadOnlyList<FollowView>> ListAsync(ActorContext actor, CancellationToken ct)
    {
        var follows = await db.Set<TeamFollowEntity>().AsNoTracking()
            .Where(f => f.GuildId == actor.GuildId.Value && f.UserId == actor.UserId.Value)
            .OrderBy(f => f.TeamName).ToListAsync(ct);
        var grants = await db.Set<RoleGrantEntity>().AsNoTracking()
            .Where(g => g.GuildId == actor.GuildId.Value && g.UserId == actor.UserId.Value && g.State == RoleGrantState.Active)
            .Select(g => g.RoleId).ToListAsync(ct);
        var mappings = await db.Set<RoleMappingEntity>().AsNoTracking().Where(m => m.GuildId == actor.GuildId.Value && m.SelfService).ToListAsync(ct);
        return follows.Select(f => new FollowView(f.TeamKey, f.TeamName,
            mappings.Where(m => MappingMatchesFollow(m, f.TeamKey) && grants.Contains(m.RoleId)).Select(m => m.RoleId).Distinct().ToList())).ToList();
    }

    public async Task<bool> GetHideResultsAsync(GuildId guild, UserId user, CancellationToken ct) =>
        (await db.Set<UserPreferenceEntity>().AsNoTracking().FirstOrDefaultAsync(p => p.GuildId == guild.Value && p.UserId == user.Value, ct))?.HideResults ?? false;

    public async Task<OperationResult> SetHideResultsAsync(ActorContext actor, bool hide, CancellationToken ct)
    {
        var set = db.Set<UserPreferenceEntity>();
        var row = await set.FirstOrDefaultAsync(p => p.GuildId == actor.GuildId.Value && p.UserId == actor.UserId.Value, ct);
        if (row is null)
        {
            row = new UserPreferenceEntity { GuildId = actor.GuildId.Value, UserId = actor.UserId.Value };
            set.Add(row);
        }

        row.HideResults = hide;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(hide ? "esports.pref.hide_on" : "esports.pref.hide_off");
    }

    /// <summary>
    /// Brings the member's bot-managed roles in line with their follows. <paramref name="memberRoles"/> are the
    /// member's current roles from the interaction payload (null when unknown, e.g. background retries).
    /// Returns localization keys describing anything that could not be done.
    /// </summary>
    public async Task<List<string>> ReconcileRolesAsync(GuildId guild, UserId user, IReadOnlyCollection<RoleId>? memberRoles, CancellationToken ct)
    {
        var notes = new List<string>();
        var followKeys = await db.Set<TeamFollowEntity>().AsNoTracking()
            .Where(f => f.GuildId == guild.Value && f.UserId == user.Value).Select(f => f.TeamKey).ToListAsync(ct);
        var mappings = await db.Set<RoleMappingEntity>().AsNoTracking().Where(m => m.GuildId == guild.Value && m.SelfService).ToListAsync(ct);
        var desired = mappings.Where(m => followKeys.Any(k => MappingMatchesFollow(m, k)) && m.RoleId != guild.Value)
            .Select(m => m.RoleId).ToHashSet();
        var grants = await db.Set<RoleGrantEntity>().Where(g => g.GuildId == guild.Value && g.UserId == user.Value).ToListAsync(ct);

        GuildRoleSnapshot? snapshot = null;
        foreach (var roleId in desired.Where(r => grants.All(g => g.RoleId != r || g.State != RoleGrantState.Active)))
        {
            var grant = grants.FirstOrDefault(g => g.RoleId == roleId);
            if (memberRoles is not null && memberRoles.Contains(new RoleId(roleId)) && grant is null)
            {
                // Member already had it for another reason: record, never touch it.
                db.Add(new RoleGrantEntity { GuildId = guild.Value, UserId = user.Value, RoleId = roleId, State = RoleGrantState.Active, GrantedByBot = false, HadRoleBefore = true, UpdatedAt = clock.GetUtcNow() });
                continue;
            }

            snapshot ??= await guilds.GetRoleSnapshotAsync(guild, ct);
            var verdict = snapshot is null ? null : SelfServiceRolePolicy.Evaluate(snapshot, new RoleId(roleId));
            if (verdict is null || !verdict.IsSafe)
            {
                notes.Add("esports.roles.unsafe_skipped");
                logger.LogWarning("Self-service role {Role} in guild {Guild} not granted: {Problems}", roleId, guild, verdict is null ? "no snapshot" : string.Join(",", verdict.Problems));
                continue;
            }

            if (grant is null)
            {
                grant = new RoleGrantEntity { GuildId = guild.Value, UserId = user.Value, RoleId = roleId, GrantedByBot = true };
                db.Add(grant);
            }

            grant.State = RoleGrantState.PendingAdd;
            grant.Attempts++;
            grant.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct); // persisted BEFORE the Discord call → reconcilable after a crash

            var outcome = await guilds.AddRoleAsync(guild, user, new RoleId(roleId), "ToroSquad: esports follow (self-service)", ct);
            if (outcome == RoleOperationOutcome.Success)
            {
                grant.State = RoleGrantState.Active;
                grant.LastError = null;
            }
            else
            {
                grant.State = RoleGrantState.Failed;
                grant.LastError = outcome.ToString();
                notes.Add("esports.roles.add_failed");
            }

            grant.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        foreach (var grant in grants.Where(g => !desired.Contains(g.RoleId)))
        {
            if (!grant.GrantedByBot)
            {
                db.Remove(grant); // pre-existing membership: forget the record, keep the role
                continue;
            }

            grant.State = RoleGrantState.PendingRemove;
            grant.Attempts++;
            grant.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            var outcome = await guilds.RemoveRoleAsync(guild, user, new RoleId(grant.RoleId), "ToroSquad: esports unfollow", ct);
            if (outcome is RoleOperationOutcome.Success or RoleOperationOutcome.UnknownRole or RoleOperationOutcome.UnknownMember)
            {
                db.Remove(grant);
            }
            else
            {
                grant.LastError = outcome.ToString();
                notes.Add("esports.roles.remove_failed");
            }

            await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        return notes.Distinct().ToList();
    }

    /// <summary>Background retry for grants left Pending/Failed (bounded attempts).</summary>
    public async Task<int> RetryPendingAsync(int maxAttempts, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(2);
        var stuck = await db.Set<RoleGrantEntity>().AsNoTracking()
            .Where(g => g.State != RoleGrantState.Active && g.Attempts < maxAttempts && g.UpdatedAt < cutoff)
            .Select(g => new { g.GuildId, g.UserId })
            .Distinct()
            .OrderBy(x => x.GuildId).ThenBy(x => x.UserId)
            .Take(50)
            .ToListAsync(ct);
        foreach (var item in stuck)
            await ReconcileRolesAsync(new GuildId(item.GuildId), new UserId(item.UserId), null, ct);
        return stuck.Count;
    }

    private static bool MappingMatchesFollow(RoleMappingEntity mapping, string followKey) =>
        mapping.TeamKey.Length == 0 ? followKey == AllMatchesKey : mapping.TeamKey == followKey;

    private async Task<(string Key, string Name)?> ResolveTeamAsync(GuildId guild, string teamKey, CancellationToken ct)
    {
        if (teamKey == AllMatchesKey)
        {
            var allMapping = await db.Set<RoleMappingEntity>().AnyAsync(m => m.GuildId == guild.Value && m.SelfService && m.TeamKey == "", ct);
            return allMapping ? (AllMatchesKey, "*") : null;
        }

        var cached = cache.Teams.FirstOrDefault(t => t.Key == teamKey);
        if (cached is not null)
            return (cached.Key, cached.Name);
        var known = await db.Set<KnownTeamEntity>().AsNoTracking().FirstOrDefaultAsync(t => t.TeamKey == teamKey, ct);
        return known is null ? null : (known.TeamKey, known.Name);
    }
}
