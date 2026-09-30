using System.Globalization;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Quote.Application;

/// <summary>A channel of the invoking guild as the bot's cache sees it.</summary>
/// <param name="IsMessageChannel">Holds normal messages (text, announcement, voice/stage chat, thread).</param>
/// <param name="PermissionChannel">Where access is decided: the parent channel for a thread, the channel itself otherwise.</param>
public sealed record QuoteChannel(ChannelId Id, bool IsMessageChannel, bool IsPrivateThread, ChannelId PermissionChannel, bool IsNsfw);

/// <summary>Who wrote the message, as shown in this guild: nickname/display name, @username, the Discord CDN avatar url.</summary>
public sealed record QuoteAuthor(UserId Id, string DisplayName, string Username, string? AvatarUrl);

/// <summary>
/// The fetched message. <paramref name="TextAvailability"/> tells an empty text apart (see <see cref="QuoteMessageShape"/>);
/// <paramref name="ApplicationHasContentAccess"/> is the application's Message Content flag, for the log only (null: not read).
/// </summary>
public sealed record QuoteSourceMessage(
    MessageId Id,
    ChannelId Channel,
    string RawContent,
    QuoteAuthor Author,
    QuoteMentionNames Mentions,
    QuoteTextAvailability TextAvailability,
    bool? ApplicationHasContentAccess = null);

public enum QuoteFetchStatus
{
    Found = 0,
    NotFound = 1,
    NoAccess = 2,
    Failed = 3,
}

public sealed record QuoteFetch(QuoteFetchStatus Status, QuoteSourceMessage? Message = null)
{
    public static QuoteFetch NotFound { get; } = new(QuoteFetchStatus.NotFound);
    public static QuoteFetch NoAccess { get; } = new(QuoteFetchStatus.NoAccess);
    public static QuoteFetch Failed { get; } = new(QuoteFetchStatus.Failed);
}

/// <summary>
/// The Discord reads /quote needs beyond <see cref="IGuildGateway"/>: one channel from the cache, one member's effective
/// permissions in a channel, one message by id. Implemented over Discord.Net in Commands; faked in tests.
/// </summary>
public interface IQuoteDiscord
{
    QuoteChannel? GetChannel(GuildId guild, ChannelId channel);

    /// <summary>The member's effective permissions in <paramref name="channel"/> (roles + overwrites); null if they are not a member.</summary>
    Task<GuildPermission?> GetMemberPermissionsAsync(GuildId guild, UserId member, ChannelId channel, CancellationToken cancellationToken);

    /// <summary>A single read of one message by id (never a channel scan or search).</summary>
    Task<QuoteFetch> GetMessageAsync(GuildId guild, ChannelId channel, MessageId message, CancellationToken cancellationToken);
}

public enum QuoteFailure
{
    None = 0,

    /// <summary>Neither a message id nor a Discord message link.</summary>
    InvalidReference = 1,

    /// <summary>A link to another server or to a DM.</summary>
    OtherGuild = 2,

    /// <summary>Missing, deleted, not a message channel, no access for the member or the bot — deliberately one answer.</summary>
    NotFound = 3,

    /// <summary>Found and allowed, but there is no text to quote.</summary>
    NoText = 4,

    /// <summary>Found and allowed, but Discord withheld the text from the bot (Message Content intent not enabled).</summary>
    ContentUnavailable = 5,

    /// <summary>An age-restricted channel's message into a channel that is not age-restricted.</summary>
    AgeRestricted = 6,

    /// <summary>The bot may not attach files in the channel the command ran in (checked before anything is read or drawn).</summary>
    BotCannotAttach = 7,
}

public sealed record QuoteRequest(GuildId Guild, UserId Member, ChannelId InvokedIn, string MessageInput, ChannelId? ChannelOption);

/// <summary>Apps → Quote: the message the MESSAGE command was used on (from the interaction payload) and where it was used.</summary>
public sealed record QuoteSelection(GuildId Guild, UserId Member, ChannelId InvokedIn, QuoteSourceMessage Message);

public sealed record QuoteResolution(QuoteFailure Failure, QuoteSourceMessage? Message = null, string Text = "")
{
    public bool Succeeded => Failure == QuoteFailure.None;

    public string MessageKey => Failure switch
    {
        QuoteFailure.InvalidReference => "quote.invalid_reference",
        QuoteFailure.OtherGuild => "quote.other_guild",
        QuoteFailure.NoText => "quote.no_text",
        QuoteFailure.ContentUnavailable => "quote.content_unavailable",
        QuoteFailure.AgeRestricted => "quote.age_restricted",
        QuoteFailure.BotCannotAttach => "quote.bot_cannot_attach",
        _ => "quote.not_found",
    };
}

