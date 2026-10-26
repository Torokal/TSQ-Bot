namespace ToroSquad.Modules.Summary.Application;

/// <summary>Why an AI request produced no summary. Logged as a category; never retried.</summary>
public enum SummaryAiFailure
{
    None = 0,

    /// <summary>No API key configured: no request was sent.</summary>
    NotConfigured = 1,

    /// <summary>No answer within <see cref="SummaryOptions.RequestTimeoutSeconds"/>, or HTTP 408 from the provider.</summary>
    Timeout = 2,

    /// <summary>DNS, connect, TLS or a dropped connection.</summary>
    Network = 3,

    /// <summary>HTTP 429: a request or usage limit (with the provider's Retry-After when it sent one). Not a key problem.</summary>
    RateLimited = 4,

    /// <summary>HTTP 5xx.</summary>
    ServerError = 5,

    /// <summary>HTTP 401: authentication refused — the key is invalid, expired or revoked.</summary>
    Unauthorized = 6,

    /// <summary>Any other 4xx (not 401, 403, 408, 429): unknown model, invalid request, region or plan setting.</summary>
    Rejected = 7,

    /// <summary>A success status whose body is not the expected JSON.</summary>
    InvalidResponse = 8,

    /// <summary>A valid answer without any text.</summary>
    EmptyOutput = 9,

    /// <summary>HTTP 403: the request is outside the key's permissions or an account/provider policy — not necessarily a bad key.</summary>
    Forbidden = 10,
}

/// <summary>Token counts as the provider reported them (null when it did not).</summary>
/// <param name="InputTokens"><c>usage.prompt_tokens</c>.</param>
/// <param name="OutputTokens"><c>usage.completion_tokens</c> (includes reasoning tokens).</param>
/// <param name="ReasoningTokens"><c>usage.completion_tokens_details.reasoning_tokens</c>.</param>
public sealed record SummaryAiUsage(int? InputTokens, int? OutputTokens, int? ReasoningTokens)
{
    public static SummaryAiUsage None { get; } = new(null, null, null);
}

/// <summary>
/// The outcome of the one request. <paramref name="ProviderError"/> is a short provider error code (never the message text of
/// a request or answer); <paramref name="HttpStatus"/> the status when there was one; <paramref name="RetryAfter"/> the wait a
/// 429 asked for (only shown to the member — nothing retries).
/// </summary>
public sealed record SummaryAiResult(
    SummaryAiFailure Failure,
    string? Text,
    string? FinishReason,
    SummaryAiUsage Usage,
    TimeSpan Latency,
    int? HttpStatus = null,
    string? ProviderError = null,
    TimeSpan? RetryAfter = null)
{
    public bool Succeeded => Failure == SummaryAiFailure.None;

    public static SummaryAiResult Failed(SummaryAiFailure failure, TimeSpan latency, int? status = null, string? providerError = null, TimeSpan? retryAfter = null) =>
        new(failure, null, null, SummaryAiUsage.None, latency, status, providerError, retryAfter);
}

/// <summary>How one request tells the model about hidden reasoning. The two shapes were each checked against the gateway.</summary>
public enum SummaryThinking
{
    /// <summary><c>thinking: {"type": "disabled"}</c> and no <c>reasoning_effort</c> (the DeepSeek shape).</summary>
    Disabled = 0,

    /// <summary>No <c>thinking</c> object, <c>reasoning_effort</c> only (models that list effort levels but no on/off switch).</summary>
    EffortOnly = 1,
}

/// <summary>
/// The model and reasoning shape of ONE request, when it is not the configured legacy request: the grounded mode's generator
/// and reviewer each carry their own. <paramref name="RequestTimeout"/> is that request's own deadline (null keeps
/// <see cref="SummaryOptions.RequestTimeoutSeconds"/>). Nothing here is a fallback: a request has exactly one profile and
/// is sent once.
/// </summary>
public sealed record SummaryAiProfile(string Model, SummaryThinking Thinking, string? ReasoningEffort, TimeSpan? RequestTimeout = null);

/// <summary>
/// One summary request to one configured model. Implementations send exactly one inference request per call — no retry, no
/// fallback model — and never throw for provider problems (every failure is a <see cref="SummaryAiResult"/>).
/// </summary>
public interface ISummaryAiClient
{
    /// <summary>The configured model id (for logs).</summary>
    string Model { get; }

    /// <summary>False when no API key is configured: /ozetle answers "not configured" without any request.</summary>
    bool IsConfigured { get; }

    Task<SummaryAiResult> SummarizeAsync(SummaryPromptMessages prompt, CancellationToken cancellationToken);
}
