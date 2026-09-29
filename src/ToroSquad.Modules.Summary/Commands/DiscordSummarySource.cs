using System.Net;
using System.Text.RegularExpressions;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Modules.Summary.Application;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Modules.Summary.Commands;

/// <summary>
/// <see cref="ISummaryDiscord"/> over Discord.Net. Channels and roles come from the gateway cache (Guilds intent — no
/// privileged gateway intents, no message events, no message cache); the messages are a few REST page reads of THIS channel
/// only, made when /ozetle runs (a thread is read without its parent). Message Content access (Developer Portal) is what
/// makes REST return other members' text. Authors' server names come from the cache or a bounded number of single-member
/// REST reads. Nothing read here is stored or logged. Created per interaction with the invoking member (roles from the
/// interaction payload).
/// </summary>
public sealed partial class DiscordSummarySource(DiscordSocketClient client, IGuildUser invoker) : ISummaryDiscord
{
    /// <summary>Discord's page size for channel history.</summary>
    public const int PageSize = 100;

    /// <summary>At most this many history pages per summary (bot-heavy channels still reach enough member messages).</summary>
    public const int MaxPages = 3;

    /// <summary>At most this many member look-ups (for server nicknames) per summary; others show their global name.</summary>
    public const int MaxMemberLookups = 20;

    private const int RequestTimeoutMs = 10_000;

    public SummaryChannel? GetChannel(GuildId guild, ChannelId channel)
    {
        var c = client.GetGuild(guild.Value)?.GetChannel(channel.Value);
        return c switch
        {
            null => null,
            // A thread (forum posts too) is read with its parent's permissions; never guess them when the parent is not cached.
            SocketThreadChannel { ParentChannel: { } parent } => new SummaryChannel(channel, true, new ChannelId(parent.Id)),
            SocketThreadChannel => new SummaryChannel(channel, false, channel),
            // Text, announcement, and the chat of voice and stage channels.
            ITextChannel => new SummaryChannel(channel, true, channel),
            _ => new SummaryChannel(channel, false, channel),
        };
    }

    public CorePermission? InvokerPermissions(ChannelId channel) =>
        client.GetGuild(invoker.GuildId)?.GetChannel(channel.Value) is { } target ? (CorePermission)invoker.GetPermissions(target).RawValue : null;

    public async Task<SummaryFetch> FetchRecentAsync(GuildId guild, ChannelId channel, int memberMessages, CancellationToken cancellationToken)
    {
        var g = client.GetGuild(guild.Value);
        if (g?.GetChannel(channel.Value) is not IMessageChannel history)
            return SummaryFetch.NoAccess;

        var read = new List<IMessage>();
        try
        {
            ulong? before = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var batch = (before is { } oldest
                    ? await history.GetMessagesAsync(oldest, Direction.Before, PageSize, CacheMode.AllowDownload, Options(cancellationToken)).FlattenAsync()
                    : await history.GetMessagesAsync(PageSize, CacheMode.AllowDownload, Options(cancellationToken)).FlattenAsync()).ToList();
                read.AddRange(batch);
                if (batch.Count < PageSize || read.Count(IsMemberMessage) >= memberMessages)
                    break;
                before = batch.Min(m => m.Id);
            }
        }
        catch (HttpException ex) when (ex.HttpCode is HttpStatusCode.Forbidden)
        {
            return SummaryFetch.NoAccess;
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or OperationCanceledException)
        {
            return SummaryFetch.Failed;
        }