/// <summary>
/// Everything between "the user asked for a quote" and "this is the text and author to draw", with every check done on the
/// server. Two ways in, one tail (text normalization, empty-text answers, logs):
/// <list type="bullet">
/// <item><see cref="ResolveAsync"/> (/quote): the reference parses; the message belongs to THIS guild; the channel is a
/// message channel of this guild; the member may see it and read its history (their current roles and the channel's
/// overwrites, never what the Discord client showed them); the bot may read it too; only then one message is fetched by
/// id. Every "no" after the guild check is the same answer, so /quote does not reveal whether a hidden channel or message
/// exists.</item>
/// <item><see cref="ResolveSelectedAsync"/> (Apps → Quote): the interaction already carries the message, in the channel
/// the command was used in, so nothing is fetched and source = destination: the cross-channel rules (private threads, age
/// restriction) have nothing to protect; the member's access and the bot's right to post the file are still checked.</item>
/// </list>
/// </summary>
public sealed partial class QuoteMessageResolver(IGuildGateway guilds, ILogger<QuoteMessageResolver> logger)
{
    /// <summary>What the member and the bot both need in the source channel.</summary>
    public const GuildPermission RequiredToRead = GuildPermission.ViewChannel | GuildPermission.ReadMessageHistory;

    /// <summary>
    /// What the bot needs where the command ran: the card is an interaction follow-up carrying quote.png. Follow-ups are
    /// sent through the interaction webhook, so Send Messages is not required; Attach Files is (TSQ policy: never post a
    /// file where the bot may not attach one).
    /// </summary>
    public const GuildPermission RequiredToPost = GuildPermission.ViewChannel | GuildPermission.AttachFiles;

    public async Task<QuoteResolution> ResolveAsync(IQuoteDiscord discord, QuoteRequest request, CultureInfo culture, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        ChannelId channelId;
        MessageId messageId;
        switch (QuoteReference.Parse(request.MessageInput))
        {
            case QuoteReference.Link link when link.Guild != request.Guild:
                return new(QuoteFailure.OtherGuild);
            case QuoteReference.Link link:
                (channelId, messageId) = (link.Channel, link.Message); // the link names its channel; the channel option is not needed
                break;
            case QuoteReference.MessageOnly bare:
                (channelId, messageId) = (request.ChannelOption ?? request.InvokedIn, bare.Message);
                break;
            case QuoteReference.DirectMessageLink:
                return new(QuoteFailure.OtherGuild);
            default:
                return new(QuoteFailure.InvalidReference);
        }

        // Where the card goes, first: no source channel is looked at, no message read, nothing drawn if it cannot be posted.
        var destination = discord.GetChannel(request.Guild, request.InvokedIn);
        if (!await CanPostAsync(request.Guild, request.InvokedIn, destination, cancellationToken))
            return new(QuoteFailure.BotCannotAttach);

        // A channel id from a link or an option is only a claim: it must be a message channel the cache knows in THIS guild.
        if (discord.GetChannel(request.Guild, channelId) is not { IsMessageChannel: true } channel || channel.IsPrivateThread)
            return Denied("channel is not a readable message channel of this guild", request.Guild, request.Member, channelId);

        var member = await discord.GetMemberPermissionsAsync(request.Guild, request.Member, channel.PermissionChannel, cancellationToken);
        if (member is not { } memberPermissions || !memberPermissions.Grants(RequiredToRead))
            return Denied("member cannot view the channel or read its history", request.Guild, request.Member, channelId);

        var bot = await guilds.GetBotChannelAccessAsync(request.Guild, channel.PermissionChannel, cancellationToken);
        if (!bot.Exists || !bot.Permissions.Grants(RequiredToRead))
            return Denied("bot cannot view the channel or read its history", request.Guild, request.Member, channelId);

        // Age-restricted text only stays in age-restricted channels, whoever may read it.
        if (channel.IsNsfw && destination is not { IsNsfw: true })
            return new(QuoteFailure.AgeRestricted);

        var fetch = await discord.GetMessageAsync(request.Guild, channel.Id, messageId, cancellationToken);
        if (fetch is not { Status: QuoteFetchStatus.Found, Message: { } message })
            return Denied("message fetch returned " + fetch.Status, request.Guild, request.Member, channelId);

        return Finish(request.Guild, message, "slash", culture, zone);
    }

