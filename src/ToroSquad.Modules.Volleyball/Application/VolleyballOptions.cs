using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

/// <summary>
/// Section "Volleyball". Every cadence is state-aware and bounded; values are validated at startup. The module follows
/// exactly one team (Türkiye women's senior national team); there is deliberately no setting for another team.
/// </summary>
public sealed class VolleyballOptions
{
    public const string Section = "Volleyball";

    public ProviderSection Provider { get; set; } = new();

    /// <summary>Fixture/result discovery when no match of the team is within 48 h (a calendar changes slowly).</summary>
    public int FixtureRefreshHours { get; set; } = 6;

    /// <summary>Fixture refresh when a match is within 48 h (late time changes, postponements).</summary>
    public int FixtureRefreshNearMinutes { get; set; } = 60;

    /// <summary>Discovery window: this many days back (recent results for /volleyball schedule) …</summary>
    public int FixtureWindowPastDays { get; set; } = 3;

    /// <summary>… and this many days ahead.</summary>
    public int FixtureWindowAheadDays { get; set; } = 60;

    /// <summary>Live polling starts this long before a scheduled start (so the first live observation is "not started yet").</summary>
    public int LiveLeadMinutes { get; set; } = 30;

    /// <summary>A started match keeps being polled until finished, at most this long after its scheduled start.</summary>
    public int LiveTrailingHours { get; set; } = 5;

    /// <summary>Live state polling interval. Bounded by the provider's own update cadence and limits (docs/volleyball/PROVIDER_RESEARCH.md).</summary>
    public int LivePollSeconds { get; set; } = 60;

    /// <summary>
    /// Two observations further apart than this are not continuous (restart, outage): transitions in between are recorded
    /// but "started"/"set" cards are not sent — no catch-up spam.
    /// </summary>
    public int ContinuityMinutes { get; set; } = 10;

    /// <summary>Reminder lead before the scheduled start.</summary>
    public int ReminderLeadMinutes { get; set; } = 15;

    /// <summary>A "started"/"set" card is only sent this soon after the transition was observed.</summary>
    public int LiveFreshMinutes { get; set; } = 15;

    /// <summary>A reminder needs fixture data fetched at most this long ago (a stale start time is not announced).</summary>
    public int FixtureStaleAfterHours { get; set; } = 12;

    /// <summary>Provider data whose own update time is older than this (while live) is stale: nothing is applied.</summary>
    public int LiveStaleAfterMinutes { get; set; } = 20;

    /// <summary>After downtime, a final result is still announced when the match started within this window.</summary>
    public int ResultCatchUpHours { get; set; } = 6;

    /// <summary>Final cards keep being corrected (edited, never re-posted, never re-pinged) for this long.</summary>
    public int FinalCorrectionHours { get; set; } = 6;

    /// <summary>Postponed/cancelled cards are only sent for matches that start at most this long ago (no old news).</summary>
    public int StatusChangeRecentHours { get; set; } = 24;

    public int MaxCatchUpPerGuildPerRun { get; set; } = 2;

    public sealed class ProviderSection
    {
        public VbProviderMode Mode { get; set; } = VbProviderMode.Fixture;
        public VbProviderName Name { get; set; } = VbProviderName.FivbVis;
    }

    public TimeSpan FixtureStaleAfter => TimeSpan.FromHours(FixtureStaleAfterHours);
    public TimeSpan Continuity => TimeSpan.FromMinutes(ContinuityMinutes);
    public TimeSpan LiveFresh => TimeSpan.FromMinutes(LiveFreshMinutes);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Range(string name, int value, int min, int max)
        {
            if (value < min || value > max)
                errors.Add($"Volleyball:{name} must be {min}..{max}");
        }

        Range(nameof(FixtureRefreshHours), FixtureRefreshHours, 1, 24);
        Range(nameof(FixtureRefreshNearMinutes), FixtureRefreshNearMinutes, 15, 360);
        Range(nameof(FixtureWindowPastDays), FixtureWindowPastDays, 1, 30);
        Range(nameof(FixtureWindowAheadDays), FixtureWindowAheadDays, 7, 400);
        Range(nameof(LiveLeadMinutes), LiveLeadMinutes, 20, 180);
        Range(nameof(LiveTrailingHours), LiveTrailingHours, 2, 12);
        Range(nameof(LivePollSeconds), LivePollSeconds, 20, 600);
        Range(nameof(ContinuityMinutes), ContinuityMinutes, 3, 60);
        Range(nameof(ReminderLeadMinutes), ReminderLeadMinutes, 5, 120);
        Range(nameof(LiveFreshMinutes), LiveFreshMinutes, 5, 60);
        Range(nameof(FixtureStaleAfterHours), FixtureStaleAfterHours, 2, 72);
        Range(nameof(LiveStaleAfterMinutes), LiveStaleAfterMinutes, 5, 120);
        Range(nameof(ResultCatchUpHours), ResultCatchUpHours, 0, 48);
        Range(nameof(FinalCorrectionHours), FinalCorrectionHours, 1, 72);
        Range(nameof(StatusChangeRecentHours), StatusChangeRecentHours, 1, 168);
        Range(nameof(MaxCatchUpPerGuildPerRun), MaxCatchUpPerGuildPerRun, 0, 10);
        if (ContinuityMinutes * 60 < 3 * LivePollSeconds)
            errors.Add("Volleyball:ContinuityMinutes must cover at least three live polls (ContinuityMinutes*60 >= 3*LivePollSeconds)");
        if (LiveLeadMinutes <= ReminderLeadMinutes)
            errors.Add("Volleyball:LiveLeadMinutes must be greater than ReminderLeadMinutes (the match must be watched before it can start)");
        if (FixtureRefreshNearMinutes > FixtureStaleAfterHours * 60)
            errors.Add("Volleyball:FixtureRefreshNearMinutes must not exceed FixtureStaleAfterHours");
        return errors;
    }
}
