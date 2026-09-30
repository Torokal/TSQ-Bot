using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SixLabors.ImageSharp;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Modules.Quote.Application;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Quote end to end without Discord: message id → resolver → avatar → card, and what an empty text means. The message
/// body is request data only: no log line and no logged exception may ever contain it, whatever goes wrong.
/// </summary>
public sealed class QuoteFlowTests
{
    private const string Secret = "GİZLİ-İÇERİK-7d3f çok özel bir cümle";
    private static readonly GuildId Guild = new(689812743242514448);
    private static readonly ChannelId Here = new(1200000000000000001);
    private static readonly MessageId Message = new(1300000000000000002);
    private static readonly UserId Member = new(500000000000000001);
    private const string AvatarUrl = "https://cdn.discordapp.com/avatars/500000000000000002/abc.png?size=1024";

    private sealed class OneMessage(QuoteFetch fetch) : IQuoteDiscord
    {
        public QuoteChannel? GetChannel(GuildId guild, ChannelId channel) =>
            channel == Here ? new QuoteChannel(Here, true, false, Here, false) : null;

        public Task<GuildPermission?> GetMemberPermissionsAsync(GuildId guild, UserId member, ChannelId channel, CancellationToken cancellationToken) =>
            Task.FromResult<GuildPermission?>(GuildPermission.ViewChannel | GuildPermission.ReadMessageHistory);

        public Task<QuoteFetch> GetMessageAsync(GuildId guild, ChannelId channel, MessageId message, CancellationToken cancellationToken) =>
            Task.FromResult(fetch);
    }

    private sealed class ThrowingRenderer : IQuoteRenderer
    {
        public QuoteCard Render(QuoteRenderModel model) => throw new InvalidOperationException("layout failed for: " + model.Text);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static QuoteSourceMessage Source(string content, QuoteTextAvailability availability = QuoteTextAvailability.Available) =>
        new(Message, Here, content, new QuoteAuthor(new UserId(500000000000000002), "Pizza Venk", "pizzavenk", AvatarUrl), QuoteMentionNames.Empty, availability);

