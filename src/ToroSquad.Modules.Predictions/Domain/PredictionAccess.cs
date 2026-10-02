using ToroSquad.Core;
using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Predictions.Domain;

/// <summary>
/// Who may do what, decided from the <see cref="ActorContext"/> of THIS interaction (Discord sends the member's current roles
/// and permissions with every command, click and form submit, so a role removed after the form opened is seen at once):
/// <list type="bullet">
/// <item>create: the creator role — Administrator alone does not grant it;</item>
/// <item>settle ANY prediction (manual or automatic, whoever created it): the creator role, Administrator or the server
/// owner;</item>
/// <item>lock and cancel a prediction: Administrator or the server owner for every prediction; its own creator only while
/// still holding the creator role; an AUTOMATIC prediction has no creator — only Administrator or the server owner;</item>
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

    /// <summary>✅ Sonuçlandır on any prediction: the creator role, Administrator or the server owner (checked on every step).</summary>
    public static bool CanSettle(ActorContext actor, RoleId creatorRole) => IsAdministrator(actor) || HasCreatorRole(actor, creatorRole);

    /// <summary>🔒 Kilitle and ↩️ İptal / İade (unchanged): Administrator/owner, or a manual prediction's own creator holding the role.</summary>
    public static bool CanManage(ActorContext actor, RoleId creatorRole, UserId creator, PredictionOrigin origin = PredictionOrigin.Manual) =>
        IsAdministrator(actor) ||
        (origin == PredictionOrigin.Manual && HasCreatorRole(actor, creatorRole) && creator.Value != 0 && creator == actor.UserId);

    public static bool CanEndTournament(ActorContext actor) => IsAdministrator(actor);
}
