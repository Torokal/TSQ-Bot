using System.Globalization;
using ToroSquad.Core.Guilds;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions;

/// <summary>
/// Section "Predictions:Automation" — the automatic football opener (docs/predictions/AUTO_FOOTBALL.md). Off by default.
/// A bad or incomplete value never stops the bot or the manual predictions: <see cref="Problems"/> is reported by
/// /bot status and doctor, and the automation then runs as <see cref="AutomationMode.Disabled"/>. The API key is a secret
/// and is NOT part of this class: it is read from <see cref="ApiKeySetting"/> (user-secrets or the environment variable
/// TOROSQUAD_Predictions__Automation__TheOddsApi__ApiKey) and redacted everywhere. The target channel is the predictions
/// channel of <see cref="PredictionsOptions"/> (no second channel setting), the guild the deployment's single allowed guild.
/// </summary>
public sealed class AutoFootballOptions
{
    public const string Section = "Predictions:Automation";
    public const string ApiKeySetting = "Predictions:Automation:TheOddsApi:ApiKey";
    public const string ProviderName = "TheOddsApi";

    /// <summary>Club competitions (the three clubs are looked for here).</summary>
    public static readonly IReadOnlyList<string> ClubCompetitions =
    [
        "soccer_turkey_super_league",
        "soccer_uefa_champs_league",
        "soccer_uefa_champs_league_qualification",
        "soccer_uefa_europa_league",
        "soccer_uefa_europa_conference_league",
    ];

    /// <summary>
    /// National-team competitions (the Türkiye men's senior team is looked for here). Match keys only: an outright ("who wins
    /// the tournament", e.g. soccer_fifa_world_cup_winner) is never listed, and a catalog entry flagged has_outrights is not
    /// read. The provider has no key for friendlies: they are not covered.
    /// </summary>
    public static readonly IReadOnlyList<string> NationalCompetitions =
    [
        "soccer_uefa_nations_league",
        "soccer_uefa_euro_qualification",
        "soccer_uefa_european_championship",
        "soccer_fifa_world_cup_qualifiers_europe",
        "soccer_fifa_world_cup",
    ];

    /// <summary>The competitions the opener may ever read (explicit allow-list; the provider's other sports are never watched).</summary>
    public static readonly IReadOnlyList<string> KnownCompetitions = [.. ClubCompetitions, .. NationalCompetitions];

    /// <summary>
    /// Bookmakers whose OWN rules are verified to settle the football match result on regular time (90 minutes plus the
    /// time the referee adds; extra time and penalty shoot-outs excluded). Only these may be used by Live; any other one is
    /// visible in Observe and in the read-only check but never published. Evidence per entry: docs/predictions/
    /// PROVIDER_VERIFICATION.md. Empty today: the official rule pages of Pinnacle and 1xBet could not be read (MARKET_RULE_UNVERIFIED).
    /// </summary>
    public static readonly IReadOnlyList<string> RuleVerifiedBookmakers = [];

    public static TeamScope? ScopeOf(string competition) =>
        ClubCompetitions.Contains(competition, StringComparer.Ordinal) ? TeamScope.Club
        : NationalCompetitions.Contains(competition, StringComparer.Ordinal) ? TeamScope.National
        : null;

    /// <summary>Standard sportsbooks of the provider's "eu" region, in the default order. Exchanges are never allowed.</summary>
    public static readonly IReadOnlyList<string> DefaultBookmakerPriority =
        ["pinnacle", "onexbet", "marathonbet", "williamhill", "betsson", "nordicbet", "sport888", "unibet_nl", "unibet_fr", "betclic_fr"];

    public static readonly IReadOnlyList<string> OfficialHosts = ["api.the-odds-api.com", "ipv6-api.the-odds-api.com"];

    /// <summary>Disabled, Observe or Live (text, so a typo is reported instead of failing the configuration binding).</summary>
    public string Mode { get; set; } = nameof(AutomationMode.Disabled);

    /// <summary>The parsed <see cref="Mode"/>; null when it is not one of the three.</summary>
    public AutomationMode? ParsedMode =>
        Enum.TryParse<AutomationMode>(Mode, ignoreCase: true, out var mode) && Enum.IsDefined(mode) && !int.TryParse(Mode, out _) ? mode : null;

    public string Provider { get; set; } = ProviderName;
    public string BaseUrl { get; set; } = "https://api.the-odds-api.com/";
    public string PublishLocalTime { get; set; } = "09:00";
    public string TimeZone { get; set; } = TurkeyCalendar.TimeZoneId;
    public int LockBeforeKickoffMinutes { get; set; } = 2;
    public int MinLeadTimeToPublishMinutes { get; set; } = 15;

    /// <summary>A kickoff before (or soon after) the publish time is opened this long before kickoff (same local day only).</summary>
    public int EarlyPublishLeadMinutes { get; set; } = 120;

    public int MaxOddsAgeMinutes { get; set; } = 30;
    public int DiscoveryIntervalMinutes { get; set; } = 15;
    public int CatalogIntervalHours { get; set; } = 12;

    /// <summary>How far ahead matches are discovered (the free events list); only today's ever get odds.</summary>
    public int DiscoveryHorizonHours { get; set; } = 48;

    public int MaxOddsAttemptsPerEvent { get; set; } = 4;

    /// <summary>Credit-consuming calls stop when the provider reports this many credits or fewer left.</summary>
    public int CreditReserve { get; set; } = 50;