    private static (QuoteMessageResolver Resolver, QuoteCardBuilder Builder, CapturingLoggers Logs) Create(HttpStatusCode avatarStatus = HttpStatusCode.NotFound, IQuoteRenderer? renderer = null)
    {
        var logs = new CapturingLoggers();
        var gateway = new FakeGuildGateway();
        gateway.SetChannel(Guild, Here, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.ReadMessageHistory | GuildPermission.SendMessages | GuildPermission.AttachFiles));
        var png = new byte[] { 1, 2, 3 };
        var avatars = new QuoteAvatarClient(new Factory(new StubHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(avatarStatus) { Content = new ByteArrayContent(png) }))), new FakeTimeProvider(), logs.For<QuoteAvatarClient>());
        return (new QuoteMessageResolver(gateway, logs.For<QuoteMessageResolver>()),
            new QuoteCardBuilder(avatars, renderer ?? new QuoteImageRenderer(QuoteFonts.Load()), logs.For<QuoteCardBuilder>()),
            logs);
    }

    private static Task<QuoteResolution> Resolve(QuoteMessageResolver resolver, QuoteFetch fetch, string input = "1300000000000000002") =>
        resolver.ResolveAsync(new OneMessage(fetch), new QuoteRequest(Guild, Member, Here, input, null),
            CultureInfo.GetCultureInfo("tr-TR"), TimeZoneInfo.Utc, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_normal_members_message_by_id_becomes_a_card_with_the_fallback_avatar()
    {
        var (resolver, builder, logs) = Create(HttpStatusCode.NotFound);
        var resolution = await Resolve(resolver, new QuoteFetch(QuoteFetchStatus.Found, Source(Secret)));
        resolution.Succeeded.Should().BeTrue();
        var built = await builder.BuildAsync(resolution.Message!, resolution.Text, TestContext.Current.CancellationToken);
        built.Card.Should().NotBeNull();
        built.TraceCode.Should().BeNull();
        Image.Identify(built.Card!.Png).Width.Should().Be(QuoteImageRenderer.Width);
        built.Card.Layout.AvatarUsed.Should().BeFalse("the avatar download returned 404");
        logs.Lines.Should().NotContain(l => l.Contains("GİZLİ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_renderer_failure_reports_a_trace_code_and_leaks_no_content()
    {
        var (_, builder, logs) = Create(renderer: new ThrowingRenderer());
        var built = await builder.BuildAsync(Source(Secret), Secret, TestContext.Current.CancellationToken);
        built.Card.Should().BeNull();
        built.TraceCode.Should().StartWith("TS-");
        logs.Lines.Should().Contain(l => l.Contains(built.TraceCode!, StringComparison.Ordinal) && l.Contains("InvalidOperationException", StringComparison.Ordinal));
        logs.Lines.Should().NotContain(l => l.Contains("GİZLİ", StringComparison.Ordinal), "the exception message repeated the text; it must not be logged");
    }

    [Fact]
    public async Task No_outcome_ever_logs_the_message_body()
    {
        var outcomes = new[]
        {
            new QuoteFetch(QuoteFetchStatus.Found, Source(Secret)),
            new QuoteFetch(QuoteFetchStatus.Found, Source("", QuoteTextAvailability.ProbablyWithheld)),
            new QuoteFetch(QuoteFetchStatus.Found, Source("", QuoteTextAvailability.NoTextInMessage)),
            QuoteFetch.NotFound,
            QuoteFetch.NoAccess,
            QuoteFetch.Failed,
        };
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.NotFound })
        {
            var (resolver, builder, logs) = Create(status);
            foreach (var fetch in outcomes)
            {
                var resolution = await Resolve(resolver, fetch);
                if (resolution.Message is { } message && resolution.Succeeded)
                    await builder.BuildAsync(message, resolution.Text, TestContext.Current.CancellationToken);
            }

            await Resolve(resolver, outcomes[0], "https://discord.com/channels/1/2/3");
            await Resolve(resolver, outcomes[0], Secret);
            logs.Lines.Should().NotBeEmpty();
            logs.Lines.Should().NotContain(l => l.Contains("GİZLİ", StringComparison.Ordinal) || l.Contains("özel bir cümle", StringComparison.Ordinal));
            logs.Lines.Should().NotContain(l => l.Contains("Pizza Venk", StringComparison.Ordinal) || l.Contains(AvatarUrl, StringComparison.Ordinal),
                "names and avatar urls are not logged either");
        }
    }

    [Fact]
    public async Task Withheld_and_really_empty_messages_get_different_answers()
    {
        var (resolver, _, _) = Create();
        var withheld = await Resolve(resolver, new QuoteFetch(QuoteFetchStatus.Found, Source("", QuoteTextAvailability.ProbablyWithheld)));
        withheld.Failure.Should().Be(QuoteFailure.ContentUnavailable);
        withheld.MessageKey.Should().Be("quote.content_unavailable");

        var empty = await Resolve(resolver, new QuoteFetch(QuoteFetchStatus.Found, Source("", QuoteTextAvailability.NoTextInMessage)));
        empty.Failure.Should().Be(QuoteFailure.NoText);
        empty.MessageKey.Should().Be("quote.no_text");

        // Text that is only formatting or invisible characters is "no text", never "withheld".
        var invisible = await Resolve(resolver, new QuoteFetch(QuoteFetchStatus.Found, Source("‮\u0000", QuoteTextAvailability.Available)));
        invisible.Failure.Should().Be(QuoteFailure.NoText);
    }

    // ---- what an empty text means (QuoteMessageShape) ----

    private static QuoteMessageShape Shape(bool text = false, bool regular = true, bool gated = false, bool stickers = false, bool forward = false,
        bool exempt = false, bool? access = null) => new(text, regular, gated, stickers, forward, exempt, access);

    [Fact]
    public void Text_is_available_whenever_discord_returned_some() =>
        Shape(text: true, access: false).Classify().Should().Be(QuoteTextAvailability.Available);

    [Theory]
    [InlineData(true, false, false, false)] // attachment/embed/poll only: those fields are content-gated, so access works
    [InlineData(false, true, false, false)] // a forward: the text is in the forwarded snapshot
    [InlineData(false, false, true, false)] // the bot's own message or one that mentions it: always sent
    [InlineData(false, false, false, true)] // a system message (join, pin, boost …)
    public void Messages_without_text_are_no_text_not_withheld(bool gated, bool forward, bool exempt, bool system) =>
        Shape(gated: gated, forward: forward, exempt: exempt, regular: !system, access: false).Classify()
            .Should().Be(QuoteTextAvailability.NoTextInMessage);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)] // flag on but a normal message still came back empty: say so without claiming the cause
    public void A_completely_empty_normal_message_was_probably_withheld(bool? access) =>
        Shape(access: access).Classify().Should().Be(QuoteTextAvailability.ProbablyWithheld);

    [Fact]
    public void A_sticker_alone_is_no_text_only_when_content_access_is_known_to_be_on()
    {
        Shape(stickers: true, access: true).Classify().Should().Be(QuoteTextAvailability.NoTextInMessage);
        Shape(stickers: true, access: false).Classify().Should().Be(QuoteTextAvailability.ProbablyWithheld);
        Shape(stickers: true, access: null).Classify().Should().Be(QuoteTextAvailability.ProbablyWithheld);
    }
}
