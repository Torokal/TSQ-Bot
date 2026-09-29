using System.Globalization;
using System.Net;
using System.Text;
using Discord;
using Discord.Net.Rest;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Modules.Giveaway.Application;
using ToroSquad.Modules.Giveaway.Commands;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// What <see cref="DiscordGiveawayReactions"/> puts on the wire, with the real Discord.Net and only the HTTP layer replaced by
/// a recorder: reading the 🎉 reactions of a card follows Discord's pagination (at most 100 users per request, the next
/// request after the highest user id so far, until a short page) so a giveaway with more than 100 entrants loses nobody; a
/// failure on any page is "unavailable", never a partial list; 404s mean the card is gone. Adding the bot's 🎉 is a PUT on
/// the card's own reaction route. It does NOT prove how Discord answers; the canned responses mirror the documented shapes.
/// </summary>
public sealed class DiscordReactionContractTests
{
    private const ulong BotId = 900;
    private const ulong ChannelId = 7001;
    private const ulong GuildId = 777;
    private const ulong CardId = 424242;
    private const string Tada = "%f0%9f%8e%89"; // 🎉, URL-encoded as Discord.Net sends it

    private sealed class RecordingRestClient(List<(string Method, string Endpoint)> requests, Func<string, string, (HttpStatusCode Status, string Body)> respond) : IRestClient
    {
        /// <summary>Shared by every REST client Discord.Net creates (the socket client and its Rest client each get one).</summary>
        public List<(string Method, string Endpoint)> Requests => requests;

        public void SetHeader(string key, string value)
        {
        }

        public void SetCancelToken(CancellationToken cancelToken)
        {
        }

        public Task<RestResponse> SendAsync(string method, string endpoint, CancellationToken cancelToken, bool headerOnly = false, string? reason = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) => Respond(method, endpoint);

        public Task<RestResponse> SendAsync(string method, string endpoint, string json, CancellationToken cancelToken, bool headerOnly = false, string? reason = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) => Respond(method, endpoint);

        public Task<RestResponse> SendAsync(string method, string endpoint, IReadOnlyDictionary<string, object> multipartParams, CancellationToken cancelToken,
            bool headerOnly = false, string? reason = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) => Respond(method, endpoint);

        public void Dispose()
        {
        }

