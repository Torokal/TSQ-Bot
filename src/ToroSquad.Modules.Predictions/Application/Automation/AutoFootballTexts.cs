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

    /// <summary>The approved bookmakers in the configured priority order (what Live can ever choose from).</summary>
    public IReadOnlyList<string> Usable(IReadOnlyList<string> priority) => priority.Where(b => ApprovedBookmakers.Contains(b, StringComparer.Ordinal)).ToList();
}

/// <summary>
/// The texts of an automatic card, shared by the automation and the local card preview of the read-only check. Three names
/// are kept apart: the provider's RAW team names (only they match the odds outcomes), the followed team, and the Turkish
/// name shown to members ("Türkiye", "Fenerbahçe"); other teams are shown as the provider writes them (the card defuses them).
/// The provider's home/away order is kept, also for a neutral venue and whoever is followed.
/// </summary>
public static class AutoFootballTexts
{
    public static string TeamName(string providerName, TeamScope scope) => TrackedTeams.Match(providerName, scope)?.DisplayName ?? providerName.Trim();

    public static (string Title, string Rules, IReadOnlyList<(string Label, int OddsX100)> Outcomes) Build(ILocalizer localizer, string language, string homeRaw,
        string awayRaw, TeamScope scope, int lockMinutes, SelectedOdds odds)
    {
        string L(string key, params object?[] args) => localizer.Get(language, key, args);
        var home = TeamName(homeRaw, scope);
        var away = TeamName(awayRaw, scope);
        return (L("predictions.auto.title", home, away), L("predictions.auto.rules", lockMinutes),
            [(L("predictions.auto.wins", home), odds.HomeX100), (L("predictions.auto.draw"), odds.DrawX100), (L("predictions.auto.wins", away), odds.AwayX100)]);
    }
}
