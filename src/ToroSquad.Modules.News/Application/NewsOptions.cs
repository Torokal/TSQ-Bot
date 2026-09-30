using System.Globalization;

namespace ToroSquad.Modules.News.Application;

/// <summary>What the running host does with the news feed.</summary>
public enum NewsMode
{
    /// <summary>No feed request, no roster request, nothing planned (the default).</summary>
    Off = 0,

    /// <summary>Feed and matching run; cards go to the outbox as dry-run rows (never to Discord; own delivery state).</summary>
    DryRun = 1,

    /// <summary>Cards are delivered to the configured channel.</summary>
    Live = 2,
}

/// <summary>
/// Section "News". The feed address is not configurable (only the official HLTV RSS news feed is ever requested); the
/// target team, its excluded look-alikes and the dated starting roster are project data kept here so they can be corrected
/// without a code change. The channel is guild data (/tsq-admin modul:news islem:configure), not configuration.
/// </summary>
public sealed class NewsOptions
{
    public const string Section = "News";

    public NewsMode Mode { get; set; } = NewsMode.Off;

    /// <summary>Project default between feed checks. The feed's own RSS &lt;ttl&gt; wins when it is longer.</summary>
    public int PollIntervalMinutes { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>Upper bound for the decompressed feed body.</summary>
    public int MaxFeedBytes { get; set; } = 1024 * 1024;

    /// <summary>After downtime or a pause only news published within this window can still be sent.</summary>
    public int CatchUpHours { get; set; } = 6;

    /// <summary>New cards per guild per round (a backlog continues on the next rounds).</summary>
    public int MaxCardsPerRound { get; set; } = 3;

    /// <summary>Headlines are kept this long (edits of a posted card are possible within it).</summary>
    public int TextRetentionDays { get; set; } = 30;

    /// <summary>Article ids and delivery records are kept this long; older ids are covered by a watermark.</summary>
    public int DedupRetentionDays { get; set; } = 365;

    /// <summary>Contact User-Agent for the feed and Liquipedia (their terms ask for an identifiable client).</summary>
    public string UserAgent { get; set; } = "TSQBot/0.1 NewsModule (+https://github.com/Torokal/TSQ-Bot)";

    public NewsTeamOptions Team { get; set; } = new();

    public NewsRosterOptions Roster { get; set; } = new();

    public TimeSpan PollInterval => TimeSpan.FromMinutes(PollIntervalMinutes);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PollIntervalMinutes is < 5 or > 240)
            errors.Add("News:PollIntervalMinutes must be between 5 and 240");
        if (RequestTimeoutSeconds is < 5 or > 60)
            errors.Add("News:RequestTimeoutSeconds must be between 5 and 60");
        if (MaxFeedBytes is < 16 * 1024 or > 8 * 1024 * 1024)
            errors.Add("News:MaxFeedBytes must be between 16 KiB and 8 MiB");
        if (CatchUpHours is < 1 or > 48)
            errors.Add("News:CatchUpHours must be between 1 and 48");
        if (MaxCardsPerRound is < 1 or > 10)
            errors.Add("News:MaxCardsPerRound must be between 1 and 10");
        if (TextRetentionDays is < 7 or > 365)
            errors.Add("News:TextRetentionDays must be between 7 and 365");
        if (DedupRetentionDays < Math.Max(TextRetentionDays, 30) || DedupRetentionDays > 3650)
            errors.Add("News:DedupRetentionDays must be at least TextRetentionDays (and 30) and at most 3650");
        if (string.IsNullOrWhiteSpace(UserAgent) || !UserAgent.Contains("http", StringComparison.OrdinalIgnoreCase))
            errors.Add("News:UserAgent must name the bot and a contact URL");
        errors.AddRange(Team.Validate());
        errors.AddRange(Roster.Validate());
        return errors;
    }
}

/// <summary>The one target team. Matching uses names only: the RSS feed carries no team ids or tags.</summary>
public sealed class NewsTeamOptions
{
    /// <summary>Shown on the card ("📰 Aurora — HLTV").</summary>
    public string DisplayName { get; set; } = "Aurora";

    /// <summary>Names that mean the main CS2 team (whole words, case and accent insensitive).</summary>
    public string[] Aliases { get; set; } = ["Aurora", "Aurora Gaming"];

    /// <summary>Other teams whose names contain an alias; an alias inside one of these never counts on its own.</summary>
    public string[] ExcludedNames { get; set; } =
        ["CRUISER AURORA", "Aurora Young Blood", "Aurora YB", "Aurora Academy", "Aurora Female", "Aurora fe", "ex-Aurora", "ex Aurora"];

    /// <summary>Reference ids (documentation and doctor only — the feed cannot be filtered by them).</summary>
    public int HltvTeamId { get; set; } = 11861;

    public int PandaScoreTeamId { get; set; } = 131505;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 40)
            errors.Add("News:Team:DisplayName must be 1-40 characters");
        if (Aliases.Length == 0 || Aliases.Any(a => string.IsNullOrWhiteSpace(a) || a.Trim().Length < 3))
            errors.Add("News:Team:Aliases needs at least one alias of 3+ characters");
        return errors;
    }
}

/// <summary>
/// The current players, used only for "a current player is in the headline". The automatic source is the team page's
/// Active squad on Liquipedia (MediaWiki API, one automatic request per <see cref="RefreshHours"/> at most — at least 24 h,
/// successful or not; the manual `news check --roster` CLI is a separate, operator-run request). Until the first successful sync
/// the dated <see cref="SeedPlayers"/> are used; either list expires after <see cref="MaxAgeDays"/> without a successful
/// refresh, and then only team-name matching remains.
/// </summary>
public sealed class NewsRosterOptions
{
    public bool SyncFromLiquipedia { get; set; } = true;

    /// <summary>Liquipedia Counter-Strike page of the team.</summary>
    public string LiquipediaPage { get; set; } = "Aurora_Gaming";

    public int RefreshHours { get; set; } = 24;

    public int MaxAgeDays { get; set; } = 7;

    /// <summary>Active squad as verified on Liquipedia (players only, no staff) on <see cref="SeedVerifiedAt"/>.</summary>
    public string[] SeedPlayers { get; set; } = ["XANTARES", "woxic", "Wicadia", "Jimpphat", "kyxsan"];

    public string SeedVerifiedAt { get; set; } = "2026-09-30T00:00:00Z";

    /// <summary>Player names that are also ordinary words: they only count together with the team name.</summary>
    public string[] AmbiguousNames { get; set; } = [];

    public DateTimeOffset? SeedVerifiedAtValue =>
        DateTimeOffset.TryParse(SeedVerifiedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RefreshHours is < 24 or > 168)
            errors.Add("News:Roster:RefreshHours must be between 24 and 168 (at most one automatic Liquipedia request per day)");
        if (MaxAgeDays is < 1 or > 60)
            errors.Add("News:Roster:MaxAgeDays must be between 1 and 60");
        if (SeedPlayers.Length > 0 && SeedVerifiedAtValue is null)
            errors.Add("News:Roster:SeedVerifiedAt must be an ISO date when SeedPlayers is set");
        if (string.IsNullOrWhiteSpace(LiquipediaPage) || LiquipediaPage.Any(c => char.IsWhiteSpace(c) || c is '|' or '#' or '&' or '?'))
            errors.Add("News:Roster:LiquipediaPage must be a plain page title (underscores for spaces)");
        return errors;
    }
}
