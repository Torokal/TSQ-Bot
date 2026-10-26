using System.Globalization;
using ToroSquad.Ai;

namespace ToroSquad.Modules.Sor;

/// <summary>
/// Section "Sor". Nothing here is a secret: the API key is the shared <see cref="OpenCodeGo.ApiKeyVariable"/>. The model
/// profile is explicit and starts as TSQ Özet's single-request legacy profile (same model, thinking disabled without
/// <c>reasoning_effort</c>, temperature, top_p, output budget, timeout) — copied, not read from Özet, so neither module can
/// change the other's model silently. Limits are starting product settings (not the provider's plan); the per-guild ones
/// can be lowered or raised within <see cref="SorLimits"/> bounds by /tsq-admin modul:sor. Validated at startup.
/// </summary>
public sealed class SorOptions
{
    public const string Section = "Sor";

    /// <summary>The shared OpenCode Go endpoint (the same default as TSQ Özet).</summary>
    public string BaseUrl { get; set; } = OpenCodeGo.DefaultBaseUrl;

    /// <summary>The API model id. Members can never choose it; an operator changes it only here (configuration + restart).</summary>
    public string Model { get; set; } = "deepseek-v4.1-flash";

    /// <summary>
    /// True (default): <c>thinking: {"type": "disabled"}</c> and NO <c>reasoning_effort</c> — the shape checked for this model
    /// (TSQ Özet, 2026-09-29). False: <c>thinking: enabled</c> plus <see cref="ReasoningEffort"/>.
    /// </summary>
    public bool DisableThinking { get; set; } = true;

    /// <summary><c>reasoning_effort</c> (low, high or max) — sent only when <see cref="DisableThinking"/> is false.</summary>
    public string ReasoningEffort { get; set; } = "low";

    public double Temperature { get; set; } = 0.3;

    public double TopP { get; set; } = 0.9;

    /// <summary><c>max_tokens</c>: the answer AND the memory candidates together.</summary>
    public int MaxOutputTokens { get; set; } = 1200;

    /// <summary>The whole request (connect + answer). On timeout nothing is posted and nothing is retried.</summary>
    public int RequestTimeoutSeconds { get; set; } = 25;

    /// <summary>
    /// The input budget in tokens (system + history + context + question). There is no exact tokenizer here: the estimate is
    /// conservative (<see cref="CharsPerToken"/> characters per token) and the oldest turns, then the least relevant notes,
    /// are left out first.
    /// </summary>
    public int MaxInputTokens { get; set; } = 6000;

    /// <summary>The longest question (Discord's own option and modal limits are set to the same value).</summary>
    public int MaxQuestionLength { get; set; } = 4000;

    /// <summary>Generations running at the same time across the bot; more are refused at once (no queue).</summary>
    public int MaxConcurrentRequests { get; set; } = 1;

    /// <summary>Default member cooldown between two questions (per guild; /tsq-admin can change it).</summary>
    public int UserCooldownSeconds { get; set; } = 10;

    /// <summary>Default generations per member per day (guild time zone day).</summary>
    public int UserDailyLimit { get; set; } = 30;

    /// <summary>Default generations per guild per day (guild time zone day).</summary>
    public int GuildDailyLimit { get; set; } = 300;

    /// <summary>Default most recent completed question/answer pairs sent as context (and the most a conversation keeps).</summary>
    public int MaxContextTurns { get; set; } = 8;

    /// <summary>Default inactivity after which the next /sor starts a new conversation.</summary>
    public int ConversationIdleMinutes { get; set; } = 60;

    /// <summary>Default retention of conversations and their turns, counted from the last activity.</summary>
    public int HistoryRetentionHours { get; set; } = 24;

    /// <summary>The most remembered notes a member has in one guild.</summary>
    public int MaxMemoryItems { get; set; } = 30;

    /// <summary>The longest remembered note.</summary>
    public int MaxMemoryItemLength { get; set; } = 240;

    /// <summary>The most notes put into one request (the asker's and a mentioned member's shared ones together).</summary>
    public int MaxMemoryItemsPerRequest { get; set; } = 6;

