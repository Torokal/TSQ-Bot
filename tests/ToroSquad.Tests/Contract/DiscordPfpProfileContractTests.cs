using System.Net;
using System.Text;
using Discord;
using Discord.Net.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using ToroSquad.Modules.Pfp.Application;
using ToroSquad.Modules.Pfp.Commands;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// What <see cref="DiscordPfpProfileSource"/> makes of Discord's answers, with the real Discord.Net 3.20.1 and only the HTTP
/// layer replaced by a recorder: a picture field sent with a hash, sent as null, or not sent at all (Discord.Net turns the last
/// two into the same null, so an optional field without a hash is only "not reported"); which requests each command and type
/// sends; and that a 404 on the guild, a refused read or a broken body is never "not a member" or "no picture". It does NOT
/// prove how Discord answers; the canned bodies follow the documented User and Guild Member objects.
/// </summary>
public sealed class DiscordPfpProfileContractTests
{
    private const ulong BotId = 900;
    private const ulong GuildId = 689812743242514448;
    private const ulong TargetId = 222222222222222222;
    private const string Hash = "0123456789abcdef0123456789abcdef";
    private const string Banner = "00112233445566778899aabbccddeeff";

    private static string GuildRoute => $"guilds/{GuildId}?with_counts=false";
    private static string MemberRoute => $"guilds/{GuildId}/members/{TargetId}";
    private static string UserRoute => $"users/{TargetId}";

