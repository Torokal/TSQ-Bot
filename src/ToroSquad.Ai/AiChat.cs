namespace ToroSquad.Ai;

/// <summary>Why a chat request produced no answer. A category for logs and member messages; never retried here.</summary>
public enum AiFailure
{
    None = 0,

    /// <summary>No API key configured: no request was sent.</summary>
    NotConfigured = 1,

    /// <summary>No answer within the request's own deadline, or HTTP 408 from the provider.</summary>
    Timeout = 2,

    /// <summary>DNS, connect, TLS, a dropped connection or an answer over the size limit.</summary>
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

/// <summary>Token counts as the provider reported them (null when it did not report a value — never assumed to be zero).</summary>
/// <param name="InputTokens"><c>usage.prompt_tokens</c>.</param>
/// <param name="OutputTokens"><c>usage.completion_tokens</c> (includes reasoning tokens).</param>
/// <param name="ReasoningTokens"><c>usage.completion_tokens_details.reasoning_tokens</c>.</param>
/// <param name="CachedInputTokens"><c>usage.prompt_tokens_details.cached_tokens</c>.</param>
public sealed record AiUsage(int? InputTokens, int? OutputTokens, int? ReasoningTokens, int? CachedInputTokens = null)
{
    public static AiUsage None { get; } = new(null, null, null);

    /// <summary>True when the provider reported at least the input and output counts.</summary>
    public bool IsKnown => InputTokens is not null && OutputTokens is not null;
}

/// <summary>
/// The outcome of ONE request. <paramref name="ProviderError"/> is a short provider error code (never the message text of a
/// request or answer); <paramref name="HttpStatus"/> the status when there was one; <paramref name="RetryAfter"/> the wait a
/// 429 asked for (shown or honoured by the caller — nothing here retries).
/// </summary>
public sealed record AiChatResult(
    AiFailure Failure,
    string? Text,
    string? FinishReason,
    AiUsage Usage,
    TimeSpan Latency,
    int? HttpStatus = null,
    string? ProviderError = null,
    TimeSpan? RetryAfter = null)
{
    public bool Succeeded => Failure == AiFailure.None;

    /// <summary>The provider stopped because of the output limit (<c>finish_reason</c> "length"): the text is cut.</summary>
    public bool Truncated => string.Equals(FinishReason, "length", StringComparison.Ordinal);

    public static AiChatResult Failed(AiFailure failure, TimeSpan latency, int? status = null, string? providerError = null, TimeSpan? retryAfter = null) =>
        new(failure, null, null, AiUsage.None, latency, status, providerError, retryAfter);
}

/// <summary>How one request tells the model about hidden reasoning. Each shape was checked against the gateway for its model.</summary>
public enum AiThinking
{
    /// <summary><c>thinking: {"type": "disabled"}</c> and no <c>reasoning_effort</c> (the DeepSeek shape).</summary>
    Disabled = 0,

    /// <summary><c>thinking: {"type": "enabled"}</c> plus <c>reasoning_effort</c>.</summary>
    Enabled = 1,

    /// <summary>No <c>thinking</c> object, <c>reasoning_effort</c> only (models that list effort levels but no on/off switch).</summary>
    EffortOnly = 2,
}

/// <summary>A chat message role on the wire.</summary>
public enum AiRole
{
    System = 0,
    User = 1,
    Assistant = 2,
}

public sealed record AiMessage(AiRole Role, string Content);

/// <summary>
/// One complete chat request: the exact model, messages and generation settings, and that request's own deadline. Nothing
/// here is a fallback: a request has exactly one model and is sent once. <paramref name="SessionId"/> is the
/// <see cref="OpenCodeGo.SessionHeader"/> value for a feature that keeps a conversation (stable for that conversation,
/// random, never an id, name or text — see <see cref="OpenCodeGo.IsValidSessionId"/>); null sends a new random GUID, as
/// one-off requests always did. It is routing/caching information only: the provider keeps no conversation for TSQ.
/// </summary>
public sealed record AiChatRequest(
    string Model,
    IReadOnlyList<AiMessage> Messages,
    int MaxOutputTokens,
    double Temperature,
    double TopP,
    AiThinking Thinking,
    string? ReasoningEffort,
    TimeSpan Timeout,
    string? SessionId = null);

/// <summary>
/// One chat completion. Implementations send exactly one inference request per call — no retry, no fallback model — and never
/// throw for provider problems (every failure is an <see cref="AiChatResult"/>).
/// </summary>
public interface IAiChatClient
{
    /// <summary>False when no API key is configured: callers answer "not configured" without any request.</summary>
    bool IsConfigured { get; }

    Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken);
}
