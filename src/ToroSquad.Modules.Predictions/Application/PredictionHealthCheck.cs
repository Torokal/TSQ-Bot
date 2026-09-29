using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>
/// /bot status lines for TSQ Öngörü — cheap reads only (no Discord call beyond the gateway cache): for every guild with the
/// module enabled, the bot's permissions in both channels and whether the creator role exists; then cards whose post is
/// still uncertain, cards that are gone or could not be updated, closing announcements not delivered, and a consistency
/// check (every wallet's pending coins equal its pending stakes). Counts only — never names or amounts of members. A
/// singleton like every health check (it may be resolved outside a request); the database is read in its own scope.
/// </summary>
public sealed class PredictionHealthCheck(IServiceScopeFactory scopes, IGuildGateway guilds, ILogger<PredictionHealthCheck> logger) : IModuleHealthCheck
{
    public ModuleId Module => PredictionsModule.ModuleIdTyped;

    public async Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<PredictionStore>();
        var guards = scope.ServiceProvider.GetRequiredService<PredictionGuards>();
        var modules = scope.ServiceProvider.GetRequiredService<IModuleStateStore>();
        var entries = new List<HealthEntry>();
        foreach (var guild in await modules.GetGuildsWithModuleEnabledAsync(Module, cancellationToken))
        {
            var missing = new List<string>();
            foreach (var (channel, required) in new[]
                     {
                         (guards.PredictionsChannel, PredictionRules.RequiredChannelPermissions),
                         (guards.CommandsChannel, PredictionRules.RequiredCommandsChannelPermissions),
                     })
            {
                var access = await guilds.GetBotChannelAccessAsync(guild, channel, cancellationToken);
                if (!access.Exists || !access.IsTextBased)
                    missing.Add(PredictionGuards.ChannelMention(channel) + ": ?");
                else if (!access.Permissions.Grants(required))
                    missing.Add(PredictionGuards.ChannelMention(channel) + ": " + (required & ~access.Permissions));
            }

            entries.Add(missing.Count == 0
                ? new HealthEntry("predictions.health.channels", HealthState.Healthy, "predictions.health.channels_ok")
                : new HealthEntry("predictions.health.channels", HealthState.Degraded, "predictions.health.channels_missing", [string.Join(", ", missing)]));

            var roles = await guilds.GetRoleSnapshotAsync(guild, cancellationToken);
            if (roles is not null && roles.Find(guards.CreatorRole) is null)
                entries.Add(new HealthEntry("predictions.health.role", HealthState.Degraded, "predictions.health.role_missing", [PredictionGuards.RoleMention(guards.CreatorRole)]));
        }

        var publishing = await store.Predictions.AsNoTracking().CountAsync(p => p.Status == PredictionStatus.Publishing, cancellationToken);
        var missingCards = await store.Predictions.AsNoTracking()
            .CountAsync(p => p.CardMissing && (p.Status == PredictionStatus.Open || p.Status == PredictionStatus.Locked), cancellationToken);
        var stuck = await store.Predictions.AsNoTracking().CountAsync(p => !p.CardStale && p.CardSyncAttempts >= PredictionCardSync.MaxAttempts, cancellationToken);
        entries.Add(publishing + missingCards + stuck == 0
            ? new HealthEntry("predictions.health.cards", HealthState.Healthy, "predictions.health.cards_ok")
            : new HealthEntry("predictions.health.cards", HealthState.Degraded, "predictions.health.cards_problem", [publishing, missingCards, stuck]));

        var outbox = await store.Db.Outbox.AsNoTracking().Where(o => o.ModuleId == PredictionsModule.ModuleIdValue)
            .GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var waiting = outbox.Where(o => o.Key is OutboxStatus.Pending or OutboxStatus.InFlight or OutboxStatus.DeliveryUnknown).Sum(o => o.Count);
        var failed = outbox.Where(o => o.Key is OutboxStatus.Failed or OutboxStatus.Expired).Sum(o => o.Count);
        if (waiting + failed > 0)
            entries.Add(new HealthEntry("predictions.health.announcements", failed > 0 ? HealthState.Degraded : HealthState.Healthy,
                "predictions.health.announcements_value", [waiting, failed]));

        var inconsistent = await store.Wallets.AsNoTracking()
            .CountAsync(w => w.PendingMinor != (store.Entries.Where(e => e.WalletId == w.Id && e.Status == PredictionEntryStatus.Pending).Sum(e => (long?)e.StakeMinor) ?? 0),
                cancellationToken);
        if (inconsistent > 0)
        {
            logger.LogError("TSQ Öngörü consistency: {Count} wallet(s) whose pending coins differ from their pending stakes", inconsistent);
            entries.Add(new HealthEntry("predictions.health.consistency", HealthState.Unavailable, "predictions.health.inconsistent", [inconsistent]));
        }

        return new ModuleHealthReport(Module, entries);
    }
}

/// <summary>
/// The closing announcement goes through the shared outbox (retries, at most once, never late). Disabling the module keeps
/// the default gate: an announcement not yet delivered when the module is disabled is cancelled (the tournament itself is
/// closed either way). Channel problems are logged for the doctor.
/// </summary>
public sealed class PredictionDeliveryPolicy(ILogger<PredictionDeliveryPolicy> logger) : IDeliveryPolicy
{
    public ModuleId Module => PredictionsModule.ModuleIdTyped;

    public Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken) =>
        Task.FromResult(DeliveryDecision.Allowed);

    public Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        logger.LogWarning("TSQ Öngörü announcement: channel {Channel} in guild {Guild} is unusable ({Kind})", channel, guild, kind);
        return Task.CompletedTask;
    }
}
