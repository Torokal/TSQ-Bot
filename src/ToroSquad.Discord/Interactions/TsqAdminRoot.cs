using Discord;
using Discord.Interactions;
using ToroSquad.Core;

namespace ToroSquad.Discord.Interactions;

/// <summary>
/// The single <c>/tsq-admin</c> root that holds every module's admin operations as one subcommand group each
/// (<c>/tsq-admin news doctor</c>, <c>/tsq-admin esports roles-map</c>, …). The root itself is core's; each group stays owned,
/// gated and authorized by its module.
/// <para>
/// A module contributes by deriving one class from this root and nesting ONE <c>[Group("&lt;module&gt;")]</c> class with its
/// <c>[ToroModule]</c> inside it (Discord.Net builds command paths from nested classes). The group name is the module's
/// former <c>/&lt;module&gt;-admin</c> prefix. Discord allows only two levels under a command, so a group cannot hold another
/// group: an operation that used to sit in a sub-group is a plain subcommand named <c>&lt;subgroup&gt;-&lt;operation&gt;</c>,
/// declared in a nested class WITHOUT <c>[Group]</c> (it keeps its own dependencies).
/// </para>
/// <para>
/// The group name, description, permissions and contexts below are inherited by every contributor, so all of them describe
/// the same root; <see cref="Commands.Manifest.CommandManifestBuilder"/> merges the contributors into one command and refuses
/// (blocking any sync) a contributor that disagrees, adds commands to the root itself, or reuses another module's group name.
/// The running bot dispatches with the same Discord.Net command map, so manifest and dispatch share one path per operation.
/// </para>
/// <para>
/// default_member_permissions (Manage Server) only hides the root in Discord's UI; Discord cannot set it per group. Every
/// operation still authorizes server-side (<c>Authorize.Require</c>), including the stricter Administrator checks.
/// </para>
/// </summary>
[Group(Name, Description)]
[DefaultMemberPermissions(GuildPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public abstract class TsqAdminRoot : InteractionModuleBase<SocketInteractionContext>
{
    public const string Name = "tsq-admin";
    public const string Description = $"{ProductInfo.ProductName} module administration (admins)";

    /// <summary>The <see cref="ToroSquad.Core.Modules.ModuleDescriptor.AdminCommands"/> entry of a module's group: "tsq-admin news".</summary>
    public static string Group(string group) => Name + " " + group;
}
