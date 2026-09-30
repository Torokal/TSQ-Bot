using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions.Application.Automation;

/// <summary>The automation's configuration as it applies right now (a problem, a missing key or no single guild = Disabled).</summary>
public sealed record AutoFootballSettings(
    AutomationMode ConfiguredMode,
    AutomationMode Mode,
    AutoFootballOptions Options,
    AutoTiming? Timing,
    GuildId? Guild,
    bool KeySet,
    IReadOnlyList<string> Problems);

/// <summary>What one pass did (for tests, logs and diagnostics).</summary>
public sealed record AutoRunSummary(AutomationMode Mode, bool Ran, int Discovered, int OddsCalls, int Observed, int Published, int Skipped)
{
    public static AutoRunSummary NotRun(AutomationMode mode) => new(mode, false, 0, 0, 0, 0, 0);
}

/// <summary>Process-wide state of the automation: one pass at a time, and the last configuration warning (logged once).</summary>
public sealed class AutoFootballRuntime
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public string? LastWarning { get; set; }
}

/// <summary>
/// The automatic football opener (docs/predictions/AUTO_FOOTBALL.md). One pass, called by <see cref="AutoFootballWorker"/>
/// about once a minute (never by the 10-second prediction loop), at most one pass at a time per process:
/// <list type="number">
/// <item>Bookkeeping (database only, in every mode): automatic predictions whose uncertain card was found or abandoned.</item>
/// <item>Disabled (configured, or forced by a configuration problem, a missing key or no single allowed guild): nothing
/// else — no HTTP call, no planning.</item>
/// <item>Discovery, about every <see cref="AutoFootballOptions.DiscoveryIntervalMinutes"/>: the provider's whole catalog now
/// and then (which allow-listed MATCH competitions are in season — outrights never; an inactive one joins as soon as a later
/// catalog lists it active), then each such competition's free events list; only matches of the followed teams (exact
/// names, club teams in club competitions, the national team in national ones) become rows — one row per real match, so a
/// derby is ONE row. A changed kickoff
/// re-plans a row that is not published yet; for a published one it is kept next to the original, the open card is locked
/// and the row waits for an administrator. A match that stops appearing is never taken as cancelled.</item>
/// <item>Due rows (publish time reached, Türkiye day of the kickoff, at least the minimum lead left): the odds of the
/// competition's due matches in ONE credit-consuming call (tracked eventIds only), behind the quota guard, at most
/// <see cref="AutoFootballOptions.MaxOddsAttemptsPerEvent"/> attempts per match spread before the deadline; each attempt is
/// recorded before the call, so a restart continues the count. A complete fresh set from the first bookmaker of the
/// priority → Observe: the decision is recorded in the Observe rows only; Live: the prediction is opened through
/// <see cref="PredictionService.PublishAutomaticAsync"/>. No set → the reason, a later attempt, or a skip — never a
/// default price.</item>
/// </list>
/// Row changes run in the predictions write transaction (the unique match key and the claim of an attempt hold across
/// processes too). The provider is told nothing about Discord.
/// </summary>
public sealed class AutoFootballService(
    PredictionStore store,
    PredictionService predictions,
    IFootballOddsProvider provider,
    IOptions<AutoFootballOptions> options,
    DeploymentPolicy deployment,
    IGuildSettingsStore guildSettings,
    IModuleGate moduleGate,
    FootballMarketRules rules,
    ILocalizer localizer,
    AutoFootballRuntime runtime,
    TimeProvider clock,
    ILogger<AutoFootballService> logger)
{
    public const string Market = OddsSelector.Market;

    /// <summary>Matches that started up to this long ago are still read (to notice a changed kickoff of a card published today).</summary>
    public static readonly TimeSpan PastWindow = TimeSpan.FromHours(3);

    public static readonly TimeSpan AuthPause = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RedeliveryDelay = TimeSpan.FromMinutes(5);
    public const int MaxDeliveryFailures = 3;
    private const int Batch = 50;

    public const string PauseAuth = "AUTH_ERROR";
    public const string PauseRate = "RATE_LIMITED";
    public const string PauseProvider = "PROVIDER_UNAVAILABLE";

    public AutoFootballSettings Settings()
    {
        AutoFootballOptions o;
        try
        {
            o = options.Value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return new AutoFootballSettings(AutomationMode.Disabled, AutomationMode.Disabled, new AutoFootballOptions(), null, null, provider.IsConfigured,
                [AutoFootballOptions.Section + " could not be read (a value has the wrong type)"]);
        }

        var problems = o.Problems();
        var configured = o.ParsedMode ?? AutomationMode.Disabled;
        GuildId? guild = deployment.SingleGuild ? new GuildId(deployment.AllowedGuildIds!.Single()) : null;
        var mode = problems.Count > 0 || !provider.IsConfigured || guild is null ? AutomationMode.Disabled : configured;
        return new AutoFootballSettings(configured, mode, o, o.Timing(), guild, provider.IsConfigured, problems);
    }

    public async Task<AutoRunSummary> RunOnceAsync(CancellationToken ct)
    {
        if (!await runtime.Gate.WaitAsync(0, ct))
            return AutoRunSummary.NotRun(AutomationMode.Disabled); // a pass is running: never two at once
        try
        {
            await SyncPublishingAsync(ct);
            var settings = Settings();
            WarnOnce(settings);
            if (settings.Mode == AutomationMode.Disabled || settings.Timing is null || settings.Guild is null)
                return AutoRunSummary.NotRun(settings.Mode);

            var pass = new Pass(settings, settings.Timing, settings.Guild.Value, clock.GetUtcNow());
            var state = await ProviderStateAsync(ct);
            if (state.PauseReason == PauseAuth && state.PausedUntil > pass.Now)
                return new AutoRunSummary(settings.Mode, true, 0, 0, 0, 0, 0);
            if (state.LastDiscoveryAt is not { } last || pass.Now - last >= settings.Options.DiscoveryInterval || state.ActiveCompetitions is null)
                await DiscoverAsync(pass, ct);
            await ReopenForApprovalAsync(pass, ct);
            await ProcessDueAsync(pass, ct);
            return new AutoRunSummary(settings.Mode, true, pass.Discovered, pass.OddsCalls, pass.Observed, pass.Published, pass.Skipped);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private sealed class Pass(AutoFootballSettings settings, AutoTiming timing, GuildId guild, DateTimeOffset now)
    {
        public AutoFootballSettings Settings { get; } = settings;
        public AutoTiming Timing { get; } = timing;
        public GuildId Guild { get; } = guild;
        public DateTimeOffset Now { get; } = now;
        public AutomationMode Mode => Settings.Mode;
        public int Discovered { get; set; }
        public int OddsCalls { get; set; }
        public int Observed { get; set; }
        public int Published { get; set; }
        public int Skipped { get; set; }
    }

    private void WarnOnce(AutoFootballSettings settings)
    {
        var warning = settings.Problems.Count > 0
            ? "configuration problem(s): " + string.Join("; ", settings.Problems)
            : settings.ConfiguredMode != AutomationMode.Disabled && !settings.KeySet
                ? "no API key set (" + AutoFootballOptions.ApiKeySetting + ")"
                : settings.ConfiguredMode != AutomationMode.Disabled && settings.Guild is null
                    ? "the automation needs exactly one allowed guild (Discord:AllowedGuildIds)"
                    : null;
        if (warning is not null && warning != runtime.LastWarning)
            logger.LogWarning("auto_football disabled: {Warning}", warning);
        runtime.LastWarning = warning;
    }

    // ---- bookkeeping ----

    /// <summary>Automatic predictions whose card post was uncertain: found → Published; abandoned → review (never re-posted).</summary>
    private async Task SyncPublishingAsync(CancellationToken ct)
    {
        var rows = await store.AutoEvents.AsNoTracking().Where(a => a.State == AutoEventState.Publishing && a.PredictionId != null)
            .Join(store.Predictions.AsNoTracking(), a => a.PredictionId, p => (long?)p.Id, (a, p) => new { a.Id, p.Status })
            .Where(x => x.Status != PredictionStatus.Publishing).OrderBy(x => x.Id).Take(Batch).ToListAsync(ct);
        foreach (var row in rows)
        {
            var (state, why) = row.Status == PredictionStatus.Abandoned
                ? (AutoEventState.ReviewRequired, AutoBlockReason.DeliveryUnknown)
                : (AutoEventState.Published, AutoBlockReason.None);
            await UpdateRowAsync(row.Id, r =>
            {
                if (r.State != AutoEventState.Publishing)
                    return;
                r.State = state;
                r.Reason = why;
            }, ct);
            if (state == AutoEventState.ReviewRequired)
                logger.LogWarning("auto_football_review event={Event} code={Code}", row.Id, AutoBlockCodes.Code(why));
        }
    }

    // ---- discovery (free calls) ----

    private async Task DiscoverAsync(Pass pass, CancellationToken ct)
    {
        var state = await ProviderStateAsync(ct);
        if (state.PausedUntil > pass.Now)
            return;
        var active = state.ActiveCompetitions;
        if (state.LastCatalogAt is not { } catalogAt || pass.Now - catalogAt >= pass.Settings.Options.CatalogInterval || active is null)
        {
            var sports = await provider.GetSportsAsync(includeInactive: true, ct);
            await RecordAsync(sports, costly: false, ct);
            if (sports.Ok)
            {
                active = string.Join(',', sports.Value!.Where(s => s.Active && !s.HasOutrights && AutoFootballOptions.KnownCompetitions.Contains(s.Key, StringComparer.Ordinal))
                    .Select(s => s.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                await UpdateProviderAsync(p =>
                {
                    p.ActiveCompetitions = active;
                    p.LastCatalogAt = pass.Now;
                }, ct);
            }
            else if (active is null)
            {
                return; // no catalog yet: nothing to read
            }
        }

        var from = pass.Now - PastWindow;
        var to = pass.Now + pass.Settings.Options.DiscoveryHorizon;
        var any = false;
        foreach (var sport in Competitions(pass, active))
        {
            var events = await provider.GetEventsAsync(sport, from, to, ct);
            await RecordAsync(events, costly: false, ct);
            if (!events.Ok)
            {
                if (events.Outcome is ProviderCallOutcome.AuthFailed or ProviderCallOutcome.RateLimited)
                    break;
                continue;
            }

            any = true;
            await UpsertAsync(pass, sport, events.Value!, complete: events.Dropped == 0, to, ct);
        }

        if (any)
            await UpdateProviderAsync(p => p.LastDiscoveryAt = pass.Now, ct);
    }

    private static IEnumerable<string> Competitions(Pass pass, string? active)
    {
        var inSeason = (active ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        return pass.Settings.Options.Competitions.Where(c => inSeason.Contains(c, StringComparer.Ordinal));
    }

    /// <summary>
    /// One transaction per competition: new tracked matches become rows, known ones follow the provider's data. A known match
    /// counts as missing only from a COMPLETE successful list of its own competition (a failed call never gets here; a list
    /// with dropped items is partial) whose time window covers it.
    /// </summary>
    private async Task UpsertAsync(Pass pass, string sport, IReadOnlyList<ProviderEvent> events, bool complete, DateTimeOffset to, CancellationToken ct)
    {
        if (AutoFootballOptions.ScopeOf(sport) is not { } scope)
            return;
        var tracked = events.Where(e => string.Equals(e.SportKey, sport, StringComparison.Ordinal))
            .GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.First())
            .Select(e => (Event: e, Home: TrackedTeams.Match(e.HomeTeam, scope), Away: TrackedTeams.Match(e.AwayTeam, scope)))
            .Where(x => x.Home is not null || x.Away is not null).ToList();
        var ids = tracked.Select(x => x.Event.Id).ToList();
        var locks = new List<long>();
        await PredictionWrites.RunAsync(store.Db, async () =>
        {
            locks.Clear();
            var rows = await store.AutoEvents.Where(a => a.GuildId == pass.Guild.Value && a.Provider == provider.Name && a.MarketKind == Market && a.Mode == pass.Mode &&
                                                         (ids.Contains(a.ExternalEventId) || a.CompetitionKey == sport)).ToListAsync(ct);
            foreach (var (e, home, away) in tracked)
            {
                var row = rows.FirstOrDefault(r => r.ExternalEventId == e.Id);
                var teams = string.Join(',', new[] { home?.Code, away?.Code }.OfType<string>());
                if (row is null)
                {
                    if (e.CommenceTime <= pass.Now)
                        continue; // never plan a match that already started
                    var publishAt = AutoSchedule.PublishAt(e.CommenceTime, pass.Timing);
                    store.AutoEvents.Add(new PredictionAutoEventEntity
                    {
                        GuildId = pass.Guild.Value,
                        Mode = pass.Mode,
                        Provider = provider.Name,
                        ExternalEventId = e.Id,
                        MarketKind = Market,
                        CompetitionKey = sport,
                        HomeTeam = e.HomeTeam,
                        AwayTeam = e.AwayTeam,
                        TrackedTeams = teams,
                        KickoffAt = e.CommenceTime,
                        LatestKickoffAt = e.CommenceTime,
                        PublishAt = publishAt,
                        NextAttemptAt = publishAt,
                        State = AutoEventState.Planned,
                        CreatedAt = pass.Now,
                        UpdatedAt = pass.Now,
                        LastSeenAt = pass.Now,
                    });
                    pass.Discovered++;
                    continue;
                }

                row.LastSeenAt = pass.Now;
                row.MissingCount = 0;
                if (Follow(pass, row, e, teams) is { } predictionToLock)
                    locks.Add(predictionToLock);
            }

            // Known matches of this competition the provider no longer lists: counted, never taken as cancelled.
            foreach (var row in rows.Where(r => complete && r.CompetitionKey == sport && !ids.Contains(r.ExternalEventId) && r.LatestKickoffAt > pass.Now && r.LatestKickoffAt <= to &&
                                                r.State is AutoEventState.Planned or AutoEventState.WaitingForOdds or AutoEventState.Publishing or AutoEventState.Published))
            {
                row.MissingCount++;
                row.UpdatedAt = pass.Now;
                if (row.MissingCount >= 2 && row.State is AutoEventState.Publishing or AutoEventState.Published && row.PredictionId is { } id)
                {
                    row.State = AutoEventState.ReviewRequired;
                    row.Reason = AutoBlockReason.EventMissing;
                    locks.Add(id);
                }
            }

            await store.Db.SaveChangesAsync(ct);
            foreach (var id in locks)
                await predictions.LockForReviewAsync(id, ct); // same transaction: the row and the lock commit together
            return 0;
        }, ct);
        foreach (var id in locks)
            logger.LogWarning("auto_football_review prediction={Prediction}: its match changed or vanished at the provider; entries stopped", id);
    }

    /// <summary>A known match seen again. Returns the prediction to lock when a published match changed.</summary>
    private static long? Follow(Pass pass, PredictionAutoEventEntity row, ProviderEvent e, string teams)
    {
        var sameTeams = string.Equals(row.HomeTeam, e.HomeTeam, StringComparison.Ordinal) && string.Equals(row.AwayTeam, e.AwayTeam, StringComparison.Ordinal);
        var sameKickoff = row.LatestKickoffAt == e.CommenceTime; // the UTC instant; a different offset notation is the same time
        if (sameTeams && sameKickoff)
            return null;
        row.UpdatedAt = pass.Now;
        switch (row.State)
        {
            case AutoEventState.Planned or AutoEventState.WaitingForOdds:
                // Not published yet: follow the provider (a new time is planned again; odds read for the old data are dropped).
                if (!sameTeams && teams.Length == 0)
                {
                    row.State = AutoEventState.Skipped;
                    row.Reason = AutoBlockReason.AmbiguousMatch;
                    return null;
                }

                row.HomeTeam = e.HomeTeam;
                row.AwayTeam = e.AwayTeam;
                row.TrackedTeams = teams;
                row.KickoffAt = e.CommenceTime;
                row.LatestKickoffAt = e.CommenceTime;
                row.PublishAt = AutoSchedule.PublishAt(e.CommenceTime, pass.Timing);
                row.NextAttemptAt = row.State == AutoEventState.WaitingForOdds && row.PublishAt <= pass.Now ? pass.Now : row.PublishAt;
                ClearOdds(row);
                return null;

            case AutoEventState.Publishing or AutoEventState.Published:
                // Published: the original stays; the open card is locked for an administrator (never reopened or extended).
                row.LatestKickoffAt = e.CommenceTime;
                row.State = AutoEventState.ReviewRequired;
                row.Reason = sameTeams ? AutoBlockReason.ScheduleChanged : AutoBlockReason.AmbiguousMatch;
                return row.PredictionId;

            default:
                row.LatestKickoffAt = e.CommenceTime;
                return null;
        }
    }

    private static void ClearOdds(PredictionAutoEventEntity row)
    {
        row.BookmakerKey = null;
        row.BookmakerTitle = null;
        row.OddsUpdatedAt = null;
        row.OddsFetchedAt = null;
        row.HomeOddsX100 = null;
        row.DrawOddsX100 = null;
        row.AwayOddsX100 = null;
        row.RawPrices = null;
    }

    /// <summary>
    /// Observations judged while no bookmaker was approved (MARKET_RULE_UNVERIFIED) are judged again once one is: back to
    /// waiting with their used attempts kept. The old snapshot stays for the record but is never reused (it was not fetched
    /// for this decision; its last_update is untouched). Used-up attempts are reported, never reset.
    /// </summary>
    private async Task ReopenForApprovalAsync(Pass pass, CancellationToken ct)
    {
        if (rules.Usable(pass.Settings.Options.Bookmakers).Count == 0)
            return;
        var changed = await PredictionWrites.RunAsync(store.Db, async () =>
        {
            var rows = await store.AutoEvents.Where(a => a.GuildId == pass.Guild.Value && a.Provider == provider.Name && a.Mode == pass.Mode &&
                                                         a.State == AutoEventState.Observed && a.Reason == AutoBlockReason.MarketRuleUnverified).ToListAsync(ct);
            var reopened = new List<(long Id, bool Exhausted)>();
            foreach (var row in rows.Where(r => AutoSchedule.Deadline(r.KickoffAt, pass.Timing) > pass.Now))
            {
                var exhausted = row.OddsAttempts >= pass.Settings.Options.MaxOddsAttemptsPerEvent;
                row.State = exhausted ? AutoEventState.Skipped : AutoEventState.WaitingForOdds;
                row.Reason = exhausted ? AutoBlockReason.AttemptsExhausted : AutoBlockReason.None;
                row.NextAttemptAt = exhausted ? null : pass.Now;
                row.OddsFetchedAt = null;
                row.UpdatedAt = pass.Now;
                reopened.Add((row.Id, exhausted));
            }

            await store.Db.SaveChangesAsync(ct);
            return reopened;
        }, ct);
        foreach (var (id, exhausted) in changed)
        {
            if (exhausted)
                pass.Skipped++;
            logger.LogInformation("auto_football_recheck event={Event} code={Code}", id, exhausted ? AutoBlockCodes.Code(AutoBlockReason.AttemptsExhausted) : "REOPENED");
        }
    }

    // ---- due matches ----

    private async Task ProcessDueAsync(Pass pass, CancellationToken ct)
    {
        var rows = await store.AutoEvents.AsNoTracking()
            .Where(a => a.GuildId == pass.Guild.Value && a.Provider == provider.Name && a.Mode == pass.Mode &&
                        (a.State == AutoEventState.Planned || a.State == AutoEventState.WaitingForOdds) && a.NextAttemptAt != null && a.NextAttemptAt <= pass.Now)
            .OrderBy(a => a.KickoffAt).Take(Batch).ToListAsync(ct);
        var state = await ProviderStateAsync(ct);
        var inSeason = (state.ActiveCompetitions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        // Live with the module disabled, or without any bookmaker whose full-time rule is verified: no paid call for a card
        // that could not be opened anyway.
        var moduleOff = pass.Mode == AutomationMode.Live && !await moduleGate.IsEnabledAsync(pass.Guild, PredictionsModule.ModuleIdTyped, ct);
        var ruleUnverified = pass.Mode == AutomationMode.Live && rules.Usable(pass.Settings.Options.Bookmakers).Count == 0;
        var fetch = new List<PredictionAutoEventEntity>();
        foreach (var row in rows)
        {
            var window = AutoSchedule.Window(pass.Now, row.KickoffAt, pass.Timing);
            if (window == PublishWindow.TooLate)
            {
                await SkipAsync(pass, row.Id, row.MissingCount > 0 ? AutoBlockReason.EventMissing : row.Reason == AutoBlockReason.None ? AutoBlockReason.TooLateToPublish : row.Reason, ct);
                continue;
            }

            if (window == PublishWindow.NotYet)
            {
                await UpdateRowAsync(row.Id, r => r.NextAttemptAt = r.PublishAt > pass.Now ? r.PublishAt : pass.Now + pass.Settings.Options.DiscoveryInterval, ct);
                continue;
            }

            if (row.MissingCount > 0)
            {
                await UpdateRowAsync(row.Id, r => r.NextAttemptAt = pass.Now + pass.Settings.Options.DiscoveryInterval, ct); // wait until it is seen again
                continue;
            }

            if (!inSeason.Contains(row.CompetitionKey, StringComparer.Ordinal) || !pass.Settings.Options.Competitions.Contains(row.CompetitionKey, StringComparer.Ordinal))
            {
                await SkipAsync(pass, row.Id, AutoBlockReason.UnsupportedCompetition, ct);
                continue;
            }

            if (moduleOff || ruleUnverified)
            {
                await WaitOrSkipAsync(pass, row.Id, moduleOff ? AutoBlockReason.ModuleDisabled : AutoBlockReason.MarketRuleUnverified, ct);
                continue;
            }

            if (FreshSnapshot(pass, row) is { } kept)
            {
                await DecideAsync(pass, row, kept, ct); // e.g. a card Discord refused: the same fixed set, no new call
                continue;
            }

            if (row.OddsAttempts >= pass.Settings.Options.MaxOddsAttemptsPerEvent)
            {
                await SkipAsync(pass, row.Id, row.Reason == AutoBlockReason.None ? AutoBlockReason.NoOdds : row.Reason, ct);
                continue;
            }

            fetch.Add(row);
        }

        foreach (var group in fetch.GroupBy(r => r.CompetitionKey, StringComparer.Ordinal))
            await FetchAndDecideAsync(pass, group.Key, group.ToList(), ct);
    }

    private static SelectedOdds? FreshSnapshot(Pass pass, PredictionAutoEventEntity row) =>
        row.OddsFetchedAt is not null && row.OddsUpdatedAt is { } updated && pass.Now - updated <= pass.Settings.Options.MaxOddsAge &&
        row.HomeOddsX100 is { } h && row.DrawOddsX100 is { } d && row.AwayOddsX100 is { } a
            ? new SelectedOdds(row.BookmakerKey ?? "", row.BookmakerTitle ?? "", updated, h, d, a, 0, 0, 0)
            : null;

    private async Task FetchAndDecideAsync(Pass pass, string sport, List<PredictionAutoEventEntity> due, CancellationToken ct)
    {
        if (await SpendBlockAsync(pass, ct) is { } blocked)
        {
            foreach (var row in due)
                await WaitOrSkipAsync(pass, row.Id, blocked, ct); // no attempt is used while the provider is paused
            return;
        }

        // Claim the attempt BEFORE the call (restart-safe; a second process sees it taken and does not call as well).
        var claimed = await PredictionWrites.RunAsync(store.Db, async () =>
        {
            var ids = due.Select(r => r.Id).ToList();
            var rows = await store.AutoEvents.Where(a => ids.Contains(a.Id) && (a.State == AutoEventState.Planned || a.State == AutoEventState.WaitingForOdds) &&
                                                         a.NextAttemptAt != null && a.NextAttemptAt <= pass.Now).ToListAsync(ct);
            foreach (var row in rows)
            {
                row.OddsAttempts++;
                row.LastAttemptAt = pass.Now;
                row.State = AutoEventState.WaitingForOdds;
                row.NextAttemptAt = AutoSchedule.NextAttempt(pass.Now, AutoSchedule.Deadline(row.KickoffAt, pass.Timing), row.OddsAttempts,
                    pass.Settings.Options.MaxOddsAttemptsPerEvent) ?? AutoSchedule.Deadline(row.KickoffAt, pass.Timing);
                row.UpdatedAt = pass.Now;
            }

            await store.Db.SaveChangesAsync(ct);
            return rows.Select(r => (r.Id, r.ExternalEventId)).ToList();
        }, ct);
        if (claimed.Count == 0)
            return;

        var call = await provider.GetOddsAsync(sport, claimed.Select(c => c.ExternalEventId).ToList(), ct);
        pass.OddsCalls++;
        await RecordAsync(call, costly: true, ct);
        if (!call.Ok)
        {
            var reason = Reason(call.Outcome);
            foreach (var (id, _) in claimed)
                await UpdateRowAsync(id, r => r.Reason = reason, ct);
            return;
        }

        var fresh = await store.AutoEvents.AsNoTracking().Where(a => claimed.Select(c => c.Id).Contains(a.Id)).ToListAsync(ct);
        foreach (var row in fresh)
        {
            var match = call.Value!.FirstOrDefault(e => string.Equals(e.Id, row.ExternalEventId, StringComparison.Ordinal));
            if (match is null)
            {
                await UpdateRowAsync(row.Id, r => r.Reason = AutoBlockReason.NoOdds, ct);
                continue;
            }

            if (!string.Equals(match.SportKey, row.CompetitionKey, StringComparison.Ordinal) || !string.Equals(match.HomeTeam, row.HomeTeam, StringComparison.Ordinal) ||
                !string.Equals(match.AwayTeam, row.AwayTeam, StringComparison.Ordinal))
            {
                await UpdateRowAsync(row.Id, r => r.Reason = AutoBlockReason.AmbiguousMatch, ct); // rediscovery decides
                continue;
            }

            if (match.CommenceTime != row.KickoffAt)
            {
                // The kickoff moved since discovery: plan again from the new time, publish nothing now.
                await UpdateRowAsync(row.Id, r =>
                {
                    r.KickoffAt = match.CommenceTime;
                    r.LatestKickoffAt = match.CommenceTime;
                    r.PublishAt = AutoSchedule.PublishAt(match.CommenceTime, pass.Timing);
                    r.NextAttemptAt = r.PublishAt > pass.Now ? r.PublishAt : pass.Now + AutoSchedule.MinAttemptGap;
                    r.Reason = AutoBlockReason.None;
                    ClearOdds(r);
                }, ct);
                continue;
            }

            var choice = OddsSelector.Choose(match, pass.Settings.Options.Bookmakers, rules.ApprovedBookmakers, pass.Now, pass.Settings.Options.MaxOddsAge);
            if (choice.Odds is not { } odds)
            {
                if (pass.Mode == AutomationMode.Observe && choice.Unapproved is { } candidate)
                {
                    // Observe shows what the data offers even when OUR rule would not publish it — with that reason.
                    await UpdateRowAsync(row.Id, r =>
                    {
                        Snapshot(r, candidate, pass.Now);
                        r.State = AutoEventState.Observed;
                        r.Reason = choice.Reason;
                        r.NextAttemptAt = null;
                    }, ct);
                    pass.Observed++;
                    continue;
                }

                await UpdateRowAsync(row.Id, r => r.Reason = choice.Reason, ct);
                continue;
            }

            await UpdateRowAsync(row.Id, r =>
            {
                Snapshot(r, odds, pass.Now);
                r.Reason = AutoBlockReason.None;
            }, ct);
            await DecideAsync(pass, row, odds, ct);
        }
    }

    private static void Snapshot(PredictionAutoEventEntity row, SelectedOdds odds, DateTimeOffset now)
    {
        row.BookmakerKey = odds.BookmakerKey;
        row.BookmakerTitle = odds.BookmakerTitle;
        row.OddsUpdatedAt = odds.LastUpdate;
        row.OddsFetchedAt = now;
        row.HomeOddsX100 = odds.HomeX100;
        row.DrawOddsX100 = odds.DrawX100;
        row.AwayOddsX100 = odds.AwayX100;
        row.RawPrices = OddsSelector.Raw(odds.HomeRaw) + "|" + OddsSelector.Raw(odds.DrawRaw) + "|" + OddsSelector.Raw(odds.AwayRaw);
    }

    /// <summary>Observe: record the decision only. Live: open the prediction (every gate checked again there).</summary>
    private async Task DecideAsync(Pass pass, PredictionAutoEventEntity row, SelectedOdds odds, CancellationToken ct)
    {
        var lockAt = AutoSchedule.LockAt(row.KickoffAt, pass.Timing);
        if (pass.Mode == AutomationMode.Observe)
        {
            await UpdateRowAsync(row.Id, r =>
            {
                r.State = AutoEventState.Observed;
                r.Reason = AutoBlockReason.None;
                r.NextAttemptAt = null;
            }, ct);
            pass.Observed++;
            logger.LogInformation("auto_football_observed event={Event} competition={Competition} kickoff={Kickoff:O} lock={LockAt:O} bookmaker={Bookmaker} odds={Home}/{Draw}/{Away}",
                row.ExternalEventId, row.CompetitionKey, row.KickoffAt, lockAt, odds.BookmakerKey, Odds.Format(odds.HomeX100), Odds.Format(odds.DrawX100),
                Odds.Format(odds.AwayX100));
            return;
        }

        var language = (await guildSettings.GetAsync(pass.Guild, ct)).Language;
        var scope = AutoFootballOptions.ScopeOf(row.CompetitionKey) ?? TeamScope.Club;
        var (title, rulesText, outcomes) = AutoFootballTexts.Build(localizer, language, row.HomeTeam, row.AwayTeam, scope, pass.Settings.Options.LockBeforeKickoffMinutes, odds);
        var plan = new AutoPublishPlan(
            row.Id,
            pass.Guild,
            title,
            rulesText,
            outcomes,
            lockAt,
            AutoSchedule.Deadline(row.KickoffAt, pass.Timing),
            PublishKey(pass.Guild, row),
            new AutoCardInfo(row.KickoffAt, odds.BookmakerTitle, odds.LastUpdate));
        var outcome = await predictions.PublishAutomaticAsync(plan, () => Settings().Mode == AutomationMode.Live, ct);
        switch (outcome.Result)
        {
            case AutoPublishResult.Published or AutoPublishResult.Uncertain:
                pass.Published++;
                break;
            case AutoPublishResult.Refused:
                var failures = await store.AutoEvents.AsNoTracking().Where(a => a.Id == row.Id).Select(a => a.DeliveryFailures).FirstAsync(ct);
                if (failures >= MaxDeliveryFailures)
                    await SkipAsync(pass, row.Id, AutoBlockReason.ChannelUnavailable, ct);
                else
                    await UpdateRowAsync(row.Id, r => r.NextAttemptAt = pass.Now + RedeliveryDelay, ct);
                break;
            case AutoPublishResult.Blocked when outcome.Reason is AutoBlockReason.TooLateToPublish or AutoBlockReason.CardInvalid:
                await SkipAsync(pass, row.Id, outcome.Reason, ct);
                break;
            case AutoPublishResult.Blocked when outcome.Reason != AutoBlockReason.None:
                // Module off or channel unusable right now: wait (the published odds snapshot is kept while it is fresh).
                await WaitOrSkipAsync(pass, row.Id, outcome.Reason, ct);
                break;
        }
    }

    /// <summary>The prediction's unique publish key: a hash of guild, provider, match and market (never a tournament).</summary>
    public static string PublishKey(GuildId guild, PredictionAutoEventEntity row)
    {
        var text = string.Join('|', guild.Value.ToString(CultureInfo.InvariantCulture), row.Provider, row.ExternalEventId, row.MarketKind);
        return "auto:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..40];
    }

    private static AutoBlockReason Reason(ProviderCallOutcome outcome) => outcome switch
    {
        ProviderCallOutcome.AuthFailed or ProviderCallOutcome.NotConfigured => AutoBlockReason.AuthError,
        ProviderCallOutcome.RateLimited => AutoBlockReason.QuotaPaused,
        _ => AutoBlockReason.ProviderUnavailable,
    };

    private async Task SkipAsync(Pass pass, long id, AutoBlockReason reason, CancellationToken ct)
    {
        await UpdateRowAsync(id, r =>
        {
            if (r.State is not (AutoEventState.Planned or AutoEventState.WaitingForOdds))
                return;
            r.State = AutoEventState.Skipped;
            r.Reason = reason;
            r.NextAttemptAt = null;
        }, ct);
        pass.Skipped++;
        var code = AutoBlockCodes.Code(reason);
        logger.LogInformation("auto_football_skipped event={Event} code={Code}", id, code);
    }

    /// <summary>Try again later without using an attempt — or skip when no safe moment is left.</summary>
    private async Task WaitOrSkipAsync(Pass pass, long id, AutoBlockReason reason, CancellationToken ct)
    {
        var state = await ProviderStateAsync(ct);
        var next = state.PausedUntil is { } until && until > pass.Now ? until : pass.Now + pass.Settings.Options.DiscoveryInterval;
        var row = await store.AutoEvents.AsNoTracking().FirstAsync(a => a.Id == id, ct);
        if (next >= AutoSchedule.Deadline(row.KickoffAt, pass.Timing))
        {
            await SkipAsync(pass, id, reason, ct);
            return;
        }

        await UpdateRowAsync(id, r =>
        {
            r.Reason = reason;
            r.NextAttemptAt = next;
        }, ct);
    }

    // ---- diagnostics (database only, no provider call) ----

    /// <summary>
    /// /bot status lines: the mode (and why it is off), then — when the automation is configured — the last discovery and the
    /// competitions in season, today's matches by outcome, the provider's last reported credits, the last error and the
    /// matches waiting for an administrator. Never the key.
    /// </summary>
    public async Task<IReadOnlyList<HealthEntry>> HealthAsync(CancellationToken ct)
    {
        var s = Settings();
        var entries = new List<HealthEntry>();
        if (s.Problems.Count > 0)
            entries.Add(new HealthEntry("predictions.health.auto", HealthState.Degraded, "predictions.health.auto_config", [s.Problems.Count]));
        else if (s.ConfiguredMode == AutomationMode.Disabled)
            return [new HealthEntry("predictions.health.auto", HealthState.Healthy, "predictions.health.auto_off")];
        else if (!s.KeySet)
            entries.Add(new HealthEntry("predictions.health.auto", HealthState.NotConfigured, "predictions.health.auto_no_key", [AutoFootballOptions.ApiKeySetting]));
        else if (s.Guild is null)
            entries.Add(new HealthEntry("predictions.health.auto", HealthState.NotConfigured, "predictions.health.auto_no_guild"));
        else
            entries.Add(new HealthEntry("predictions.health.auto", HealthState.Healthy, "predictions.health.auto_mode", [s.Mode.ToString()]));
        if (s.ConfiguredMode == AutomationMode.Live && rules.Usable(s.Options.Bookmakers).Count == 0)
            entries.Add(new HealthEntry("predictions.health.auto_rule", HealthState.Degraded, "predictions.health.auto_rule_unverified"));

        var state = await ProviderStateAsync(ct);
        var now = clock.GetUtcNow();
        entries.Add(new HealthEntry("predictions.health.auto_discovery", state.LastDiscoveryAt is null ? HealthState.NotConfigured : HealthState.Healthy,
            "predictions.health.auto_discovery_value", [Time(state.LastDiscoveryAt), string.IsNullOrEmpty(state.ActiveCompetitions) ? "—" : state.ActiveCompetitions]));

        if (s.Timing is { } timing)
        {
            var day = AutoSchedule.LocalDay(now, timing.Zone);
            var from = AutoSchedule.At(day, TimeOnly.MinValue, timing.Zone);
            var to = AutoSchedule.At(day.AddDays(1), TimeOnly.MinValue, timing.Zone);
            var today = await store.AutoEvents.AsNoTracking().Where(a => a.Mode == s.ConfiguredMode && a.KickoffAt >= from && a.KickoffAt < to)
                .GroupBy(a => a.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            int Count(params AutoEventState[] states) => today.Where(t => states.Contains(t.Key)).Sum(t => t.Count);
            entries.Add(new HealthEntry("predictions.health.auto_today", HealthState.Healthy, "predictions.health.auto_today_value",
            [
                today.Sum(t => t.Count), Count(AutoEventState.Published, AutoEventState.Publishing), Count(AutoEventState.Observed),
                Count(AutoEventState.Planned, AutoEventState.WaitingForOdds), Count(AutoEventState.Skipped),
            ]));
        }

        var low = state.RemainingCredits is { } remaining && remaining - state.UnmeasuredCalls - 1 < s.Options.CreditReserve;
        entries.Add(new HealthEntry("predictions.health.auto_quota", low || state.PausedUntil > now ? HealthState.Degraded : HealthState.Healthy,
            "predictions.health.auto_quota_value",
            [
                state.RemainingCredits?.ToString(CultureInfo.InvariantCulture) ?? "?", Time(state.MeasuredAt), state.UnmeasuredCalls,
                state.PausedUntil > now ? (state.PauseReason ?? "?") + " → " + Time(state.PausedUntil) : "—",
            ]));
        if (state.LastError is { } error && state.LastErrorAt > now - TimeSpan.FromDays(1))
            entries.Add(new HealthEntry("predictions.health.auto_error", HealthState.Degraded, "predictions.health.auto_error_value", [error, Time(state.LastErrorAt)]));
        var review = await store.AutoEvents.AsNoTracking().CountAsync(a => a.State == AutoEventState.ReviewRequired, ct);
        if (review > 0)
            entries.Add(new HealthEntry("predictions.health.auto_review", HealthState.Degraded, "predictions.health.auto_review_value", [review]));
        return entries;
    }

    private static string Time(DateTimeOffset? at) => at is { } value ? DiscordText.Timestamp(value, 'R') : "—";

    // ---- quota and provider state (persisted) ----

    /// <summary>Why a credit-consuming call may not be made now (null: it may).</summary>
    private async Task<AutoBlockReason?> SpendBlockAsync(Pass pass, CancellationToken ct)
    {
        var state = await ProviderStateAsync(ct);
        if (state.PausedUntil is { } until && until > pass.Now)
            return state.PauseReason switch
            {
                PauseAuth => AutoBlockReason.AuthError,
                PauseRate => AutoBlockReason.QuotaPaused,
                _ => AutoBlockReason.ProviderUnavailable,
            };
        if (state.RemainingCredits is { } remaining)
            return remaining - state.UnmeasuredCalls - 1 >= pass.Settings.Options.CreditReserve ? null : AutoBlockReason.QuotaPaused;
        // No measurement yet: one call to learn it — never a series of blind calls.
        return state.UnmeasuredCalls == 0 ? null : AutoBlockReason.QuotaPaused;
    }

    private async Task RecordAsync<T>(ProviderCall<T> call, bool costly, CancellationToken ct) where T : class
    {
        var now = clock.GetUtcNow();
        await UpdateProviderAsync(p =>
        {
            if (call.Quota.Known)
            {
                p.RemainingCredits = call.Quota.Remaining;
                p.UsedCredits = call.Quota.Used;
                p.LastCost = call.Quota.LastCost;
                p.MeasuredAt = now;
                p.UnmeasuredCalls = 0;
            }
            else if (costly && (call.Ok || call.MayHaveCost))
            {
                p.UnmeasuredCalls++; // the cost is unknown: counted as spent
            }

            if (costly)
                p.CostlyCalls++;

            switch (call.Outcome)
            {
                case ProviderCallOutcome.Ok:
                    p.ConsecutiveFailures = 0;
                    if (p.PauseReason is PauseProvider or PauseRate)
                    {
                        p.PausedUntil = null;
                        p.PauseReason = null;
                    }

                    break;
                case ProviderCallOutcome.AuthFailed or ProviderCallOutcome.NotConfigured:
                    Pause(p, now + AuthPause, PauseAuth);
                    break;
                case ProviderCallOutcome.RateLimited:
                    var wait = call.RetryAfter is { } after ? after : TimeSpan.FromMinutes(5);
                    Pause(p, now + (wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait), PauseRate);
                    break;
                default:
                    p.ConsecutiveFailures++;
                    var backoff = TimeSpan.FromMinutes(1 << Math.Min(p.ConsecutiveFailures - 1, 5)); // 1, 2, 4 … minutes
                    backoff = backoff > MaxBackoff ? MaxBackoff : backoff;
                    Pause(p, now + backoff, PauseProvider);
                    break;
            }

            if (call.Outcome != ProviderCallOutcome.Ok)
            {
                p.LastError = call.Outcome + (call.HttpStatus is { } status ? " " + status.ToString(CultureInfo.InvariantCulture) : "");
                p.LastErrorAt = now;
            }
        }, ct);
    }

    private static void Pause(PredictionAutoProviderEntity p, DateTimeOffset until, string reason)
    {
        p.PausedUntil = until;
        p.PauseReason = reason;
    }

    public async Task<PredictionAutoProviderEntity> ProviderStateAsync(CancellationToken ct) =>
        await store.AutoProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Provider == provider.Name, ct) ?? new PredictionAutoProviderEntity { Provider = provider.Name };

    private Task UpdateProviderAsync(Action<PredictionAutoProviderEntity> change, CancellationToken ct) =>
        PredictionWrites.RunAsync(store.Db, async () =>
        {
            var row = await store.AutoProviders.FirstOrDefaultAsync(p => p.Provider == provider.Name, ct);
            if (row is null)
            {
                row = new PredictionAutoProviderEntity { Provider = provider.Name };
                store.AutoProviders.Add(row);
            }

            change(row);
            row.UpdatedAt = clock.GetUtcNow();
            await store.Db.SaveChangesAsync(ct);
            return 0;
        }, ct);

    private Task UpdateRowAsync(long id, Action<PredictionAutoEventEntity> change, CancellationToken ct) =>
        PredictionWrites.RunAsync(store.Db, async () =>
        {
            var row = await store.AutoEvents.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (row is null)
                return 0;
            change(row);
            row.UpdatedAt = clock.GetUtcNow();
            await store.Db.SaveChangesAsync(ct);
            return 0;
        }, ct);
}
