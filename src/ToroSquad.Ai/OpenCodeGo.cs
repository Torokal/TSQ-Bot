using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ToroSquad.Ai;

/// <summary>The OpenCode Go API key (<see cref="OpenCodeGo.ApiKeyVariable"/>). Never printed: <see cref="ToString"/> hides it.</summary>
public sealed class OpenCodeGoApiKey(string? value)
{
    public string? Value { get; } = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public bool IsSet => Value is not null;

    /// <summary>User-secrets/configuration first (development), then the plain environment variable (Railway Variables).</summary>
    public static OpenCodeGoApiKey FromSources(string? configured) =>
        new(configured ?? Environment.GetEnvironmentVariable(OpenCodeGo.ApiKeyVariable));

    public override string ToString() => IsSet ? "***" : "(not set)";
}

/// <summary>
/// OpenCode Go's OpenAI-compatible <c>POST chat/completions</c>: the one account, key and endpoint every TSQ AI feature uses.
/// The wire format (<see cref="RequestBody"/>, <see cref="Parse"/>), the error classification and the HTTP client settings
/// live here once; prompts, models, limits and data rules stay in the modules.
/// </summary>
public static class OpenCodeGo
{
    /// <summary>The API key: an environment variable (Railway Variables), or a user-secrets key of the same name.</summary>
    public const string ApiKeyVariable = "OPENCODE_GO_API_KEY";

    /// <summary>OpenCode Go's OpenAI-compatible endpoint; "chat/completions" is read relative to it.</summary>
    public const string DefaultBaseUrl = "https://opencode.ai/zen/go/v1/";

    /// <summary>
    /// A new random GUID per request — never a guild, channel, member, name or text. Not a conversation memory: TSQ features
    /// keep their own context and send it in the messages.
    /// </summary>
    public const string SessionHeader = "x-opencode-session";

    /// <summary>The largest answer accepted (an answer is a few KB).</summary>
    public const int MaxResponseBytes = 512 * 1024;

    /// <summary>The longest Retry-After passed on; a longer one is shortened to this.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    /// <summary>An absolute https URL ending with '/', without credentials, query or fragment.</summary>
    public static bool IsValidBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath.EndsWith('/');

    /// <summary>A model id as the API expects it: no spaces, at most 100 characters.</summary>
    public static bool IsValidModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Length <= 100 && !model.Any(char.IsWhiteSpace);