    /// <summary>Conservative characters-per-token estimate for Turkish and English text.</summary>
    public const int CharsPerToken = 3;

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);

    public SorLimits DefaultLimits => new(UserCooldownSeconds, UserDailyLimit, GuildDailyLimit, MaxContextTurns, ConversationIdleMinutes, HistoryRetentionHours);

    /// <summary>The one request profile of /sor.</summary>
    public AiChatRequest Request(IReadOnlyList<AiMessage> messages) => new(
        Model, messages, MaxOutputTokens, Temperature, TopP,
        DisableThinking ? AiThinking.Disabled : AiThinking.Enabled,
        DisableThinking ? null : ReasoningEffort,
        RequestTimeout);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!OpenCodeGo.IsValidBaseUrl(BaseUrl))
            errors.Add($"{Section}:BaseUrl must be an absolute https URL ending with '/', without credentials, query or fragment (got '{BaseUrl}')");
        if (!OpenCodeGo.IsValidModel(Model))
            errors.Add($"{Section}:Model must be a model id without spaces (got '{Model}')");
        if (ReasoningEffort is not ("low" or "high" or "max"))
            errors.Add($"{Section}:ReasoningEffort must be low, high or max (got '{ReasoningEffort}')");
        if (Temperature is < 0 or > 2 || double.IsNaN(Temperature))
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:Temperature must be 0-2 (got {Temperature})"));
        if (TopP is <= 0 or > 1 || double.IsNaN(TopP))
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:TopP must be greater than 0 and at most 1 (got {TopP})"));
        Range(errors, nameof(MaxOutputTokens), MaxOutputTokens, 200, 4000);
        Range(errors, nameof(RequestTimeoutSeconds), RequestTimeoutSeconds, 5, 120);
        Range(errors, nameof(MaxInputTokens), MaxInputTokens, 1500, 32000);
        Range(errors, nameof(MaxQuestionLength), MaxQuestionLength, 100, 4000);
        Range(errors, nameof(MaxConcurrentRequests), MaxConcurrentRequests, 1, 5);
        Range(errors, nameof(MaxMemoryItems), MaxMemoryItems, 1, 100);
        Range(errors, nameof(MaxMemoryItemLength), MaxMemoryItemLength, 40, 500);
        Range(errors, nameof(MaxMemoryItemsPerRequest), MaxMemoryItemsPerRequest, 0, 20);
        Range(errors, nameof(HistoryRetentionHours), HistoryRetentionHours, 1, SorLimits.MaxHistoryHours);
        foreach (var problem in DefaultLimits.Problems())
            errors.Add($"{Section}:{problem}");
        return errors;
    }

    private static void Range(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
            errors.Add($"{Section}:{name} must be {min}-{max} (got {value})");
    }
}

/// <summary>
/// The limits one guild runs with: the configured defaults, or the guild's own values from /tsq-admin. Bounds are fixed in
/// code so a typo cannot open an unlimited budget.
/// </summary>
public sealed record SorLimits(int UserCooldownSeconds, int UserDailyLimit, int GuildDailyLimit, int MaxContextTurns, int IdleMinutes, int HistoryHours)
{
    public const int MaxCooldownSeconds = 3600;
    public const int MaxUserDaily = 500;
    public const int MaxGuildDaily = 5000;
    public const int MaxTurns = 8;
    public const int MinIdleMinutes = 5;
    public const int MaxIdleMinutes = 24 * 60;
    public const int MaxHistoryHours = 24;

    public TimeSpan Cooldown => TimeSpan.FromSeconds(UserCooldownSeconds);
    public TimeSpan Idle => TimeSpan.FromMinutes(IdleMinutes);
    public TimeSpan History => TimeSpan.FromHours(HistoryHours);

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (UserCooldownSeconds is < 0 or > MaxCooldownSeconds)
            problems.Add($"UserCooldownSeconds must be 0-{MaxCooldownSeconds} (got {UserCooldownSeconds})");
        if (UserDailyLimit is < 1 or > MaxUserDaily)
            problems.Add($"UserDailyLimit must be 1-{MaxUserDaily} (got {UserDailyLimit})");
        if (GuildDailyLimit is < 1 or > MaxGuildDaily)
            problems.Add($"GuildDailyLimit must be 1-{MaxGuildDaily} (got {GuildDailyLimit})");
        if (UserDailyLimit > GuildDailyLimit)
            problems.Add($"UserDailyLimit must not exceed GuildDailyLimit (got {UserDailyLimit} > {GuildDailyLimit})");
        if (MaxContextTurns is < 0 or > MaxTurns)
            problems.Add($"MaxContextTurns must be 0-{MaxTurns} (got {MaxContextTurns})");
        if (IdleMinutes is < MinIdleMinutes or > MaxIdleMinutes)
            problems.Add($"ConversationIdleMinutes must be {MinIdleMinutes}-{MaxIdleMinutes} (got {IdleMinutes})");
        if (HistoryHours is < 1 or > MaxHistoryHours)
            problems.Add($"HistoryRetentionHours must be 1-{MaxHistoryHours} (got {HistoryHours})");
        if (IdleMinutes > HistoryHours * 60)
            problems.Add($"ConversationIdleMinutes must not exceed the history retention (got {IdleMinutes} min > {HistoryHours} h)");
        return problems;
    }
}
