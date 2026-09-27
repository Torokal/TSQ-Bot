using System.Net;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.Net.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Transport;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// What OUR code puts on the wire when the bot edits or checks one of its own messages later (TSQ LFG expiry/close, outbox
/// edits): the real <see cref="DiscordMessageTransport"/> and Discord.Net, with only the HTTP layer replaced by a recorder.
/// It proves the route and credentials we use — a bot-token PATCH/GET on /channels/{channel}/messages/{message}, never an
/// interaction or webhook endpoint (whose token expires) — and that edits carry allowed_mentions with nothing to parse. It
/// does NOT prove how Discord answers; the canned responses below only exercise our classification of documented errors.
/// </summary>
public sealed class DiscordEditRouteContractTests
{
    private const ulong BotId = 900;
    private const ulong ChannelId = 7001;
    private const ulong GuildId = 777;
    private const ulong CardId = 424242;

    private sealed record Request(string Method, string Endpoint, string? Json, IReadOnlyDictionary<string, string> Headers);

    private sealed class RecordingRestClient(Func<string, string, (HttpStatusCode Status, string Body)> respond) : IRestClient
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public List<Request> Requests { get; } = [];

        public void SetHeader(string key, string value)
        {
            if (value is null)
                _headers.Remove(key);
            else
                _headers[key] = value;
        }

        public void SetCancelToken(CancellationToken cancelToken)
        {
        }

        public Task<RestResponse> SendAsync(string method, string endpoint, CancellationToken cancelToken, bool headerOnly = false, string? reason = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) => Respond(method, endpoint, null, requestHeaders);

        public Task<RestResponse> SendAsync(string method, string endpoint, string json, CancellationToken cancelToken, bool headerOnly = false, string? reason = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) => Respond(method, endpoint, json, requestHeaders);

        public Task<RestResponse> SendAsync(string method, string endpoint, IReadOnlyDictionary<string, object> multipartParams, CancellationToken cancelToken,
            bool headerOnly = false, string? reason = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) =>
            Respond(method, endpoint, "<multipart>", requestHeaders);

        public void Dispose()
        {
        }