    /// <summary>
    /// Registers a named HTTP client for one feature: fixed base URL, no redirects (a moved endpoint is a visible failure), no
    /// cookies, bounded answer size, an honest User-Agent naming the feature. The per-request deadline is applied by
    /// <see cref="OpenCodeGoChatClient"/> on the injected clock; <paramref name="backstopTimeout"/> only has to be longer.
    /// </summary>
    public static IHttpClientBuilder AddOpenCodeGoHttpClient(this IServiceCollection services, string name,
        Func<IServiceProvider, (string BaseUrl, TimeSpan BackstopTimeout, string UserAgent)> settings) =>
        services.AddHttpClient(name, (sp, client) =>
            {
                var (baseUrl, backstopTimeout, userAgent) = settings(sp);
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = backstopTimeout;
                client.MaxResponseContentBufferSize = MaxResponseBytes;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

    /// <summary>Registers the one key (shared by every feature that uses OpenCode Go).</summary>
    public static IServiceCollection AddOpenCodeGoApiKey(this IServiceCollection services, string? configured)
    {
        services.TryAddSingleton(OpenCodeGoApiKey.FromSources(configured));
        return services;
    }

    /// <summary>401 authentication, 403 permission/policy, 408 the provider's own timeout, 429 a limit, 5xx an outage, other 4xx the request or its settings.</summary>
    public static AiFailure Classify(HttpStatusCode status) => (int)status switch
    {
        401 => AiFailure.Unauthorized,
        403 => AiFailure.Forbidden,
        408 => AiFailure.Timeout,
        429 => AiFailure.RateLimited,
        >= 500 => AiFailure.ServerError,
        _ => AiFailure.Rejected,
    };

    /// <summary>A 429's Retry-After (seconds or an HTTP date) as a wait from now; null when absent, invalid or already past.</summary>
    public static TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        var wait = header?.Delta ?? (header?.Date is { } at ? at - now : null);
        return wait is { } w && w > TimeSpan.Zero ? (w > MaxRetryAfter ? MaxRetryAfter : w) : null;
    }

    /// <summary>
    /// The request body: the messages in order and the generation settings. Nothing else (no tools, no user id, no response
    /// format the gateway was not checked for). Field order is stable (regression tests compare it).
    /// </summary>
    public static byte[] RequestBody(AiChatRequest request)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("model", request.Model);
            json.WriteStartArray("messages");
            foreach (var message in request.Messages)
            {
                json.WriteStartObject();
                json.WriteString("role", message.Role switch
                {
                    AiRole.System => "system",
                    AiRole.Assistant => "assistant",
                    _ => "user",
                });
                json.WriteString("content", message.Content);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteNumber("max_tokens", request.MaxOutputTokens);
            json.WriteNumber("temperature", request.Temperature);
            json.WriteNumber("top_p", request.TopP);
            // Thinking off is thinking.type=disabled ALONE: reasoning_effort is a thinking-mode setting, and sending both was a
            // contradictory request that some upstream backends answered with unbounded hidden reasoning.
            switch (request.Thinking)
            {
                case AiThinking.Disabled:
                    json.WriteStartObject("thinking");
                    json.WriteString("type", "disabled");
                    json.WriteEndObject();
                    break;
                case AiThinking.Enabled:
                    json.WriteStartObject("thinking");
                    json.WriteString("type", "enabled");
                    json.WriteEndObject();
                    if (request.ReasoningEffort is { Length: > 0 } enabledEffort)
                        json.WriteString("reasoning_effort", enabledEffort);
                    break;
                case AiThinking.EffortOnly when request.ReasoningEffort is { Length: > 0 } effort:
                    json.WriteString("reasoning_effort", effort);
                    break;
            }

            json.WriteBoolean("stream", false);
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>choices[0].message.content, finish_reason and usage; anything else in the answer is ignored.</summary>
    public static AiChatResult Parse(byte[] body, TimeSpan latency, int status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return AiChatResult.Failed(AiFailure.InvalidResponse, latency, status);

            var choice = choices[0];
            var text = choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                       message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
            var finish = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
            var usage = Usage(root);
            return string.IsNullOrWhiteSpace(text)
                ? new AiChatResult(AiFailure.EmptyOutput, null, finish, usage, latency, status)
                : new AiChatResult(AiFailure.None, text, finish, usage, latency, status);
        }
        catch (JsonException)
        {
            return AiChatResult.Failed(AiFailure.InvalidResponse, latency, status);
        }
    }

    private static AiUsage Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return AiUsage.None;
        int? reasoning = usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Int(details, "reasoning_tokens")
            : null;
        int? cached = usage.TryGetProperty("prompt_tokens_details", out var prompt) && prompt.ValueKind == JsonValueKind.Object
            ? Int(prompt, "cached_tokens")
            : null;
        return new AiUsage(Int(usage, "prompt_tokens"), Int(usage, "completion_tokens"), reasoning, cached);
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>
    /// A short, fixed-alphabet error code from an error body — <c>error.type</c> (OpenAI style) or <c>type</c> — plus
    /// "RegionPolicy" when the provider says the model needs another workspace region. Never the provider's message text.
    /// </summary>
    public static string? ProviderErrorCode(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : root;
            var type = error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (message?.Contains("region", StringComparison.OrdinalIgnoreCase) == true)
                return "RegionPolicy";
            return type is null ? null : new string(type.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').Take(40).ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// <see cref="IAiChatClient"/> over OpenCode Go: one request per call, <c>stream: false</c>, no tools, no web access. Every
/// request carries a new random <see cref="OpenCodeGo.SessionHeader"/> and the key only in the Authorization header. No retry of
/// any kind: a 4xx, a 429, a 5xx, a network error or a timeout is returned as a failure. (The only resend .NET itself does is a
/// request that never left on a pooled connection the server had already closed — not a second inference.) The answer's text
/// is returned to the caller and never logged; nothing is logged here at all.
/// </summary>
/// <param name="httpClientName">The feature's named client (<see cref="OpenCodeGo.AddOpenCodeGoHttpClient"/>): its own User-Agent, the shared endpoint.</param>
public sealed class OpenCodeGoChatClient(IHttpClientFactory http, string httpClientName, OpenCodeGoApiKey key, TimeProvider clock) : IAiChatClient
{
    public bool IsConfigured => key.IsSet;

    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        if (!key.IsSet)
            return AiChatResult.Failed(AiFailure.NotConfigured, TimeSpan.Zero);

        var started = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(request.Timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("chat/completions", UriKind.Relative))
            {
                Content = new ByteArrayContent(OpenCodeGo.RequestBody(request)),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Value);
            message.Headers.TryAddWithoutValidation(OpenCodeGo.SessionHeader, Guid.NewGuid().ToString());

            using var response = await http.CreateClient(httpClientName).SendAsync(message, HttpCompletionOption.ResponseContentRead, linked.Token);
            var body = await response.Content.ReadAsByteArrayAsync(linked.Token);
            var latency = clock.GetElapsedTime(started);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var failure = OpenCodeGo.Classify(response.StatusCode);
                return AiChatResult.Failed(failure, latency, status, OpenCodeGo.ProviderErrorCode(body),
                    failure == AiFailure.RateLimited ? OpenCodeGo.RetryAfterOf(response.Headers.RetryAfter, clock.GetUtcNow()) : null);
            }

            return OpenCodeGo.Parse(body, latency, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AiChatResult.Failed(AiFailure.Timeout, clock.GetElapsedTime(started));
        }
        catch (HttpRequestException ex)
        {
            // DNS, connect, TLS, reset, or an answer over the client's buffer limit.
            return AiChatResult.Failed(AiFailure.Network, clock.GetElapsedTime(started), ex.StatusCode is { } s ? (int)s : null);
        }
    }
}
