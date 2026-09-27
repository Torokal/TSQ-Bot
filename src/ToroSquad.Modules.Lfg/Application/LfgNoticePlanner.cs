using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// The two optional event notices of a scheduled listing — "starts in 30 minutes" and "starts now" — which really ping the
/// listing's Joined players (the only LFG messages that ping). Unlike the card (an interaction response) these are new,
/// scheduled messages, so they go through the existing outbox: in ONE write transaction the planner re-reads the listing,
/// takes the current Joined players as the recipients, stages the outbox row and marks the notice
/// <see cref="LfgNoticeState.Queued"/>. The outbox then delivers it (module gate, allow-list, InFlight claim, ambiguous
/// reconciliation — and a resend after an uncertain delivery carries no pings). There is no "send, then mark" window, and
/// the marker plus the unique outbox key make a notice happen at most once across restarts.
/// Late notices are never sent: the reminder only before the start, the start notice only within
/// <see cref="LfgRules.StartGrace"/>; otherwise (or while the module is disabled / the guild not allowed) the notice is
/// consumed as <see cref="LfgNoticeState.Skipped"/>, so neither a restart nor re-enabling the module sends it later. Closed,
/// expired and orphaned listings produce nothing; closing cancels a notice still waiting in the outbox.
/// </summary>
public sealed class LfgNoticePlanner(
    ToroDbContext db,
    INotificationOutbox outbox,
    IModuleGate gate,
    DeploymentPolicy deployment,
    IGuildSettingsStore settings,
    LfgNoticeRenderer renderer,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<LfgNoticePlanner> logger)
{
    public const string KindReminder = "lfg-reminder";
    public const string KindStart = "lfg-start";

    /// <summary>Delivered/finished notice rows (their payload lists the pinged user ids) are removed after this.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private const int Batch = 50;

    private DbSet<LfgListingEntity> Listings => db.Set<LfgListingEntity>();

    public static string SourceKey(long listingId) => "listing:" + listingId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Worker pass: handles every notice that became due (one transaction per listing). Returns how many were queued.</summary>
    public async Task<int> PlanDueAsync(CancellationToken ct)
    {
        var horizon = clock.GetUtcNow() + LfgRules.ReminderLead;
        var ids = await Listings.AsNoTracking()
            .Where(x => (x.Status == LfgStatus.Open || x.Status == LfgStatus.Full) && x.EventAt != null && x.EventAt <= horizon &&
                        ((x.NotifyBeforeStart && x.ReminderState == LfgNoticeState.Pending) || (x.NotifyAtStart && x.StartNoticeState == LfgNoticeState.Pending)))
            .OrderBy(x => x.EventAt).Select(x => x.Id).Take(Batch).ToListAsync(ct);
        var queued = 0;
        foreach (var id in ids)
            queued += await PlanAsync(id, ct);
        return queued;
    }

    private async Task<int> PlanAsync(long listingId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        // SQLite BEGIN IMMEDIATE: no join/leave/maybe/close can interleave between reading the players and the marker.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var listing = await Listings.FirstOrDefaultAsync(x => x.Id == listingId, ct);
        if (listing is not { Status: LfgStatus.Open or LfgStatus.Full, EventAt: { } eventAt } || listing.ExpiresAt <= now)
            return 0;

        var guild = new GuildId(listing.GuildId);
        var deliverable = deployment.IsGuildAllowed(guild) && await gate.IsEnabledAsync(guild, LfgModule.ModuleIdTyped, ct);
        var queued = 0;

        if (listing.NotifyBeforeStart && listing.ReminderState == LfgNoticeState.Pending && now >= eventAt - LfgRules.ReminderLead)
        {
            listing.ReminderState = deliverable && now < eventAt && await StageAsync(listing, KindReminder, eventAt, ct)
                ? LfgNoticeState.Queued
                : Skip(listing, KindReminder, deliverable ? "event already started" : "module disabled or guild not allowed");
            listing.ReminderHandledAt = now;
            queued += listing.ReminderState == LfgNoticeState.Queued ? 1 : 0;
        }

        if (listing.NotifyAtStart && listing.StartNoticeState == LfgNoticeState.Pending && now >= eventAt)
        {
            var until = eventAt + LfgRules.StartGrace;
            listing.StartNoticeState = deliverable && now < until && await StageAsync(listing, KindStart, until, ct)
                ? LfgNoticeState.Queued
                : Skip(listing, KindStart, deliverable ? "start grace passed" : "module disabled or guild not allowed");
            listing.StartNoticeHandledAt = now;
            queued += listing.StartNoticeState == LfgNoticeState.Queued ? 1 : 0;
        }

        await db.SaveChangesAsync(ct); // outbox row + marker together
        await transaction.CommitAsync(ct);
        return queued;
    }

    private async Task<bool> StageAsync(LfgListingEntity listing, string kind, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // The recipients are the Joined players at this moment (DB is the source of truth): Maybe and former players are not.
        var players = await db.Set<LfgParticipantEntity>().AsNoTracking().Where(p => p.ListingId == listing.Id).ToListAsync(ct);
        var view = LfgService.ToView(listing, players);
        var language = (await settings.GetAsync(view.Guild, ct)).Language;
        var message = renderer.Render(view, kind, language);
        await outbox.StageAsync(new NotificationRequest(view.Guild, LfgModule.ModuleIdTyped, SourceKey(listing.Id), view.Channel, kind, message, expiresAt,
            delivery.Value.Mode != DeliveryMode.Send), ct);
        logger.LogInformation("LFG listing {Listing}: {Kind} queued for {Players} joined player(s)", listing.Id, kind, view.Players.Count);
        return true;
    }

    private LfgNoticeState Skip(LfgListingEntity listing, string kind, string reason)
    {
        logger.LogInformation("LFG listing {Listing}: {Kind} skipped ({Reason})", listing.Id, kind, reason);
        return LfgNoticeState.Skipped;
    }

    /// <summary>
    /// Cancels this listing's notices still waiting in the outbox (closed/orphaned listing). Bumps the outbox version so a
    /// dispatcher that already read the row cannot claim it afterwards.
    /// </summary>
    public static Task<int> CancelPendingAsync(ToroDbContext db, long listingId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var key = SourceKey(listingId);
        return db.Outbox.Where(o => o.ModuleId == LfgModule.ModuleIdValue && o.SourceKey == key && o.Status == OutboxStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OutboxStatus.Cancelled)
                .SetProperty(o => o.LastError, reason)
                .SetProperty(o => o.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(o => o.UpdatedAt, now)
                .SetProperty(o => o.Version, o => o.Version + 1), ct);
    }

    /// <summary>Removes finished LFG notice rows after <see cref="Retention"/> (their payload holds the pinged user ids).</summary>
    public Task<int> PruneAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - Retention;
        return db.Outbox.Where(o => o.ModuleId == LfgModule.ModuleIdValue && o.UpdatedAt < cutoff &&
                                    (o.Status == OutboxStatus.Cancelled || o.Status == OutboxStatus.Expired || o.Status == OutboxStatus.Failed ||
                                     o.Status == OutboxStatus.Simulated || (o.Status == OutboxStatus.Sent && !o.EditPending)))
            .ExecuteDeleteAsync(ct);
    }
}
