using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Discord.Guilds;

/// <summary>
/// Reads roles/channels from the gateway cache (Guilds intent — no privileged intents) and performs role changes via
/// REST. Everything is re-read at call time so permission/hierarchy changes are always honoured.
/// </summary>
public sealed class DiscordGuildGateway(DiscordSocketClient client) : IGuildGateway
{
    public Task<GuildRoleSnapshot?> GetRoleSnapshotAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var g = client.GetGuild(guild.Value);
        if (g?.CurrentUser is null)
            return Task.FromResult<GuildRoleSnapshot?>(null);

        var roles = g.Roles.Select(r => new RoleInfo(
            new RoleId(r.Id), r.Name, r.Position, (CorePermission)r.Permissions.RawValue, r.IsManaged, r.IsEveryone, r.IsMentionable)).ToList();
        var overwrites = g.Channels
            .SelectMany(c => c.PermissionOverwrites
                .Where(o => o.TargetType == PermissionTarget.Role)
                .Select(o => new ChannelRoleOverwrite(new ChannelId(c.Id), new RoleId(o.TargetId), (CorePermission)o.Permissions.AllowValue, (CorePermission)o.Permissions.DenyValue)))
            .ToList();
        var botTop = g.CurrentUser.Roles.Count == 0 ? 0 : g.CurrentUser.Roles.Max(r => r.Position);
        return Task.FromResult<GuildRoleSnapshot?>(new GuildRoleSnapshot(guild, roles, botTop, (CorePermission)g.CurrentUser.GuildPermissions.RawValue, overwrites));
    }

    public Task<BotChannelAccess> GetBotChannelAccessAsync(GuildId guild, ChannelId channel, CancellationToken cancellationToken)
    {
        var g = client.GetGuild(guild.Value);
        var c = g?.GetChannel(channel.Value);
        if (g?.CurrentUser is null || c is null)
            return Task.FromResult(BotChannelAccess.Missing);
        var isText = c is ITextChannel or IThreadChannel;
        var perms = g.CurrentUser.GetPermissions(c);
        return Task.FromResult(new BotChannelAccess(true, isText, (CorePermission)perms.RawValue));
    }

    public Task<RoleOperationOutcome> AddRoleAsync(GuildId guild, UserId user, RoleId role, string auditReason, CancellationToken cancellationToken) =>
        RunAsync(() => client.Rest.AddRoleAsync(guild.Value, user.Value, role.Value, Options(auditReason, cancellationToken)));

    public Task<RoleOperationOutcome> RemoveRoleAsync(GuildId guild, UserId user, RoleId role, string auditReason, CancellationToken cancellationToken) =>
        RunAsync(() => client.Rest.RemoveRoleAsync(guild.Value, user.Value, role.Value, Options(auditReason, cancellationToken)));

    public Task<VoiceChannelAccess> GetVoiceChannelAccessAsync(GuildId guild, ChannelId channel, CancellationToken cancellationToken)
    {
        var g = client.GetGuild(guild.Value);
        var c = g?.GetChannel(channel.Value);
        if (g?.CurrentUser is null || c is null)
            return Task.FromResult(VoiceChannelAccess.Missing); // unknown here = not a channel of this guild
        return Task.FromResult(new VoiceChannelAccess(true, IsGuildVoice(c), (CorePermission)g.CurrentUser.GetPermissions(c).RawValue));
    }

    public async Task<VoiceMoveOutcome> MoveMemberToVoiceAsync(GuildId guild, UserId user, ChannelId channel, CancellationToken cancellationToken)
    {
        var g = client.GetGuild(guild.Value);
        if (g?.CurrentUser is null || g.GetChannel(channel.Value) is not IVoiceChannel voice || !IsGuildVoice(voice))
            return VoiceMoveOutcome.ChannelUnavailable;
        var bot = g.CurrentUser.GetPermissions(voice);
        if (!bot.ViewChannel || !bot.Connect || !bot.MoveMembers)
            return VoiceMoveOutcome.BotMissingPermissions;

        try
        {
            IGuildUser? member = g.GetUser(user.Value);
            member ??= await client.Rest.GetGuildUserAsync(guild.Value, user.Value, new RequestOptions { CancelToken = cancellationToken });
            if (member is null)
                return VoiceMoveOutcome.Failed;
            var own = member.GetPermissions(voice);
            if (!own.ViewChannel || !own.Connect)
                return VoiceMoveOutcome.MemberCannotConnect;
            // The bot's Move Members would bypass a user limit the member is subject to; occupancy is unknown (no voice states).
            if (voice.UserLimit is > 0 && !own.MoveMembers)
                return VoiceMoveOutcome.LimitedChannel;
            // PATCH /guilds/{guild}/members/{user} { channel_id }: only works while the member is connected to voice.
            await member.ModifyAsync(p => p.ChannelId = voice.Id,
                new RequestOptions { CancelToken = cancellationToken, AuditLogReason = "TSQ LFG: member asked to join the listing's voice channel" });
            return VoiceMoveOutcome.Moved;
        }
        catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.TargetUserNotInVoice)
        {
            return VoiceMoveOutcome.NotConnected;
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden)
        {
            return VoiceMoveOutcome.BotMissingPermissions;
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound)
        {
            return VoiceMoveOutcome.ChannelUnavailable;
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return VoiceMoveOutcome.Failed;
        }
    }

    /// <summary>Plain guild voice channels only; stage channels (a voice subtype in Discord.Net) are excluded.</summary>
    private static bool IsGuildVoice(IChannel channel) => channel is IVoiceChannel and not IStageChannel;

    private static RequestOptions Options(string reason, CancellationToken ct) => new() { AuditLogReason = reason, CancelToken = ct };

    private static async Task<RoleOperationOutcome> RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return RoleOperationOutcome.Success;
        }
        catch (HttpException ex)
        {
            return ex.HttpCode switch
            {
                // 50013 covers both "no Manage Roles" and "role above bot"; the policy pre-check tells them apart.
                HttpStatusCode.Forbidden => RoleOperationOutcome.MissingPermissions,
                HttpStatusCode.NotFound when ex.DiscordCode == DiscordErrorCode.UnknownRole => RoleOperationOutcome.UnknownRole,
                HttpStatusCode.NotFound => RoleOperationOutcome.UnknownMember,
                _ => RoleOperationOutcome.Transient,
            };
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or TaskCanceledException)
        {
            return RoleOperationOutcome.Transient;
        }
    }
}
