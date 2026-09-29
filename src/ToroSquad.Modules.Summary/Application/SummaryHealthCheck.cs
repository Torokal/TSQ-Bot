using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>
/// /bot status line: whether the API key is configured and how the last /ozetle ended. Reads remembered state only — never
/// calls the AI provider (a status check must not spend quota).
/// </summary>
public sealed class SummaryHealthCheck(SummaryService summaries) : IModuleHealthCheck
{
    public const string Component = "summary.health.component";

    public ModuleId Module => SummaryModule.ModuleIdTyped;

    public Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var entry = !summaries.IsConfigured
            ? new HealthEntry(Component, HealthState.NotConfigured, "summary.health.not_configured")
            : summaries.LastRun switch
            {
                null => new HealthEntry(Component, HealthState.Healthy, "summary.health.idle"),
                { Outcome: SummaryOutcome.AiFailed } last => new HealthEntry(Component, HealthState.Degraded, "summary.health.ai_failed",
                    [last.AiFailure.ToString(), DiscordText.Timestamp(last.At, 'R')]),
                { Outcome: SummaryOutcome.FetchFailed or SummaryOutcome.PostFailed } last => new HealthEntry(Component, HealthState.Degraded,
                    "summary.health.discord_failed", [DiscordText.Timestamp(last.At, 'R')]),
                var last => new HealthEntry(Component, HealthState.Healthy, "summary.health.ok", [DiscordText.Timestamp(last.At, 'R')]),
            };
        return Task.FromResult(new ModuleHealthReport(Module, [entry]));
    }
}
