using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Modules.Quote.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Quote message resolution: which channel is read, and every server-side check before one message is fetched — same
/// guild, a message channel of this guild, the member AND the bot may view it and read its history. Every refusal after
/// the guild check is the same answer, and nothing is fetched unless all checks pass.
/// </summary>
public sealed class QuoteResolverTests
{
    private static readonly GuildId Guild = new(689812743242514448);
    private static readonly GuildId OtherGuild = new(618763184815472651);
    private static readonly UserId Member = new(500000000000000001);
    private static readonly UserId Author = new(500000000000000002);
    private static readonly ChannelId Here = new(1200000000000000001);
    private static readonly ChannelId Elsewhere = new(1200000000000000002);
    private static readonly ChannelId Thread = new(1200000000000000003);
    private static readonly ChannelId Private = new(1200000000000000004);
    private static readonly ChannelId Nsfw = new(1200000000000000005);
    private static readonly ChannelId Forum = new(1200000000000000006);
    private static readonly MessageId Message = new(1300000000000000002);

    private const GuildPermission Read = GuildPermission.ViewChannel | GuildPermission.ReadMessageHistory;

    private sealed class FakeQuoteDiscord : IQuoteDiscord
    {
        public Dictionary<ChannelId, QuoteChannel> Channels { get; } = [];
        public Dictionary<(UserId, ChannelId), GuildPermission> Permissions { get; } = [];
        public Dictionary<(ChannelId, MessageId), QuoteFetch> Messages { get; } = [];
        public List<(GuildId Guild, ChannelId Channel, MessageId Message)> Fetches { get; } = [];

        public QuoteChannel? GetChannel(GuildId guild, ChannelId channel) =>
            guild == Guild && Channels.TryGetValue(channel, out var c) ? c : null;

        public Task<GuildPermission?> GetMemberPermissionsAsync(GuildId guild, UserId member, ChannelId channel, CancellationToken cancellationToken) =>
            Task.FromResult<GuildPermission?>(Permissions.TryGetValue((member, channel), out var p) ? p : null);

        public Task<QuoteFetch> GetMessageAsync(GuildId guild, ChannelId channel, MessageId message, CancellationToken cancellationToken)
        {
            Fetches.Add((guild, channel, message));
            return Task.FromResult(Messages.TryGetValue((channel, message), out var m) ? m : QuoteFetch.NotFound);
        }

        public void Allow(ChannelId channel, GuildPermission permissions = Read) => Permissions[(Member, channel)] = permissions;

        public void Put(ChannelId channel, string content, bool withheld = false, QuoteMentionNames? mentions = null) =>
            Messages[(channel, Message)] = new QuoteFetch(QuoteFetchStatus.Found, new QuoteSourceMessage(Message, channel, content,
                new QuoteAuthor(Author, "Pizza Venk", "pizzavenk", "https://cdn.discordapp.com/avatars/500000000000000002/abc.png?size=1024"),
                mentions ?? QuoteMentionNames.Empty, withheld));
    }

    private readonly FakeQuoteDiscord _discord = new();
    private readonly FakeGuildGateway _gateway = new();
    private readonly QuoteMessageResolver _resolver;

    public QuoteResolverTests()
    {
        _resolver = new QuoteMessageResolver(_gateway, NullLogger<QuoteMessageResolver>.Instance);
        foreach (var channel in new[] { Here, Elsewhere, Nsfw })
        {
            _discord.Channels[channel] = new QuoteChannel(channel, true, false, channel, channel == Nsfw);
            BotReads(channel);
        }

        _discord.Channels[Thread] = new QuoteChannel(Thread, true, false, Elsewhere, false); // a thread in #elsewhere
        _discord.Channels[Private] = new QuoteChannel(Private, true, true, Elsewhere, false); // a private thread in #elsewhere
        _discord.Channels[Forum] = new QuoteChannel(Forum, false, false, Forum, false); // a forum itself holds no messages
        BotReads(Forum);
    }

    private void BotReads(ChannelId channel, GuildPermission permissions = Read | GuildPermission.SendMessages) =>
        _gateway.SetChannel(Guild, channel, new BotChannelAccess(true, true, permissions));

    private Task<QuoteResolution> Resolve(string input, ChannelId? option = null, ChannelId? invokedIn = null) =>
        _resolver.ResolveAsync(_discord, new QuoteRequest(Guild, Member, invokedIn ?? Here, input, option),
            CultureInfo.GetCultureInfo("tr-TR"), TimeZoneInfo.Utc, TestContext.Current.CancellationToken);

