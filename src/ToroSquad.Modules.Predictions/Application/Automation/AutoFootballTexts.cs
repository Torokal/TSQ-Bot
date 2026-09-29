using ToroSquad.Core.Localization;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application.Automation;

/// <summary>
/// The bookmakers Live may use: those whose own full-time rule is verified (<see cref="AutoFootballOptions.RuleVerifiedBookmakers"/>
/// in production; a test host may register its own, clearly synthetic, list).
/// </summary>
public sealed record FootballMarketRules(IReadOnlyList<string> ApprovedBookmakers)
{
    public static FootballMarketRules Production { get; } = new(AutoFootballOptions.RuleVerifiedBookmakers);

    /// <summary>No approved bookmaker at all: the general gate (Live opens nothing, reason MARKET_RULE_UNVERIFIED).</summary>
    public static FootballMarketRules None { get; } = new([]);

    /// <summary>The approved bookmakers in the configured priority order (what Live can ever choose from).</summary>
    public IReadOnlyList<string> Usable(IReadOnlyList<string> priority) => priority.Where(b => ApprovedBookmakers.Contains(b, StringComparer.Ordinal)).ToList();
}

/// <summary>
/// The texts of an automatic card, shared by the automation and the local card preview of the read-only check. Three names
/// are kept apart: the provider's RAW team names (only they match the odds outcomes), the followed team, and the Turkish
/// name shown to members ("Türkiye", "Fenerbahçe"); a few national opponents get their Turkish name on a Turkish card
/// ("Belgium" → "Belçika"); any other team is shown as the provider writes it (the card defuses it). Display only: odds are
/// matched on the raw names. The provider's home/away order is kept, also for a neutral venue and whoever is followed.
/// </summary>
public static class AutoFootballTexts
{
    // Türkiye's current opponents only, not a country database: an unknown name stays as the provider writes it.
    private static readonly Dictionary<string, string> TurkishNationalNames = new(StringComparer.Ordinal)
    {
        ["Belgium"] = "Belçika",
        ["France"] = "Fransa",
        ["Italy"] = "İtalya",
    };

    public static string DisplayName(string providerName, TeamScope scope, string language) =>
        TrackedTeams.Match(providerName, scope) is { } tracked ? tracked.DisplayName
        : scope == TeamScope.National && string.Equals(language, "tr", StringComparison.OrdinalIgnoreCase) && TurkishNationalNames.TryGetValue(providerName.Trim(), out var tr) ? tr
        : providerName.Trim();

    public static (string Title, string Rules, IReadOnlyList<(string Label, int OddsX100)> Outcomes) Build(ILocalizer localizer, string language, string homeRaw,
        string awayRaw, TeamScope scope, int lockMinutes, SelectedOdds odds)
    {
        string L(string key, params object?[] args) => localizer.Get(language, key, args);
        var home = DisplayName(homeRaw, scope, language);
        var away = DisplayName(awayRaw, scope, language);
        return (L("predictions.auto.title", home, away), L("predictions.auto.rules", lockMinutes),
            [(L("predictions.auto.wins", home), odds.HomeX100), (L("predictions.auto.draw"), odds.DrawX100), (L("predictions.auto.wins", away), odds.AwayX100)]);
    }
}