        private Task<RestResponse> Respond(string method, string endpoint, string? json, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders)
        {
            var headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, values) in requestHeaders ?? [])
                headers[key] = string.Join(",", values);
            Requests.Add(new Request(method, endpoint, json, headers));
            var (status, body) = respond(method, endpoint);
            var response = new RestResponse(status, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes(body)));
            return Task.FromResult(response);
        }
    }

    private static string User(ulong id) => $$"""{"id":"{{id}}","username":"tsq","discriminator":"0000","bot":true}""";

    private static string Message(ulong id, ulong author) =>
        $$"""{"id":"{{id}}","channel_id":"{{ChannelId}}","guild_id":"{{GuildId}}","author":{{User(author)}},"content":"","timestamp":"2026-09-27T12:00:00.000000+00:00","edited_timestamp":null,"tts":false,"mention_everyone":false,"mentions":[],"mention_roles":[],"attachments":[],"embeds":[],"pinned":false,"type":0}""";

    private static (HttpStatusCode, string) Common(string method, string endpoint, Func<string, string, (HttpStatusCode, string)?> specific)
    {
        if (specific(method, endpoint) is { } answer)
            return answer;
        return (method, endpoint) switch
        {
            ("GET", "users/@me") => (HttpStatusCode.OK, User(BotId)),
            ("GET", var e) when e.StartsWith("applications/@me", StringComparison.Ordinal) || e.StartsWith("oauth2/applications/@me", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, $$"""{"id":"{{BotId}}","name":"TSQ Bot","description":"","bot_public":false,"bot_require_code_grant":false,"flags":0}"""),
            ("GET", var c) when c == $"channels/{ChannelId}" =>
                (HttpStatusCode.OK, $$"""{"id":"{{ChannelId}}","type":0,"guild_id":"{{GuildId}}","name":"ekip-bul","position":0,"permission_overwrites":[],"nsfw":false}"""),
            _ => (HttpStatusCode.NotFound, """{"message":"404: Not Found","code":0}"""),
        };
    }

    private static async Task<(DiscordMessageTransport Transport, RecordingRestClient Recorder, DiscordSocketClient Client)> ConnectAsync(
        Func<string, string, (HttpStatusCode, string)?> specific)
    {
        RecordingRestClient? rest = null;
        var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            DefaultRetryMode = RetryMode.RetryRatelimit,
            RestClientProvider = _ => rest = new RecordingRestClient((m, e) => Common(m, e, specific)),
        });
        await client.LoginAsync(TokenType.Bot, "offline-contract-token", validateToken: false);
        return (new DiscordMessageTransport(client, NullLogger<DiscordMessageTransport>.Instance), rest!, client);
    }

    private static OutgoingMessage Card() => new(null,
        new MessageEmbed("🎮 Deadlock", "<@10> ekip arıyor\n👥 **2 / 6**\n\n**Oyuncular**\n<@10> · <@20>\n\n🔒 **İlan kapatıldı**", null, [], "TSQ LFG · Oyuncu Bul", null, 0x747F8D),
        MentionPolicy.None,
        [new MessageButton("Katıl", "tsq:lfg:join:1", null, Disabled: true, Style: MessageButtonStyle.Success)]);

    [Fact]
    public async Task A_later_edit_is_a_bot_token_patch_on_the_channel_message_route_with_no_mentions_to_parse()
    {
        var (transport, rest, client) = await ConnectAsync((m, e) =>
            m == "PATCH" && e == $"channels/{ChannelId}/messages/{CardId}" ? (HttpStatusCode.OK, Message(CardId, BotId)) : null);
        using var _ = client;

        var outcome = await transport.EditAsync(new ChannelId(ChannelId), new MessageId(CardId), Card() with { Mentions = MentionPolicy.EveryoneOnly }, CancellationToken.None);

        outcome.Should().Be(new SendOutcome.Sent(new MessageId(CardId)));
        var edit = rest.Requests.Should().ContainSingle(r => r.Method == "PATCH").Subject;
        edit.Endpoint.Should().Be($"channels/{ChannelId}/messages/{CardId}");
        edit.Headers["Authorization"].Should().StartWith("Bot ", "the bot's own REST credentials, not an interaction token");
        rest.Requests.Should().NotContain(r => r.Endpoint.StartsWith("webhooks/", StringComparison.Ordinal) || r.Endpoint.StartsWith("interactions/", StringComparison.Ordinal));

        using var body = JsonDocument.Parse(edit.Json!);
        var allowed = body.RootElement.GetProperty("allowed_mentions");
        allowed.GetProperty("parse").GetArrayLength().Should().Be(0, "edits never ping, even if the payload asked for it");
        foreach (var list in new[] { "users", "roles" })
        {
            if (allowed.TryGetProperty(list, out var ids))
                ids.GetArrayLength().Should().Be(0, $"no {list} may be pinged by an edit");
        }
        body.RootElement.GetProperty("embeds")[0].GetProperty("description").GetString().Should().Contain("<@20>");
        body.RootElement.GetProperty("components")[0].GetProperty("components")[0].GetProperty("style").GetInt32().Should().Be(3, "Success");
    }

    [Fact]
    public async Task Unknown_message_and_missing_permissions_are_classified_for_the_lfg_retry_rules()
    {
        var (transport, _, client) = await ConnectAsync((m, e) => (m, e) switch
        {
            ("PATCH", var x) when x.EndsWith("/1", StringComparison.Ordinal) => (HttpStatusCode.NotFound, """{"message":"Unknown Message","code":10008}"""),
            ("PATCH", var x) when x.EndsWith("/2", StringComparison.Ordinal) => (HttpStatusCode.Forbidden, """{"message":"Missing Permissions","code":50013}"""),
            ("PATCH", var x) when x.EndsWith("/3", StringComparison.Ordinal) => (HttpStatusCode.Forbidden, """{"message":"Missing Access","code":50001}"""),
            _ => null,
        });
        using var _c = client;

        (await transport.EditAsync(new ChannelId(ChannelId), new MessageId(1), Card(), CancellationToken.None))
            .Should().BeOfType<SendOutcome.Permanent>().Which.Kind.Should().Be(PermanentFailureKind.UnknownMessage);
        (await transport.EditAsync(new ChannelId(ChannelId), new MessageId(2), Card(), CancellationToken.None))
            .Should().BeOfType<SendOutcome.Permanent>().Which.Kind.Should().Be(PermanentFailureKind.MissingPermissions);
        (await transport.EditAsync(new ChannelId(ChannelId), new MessageId(3), Card(), CancellationToken.None))
            .Should().BeOfType<SendOutcome.Permanent>().Which.Kind.Should().Be(PermanentFailureKind.MissingAccess);
    }

    [Fact]
    public async Task Presence_is_one_get_of_that_message_and_only_a_404_counts_as_missing_while_the_bot_identity_is_unknown()
    {
        var (transport, rest, client) = await ConnectAsync((m, e) => (m, e) switch
        {
            ("GET", var e1) when e1 == $"channels/{ChannelId}/messages/1" => (HttpStatusCode.OK, Message(1, BotId)),
            ("GET", var e2) when e2 == $"channels/{ChannelId}/messages/2" => (HttpStatusCode.NotFound, """{"message":"Unknown Message","code":10008}"""),
            ("GET", var e3) when e3 == $"channels/{ChannelId}/messages/3" => (HttpStatusCode.Forbidden, """{"message":"Missing Access","code":50001}"""),
            ("GET", var e4) when e4 == $"channels/{ChannelId}/messages/4" => (HttpStatusCode.OK, Message(4, 12345)),
            _ => null,
        });
        using var _c = client;

        async Task<MessagePresence> Check(ulong id) => await transport.GetPresenceAsync(new ChannelId(ChannelId), new MessageId(id), CancellationToken.None);
        // Offline there is no gateway READY, so the bot's own id is unknown: an existing message is then "cannot tell" (never
        // "missing"); with the gateway connected the author check applies (FakeMessageTransport covers Present/Missing).
        (await Check(1)).Should().Be(MessagePresence.Unknown);
        (await Check(4)).Should().Be(MessagePresence.Unknown);
        (await Check(2)).Should().Be(MessagePresence.Missing, "404 Unknown Message");
        (await Check(3)).Should().Be(MessagePresence.Unknown, "no access is not a deletion");

        rest.Requests.Where(r => r.Endpoint.Contains("/messages", StringComparison.Ordinal)).Should()
            .OnlyContain(r => r.Method == "GET" && r.Endpoint.StartsWith($"channels/{ChannelId}/messages/", StringComparison.Ordinal) && r.Headers["Authorization"].StartsWith("Bot ", StringComparison.Ordinal));
    }
}
