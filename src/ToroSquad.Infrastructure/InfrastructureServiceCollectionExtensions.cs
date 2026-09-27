using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Hosting;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Configuration keys whose values are secrets (redacted everywhere, never logged).</summary>
    public static readonly IReadOnlyList<string> SecretConfigurationKeys =
    [
        "Discord:Token",
        "Esports:Liquipedia:ApiKey",
        "PandaScore:Token",
        "Formula1:OpenF1:Username",
        "Formula1:OpenF1:Password",
        "Volleyball:Fivb:AppId",
        "Live:Twitch:ClientId",
        "Live:Twitch:ClientSecret",
        "Live:Kick:ClientId",
        "Live:Kick:ClientSecret",
    ];

    public static IServiceCollection AddToroInfrastructure(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<BotOptions>().Bind(configuration.GetSection(BotOptions.Section));
        services.AddOptions<DeliveryOptions>().Bind(configuration.GetSection("Delivery"));

        var botOptions = configuration.GetSection(BotOptions.Section).Get<BotOptions>() ?? new BotOptions();
        var databasePath = botOptions.DatabasePath(contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        services.AddSingleton(new DatabaseLocation(databasePath));
        services.AddDbContext<ToroDbContext>(o => o
            .UseSqlite(DatabaseMaintenance.ConnectionString(databasePath), sqlite => sqlite.MigrationsAssembly("ToroSquad.Bot"))
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ToroDbContext>());

        services.AddSingleton(new SecretRedactor(SecretConfigurationKeys.Select(k => configuration[k])));

        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<ILocalizer>(sp => new LocalizationCatalog(sp.GetServices<LocalizationSource>()));

        services.AddScoped<IGuildSettingsStore, GuildSettingsStore>();
        services.AddScoped<IModuleStateStore, ModuleStateStore>();
        services.AddScoped<IModuleGate, ModuleGate>();
        services.AddScoped<IConfirmationStore, ConfirmationStore>();
        services.AddScoped<GuildPresenceStore>();
        services.AddScoped<IGuildPresenceTracker>(sp => sp.GetRequiredService<GuildPresenceStore>());
        services.AddScoped<IManagedCommandStore, ManagedCommandStore>();
        services.AddScoped<CoreGuildDataPurger>();
        services.AddScoped<INotificationOutbox, NotificationOutbox>();
        services.AddScoped<GuildSettingsService>();
        services.AddScoped<ModuleManagementService>();
        services.AddScoped<PrivacyService>();

        services.AddSingleton<OutboxProcessor>();
        return services;
    }

    /// <summary>Background jobs (not registered for one-shot CLI verbs).</summary>
    public static IServiceCollection AddToroBackgroundJobs(this IServiceCollection services)
    {
        services.AddHostedService<OutboxDispatcherService>();
        services.AddHostedService<RetentionService>();
        services.AddHostedService<DatabaseBackupService>();
        return services;
    }
}
