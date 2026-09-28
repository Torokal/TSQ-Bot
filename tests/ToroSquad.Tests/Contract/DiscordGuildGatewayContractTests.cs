using System.Reflection;
using System.Runtime.CompilerServices;
using Discord;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Discord.Guilds;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// The real <see cref="DiscordGuildGateway"/> against Discord.Net's own channel types. A guild's socket channel cache also
/// holds its active threads, and Discord.Net's <see cref="SocketThreadChannel"/> throws on
/// <c>PermissionOverwrites</c> ("This method is not supported in threads.") — production trace TS-78EFYXYQ
/// (/birthday-admin doctor, 2026-09-28): every role snapshot failed as soon as the guild had an active thread.
/// </summary>
public sealed class DiscordGuildGatewayContractTests
{
    private const ulong Channel = 7001;
    private const ulong Role = 1553890408348520468;

    [Fact]
    public void Discord_net_thread_channels_refuse_permission_overwrites()
    {
        IGuildChannel thread = Thread();
        FluentActions.Invoking(() => thread.PermissionOverwrites).Should().Throw<NotSupportedException>()
            .WithMessage("*not supported in threads*");
    }

    [Fact]
    public void Role_overwrites_skip_threads_and_keep_the_channels_own_role_overwrites()
    {
        var text = ChannelProxy.Create(Channel,
            new Overwrite(Role, PermissionTarget.Role, new OverwritePermissions(allowValue: (ulong)CorePermission.ViewChannel, denyValue: 0)),
            new Overwrite(42, PermissionTarget.User, new OverwritePermissions(allowValue: (ulong)CorePermission.SendMessages, denyValue: 0)));

        // A thread inherits its parent channel's permissions and has no overwrites of its own.
        var overwrites = DiscordGuildGateway.RoleOverwrites([text, Thread()]);

        overwrites.Should().Equal(new ChannelRoleOverwrite(new ChannelId(Channel), new RoleId(Role), CorePermission.ViewChannel, CorePermission.None));
    }

    /// <summary>Discord.Net's real thread type; its PermissionOverwrites getter throws before touching any state.</summary>
    private static SocketThreadChannel Thread() => (SocketThreadChannel)RuntimeHelpers.GetUninitializedObject(typeof(SocketThreadChannel));

    /// <summary>A plain guild channel with fixed overwrites (only Id and PermissionOverwrites are read).</summary>
    public class ChannelProxy : DispatchProxy
    {
        private ulong _id;
        private IReadOnlyCollection<Overwrite> _overwrites = [];

        public static IGuildChannel Create(ulong id, params Overwrite[] overwrites)
        {
            var channel = DispatchProxy.Create<IGuildChannel, ChannelProxy>();
            var proxy = (ChannelProxy)(object)channel;
            proxy._id = id;
            proxy._overwrites = overwrites;
            return channel;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Id" => _id,
            "get_PermissionOverwrites" => _overwrites,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
