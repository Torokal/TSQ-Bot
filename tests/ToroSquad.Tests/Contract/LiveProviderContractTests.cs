using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Live.Domain;
using ToroSquad.Modules.Live.Providers;
using ToroSquad.Modules.Live.Providers.Kick;
using ToroSquad.Modules.Live.Providers.Twitch;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// Official-API contracts of the live providers (response shapes as documented on dev.twitch.tv / docs.kick.com,
/// verified 2026-09-26): batched requests, header-only auth, offline semantics, and that every failure is "unknown" —
/// never an offline observation.
/// </summary>
public sealed class LiveProviderContractTests
{
    // The system clock: the bounded 5xx retry really waits (about half a second) instead of waiting on a fake timer.
    private static readonly string[] Logins = ["lordtoro", "nasilyani69"];

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private const string TwitchToken = """{"access_token":"app-token-value","expires_in":5011271,"token_type":"bearer"}""";

    private const string TwitchStreams = """
        {"data":[{"id":"40952121085","user_id":"101051819","user_login":"lordtoro","user_name":"LORDTORO","game_id":"32982",
          "game_name":"Valheim","type":"live","title":"VALHEIM SERVERA GİRİYORUZ","tags":["Türkçe"],"viewer_count":78365,
          "started_at":"2026-09-26T17:48:12Z","language":"tr","thumbnail_url":"https://static-cdn.jtvnw.net/previews-ttv/live_user_lordtoro-{width}x{height}.jpg",
          "tag_ids":[],"is_mature":false}],"pagination":{}}
        """;

    private const string TwitchUsers = """
        {"data":[{"id":"101051819","login":"lordtoro","display_name":"LORDTORO","profile_image_url":"https://static-cdn.jtvnw.net/jtv_user_pictures/abc-profile_image-300x300.png"},
                 {"id":"2","login":"nasilyani69","display_name":"nasilyani69","profile_image_url":"https://static-cdn.jtvnw.net/jtv_user_pictures/def-profile_image-300x300.png"}]}
        """;

    private static (TwitchStatusProvider Provider, StubHttpHandler Http) Twitch(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> api, TwitchOptions? options = null)
    {
        var http = new StubHttpHandler(api);
        var provider = new TwitchStatusProvider(new Factory(http), Options.Create(options ?? new TwitchOptions { ClientId = "client-id-value", ClientSecret = "client-secret-value", MaxRetries = 1 }),
            TimeProvider.System, NullLogger<TwitchStatusProvider>.Instance);
        return (provider, http);
    }

    private static Task<HttpResponseMessage> TwitchHappy(HttpRequestMessage r)
    {
        var url = r.RequestUri!.AbsoluteUri;
        if (url.StartsWith("https://id.twitch.tv/oauth2/token", StringComparison.Ordinal))
            return Task.FromResult(StubHttpHandler.Json(TwitchToken));
        if (url.StartsWith("https://id.twitch.tv/oauth2/validate", StringComparison.Ordinal))
            return Task.FromResult(StubHttpHandler.Json("""{"client_id":"client-id-value","scopes":[],"expires_in":5011271}"""));
        if (url.Contains("/helix/users", StringComparison.Ordinal))
            return Task.FromResult(StubHttpHandler.Json(TwitchUsers));
        return Task.FromResult(StubHttpHandler.Json(TwitchStreams));
    }

