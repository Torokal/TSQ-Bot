using Microsoft.Extensions.Options;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>
/// /bot status lines: whether the API key is configured and how the last /ozetle ended, and the configured generation —
/// the global mode, how many canary channels there are (never their ids) and the two grounded models. Reads configuration
/// and remembered state only — never calls the AI provider (a status check must not spend quota).
/// </summary>
public sealed class SummaryHealthCheck(SummaryService summaries, IOptions<SummaryOptions> options) : IModuleHealthCheck
{
    public const string Component = "summary.health.component";
    public const string GenerationComponent = "summary.health.generation_component";

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
        var settings = options.Value;
        var generation = new HealthEntry(GenerationComponent, HealthState.Healthy, "summary.health.generation",
            [settings.GenerationMode.ToString(), settings.GroundedCanaryChannelIds.Length, settings.GroundedGeneratorModel, settings.GroundedReviewerModel]);
        return Task.FromResult(new ModuleHealthReport(Module, [entry, generation]));
    }
}
