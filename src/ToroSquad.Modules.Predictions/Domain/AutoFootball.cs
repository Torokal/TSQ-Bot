using System.Globalization;
using System.Text;

namespace ToroSquad.Modules.Predictions.Domain;

/// <summary>Where a prediction came from: a member's form, or the automatic football opener.</summary>
public enum PredictionOrigin
{
    Manual = 0,

    /// <summary>
    /// Opened by the automatic football job from provider data. No human creator: CreatorUserId is 0, nobody gets a wallet or
    /// leaderboard eligibility for it, and only Administrator or the server owner manage it.
    /// </summary>
    AutoFootball = 1,
}

/// <summary>What the automatic football job may do.</summary>
public enum AutomationMode
{
    /// <summary>No HTTP call, no planning, nothing written.</summary>
    Disabled = 0,

    /// <summary>Reads the provider and records "which match, which odds, when" in its own rows — no prediction, no coin, no Discord.</summary>
    Observe = 1,

    /// <summary>Opens real automatic predictions when every gate passes.</summary>
    Live = 2,
}

/// <summary>The life of one tracked match in the automation.</summary>
public enum AutoEventState
{
    /// <summary>Discovered; its publish time has not come yet.</summary>
    Planned = 0,

    /// <summary>Its publish time came; odds are being looked for (bounded attempts).</summary>
    WaitingForOdds = 1,

    /// <summary>A prediction row exists and its card is being posted (or the post was uncertain).</summary>
    Publishing = 2,

    /// <summary>Its card is public. Never opened again, whatever happens to the prediction.</summary>
    Published = 3,

    /// <summary>Observe mode: the decision was recorded; nothing was created.</summary>
    Observed = 4,

    /// <summary>Not opened, for the recorded reason (terminal).</summary>
    Skipped = 5,

    /// <summary>Needs an administrator: a published match changed, vanished, or its card delivery stayed unknown (terminal for the job).</summary>
    ReviewRequired = 6,
}

/// <summary>Why a match was not opened (or why a published one needs review). <see cref="AutoBlockCodes.Code"/> gives the ops code.</summary>
public enum AutoBlockReason
{
    None = 0,
    NoOdds = 1,
    IncompleteMarket = 2,
    StaleOdds = 3,
    InvalidOdds = 4,
    UnsupportedCompetition = 5,
    AmbiguousMatch = 6,
    QuotaPaused = 7,
    AuthError = 8,
    ProviderUnavailable = 9,
    TooLateToPublish = 10,
    CardInvalid = 11,
    ModuleDisabled = 12,
    ChannelUnavailable = 13,
    ScheduleChanged = 14,
    EventMissing = 15,
    DeliveryUnknown = 16,
    AutomationStopped = 17,

    /// <summary>A complete set exists, but only from a bookmaker whose full-time rule is not verified (never used by Live).</summary>
    BookmakerNotApproved = 18,

    /// <summary>No bookmaker of the priority has a verified full-time (90 minutes + stoppage time) rule: Live opens nothing.</summary>
    MarketRuleUnverified = 19,

    /// <summary>A match to judge again under a changed approval, but its odds attempts are used up (never reset).</summary>
    AttemptsExhausted = 20,
}

