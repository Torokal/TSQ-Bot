using System.Globalization;

namespace ToroSquad.Modules.Summary;

/// <summary>
/// Section "Summary". Nothing here is a secret: the API key comes only from <see cref="ApiKeyVariable"/> (environment
/// variable, or user-secrets in development). Defaults are the production values; every value is validated at startup.
/// </summary>
public sealed class SummaryOptions
{
    public const string Section = "Summary";

    /// <summary>The OpenCode Go API key: an environment variable (Railway Variables), or a user-secrets key of the same name.</summary>
    public const string ApiKeyVariable = "OPENCODE_GO_API_KEY";

    /// <summary>OpenCode Go's OpenAI-compatible endpoint; "chat/completions" is read relative to it.</summary>
    public string BaseUrl { get; set; } = "https://opencode.ai/zen/go/v1/";

    /// <summary>The API model id (the <c>model</c> field, without the CLI's "opencode-go/" provider prefix).</summary>
    public string Model { get; set; } = "deepseek-v4.1-flash";

    /// <summary>The most member messages one summary reads (newest first when collecting, sent oldest → newest).</summary>
    public int MaxMessages { get; set; } = 100;

    /// <summary>Fewer usable member messages than this: no AI request, a private "not enough messages" answer.</summary>
    public int MinMessages { get; set; } = 5;

    /// <summary>After a summary was produced, the same member waits this long before the next one.</summary>
    public int UserCooldownSeconds { get; set; } = 30;

    /// <summary>After a summary was produced, the same channel waits this long before the next one.</summary>
    public int ChannelCooldownSeconds { get; set; } = 60;

    /// <summary>AI requests running at the same time across the bot; more are refused at once (no queue).</summary>
    public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>The whole AI request (connect + answer). On timeout nothing is posted and nothing is retried.</summary>
    public int RequestTimeoutSeconds { get; set; } = 25;

    /// <summary>
    /// <c>max_tokens</c>. With thinking disabled it only has to hold the answer: the prompt asks for 150–250 words, which
    /// measured ~640 tokens for 100 messages.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 1200;

    /// <summary>
    /// <c>reasoning_effort</c> (low, high or max) — sent only in thinking mode, i.e. when <see cref="DisableThinking"/> is false.
    /// </summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>
    /// True: <c>thinking: {"type": "disabled"}</c> and NO <c>reasoning_effort</c> (DeepSeek: effort is a thinking-mode setting).
    /// False: <c>thinking: {"type": "enabled"}</c> plus <see cref="ReasoningEffort"/>. History (2026-09-29): "low" alone spent
    /// the whole budget on hidden reasoning (900, 2500 tokens, no text); disabled + low together still did on some requests
    /// (1200/1200, 2000/2000); disabled alone used 0 reasoning tokens, 757 answer tokens, 8.4 s, complete format.
    /// </summary>
    public bool DisableThinking { get; set; } = true;

    public double Temperature { get; set; } = 0.3;

    public double TopP { get; set; } = 0.9;

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);
    public TimeSpan UserCooldown => TimeSpan.FromSeconds(UserCooldownSeconds);
    public TimeSpan ChannelCooldown => TimeSpan.FromSeconds(ChannelCooldownSeconds);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !uri.AbsolutePath.EndsWith('/'))
            errors.Add($"{Section}:BaseUrl must be an absolute https URL ending with '/', without credentials, query or fragment (got '{BaseUrl}')");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 100 || Model.Any(char.IsWhiteSpace))
            errors.Add($"{Section}:Model must be a model id without spaces (got '{Model}')");
        if (MaxMessages is < 10 or > 200)
            errors.Add($"{Section}:MaxMessages must be 10-200 (got {MaxMessages})");
        if (MinMessages < 1 || MinMessages > MaxMessages)
            errors.Add($"{Section}:MinMessages must be 1-MaxMessages (got {MinMessages})");
        if (UserCooldownSeconds is < 0 or > 3600)
            errors.Add($"{Section}:UserCooldownSeconds must be 0-3600 (got {UserCooldownSeconds})");
        if (ChannelCooldownSeconds is < 0 or > 3600)
            errors.Add($"{Section}:ChannelCooldownSeconds must be 0-3600 (got {ChannelCooldownSeconds})");
        if (MaxConcurrentRequests is < 1 or > 10)
            errors.Add($"{Section}:MaxConcurrentRequests must be 1-10 (got {MaxConcurrentRequests})");
        if (RequestTimeoutSeconds is < 5 or > 120)
            errors.Add($"{Section}:RequestTimeoutSeconds must be 5-120 (got {RequestTimeoutSeconds})");
        if (MaxOutputTokens is < 200 or > 4000)
            errors.Add($"{Section}:MaxOutputTokens must be 200-4000 (got {MaxOutputTokens})");
        if (ReasoningEffort is not ("low" or "high" or "max"))
            errors.Add($"{Section}:ReasoningEffort must be low, high or max (got '{ReasoningEffort}')");
        if (Temperature is < 0 or > 2 || double.IsNaN(Temperature))
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:Temperature must be 0-2 (got {Temperature})"));
        if (TopP is <= 0 or > 1 || double.IsNaN(TopP))
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:TopP must be greater than 0 and at most 1 (got {TopP})"));
        return errors;
    }
}
