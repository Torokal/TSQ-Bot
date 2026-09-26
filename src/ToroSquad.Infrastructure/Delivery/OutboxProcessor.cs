using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Infrastructure.Delivery;

public enum DeliveryMode
{
    /// <summary>Safe default: notifications are planned and stored, but only logged — never sent to Discord.</summary>
    DryRun = 0,
    Send = 1,
}

public sealed class DeliveryOptions
{
    public DeliveryMode Mode { get; set; } = DeliveryMode.DryRun;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; set; } = 25;
    public int MaxAttempts { get; set; } = 5;
    public int MaxEditAttempts { get; set; } = 3;
    public TimeSpan ReconcileDelay { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxReconcileAttempts { get; set; } = 3;
    public int ReconcileScanLimit { get; set; } = 50;
}

/// <summary>
/// Drains the outbox. Guarantees (see docs/NOTIFICATIONS.md):
/// <list type="bullet">
/// <item>Module gate + module delivery policy are checked immediately before every send/edit.</item>
/// <item>Row is marked InFlight and committed before calling Discord; a crash leaves it InFlight, which recovery
/// turns into DeliveryUnknown — never blindly resent.</item>
/// <item>Ambiguous outcomes (timeouts) become DeliveryUnknown and go through bounded reconciliation (content fingerprint of what was sent; no visible reference in the message).</item>
/// <item>Edits never ping; a missing edit target is not replaced by a new message.</item>
/// <item>@everyone at most once: a resend after an uncertain attempt (the row went through reconciliation) never carries
/// the @everyone opt-in.</item>
/// <item>One guild's failure never stops the batch.</item>
/// </list>
/// No exactly-once guarantee is claimed.
/// </summary>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopes,
    IMessageTransport transport,
    IOptions<DeliveryOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger)
{
    private readonly DeliveryOptions _options = options.Value;

    /// <summary>Crash recovery at startup: anything still InFlight may or may not have reached Discord.</summary>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
        var now = clock.GetUtcNow();
        var rows = await db.Outbox.Where(x => x.Status == OutboxStatus.InFlight).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.Status = OutboxStatus.DeliveryUnknown;
            row.NextAttemptAt = now + _options.ReconcileDelay;
            row.LastError = "recovered_after_restart";
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (rows.Count > 0)
            logger.LogWarning("Outbox recovery: {Count} in-flight notifications marked DeliveryUnknown", rows.Count);
        return rows.Count;
    }

    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        List<long> dueIds;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            var now = clock.GetUtcNow();
            dueIds = await db.Outbox.AsNoTracking()
                .Where(x => x.NextAttemptAt != null && x.NextAttemptAt <= now &&
                            (x.Status == OutboxStatus.Pending ||
                             x.Status == OutboxStatus.DeliveryUnknown ||
                             (x.Status == OutboxStatus.Sent && x.EditPending)))
                .OrderBy(x => x.NextAttemptAt)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .Take(_options.BatchSize)
                .ToListAsync(cancellationToken);
        }

        var processed = 0;
        foreach (var id in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessRowAsync(id, cancellationToken);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Isolation: a bug or DB hiccup for one row/guild must not stop other guilds' deliveries.
                logger.LogError(ex, "Outbox row {OutboxId} processing failed", id);
            }
        }

        return processed;
    }

    private async Task ProcessRowAsync(long id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ToroDbContext>();
        var row = await db.Outbox.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
            return;

        var now = clock.GetUtcNow();
        var guild = new GuildId(row.GuildId);
        var channel = new ChannelId(row.ChannelId);
        var module = new ModuleId(row.ModuleId);

        if (row.Status == OutboxStatus.DeliveryUnknown)
        {
            await ReconcileAsync(db, row, channel, cancellationToken);
            return;
        }

        var isEdit = row.Status == OutboxStatus.Sent && row.EditPending;

        if (!isEdit && now > row.ExpiresAt)
        {
            Finish(row, OutboxStatus.Expired, "expired_before_delivery", now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // Last-moment gates: guild allow-list, module enabled, module policy (paused, channel still configured, ...).
        if (sp.GetService<DeploymentPolicy>() is { } deployment && !deployment.IsGuildAllowed(guild))
        {
            CancelOrDropEdit(row, isEdit, "guild_not_allowed", now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var gate = sp.GetRequiredService<IModuleGate>();
        if (!await gate.IsEnabledAsync(guild, module, cancellationToken))
        {
            CancelOrDropEdit(row, isEdit, "module_disabled", now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var policy = sp.GetServices<IDeliveryPolicy>().FirstOrDefault(p => p.Module == module);
        if (policy is not null && await policy.CanDeliverAsync(guild, channel, row.Kind, cancellationToken) is DeliveryDecision.Cancel cancel)
        {
            CancelOrDropEdit(row, isEdit, cancel.Reason, now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (row.IsDryRun)
        {
            var preview = PayloadSerializer.Deserialize(row.PayloadJson);
            logger.LogInformation("[DRY-RUN] {Kind} guild={Guild} channel={Channel} ref={Marker} title={Title} pingRoles={Roles}",
                row.Kind, row.GuildId, row.ChannelId, row.Marker, preview.Embed?.Title, preview.Mentions.Roles.Count);
            if (isEdit)
            {
                row.EditPending = false;
                row.DeliveredPayloadHash = row.PayloadHash;
                row.NextAttemptAt = null;
            }
            else
            {
                Finish(row, OutboxStatus.Simulated, null, now);
            }

            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (_options.Mode != DeliveryMode.Send)
        {
            // Live row but delivery switched to dry-run: hold it (it will expire), never send.
            row.NextAttemptAt = now + TimeSpan.FromMinutes(5);
            row.LastError = "held_delivery_mode_dry_run";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // Sent exactly as rendered: the internal reference (row.Marker) stays in the database and the logs only.
        var message = PayloadSerializer.Deserialize(row.PayloadJson);

        if (isEdit)
        {
            await EditAsync(db, row, channel, message, policy, cancellationToken);
            return;
        }

        // @everyone is at most once. A row that went through reconciliation had an attempt that may have reached Discord
        // (timeout, lost response, crash while in flight): its resend never opts in to @everyone again, even when the
        // earlier message was not found — a missing ping is acceptable, a second one is not.
        if (message.Mentions.Everyone && row.ReconcileAttempts > 0)
        {
            message = message with { Mentions = message.Mentions with { Everyone = false } };
            logger.LogWarning("Outbox {OutboxId} ref={Marker}: resend after an uncertain delivery goes out without @everyone", row.Id, row.Marker);
        }

        // Claim: persist InFlight BEFORE talking to Discord (crash => DeliveryUnknown, not a blind resend).
        row.Status = OutboxStatus.InFlight;
        row.Attempts++;
        row.LastAttemptAt = now;
        row.DeliveredPayloadHash = row.PayloadHash;
        row.DeliveredFingerprint = MessageFingerprint.Of(message);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        SendOutcome outcome;
        try
        {
            outcome = await transport.SendAsync(channel, message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-request: we cannot know whether Discord accepted it.
            row.Status = OutboxStatus.DeliveryUnknown;
            row.NextAttemptAt = clock.GetUtcNow() + _options.ReconcileDelay;
            row.LastError = "cancelled_during_send";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            // Unknown failure after the claim: we cannot prove Discord did not get it, so treat as ambiguous.
            logger.LogError(ex, "Transport threw for outbox {OutboxId}", row.Id);
            outcome = new SendOutcome.Ambiguous("transport exception: " + ex.GetType().Name);
        }

        await ApplySendOutcomeAsync(db, row, channel, outcome, policy, cancellationToken);
    }

    private async Task ApplySendOutcomeAsync(ToroDbContext db, OutboxMessageEntity row, ChannelId channel, SendOutcome outcome, IDeliveryPolicy? policy, CancellationToken ct)
    {
        await SaveResilientAsync(db, row, r => ApplySendOutcome(r, outcome), ct);

        if (outcome is SendOutcome.Sent sent)
            logger.LogInformation("Delivered {Kind} ref={Marker} guild={Guild} channel={Channel} message={MessageId}", row.Kind, row.Marker, row.GuildId, row.ChannelId, sent.MessageId.Value);

        if (outcome is SendOutcome.Ambiguous ambiguous)
            logger.LogWarning("Outbox {OutboxId} delivery unknown ({Reason}); will reconcile, not resend", row.Id, ambiguous.Reason);
        if (outcome is SendOutcome.Permanent permanent && policy is not null && IsChannelProblem(permanent.Kind))
            await policy.ReportChannelProblemAsync(new GuildId(row.GuildId), channel, permanent.Kind, ct);
    }

    private void ApplySendOutcome(OutboxMessageEntity row, SendOutcome outcome)
    {
        var now = clock.GetUtcNow();
        switch (outcome)
        {
            case SendOutcome.Sent sent:
                row.Status = OutboxStatus.Sent;
                row.DiscordMessageId = sent.MessageId.Value;
                row.SentAt = now;
                row.LastError = null;
                // The planner may have staged a correction while we were sending: edit it in afterwards.
                row.EditPending = row.PayloadHash != row.DeliveredPayloadHash;
                row.NextAttemptAt = row.EditPending ? now : null;
                break;

            case SendOutcome.RateLimited limited:
                if (row.Attempts >= _options.MaxAttempts * 2)
                {
                    Finish(row, OutboxStatus.Failed, "rate_limited_too_often", now);
                }
                else
                {
                    row.Status = OutboxStatus.Pending;
                    row.NextAttemptAt = now + limited.RetryAfter + TimeSpan.FromMilliseconds(250);
                    row.LastError = "rate_limited";
                }

                break;

            case SendOutcome.Transient transient:
                if (row.Attempts >= _options.MaxAttempts)
                {
                    Finish(row, OutboxStatus.Failed, Truncate("transient_exhausted: " + transient.Reason), now);
                }
                else
                {
                    row.Status = OutboxStatus.Pending;
                    row.NextAttemptAt = now + Backoff(row.Attempts);
                    row.LastError = Truncate(transient.Reason);
                }

                break;

            case SendOutcome.Permanent permanent:
                // 403/404/invalid payload: retrying cannot help, so no retry storm.
                Finish(row, OutboxStatus.Failed, Truncate($"{permanent.Kind}: {permanent.Reason}"), now);
                break;

            case SendOutcome.Ambiguous ambiguous:
                row.Status = OutboxStatus.DeliveryUnknown;
                row.NextAttemptAt = now + _options.ReconcileDelay;
                row.LastError = Truncate("ambiguous: " + ambiguous.Reason);
                break;
        }

        row.UpdatedAt = now;
    }

    private async Task EditAsync(ToroDbContext db, OutboxMessageEntity row, ChannelId channel, OutgoingMessage message, IDeliveryPolicy? policy, CancellationToken ct)
    {
        if (row.DiscordMessageId is not { } messageId)
        {
            await SaveResilientAsync(db, row, r =>
            {
                r.EditPending = false;
                r.NextAttemptAt = null;
                r.LastError = "edit_without_message_id";
            }, ct);
            return;
        }

        var editedHash = row.PayloadHash;
        var attempts = row.EditAttempts + 1;
        SendOutcome outcome;
        try
        {
            outcome = await transport.EditAsync(channel, new MessageId(messageId), message.WithoutPings(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Transport threw while editing outbox {OutboxId}", row.Id);
            outcome = new SendOutcome.Transient("transport exception: " + ex.GetType().Name);
        }
        var now = clock.GetUtcNow();

        await SaveResilientAsync(db, row, r =>
        {
            r.EditAttempts = attempts;
            r.UpdatedAt = now;
            switch (outcome)
            {
                case SendOutcome.Sent:
                    r.DeliveredPayloadHash = editedHash;
                    r.EditPending = r.PayloadHash != editedHash; // another correction arrived meanwhile
                    r.EditAttempts = r.EditPending ? 0 : attempts;
                    r.NextAttemptAt = r.EditPending ? now : null;
                    r.LastError = null;
                    break;
                case SendOutcome.Permanent { Kind: PermanentFailureKind.UnknownMessage }:
                    // Message was deleted by someone: do NOT post a replacement (would duplicate and could re-ping).
                    r.EditPending = false;
                    r.NextAttemptAt = null;
                    r.LastError = "edit_target_deleted";
                    break;
                case SendOutcome.Permanent permanent:
                    r.EditPending = false;
                    r.NextAttemptAt = null;
                    r.LastError = Truncate($"edit_failed {permanent.Kind}: {permanent.Reason}");
                    break;
                case SendOutcome.RateLimited limited:
                    r.NextAttemptAt = now + limited.RetryAfter + TimeSpan.FromMilliseconds(250);
                    break;
                default:
                    // Transient/ambiguous edits are idempotent; retry a bounded number of times.
                    if (attempts >= _options.MaxEditAttempts)
                    {
                        r.EditPending = false;
                        r.NextAttemptAt = null;
                        r.LastError = "edit_attempts_exhausted";
                    }
                    else
                    {
                        r.NextAttemptAt = now + Backoff(attempts);
                    }

                    break;
            }
        }, ct);

        if (outcome is SendOutcome.Permanent p && p.Kind != PermanentFailureKind.UnknownMessage && policy is not null && IsChannelProblem(p.Kind))
            await policy.ReportChannelProblemAsync(new GuildId(row.GuildId), channel, p.Kind, ct);
    }

    private async Task ReconcileAsync(ToroDbContext db, OutboxMessageEntity row, ChannelId channel, CancellationToken ct)
    {
        var attempts = row.ReconcileAttempts + 1;
        ReconcileOutcome outcome;
        try
        {
            outcome = await transport.FindRecentAsync(channel, await ProbeAsync(db, row, ct), _options.ReconcileScanLimit, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Transport threw while reconciling outbox {OutboxId}", row.Id);
            outcome = new ReconcileOutcome.NotPossible("transport exception: " + ex.GetType().Name);
        }
        var now = clock.GetUtcNow();

        await SaveResilientAsync(db, row, r =>
        {
            r.ReconcileAttempts = attempts;
            r.UpdatedAt = now;
            switch (outcome)
            {
                case ReconcileOutcome.Found found:
                    r.Status = OutboxStatus.Sent;
                    r.DiscordMessageId = found.MessageId.Value;
                    r.SentAt ??= now;
                    r.LastError = "reconciled_found";
                    r.EditPending = r.PayloadHash != r.DeliveredPayloadHash;
                    r.NextAttemptAt = r.EditPending ? now : null;
                    break;
                case ReconcileOutcome.NotFound when r.Attempts >= 2:
                    // Already resent once after a verified absence and it became ambiguous again: stop, never loop
                    // (the earlier message may simply have scrolled out of the scanned history).
                    Finish(r, OutboxStatus.Failed, "unconfirmed_after_single_resend", now);
                    break;
                case ReconcileOutcome.NotFound when now <= r.ExpiresAt:
                    // Verified absent from the recent channel history: a single resend is safe.
                    r.Status = OutboxStatus.Pending;
                    r.NextAttemptAt = now;
                    r.LastError = "reconciled_absent";
                    break;
                case ReconcileOutcome.NotFound:
                    Finish(r, OutboxStatus.Expired, "reconciled_absent_expired", now);
                    break;
                case ReconcileOutcome.NotPossible notPossible:
                    r.LastError = Truncate("reconcile_not_possible: " + notPossible.Reason);
                    // Stop automatic handling after a few tries; surfaced to operators via doctor.
                    r.NextAttemptAt = attempts >= _options.MaxReconcileAttempts ? null : now + Backoff(attempts);
                    break;
            }
        }, ct);
    }

    /// <summary>
    /// Describes the ambiguous delivery: the fingerprint of what was transmitted (older rows: the current payload), created
    /// no earlier than the attempt (minus clock skew), and never a message another row already owns.
    /// </summary>
    private static async Task<DeliveryProbe> ProbeAsync(ToroDbContext db, OutboxMessageEntity row, CancellationToken ct)
    {
        var attemptAt = row.LastAttemptAt ?? row.CreatedAt;
        var channelId = row.ChannelId;
        var owned = await db.Outbox.AsNoTracking()
            .Where(o => o.ChannelId == channelId && o.Id != row.Id && o.DiscordMessageId != null)
            .OrderByDescending(o => o.Id)
            .Take(500)
            .Select(o => o.DiscordMessageId!.Value)
            .ToListAsync(ct);
        return new DeliveryProbe(
            row.DeliveredFingerprint ?? MessageFingerprint.Of(PayloadSerializer.Deserialize(row.PayloadJson)),
            attemptAt - TimeSpan.FromMinutes(5),
            owned.Select(id => new MessageId(id)).ToHashSet(),
            row.Marker);
    }

    private static bool IsChannelProblem(PermanentFailureKind kind) =>
        kind is PermanentFailureKind.MissingPermissions or PermanentFailureKind.MissingAccess or PermanentFailureKind.UnknownChannel;

    /// <summary>Applies a mutation and saves; on a concurrent update, reloads the row and re-applies (bounded).</summary>
    private static async Task SaveResilientAsync(ToroDbContext db, OutboxMessageEntity row, Action<OutboxMessageEntity> mutate, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            mutate(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                await db.Entry(row).ReloadAsync(ct);
            }
        }
    }

    private static void CancelOrDropEdit(OutboxMessageEntity row, bool isEdit, string reason, DateTimeOffset now)
    {
        if (isEdit)
        {
            row.EditPending = false;
            row.NextAttemptAt = null;
            row.LastError = "edit_skipped_" + reason;
        }
        else
        {
            Finish(row, OutboxStatus.Cancelled, reason, now);
        }

        row.UpdatedAt = now;
    }

    private static void Finish(OutboxMessageEntity row, OutboxStatus status, string? error, DateTimeOffset now)
    {
        row.Status = status;
        row.NextAttemptAt = null;
        row.LastError = error;
        row.UpdatedAt = now;
    }

    private static TimeSpan Backoff(int attempt)
    {
        var baseSeconds = Math.Min(15 * Math.Pow(2, Math.Max(0, attempt - 1)), 1800);
#pragma warning disable CA5394 // Jitter does not need cryptographic randomness.
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
#pragma warning restore CA5394
        return TimeSpan.FromSeconds(baseSeconds * jitter);
    }

    private static string Truncate(string value) => value.Length <= 480 ? value : value[..480];
}

public sealed class OutboxDispatcherService(OutboxProcessor processor, IOptions<DeliveryOptions> options, TimeProvider clock, ILogger<OutboxDispatcherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await processor.RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatcher iteration failed");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