    /// <summary>
    /// Apps → Quote. The message comes from the interaction payload — <see cref="IQuoteDiscord.GetMessageAsync"/> is never
    /// called here — and was selected in the channel the command was used in, which is also where the card goes.
    /// </summary>
    public async Task<QuoteResolution> ResolveSelectedAsync(IQuoteDiscord discord, QuoteSelection selection, CultureInfo culture, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        // The payload's message must be one of this very channel (a context-menu interaction always is; anything else is refused).
        if (selection.Message.Channel != selection.InvokedIn)
            return Denied("the selected message is not in the channel the command was used in", selection.Guild, selection.Member, selection.InvokedIn);

        if (discord.GetChannel(selection.Guild, selection.InvokedIn) is not { IsMessageChannel: true } channel)
            return Denied("channel is not a message channel of this guild", selection.Guild, selection.Member, selection.InvokedIn);

        if (!await CanPostAsync(selection.Guild, selection.InvokedIn, channel, cancellationToken))
            return new(QuoteFailure.BotCannotAttach);

        // Still checked server-side although the member just clicked the message: their current access, not the client's view.
        var member = await discord.GetMemberPermissionsAsync(selection.Guild, selection.Member, channel.PermissionChannel, cancellationToken);
        if (member is not { } memberPermissions || !memberPermissions.Grants(RequiredToRead))
            return Denied("member cannot view the channel or read its history", selection.Guild, selection.Member, selection.InvokedIn);

        // No private-thread or age-restriction refusal: the card stays in the channel the message is in, seen by the same people.
        return Finish(selection.Guild, selection.Message, "apps", culture, zone);
    }

    /// <summary>The bot may view the channel the command ran in and attach the card there (a thread: its parent's permissions).</summary>
    private async Task<bool> CanPostAsync(GuildId guild, ChannelId invokedIn, QuoteChannel? destination, CancellationToken cancellationToken)
    {
        var post = await guilds.GetBotChannelAccessAsync(guild, destination?.PermissionChannel ?? invokedIn, cancellationToken);
        if (post.Exists && post.Permissions.Grants(RequiredToPost))
            return true;
        LogCannotPost(logger, guild.Value, invokedIn.Value);
        return false;
    }

    /// <summary>Both ways end here: the readable text, or why there is none.</summary>
    private QuoteResolution Finish(GuildId guild, QuoteSourceMessage message, string via, CultureInfo culture, TimeZoneInfo zone)
    {
        var text = QuoteText.Normalize(message.RawContent, message.Mentions, culture, zone);
        if (text.Length > 0)
        {
            LogResolved(logger, guild.Value, message.Channel.Value, message.Id.Value, via);
            return new(QuoteFailure.None, message, text);
        }

        if (message.TextAvailability == QuoteTextAvailability.ProbablyWithheld)
        {
            LogContentWithheld(logger, guild.Value, message.Channel.Value, message.Id.Value,
                message.ApplicationHasContentAccess?.ToString() ?? "unknown");
            return new(QuoteFailure.ContentUnavailable, message);
        }

        LogNoText(logger, guild.Value, message.Channel.Value, message.Id.Value);
        return new(QuoteFailure.NoText, message);
    }

    private QuoteResolution Denied(string reason, GuildId guild, UserId member, ChannelId channel)
    {
        LogDenied(logger, reason, guild.Value, channel.Value, member.Value);
        return new(QuoteFailure.NotFound);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Quote refused: {Reason} guild={Guild} channel={Channel} user={User}")]
    private static partial void LogDenied(ILogger logger, string reason, ulong guild, ulong channel, ulong user);

    // Logs carry ids and outcomes only — never message text, names or avatar urls.
    [LoggerMessage(Level = LogLevel.Information, Message = "Quote refused: the bot may not attach files where the command ran guild={Guild} channel={Channel}")]
    private static partial void LogCannotPost(ILogger logger, ulong guild, ulong channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Quote resolved guild={Guild} channel={Channel} message={Message} via={Via}")]
    private static partial void LogResolved(ILogger logger, ulong guild, ulong channel, ulong message, string via);

    [LoggerMessage(Level = LogLevel.Information, Message = "Quote refused: the message has no text guild={Guild} channel={Channel} message={Message}")]
    private static partial void LogNoText(ILogger logger, ulong guild, ulong channel, ulong message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Quote: Discord returned a normal message with no content at all guild={Guild} channel={Channel} " +
        "message={Message} (application Message Content flag: {ContentAccess}). Discord withholds content from apps without Message Content " +
        "access (Developer Portal → Bot → Privileged Gateway Intents).")]
    private static partial void LogContentWithheld(ILogger logger, ulong guild, ulong channel, ulong message, string contentAccess);
}
