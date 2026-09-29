using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Commands;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Modules.Predictions;

/// <summary>
/// TSQ Öngörü (Predictions): community predictions with FIXED odds and purely virtual TSQ Coin (docs/predictions/
/// TSQ_PREDICTIONS.md). Members of the creator role publish a question with 2–25 outcomes and odds in the predictions
/// channel; members pick an outcome on the public card and stake coins; the creator (while holding the role) or an
/// administrator settles or cancels it by hand, and payouts or refunds are booked atomically. Every member starts each
/// tournament with the starting balance and may claim a daily reward once per Türkiye calendar day; administrators close a
/// tournament, which freezes its podium and starts the next one. No real money, no transfers, no shop, no providers, no AI.
/// A separate feature module: depends only on the shared TSQ layers. Off by default in every guild
/// (/modules enable predictions).
/// </summary>
public sealed class PredictionsModule : IToroModule
{
    public const string ModuleIdValue = "predictions";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.predictions.name",
        "module.predictions.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable predictions
        RequiredBotChannelPermissions: PredictionRules.RequiredChannelPermissions, // the card: post, embed, find again, edit
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: []); // /ongoru mixes member and manager subcommands: every subcommand authorizes itself

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(PredictionCommands), typeof(PredictionComponents)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PredictionsOptions>().Bind(configuration.GetSection(PredictionsOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(PredictionsModule).Assembly, "ToroSquad.Modules.Predictions.Localization"));
        services.AddSingleton<IModelContributor, PredictionsModelContributor>();

        services.AddSingleton<IPredictionRandom, SecurePredictionRandom>();
        services.AddSingleton<PredictionTokens>();
        services.AddSingleton<PredictionCardGate>();
        services.AddSingleton<PredictionCards>();
        services.AddSingleton<PredictionMessages>();
        services.AddSingleton<PredictionWorker>();
        services.AddScoped<PredictionGuards>();
        services.AddScoped<PredictionStore>();
        services.AddScoped<PredictionCardSync>();
        services.AddScoped<PredictionService>();
        services.AddScoped<PredictionEconomy>();
        services.AddScoped<IUserDataContributor, PredictionUserData>();
        services.AddScoped<IDeliveryPolicy, PredictionDeliveryPolicy>();
        services.AddSingleton<IModuleHealthCheck, PredictionHealthCheck>();
    }

    /// <summary>Lock + card loop, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<PredictionWorker>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration)
    {
        var errors = new List<string>((configuration.GetSection(PredictionsOptions.Section).Get<PredictionsOptions>() ?? new PredictionsOptions()).Validate());
        if (!GuildTime.TryResolve(TurkeyCalendar.TimeZoneId, out _))
            errors.Add($"time zone {TurkeyCalendar.TimeZoneId} is not available on this host (lock times and the daily reward use Türkiye time)");
        return errors;
    }
}
