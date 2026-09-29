namespace ToroSquad.Modules.Summary.Application;

/// <summary>Why an AI request produced no summary. Logged as a category; never retried.</summary>
public enum SummaryAiFailure
{
    None = 0,

    /// <summary>No API key configured: no request was sent.</summary>
    NotConfigured = 1,

    /// <summary>No answer within <see cref="SummaryOptions.RequestTimeoutSeconds"/>.</summary>
    Timeout = 2,

    /// <summary>DNS, connect, TLS or a dropped connection.</summary>
    Network = 3,

    /// <summary>HTTP 429.</summary>
    RateLimited = 4,

    /// <summary>HTTP 5xx.</summary>
    ServerError = 5,

    /// <summary>HTTP 401/403: the key or the account's access (e.g. the workspace region setting).</summary>
    Unauthorized = 6,

    /// <summary>Any other 4xx: unknown model, invalid request, provider policy.</summary>
    Rejected = 7,

    /// <summary>A success status whose body is not the expected JSON.</summary>
    InvalidResponse = 8,

    /// <summary>A valid answer without any text.</summary>
    EmptyOutput = 9,
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
/// a request or answer); <paramref name="HttpStatus"/> the status when there was one.
/// </summary>
public sealed record SummaryAiResult(
    SummaryAiFailure Failure,
    string? Text,
    string? FinishReason,
    SummaryAiUsage Usage,
    TimeSpan Latency,
    int? HttpStatus = null,
    string? ProviderError = null)
{
    public bool Succeeded => Failure == SummaryAiFailure.None;

    public static SummaryAiResult Failed(SummaryAiFailure failure, TimeSpan latency, int? status = null, string? providerError = null) =>
        new(failure, null, null, SummaryAiUsage.None, latency, status, providerError);
}

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
