using System.Collections.Concurrent;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;

namespace ToroSquad.Discord.Guilds;

/// <summary>In-memory guild model for local development and tests (roles, overwrites, member role sets).</summary>
public sealed class FakeGuildGateway : IGuildGateway
{
    private readonly ConcurrentDictionary<ulong, GuildRoleSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<(ulong Guild, ulong Channel), BotChannelAccess> _channels = new();
    private readonly ConcurrentDictionary<(ulong Guild, ulong User), HashSet<ulong>> _memberRoles = new();

    public Queue<RoleOperationOutcome> ScriptedRoleOutcomes { get; } = new();

    public List<(string Op, GuildId Guild, UserId User, RoleId Role)> Operations { get; } = [];

    public void SetSnapshot(GuildRoleSnapshot snapshot) => _snapshots[snapshot.Guild.Value] = snapshot;

    public void SetChannel(GuildId guild, ChannelId channel, BotChannelAccess access) => _channels[(guild.Value, channel.Value)] = access;

    public void SetMemberRoles(GuildId guild, UserId user, params RoleId[] roles) =>
        _memberRoles[(guild.Value, user.Value)] = roles.Select(r => r.Value).ToHashSet();

    public bool MemberHasRole(GuildId guild, UserId user, RoleId role) =>
        _memberRoles.TryGetValue((guild.Value, user.Value), out var set) && set.Contains(role.Value);

    public Task<GuildRoleSnapshot?> GetRoleSnapshotAsync(GuildId guild, CancellationToken cancellationToken) =>
        Task.FromResult(_snapshots.TryGetValue(guild.Value, out var s) ? s : null);

    public Task<BotChannelAccess> GetBotChannelAccessAsync(GuildId guild, ChannelId channel, CancellationToken cancellationToken) =>
        Task.FromResult(_channels.TryGetValue((guild.Value, channel.Value), out var a) ? a : BotChannelAccess.Missing);

    public Task<RoleOperationOutcome> AddRoleAsync(GuildId guild, UserId user, RoleId role, string auditReason, CancellationToken cancellationToken)
    {
        Operations.Add(("add", guild, user, role));
        if (ScriptedRoleOutcomes.TryDequeue(out var scripted) && scripted != RoleOperationOutcome.Success)
            return Task.FromResult(scripted);
        _memberRoles.GetOrAdd((guild.Value, user.Value), _ => []).Add(role.Value);
        return Task.FromResult(RoleOperationOutcome.Success);
    }

    public Task<RoleOperationOutcome> RemoveRoleAsync(GuildId guild, UserId user, RoleId role, string auditReason, CancellationToken cancellationToken)
    {
        Operations.Add(("remove", guild, user, role));
        if (ScriptedRoleOutcomes.TryDequeue(out var scripted) && scripted != RoleOperationOutcome.Success)
            return Task.FromResult(scripted);
        if (_memberRoles.TryGetValue((guild.Value, user.Value), out var set))
            set.Remove(role.Value);
        return Task.FromResult(RoleOperationOutcome.Success);
    }

    /// <summary>A typical, well-configured demo guild used by Start-Dev and tests.</summary>
    public static GuildRoleSnapshot DemoSnapshot(GuildId guild, params RoleInfo[] extraRoles)
    {
        var everyone = new RoleInfo(new RoleId(guild.Value), "@everyone", 0,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.ReadMessageHistory | GuildPermission.UseApplicationCommands,
            false, true, false);
        var bot = new RoleInfo(new RoleId(guild.Value + 1), "ToroSquad", 10,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory | GuildPermission.ManageRoles,
            true, false, false);
        return new GuildRoleSnapshot(guild, [everyone, bot, .. extraRoles], 10,
            everyone.Permissions | bot.Permissions, []);
    }
}