    private sealed class RecordingRestClient(List<(string Method, string Endpoint)> requests, Func<string, string, (HttpStatusCode Status, string Body)> respond) : IRestClient
    {
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
            lock (requests)
                requests.Add((method, endpoint));
            var (status, body) = respond(method, endpoint);
            return Task.FromResult(new RestResponse(status, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes(body))));
        }
    }

    /// <summary>A JSON value, or <see cref="Omit"/> to leave the field out of the object.</summary>
    private const string Omit = "<omit>";

    private static string Field(string name, string value) => value == Omit ? "" : $",\"{name}\":{value}";

    private static string Str(string value) => $"\"{value}\"";

    private static string User(ulong id, string avatar = "null", string banner = Omit) =>
        $"{{\"id\":\"{id}\",\"username\":\"u{id}\",\"discriminator\":\"0\",\"global_name\":\"Yusuf\"{Field("avatar", avatar)}{Field("banner", banner)}}}";

    private static string Member(string userAvatar = "null", string avatar = Omit, string banner = Omit) =>
        $"{{\"user\":{User(TargetId, userAvatar)},\"nick\":null,\"roles\":[],\"joined_at\":\"2026-01-01T00:00:00.000000+00:00\",\"deaf\":false,\"mute\":false,\"flags\":0" +
        $"{Field("avatar", avatar)}{Field("banner", banner)}}}";

    private static readonly string Guild =
        $$"""{"id":"{{GuildId}}","name":"TSQ","icon":null,"splash":null,"discovery_splash":null,"owner_id":"1","afk_channel_id":null,"afk_timeout":300,"verification_level":0,"default_message_notifications":0,"explicit_content_filter":0,"roles":[],"emojis":[],"features":[],"mfa_level":0,"application_id":null,"system_channel_id":null,"system_channel_flags":0,"rules_channel_id":null,"vanity_url_code":null,"description":null,"banner":null,"premium_tier":0,"preferred_locale":"en-US","public_updates_channel_id":null,"nsfw_level":0,"stickers":[]}""";

    private static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);

    private static (HttpStatusCode, string) Answer(string endpoint, string member, string user) => endpoint switch
    {
        "users/@me" => Ok(User(BotId)),
        _ when endpoint == GuildRoute => Ok(Guild),
        _ when endpoint == MemberRoute => Ok(member),
        _ when endpoint == UserRoute => Ok(user),
        _ => (HttpStatusCode.NotFound, """{"message":"404: Not Found","code":0}"""),
    };

    private static async Task<(DiscordPfpProfileSource Source, List<(string Method, string Endpoint)> Requests, DiscordSocketClient Client)> ConnectAsync(
        Func<string, (HttpStatusCode, string)> respond)
    {
        var requests = new List<(string Method, string Endpoint)>();
        var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            DefaultRetryMode = RetryMode.RetryRatelimit,
            RestClientProvider = _ => new RecordingRestClient(requests, (_, e) => respond(e)),
        });
        await client.LoginAsync(TokenType.Bot, "offline-contract-token", validateToken: false);
        return (new DiscordPfpProfileSource(client.Rest, NullLogger.Instance), requests, client);
    }

    private static List<string> Reads(List<(string Method, string Endpoint)> requests)
    {
        lock (requests)
            return requests.Where(r => r.Endpoint != "users/@me").Select(r => $"{r.Method} {r.Endpoint}").ToList();
    }

    public static TheoryData<string, PfpHashState> OptionalField => new()
    {
        { Str(Hash), PfpHashState.Present },
        { "null", PfpHashState.NotReported },
        { Omit, PfpHashState.NotReported },
    };

    [Theory]
    [MemberData(nameof(OptionalField))]
    public async Task The_members_own_avatar_and_banner_are_optional_fields_so_no_hash_is_only_not_reported(string value, PfpHashState expected)
    {
        var (source, _, client) = await ConnectAsync(e => Answer(e, Member(userAvatar: Str(Hash), avatar: value, banner: value), User(TargetId)));
        using var _c = client;

        var read = await source.GetMemberAsync(GuildId, TargetId, CancellationToken.None);

        read.Status.Should().Be(PfpFetch.Ok);
        read.Profile!.GuildAvatar.State.Should().Be(expected);
        read.Profile.GuildBanner.State.Should().Be(expected);
        read.Profile.Avatar.Should().Be(PfpHash.Of(Hash));
        read.Profile.DisplayName.Should().Be("Yusuf");
    }

    [Fact]
    public async Task The_global_avatar_is_an_always_sent_field_so_null_is_no_avatar()
    {
        var (source, _, client) = await ConnectAsync(e => Answer(e, Member(userAvatar: "null"), User(TargetId)));
        using var _c = client;

        var read = await source.GetMemberAsync(GuildId, TargetId, CancellationToken.None);

        read.Profile!.Avatar.Should().Be(PfpHash.None);
        read.Profile.DefaultAvatarUrl.Should().MatchRegex(@"^https://cdn\.discordapp\.com/embed/avatars/[0-9]\.png$");
    }

    [Theory]
    [MemberData(nameof(OptionalField))]
    public async Task The_global_banner_comes_from_get_user_and_no_hash_is_only_not_reported(string value, PfpHashState expected)
    {
        var (source, requests, client) = await ConnectAsync(e => Answer(e, Member(), User(TargetId, banner: value)));
        using var _c = client;

        var read = await source.GetUserBannerAsync(TargetId, CancellationToken.None);

        read.Status.Should().Be(PfpFetch.Ok);
        read.Banner.State.Should().Be(expected);
        Reads(requests).Should().Equal($"GET {UserRoute}");
    }

    public static TheoryData<PfpKind, PfpScope, bool, string[]> Requests => new()
    {
        // kind, scope, the member has its own server picture, the requests sent
        { PfpKind.Avatar, PfpScope.Auto, false, ["guild", "member"] },
        { PfpKind.Avatar, PfpScope.Global, true, ["guild", "member"] },
        { PfpKind.Avatar, PfpScope.Server, false, ["guild", "member"] },
        { PfpKind.Banner, PfpScope.Auto, true, ["guild", "member"] },
        { PfpKind.Banner, PfpScope.Auto, false, ["guild", "member", "user"] },
        { PfpKind.Banner, PfpScope.Global, true, ["guild", "member", "user"] },
        { PfpKind.Banner, PfpScope.Server, false, ["guild", "member"] },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Each_command_sends_only_the_reads_it_needs(PfpKind kind, PfpScope scope, bool serverPicture, string[] expected)
    {
        var own = serverPicture ? Str(Hash) : "null";
        var (source, requests, client) = await ConnectAsync(e => Answer(e, Member(userAvatar: Str(Hash), avatar: own, banner: own), User(TargetId, banner: Str(Banner))));
        using var _c = client;

        await new PfpResolver(NullLogger<PfpResolver>.Instance).ResolveAsync(source, GuildId, TargetId, kind, scope, CancellationToken.None);

        var routes = new Dictionary<string, string> { ["guild"] = $"GET {GuildRoute}", ["member"] = $"GET {MemberRoute}", ["user"] = $"GET {UserRoute}" };
        Reads(requests).Should().Equal(expected.Select(r => routes[r]));
    }

    [Fact]
    public async Task Only_a_404_on_the_member_is_not_a_member()
    {
        var (gone, _, c1) = await ConnectAsync(e => e == MemberRoute ? (HttpStatusCode.NotFound, """{"message":"Unknown Member","code":10007}""") : Answer(e, Member(), User(TargetId)));
        using var _c1 = c1;
        (await gone.GetMemberAsync(GuildId, TargetId, CancellationToken.None)).Status.Should().Be(PfpFetch.NotFound);

        var (noGuild, requests, c2) = await ConnectAsync(e => e == GuildRoute ? (HttpStatusCode.NotFound, """{"message":"Unknown Guild","code":10004}""") : Answer(e, Member(), User(TargetId)));
        using var _c2 = c2;
        (await noGuild.GetMemberAsync(GuildId, TargetId, CancellationToken.None)).Status
            .Should().Be(PfpFetch.Failed, "a guild Discord does not show says nothing about the member");
        Reads(requests).Should().Equal($"GET {GuildRoute}");
    }

    [Fact]
    public async Task Refused_missing_or_broken_answers_are_failures_never_no_picture()
    {
        var (refused, _, c1) = await ConnectAsync(e => e == MemberRoute ? (HttpStatusCode.Forbidden, """{"message":"Missing Access","code":50001}""") : Answer(e, Member(), User(TargetId)));
        using var _c1 = c1;
        (await refused.GetMemberAsync(GuildId, TargetId, CancellationToken.None)).Status.Should().Be(PfpFetch.Failed);

        var (serverError, _, c2) = await ConnectAsync(e => e == UserRoute ? (HttpStatusCode.InternalServerError, """{"message":"500: Internal Server Error","code":0}""") : Answer(e, Member(), User(TargetId)));
        using var _c2 = c2;
        (await serverError.GetUserBannerAsync(TargetId, CancellationToken.None)).Status.Should().Be(PfpFetch.Failed);

        var (noUser, _, c3) = await ConnectAsync(e => e == UserRoute ? (HttpStatusCode.NotFound, """{"message":"Unknown User","code":10013}""") : Answer(e, Member(), User(TargetId)));
        using var _c3 = c3;
        (await noUser.GetUserBannerAsync(TargetId, CancellationToken.None)).Status.Should().Be(PfpFetch.Failed);

        var (broken, _, c4) = await ConnectAsync(e => e == MemberRoute ? Ok("{\"user\":") : Answer(e, Member(), User(TargetId)));
        using var _c4 = c4;
        // Never an empty profile: the exception reaches PfpCommandFlow, which answers "alınamadı" (PfpFlowTests).
        await FluentActions.Awaiting(() => broken.GetMemberAsync(GuildId, TargetId, CancellationToken.None)).Should().ThrowAsync<Exception>();
    }
}