    private static string Link(GuildId guild, ChannelId channel) => $"https://discord.com/channels/{guild}/{channel}/{Message}";

    // ---- channel resolution ----

    [Fact]
    public async Task A_bare_id_is_read_from_the_channel_the_command_ran_in()
    {
        _discord.Allow(Here);
        _discord.Put(Here, "aminiza koim");
        var result = await Resolve(Message.ToString());
        result.Succeeded.Should().BeTrue();
        result.Text.Should().Be("aminiza koim");
        result.Message!.Author.Should().Be(new QuoteAuthor(Author, "Pizza Venk", "pizzavenk", "https://cdn.discordapp.com/avatars/500000000000000002/abc.png?size=1024"));
        _discord.Fetches.Should().Equal((Guild, Here, Message));
    }

    [Fact]
    public async Task A_bare_id_with_the_channel_option_is_read_from_that_channel()
    {
        _discord.Allow(Elsewhere);
        _discord.Put(Elsewhere, "başka kanaldan");
        (await Resolve(Message.ToString(), option: Elsewhere)).Text.Should().Be("başka kanaldan");
        _discord.Fetches.Should().Equal((Guild, Elsewhere, Message));
    }

    [Fact]
    public async Task A_link_is_read_from_its_own_channel_and_needs_no_channel_option()
    {
        _discord.Allow(Elsewhere);
        _discord.Put(Elsewhere, "bağlantıdan");
        (await Resolve(Link(Guild, Elsewhere))).Text.Should().Be("bağlantıdan");
        (await Resolve(Link(Guild, Elsewhere), option: Here)).Text.Should().Be("bağlantıdan", "the link names the channel; the option is ignored");
        _discord.Fetches.Should().OnlyContain(f => f.Channel == Elsewhere);
    }

    [Fact]
    public async Task A_thread_message_is_checked_against_the_parent_channel()
    {
        _discord.Put(Thread, "thread mesajı");
        (await Resolve(Link(Guild, Thread))).Failure.Should().Be(QuoteFailure.NotFound, "no access to the parent yet");
        _discord.Allow(Elsewhere);
        (await Resolve(Link(Guild, Thread))).Text.Should().Be("thread mesajı");
        _discord.Fetches.Should().ContainSingle().Which.Channel.Should().Be(Thread);
    }

    // ---- refusals ----

