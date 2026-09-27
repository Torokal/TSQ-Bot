using Microsoft.Extensions.Options;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;

namespace ToroSquad.Modules.Birthday.Application;

/// <summary>What one reconciliation pass did (counts only; no user ids).</summary>
public sealed record BirthdayPassSummary(
    int Guilds,
    int Detected,
    int MissingMembers,
    int AnnouncementsQueued,
    int RolesAssigned,
    int RolesRemoved,
    int RoleFailures,
    int Errors)
{
    public bool Changed => Detected + AnnouncementsQueued + RolesAssigned + RolesRemoved + RoleFailures + Errors > 0;
}

/// <summary>Process-wide, thread-safe view of the reconciliation loop for status, doctor and /bot status.</summary>
public sealed class BirthdayHealth
{
    private readonly Lock _gate = new();

    public DateTimeOffset? WorkerStartedAt { get; private set; }
    public DateTimeOffset? LastStartedAt { get; private set; }
    public DateTimeOffset? LastCompletedAt { get; private set; }
    public DateTimeOffset? LastFailedAt { get; private set; }
    public string? LastError { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public BirthdayPassSummary? LastSummary { get; private set; }

    public void WorkerStarted(DateTimeOffset at)
    {
        lock (_gate)
            WorkerStartedAt = at;
    }

    public void Started(DateTimeOffset at)
    {
        lock (_gate)
            LastStartedAt = at;
    }

    public void Completed(DateTimeOffset at, BirthdayPassSummary summary)
    {
        lock (_gate)
        {
            LastCompletedAt = at;
            LastSummary = summary;
            ConsecutiveFailures = 0;
        }
    }

    public void Failed(DateTimeOffset at, string error)
    {
        lock (_gate)
        {
            LastFailedAt = at;
            LastError = error;
            ConsecutiveFailures++;
        }
    }

    /// <summary>A pass completed recently enough: at most three intervals plus the start delay ago.</summary>
    public bool IsFresh(DateTimeOffset now, TimeSpan interval) =>
        LastCompletedAt is { } done && now - done <= (interval * 3) + BirthdayWorker.StartDelay;
}

/// <summary>Cheap summary for /bot status (cached loop state only).</summary>
public sealed class BirthdayHealthCheck(BirthdayHealth health, IOptions<BirthdayOptions> options, TimeProvider clock) : IModuleHealthCheck
{
    public ModuleId Module => BirthdayModule.ModuleIdTyped;

    public Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var entries = new List<HealthEntry>();
        if (health.WorkerStartedAt is null)
            entries.Add(new HealthEntry("birthday.health.scheduler", HealthState.NotConfigured, "birthday.health.not_running"));
        else if (health.LastCompletedAt is not { } done)
            entries.Add(new HealthEntry("birthday.health.scheduler", HealthState.Degraded, "birthday.health.no_pass_yet"));
        else
            entries.Add(new HealthEntry("birthday.health.scheduler",
                health.IsFresh(clock.GetUtcNow(), options.Value.ReconciliationInterval) ? health.ConsecutiveFailures > 0 ? HealthState.Degraded : HealthState.Healthy : HealthState.Unavailable,
                "birthday.health.last_pass", [DiscordText.Timestamp(done, 'R')]));
        return Task.FromResult(new ModuleHealthReport(Module, entries));
    }
}
