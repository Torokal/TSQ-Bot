using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

/// <summary>
/// Section "Formula1". Every cadence is state-aware and bounded; values are validated at startup. Provider selection is
/// per capability (<see cref="ProviderSection"/>), so one provider can be replaced without touching the rest.
/// </summary>
public sealed class Formula1Options
{
    public const string Section = "Formula1";

    public ProviderSection Provider { get; set; } = new();

    /// <summary>Schedule refresh outside race weekends (6–12 h is plenty for a calendar).</summary>
    public int ScheduleRefreshHours { get; set; } = 6;

    /// <summary>Schedule refresh when a session is within 48 h (late time changes).</summary>
    public int ScheduleRefreshWeekendMinutes { get; set; } = 60;

    /// <summary>Routine standings refresh (standings only change after sprints and races).</summary>
    public int StandingsRefreshHours { get; set; } = 6;

    /// <summary>Schedule data older than this is stale: shown with a warning, never used for new notifications.</summary>
    public int ScheduleStaleAfterHours { get; set; } = 48;

    /// <summary>Standings older than this are shown with a stale warning.</summary>
    public int StandingsStaleAfterHours { get; set; } = 24;

    /// <summary>A classification fetched longer ago than this is stale: no new result message and no edit is made from it.</summary>
    public int ResultStaleAfterMinutes { get; set; } = 90;

    /// <summary>The live lifecycle connection is prepared this long before a scheduled start (OpenF1: live data from 30 min before).</summary>
    public int LifecycleLeadMinutes { get; set; } = 35;

    /// <summary>A session stays "active" (listener + reconciliation) this long after its planned end unless it finished.</summary>
    public int LifecycleTrailingHours { get; set; } = 4;

    /// <summary>REST reconciliation of lifecycle events while the live stream is connected (safety net only).</summary>
    public int LifecycleReconcileSeconds { get; set; } = 120;

    /// <summary>REST reconciliation cadence when no live stream is connected (fixture mode, outage).</summary>
    public int LifecycleReconcileFallbackSeconds { get; set; } = 60;

    /// <summary>A "started" card is only sent this soon after the PROVIDER's start timestamp (no late "race started").</summary>
    public int StartFreshMinutes { get; set; } = 10;

    /// <summary>Results polling after a session gives up after this long (no fake result ever).</summary>
    public int ResultsMaxWaitHours { get; set; } = 12;

    /// <summary>Result messages keep being corrected (edited, never re-posted, never re-pinged) for this long.</summary>
    public int ResultCorrectionHours { get; set; } = 24;

    /// <summary>After downtime, results of sessions that ended within this window may still be announced (bounded catch-up).</summary>
    public int ResultCatchUpHours { get; set; } = 6;

    public int MaxCatchUpPerGuildPerRun { get; set; } = 3;

    /// <summary>A classification with fewer entries is treated as incomplete (F1 grids have 20+ cars).</summary>
    public int MinResultEntries { get; set; } = 10;

    /// <summary>
    /// Bounded window after a sprint/race result in which standings are re-checked (with backoff) and a change edits the
    /// same result message. Jolpica is volunteer-maintained and may lag, hence hours rather than minutes.
    /// </summary>
    public int StandingsSettleWindowMinutes { get; set; } = 180;

    /// <summary>Max provider start-time difference for mapping a provider session onto the schedule (fail closed beyond).</summary>
    public int SessionMatchToleranceHours { get; set; } = 6;

    /// <summary>Rows shown in the standings section of a result card (full tables: /f1 standings).</summary>
    public int CardStandingsRows { get; set; } = 10;

    public sealed class ProviderSection
    {
        public F1ProviderMode Mode { get; set; } = F1ProviderMode.Fixture;
        public F1ScheduleProviderName Schedule { get; set; } = F1ScheduleProviderName.Jolpica;
        public F1LifecycleProviderName Lifecycle { get; set; } = F1LifecycleProviderName.OpenF1;
        public F1ResultsProviderName Results { get; set; } = F1ResultsProviderName.OpenF1;
        public F1StandingsProviderName Standings { get; set; } = F1StandingsProviderName.Jolpica;
    }

    public TimeSpan ScheduleStaleAfter => TimeSpan.FromHours(ScheduleStaleAfterHours);
    public TimeSpan StandingsStaleAfter => TimeSpan.FromHours(StandingsStaleAfterHours);
    public TimeSpan ResultStaleAfter => TimeSpan.FromMinutes(ResultStaleAfterMinutes);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Range(string name, int value, int min, int max)
        {
            if (value < min || value > max)
                errors.Add($"Formula1:{name} must be {min}..{max}");
        }

        Range(nameof(ScheduleRefreshHours), ScheduleRefreshHours, 1, 24);
        Range(nameof(ScheduleRefreshWeekendMinutes), ScheduleRefreshWeekendMinutes, 15, 360);
        Range(nameof(StandingsRefreshHours), StandingsRefreshHours, 1, 48);
        Range(nameof(ScheduleStaleAfterHours), ScheduleStaleAfterHours, 12, 336);
        Range(nameof(StandingsStaleAfterHours), StandingsStaleAfterHours, 1, 336);
        Range(nameof(ResultStaleAfterMinutes), ResultStaleAfterMinutes, 10, 360);
        Range(nameof(LifecycleLeadMinutes), LifecycleLeadMinutes, 5, 120);
        Range(nameof(LifecycleTrailingHours), LifecycleTrailingHours, 1, 12);
        // OpenF1 free tier: 30 requests/minute. Reconciliation must stay far below it.
        Range(nameof(LifecycleReconcileSeconds), LifecycleReconcileSeconds, 30, 900);
        Range(nameof(LifecycleReconcileFallbackSeconds), LifecycleReconcileFallbackSeconds, 20, 900);
        Range(nameof(StartFreshMinutes), StartFreshMinutes, 1, 60);
        Range(nameof(ResultsMaxWaitHours), ResultsMaxWaitHours, 1, 48);
        Range(nameof(ResultCorrectionHours), ResultCorrectionHours, 1, 168);
        Range(nameof(ResultCatchUpHours), ResultCatchUpHours, 0, 48);
        Range(nameof(MaxCatchUpPerGuildPerRun), MaxCatchUpPerGuildPerRun, 0, 10);
        Range(nameof(MinResultEntries), MinResultEntries, 1, 30);
        Range(nameof(StandingsSettleWindowMinutes), StandingsSettleWindowMinutes, 5, 1440);
        Range(nameof(SessionMatchToleranceHours), SessionMatchToleranceHours, 1, 24);
        Range(nameof(CardStandingsRows), CardStandingsRows, 3, 22);
        if (StandingsSettleWindowMinutes > ResultCorrectionHours * 60)
            errors.Add("Formula1:StandingsSettleWindowMinutes must not exceed ResultCorrectionHours (standings edit the result message)");
        return errors;
    }
}