    [Theory]
    [InlineData("")]
    [InlineData("bu bir id değil")]
    [InlineData("https://example.com/channels/1/2/3")]
    public async Task Garbage_is_an_invalid_reference(string input)
    {
        (await Resolve(input)).Failure.Should().Be(QuoteFailure.InvalidReference);
        _discord.Fetches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_link_to_another_server_or_a_dm_is_refused_before_anything_is_read()
    {
        _discord.Allow(Here);
        _discord.Put(Here, "x");
        (await Resolve(Link(OtherGuild, Here))).Failure.Should().Be(QuoteFailure.OtherGuild);
        (await Resolve($"https://discord.com/channels/@me/{Here}/{Message}")).Failure.Should().Be(QuoteFailure.OtherGuild);
        _discord.Fetches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(GuildPermission.None)]
    [InlineData(GuildPermission.ReadMessageHistory)] // history without View Channel
    [InlineData(GuildPermission.ViewChannel)] // can see the channel but not its history
    [InlineData(GuildPermission.ViewChannel | GuildPermission.SendMessages)]
    public async Task A_member_who_cannot_view_the_channel_and_read_its_history_gets_not_found(GuildPermission permissions)
    {
        _discord.Allow(Elsewhere, permissions);
        _discord.Put(Elsewhere, "gizli kanal");
        var result = await Resolve(Link(Guild, Elsewhere));
        result.Failure.Should().Be(QuoteFailure.NotFound);
        result.MessageKey.Should().Be("quote.not_found");
        _discord.Fetches.Should().BeEmpty("nothing is read for a member without access");
    }

    [Fact]
    public async Task Administrator_grants_access_like_in_discord()
    {
        _discord.Allow(Elsewhere, GuildPermission.Administrator);
        _discord.Put(Elsewhere, "yönetici");
        (await Resolve(Link(Guild, Elsewhere))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_non_member_gets_not_found()
    {
        _discord.Put(Here, "x"); // no permissions entry = not a member of the guild
        (await Resolve(Message.ToString())).Failure.Should().Be(QuoteFailure.NotFound);
        _discord.Fetches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, Read)]
    [InlineData(true, GuildPermission.ViewChannel | GuildPermission.SendMessages)]
    [InlineData(true, GuildPermission.None)]
    public async Task When_the_bot_cannot_read_the_channel_the_member_gets_the_same_answer(bool exists, GuildPermission bot)
    {
        _gateway.SetChannel(Guild, Elsewhere, new BotChannelAccess(exists, true, bot));
        _discord.Allow(Elsewhere);
        _discord.Put(Elsewhere, "x");
        (await Resolve(Link(Guild, Elsewhere))).Failure.Should().Be(QuoteFailure.NotFound);
        _discord.Fetches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(QuoteFetchStatus.NotFound)]
    [InlineData(QuoteFetchStatus.NoAccess)]
    [InlineData(QuoteFetchStatus.Failed)]
    public async Task A_message_discord_does_not_return_is_not_found(QuoteFetchStatus status)
    {
        _discord.Allow(Here);
        _discord.Messages[(Here, Message)] = new QuoteFetch(status);
        (await Resolve(Message.ToString())).Failure.Should().Be(QuoteFailure.NotFound);
    }

    [Fact]
    public async Task Unknown_channels_non_message_channels_and_private_threads_are_not_found()
    {
        _discord.Allow(Elsewhere);
        _discord.Allow(Forum);
        (await Resolve(Link(Guild, new ChannelId(1299999999999999999)))).Failure.Should().Be(QuoteFailure.NotFound);
        (await Resolve(Message.ToString(), option: Forum)).Failure.Should().Be(QuoteFailure.NotFound);
        _discord.Put(Private, "özel thread");
        (await Resolve(Link(Guild, Private))).Failure.Should().Be(QuoteFailure.NotFound, "private thread membership is not checked, so it is never quoted");
        _discord.Fetches.Should().BeEmpty();
    }

    [Fact]
    public async Task Age_restricted_text_stays_in_age_restricted_channels()
    {
        _discord.Allow(Nsfw);
        _discord.Put(Nsfw, "+18");
        var outside = await Resolve(Link(Guild, Nsfw), invokedIn: Here);
        outside.Failure.Should().Be(QuoteFailure.AgeRestricted);
        _discord.Fetches.Should().BeEmpty();
        (await Resolve(Link(Guild, Nsfw), invokedIn: Nsfw)).Succeeded.Should().BeTrue();
    }

    // ---- content ----

    [Fact]
    public async Task A_message_without_text_cannot_be_quoted()
    {
        _discord.Allow(Here);
        _discord.Put(Here, "   \n  ");
        var result = await Resolve(Message.ToString());
        result.Failure.Should().Be(QuoteFailure.NoText);
        result.MessageKey.Should().Be("quote.no_text");
    }

    [Fact]
    public async Task Text_withheld_by_discord_is_reported_as_such()
    {
        _discord.Allow(Here);
        _discord.Put(Here, "", withheld: true);
        var result = await Resolve(Message.ToString());
        result.Failure.Should().Be(QuoteFailure.ContentUnavailable);
        result.MessageKey.Should().Be("quote.content_unavailable");
    }

    [Fact]
    public async Task Mentions_markdown_newlines_and_turkish_text_reach_the_card_readable()
    {
        _discord.Allow(Here);
        _discord.Put(Here, "**Selam** <@500000000000000002>!\nİyi akşamlar, ığdır çağ 😂",
            mentions: new QuoteMentionNames(new Dictionary<ulong, string> { [500000000000000002] = "Pizza Venk" },
                new Dictionary<ulong, string>(), new Dictionary<ulong, string>()));
        (await Resolve(Message.ToString())).Text.Should().Be("Selam @Pizza Venk!\nİyi akşamlar, ığdır çağ 😂");
    }

    [Fact]
    public void Every_failure_has_a_user_message()
    {
        foreach (var failure in Enum.GetValues<QuoteFailure>().Where(f => f != QuoteFailure.None))
            new QuoteResolution(failure).MessageKey.Should().StartWith("quote.");
        new QuoteResolution(QuoteFailure.NotFound).MessageKey.Should().Be("quote.not_found");
    }
}
