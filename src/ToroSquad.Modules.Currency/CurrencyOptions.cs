namespace ToroSquad.Modules.Currency;

/// <summary>
/// Section "Currency". Public feeds without API keys: nothing here is a secret. Defaults are the production values; every
/// value is validated at startup (a typo in a provider URL stops the bot with a clear CONFIG line instead of failing every
/// command later).
/// </summary>
public sealed class CurrencyOptions
{
    public const string Section = "Currency";

    /// <summary>The main guild's currency channel (production default).</summary>
    public const ulong DefaultChannelId = 1242464361855848459;

    /// <summary>Smallest id Discord can issue (timestamp bits above the 22 worker/process/increment bits).</summary>
    public const ulong MinSnowflake = 1UL << 22;

    /// <summary>
    /// The only channel where /dolar, /euro and /altın answer, and where the daily 09:00 card is posted. Fixed by
    /// configuration (no admin command, no table).
    /// </summary>
    public ulong ChannelId { get; set; } = DefaultChannelId;

    /// <summary>
    /// The daily card is due at 09:00 Türkiye time; a bot that was down at 09:00 still posts it until this many minutes
    /// later, never after (no late "morning" card in the afternoon).
    /// </summary>
    public int DailyCatchUpMinutes { get; set; } = 30;

    /// <summary>Altınkaynak public web service; "Currency" and "Gold" are read relative to it.</summary>
    public string AltinkaynakBaseUrl { get; set; } = "https://static.altinkaynak.com/public/";

    /// <summary>TCMB exchange rates; "today.xml" is read relative to it.</summary>
    public string TcmbBaseUrl { get; set; } = "https://www.tcmb.gov.tr/kurlar/";

    /// <summary>Trunçgil Finance v4; "today.json" is read relative to it.</summary>
    public string TruncgilBaseUrl { get; set; } = "https://finans.truncgil.com/v4/";

    /// <summary>Per request (connect + response). Short: a slow provider must not hold the answer back.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>A primary provider answer is reused this long (every command inside it is served from memory).</summary>
    public int FreshSeconds { get; set; } = 60;

    /// <summary>
    /// A fallback answer is reused this long, and a failed provider is skipped this long before it is asked again — so the
    /// primary is retried soon after it recovers.
    /// </summary>
    public int FallbackFreshSeconds { get; set; } = 30;

    /// <summary>When every provider fails, the last good price is shown (marked stale) only while it is at most this old.</summary>
    public int StaleMaxMinutes { get; set; } = 15;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
    public TimeSpan Fresh => TimeSpan.FromSeconds(FreshSeconds);
    public TimeSpan FallbackFresh => TimeSpan.FromSeconds(FallbackFreshSeconds);
    public TimeSpan StaleMax => TimeSpan.FromMinutes(StaleMaxMinutes);
    public TimeSpan DailyCatchUp => TimeSpan.FromMinutes(DailyCatchUpMinutes);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (name, url) in new[] { (nameof(AltinkaynakBaseUrl), AltinkaynakBaseUrl), (nameof(TcmbBaseUrl), TcmbBaseUrl), (nameof(TruncgilBaseUrl), TruncgilBaseUrl) })
        {
            if (!IsBaseUrl(url))
                errors.Add($"{Section}:{name} must be an absolute https URL ending with '/', without credentials, query or fragment (got '{url}')");
        }

        if (TimeoutSeconds is < 1 or > 30)
            errors.Add($"{Section}:TimeoutSeconds must be 1-30 (got {TimeoutSeconds})");
        if (FreshSeconds is < 10 or > 3600)
            errors.Add($"{Section}:FreshSeconds must be 10-3600 (got {FreshSeconds})");
        if (FallbackFreshSeconds < 5 || FallbackFreshSeconds > FreshSeconds)
            errors.Add($"{Section}:FallbackFreshSeconds must be 5-FreshSeconds (got {FallbackFreshSeconds})");
        if (ChannelId is < MinSnowflake or > long.MaxValue)
            errors.Add($"{Section}:ChannelId must be a Discord channel id (got {ChannelId})");
        if (DailyCatchUpMinutes is < 1 or > 180)
            errors.Add($"{Section}:DailyCatchUpMinutes must be 1-180 (got {DailyCatchUpMinutes})");
        if (StaleMaxMinutes is < 1 or > 60 || StaleMax < Fresh)
            errors.Add($"{Section}:StaleMaxMinutes must be 1-60 and not shorter than FreshSeconds (got {StaleMaxMinutes})");
        return errors;
    }

    private static bool IsBaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath.EndsWith('/');
}
