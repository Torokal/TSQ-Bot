namespace ToroSquad.Core.Security;

/// <summary>
/// Who is performing an operation, in which guild, with which effective guild permissions.
/// Built by the Discord layer from the interaction payload (never from user-supplied IDs) and passed
/// to every application operation so authorization is re-checked server-side for slash commands,
/// buttons, select menus and modals alike. Hiding a command in Discord's picker is not a security control.
/// </summary>
public sealed record ActorContext(
    GuildId GuildId,
    UserId UserId,
    GuildPermission Permissions,
    IReadOnlyCollection<RoleId> RoleIds,
    bool IsGuildOwner,
    int HighestRolePosition)
{
    public bool Has(GuildPermission required) => IsGuildOwner || Permissions.Grants(required);
}

public enum AuthorizationFailure
{
    None = 0,
    MissingPermission = 1,
    WrongGuild = 2,
    RoleHierarchy = 3,
    NotOwnerOfResource = 4,
}

public sealed record AuthorizationResult(AuthorizationFailure Failure, GuildPermission Missing = GuildPermission.None)
{
    public static AuthorizationResult Allowed { get; } = new(AuthorizationFailure.None);
    public bool IsAllowed => Failure == AuthorizationFailure.None;
}

/// <summary>Central authorization rules shared by all modules.</summary>
public static class Authorize
{
    /// <summary>Server configuration (setup, modules, esports-admin) requires Manage Server.</summary>
    public const GuildPermission ServerSettings = GuildPermission.ManageGuild;

    /// <summary>Role mapping / self-service role approval requires Manage Server + Manage Roles.</summary>
    public const GuildPermission RoleSettings = GuildPermission.ManageGuild | GuildPermission.ManageRoles;

    public static AuthorizationResult Require(ActorContext actor, GuildId resourceGuild, GuildPermission required)
    {
        if (actor.GuildId != resourceGuild)
            return new(AuthorizationFailure.WrongGuild);
        if (!actor.Has(required))
            return new(AuthorizationFailure.MissingPermission, required & ~actor.Permissions);
        return AuthorizationResult.Allowed;
    }

    /// <summary>
    /// A non-owner may only manage roles strictly below their own highest role (Discord hierarchy semantics).
    /// </summary>
    public static AuthorizationResult RequireAboveRole(ActorContext actor, int targetRolePosition)
    {
        if (actor.IsGuildOwner)
            return AuthorizationResult.Allowed;
        return actor.HighestRolePosition > targetRolePosition
            ? AuthorizationResult.Allowed
            : new(AuthorizationFailure.RoleHierarchy);
    }
}
