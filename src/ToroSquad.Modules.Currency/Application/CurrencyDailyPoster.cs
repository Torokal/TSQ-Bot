using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Modules.Currency.Providers;

namespace ToroSquad.Modules.Currency.Application;

/// <summary>
/// When the daily card is due: 09:00 Türkiye local time (Europe/Istanbul — never a fixed UTC hour), with a bounded catch-up
/// window after it. The day is the Türkiye calendar date, never the UTC date.
/// </summary>
public static class CurrencyDailySchedule
{
    public const string Kind = "currency-daily";

    public static readonly TimeOnly DueTime = new(9, 0);

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>09:00 of that Türkiye day as an instant.</summary>
    public static DateTimeOffset DueAt(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(DueTime);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// The absolute delivery deadline of that day's card (the outbox expiry): 09:00 + catch-up + delivery grace, e.g. 09:35.
    /// Fixed per day — not "staged + grace" — so no card, whenever it was staged, reaches Discord after it.
    /// </summary>
    public static DateTimeOffset DeliveryDeadline(DateOnly day, TimeZoneInfo zone, TimeSpan catchUp) =>
        DueAt(day, zone) + catchUp + CurrencyDailyPoster.DeliveryGrace;

    /// <summary>The outbox source key: one daily card per Türkiye day ("day:2026-09-29").</summary>
    public static string SourceKey(DateOnly day) => "day:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public enum DailyPassOutcome
{
    NotDue = 0,
    WindowPassed = 1,
    NoTarget = 2,
    AlreadyQueued = 3,
    NoData = 4,
    Queued = 5,
}

/// <summary>
/// The daily 09:00 card. Each pass, inside the day's window [09:00, 09:00 + <see cref="CurrencyOptions.DailyCatchUpMinutes"/>]:
/// for every guild with the module enabled (and allowed) whose card for today is not in the outbox yet, it takes the three
/// quotes from <see cref="MarketQuoteService"/> (same providers, cache and single flight as the commands — one Currency and
/// one Gold fetch at most), renders one combined card and stages it into the persistent outbox (logical key: guild +
/// module + "day:&lt;Türkiye date&gt;" + channel + kind). The outbox delivers it; this class never talks to Discord. So a
/// restart, a redeploy or two overlapping passes can never produce a second card for the same day (the row exists, or the
/// unique key refuses it), and an already queued card is never restaged (no edit of a delivered card either).
/// Before 09:00 nothing happens; after the window nothing is caught up. No usable price at all → nothing is posted, the next
/// pass tries again, and past the window the day is skipped with a warning. A disabled module gets no card (and no quote
/// is fetched for it). Never pings.
/// </summary>
public sealed class CurrencyDailyPoster(
    IServiceScopeFactory scopes,
    MarketQuoteService quotes,
    CurrencyCardRenderer cards,
    IGuildGateway guilds,
    DeploymentPolicy deployment,
    IOptions<CurrencyOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<CurrencyDailyPoster> logger) : IDisposable
{
    /// <summary>How often a pass runs inside the window (until the card is queued or the window ends).</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>The longest sleep between passes (the next due time is recomputed from the clock every pass).</summary>
    public static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    /// <summary>
    /// Only so the outbox can still deliver a card staged at the very end of the window: a queued card Discord did not take
    /// by the end of the window plus this (09:35) expires — no late "morning" card.
    /// </summary>
    public static readonly TimeSpan DeliveryGrace = TimeSpan.FromMinutes(5);

    private const int SqliteConstraint = 19;

    private readonly SemaphoreSlim _pass = new(1, 1);

    // Per Türkiye day, in memory only (for logging once, not for deciding: the outbox decides).
    private DateOnly _day;
    private bool _noDataToday;
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);

    public void Dispose() => _pass.Dispose();

    public async Task<DailyPassOutcome> RunAsync(string reason, CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            return await RunPassAsync(reason, ct);
        }
        finally
        {
            _pass.Release();
        }
    }

