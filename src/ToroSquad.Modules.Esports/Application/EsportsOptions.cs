using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;
using ToroSquad.Modules.Esports.Providers.PandaScore;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>Section "Esports". Polling cadence is validated against the verified provider quota at startup.</summary>
public sealed class EsportsOptions
{
    public const string Section = "Esports";

    public ProviderSection Provider { get; set; } = new();
    /// <summary>5 minutes keeps "match started"/results timely within the PandaScore budget (validated at startup).</summary>
    public int MatchPollMinutes { get; set; } = 5;
    public int EventPollHours { get; set; } = 6;
    public int RankingPollHours { get; set; } = 12;
    public int PastWindowHours { get; set; } = 12;
    public int FutureWindowHours { get; set; } = 48;

    /// <summary>Cached data older than this is "stale": shown with a warning and never used for new notifications.</summary>
    public int StaleAfterMinutes { get; set; } = 30;

    /// <summary>After downtime, results whose match started within this window may still be announced (bounded catch-up).</summary>
    public int ResultCatchUpHours { get; set; } = 6;

    /// <summary>Result messages keep being corrected (edited, never re-posted) for this long.</summary>
    public int ResultCorrectionHours { get; set; } = 24;

    public int MaxCatchUpPerGuildPerPoll { get; set; } = 5;
    public int ReminderGraceMinutes { get; set; } = 10;

    /// <summary>Explicit team-key → VRS team name links (e.g. "counterstrike/Team_Spirit": "Spirit").</summary>
    public Dictionary<string, string> TeamAliases { get; set; } = [];

    /// <summary>Operator-curated external match pages (verified HLTV / official). See <see cref="MatchLinkCatalog"/>.</summary>
    public List<VerifiedMatchLink> VerifiedMatchLinks { get; set; } = [];

    /// <summary>A start-time move smaller than this is treated as noise, not as a reschedule announcement.</summary>
    public int RescheduleThresholdMinutes { get; set; } = 15;

    /// <summary>Lifecycle messages (started, postponed, rescheduled, cancelled) are only sent this soon after the change was observed.</summary>
    public int LifecycleFreshMinutes { get; set; } = 90;

    public sealed class ProviderSection
    {
        public ProviderMode Mode { get; set; } = ProviderMode.Fixture;

        /// <summary>Match data provider: PandaScore (default) or Liquipedia (legacy, optional).</summary>
        public MatchProviderName Name { get; set; } = MatchProviderName.PandaScore;
    }

    public IReadOnlyList<string> Validate(LiquipediaOptions liquipedia, PandaScoreOptions pandaScore)
    {
        var errors = new List<string>();
        if (MatchPollMinutes < 2)
            errors.Add("Esports:MatchPollMinutes must be >= 2");
        if (Provider.Name == MatchProviderName.Liquipedia)
        {
            var budget = Math.Floor(liquipedia.RequestsPerHourPerTable * Math.Clamp(liquipedia.BudgetShare, 0.1, 1.0));
            var worstCasePerHour = 60.0 / Math.Max(1, MatchPollMinutes) * Math.Max(1, liquipedia.MaxPages);
            if (worstCasePerHour > budget)
                errors.Add($"Polling every {MatchPollMinutes} min with up to {liquipedia.MaxPages} pages needs {worstCasePerHour:0} req/h > budget {budget:0} (Esports:Liquipedia:RequestsPerHourPerTable x BudgetShare)");
        }
        else
        {
            // Matches every MatchPollMinutes plus events every EventPollHours, all pages, share one PandaScore budget.
            var worstCasePerHour = (60.0 / Math.Max(1, MatchPollMinutes) * Math.Max(1, pandaScore.MaxPages)) +
                                   (Math.Max(1, pandaScore.MaxPages) / (double)Math.Max(1, EventPollHours));
            if (worstCasePerHour > pandaScore.PlannedRequestsPerHour)
                errors.Add($"Polling every {MatchPollMinutes} min with up to {pandaScore.MaxPages} pages needs {worstCasePerHour:0} req/h > budget {pandaScore.PlannedRequestsPerHour} (PandaScore:RequestsPerHour x BudgetShare)");
            if (PandaScoreOptions.ConfigurationProblem(pandaScore, requireToken: false) is { } problem)
                errors.Add(problem);
        }

        errors.AddRange(MatchLinkCatalog.Problems(VerifiedMatchLinks));
        if (RescheduleThresholdMinutes is < 1 or > 1440)
            errors.Add("Esports:RescheduleThresholdMinutes must be 1..1440");
        if (ReminderGraceMinutes is < 1 or > 60)
            errors.Add("Esports:ReminderGraceMinutes must be 1..60");
        return errors;
    }
}