        var names = await AuthorNamesAsync(g, read.Where(IsMemberMessage), cancellationToken);
        var messages = read.Select(m => new SummarySourceMessage(
            m.Id,
            m.Timestamp,
            Kind(m),
            names.TryGetValue(m.Author.Id, out var name) ? name : m.Author.GlobalName ?? m.Author.Username,
            m.Content ?? "",
            m.Attachments.Select(a => new SummaryAttachment(a.Filename, a.ContentType)).ToList(),
            m.Stickers.Select(s => s.Name).ToList(),
            IsForward: m.Reference?.ReferenceType.GetValueOrDefault() == MessageReferenceType.Forward,
            HasPoll: (m as IUserMessage)?.Poll is not null,
            HasEmbeds: m.Embeds.Count > 0)).ToList();
        return new SummaryFetch(SummaryFetchStatus.Ok, messages, Mentions(g, read, names));
    }

    public async Task<bool?> HasMessageContentAccessAsync()
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

    /// <summary>A person's own message: not a bot, not a webhook, not a system event (joins, pins, boosts, thread notices …).</summary>
    public static bool IsMemberMessage(IMessage message) => Kind(message) == SummaryAuthorKind.Member;

    public static SummaryAuthorKind Kind(IMessage message) => message switch
    {
        { Author.IsWebhook: true } or { Source: MessageSource.Webhook } => SummaryAuthorKind.Webhook,
        { Author.IsBot: true } or { Source: MessageSource.Bot } => SummaryAuthorKind.Bot,
        { Source: MessageSource.User, Type: MessageType.Default or MessageType.Reply } => SummaryAuthorKind.Member,
        _ => SummaryAuthorKind.System,
    };

    /// <summary>Server display names of the authors: the cache first, then single REST reads up to <see cref="MaxMemberLookups"/>.</summary>
    private async Task<Dictionary<ulong, string>> AuthorNamesAsync(SocketGuild guild, IEnumerable<IMessage> messages, CancellationToken cancellationToken)
    {
        var names = new Dictionary<ulong, string>();
        var lookups = 0;
        foreach (var author in messages.Select(m => m.Author).DistinctBy(a => a.Id))
        {
            IGuildUser? member = guild.GetUser(author.Id);
            if (member is null && lookups < MaxMemberLookups && !cancellationToken.IsCancellationRequested)
            {
                lookups++;
                try
                {
                    member = await client.Rest.GetGuildUserAsync(guild.Id, author.Id, Options(cancellationToken));
                }
                catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or OperationCanceledException)
                {
                    member = null; // left the server, or Discord did not answer: the global name is fine
                }
            }

            names[author.Id] = member?.DisplayName ?? author.GlobalName ?? author.Username;
        }

        return names;
    }

    /// <summary>
    /// Names for the markup in the messages, as the invoking member's client would show them. Channels: only channels the
    /// member can see (a hidden channel's name stays hidden).
    /// </summary>
    private SummaryMentionNames Mentions(SocketGuild guild, IReadOnlyList<IMessage> messages, IReadOnlyDictionary<ulong, string> authors)
    {
        var users = new Dictionary<ulong, string>();
        var roles = new Dictionary<ulong, string>();
        var channels = new Dictionary<ulong, string>();
        foreach (var message in messages)
        {
            foreach (var id in message.MentionedUserIds)
            {
                if (users.ContainsKey(id))
                    continue;
                if (authors.TryGetValue(id, out var known))
                    users[id] = known;
                else if (guild.GetUser(id) is { } cached)
                    users[id] = cached.DisplayName;
                else if (message is RestMessage rest && rest.MentionedUsers.FirstOrDefault(u => u.Id == id) is { } user)
                    users[id] = user.GlobalName ?? user.Username;
            }

            foreach (var id in message.MentionedRoleIds)
            {
                if (guild.GetRole(id) is { } role)
                    roles[id] = role.Name;
            }

            foreach (Match m in ChannelMention().Matches(message.Content ?? ""))
            {
                if (ulong.TryParse(m.Groups[1].Value, out var id) && guild.GetChannel(id) is { } c && invoker.GetPermissions(c).ViewChannel)
                    channels[id] = c.Name;
            }
        }

        return new SummaryMentionNames(users, roles, channels);
    }

    private static RequestOptions Options(CancellationToken cancellationToken) =>
        new() { CancelToken = cancellationToken, Timeout = RequestTimeoutMs };

    [GeneratedRegex(@"<#(\d{1,20})>", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelMention();
}
