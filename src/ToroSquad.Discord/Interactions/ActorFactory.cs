using Discord;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Core.Security;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Discord.Interactions;

/// <summary>
/// Builds the <see cref="ActorContext"/> from the interaction itself (guild id, invoking member, their current
/// permissions and roles). Nothing here comes from command options, so users cannot spoof another guild or user.
/// </summary>
public static class ActorFactory
{
    public static ActorContext? From(IInteractionContext context)
    {
        if (context.Guild is null || context.User is not IGuildUser member)
            return null;

        var roleIds = member.RoleIds.Select(r => new RoleId(r)).ToArray();
        var highest = member is SocketGuildUser socketMember && socketMember.Roles.Count > 0
            ? socketMember.Roles.Max(r => r.Position)
            : 0;
        return new ActorContext(
            new GuildId(context.Guild.Id),
            new UserId(member.Id),
            (CorePermission)member.GuildPermissions.RawValue,
            roleIds,
            context.Guild.OwnerId == member.Id,
            highest);
    }
}
