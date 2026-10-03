using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Summary;
using ToroSquad.Modules.Summary.Application;
using ToroSquad.Modules.Summary.Providers;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The OpenCode Go client against a scripted HTTP handler (never the real service): the exact request (model, settings, no
/// tools, a fresh random session id, an honest User-Agent, the key only in the Authorization header), usage parsing, and
/// exactly ONE request whatever goes wrong — 400, 401, 429, 500, a network error or a timeout are returned, never retried.
/// </summary>
public sealed class SummaryAiClientTests
{
    private const string Key = "test-key-not-a-real-secret-0123456789";

    private const string OkBody = """
        {"id":"x","object":"chat.completion","model":"deepseek-v4.1-flash",
         "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"# Son Mesajların Özeti\n\n## Ana konu\nKonu.","reasoning_content":"düşünce"}}],
         "usage":{"prompt_tokens":2986,"completion_tokens":829,"total_tokens":3815,"completion_tokens_details":{"reasoning_tokens":230}}}
        """;

    private static readonly SummaryPromptMessages Prompt = SummaryPrompt.Build("Mert: selam\nAyşe: naber");

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be(OpenCodeSummaryAiClient.HttpClientName);
            var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(new SummaryOptions().BaseUrl) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(SummaryModule.UserAgent("1.4.0+abc123"));
            return client;
        }
    }

    private sealed class Recorder(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            Started.TrySetResult();
            return await respond(Requests.Count, cancellationToken);
        }
    }

    private static (OpenCodeSummaryAiClient Client, Recorder Http, FakeTimeProvider Clock) Create(
        Func<int, CancellationToken, Task<HttpResponseMessage>> respond, string? key = Key)
    {
        var http = new Recorder(respond);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        return (new OpenCodeSummaryAiClient(new Factory(http), Options.Create(new SummaryOptions()), new SummaryApiKey(key), clock), http, clock);
    }

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    [Fact]
    public async Task Sends_one_openai_compatible_request_with_the_configured_model_and_settings_and_no_tools()
    {
        var (client, http, _) = Create((_, _) => Respond(HttpStatusCode.OK, OkBody));

        var result = await client.SummarizeAsync(Prompt, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        http.Requests.Should().ContainSingle();
        var (request, body) = http.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://opencode.ai/zen/go/v1/chat/completions");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("model").GetString().Should().Be("deepseek-v4.1-flash", "the API model id, without the CLI's provider prefix");
        root.GetProperty("max_tokens").GetInt32().Should().Be(1200);
        root.GetProperty("temperature").GetDouble().Should().Be(0.3);
        root.GetProperty("top_p").GetDouble().Should().Be(0.9);
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
        root.TryGetProperty("reasoning_effort", out _).Should().BeFalse("effort is a thinking-mode setting: never sent with thinking disabled");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("model", "messages", "max_tokens", "temperature", "top_p", "thinking", "stream");
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("role").GetString()).Should().Equal("system", "user");
        messages[0].GetProperty("content").GetString().Should().Be(SummaryPrompt.System);
        messages[1].GetProperty("content").GetString().Should().Be(Prompt.User);
        body.Should().NotContain(Key, "the key travels only in the Authorization header");
    }

    [Fact]
    public void A_request_may_carry_its_own_output_cap_and_nothing_else_changes()
    {
        using var legacy = JsonDocument.Parse(OpenCodeSummaryAiClient.RequestBody(Prompt, new SummaryOptions()));
        using var grounded = JsonDocument.Parse(OpenCodeSummaryAiClient.RequestBody(Prompt with { MaxOutputTokens = 2000 }, new SummaryOptions()));

        legacy.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(1200);
        grounded.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(2000);
        foreach (var name in new[] { "model", "temperature", "top_p", "thinking", "stream" })
            grounded.RootElement.GetProperty(name).GetRawText().Should().Be(legacy.RootElement.GetProperty(name).GetRawText(), name);
        grounded.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(legacy.RootElement.EnumerateObject().Select(p => p.Name),
            "no response_format, no tools, no reasoning_effort");
    }

    [Fact]
    public void Reasoning_effort_is_sent_only_in_thinking_mode()
    {
        using var enabled = JsonDocument.Parse(OpenCodeSummaryAiClient.RequestBody(Prompt, new SummaryOptions { DisableThinking = false, ReasoningEffort = "high" }));
        enabled.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        enabled.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");

        using var disabled = JsonDocument.Parse(OpenCodeSummaryAiClient.RequestBody(Prompt, new SummaryOptions { DisableThinking = true, ReasoningEffort = "high" }));
        disabled.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
        disabled.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse("never both: a contradictory request reasoned without bound live");
    }

    [Fact]
    public async Task Headers_carry_the_key_a_fresh_random_session_id_and_an_honest_user_agent()
    {
        var (client, http, _) = Create((_, _) => Respond(HttpStatusCode.OK, OkBody));

        await client.SummarizeAsync(Prompt, CancellationToken.None);
        await client.SummarizeAsync(Prompt, CancellationToken.None);

        http.Requests.Should().HaveCount(2);
        http.Requests.Should().OnlyContain(r => r.Request.Headers.Authorization!.Scheme == "Bearer" && r.Request.Headers.Authorization.Parameter == Key);
        var sessions = http.Requests.Select(r => r.Request.Headers.GetValues(OpenCodeSummaryAiClient.SessionHeader).Single()).ToList();
        sessions.Select(s => Guid.TryParse(s, out var id) && id != Guid.Empty).Should().AllBeEquivalentTo(true);
        sessions.Distinct().Should().HaveCount(2, "a new session per summary");
        sessions.Should().NotContain(s => s.Contains("Mert", StringComparison.Ordinal));

        var agent = http.Requests[0].Request.Headers.UserAgent.ToString();
        agent.Should().Be("TSQBot/1.4.0 SummaryModule (+https://github.com/Torokal/TSQ-Bot)");
        agent.Should().NotContainAny("opencode", "claude", "codex", "cursor", "agent");
    }

    [Fact]
    public async Task Reads_text_finish_reason_and_usage()
    {
        var (client, _, _) = Create((_, _) => Respond(HttpStatusCode.OK, OkBody));

        var result = await client.SummarizeAsync(Prompt, CancellationToken.None);

        result.Text.Should().Be("# Son Mesajların Özeti\n\n## Ana konu\nKonu.");
        result.FinishReason.Should().Be("stop");
        result.Usage.Should().Be(new SummaryAiUsage(2986, 829, 230));
        result.HttpStatus.Should().Be(200);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SummaryAiFailure.Rejected)]
    [InlineData(HttpStatusCode.NotFound, SummaryAiFailure.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, SummaryAiFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, SummaryAiFailure.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, SummaryAiFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, SummaryAiFailure.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, SummaryAiFailure.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SummaryAiFailure.ServerError)]
    public async Task Provider_errors_are_returned_after_exactly_one_request(HttpStatusCode status, SummaryAiFailure expected)
    {
        var (client, http, _) = Create((_, _) => Respond(status, """{"type":"error","error":{"type":"server_error","message":"no"}}"""));

        var result = await client.SummarizeAsync(Prompt, CancellationToken.None);

        result.Failure.Should().Be(expected);
        result.HttpStatus.Should().Be((int)status);
        result.ProviderError.Should().Be("server_error");
        http.Requests.Should().ContainSingle("no retry of any kind");
    }

    [Fact]
    public async Task A_region_or_privacy_configuration_error_is_named_but_its_message_is_not_kept()
    {
        var body = """{"type":"error","error":{"type":"APIError","message":"Upstream request failed: This Go model requires Global regions. Select Global in your workspace's Privacy settings to use it."}}""";
        var (client, http, _) = Create((_, _) => Respond(HttpStatusCode.BadRequest, body));

        var result = await client.SummarizeAsync(Prompt, CancellationToken.None);

        (result.Failure, result.ProviderError).Should().Be((SummaryAiFailure.Rejected, "RegionPolicy"));
        http.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_network_error_is_returned_after_one_request()
    {
        var (client, http, _) = Create((_, _) => throw new HttpRequestException("Connection reset"));

        var result = await client.SummarizeAsync(Prompt, CancellationToken.None);

        result.Failure.Should().Be(SummaryAiFailure.Network);
        http.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_timeout_is_returned_after_one_request_on_the_injected_clock()
    {
        var (client, http, clock) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var pending = client.SummarizeAsync(Prompt, CancellationToken.None);
        await http.Started.Task;
        clock.Advance(TimeSpan.FromSeconds(new SummaryOptions().RequestTimeoutSeconds));
        var result = await pending;

        result.Failure.Should().Be(SummaryAiFailure.Timeout);
        http.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"object":"list"}""")]
    public async Task A_success_status_with_an_unexpected_body_is_invalid(string body)
    {
        var (client, _, _) = Create((_, _) => Respond(HttpStatusCode.OK, body));

        (await client.SummarizeAsync(Prompt, CancellationToken.None)).Failure.Should().Be(SummaryAiFailure.InvalidResponse);
    }

    [Theory]
    [InlineData(null, 25)] // the legacy request: Summary:RequestTimeoutSeconds
    [InlineData("generator", 35)]
    [InlineData("reviewer", 25)]
    public async Task Each_request_times_out_on_its_own_deadline(string? stage, int seconds)
    {
        var settings = new SummaryOptions();
        var (client, http, clock) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var prompt = stage switch
        {
            "generator" => Prompt with { Profile = settings.GroundedGenerator },
            "reviewer" => Prompt with { Profile = settings.GroundedReviewer },
            _ => Prompt,
        };

        var pending = client.SummarizeAsync(prompt, CancellationToken.None);
        await http.Started.Task;
        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        pending.IsCompleted.Should().BeFalse("one second before its own deadline the request is still running");
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await pending;

        result.Failure.Should().Be(SummaryAiFailure.Timeout);
        http.Requests.Should().ContainSingle("a timeout is not retried");
    }

    [Fact]
    public async Task An_answer_without_text_is_empty_output()
    {
        var (client, _, _) = Create((_, _) => Respond(HttpStatusCode.OK,
            """{"choices":[{"finish_reason":"length","message":{"role":"assistant","content":""}}],"usage":{"prompt_tokens":10,"completion_tokens":900}}"""));

        var result = await client.SummarizeAsync(Prompt, CancellationToken.None);

        (result.Failure, result.FinishReason, result.Usage.OutputTokens).Should().Be((SummaryAiFailure.EmptyOutput, "length", (int?)900));
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var (client, http, _) = Create((_, _) => Respond(HttpStatusCode.OK, OkBody), key: "  ");

        client.IsConfigured.Should().BeFalse();
        (await client.SummarizeAsync(Prompt, CancellationToken.None)).Failure.Should().Be(SummaryAiFailure.NotConfigured);
        http.Requests.Should().BeEmpty();
        new SummaryApiKey(Key).ToString().Should().NotContain(Key);
    }
    [Fact]
    public void A_request_with_its_own_profile_uses_that_model_and_reasoning_shape_and_the_legacy_request_is_unchanged()
    {
        var settings = new SummaryOptions();
        var prompt = new SummaryPromptMessages("sistem", "kullanıcı", 2000);

        using var legacy = JsonDocument.Parse(OpenCodeSummaryAiClient.RequestBody(prompt with { MaxOutputTokens = null }, settings));
        using var generator = JsonDocument.Parse(OpenCodeSummaryAiClient.RequestBody(prompt with { Profile = settings.GroundedGenerator }, settings));
        using var reviewer = JsonDocument.Parse(OpenCodeSummaryAiClient.RequestBody(prompt with { Profile = settings.GroundedReviewer }, settings));

        // Legacy: the configured model, thinking disabled, no effort — exactly as before.
        legacy.RootElement.GetProperty("model").GetString().Should().Be("deepseek-v4.1-flash");
        legacy.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
        legacy.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        legacy.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(1200);

        // Generator: no thinking object at all, the lowest effort level.
        generator.RootElement.GetProperty("model").GetString().Should().Be("glm-5.3-flash");
        generator.RootElement.TryGetProperty("thinking", out _).Should().BeFalse("this model lists effort levels and no on/off switch");
        generator.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("low");

        // Reviewer: the shape checked for it — thinking disabled, never together with an effort.
        reviewer.RootElement.GetProperty("model").GetString().Should().Be("deepseek-v4.1-flash");
        reviewer.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
        reviewer.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();

        foreach (var body in new[] { generator, reviewer })
        {
            body.RootElement.EnumerateObject().Select(p => p.Name).Should().NotContain(["tools", "tool_choice", "web_search", "user"], "no tools, no web access, nothing about the member");
            (body.RootElement.GetProperty("max_tokens").GetInt32(), body.RootElement.GetProperty("temperature").GetDouble(), body.RootElement.GetProperty("top_p").GetDouble(),
                body.RootElement.GetProperty("stream").GetBoolean()).Should().Be((2000, 0.3, 0.9, false));
        }
    }

    [Fact]
    public void The_grounded_models_are_validated_like_the_legacy_model()
    {
        new SummaryOptions().Validate().Should().BeEmpty();
        new SummaryOptions { GroundedGeneratorModel = "" }.Validate().Should().ContainSingle().Which.Should().Contain("GroundedGeneratorModel");
        new SummaryOptions { GroundedReviewerModel = "iki kelime" }.Validate().Should().ContainSingle().Which.Should().Contain("GroundedReviewerModel");
        new SummaryOptions { GroundedGeneratorReasoningEffort = "none" }.Validate().Should().ContainSingle().Which.Should().Contain("GroundedGeneratorReasoningEffort");
    }

}
