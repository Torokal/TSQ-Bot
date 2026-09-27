using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Quote.Application;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>TSQ Quote avatar download: Discord CDN only, bounded in time and size, every failure is "no avatar" (never an exception).</summary>
public sealed class QuoteAvatarClientTests
{
    private const string Avatar = "https://cdn.discordapp.com/avatars/111111111111111111/a_0123456789abcdef.png?size=1024";

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be(QuoteAvatarClient.HttpClientName);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static (QuoteAvatarClient Client, StubHttpHandler Handler, FakeTimeProvider Clock) Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHttpHandler((request, _, ct) => respond(request, ct));
        var clock = new FakeTimeProvider();
        return (new QuoteAvatarClient(new Factory(handler), clock, NullLogger<QuoteAvatarClient>.Instance), handler, clock);
    }

    private static HttpResponseMessage Bytes(byte[] body, HttpStatusCode status = HttpStatusCode.OK, long? declaredLength = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        if (declaredLength is { } length)
            content.Headers.ContentLength = length;
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task Downloads_the_discord_cdn_avatar()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var (client, handler, _) = Create((_, _) => Task.FromResult(Bytes(png)));
        (await client.DownloadAsync(Avatar, TestContext.Current.CancellationToken)).Should().Equal(png);
        handler.Requests.Should().ContainSingle().Which.Should().Be(new Uri(Avatar));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)] // redirects are not followed
    [InlineData(HttpStatusCode.NoContent)]
    public async Task A_non_200_answer_is_no_avatar(HttpStatusCode status)
    {
        var (client, _, _) = Create((_, _) => Task.FromResult(Bytes([1, 2, 3], status)));
        (await client.DownloadAsync(Avatar, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_slow_cdn_times_out_into_no_avatar()
    {
        var started = new TaskCompletionSource();
        var (client, _, clock) = Create(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Bytes([1]);
        });
        var download = client.DownloadAsync(Avatar, TestContext.Current.CancellationToken);
        await started.Task;
        clock.Advance(QuoteAvatarClient.Timeout + TimeSpan.FromSeconds(1));
        (await download).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var (client, _, _) = Create(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Bytes([1]);
        });
        await FluentActions.Awaiting(() => client.DownloadAsync(Avatar, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Network_errors_are_no_avatar() =>
        (await Create((_, _) => throw new HttpRequestException("connection refused")).Client
            .DownloadAsync(Avatar, TestContext.Current.CancellationToken)).Should().BeNull();

    [Fact]
    public async Task Oversized_responses_are_refused_whatever_content_length_says()
    {
        var big = new byte[QuoteAvatarClient.MaxBytes + 1];
        var declared = Create((_, _) => Task.FromResult(Bytes([1, 2, 3], declaredLength: big.Length)));
        (await declared.Client.DownloadAsync(Avatar, TestContext.Current.CancellationToken)).Should().BeNull();

        // No or a wrong Content-Length: the body itself is counted while reading.
        var streamed = Create((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(big)) };
            response.Content.Headers.ContentLength = null;
            return Task.FromResult(response);
        });
        (await streamed.Client.DownloadAsync(Avatar, TestContext.Current.CancellationToken)).Should().BeNull();

        var exact = Create((_, _) => Task.FromResult(Bytes(new byte[QuoteAvatarClient.MaxBytes])));
        (await exact.Client.DownloadAsync(Avatar, TestContext.Current.CancellationToken)).Should().HaveCount(QuoteAvatarClient.MaxBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://cdn.discordapp.com/avatars/1/a.png")] // not https
    [InlineData("https://cdn.discordapp.com:8443/avatars/1/a.png")]
    [InlineData("https://evil.example/avatars/1/a.png")]
    [InlineData("https://cdn.discordapp.com.evil.example/avatars/1/a.png")]
    [InlineData("https://user:pass@cdn.discordapp.com/avatars/1/a.png")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/avatars/1/a.png")]
    public async Task Only_https_discord_cdn_urls_are_ever_requested(string? url)
    {
        var (client, handler, _) = Create((_, _) => Task.FromResult(Bytes([1])));
        (await client.DownloadAsync(url, TestContext.Current.CancellationToken)).Should().BeNull();
        handler.Requests.Should().BeEmpty();
        QuoteAvatarClient.IsAllowed("https://cdn.discordapp.com/embed/avatars/3.png").Should().BeTrue("Discord's default avatars");
        QuoteAvatarClient.IsAllowed("https://cdn.discordapp.com/guilds/1/users/2/avatars/abc.png?size=1024").Should().BeTrue("server avatars");
    }

    [Fact]
    public async Task Invalid_image_bytes_download_fine_and_the_renderer_falls_back()
    {
        var garbage = "<html>not an image</html>"u8.ToArray();
        var (client, _, _) = Create((_, _) => Task.FromResult(Bytes(garbage)));
        var bytes = await client.DownloadAsync(Avatar, TestContext.Current.CancellationToken);
        bytes.Should().Equal(garbage);
        QuoteImageRenderer.DecodeAvatar(bytes).Should().BeNull();
        var (canvas, layout) = new QuoteImageRenderer(QuoteFonts.Load()).Compose(new QuoteRenderModel("metin", "Ad", "ad", bytes));
        canvas.Dispose();
        layout.AvatarUsed.Should().BeFalse();
    }
}