    /// <summary>
    /// When the next pass is due, recomputed from the clock each time (no fixed 24-hour sleep, so a restart or deploy never
    /// shifts the schedule): until today's 09:00, every minute inside the window, else until tomorrow's 09:00 — at most an hour.
    /// </summary>
    public TimeSpan NextDelay()
    {
        if (!ProviderFormats.TryTurkeyZone(out var zone))
            return MaxSleep;
        var now = clock.GetUtcNow();
        var today = CurrencyDailySchedule.LocalDate(now, zone);
        var due = CurrencyDailySchedule.DueAt(today, zone);
        if (now < due)
            return Min(due - now, MaxSleep);
        if (now <= due + options.Value.DailyCatchUp)
            return RetryInterval;
        return Min(CurrencyDailySchedule.DueAt(today.AddDays(1), zone) - now, MaxSleep);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private async Task<DailyPassOutcome> RunPassAsync(string reason, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        if (!ProviderFormats.TryTurkeyZone(out var zone))
        {
            logger.LogError("currency_daily_failed reason={Reason}: time zone {Zone} is unknown", reason, ProviderFormats.TurkeyTimeZoneId);
            return DailyPassOutcome.NotDue;
        }

        var today = CurrencyDailySchedule.LocalDate(now, zone);
        if (today != _day)
        {
            _day = today;
            _noDataToday = false;
            _noted.Clear();
        }

        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var due = CurrencyDailySchedule.DueAt(today, zone);
        var end = due + o.DailyCatchUp;
        if (now < due)
            return DailyPassOutcome.NotDue;
        if (now > end)
        {
            if (_noDataToday && _noted.Add("skipped"))
                logger.LogWarning("currency_daily_skipped localDate={Date} channel={Channel}: no usable price from any provider until {End:HH:mm} (Türkiye); next card tomorrow",
                    date, o.ChannelId, TimeZoneInfo.ConvertTime(end, zone));
            else if (reason == "startup" && _noted.Add("window"))
                logger.LogInformation("currency_daily_window_passed localDate={Date}: started after the catch-up window; no late card today", date);
            return DailyPassOutcome.WindowPassed;
        }

        var channel = new ChannelId(o.ChannelId);
        var dryRun = delivery.Value.Mode != DeliveryMode.Send;
        var sourceKey = CurrencyDailySchedule.SourceKey(today);
        var targets = new List<GuildId>();
        var alreadyQueued = 0;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var gate = sp.GetRequiredService<IModuleGate>();
            var db = sp.GetRequiredService<ToroDbContext>();
            foreach (var guild in await sp.GetRequiredService<IModuleStateStore>().GetGuildsWithModuleEnabledAsync(CurrencyModule.ModuleIdTyped, ct))
            {
                if (!deployment.IsGuildAllowed(guild) || !await gate.IsEnabledAsync(guild, CurrencyModule.ModuleIdTyped, ct))
                    continue;

                // Any row for today's key — pending, sent, failed, expired — means today's card was decided: never a second one.
                var key = NotificationRequest.BuildLogicalKey(guild, CurrencyModule.ModuleIdTyped, sourceKey, channel, CurrencyDailySchedule.Kind, dryRun);
                if (await db.Outbox.AsNoTracking().AnyAsync(x => x.LogicalKey == key, ct))
                {
                    alreadyQueued++;
                    continue;
                }

                // The configured channel must belong to this guild as the gateway sees it (not connected yet → next pass).
                // Missing permissions are not checked here: the outbox reports a refused delivery.
                if (!(await guilds.GetBotChannelAccessAsync(guild, channel, ct)).Exists)
                {
                    if (_noted.Add("channel|" + guild))
                        logger.LogInformation("currency_daily_channel_unavailable guild={Guild} channel={Channel} localDate={Date} reason={Reason}: not visible in the Discord gateway; retrying on the next pass",
                            guild, o.ChannelId, date, reason);
                    continue;
                }

                targets.Add(guild);
            }
        }

        if (targets.Count == 0)
            return alreadyQueued > 0 ? DailyPassOutcome.AlreadyQueued : DailyPassOutcome.NoTarget;

        // Once per pass for all guilds; USD and EUR share one Altınkaynak Currency response.
        var results = new Dictionary<MarketInstrument, MarketQuoteResult>();
        foreach (var instrument in CurrencyCardRenderer.DailyInstruments)
            results[instrument] = await quotes.GetQuoteAsync(instrument, ct);
        var availability = string.Join(",", results.Select(r => $"{r.Key}={Availability(r.Value)}"));
        if (results.Values.All(r => r.Quote is null))
        {
            _noDataToday = true;
            logger.LogWarning("currency_daily_no_data localDate={Date} channel={Channel} reason={Reason} quotes={Quotes}: nothing to post; retrying until {End:HH:mm} (Türkiye)",
                date, o.ChannelId, reason, availability, TimeZoneInfo.ConvertTime(end, zone));
            return DailyPassOutcome.NoData;
        }

        var queued = 0;
        foreach (var guild in targets)
        {
            if (await StageAsync(guild, channel, today, CurrencyDailySchedule.DeliveryDeadline(today, zone, o.DailyCatchUp), results, dryRun, ct) is { } outcome)
            {
                queued++;
                logger.LogInformation("currency_daily_queued guild={Guild} channel={Channel} localDate={Date} reason={Reason} quotes={Quotes} stage={Stage} dryRun={DryRun}",
                    guild, o.ChannelId, date, reason, availability, outcome, dryRun);
            }
        }

        return queued > 0 ? DailyPassOutcome.Queued : DailyPassOutcome.AlreadyQueued;
    }

