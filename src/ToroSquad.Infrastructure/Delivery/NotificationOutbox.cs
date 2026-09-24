using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Infrastructure.Delivery;

public static class PayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Serialize(OutgoingMessage message) => JsonSerializer.Serialize(message, Options);

    public static OutgoingMessage Deserialize(string json) =>
        JsonSerializer.Deserialize<OutgoingMessage>(json, Options) ?? throw new InvalidOperationException("Empty outbox payload.");

    public static string Hash(string payloadJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));

    /// <summary>Short public reference placed in the embed footer; used for bounded reconciliation.</summary>
    public static string Marker(string logicalKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(logicalKey)))[..10];
}

/// <summary>
/// Stages notifications into the caller's DbContext (no SaveChanges here) so the module commits its own state
/// and the outbox rows in one SQLite transaction. The unique LogicalKey index is the final duplicate guard.
/// </summary>
public sealed class NotificationOutbox(ToroDbContext db, TimeProvider clock) : INotificationOutbox
{
    public async Task<StageOutcome> StageAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        var errors = DiscordLimits.Validate(request.Message);
        if (errors.Count > 0)
            throw new ArgumentException("Invalid notification payload: " + string.Join("; ", errors), nameof(request));

        var key = request.LogicalKey;
        var json = PayloadSerializer.Serialize(request.Message);
        var hash = PayloadSerializer.Hash(json);
        var now = clock.GetUtcNow();

        var row = db.Outbox.Local.FirstOrDefault(x => x.LogicalKey == key)
                  ?? await db.Outbox.FirstOrDefaultAsync(x => x.LogicalKey == key, cancellationToken);

        if (row is null)
        {
            db.Outbox.Add(new OutboxMessageEntity
            {
                LogicalKey = key,
                GuildId = request.Guild.Value,
                ModuleId = request.Module.Value,
                ChannelId = request.Channel.Value,
                Kind = request.Kind,
                SourceKey = request.SourceKey,
                Marker = PayloadSerializer.Marker(key),
                IsDryRun = request.IsDryRun,
                PayloadJson = json,
                PayloadHash = hash,
                Status = OutboxStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                NextAttemptAt = now,
                ExpiresAt = request.ExpiresAt,
            });
            return StageOutcome.Created;
        }

        if (row.Status == OutboxStatus.Sent)
        {
            // Compare against what is visible in Discord (DeliveredPayloadHash), not the last staged payload: an edit
            // that was dropped (pause/module off) is re-staged once the data is planned again.
            if (hash == row.DeliveredPayloadHash)
            {
                if (row.EditPending)
                {
                    row.PayloadJson = json;
                    row.PayloadHash = hash;
                    row.EditPending = false;
                    row.NextAttemptAt = null;
                    row.UpdatedAt = now;
                }

                return StageOutcome.Unchanged;
            }

            if (row.EditPending && hash == row.PayloadHash)
                return StageOutcome.Unchanged;
        }
        else if (row.PayloadHash == hash)
        {
            return StageOutcome.Unchanged;
        }

        switch (row.Status)
        {
            case OutboxStatus.Pending:
                // Not yet delivered: just replace the content (still exactly one message).
                row.PayloadJson = json;
                row.PayloadHash = hash;
                row.ExpiresAt = request.ExpiresAt;
                row.UpdatedAt = now;
                return StageOutcome.UpdatedPending;

            case OutboxStatus.Sent:
                // Correction after delivery: edit the same Discord message, never post a new one, never re-ping.
                row.PayloadJson = json;
                row.PayloadHash = hash;
                row.EditPending = true;
                row.EditAttempts = 0;
                row.NextAttemptAt = now;
                row.UpdatedAt = now;
                return StageOutcome.EditScheduled;

            case OutboxStatus.DeliveryUnknown:
            case OutboxStatus.InFlight:
                // Keep the newest content so a reconciled message can be edited afterwards; do not resend.
                row.PayloadJson = json;
                row.PayloadHash = hash;
                row.UpdatedAt = now;
                return StageOutcome.UpdatedPending;

            default:
                // Failed / Cancelled / Expired / Simulated are terminal: never resurrect (no backlog floods).
                return StageOutcome.IgnoredTerminal;
        }
    }
}