        private Task<RestResponse> Respond(string method, string endpoint)
        {
            lock (Requests)
                Requests.Add((method, endpoint));
            var (status, body) = respond(method, endpoint);
            return Task.FromResult(new RestResponse(status, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes(body))));
        }
    }

    private static string User(ulong id, bool bot = false) =>
        $$"""{"id":"{{id}}","username":"u{{id}}","discriminator":"0000","bot":{{(bot ? "true" : "false")}}}""";

    private static string Message(ulong id) =>
        $$"""{"id":"{{id}}","channel_id":"{{ChannelId}}","guild_id":"{{GuildId}}","author":{{User(BotId, true)}},"content":"","timestamp":"2026-09-29T12:00:00.000000+00:00","edited_timestamp":null,"tts":false,"mention_everyone":false,"mentions":[],"mention_roles":[],"attachments":[],"embeds":[],"pinned":false,"type":0}""";

    private static (HttpStatusCode, string) Common(string method, string endpoint, Func<string, string, (HttpStatusCode, string)?> specific)
    {
        if (specific(method, endpoint) is { } answer)
            return answer;
        return (method, endpoint) switch
        {
            ("GET", "users/@me") => (HttpStatusCode.OK, User(BotId, true)),
            ("GET", var c) when c == $"channels/{ChannelId}" =>
                (HttpStatusCode.OK, $$"""{"id":"{{ChannelId}}","type":0,"guild_id":"{{GuildId}}","name":"cekilis","position":0,"permission_overwrites":[],"nsfw":false}"""),
            ("GET", var m) when m == $"channels/{ChannelId}/messages/{CardId}" => (HttpStatusCode.OK, Message(CardId)),
            _ => (HttpStatusCode.NotFound, """{"message":"404: Not Found","code":0}"""),
        };
    }

    private static async Task<(DiscordGiveawayReactions Reactions, RecordingRestClient Recorder, DiscordSocketClient Client)> ConnectAsync(
        Func<string, string, (HttpStatusCode, string)?> specific)
    {
        var requests = new List<(string Method, string Endpoint)>();
        RecordingRestClient? rest = null;
        var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            DefaultRetryMode = RetryMode.RetryRatelimit,
            RestClientProvider = _ => rest = new RecordingRestClient(requests, (m, e) => Common(m, e, specific)),
        });
        await client.LoginAsync(TokenType.Bot, "offline-contract-token", validateToken: false);
        return (new DiscordGiveawayReactions(client), rest!, client);
    }

    private static string ReactionRoute => $"channels/{ChannelId}/messages/{CardId}/reactions/{Tada}";

    /// <summary>Discord's documented behaviour: users sorted by id, at most <c>limit</c> (≤ 100) after the given id.</summary>
    private static (HttpStatusCode, string)? Reactions(string method, string endpoint, IReadOnlyList<(ulong Id, bool Bot)> users, Func<int, bool>? failPage = null)
    {
        if (method != "GET" || !endpoint.StartsWith(ReactionRoute + "?", StringComparison.Ordinal))
            return null;
        var query = endpoint[(endpoint.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        var limit = int.Parse(query["limit"], CultureInfo.InvariantCulture);
        var after = query.TryGetValue("after", out var a) ? ulong.Parse(a, CultureInfo.InvariantCulture) : 0UL;
        var page = users.Where(u => u.Id > after).OrderBy(u => u.Id).Take(Math.Min(limit, 100)).ToList();
        if (failPage?.Invoke(users.Count(u => u.Id <= after) / 100) == true)
            return (HttpStatusCode.InternalServerError, """{"message":"500: Internal Server Error","code":0}""");
        return (HttpStatusCode.OK, "[" + string.Join(",", page.Select(u => User(u.Id, u.Bot))) + "]");
    }

    [Fact]
    public async Task More_than_one_hundred_entrants_are_read_page_by_page_with_after_and_nobody_is_lost()
    {
        // 237 people + the bot's own 🎉, ids deliberately not in insertion order.
        var users = Enumerable.Range(0, 237).Select(i => (Id: 1_000_000UL + (ulong)((i * 7919) % 237), Bot: false)).Append((Id: BotId, Bot: true)).ToList();
        var (reactions, rest, client) = await ConnectAsync((m, e) => Reactions(m, e, users));
        using var _ = client;

        var read = await reactions.ReadEntrantsAsync(new ChannelId(ChannelId), new MessageId(CardId), CancellationToken.None);

        var all = read.Should().BeOfType<EntrantRead.Read>().Subject.Users;
        all.Should().HaveCount(238);
        GiveawayEntrants.Valid(all).Should().HaveCount(237).And.OnlyHaveUniqueItems().And.NotContain(new UserId(BotId));

        var pages = rest.Requests.Where(r => r.Endpoint.StartsWith(ReactionRoute, StringComparison.Ordinal)).Select(r => r.Endpoint).ToList();
        pages.Should().HaveCount(3, "100 + 100 + 38");
        pages.Should().OnlyContain(p => p.Contains("limit=100", StringComparison.Ordinal));
        pages[0].Should().Contain("after=0", "the first page starts at the beginning");
        var sorted = users.Select(u => u.Id).Order().ToList();
        pages[1].Should().Contain("after=" + sorted[99].ToString(CultureInfo.InvariantCulture), "the next page starts after the highest id of the previous one");
        pages[2].Should().Contain("after=" + sorted[199].ToString(CultureInfo.InvariantCulture));
        rest.Requests.Should().OnlyContain(r => r.Method == "GET", "reading never changes anything");
    }

    [Fact]
    public async Task Exactly_one_hundred_entrants_need_a_second_empty_page_to_know_the_list_ended()
    {
        var users = Enumerable.Range(1, 100).Select(i => (Id: (ulong)(5000 + i), Bot: false)).ToList();
        var (reactions, rest, client) = await ConnectAsync((m, e) => Reactions(m, e, users));
        using var _ = client;

        var read = (EntrantRead.Read)await reactions.ReadEntrantsAsync(new ChannelId(ChannelId), new MessageId(CardId), CancellationToken.None);
        read.Users.Should().HaveCount(100);
        rest.Requests.Count(r => r.Endpoint.StartsWith(ReactionRoute, StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task A_failing_later_page_makes_the_whole_read_unavailable_never_a_partial_list()
    {
        var users = Enumerable.Range(1, 250).Select(i => (Id: (ulong)(5000 + i), Bot: false)).ToList();
        var (reactions, _, client) = await ConnectAsync((m, e) => Reactions(m, e, users, failPage: page => page == 1));
        using var _c = client;

        (await reactions.ReadEntrantsAsync(new ChannelId(ChannelId), new MessageId(CardId), CancellationToken.None))
            .Should().BeOfType<EntrantRead.Unavailable>();
    }

    [Fact]
    public async Task A_deleted_card_or_channel_is_missing_and_no_access_is_unavailable()
    {
        var (reactions, _, client) = await ConnectAsync((m, e) => (m, e) switch
        {
            ("GET", var x) when x == $"channels/{ChannelId}/messages/1" => (HttpStatusCode.NotFound, """{"message":"Unknown Message","code":10008}"""),
            ("GET", var x) when x == $"channels/{ChannelId}/messages/2" => (HttpStatusCode.OK, Message(2)),
            ("GET", var x) when x.StartsWith($"channels/{ChannelId}/messages/2/reactions/", StringComparison.Ordinal) =>
                (HttpStatusCode.Forbidden, """{"message":"Missing Access","code":50001}"""),
            ("GET", "channels/9999") => (HttpStatusCode.NotFound, """{"message":"Unknown Channel","code":10003}"""),
            _ => null,
        });
        using var _c = client;

        (await reactions.ReadEntrantsAsync(new ChannelId(ChannelId), new MessageId(1), CancellationToken.None)).Should().BeOfType<EntrantRead.Missing>();
        (await reactions.ReadEntrantsAsync(new ChannelId(9999), new MessageId(1), CancellationToken.None)).Should().BeOfType<EntrantRead.Missing>();
        (await reactions.ReadEntrantsAsync(new ChannelId(ChannelId), new MessageId(2), CancellationToken.None))
            .Should().BeOfType<EntrantRead.Unavailable>("no Read Message History is not 'nobody entered'");
    }

    [Fact]
    public async Task The_bots_own_tada_is_one_put_on_the_cards_reaction_route()
    {
        var (reactions, rest, client) = await ConnectAsync((m, e) =>
            m == "PUT" && e == $"{ReactionRoute}/@me" ? (HttpStatusCode.NoContent, "") : null);
        using var _ = client;

        (await reactions.AddEntryReactionAsync(new ChannelId(ChannelId), new MessageId(CardId), CancellationToken.None)).Should().Be(ReactionAddOutcome.Added);
        rest.Requests.Should().ContainSingle(r => r.Method == "PUT").Which.Endpoint.Should().Be($"{ReactionRoute}/@me");

        var (refused, _, other) = await ConnectAsync((m, e) =>
            m == "PUT" ? (HttpStatusCode.Forbidden, """{"message":"Missing Permissions","code":50013}""") : null);
        using var _o = other;
        (await refused.AddEntryReactionAsync(new ChannelId(ChannelId), new MessageId(CardId), CancellationToken.None)).Should().Be(ReactionAddOutcome.Failed);
    }
}