    private async Task<StageOutcome?> StageAsync(GuildId guild, ChannelId channel, DateOnly today, DateTimeOffset expiresAt,
        IReadOnlyDictionary<MarketInstrument, MarketQuoteResult> results, bool dryRun, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ToroDbContext>();
        var language = (await sp.GetRequiredService<IGuildSettingsStore>().GetAsync(guild, ct)).Language;
        var message = new OutgoingMessage(null, cards.RenderDaily(language, results), MentionPolicy.None);
        var outcome = await sp.GetRequiredService<INotificationOutbox>().StageAsync(new NotificationRequest(guild, CurrencyModule.ModuleIdTyped,
            CurrencyDailySchedule.SourceKey(today), channel, CurrencyDailySchedule.Kind, message, expiresAt, dryRun), ct);
        if (outcome != StageOutcome.Created)
        {
            // Queued meanwhile by another pass: leave that card exactly as it is (no update, no edit).
            db.ChangeTracker.Clear();
            logger.LogInformation("currency_daily_skipped guild={Guild} localDate={Date}: already queued by another pass", guild, today);
            return null;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return outcome;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("currency_daily_skipped guild={Guild} localDate={Date}: already queued by another pass", guild, today);
            return null;
        }
    }

    private static string Availability(MarketQuoteResult result) => result.Quote switch
    {
        null => "unavailable",
        { IsStale: true } q => q.Source + "(stale)",
        { IsFallback: true } q => q.Source + "(fallback)",
        var q => q.Source.ToString(),
    };
}

/// <summary>
/// Runs <see cref="CurrencyDailyPoster"/>: once shortly after startup (catch-up inside the window), then whenever
/// <see cref="CurrencyDailyPoster.NextDelay"/> says. Registered only in the long-running host. A failed pass is logged and
/// retried on the next one; it never stops the bot.
/// </summary>
public sealed class CurrencyDailyWorker(CurrencyDailyPoster poster, TimeProvider clock, ILogger<CurrencyDailyWorker> logger) : BackgroundService
{
    /// <summary>Lets the Discord gateway log in and receive the guild before the first pass needs the channel.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var reason = "startup";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await poster.RunAsync(reason, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "currency_daily_failed reason={Reason}; retrying on the next pass", reason);
            }

            reason = "scheduled";
            try
            {
                await Task.Delay(poster.NextDelay(), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
