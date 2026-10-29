using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Modules.Letterboxd.Domain;

namespace ToroSquad.Modules.Letterboxd.Application;

/// <summary>What the running host does with the feed.</summary>
public enum LetterboxdMode
{
    /// <summary>No feed request, nothing planned (the default).</summary>
    Off = 0,

    /// <summary>The feed is read and cards go to the outbox as dry-run rows (never to Discord; own delivery records).</summary>
    DryRun = 1,

    /// <summary>Cards are delivered to the configured channel.</summary>
    Live = 2,
}

public static class LetterboxdModes
{
    /// <summary>A host that does not send (Delivery:Mode) is a dry run for this module, also for cards already queued.</summary>
    public static LetterboxdMode Effective(LetterboxdOptions options, DeliveryOptions delivery) =>
        options.Mode == LetterboxdMode.Live && delivery.Mode != DeliveryMode.Send ? LetterboxdMode.DryRun : options.Mode;
}

/// <summary>
/// Section "Letterboxd". The followed member is project configuration (one shared feed request, whatever the number of
/// guilds); the channel is guild data (/tsq-admin modul:letterboxd islem:configure), never a code or config default. The
/// feed address is built from the validated username only — no other address is configurable.
/// </summary>
public sealed class LetterboxdOptions
{
    public const string Section = "Letterboxd";

    public LetterboxdMode Mode { get; set; } = LetterboxdMode.Off;

    /// <summary>The followed member (Letterboxd usernames: letters, digits, underscore).</summary>
    public string Username { get; set; } = "lordtoro";

    /// <summary>Shown in the card title ("🎬 LordToro değerlendirdi").</summary>
    public string DisplayName { get; set; } = "LordToro";

    /// <summary>Project default between feed checks (not a documented Letterboxd quota). A longer RSS &lt;ttl&gt; wins.</summary>
    public int PollIntervalMinutes { get; set; } = 15;

    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>Upper bound for the decompressed feed body (the observed 50-item feed is ~43 KB).</summary>
    public int MaxFeedBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>After downtime or a pause only entries published (or changed) within this window can still be sent.</summary>
    public int CatchUpHours { get; set; } = 24;

    /// <summary>New cards per guild per round (a backlog continues on the next rounds, still within the catch-up window).</summary>
    public int MaxCardsPerRound { get; set; } = 3;

    /// <summary>Review excerpt length on the card (the full review stays on Letterboxd, linked from the card).</summary>
    public int ReviewExcerptChars { get; set; } = 800;

    /// <summary>Review excerpts are kept this long after the entry was last seen in the feed.</summary>
    public int TextRetentionDays { get; set; } = 30;

    /// <summary>Entry ids and delivery records are kept this long; older ids are covered by a watermark.</summary>
    public int DedupRetentionDays { get; set; } = 365;

    /// <summary>Identifiable client for the feed requests.</summary>
    public string UserAgent { get; set; } = "TSQBot/0.1 LetterboxdModule (+https://github.com/Torokal/TSQ-Bot)";

    public TimeSpan PollInterval => TimeSpan.FromMinutes(PollIntervalMinutes);

    public string Account => Username.ToLowerInvariant();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!LetterboxdUrl.IsValidUsername(Username))
            errors.Add("Letterboxd:Username must be a Letterboxd username (2-30 letters, digits or underscores)");
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 40)
            errors.Add("Letterboxd:DisplayName must be 1-40 characters");
        if (PollIntervalMinutes is < 10 or > 240)
            errors.Add("Letterboxd:PollIntervalMinutes must be between 10 and 240");
        if (RequestTimeoutSeconds is < 5 or > 60)
            errors.Add("Letterboxd:RequestTimeoutSeconds must be between 5 and 60");
        if (MaxFeedBytes is < 64 * 1024 or > 8 * 1024 * 1024)
            errors.Add("Letterboxd:MaxFeedBytes must be between 64 KiB and 8 MiB");
        if (CatchUpHours is < 1 or > 72)
            errors.Add("Letterboxd:CatchUpHours must be between 1 and 72");
        if (MaxCardsPerRound is < 1 or > 10)
            errors.Add("Letterboxd:MaxCardsPerRound must be between 1 and 10");
        if (ReviewExcerptChars is < 100 or > 800)
            errors.Add("Letterboxd:ReviewExcerptChars must be between 100 and 800");
        if (TextRetentionDays is < 7 or > 365)
            errors.Add("Letterboxd:TextRetentionDays must be between 7 and 365");
        if (DedupRetentionDays < Math.Max(TextRetentionDays, 30) || DedupRetentionDays > 3650)
            errors.Add("Letterboxd:DedupRetentionDays must be at least TextRetentionDays (and 30) and at most 3650");
        if (string.IsNullOrWhiteSpace(UserAgent) || !UserAgent.Contains("http", StringComparison.OrdinalIgnoreCase))
            errors.Add("Letterboxd:UserAgent must name the bot and a contact URL");
        return errors;
    }
}