public static class AutoBlockCodes
{
    public static string Code(AutoBlockReason reason) => reason switch
    {
        AutoBlockReason.None => "NONE",
        AutoBlockReason.NoOdds => "NO_ODDS",
        AutoBlockReason.IncompleteMarket => "INCOMPLETE_MARKET",
        AutoBlockReason.StaleOdds => "STALE_ODDS",
        AutoBlockReason.InvalidOdds => "INVALID_ODDS",
        AutoBlockReason.UnsupportedCompetition => "UNSUPPORTED_COMPETITION",
        AutoBlockReason.AmbiguousMatch => "AMBIGUOUS_MATCH",
        AutoBlockReason.QuotaPaused => "QUOTA_PAUSED",
        AutoBlockReason.AuthError => "AUTH_ERROR",
        AutoBlockReason.ProviderUnavailable => "PROVIDER_UNAVAILABLE",
        AutoBlockReason.TooLateToPublish => "TOO_LATE_TO_PUBLISH",
        AutoBlockReason.CardInvalid => "CARD_INVALID",
        AutoBlockReason.ModuleDisabled => "MODULE_DISABLED",
        AutoBlockReason.ChannelUnavailable => "CHANNEL_UNAVAILABLE",
        AutoBlockReason.ScheduleChanged => "SCHEDULE_CHANGED",
        AutoBlockReason.EventMissing => "EVENT_MISSING",
        AutoBlockReason.DeliveryUnknown => "DELIVERY_UNKNOWN",
        AutoBlockReason.AutomationStopped => "AUTOMATION_STOPPED",
        AutoBlockReason.BookmakerNotApproved => "BOOKMAKER_NOT_APPROVED",
        AutoBlockReason.MarketRuleUnverified => "MARKET_RULE_UNVERIFIED",
        AutoBlockReason.AttemptsExhausted => "ATTEMPTS_EXHAUSTED",
        _ => "UNKNOWN",
    };
}

/// <summary>Club football or national-team football: a team and a competition must have the same scope to match.</summary>
public enum TeamScope
{
    Club = 0,
    National = 1,
}

/// <summary>One followed men's senior football team, its scope, its Turkish display name and the exact provider names that mean it.</summary>
public sealed record TrackedTeam(string Code, string DisplayName, TeamScope Scope, IReadOnlyList<string> Aliases);

/// <summary>
/// THE list of followed teams (one place; add a team here, nowhere else): the three clubs and the Türkiye men's senior
/// national team. A provider name matches by EXACT name after a controlled fold (trim, one space, lower case, Turkish
/// letters to their ASCII base: "Beşiktaş JK" = "besiktas jk") AND only inside a competition of the same scope — so
/// "Turkey" is never a club and a club name never a national team. No substring or fuzzy match: "Fenerbahçe U19",
/// "Galatasaray W", "Turkey U21", "Turkey Women" or "Fener" never count; an unknown similar name is not a followed team.
/// The aliases are the provider's spellings seen in its data plus the Turkish forms; a new spelling is added here after it
/// is verified, never guessed at runtime. (Türkiye: "Turkey" is the English form the provider's other team names follow,
/// "Türkiye" the official name, "Turkiye" its ASCII form — none of the three observed in the provider's data yet.)
/// </summary>
public static class TrackedTeams
{
    public static readonly TrackedTeam Galatasaray = new("GS", "Galatasaray", TeamScope.Club, ["Galatasaray", "Galatasaray SK", "Galatasaray AS"]);
    public static readonly TrackedTeam Fenerbahce = new("FB", "Fenerbahçe", TeamScope.Club, ["Fenerbahce", "Fenerbahçe", "Fenerbahce SK", "Fenerbahçe SK"]);
    public static readonly TrackedTeam Besiktas = new("BJK", "Beşiktaş", TeamScope.Club, ["Besiktas", "Beşiktaş", "Besiktas JK", "Beşiktaş JK"]);
    public static readonly TrackedTeam Turkiye = new("TR", "Türkiye", TeamScope.National, ["Turkey", "Türkiye", "Turkiye"]);

    public static readonly IReadOnlyList<TrackedTeam> All = [Galatasaray, Fenerbahce, Besiktas, Turkiye];

    private static readonly Dictionary<(TeamScope, string), TrackedTeam> ByAlias =
        All.SelectMany(t => t.Aliases.Select(a => (Key: (t.Scope, Fold(a)), Team: t))).DistinctBy(x => x.Key).ToDictionary(x => x.Key, x => x.Team);

