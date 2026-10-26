using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Persistence;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>What one cleanup pass removed or closed (counts only).</summary>
public sealed record SorCleanupReport(int Conversations, int Requests, int StalePending, int UsageDays);

/// <summary>
/// The retention matrix, applied in every guild whether /sor is enabled or not:
/// <list type="bullet">
/// <item>Conversations and their turns: deleted when the guild's history retention (default 24 h) has passed since the last
/// activity.</item>
/// <item>Pending answers: a request still "generating" past the request timeout plus a margin (a restart) becomes Unknown —
/// counted, never generated again; a generated answer not finalized within <see cref="PendingLifetime"/> is discarded and its
/// content cleared.</item>
/// <item>Request records (the idempotency keys and per-member counts): deleted after <see cref="RequestRetention"/>, longer
/// than any interaction can repeat (tokens live 15 minutes) and than the daily window.</item>
/// <item>Guild daily totals (no member ids): <see cref="UsageRetentionDays"/> days.</item>
/// </list>
/// Notes are not touched: they live until the member deletes them, /privacy delete, or the guild purge.
/// </summary>
public sealed class SorCleanup(ToroDbContext db, SorMemoryStore memory, IOptions<SorOptions> options, TimeProvider clock)
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RequestRetention = TimeSpan.FromHours(48);
    public const int UsageRetentionDays = 35;

    public async Task<SorCleanupReport> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var settings = options.Value;

        // Per-guild history retention (a guild may keep less than the default, never more).
        var shorter = (await db.Set<SorGuildConfigEntity>().AsNoTracking().Where(c => c.HistoryHours != null).Select(c => new { c.GuildId, c.HistoryHours }).ToListAsync(ct))
            .Where(c => c.HistoryHours < settings.HistoryRetentionHours).ToList();
        var defaultCutoff = now - TimeSpan.FromHours(settings.HistoryRetentionHours);
        var expired = await db.Set<SorConversationEntity>().Where(c => c.LastActivityAt < defaultCutoff).Select(c => c.Id).ToListAsync(ct);
        foreach (var guild in shorter)
        {
            var cutoff = now - TimeSpan.FromHours(guild.HistoryHours!.Value);
            expired.AddRange(await db.Set<SorConversationEntity>().Where(c => c.GuildId == guild.GuildId && c.LastActivityAt < cutoff).Select(c => c.Id).ToListAsync(ct));
        }

        var conversations = await SorWrites.RunAsync(db, () => memory.DeleteConversationsAsync(expired.Distinct().ToList(), ct), ct);

        var generatingCutoff = now - settings.RequestTimeout - TimeSpan.FromMinutes(2);
        var stale = await db.Set<SorRequestEntity>().Where(r => r.State == SorRequestState.Generating && r.UpdatedAt < generatingCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, SorRequestState.Unknown).SetProperty(r => r.Failure, "unknown:restart").SetProperty(r => r.UpdatedAt, now), ct);
        var pendingCutoff = now - PendingLifetime;
        stale += await db.Set<SorRequestEntity>().Where(r => r.State == SorRequestState.Generated && r.UpdatedAt < pendingCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, SorRequestState.Unknown).SetProperty(r => r.Failure, "unknown:pending-expired")
                .SetProperty(r => r.PendingAnswer, (string?)null).SetProperty(r => r.PendingMemory, (string?)null)
                .SetProperty(r => r.PendingContext, (string?)null).SetProperty(r => r.UpdatedAt, now), ct);

        var requestCutoff = now - RequestRetention;
        var requests = await db.Set<SorRequestEntity>().Where(r => r.CreatedAt < requestCutoff).ExecuteDeleteAsync(ct);

        var usageCutoff = now - TimeSpan.FromDays(UsageRetentionDays);
        var usage = await db.Set<SorGuildUsageEntity>().Where(u => u.UpdatedAt < usageCutoff).ExecuteDeleteAsync(ct);
        return new SorCleanupReport(conversations, requests, stale, usage);
    }
}

/// <summary>Runs <see cref="SorCleanup"/> every 10 minutes (long-running host only; never makes an AI request).</summary>
public sealed class SorCleanupWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SorCleanupWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var report = await scope.ServiceProvider.GetRequiredService<SorCleanup>().RunOnceAsync(stoppingToken);
                if (report.Conversations + report.Requests + report.StalePending + report.UsageDays > 0)
                    logger.LogInformation("Sor cleanup: {Conversations} conversation rows, {Requests} request records, {Stale} pending, {Usage} usage days",
                        report.Conversations, report.Requests, report.StalePending, report.UsageDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sor cleanup failed");
            }

            try
            {
                await Task.Delay(Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
