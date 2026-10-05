using ToroSquad.Infrastructure.Delivery;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>What the running host does with game updates.</summary>
public enum UpdatesMode
{
    /// <summary>No provider request, nothing planned, and every queued Updates card or edit is stopped (the default).</summary>
    Off = 0,

    /// <summary>Providers and classifiers run; cards go to the outbox as dry-run rows (never to Discord; own delivery state).</summary>
    DryRun = 1,

    /// <summary>Cards are delivered to the configured channel.</summary>
    Live = 2,
}

public static class UpdatesModes
{
    /// <summary>
    /// Updates:Mode, with Live reduced to DryRun while the host's Delivery:Mode does not send. Planning AND the last-moment
    /// delivery check both use this one answer, so a card queued as live is never kept waiting for a host that does not send.
    /// </summary>
    public static UpdatesMode Effective(UpdatesOptions updates, DeliveryOptions delivery) => updates.Mode switch
    {
        UpdatesMode.Off => UpdatesMode.Off,
        UpdatesMode.Live when delivery.Mode == DeliveryMode.Send => UpdatesMode.Live,
        _ => UpdatesMode.DryRun,
    };
}

/// <summary>
/// Section "Updates". Provider addresses and game ids are not configuration (a game is a registered definition); the channel
/// and the followed games are guild data (/tsq-admin modul:updates). The numbers below are project choices, not provider
/// quotas: Steam documents none for this method beyond the general Web API terms.
/// </summary>
public sealed class UpdatesOptions
{
    public const string Section = "Updates";

    public UpdatesMode Mode { get; set; } = UpdatesMode.Off;

    /// <summary>Project default between two requests for one game. A longer cache lifetime declared by the source wins.</summary>
    public int PollIntervalMinutes { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>Upper bound for the decompressed answer.</summary>
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Newest posts asked for per request (updates and other announcements together).</summary>
    public int ItemsPerRequest { get; set; } = 20;

    /// <summary>After downtime only updates published within this window can still be posted.</summary>
    public int CatchUpHours { get; set; } = 24;

    /// <summary>New cards per guild and game per round (a backlog continues on the next rounds).</summary>
    public int MaxCardsPerRound { get; set; } = 3;

    /// <summary>Titles and links are kept this long (a correction of a posted card is possible within it).</summary>
    public int TextRetentionDays { get; set; } = 90;

    /// <summary>Post ids and delivery records are kept this long; older posts stay covered by a publication-time watermark.</summary>
    public int DedupRetentionDays { get; set; } = 365;

    /// <summary>Identifies the bot to the provider.</summary>
    public string UserAgent { get; set; } = "TSQBot/0.1 UpdatesModule (+https://github.com/Torokal/TSQ-Bot)";

    public TimeSpan PollInterval => TimeSpan.FromMinutes(PollIntervalMinutes);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PollIntervalMinutes is < 5 or > 240)
            errors.Add("Updates:PollIntervalMinutes must be between 5 and 240");
        if (RequestTimeoutSeconds is < 5 or > 60)
            errors.Add("Updates:RequestTimeoutSeconds must be between 5 and 60");
        if (MaxResponseBytes is < 64 * 1024 or > 8 * 1024 * 1024)
            errors.Add("Updates:MaxResponseBytes must be between 64 KiB and 8 MiB");
        if (ItemsPerRequest is < 5 or > 50)
            errors.Add("Updates:ItemsPerRequest must be between 5 and 50");
        if (CatchUpHours is < 1 or > 72)
            errors.Add("Updates:CatchUpHours must be between 1 and 72");
        if (MaxCardsPerRound is < 1 or > 10)
            errors.Add("Updates:MaxCardsPerRound must be between 1 and 10");
        if (TextRetentionDays is < 7 or > 365)
            errors.Add("Updates:TextRetentionDays must be between 7 and 365");
        if (DedupRetentionDays < Math.Max(TextRetentionDays, 30) || DedupRetentionDays > 3650)
            errors.Add("Updates:DedupRetentionDays must be at least TextRetentionDays (and 30) and at most 3650");
        if (string.IsNullOrWhiteSpace(UserAgent) || !UserAgent.Contains("http", StringComparison.OrdinalIgnoreCase))
            errors.Add("Updates:UserAgent must name the bot and a contact URL");
        return errors;
    }
}