    public static TrackedTeam? Match(string? providerName, TeamScope scope) =>
        providerName is not null && ByAlias.TryGetValue((scope, Fold(providerName)), out var team) ? team : null;

    /// <summary>Trim, collapse white space, fold Turkish letters to ASCII and lower-case (culture-invariant).</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var raw in text.Trim())
        {
            if (char.IsWhiteSpace(raw))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(raw switch
            {
                'ç' or 'Ç' => 'c',
                'ğ' or 'Ğ' => 'g',
                'ı' or 'I' or 'İ' => 'i',
                'ö' or 'Ö' => 'o',
                'ş' or 'Ş' => 's',
                'ü' or 'Ü' => 'u',
                _ => char.ToLowerInvariant(raw),
            });
        }

        return sb.ToString();
    }
}

/// <summary>The configured timing of the automatic opener (validated in <c>AutoFootballOptions</c>).</summary>
public sealed record AutoTiming(TimeSpan PublishBefore, TimeSpan LockBefore, TimeSpan MinLead, TimeZoneInfo Zone);

/// <summary>Where "now" stands for one match.</summary>
public enum PublishWindow
{
    /// <summary>Before its publish time.</summary>
    NotYet = 0,

    /// <summary>Publish time reached, still before the last safe moment.</summary>
    Open = 1,

    /// <summary>Less than the minimum lead before kickoff (or kickoff passed): a new card is never opened.</summary>
    TooLate = 2,
}

/// <summary>
/// THE schedule of an automatic prediction, from the planned kickoff only (UTC instant from the provider) — pure durations,
/// no calendar day, no wall-clock publish time, no machine time zone:
/// <list type="bullet">
/// <item>publish <see cref="AutoTiming.PublishBefore"/> (24 h = 24 × 60 minutes) before the kickoff;</item>
/// <item>a match first seen (or a bot back) after that moment is opened at once (catch-up) — but a new card only while at
/// least <see cref="AutoTiming.MinLead"/> (15 min) remain; a passed kickoff never;</item>
/// <item>entries lock <see cref="AutoTiming.LockBefore"/> (2 min) before the PLANNED kickoff — a lock, not a claim that the
/// match started.</item>
/// </list>
/// </summary>
public static class AutoSchedule
{
    public static readonly TimeSpan MinAttemptGap = TimeSpan.FromMinutes(3);

    public static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>The instant a wall-clock time of <paramref name="day"/> happens in the zone (a skipped DST hour moves forward).</summary>
    public static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static DateTimeOffset PublishAt(DateTimeOffset kickoff, AutoTiming timing) => kickoff - timing.PublishBefore;

    public static DateTimeOffset Deadline(DateTimeOffset kickoff, AutoTiming timing) => kickoff - timing.MinLead;

    public static DateTimeOffset LockAt(DateTimeOffset kickoff, AutoTiming timing) => kickoff - timing.LockBefore;

    public static PublishWindow Window(DateTimeOffset now, DateTimeOffset kickoff, AutoTiming timing)
    {
        if (now >= Deadline(kickoff, timing))
            return PublishWindow.TooLate;
        return now >= PublishAt(kickoff, timing) ? PublishWindow.Open : PublishWindow.NotYet;
    }

    /// <summary>
    /// When to look for odds again after <paramref name="used"/> of <paramref name="max"/> attempts: halfway between now and
    /// the deadline, every time — with a 24 h window and 4 attempts about 24, 12, 6 and 3 hours before kickoff, so the few
    /// paid calls reach from the publish time to close to the match instead of being spent in the first hours. Never
    /// sooner than <see cref="MinAttemptGap"/>; null when no attempt is left or none fits before the deadline.
    /// </summary>
    public static DateTimeOffset? NextAttempt(DateTimeOffset now, DateTimeOffset deadline, int used, int max)
    {
        if (max - used <= 0 || now >= deadline)
            return null;
        var gap = (deadline - now) / 2;
        var next = now + (gap < MinAttemptGap ? MinAttemptGap : gap);
        return next < deadline ? next : null;
    }
}

