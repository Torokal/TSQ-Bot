using Discord;
using ToroSquad.Core;
using ToroSquad.Modules.Quote.Application;

namespace ToroSquad.Modules.Quote.Commands;

/// <summary>
/// A Discord.Net message → the module's <see cref="QuoteSourceMessage"/>, the same for both ways in: a message read by id
/// (/quote) and the message a MESSAGE command was used on (Apps → Quote, from the interaction payload). Pure: no Discord
/// call happens here; the caller supplies the author's member (or null) and the mention names.
/// </summary>
public static class QuoteMessageMapper
{
    public const ushort AvatarSize = 1024;

    public static QuoteSourceMessage ToSource(IMessage message, QuoteAuthor author, QuoteMentionNames mentions, QuoteMessageShape shape) =>
        new(new MessageId(message.Id), new ChannelId(message.Channel.Id), message.Content ?? "", author, mentions, shape.Classify(), shape.ApplicationHasContentAccess);

    /// <summary>
    /// Nickname and server avatar when the author is (still) a member; the global identity otherwise (left the server,
    /// webhook). Avatar order is Discord.Net's display avatar: server avatar → account avatar → Discord's default avatar.
    /// PNG: an animated avatar becomes its first frame.
    /// </summary>
    public static QuoteAuthor Author(IUser user, IGuildUser? member) => member is not null
        ? new QuoteAuthor(new UserId(user.Id), member.DisplayName, user.Username, member.GetDisplayAvatarUrl(ImageFormat.Png, AvatarSize))
        : new QuoteAuthor(new UserId(user.Id), user.GlobalName ?? user.Username, user.Username, user.GetDisplayAvatarUrl(ImageFormat.Png, AvatarSize));

    /// <summary>
    /// The facts <see cref="QuoteMessageShape"/> needs to tell a text-less message from withheld text.
    /// <paramref name="deliveredByInteraction"/>: the message came in a MESSAGE command's payload, whose content Discord always
    /// sends ("Content of the message a message context menu command is used on", discord-api-docs gateway.mdx).
    /// </summary>
    public static QuoteMessageShape Shape(IMessage message, ulong? botUserId, bool deliveredByInteraction, bool? applicationHasContentAccess) =>
        new(
            HasText: !string.IsNullOrEmpty(message.Content),
            IsRegularMessage: message.Type is MessageType.Default or MessageType.Reply,
            HasGatedContent: message.Attachments.Count > 0 || message.Embeds.Count > 0 || message.Components.Count > 0 ||
                             (message as IUserMessage)?.Poll is not null,
            HasStickers: message.Stickers.Count > 0,
            IsForward: message.Reference?.ReferenceType.GetValueOrDefault() == MessageReferenceType.Forward,
            ContentExempt: deliveredByInteraction || (botUserId is { } me && (message.Author.Id == me || message.MentionedUserIds.Contains(me))),
            ApplicationHasContentAccess: applicationHasContentAccess);

    /// <summary>Whether reading the application's Message Content flag can change the answer (only for an empty, non-exempt text).</summary>
    public static bool NeedsContentAccessFlag(IMessage message, ulong? botUserId) =>
        string.IsNullOrEmpty(message.Content) && !Shape(message, botUserId, deliveredByInteraction: false, null).ContentExempt;
}
