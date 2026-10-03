using Discord;
using ToroSquad.Modules.Summary.Application;
using ToroSquad.Modules.Summary.Commands;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The Discord-side classification that decides what can reach a summary: only a person's own default or reply message is a
/// member message; bots (TSQ Bot's own summaries included), webhooks and every system event are not.
/// </summary>
public sealed class SummaryDiscordFilterTests
{
    private static IMessage Message(MessageSource source, MessageType type, bool bot = false, bool webhook = false) =>
        InterfaceFake.Create<IMessage>(new()
        {
            ["Source"] = source,
            ["Type"] = type,
            ["Author"] = InterfaceFake.Create<IUser>(new() { ["IsBot"] = bot, ["IsWebhook"] = webhook }),
        });

    private static IMessage Referencing(MessageType type, MessageReference? reference) =>
        InterfaceFake.Create<IMessage>(new() { ["Type"] = type, ["Reference"] = reference });

    [Fact]
    public void A_reply_link_is_a_real_reply_inside_the_same_channel_only()
    {
        const ulong channel = 1200000000000000001, other = 1200000000000000002;

        DiscordSummarySource.ReplyTargetId(Referencing(MessageType.Reply, new MessageReference(41, channel)), channel).Should().Be(41UL);
        DiscordSummarySource.ReplyTargetId(Referencing(MessageType.Reply, new MessageReference(41)), channel).Should().Be(41UL, "no channel given: this channel");
        DiscordSummarySource.ReplyTargetId(Referencing(MessageType.Reply, new MessageReference(41, other)), channel).Should().BeNull("another channel's message is not context here");
        DiscordSummarySource.ReplyTargetId(Referencing(MessageType.Default, new MessageReference(41, channel)), channel).Should().BeNull("not a reply");
        DiscordSummarySource.ReplyTargetId(Referencing(MessageType.Reply, null), channel).Should().BeNull();
        DiscordSummarySource.ReplyTargetId(Referencing(MessageType.Reply, new MessageReference(41, channel, referenceType: MessageReferenceType.Forward)), channel)
            .Should().BeNull("a forward is not a reply");
    }

    [Theory]
    [InlineData(MessageSource.User, MessageType.Default, false, false, SummaryAuthorKind.Member)]
    [InlineData(MessageSource.User, MessageType.Reply, false, false, SummaryAuthorKind.Member)]
    [InlineData(MessageSource.Bot, MessageType.Default, true, false, SummaryAuthorKind.Bot)]
    [InlineData(MessageSource.Bot, MessageType.Reply, true, false, SummaryAuthorKind.Bot)]
    [InlineData(MessageSource.User, MessageType.Default, true, false, SummaryAuthorKind.Bot)]
    [InlineData(MessageSource.Webhook, MessageType.Default, true, true, SummaryAuthorKind.Webhook)]
    [InlineData(MessageSource.User, MessageType.Default, false, true, SummaryAuthorKind.Webhook)]
    [InlineData(MessageSource.System, MessageType.Default, false, false, SummaryAuthorKind.System)]
    [InlineData(MessageSource.User, MessageType.GuildMemberJoin, false, false, SummaryAuthorKind.System)]
    [InlineData(MessageSource.User, MessageType.ChannelPinnedMessage, false, false, SummaryAuthorKind.System)]
    [InlineData(MessageSource.User, MessageType.ThreadCreated, false, false, SummaryAuthorKind.System)]
    [InlineData(MessageSource.User, MessageType.ApplicationCommand, false, false, SummaryAuthorKind.System)]
    public void Only_a_persons_own_messages_are_member_messages(MessageSource source, MessageType type, bool bot, bool webhook, SummaryAuthorKind expected)
    {
        var message = Message(source, type, bot, webhook);

        DiscordSummarySource.Kind(message).Should().Be(expected);
        DiscordSummarySource.IsMemberMessage(message).Should().Be(expected == SummaryAuthorKind.Member);
    }
}