/// <summary>One price as the provider sent it (decimal odds).</summary>
public sealed record ProviderPrice(string Name, decimal Price);

/// <summary>One market of one bookmaker; <see cref="LastUpdate"/> is the provider's market-level "last seen" time.</summary>
public sealed record ProviderMarket(string Key, DateTimeOffset? LastUpdate, IReadOnlyList<ProviderPrice> Outcomes);

public sealed record ProviderBookmaker(string Key, string Title, IReadOnlyList<ProviderMarket> Markets);

/// <summary>One match of the odds response.</summary>
public sealed record ProviderOddsEvent(string Id, string SportKey, DateTimeOffset CommenceTime, string HomeTeam, string AwayTeam, IReadOnlyList<ProviderBookmaker> Bookmakers);

/// <summary>
/// What the selection found: the set Live may use (from an APPROVED bookmaker — its full-time rule verified), or the reason
/// there is none; <see cref="Unapproved"/> is the first complete set of a bookmaker that is not approved (shown by Observe
/// and the read-only check, never published).
/// </summary>
public sealed record OddsChoice(SelectedOdds? Odds, AutoBlockReason Reason, SelectedOdds? Unapproved);

/// <summary>A complete 1-X-2 set from ONE bookmaker's ONE h2h market, converted to the fixed odds model (raw prices kept).</summary>
public sealed record SelectedOdds(
    string BookmakerKey,
    string BookmakerTitle,
    DateTimeOffset LastUpdate,
    int HomeX100,
    int DrawX100,
    int AwayX100,
    decimal HomeRaw,
    decimal DrawRaw,
    decimal AwayRaw);

/// <summary>
/// Picks the odds of an automatic card: the FIRST bookmaker of the configured priority that has a complete, valid and
/// fresh full-time h2h set for exactly this match — never a mix of bookmakers, never the best price per outcome, never an
/// average, no margin added, no normalisation, nothing random. Exchange (lay) markets are never read (only the key
/// "h2h"; exchange bookmakers are refused by the options). A set counts only when it has exactly three distinct outcomes
/// named exactly the match's home team, the away team and "Draw" (the order in the response is ignored), every price
/// converts to the fixed model, and the market's last_update is present, not in the future and at most the maximum age old.
/// No set: the reason, never a default.
/// </summary>
public static class OddsSelector
{
    public const string Market = "h2h";
    public const string DrawName = "Draw";

    /// <summary>A provider clock slightly ahead of ours is tolerated; more is a broken timestamp.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Selection where every bookmaker of the priority counts as approved.</summary>
    public static (SelectedOdds? Odds, AutoBlockReason Reason) Select(ProviderOddsEvent match, IReadOnlyList<string> priority, DateTimeOffset now, TimeSpan maxAge)
    {
        var choice = Choose(match, priority, priority, now, maxAge);
        return (choice.Odds, choice.Reason);
    }

    /// <summary>
    /// Walks the priority: the first complete, valid, fresh set of an APPROVED bookmaker wins. A complete set of a bookmaker
    /// that is not approved is remembered (the first one) but never chosen: without an approved set the reason is
    /// <see cref="AutoBlockReason.MarketRuleUnverified"/> (no approved bookmaker at all) or
    /// <see cref="AutoBlockReason.BookmakerNotApproved"/> — valid data rejected by OUR rule, not "the provider has no odds".
    /// </summary>
    public static OddsChoice Choose(ProviderOddsEvent match, IReadOnlyList<string> priority, IReadOnlyCollection<string> approved, DateTimeOffset now, TimeSpan maxAge)
    {
        var worst = AutoBlockReason.NoOdds;
        SelectedOdds? unapproved = null;
        foreach (var key in priority)
        {
            var bookmaker = match.Bookmakers.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));
            if (bookmaker is null)
                continue;
            var markets = bookmaker.Markets.Where(m => string.Equals(m.Key, Market, StringComparison.Ordinal)).ToList();
            if (markets.Count != 1)
            {
                worst = Worse(worst, markets.Count == 0 ? AutoBlockReason.NoOdds : AutoBlockReason.IncompleteMarket);
                continue;
            }

