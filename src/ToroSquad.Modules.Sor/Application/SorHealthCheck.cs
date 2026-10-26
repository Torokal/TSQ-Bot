using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Ai;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>
/// /bot status: whether the shared key is configured, the model, and how the last /sor ended (stage and class, HTTP status,
/// trace code — never text). Reads configuration and remembered state only: never calls the provider.
/// </summary>
public sealed class SorHealthCheck([FromKeyedServices(SorModule.ModuleIdValue)] IAiChatClient ai, SorStatus status, IOptions<SorOptions> options) : IModuleHealthCheck
{
    public const string Component = "sor.health.component";

    public ModuleId Module => SorModule.ModuleIdTyped;

    public Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var entry = !ai.IsConfigured
            ? new HealthEntry(Component, HealthState.NotConfigured, "sor.health.not_configured")
            : status.Last switch
            {
                null => new HealthEntry(Component, HealthState.Healthy, "sor.health.idle", [options.Value.Model]),
                { Outcome: "completed" } last => new HealthEntry(Component, HealthState.Healthy, "sor.health.ok", [options.Value.Model, DiscordText.Timestamp(last.At, 'R')]),
                var last => new HealthEntry(Component, HealthState.Degraded, "sor.health.failed",
                    [last.Outcome, last.Failure ?? "—", last.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—", last.Trace ?? "—", DiscordText.Timestamp(last.At, 'R')]),
            };
        return Task.FromResult(new ModuleHealthReport(Module, [entry]));
    }
}
