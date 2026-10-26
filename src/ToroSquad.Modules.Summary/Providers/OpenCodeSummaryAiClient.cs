using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using ToroSquad.Ai;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Modules.Summary.Providers;

/// <summary>
/// <see cref="ISummaryAiClient"/> over the shared OpenCode Go transport (<see cref="OpenCodeGoChatClient"/>): this class only turns
/// a summary prompt into its exact request — the configured model, <c>thinking</c> disabled without <c>reasoning_effort</c>
/// (see <see cref="SummaryOptions.DisableThinking"/>) or the grounded profile's shape, temperature, top_p and <c>max_tokens</c>
/// — and maps the answer back. One call = one request (<see cref="IAiChatClient.CompleteAsync"/>): no retry of any kind, no
/// fallback model; a 4xx, a 429, a 5xx, a network error or a timeout is returned as a failure and the member may simply run
/// /ozetle again later. The transport sends a new random <c>x-opencode-session</c> and the honest User-Agent of this module's
/// named client (TSQ Bot's summary module). The answer's text is returned to the caller and never logged; only status, error
/// code, token counts and latency leave this class.
/// </summary>
public sealed class OpenCodeSummaryAiClient(
    IHttpClientFactory http, IOptions<SummaryOptions> options, OpenCodeGoApiKey key, TimeProvider clock) : ISummaryAiClient
{
    public const string HttpClientName = "summary-opencode";
    public const string SessionHeader = OpenCodeGo.SessionHeader;

    /// <summary>The largest answer accepted (a summary is a few KB).</summary>
    public const int MaxResponseBytes = OpenCodeGo.MaxResponseBytes;

    private readonly OpenCodeGoChatClient _transport = new(http, HttpClientName, key, clock);

    public string Model => options.Value.Model;

    public bool IsConfigured => _transport.IsConfigured;

    public async Task<SummaryAiResult> SummarizeAsync(SummaryPromptMessages prompt, CancellationToken cancellationToken) =>
        FromShared(await _transport.CompleteAsync(Request(prompt, options.Value), cancellationToken));

    /// <summary>401 authentication, 403 permission/policy, 408 the provider's own timeout, 429 a limit, 5xx an outage, other 4xx the request or its settings.</summary>
    public static SummaryAiFailure Classify(HttpStatusCode status) => (SummaryAiFailure)(int)OpenCodeGo.Classify(status);

    /// <summary>The longest Retry-After shown to a member; a longer one is shown as this.</summary>
    public static readonly TimeSpan MaxRetryAfter = OpenCodeGo.MaxRetryAfter;

    /// <summary>A 429's Retry-After (seconds or an HTTP date) as a wait from now; null when absent, invalid or already past.</summary>
    public static TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header, DateTimeOffset now) => OpenCodeGo.RetryAfterOf(header, now);

    /// <summary>
    /// The request of one summary prompt: the two prompt messages and the generation settings. Nothing else (no tools, no user
    /// id). Thinking off is thinking.type=disabled ALONE; a request with its own profile uses that profile's shape: either the
    /// same "disabled" object, or reasoning_effort without any thinking object for a model that only lists effort levels.
    /// </summary>
    public static AiChatRequest Request(SummaryPromptMessages prompt, SummaryOptions settings)
    {
        var (thinking, effort) = prompt.Profile is { } profile
            ? (profile.Thinking == SummaryThinking.Disabled ? AiThinking.Disabled : AiThinking.EffortOnly, profile.ReasoningEffort)
            : settings.DisableThinking ? (AiThinking.Disabled, (string?)null) : (AiThinking.Enabled, settings.ReasoningEffort);
        return new AiChatRequest(
            prompt.Profile?.Model ?? settings.Model,
            [new AiMessage(AiRole.System, prompt.System), new AiMessage(AiRole.User, prompt.User)],
            prompt.MaxOutputTokens ?? settings.MaxOutputTokens,
            settings.Temperature,
            settings.TopP,
            thinking,
            effort,
            // Each request has its own deadline: the legacy one, or the one its profile carries (grounded generator / reviewer).
            prompt.Profile?.RequestTimeout ?? settings.RequestTimeout);
    }

    /// <summary>The request body exactly as sent (the shared wire format of <see cref="Request"/>).</summary>
    public static byte[] RequestBody(SummaryPromptMessages prompt, SummaryOptions settings) => OpenCodeGo.RequestBody(Request(prompt, settings));

    /// <summary>choices[0].message.content, finish_reason and usage; anything else in the answer is ignored.</summary>
    public static SummaryAiResult Parse(byte[] body, TimeSpan latency, int status) => FromShared(OpenCodeGo.Parse(body, latency, status));

    /// <summary>A short, fixed-alphabet error code from an error body (never the provider's message text).</summary>
    public static string? ProviderErrorCode(byte[] body) => OpenCodeGo.ProviderErrorCode(body);

    /// <summary>The shared result as this module's own (same failure categories, same values).</summary>
    public static SummaryAiResult FromShared(AiChatResult result) => new(
        (SummaryAiFailure)(int)result.Failure,
        result.Text,
        result.FinishReason,
        result.Usage.InputTokens is null && result.Usage.OutputTokens is null && result.Usage.ReasoningTokens is null
            ? SummaryAiUsage.None
            : new SummaryAiUsage(result.Usage.InputTokens, result.Usage.OutputTokens, result.Usage.ReasoningTokens),
        result.Latency,
        result.HttpStatus,
        result.ProviderError,
        result.RetryAfter);
}