    [Fact]
    public async Task Twitch_one_batched_streams_request_live_when_listed_offline_when_absent()
    {
        HttpRequestMessage? streams = null;
        var (provider, http) = Twitch((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal))
                streams = r;
            return TwitchHappy(r);
        });

        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);

        result.Outcome.Should().Be(LiveProviderOutcome.Ok);
        var live = result.Observations.Single(o => o.Login == "lordtoro");
        live.Should().Match<LiveObservation>(o => o.IsLive && o.Kind == ObservationKind.Status && o.StreamId == "40952121085" &&
                                                  o.Title == "VALHEIM SERVERA GİRİYORUZ" && o.Category == "Valheim" && o.StartedAt == new DateTimeOffset(2026, 9, 26, 17, 48, 12, TimeSpan.Zero));
        live.AvatarUrl.Should().EndWith("abc-profile_image-300x300.png");
        result.Observations.Single(o => o.Login == "nasilyani69").IsLive.Should().BeFalse("absent from a successful answer = not broadcasting");

        streams!.RequestUri!.Query.Should().Contain("user_login=lordtoro").And.Contain("user_login=nasilyani69");
        streams.Headers.GetValues("Client-Id").Should().Equal("client-id-value");
        streams.Headers.Authorization!.Scheme.Should().Be("Bearer");
        http.Requests.Should().OnlyContain(u => !u.AbsoluteUri.Contains("app-token-value", StringComparison.Ordinal) && !u.AbsoluteUri.Contains("secret", StringComparison.Ordinal),
            "tokens and secrets never travel in a URL");
        http.Requests.Count(u => u.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)).Should().Be(1, "all channels in one request");

        await provider.GetStatusAsync(Logins, CancellationToken.None);
        http.Requests.Count(u => u.AbsolutePath == "/oauth2/token").Should().Be(1, "the app token is cached");
        http.Requests.Count(u => u.AbsolutePath == "/oauth2/validate").Should().Be(1, "validated at start, then hourly");
        http.Requests.Count(u => u.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)).Should().Be(1, "profile pictures are refreshed rarely");
    }

    [Fact]
    public async Task Twitch_entry_that_is_not_type_live_says_nothing_about_that_channel()
    {
        var (provider, _) = Twitch((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
            ? Task.FromResult(StubHttpHandler.Json("""{"data":[{"id":"1","user_login":"lordtoro","type":"","title":"x"}]}"""))
            : TwitchHappy(r));
        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);
        result.Observations.Should().ContainSingle().Which.Login.Should().Be("nasilyani69");
        result.Warnings.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, LiveProviderOutcome.TransportError)]
    [InlineData(HttpStatusCode.BadGateway, LiveProviderOutcome.TransportError)]
    [InlineData(HttpStatusCode.BadRequest, LiveProviderOutcome.SchemaError)]
    public async Task Twitch_http_errors_are_failures_without_observations(HttpStatusCode status, LiveProviderOutcome expected)
    {
        var (provider, http) = Twitch((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(status))
            : TwitchHappy(r));
        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.Observations.Should().BeEmpty("a failed request is never 'offline'");
        if (status >= HttpStatusCode.InternalServerError)
            http.Requests.Count(u => u.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)).Should().Be(2, "one bounded retry for 5xx");
    }

    [Fact]
    public async Task Twitch_timeout_is_a_failure_without_observations()
    {
        var (provider, _) = Twitch(async (r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal))
                throw new TaskCanceledException("simulated timeout");
            return await TwitchHappy(r);
        }, new TwitchOptions { ClientId = "id", ClientSecret = "secret-value", MaxRetries = 0 });
        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);
        result.Outcome.Should().Be(LiveProviderOutcome.Timeout);
        result.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task Twitch_401_fetches_a_new_token_once_then_reports_auth_failure()
    {
        var (provider, http) = Twitch((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
            : TwitchHappy(r));
        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);
        result.Outcome.Should().Be(LiveProviderOutcome.AuthFailed);
        result.Observations.Should().BeEmpty();
        http.Requests.Count(u => u.AbsolutePath == "/oauth2/token").Should().Be(2);
        provider.Auth.LastOutcome.Should().Be(LiveProviderOutcome.AuthFailed);
    }

    [Fact]
    public async Task Twitch_429_honours_ratelimit_reset()
    {
        var (provider, _) = Twitch((r, _) =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal))
                return TwitchHappy(r);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Ratelimit-Reset", (DateTimeOffset.UtcNow + TimeSpan.FromSeconds(42)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        });
        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);
        result.Outcome.Should().Be(LiveProviderOutcome.RateLimited);
        result.RetryAfter!.Value.Should().BeCloseTo(TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Missing_credentials_mean_not_configured_and_no_request()
    {
        var (twitch, twitchHttp) = Twitch((r, _) => TwitchHappy(r), new TwitchOptions());
        (await twitch.GetStatusAsync(Logins, CancellationToken.None)).Outcome.Should().Be(LiveProviderOutcome.NotConfigured);
        twitch.IsConfigured.Should().BeFalse();
        twitchHttp.Requests.Should().BeEmpty();

        var (kick, kickHttp) = Kick((r, _) => KickHappy(r), new KickOptions { ClientId = "only-id" });
        (await kick.GetStatusAsync(Logins, CancellationToken.None)).Outcome.Should().Be(LiveProviderOutcome.NotConfigured);
        kickHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Twitch_malformed_json_is_a_schema_error()
    {
        var (provider, _) = Twitch((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
            ? Task.FromResult(StubHttpHandler.Json("{\"data\": [ oops"))
            : TwitchHappy(r));
        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);
        result.Outcome.Should().Be(LiveProviderOutcome.SchemaError);
        result.Observations.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ Kick

    private const string KickToken = """{"access_token":"kick-app-token","token_type":"Bearer","expires_in":3600}""";

    private const string KickChannels = """
        {"data":[
          {"banner_picture":"","broadcaster_user_id":123,"category":{"id":101,"name":"Counter-Strike 2","thumbnail":""},"channel_description":"",
           "slug":"lordtoro","stream":{"is_live":true,"is_mature":false,"key":"","language":"tr","start_time":"2026-09-26T17:50:00Z","thumbnail":"","url":"","viewer_count":1200},
           "stream_title":"CS2 FACEIT | !discord"},
          {"broadcaster_user_id":456,"category":{"id":0,"name":"","thumbnail":""},"slug":"nasilyani69",
           "stream":{"is_live":false,"is_mature":false,"key":"","language":"","start_time":"0001-01-01T00:00:00Z","thumbnail":"","url":"","viewer_count":0},
           "stream_title":"dünkü başlık"}
        ],"message":"OK"}
        """;

    private const string KickUsers = """{"data":[{"user_id":123,"name":"lordtoro","profile_picture":"https://files.kick.com/images/user/123/profile_image/conversion/abc-fullsize.webp"}],"message":"OK"}""";

    private static (KickStatusProvider Provider, StubHttpHandler Http) Kick(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> api, KickOptions? options = null)
    {
        var http = new StubHttpHandler(api);
        var provider = new KickStatusProvider(new Factory(http), Options.Create(options ?? new KickOptions { ClientId = "kick-id", ClientSecret = "kick-secret-value" }),
            TimeProvider.System, NullLogger<KickStatusProvider>.Instance);
        return (provider, http);
    }

    private static Task<HttpResponseMessage> KickHappy(HttpRequestMessage r)
    {
        var url = r.RequestUri!.AbsoluteUri;
        if (url.StartsWith("https://id.kick.com/oauth/token", StringComparison.Ordinal))
            return Task.FromResult(StubHttpHandler.Json(KickToken));
        if (r.RequestUri.AbsolutePath.EndsWith("/users", StringComparison.Ordinal))
            return Task.FromResult(StubHttpHandler.Json(KickUsers));
        return Task.FromResult(StubHttpHandler.Json(KickChannels));
    }

    [Fact]
    public async Task Kick_one_batched_channels_request_states_live_and_offline_explicitly()
    {
        HttpRequestMessage? channels = null;
        var (provider, http) = Kick((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/channels", StringComparison.Ordinal))
                channels = r;
            return KickHappy(r);
        });

        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);

        result.Outcome.Should().Be(LiveProviderOutcome.Ok);
        var live = result.Observations.Single(o => o.Login == "lordtoro");
        live.Should().Match<LiveObservation>(o => o.IsLive && o.Title == "CS2 FACEIT | !discord" && o.Category == "Counter-Strike 2" &&
                                                  o.StartedAt == new DateTimeOffset(2026, 9, 26, 17, 50, 0, TimeSpan.Zero) && o.Platform == LivePlatform.Kick);
        live.AvatarUrl.Should().EndWith("abc-fullsize.webp");
        var offline = result.Observations.Single(o => o.Login == "nasilyani69");
        offline.IsLive.Should().BeFalse();
        offline.StartedAt.Should().BeNull("placeholder dates are not a start time");

        channels!.RequestUri!.Query.Should().Contain("slug=lordtoro").And.Contain("slug=nasilyani69");
        channels.Headers.Authorization!.Scheme.Should().Be("Bearer");
        http.Requests.Should().OnlyContain(u => !u.AbsoluteUri.Contains("kick-app-token", StringComparison.Ordinal));
        http.Requests.Count(u => u.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)).Should().Be(1);
        http.Requests.Single(u => u.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)).Query.Should().Contain("id=123").And.Contain("id=456");
    }

    [Fact]
    public async Task Kick_a_slug_missing_from_the_answer_is_unknown_not_offline()
    {
        var (provider, _) = Kick((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/channels", StringComparison.Ordinal)
            ? Task.FromResult(StubHttpHandler.Json("""{"data":[{"slug":"lordtoro","broadcaster_user_id":1,"stream":{"is_live":false}}]}"""))
            : KickHappy(r));
        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);
        result.Observations.Should().ContainSingle().Which.Login.Should().Be("lordtoro");
        result.Warnings.Should().ContainSingle(w => w.StartsWith("nasilyani69", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, LiveProviderOutcome.TransportError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LiveProviderOutcome.TransportError)]
    [InlineData(HttpStatusCode.Unauthorized, LiveProviderOutcome.AuthFailed)]
    public async Task Kick_http_errors_are_failures_without_observations(HttpStatusCode status, LiveProviderOutcome expected)
    {
        var (provider, _) = Kick((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/channels", StringComparison.Ordinal)
            ? Task.FromResult(new HttpResponseMessage(status))
            : KickHappy(r));
        var result = await provider.GetStatusAsync(Logins, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task Kick_answer_without_data_is_a_schema_error_and_token_failure_is_auth_failure()
    {
        var (provider, _) = Kick((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/channels", StringComparison.Ordinal)
            ? Task.FromResult(StubHttpHandler.Json("""{"message":"changed"}"""))
            : KickHappy(r));
        (await provider.GetStatusAsync(Logins, CancellationToken.None)).Outcome.Should().Be(LiveProviderOutcome.SchemaError);

        var (badToken, _) = Kick((r, _) => r.RequestUri!.Host == "id.kick.com"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
            : KickHappy(r));
        var result = await badToken.GetStatusAsync(Logins, CancellationToken.None);
        result.Outcome.Should().Be(LiveProviderOutcome.AuthFailed);
        result.Detail.Should().NotContain("kick-secret-value");
        badToken.Auth.LastOutcome.Should().Be(LiveProviderOutcome.AuthFailed);
    }
}