            var (odds, reason) = FromMarket(match, bookmaker, markets[0], now, maxAge);
            if (odds is null)
            {
                worst = Worse(worst, reason);
                continue;
            }

            if (approved.Contains(key, StringComparer.Ordinal))
                return new OddsChoice(odds, AutoBlockReason.None, unapproved);
            unapproved ??= odds;
        }

        return unapproved is null
            ? new OddsChoice(null, worst, null)
            : new OddsChoice(null, priority.Any(k => approved.Contains(k, StringComparer.Ordinal)) ? AutoBlockReason.BookmakerNotApproved : AutoBlockReason.MarketRuleUnverified,
                unapproved);
    }

    private static (SelectedOdds? Odds, AutoBlockReason Reason) FromMarket(ProviderOddsEvent match, ProviderBookmaker bookmaker, ProviderMarket market, DateTimeOffset now,
        TimeSpan maxAge)
    {
        var outcomes = market.Outcomes;
        if (outcomes.Count != 3 || outcomes.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count() != 3)
            return (null, AutoBlockReason.IncompleteMarket);
        var home = outcomes.SingleOrDefault(o => string.Equals(o.Name, match.HomeTeam, StringComparison.Ordinal));
        var draw = outcomes.SingleOrDefault(o => string.Equals(o.Name, DrawName, StringComparison.Ordinal));
        var away = outcomes.SingleOrDefault(o => string.Equals(o.Name, match.AwayTeam, StringComparison.Ordinal));
        if (home is null || draw is null || away is null || string.Equals(match.HomeTeam, match.AwayTeam, StringComparison.Ordinal))
            return (null, AutoBlockReason.IncompleteMarket);
        if (market.LastUpdate is not { } updated || updated > now + ClockSkew || now - updated > maxAge)
            return (null, AutoBlockReason.StaleOdds);
        if (ToX100(home.Price) is not { } h || ToX100(draw.Price) is not { } d || ToX100(away.Price) is not { } a)
            return (null, AutoBlockReason.InvalidOdds);
        return (new SelectedOdds(bookmaker.Key, bookmaker.Title, updated, h, d, a, home.Price, draw.Price, away.Price), AutoBlockReason.None);
    }

    /// <summary>
    /// THE conversion of a provider's decimal price to the fixed ×100 model: rounded DOWN to two decimals (a card never pays
    /// more than the source quoted; 1.856 → 1.85), then it must be inside the domain range (<see cref="Odds.MinX100"/>–
    /// <see cref="Odds.MaxX100"/>). Out of range is refused, never clamped.
    /// </summary>
    public static int? ToX100(decimal price)
    {
        if (price <= 1m || price > Odds.MaxX100 / 100m)
            return null;
        var x100 = decimal.Floor(price * 100m);
        return x100 is < Odds.MinX100 or > Odds.MaxX100 ? null : (int)x100;
    }

    /// <summary>The raw price for the audit trail, invariant and exact.</summary>
    public static string Raw(decimal price) => price.ToString(CultureInfo.InvariantCulture);

    // The most informative failure across bookmakers: a stale or broken set says more than a missing one.
    private static AutoBlockReason Worse(AutoBlockReason current, AutoBlockReason candidate) => Rank(candidate) > Rank(current) ? candidate : current;

    private static int Rank(AutoBlockReason reason) => reason switch
    {
        AutoBlockReason.StaleOdds => 3,
        AutoBlockReason.InvalidOdds => 2,
        AutoBlockReason.IncompleteMarket => 1,
        _ => 0,
    };
}
