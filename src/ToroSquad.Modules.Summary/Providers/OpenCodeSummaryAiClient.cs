using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Modules.Summary.Providers;

/// <summary>The OpenCode Go API key (<see cref="SummaryOptions.ApiKeyVariable"/>). Never printed: <see cref="ToString"/> hides it.</summary>
public sealed class SummaryApiKey(string? value)
{
    public string? Value { get; } = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public bool IsSet => Value is not null;

    public override string ToString() => IsSet ? "***" : "(not set)";
}

/// <summary>
/// <see cref="ISummaryAiClient"/> over OpenCode Go's OpenAI-compatible <c>POST chat/completions</c>: one request per summary,
/// <c>stream: false</c>, no tools, no web access, the configured model, <c>thinking</c> disabled without
/// <c>reasoning_effort</c> (see <see cref="SummaryOptions.DisableThinking"/>), temperature, top_p and <c>max_tokens</c>. Every
/// request carries a new random <c>x-opencode-session</c> (a GUID — nothing about the guild, channel, members or text) and an honest User-Agent (TSQ Bot's summary module). No retry of any kind here: a 4xx, a 429, a 5xx, a
/// network error or a timeout is returned as a failure and the member may simply run /ozetle again later. (The only resend
/// .NET itself does is a request that never left on a pooled connection the server had already closed — not a second
/// inference.) The answer's text is returned to the caller and never logged; only status, error code, token counts and
/// latency leave this class.
/// </summary>
public sealed class OpenCodeSummaryAiClient(
    IHttpClientFactory http, IOptions<SummaryOptions> options, SummaryApiKey key, TimeProvider clock) : ISummaryAiClient
{
    public const string HttpClientName = "summary-opencode";
    public const string SessionHeader = "x-opencode-session";

    /// <summary>The largest answer accepted (a summary is a few KB).</summary>
    public const int MaxResponseBytes = 512 * 1024;

    public string Model => options.Value.Model;

    public bool IsConfigured => key.IsSet;

    public async Task<SummaryAiResult> SummarizeAsync(SummaryPromptMessages prompt, CancellationToken cancellationToken)
    {
        if (!key.IsSet)
            return SummaryAiResult.Failed(SummaryAiFailure.NotConfigured, TimeSpan.Zero);

        var settings = options.Value;
        var started = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(settings.RequestTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("chat/completions", UriKind.Relative))
            {
                Content = new ByteArrayContent(RequestBody(prompt, settings)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Value);
            request.Headers.TryAddWithoutValidation(SessionHeader, Guid.NewGuid().ToString());

            using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);
            var body = await response.Content.ReadAsByteArrayAsync(linked.Token);
            var latency = clock.GetElapsedTime(started);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return SummaryAiResult.Failed(Classify(response.StatusCode), latency, status, ProviderErrorCode(body));
            return Parse(body, latency, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SummaryAiResult.Failed(SummaryAiFailure.Timeout, clock.GetElapsedTime(started));
        }
        catch (HttpRequestException ex)
        {
            // DNS, connect, TLS, reset, or an answer over the client's buffer limit.
            return SummaryAiResult.Failed(SummaryAiFailure.Network, clock.GetElapsedTime(started), ex.StatusCode is { } s ? (int)s : null);
        }
    }

    public static SummaryAiFailure Classify(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => SummaryAiFailure.Unauthorized,
        429 => SummaryAiFailure.RateLimited,
        >= 500 => SummaryAiFailure.ServerError,
        _ => SummaryAiFailure.Rejected,
    };

    /// <summary>The request body: the two prompt messages and the generation settings. Nothing else (no tools, no user id).</summary>
    public static byte[] RequestBody(SummaryPromptMessages prompt, SummaryOptions settings)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("model", settings.Model);
            json.WriteStartArray("messages");
            json.WriteStartObject();
            json.WriteString("role", "system");
            json.WriteString("content", prompt.System);
            json.WriteEndObject();
            json.WriteStartObject();
            json.WriteString("role", "user");
            json.WriteString("content", prompt.User);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteNumber("max_tokens", prompt.MaxOutputTokens ?? settings.MaxOutputTokens);
            json.WriteNumber("temperature", settings.Temperature);
            json.WriteNumber("top_p", settings.TopP);
            // Thinking off is thinking.type=disabled ALONE: reasoning_effort is a thinking-mode setting, and sending both was a
            // contradictory request that some upstream backends answered with unbounded hidden reasoning.
            json.WriteStartObject("thinking");
            json.WriteString("type", settings.DisableThinking ? "disabled" : "enabled");
            json.WriteEndObject();
            if (!settings.DisableThinking)
                json.WriteString("reasoning_effort", settings.ReasoningEffort);

            json.WriteBoolean("stream", false);
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>choices[0].message.content, finish_reason and usage; anything else in the answer is ignored.</summary>
    public static SummaryAiResult Parse(byte[] body, TimeSpan latency, int status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return SummaryAiResult.Failed(SummaryAiFailure.InvalidResponse, latency, status);

            var choice = choices[0];
            var text = choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                       message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
            var finish = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
            var usage = Usage(root);
            return string.IsNullOrWhiteSpace(text)
                ? new SummaryAiResult(SummaryAiFailure.EmptyOutput, null, finish, usage, latency, status)
                : new SummaryAiResult(SummaryAiFailure.None, text, finish, usage, latency, status);
        }
        catch (JsonException)
        {
            return SummaryAiResult.Failed(SummaryAiFailure.InvalidResponse, latency, status);
        }
    }

    private static SummaryAiUsage Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return SummaryAiUsage.None;
        int? reasoning = usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Int(details, "reasoning_tokens")
            : null;
        return new SummaryAiUsage(Int(usage, "prompt_tokens"), Int(usage, "completion_tokens"), reasoning);
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
