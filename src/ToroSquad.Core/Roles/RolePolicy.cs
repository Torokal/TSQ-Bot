using ToroSquad.Core.Security;

namespace ToroSquad.Core.Roles;

public sealed record RoleInfo(RoleId Id, string Name, int Position, GuildPermission Permissions, bool IsManaged, bool IsEveryone, bool IsMentionable);

public sealed record ChannelRoleOverwrite(ChannelId Channel, RoleId Role, GuildPermission Allow, GuildPermission Deny);

/// <summary>Point-in-time view of the guild's roles as the bot sees them (from the gateway cache / REST).</summary>
public sealed record GuildRoleSnapshot(
    GuildId Guild,
    IReadOnlyList<RoleInfo> Roles,
    int BotHighestPosition,
    GuildPermission BotGuildPermissions,
    IReadOnlyList<ChannelRoleOverwrite> Overwrites)
{
    public RoleInfo? Find(RoleId id) => Roles.FirstOrDefault(r => r.Id == id);
    public RoleInfo? Everyone => Roles.FirstOrDefault(r => r.IsEveryone);
}

public enum RoleSafetyProblem
{
    NotFound = 1,
    IsEveryone = 2,
    Managed = 3,
    GrantsGuildPermissions = 4,
    GrantsChannelPermissions = 5,
    AboveBot = 6,
    BotLacksManageRoles = 7,
}

public sealed record RoleSafetyVerdict(IReadOnlyList<RoleSafetyProblem> Problems, GuildPermission ExtraPermissions, IReadOnlyList<ChannelId> ChannelsWithAllows)
{
    public bool IsSafe => Problems.Count == 0;
}

/// <summary>
/// Rules for roles the bot may hand out through self-service (follow buttons / panel).
/// Deliberately strict: a self-service role may not grant ANY guild permission beyond what @everyone already
/// has and may not carry ANY channel "allow" overwrite (so it can't open private channels). Managed/integration
/// roles, @everyone and roles at/above the bot's highest role are rejected. Re-evaluated on every grant.
/// Being able to *select* a role ID is never treated as permission to *distribute* it.
/// </summary>
public static class SelfServiceRolePolicy
{
    public static RoleSafetyVerdict Evaluate(GuildRoleSnapshot snapshot, RoleId roleId)
    {
        var problems = new List<RoleSafetyProblem>();
        var role = snapshot.Find(roleId);
        if (role is null)
            return new([RoleSafetyProblem.NotFound], GuildPermission.None, []);

        if (role.IsEveryone)
            problems.Add(RoleSafetyProblem.IsEveryone);
        if (role.IsManaged)
            problems.Add(RoleSafetyProblem.Managed);

        var everyonePerms = snapshot.Everyone?.Permissions ?? GuildPermission.None;
        var extra = role.Permissions & ~everyonePerms;
        if (extra != GuildPermission.None)
            problems.Add(RoleSafetyProblem.GrantsGuildPermissions);

        var channelsWithAllows = snapshot.Overwrites
            .Where(o => o.Role == roleId && o.Allow != GuildPermission.None)
            .Select(o => o.Channel)
            .Distinct()
            .ToList();
        if (channelsWithAllows.Count > 0)
            problems.Add(RoleSafetyProblem.GrantsChannelPermissions);

        if (role.Position >= snapshot.BotHighestPosition)
            problems.Add(RoleSafetyProblem.AboveBot);
        if (!snapshot.BotGuildPermissions.Grants(GuildPermission.ManageRoles))
            problems.Add(RoleSafetyProblem.BotLacksManageRoles);

        return new(problems, extra, channelsWithAllows);
    }

    /// <summary>
    /// For a role used only as a *ping target* (not distributed): it must exist and not be @everyone.
    /// Returns whether a ping will actually notify (role mentionable, or bot has Mention Everyone permission).
    /// </summary>
    public static (bool Valid, bool PingWillWork) EvaluateMentionTarget(GuildRoleSnapshot snapshot, RoleId roleId)
    {
        var role = snapshot.Find(roleId);
        if (role is null || role.IsEveryone)
            return (false, false);
        return (true, role.IsMentionable || snapshot.BotGuildPermissions.Grants(GuildPermission.MentionEveryone));
    }
}

public enum RoleOperationOutcome
{
    Success = 0,
    MissingPermissions = 1,
    UnknownRole = 2,
    UnknownMember = 3,
    Hierarchy = 4,
    Transient = 5,
}

/// <summary>Discord-backed guild operations needed by core policies (implemented in ToroSquad.Discord, faked in tests).</summary>
public interface IGuildGateway
{
    Task<GuildRoleSnapshot?> GetRoleSnapshotAsync(GuildId guild, CancellationToken cancellationToken);

    Task<BotChannelAccess> GetBotChannelAccessAsync(GuildId guild, ChannelId channel, CancellationToken cancellationToken);

    Task<RoleOperationOutcome> AddRoleAsync(GuildId guild, UserId user, RoleId role, string auditReason, CancellationToken cancellationToken);

    Task<RoleOperationOutcome> RemoveRoleAsync(GuildId guild, UserId user, RoleId role, string auditReason, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="channel"/> is a guild voice channel of THIS guild as the bot sees it, and the bot's permissions there.</summary>
    Task<VoiceChannelAccess> GetVoiceChannelAccessAsync(GuildId guild, ChannelId channel, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a member who is ALREADY connected to a voice channel of this guild into <paramref name="channel"/> (Discord:
    /// Modify Guild Member channel_id; needs Move Members + Connect for the bot). Discord offers no way for a bot to connect
    /// a member who is not in voice (<see cref="VoiceMoveOutcome.NotConnected"/>). Never moves a member into a channel they
    /// could not join themselves.
    /// </summary>
    Task<VoiceMoveOutcome> MoveMemberToVoiceAsync(GuildId guild, UserId user, ChannelId channel, CancellationToken cancellationToken);
}

/// <summary>A voice channel as the bot sees it. Stage channels are not <see cref="IsVoice"/> (plain guild voice only).</summary>
public sealed record VoiceChannelAccess(bool Exists, bool IsVoice, GuildPermission BotPermissions)
{
    public static VoiceChannelAccess Missing { get; } = new(false, false, GuildPermission.None);

    public const GuildPermission RequiredToMove = GuildPermission.ViewChannel | GuildPermission.Connect | GuildPermission.MoveMembers;

    public bool Usable => Exists && IsVoice;

    public bool BotCanMove => Usable && BotPermissions.Grants(RequiredToMove);
}

public enum VoiceMoveOutcome
{
    Moved = 0,

    /// <summary>The member is not connected to any voice channel (Discord 40032): only the member can connect themselves.</summary>
    NotConnected = 1,

    /// <summary>The member may not view/connect to the channel themselves; the bot's Move Members never bypasses that.</summary>
    MemberCannotConnect = 2,

    BotMissingPermissions = 3,
    ChannelUnavailable = 4,
    Failed = 5,

    /// <summary>
    /// The channel has a user limit. A bot with Move Members could move a member past it, and occupancy is unknown without
    /// voice-state events, so the member joins themselves (Discord then enforces the limit).
    /// </summary>
    LimitedChannel = 6,
}

public sealed record BotChannelAccess(bool Exists, bool IsTextBased, GuildPermission Permissions)
{
    public static BotChannelAccess Missing { get; } = new(false, false, GuildPermission.None);

    public const GuildPermission RequiredForNotifications =
        GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks;

    public GuildPermission MissingRequired => RequiredForNotifications & ~Permissions;
}