    /// <summary>One region (cost = markets × regions = 1 per odds call). Only "eu" is verified for this use.</summary>
    public string Region { get; set; } = "eu";

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Empty = <see cref="KnownCompetitions"/> (arrays are appended to by the configuration binder, so no initializer here).</summary>
    public string[] CompetitionKeys { get; set; } = [];

    /// <summary>Empty = <see cref="DefaultBookmakerPriority"/>.</summary>
    public string[] BookmakerPriority { get; set; } = [];

    public IReadOnlyList<string> Competitions => CompetitionKeys.Length > 0 ? CompetitionKeys : KnownCompetitions;
    public IReadOnlyList<string> Bookmakers => BookmakerPriority.Length > 0 ? BookmakerPriority : DefaultBookmakerPriority;
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
    public TimeSpan MaxOddsAge => TimeSpan.FromMinutes(MaxOddsAgeMinutes);
    public TimeSpan DiscoveryInterval => TimeSpan.FromMinutes(DiscoveryIntervalMinutes);
    public TimeSpan CatalogInterval => TimeSpan.FromHours(CatalogIntervalHours);
    public TimeSpan DiscoveryHorizon => TimeSpan.FromHours(DiscoveryHorizonHours);

    /// <summary>The timing, or null when a value is invalid (then <see cref="Problems"/> says which).</summary>
    public AutoTiming? Timing()
    {
        if (!TimeOnly.TryParseExact(PublishLocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var publish) ||
            !GuildTime.TryResolve(TimeZone, out var zone))
            return null;
        return new AutoTiming(publish, TimeSpan.FromMinutes(EarlyPublishLeadMinutes), TimeSpan.FromMinutes(LockBeforeKickoffMinutes),
            TimeSpan.FromMinutes(MinLeadTimeToPublishMinutes), zone);
    }

    public static bool IsExchange(string bookmaker) =>
        bookmaker.StartsWith("betfair_ex", StringComparison.Ordinal) || bookmaker is "matchbook" or "smarkets" or "betdaq";

    /// <summary>Every problem of the section (never a secret value). Any problem keeps the automation disabled.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (ParsedMode is null)
            p.Add($"{Section}:Mode must be Disabled, Observe or Live");
        if (!string.Equals(Provider, ProviderName, StringComparison.Ordinal))
            p.Add($"{Section}:Provider must be {ProviderName} (the only supported provider)");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !OfficialHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
            p.Add($"{Section}:BaseUrl must be https://{OfficialHosts[0]}/ (the official API host)");
        if (!TimeOnly.TryParseExact(PublishLocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            p.Add($"{Section}:PublishLocalTime must be HH:mm (got {PublishLocalTime})");
        if (!GuildTime.TryResolve(TimeZone, out _))
            p.Add($"{Section}:TimeZone is not an available time zone (got {TimeZone})");
        if (LockBeforeKickoffMinutes is < 1 or > 60)
            p.Add($"{Section}:LockBeforeKickoffMinutes must be 1-60");
        if (MinLeadTimeToPublishMinutes <= LockBeforeKickoffMinutes || MinLeadTimeToPublishMinutes > 240)
            p.Add($"{Section}:MinLeadTimeToPublishMinutes must be greater than LockBeforeKickoffMinutes and at most 240");
        if (EarlyPublishLeadMinutes <= MinLeadTimeToPublishMinutes || EarlyPublishLeadMinutes > 720)
            p.Add($"{Section}:EarlyPublishLeadMinutes must be greater than MinLeadTimeToPublishMinutes and at most 720");
        if (MaxOddsAgeMinutes is < 1 or > 180)
            p.Add($"{Section}:MaxOddsAgeMinutes must be 1-180");
        if (DiscoveryIntervalMinutes is < 5 or > 360)
            p.Add($"{Section}:DiscoveryIntervalMinutes must be 5-360");
        if (CatalogIntervalHours is < 1 or > 168)
            p.Add($"{Section}:CatalogIntervalHours must be 1-168");
        if (DiscoveryHorizonHours is < 24 or > 168)
            p.Add($"{Section}:DiscoveryHorizonHours must be 24-168");
        if (MaxOddsAttemptsPerEvent is < 1 or > 6)
            p.Add($"{Section}:MaxOddsAttemptsPerEvent must be 1-6");
        if (CreditReserve is < 0 or > 100_000)
            p.Add($"{Section}:CreditReserve must be 0-100000");
        if (!string.Equals(Region, "eu", StringComparison.Ordinal))
            p.Add($"{Section}:Region must be eu (one region, cost 1 per odds call; others are not verified)");
        if (TimeoutSeconds is < 2 or > 60)
            p.Add($"{Section}:TimeoutSeconds must be 2-60");
        foreach (var key in Competitions.Where(k => !KnownCompetitions.Contains(k, StringComparer.Ordinal)))
            p.Add($"{Section}:CompetitionKeys contains '{key}', which is not in the supported allow-list");
        if (Competitions.Distinct(StringComparer.Ordinal).Count() != Competitions.Count)
            p.Add($"{Section}:CompetitionKeys must not repeat a key");
        if (Bookmakers.Count is < 1 or > 20 || Bookmakers.Distinct(StringComparer.Ordinal).Count() != Bookmakers.Count)
            p.Add($"{Section}:BookmakerPriority must list 1-20 distinct bookmaker keys");
        foreach (var key in Bookmakers.Where(IsExchange))
            p.Add($"{Section}:BookmakerPriority contains the exchange '{key}' (lay/commission odds are not fixed odds)");
        foreach (var key in Bookmakers.Where(k => k.Length is 0 or > 40 || !k.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            p.Add($"{Section}:BookmakerPriority contains an invalid key '{key}'");
        return p;
    }
}
