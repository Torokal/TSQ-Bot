using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Domain;
using ToroSquad.Modules.Birthday.Persistence;

namespace ToroSquad.Modules.Birthday.Application;

/// <summary>
/// Brings every guild to the state today's date asks for — there is no "fire at 00:00" job. Each pass (at startup, about
/// once a minute, and just after local midnight) works out today's date in <see cref="BirthdayOptions.TimeZone"/> and:
/// <list type="number">
/// <item>records today's celebration of every registered member who is still in the guild (unique guild + user + year);</item>
/// <item>queues the day's single announcement if it was not queued yet (unique guild + local date, staged into the outbox in
/// the same transaction — restarts, redeploys and overlapping passes cannot send it twice);</item>
/// <item>gives the role to today's celebrants and takes back every role the bot gave on an earlier day (or whose registration
/// was removed) — only roles the bot itself gave, never one a member already had;</item>
/// <item>follows the announcement's delivery in the outbox.</item>
/// </list>
/// So a deploy at 08:00, a crash during the day or a night of downtime changes nothing: the next pass catches up with today
/// and cleans up yesterday. One member's problem (left the guild, role hierarchy, Discord error) never stops the others; a
/// role problem never stops the announcement. Nothing announces or grants while the module is disabled in a guild, but roles
/// the bot gave are still taken back.
/// </summary>
public sealed class BirthdayReconciler(
    IServiceScopeFactory scopes,
    IGuildGateway guilds,
    DeploymentPolicy deployment,
    BirthdayHealth health,
    IOptions<BirthdayOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<BirthdayReconciler> logger) : IDisposable
{
    public const string AnnouncementKind = "birthday-day";

    /// <summary>Discord calls for one role step before it is given up (a policy problem such as the hierarchy costs no call).</summary>
    public const int MaxRoleAttempts = 5;

    /// <summary>Passes the announcement waits for members whose lookup failed (Discord hiccup) before it goes out without them.</summary>
    public const int MaxAnnouncementDeferrals = 10;

    /// <summary>Finished outbox rows of this module (their text lists member ids) are removed after this.</summary>
    public static readonly TimeSpan OutboxRetention = TimeSpan.FromDays(2);

    private const int SqliteConstraint = 19;

    private readonly SemaphoreSlim _pass = new(1, 1);

    // Per local day, in memory only (a restart simply asks again): members not in the guild, announcement deferrals, and
    // notes already logged — so a missing member or a missing channel is logged once a day, not once a minute.
    private DateOnly _day;
    private readonly HashSet<(ulong Guild, ulong User)> _missing = [];
    private readonly Dictionary<ulong, int> _deferrals = [];
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);

    public void Dispose() => _pass.Dispose();

    public static string SourceKey(DateOnly day) => "day:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<BirthdayPassSummary> RunAsync(string reason, CancellationToken ct)
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

    /// <summary>When the next pass is due: the regular interval, or just after the next local midnight if that comes first.</summary>
    public TimeSpan NextDelay()
    {
        var interval = options.Value.ReconciliationInterval;
        if (!GuildTime.TryResolve(options.Value.TimeZone, out var zone))
            return interval;
        var now = clock.GetUtcNow();
        var untilMidnight = BirthdayCalendar.EndOfDay(BirthdayCalendar.LocalDate(now, zone), zone) - now + TimeSpan.FromSeconds(2);
        return untilMidnight < interval && untilMidnight > TimeSpan.Zero ? untilMidnight : interval;
    }

    private async Task<BirthdayPassSummary> RunPassAsync(string reason, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        if (!GuildTime.TryResolve(o.TimeZone, out var zone))
        {
            health.Failed(now, "time_zone");
            logger.LogError("birthday_reconciliation_failed reason={Reason}: time zone {Zone} is unknown", reason, o.TimeZone);
            return new BirthdayPassSummary(0, 0, 0, 0, 0, 0, 0, 1);
        }

        var today = BirthdayCalendar.LocalDate(now, zone);
        if (today != _day)
        {
            _day = today;
            _missing.Clear();
            _deferrals.Clear();
            _noted.Clear();
        }

        var routine = reason == "periodic";
        logger.Log(routine ? LogLevel.Debug : LogLevel.Information, "birthday_reconciliation_started reason={Reason} localDate={Date} zone={Zone}",
            reason, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), o.TimeZone);
        health.Started(now);

        var pass = new Pass(today, zone, now);
        List<ulong> guildIds;
        try
        {
            guildIds = await GuildsWithWorkAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            health.Failed(clock.GetUtcNow(), ex.GetType().Name);
            logger.LogError(ex, "birthday_reconciliation_failed reason={Reason}: database not readable", reason);
            return new BirthdayPassSummary(0, 0, 0, 0, 0, 0, 0, 1);
        }

        foreach (var guild in guildIds.Where(g => deployment.IsGuildAllowed(g)))
        {
            try
            {
                await ReconcileGuildAsync(new GuildId(guild), pass, ct);
                pass.Guilds++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One guild must not hold back the others; everything is retried on the next pass.
                pass.Errors++;
                logger.LogError(ex, "birthday_reconciliation_failed guild={Guild}", guild);
            }
        }

        try
        {
            await PruneAsync(pass, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pass.Errors++;
            logger.LogError(ex, "birthday retention prune failed");
        }

        var summary = pass.Summary();
        if (summary.Errors > 0)
            health.Failed(clock.GetUtcNow(), "guild_errors");
        else
            health.Completed(clock.GetUtcNow(), summary);
        logger.Log(routine && !summary.Changed ? LogLevel.Debug : LogLevel.Information,
            "birthday_reconciliation_completed reason={Reason} guilds={Guilds} detected={Detected} missingMembers={Missing} announcements={Announcements} " +
            "rolesAssigned={Assigned} rolesRemoved={Removed} roleFailures={RoleFailures} errors={Errors}",
            reason, summary.Guilds, summary.Detected, summary.MissingMembers, summary.AnnouncementsQueued, summary.RolesAssigned, summary.RolesRemoved,
            summary.RoleFailures, summary.Errors);
        return summary;
    }

    /// <summary>Guilds with registrations, open role work or an announcement whose delivery is still being followed.</summary>
    private async Task<List<ulong>> GuildsWithWorkAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var registered = await db.Set<BirthdayRegistrationEntity>().AsNoTracking().Select(r => r.GuildId).Distinct().ToListAsync(ct);
        var roles = await db.Set<BirthdayCelebrationEntity>().AsNoTracking()
            .Where(c => c.RoleState == BirthdayRoleState.Pending || c.RoleState == BirthdayRoleState.Active).Select(c => c.GuildId).Distinct().ToListAsync(ct);
        var queued = await db.Set<BirthdayAnnouncementEntity>().AsNoTracking()
            .Where(a => a.State == BirthdayAnnouncementState.Queued).Select(a => a.GuildId).Distinct().ToListAsync(ct);
        return registered.Concat(roles).Concat(queued).Distinct().Order().ToList();
    }

    private async Task ReconcileGuildAsync(GuildId guild, Pass pass, CancellationToken ct)
    {
        List<long> roleWork;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<ToroDbContext>();
            await TrackAnnouncementsAsync(db, guild, ct);

            // Without the guild in the gateway (not connected yet, outage) nothing is decided: members and roles are unknown.
            if (await guilds.GetRoleSnapshotAsync(guild, ct) is null)
            {
                Note(LogLevel.Information, $"unavailable|{guild}", "birthday: guild {Guild} is not available in the Discord gateway yet; retrying on the next pass", guild);
                return;
            }

            var enabled = await sp.GetRequiredService<IModuleGate>().IsEnabledAsync(guild, BirthdayModule.ModuleIdTyped, ct);
            var registrations = await db.Set<BirthdayRegistrationEntity>().AsNoTracking().Where(r => r.GuildId == guild.Value)
                .ToDictionaryAsync(r => r.UserId, ct);
            if (enabled)
            {
                var unresolved = await DetectAsync(db, guild, registrations.Values, pass, ct);
                await AnnounceAsync(sp, db, guild, registrations, unresolved, pass, ct);
            }

            roleWork = await db.Set<BirthdayCelebrationEntity>().AsNoTracking()
                .Where(c => c.GuildId == guild.Value && (c.RoleState == BirthdayRoleState.Pending || c.RoleState == BirthdayRoleState.Active))
                .OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);
        }

        if (roleWork.Count > 0 && options.Value.Role is { } role)
            await ReconcileRolesAsync(guild, role, roleWork, pass, ct);
    }

    /// <summary>
    /// Records a celebration for every member whose birthday is today and who has none this year. Returns how many members
    /// could not be looked up right now (they are retried; the announcement waits for them for a while).
    /// </summary>
    private async Task<int> DetectAsync(ToroDbContext db, GuildId guild, IEnumerable<BirthdayRegistrationEntity> registrations, Pass pass, CancellationToken ct)
    {
        var todays = registrations.Where(r => BirthdayDate.Create(r.Day, r.Month) is { } d && d.IsOn(pass.Today)).Select(r => r.UserId).ToList();
        if (todays.Count == 0)
            return 0;
        var year = pass.Today.Year;
        var celebrated = await db.Set<BirthdayCelebrationEntity>().AsNoTracking()
            .Where(c => c.GuildId == guild.Value && c.Year == year).Select(c => c.UserId).ToListAsync(ct);
        var role = options.Value.Role;
        var unresolved = 0;
        foreach (var userId in todays.Except(celebrated).Where(u => !_missing.Contains((guild.Value, u))))
        {
            var user = new UserId(userId);
            var member = await guilds.GetMemberAsync(guild, user, ct);
            if (member.Outcome == MemberLookupOutcome.Unavailable)
            {
                unresolved++;
                continue;
            }

            if (member.Outcome == MemberLookupOutcome.NotMember)
            {
                // Not announced, no role; the registration is kept (they may come back). Once a day in the log, no error.
                _missing.Add((guild.Value, userId));
                pass.MissingMembers++;
                logger.LogInformation("birthday_member_missing guild={Guild} user={User}: not in the guild, skipped today (registration kept)", guild, user);
                continue;
            }

            var state = role is not { } r ? BirthdayRoleState.Skipped
                : member.HasRole(r) ? BirthdayRoleState.NotManaged // already has it (given by someone): never touched
                : BirthdayRoleState.Pending;
            var celebration = new BirthdayCelebrationEntity
            {
                GuildId = guild.Value,
                UserId = userId,
                Year = year,
                LocalDate = pass.Today,
                RoleState = state,
                CreatedAt = pass.Now,
                UpdatedAt = pass.Now,
            };
            db.Add(celebration);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
            {
                db.Entry(celebration).State = EntityState.Detached; // recorded already (unique guild + user + year)
                continue;
            }

            pass.Detected++;
            logger.LogInformation("birthday_detected guild={Guild} user={User} localDate={Date} role={RoleState}",
                guild, user, pass.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), state);
        }

        return unresolved;
    }

    /// <summary>Queues the guild's one announcement of the day (at most once, restart-safe) naming today's celebrants.</summary>
    private async Task AnnounceAsync(IServiceProvider sp, ToroDbContext db, GuildId guild, Dictionary<ulong, BirthdayRegistrationEntity> registrations,
        int unresolved, Pass pass, CancellationToken ct)
    {
        var celebrants = (await db.Set<BirthdayCelebrationEntity>().AsNoTracking()
                .Where(c => c.GuildId == guild.Value && c.LocalDate == pass.Today && !c.Announced).ToListAsync(ct))
            .Where(c => StillCelebrating(c, registrations, pass.Today))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.UserId)
            .ToList();
        if (celebrants.Count == 0)
            return;

        var today = pass.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (await db.Set<BirthdayAnnouncementEntity>().AnyAsync(a => a.GuildId == guild.Value && a.LocalDate == pass.Today, ct))
        {
            // One message a day: a member registered after it went out gets the role, but no second message.
            Note(LogLevel.Information, $"late|{guild}|{celebrants.Count}", "birthday_announcement_skipped guild={Guild} localDate={Date}: today's announcement already went out ({Count} later celebrant(s) not announced)",
                guild, today, celebrants.Count);
            return;
        }

        var config = await db.Set<BirthdayGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        if (config?.ChannelId is not { } channel)
        {
            Note(LogLevel.Warning, $"nochannel|{guild}", "birthday_announcement_skipped guild={Guild} localDate={Date}: no announcement channel configured (/birthday-admin configure); roles are unaffected",
                guild, today);
            return;
        }

        if (config.ChannelProblem is not null)
        {
            Note(LogLevel.Warning, $"channelproblem|{guild}", "birthday_announcement_failed guild={Guild} localDate={Date}: channel {Channel} reported {Problem}; configure it again",
                guild, today, channel, config.ChannelProblem);
            return;
        }

        if (unresolved > 0 && _deferrals.GetValueOrDefault(guild.Value) < MaxAnnouncementDeferrals)
        {
            _deferrals[guild.Value] = _deferrals.GetValueOrDefault(guild.Value) + 1;
            logger.LogDebug("birthday announcement guild={Guild} waits for {Count} member lookup(s)", guild, unresolved);
            return;
        }

        var language = (await sp.GetRequiredService<IGuildSettingsStore>().GetAsync(guild, ct)).Language;
        var message = sp.GetRequiredService<BirthdayAnnouncementRenderer>().Render(celebrants.Select(c => new UserId(c.UserId)).ToList(), language);
        var dryRun = delivery.Value.Mode != DeliveryMode.Send;

        db.ChangeTracker.Clear();
        // One transaction: the announcement row, the outbox row and the "announced" marks commit together or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Add(new BirthdayAnnouncementEntity
        {
            GuildId = guild.Value,
            LocalDate = pass.Today,
            ChannelId = channel,
            Celebrants = celebrants.Count,
            State = BirthdayAnnouncementState.Queued,
            CreatedAt = pass.Now,
            UpdatedAt = pass.Now,
        });
        var ids = celebrants.Select(c => c.Id).ToList();
        foreach (var row in await db.Set<BirthdayCelebrationEntity>().Where(c => ids.Contains(c.Id)).ToListAsync(ct))
        {
            row.Announced = true;
            row.UpdatedAt = pass.Now;
        }

        // Due by the end of the day: an announcement that cannot be delivered today is never sent tomorrow.
        await sp.GetRequiredService<INotificationOutbox>().StageAsync(new NotificationRequest(guild, BirthdayModule.ModuleIdTyped, SourceKey(pass.Today),
            new ChannelId(channel), AnnouncementKind, message, BirthdayCalendar.EndOfDay(pass.Today, pass.Zone), dryRun), ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("birthday_announcement_skipped guild={Guild} localDate={Date}: already queued by another pass", guild, today);
            return;
        }

        db.ChangeTracker.Clear();
        pass.AnnouncementsQueued++;
        logger.LogInformation("birthday_announcement_queued guild={Guild} channel={Channel} localDate={Date} celebrants={Count} dryRun={DryRun}",
            guild, channel, today, celebrants.Count, dryRun);
    }

    /// <summary>Follows queued announcements through the outbox: sent, simulated (dry-run) or failed — logged once each.</summary>
    private async Task TrackAnnouncementsAsync(ToroDbContext db, GuildId guild, CancellationToken ct)
    {
        var queued = await db.Set<BirthdayAnnouncementEntity>().Where(a => a.GuildId == guild.Value && a.State == BirthdayAnnouncementState.Queued).ToListAsync(ct);
        foreach (var announcement in queued)
        {
            var key = SourceKey(announcement.LocalDate);
            var row = await db.Outbox.AsNoTracking()
                .Where(o => o.ModuleId == BirthdayModule.ModuleIdValue && o.GuildId == guild.Value && o.SourceKey == key && o.Kind == AnnouncementKind)
                .OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct);
            var date = announcement.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            switch (row?.Status)
            {
                case OutboxStatus.Pending or OutboxStatus.InFlight or OutboxStatus.DeliveryUnknown:
                    continue;
                case OutboxStatus.Sent:
                    announcement.State = BirthdayAnnouncementState.Sent;
                    logger.LogInformation("birthday_announcement_sent guild={Guild} channel={Channel} localDate={Date} message={Message}",
                        guild, announcement.ChannelId, date, row.DiscordMessageId);
                    break;
                case OutboxStatus.Simulated:
                    announcement.State = BirthdayAnnouncementState.Simulated;
                    logger.LogInformation("birthday_announcement_sent guild={Guild} localDate={Date} dryRun=true (logged, not sent)", guild, date);
                    break;
                default:
                    announcement.State = BirthdayAnnouncementState.Failed;
                    announcement.Detail = Clip(row is null ? "outbox_row_missing" : $"{row.Status}: {row.LastError}");
                    logger.LogWarning("birthday_announcement_failed guild={Guild} channel={Channel} localDate={Date}: {Detail}",
                        guild, announcement.ChannelId, date, announcement.Detail);
                    break;
            }

            announcement.UpdatedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileRolesAsync(GuildId guild, RoleId role, List<long> celebrationIds, Pass pass, CancellationToken ct)
    {
        var snapshot = await guilds.GetRoleSnapshotAsync(guild, ct);
        if (snapshot is null)
            return;
        var verdict = SelfServiceRolePolicy.Evaluate(snapshot, role);
        foreach (var id in celebrationIds)
        {
            // Each member in its own unit of work: a failure here never holds back the next member.
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<ToroDbContext>();
                var celebration = await db.Set<BirthdayCelebrationEntity>().FirstOrDefaultAsync(c => c.Id == id, ct);
                if (celebration is null)
                    continue;
                var registration = await db.Set<BirthdayRegistrationEntity>().AsNoTracking()
                    .FirstOrDefaultAsync(r => r.GuildId == guild.Value && r.UserId == celebration.UserId, ct);
                var current = celebration.LocalDate == pass.Today && StillCelebrating(celebration, registration, pass.Today);

                if (celebration.RoleState == BirthdayRoleState.Pending && current)
                {
                    if (await sp.GetRequiredService<IModuleGate>().IsEnabledAsync(guild, BirthdayModule.ModuleIdTyped, ct))
                        await GrantAsync(db, guild, role, verdict, celebration, pass, ct);
                }
                else if (celebration.RoleState == BirthdayRoleState.Pending)
                {
                    await ExpirePendingAsync(db, guild, role, verdict, celebration, pass, ct);
                }
                else if (celebration.RoleState == BirthdayRoleState.Active && !current)
                {
                    await RemoveAsync(db, guild, role, verdict, celebration, pass, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pass.RoleFailures++;
                logger.LogError(ex, "birthday_role_failed guild={Guild} celebration={Celebration}: unexpected error (retried next pass)", guild, id);
            }
        }
    }

    private async Task GrantAsync(ToroDbContext db, GuildId guild, RoleId role, RoleSafetyVerdict verdict, BirthdayCelebrationEntity c, Pass pass, CancellationToken ct)
    {
        var user = new UserId(c.UserId);
        if (GrantBlockers(verdict) is { Count: > 0 } blockers)
        {
            // No Discord call can succeed (hierarchy, missing Manage Roles, unsafe role...): wait for an admin fix, log once.
            if (SetError(c, "policy:" + string.Join(",", blockers), "add", guild, pass))
                await db.SaveChangesAsync(ct);
            return;
        }

        if (c.RoleAttempts > 0 && await ResolveEarlierAttemptAsync(db, guild, role, c, pass, ct))
            return;

        c.RoleAttempts++;
        c.UpdatedAt = pass.Now;
        await db.SaveChangesAsync(ct); // persisted BEFORE the Discord call → a crash leaves a reconcilable record

        var outcome = await guilds.AddRoleAsync(guild, user, role, "TSQ Birthday: birthday role for today", ct);
        switch (outcome)
        {
            case RoleOperationOutcome.Success:
                MarkGranted(c, pass);
                pass.RolesAssigned++;
                logger.LogInformation("birthday_role_assigned guild={Guild} user={User} role={Role}", guild, user, role);
                break;
            case RoleOperationOutcome.UnknownMember:
                c.RoleState = BirthdayRoleState.Skipped;
                c.RoleError = nameof(RoleOperationOutcome.UnknownMember);
                logger.LogInformation("birthday_member_missing guild={Guild} user={User}: left the guild before the role was given", guild, user);
                break;
            default:
                SetError(c, outcome.ToString(), "add", guild, pass);
                if (c.RoleAttempts >= MaxRoleAttempts)
                {
                    c.RoleState = BirthdayRoleState.Failed;
                    logger.LogWarning("birthday_role_failed guild={Guild} user={User} op=add: gave up after {Attempts} attempts ({Error})", guild, user, c.RoleAttempts, c.RoleError);
                }

                break;
        }

        c.UpdatedAt = pass.Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// An earlier add may have reached Discord although it reported an error (timeout). The member's current roles decide:
    /// has the role → it was given by us; left → skipped. Returns true when that settled the celebration.
    /// </summary>
    private async Task<bool> ResolveEarlierAttemptAsync(ToroDbContext db, GuildId guild, RoleId role, BirthdayCelebrationEntity c, Pass pass, CancellationToken ct)
    {
        var member = await guilds.GetMemberAsync(guild, new UserId(c.UserId), ct);
        switch (member.Outcome)
        {
            case MemberLookupOutcome.Unavailable:
                return true; // unknown now: ask again next pass rather than risk a blind retry
            case MemberLookupOutcome.NotMember:
                c.RoleState = BirthdayRoleState.Skipped;
                c.UpdatedAt = pass.Now;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("birthday_member_missing guild={Guild} user={User}: left the guild before the role was given", guild, new UserId(c.UserId));
                return true;
            default:
                if (!member.HasRole(role))
                    return false;
                MarkGranted(c, pass);
                c.UpdatedAt = pass.Now;
                await db.SaveChangesAsync(ct);
                pass.RolesAssigned++;
                logger.LogInformation("birthday_role_assigned guild={Guild} user={User} role={Role} (earlier attempt had reached Discord)", guild, new UserId(c.UserId), role);
                return true;
        }
    }

    /// <summary>The day is over (or the registration is gone) before the role was given: nothing to give any more.</summary>
    private async Task ExpirePendingAsync(ToroDbContext db, GuildId guild, RoleId role, RoleSafetyVerdict verdict, BirthdayCelebrationEntity c, Pass pass, CancellationToken ct)
    {
        if (c.RoleAttempts > 0)
        {
            // An attempt that reported an error may still have given the role: if so, it is ours and is taken back now.
            var member = await guilds.GetMemberAsync(guild, new UserId(c.UserId), ct);
            if (member.Outcome == MemberLookupOutcome.Unavailable)
                return;
            if (member.HasRole(role))
            {
                MarkGranted(c, pass);
                await RemoveAsync(db, guild, role, verdict, c, pass, ct);
                return;
            }
        }

        c.RoleState = BirthdayRoleState.Skipped;
        c.UpdatedAt = pass.Now;
        await db.SaveChangesAsync(ct);
        if (c.RoleError is not null)
            logger.LogWarning("birthday_role_failed guild={Guild} user={User} op=add: the day ended before the role could be given ({Error})",
                guild, new UserId(c.UserId), c.RoleError);
    }

    private async Task RemoveAsync(ToroDbContext db, GuildId guild, RoleId role, RoleSafetyVerdict verdict, BirthdayCelebrationEntity c, Pass pass, CancellationToken ct)
    {
        var user = new UserId(c.UserId);
        if (verdict.Problems.Contains(RoleSafetyProblem.NotFound))
        {
            MarkRemoved(c, pass, "role_deleted");
            await db.SaveChangesAsync(ct);
            logger.LogInformation("birthday_role_removed guild={Guild} user={User} role={Role}: the role no longer exists", guild, user, role);
            return;
        }

        if (RemoveBlockers(verdict) is { Count: > 0 } blockers)
        {
            if (SetError(c, "policy:" + string.Join(",", blockers), "remove", guild, pass))
                await db.SaveChangesAsync(ct);
            return;
        }

        c.RoleAttempts++;
        c.UpdatedAt = pass.Now;
        await db.SaveChangesAsync(ct);

        var outcome = await guilds.RemoveRoleAsync(guild, user, role, "TSQ Birthday: the birthday is over", ct);
        if (outcome is RoleOperationOutcome.Success or RoleOperationOutcome.UnknownMember or RoleOperationOutcome.UnknownRole)
        {
            MarkRemoved(c, pass, null);
            pass.RolesRemoved++;
            logger.LogInformation("birthday_role_removed guild={Guild} user={User} role={Role} celebrated={Date}{Note}", guild, user, role,
                c.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), outcome == RoleOperationOutcome.Success ? "" : " (" + outcome + ")");
        }
        else
        {
            SetError(c, outcome.ToString(), "remove", guild, pass);
            if (c.RoleAttempts >= MaxRoleAttempts * 2)
            {
                c.RoleState = BirthdayRoleState.Failed;
                logger.LogWarning("birthday_role_failed guild={Guild} user={User} op=remove: gave up after {Attempts} attempts ({Error}); remove the role manually",
                    guild, user, c.RoleAttempts, c.RoleError);
            }
        }

        c.UpdatedAt = pass.Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Why the bot must not (or cannot) give the role. Beyond Discord's own rules the role may carry no permission @everyone
    /// does not have: anyone can register a birthday, so the role must never be a way to gain rights. A channel overwrite
    /// (e.g. a birthday channel) is allowed.
    /// </summary>
    public static IReadOnlyList<RoleSafetyProblem> GrantBlockers(RoleSafetyVerdict verdict) =>
        verdict.Problems.Where(p => p != RoleSafetyProblem.GrantsChannelPermissions).ToList();

    /// <summary>Why the bot cannot take the role back (Discord would refuse); the role's own permissions do not matter here.</summary>
    public static IReadOnlyList<RoleSafetyProblem> RemoveBlockers(RoleSafetyVerdict verdict) =>
        verdict.Problems.Where(p => p is RoleSafetyProblem.AboveBot or RoleSafetyProblem.BotLacksManageRoles or RoleSafetyProblem.Managed or RoleSafetyProblem.IsEveryone).ToList();

    private static bool StillCelebrating(BirthdayCelebrationEntity c, Dictionary<ulong, BirthdayRegistrationEntity> registrations, DateOnly today) =>
        StillCelebrating(c, registrations.GetValueOrDefault(c.UserId), today);

    /// <summary>The celebration is today's and the member's registration still says today (not removed or changed since).</summary>
    private static bool StillCelebrating(BirthdayCelebrationEntity c, BirthdayRegistrationEntity? registration, DateOnly today) =>
        c.LocalDate == today && registration is not null && BirthdayDate.Create(registration.Day, registration.Month) is { } d && d.IsOn(today);

    private static void MarkGranted(BirthdayCelebrationEntity c, Pass pass)
    {
        c.RoleState = BirthdayRoleState.Active;
        c.RoleGrantedAt = pass.Now;
        c.RoleAttempts = 0;
        c.RoleError = null;
    }

    private static void MarkRemoved(BirthdayCelebrationEntity c, Pass pass, string? note)
    {
        c.RoleState = BirthdayRoleState.Removed;
        c.RoleRemovedAt = pass.Now;
        c.RoleError = note;
        c.UpdatedAt = pass.Now;
    }

    /// <summary>Records a role problem; logs a structured warning only when it differs from the last one (no spam). True when it changed.</summary>
    private bool SetError(BirthdayCelebrationEntity c, string error, string op, GuildId guild, Pass pass)
    {
        error = Clip(error);
        if (c.RoleError == error)
            return false;
        c.RoleError = error;
        c.UpdatedAt = pass.Now;
        pass.RoleFailures++;
        logger.LogWarning("birthday_role_failed guild={Guild} user={User} op={Op} error={Error} attempts={Attempts}", guild, new UserId(c.UserId), op, error, c.RoleAttempts);
        return true;
    }

    private void Note(LogLevel level, string key, string message, params object?[] args)
    {
        if (_noted.Add(key))
#pragma warning disable CA2254 // the templates are constants of this class
            logger.Log(level, message, args);
#pragma warning restore CA2254
    }

    /// <summary>Old celebrations and announcements, and delivered outbox rows (their text lists member ids).</summary>
    private async Task PruneAsync(Pass pass, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var oldYear = pass.Today.Year - 1;
        await db.Set<BirthdayCelebrationEntity>()
            .Where(c => c.Year < oldYear && c.RoleState != BirthdayRoleState.Pending && c.RoleState != BirthdayRoleState.Active)
            .ExecuteDeleteAsync(ct);
        var oldDay = pass.Today.AddDays(-30);
        await db.Set<BirthdayAnnouncementEntity>().Where(a => a.LocalDate < oldDay && a.State != BirthdayAnnouncementState.Queued).ExecuteDeleteAsync(ct);
        var cutoff = pass.Now - OutboxRetention;
        await db.Outbox.Where(o => o.ModuleId == BirthdayModule.ModuleIdValue && o.UpdatedAt < cutoff &&
                                   (o.Status == OutboxStatus.Sent || o.Status == OutboxStatus.Simulated || o.Status == OutboxStatus.Failed ||
                                    o.Status == OutboxStatus.Cancelled || o.Status == OutboxStatus.Expired ||
                                    (o.Status == OutboxStatus.DeliveryUnknown && o.NextAttemptAt == null)))
            .ExecuteDeleteAsync(ct);
    }

    private static string Clip(string text) => text.Length <= 100 ? text : text[..100];

    private sealed class Pass(DateOnly today, TimeZoneInfo zone, DateTimeOffset now)
    {
        public DateOnly Today { get; } = today;
        public TimeZoneInfo Zone { get; } = zone;
        public DateTimeOffset Now { get; } = now;
        public int Guilds { get; set; }
        public int Detected { get; set; }
        public int MissingMembers { get; set; }
        public int AnnouncementsQueued { get; set; }
        public int RolesAssigned { get; set; }
        public int RolesRemoved { get; set; }
        public int RoleFailures { get; set; }
        public int Errors { get; set; }

        public BirthdayPassSummary Summary() => new(Guilds, Detected, MissingMembers, AnnouncementsQueued, RolesAssigned, RolesRemoved, RoleFailures, Errors);
    }
}
