using ToroSquad.Core;
using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Predictions.Domain;

/// <summary>
/// Who may do what, decided from the <see cref="ActorContext"/> of THIS interaction (Discord sends the member's current roles
/// and permissions with every command, click and form submit, so a role removed after the form opened is seen at once):
/// <list type="bullet">
/// <item>create: the creator role — Administrator alone does not grant it;</item>
/// <item>manage (lock, settle, cancel) a prediction: Administrator or the server owner for every prediction; its own
/// creator only while still holding the creator role;</item>
/// <item>end the tournament: Administrator or the server owner only (the creator role is not enough).</item>
/// </list>
/// Ordinary members need no role for the member commands and for entering. The server owner counts as Administrator.
/// </summary>
public static class PredictionAccess
{
    public static bool HasCreatorRole(ActorContext actor, RoleId creatorRole) => actor.RoleIds.Contains(creatorRole);

    /// <summary>The Administrator permission itself (not "has every permission bit") or the server owner.</summary>
    public static bool IsAdministrator(ActorContext actor) =>
        actor.IsGuildOwner || (actor.Permissions & GuildPermission.Administrator) == GuildPermission.Administrator;

    public static bool CanCreate(ActorContext actor, RoleId creatorRole) => HasCreatorRole(actor, creatorRole);

    public static bool CanManage(ActorContext actor, RoleId creatorRole, UserId creator) =>
        IsAdministrator(actor) || (HasCreatorRole(actor, creatorRole) && creator.Value != 0 && creator == actor.UserId);

    public static bool CanEndTournament(ActorContext actor) => IsAdministrator(actor);
}
