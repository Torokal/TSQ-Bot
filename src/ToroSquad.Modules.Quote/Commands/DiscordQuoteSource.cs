using System.Net;
using System.Text.RegularExpressions;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Modules.Quote.Application;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Modules.Quote.Commands;

/// <summary>
/// <see cref="IQuoteDiscord"/> over Discord.Net. Channels, roles and permission overwrites come from the gateway cache
/// (Guilds intent — no privileged intents); a member outside the cache and, for /quote, the message itself are single
/// REST reads. The Apps → Quote path never reads a message: <see cref="DescribeSelectedAsync"/> maps the one the
/// interaction delivered.
/// Discord.Net's own permission resolution is used (owner, Administrator, @everyone, roles, overwrites). Created per
/// interaction with the invoking member, whose roles come from the interaction payload.
/// </summary>
public sealed partial class DiscordQuoteSource(DiscordSocketClient client, IGuildUser invoker) : IQuoteDiscord
{
    private const int RequestTimeoutMs = 10_000;

    public QuoteChannel? GetChannel(GuildId guild, ChannelId channel)
    {
        var c = client.GetGuild(guild.Value)?.GetChannel(channel.Value);
        return c switch
        {
            null => null,
            // A thread is read with its parent's permissions; forum posts are threads of a forum channel.
            SocketThreadChannel thread when thread.ParentChannel is { } parent =>
                new QuoteChannel(channel, true, thread.IsPrivateThread, new ChannelId(parent.Id), parent is ITextChannel { IsNsfw: true } or IForumChannel { IsNsfw: true }),
            SocketThreadChannel => new QuoteChannel(channel, false, false, channel, false), // parent not cached: never guess its permissions
            // Text, announcement, and the chat of voice and stage channels.
            ITextChannel text => new QuoteChannel(channel, true, false, channel, text.IsNsfw),
            _ => new QuoteChannel(channel, false, false, channel, false),
        };
    }

    public async Task<CorePermission?> GetMemberPermissionsAsync(GuildId guild, UserId member, ChannelId channel, CancellationToken cancellationToken)
    {
        var g = client.GetGuild(guild.Value);
        if (g?.GetChannel(channel.Value) is not { } target)
            return null;

        var user = invoker.Id == member.Value && invoker.GuildId == guild.Value
            ? invoker
            : await MemberAsync(g, member.Value, cancellationToken);
        return user is null ? null : (CorePermission)user.GetPermissions(target).RawValue;
    }

    public async Task<QuoteFetch> GetMessageAsync(GuildId guild, ChannelId channel, MessageId message, CancellationToken cancellationToken)
    {
        var g = client.GetGuild(guild.Value);
        if (g?.GetChannel(channel.Value) is not IMessageChannel messages)
            return QuoteFetch.NotFound;

        IMessage? found;
        try
        {
            found = await messages.GetMessageAsync(message.Value, CacheMode.AllowDownload, Options(cancellationToken));
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound)
        {
            return QuoteFetch.NotFound;
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden)
        {
            return QuoteFetch.NoAccess;
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return QuoteFetch.Failed;
        }

        if (found is null || found.Channel.Id != channel.Value)
            return QuoteFetch.NotFound;

        var botId = client.CurrentUser?.Id;
        var access = QuoteMessageMapper.NeedsContentAccessFlag(found, botId) ? await ContentAccessAsync() : null;
        return new QuoteFetch(QuoteFetchStatus.Found, QuoteMessageMapper.ToSource(found, await AuthorAsync(g, found, cancellationToken),
            Mentions(g, found), QuoteMessageMapper.Shape(found, botId, deliveredByInteraction: false, access)));
    }

    /// <summary>
    /// The message a MESSAGE command (Apps → Quote) was used on, exactly as the interaction payload delivered it. Nothing
    /// about the message is read from Discord; only the author's server nickname/avatar may need a member lookup.
    /// </summary>
    public async Task<QuoteSourceMessage> DescribeSelectedAsync(GuildId guild, IMessage message, CancellationToken cancellationToken)
    {
        var g = client.GetGuild(guild.Value);
        var author = g is null ? QuoteMessageMapper.Author(message.Author, null) : await AuthorAsync(g, message, cancellationToken);
        var mentions = g is null ? QuoteMentionNames.Empty : Mentions(g, message);
        return QuoteMessageMapper.ToSource(message, author, mentions,
            QuoteMessageMapper.Shape(message, client.CurrentUser?.Id, deliveredByInteraction: true, applicationHasContentAccess: null));
    }

    private async Task<QuoteAuthor> AuthorAsync(SocketGuild guild, IMessage message, CancellationToken cancellationToken)
    {
        var user = message.Author;
        return QuoteMessageMapper.Author(user, user.IsWebhook ? null : await MemberAsync(guild, user.Id, cancellationToken));
    }

    /// <summary>
    /// Names as the invoking member's client would show them. Users: server nickname when cached, else their display name.
    /// Channels: only channels the member can see (a hidden channel's name stays hidden). Roles: from the guild cache.
    /// </summary>
    private QuoteMentionNames Mentions(SocketGuild guild, IMessage message)
    {
        IEnumerable<IUser> mentioned = message switch
        {
            RestMessage rest => rest.MentionedUsers,
            SocketMessage socket => socket.MentionedUsers,
            _ => [],
        };
        var users = new Dictionary<ulong, string>();
        foreach (var user in mentioned)
            users[user.Id] = (guild.GetUser(user.Id) as IGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username;

        var roles = new Dictionary<ulong, string>();
        foreach (var id in message.MentionedRoleIds)
        {
            if (guild.GetRole(id) is { } role)
                roles[id] = role.Name;
        }

        var channels = new Dictionary<ulong, string>();
        foreach (Match m in ChannelMention().Matches(message.Content ?? ""))
        {
            if (ulong.TryParse(m.Groups[1].Value, out var id) && guild.GetChannel(id) is { } c && invoker.GetPermissions(c).ViewChannel)
                channels[id] = c.Name;
        }

        return new QuoteMentionNames(users, roles, channels);
    }

    /// <summary>The application's Message Content flag (one cached application-info request), read only for an empty /quote text.</summary>
    private async Task<bool?> ContentAccessAsync()
    {
        try
        {
            var flags = (await client.GetApplicationInfoAsync()).Flags;
            return (flags & (ApplicationFlags.GatewayMessageContent | ApplicationFlags.GatewayMessageContentLimited)) != 0;
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException)
        {
            return null; // unknown: never guess about configuration
        }
    }

    private async Task<IGuildUser?> MemberAsync(SocketGuild guild, ulong user, CancellationToken cancellationToken)
    {
        if (guild.GetUser(user) is { } cached)
            return cached;
        try
        {
            return await client.Rest.GetGuildUserAsync(guild.Id, user, Options(cancellationToken));
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static RequestOptions Options(CancellationToken cancellationToken) =>
        new() { CancelToken = cancellationToken, Timeout = RequestTimeoutMs };

    [GeneratedRegex(@"<#(\d{1,20})>", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelMention();
}
