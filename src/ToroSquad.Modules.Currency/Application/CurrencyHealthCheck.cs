using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;

namespace ToroSquad.Modules.Currency.Application;

/// <summary>
/// /bot status line: how the last /dolar, /euro or /altın was answered. Reads remembered state only — never calls a
/// provider, so a provider outage can neither slow the status command nor keep the bot from starting.
/// </summary>
public sealed class CurrencyHealthCheck(MarketQuoteService quotes) : IModuleHealthCheck
{
    public const string Component = "currency.health.component";

    public ModuleId Module => CurrencyModule.ModuleIdTyped;

    public Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var entry = quotes.LastStatus is not { } last
            ? new HealthEntry(Component, HealthState.Healthy, "currency.health.idle")
            : last.Outcome switch
            {
                QuoteOutcome.Primary => new HealthEntry(Component, HealthState.Healthy, "currency.health.primary", [DiscordText.Timestamp(last.At, 'R')]),
                QuoteOutcome.Fallback => new HealthEntry(Component, HealthState.Degraded, "currency.health.fallback",
                    [CurrencyCardRenderer.SourceKey(last.Source!.Value), DiscordText.Timestamp(last.At, 'R')]),
                QuoteOutcome.Stale => new HealthEntry(Component, HealthState.Degraded, "currency.health.stale", [DiscordText.Timestamp(last.At, 'R')]),
                _ => new HealthEntry(Component, HealthState.Unavailable, "currency.health.unavailable", [DiscordText.Timestamp(last.At, 'R')]),
            };
        return Task.FromResult(new ModuleHealthReport(Module, [entry]));
    }
}
