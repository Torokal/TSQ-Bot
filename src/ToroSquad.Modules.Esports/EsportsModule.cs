using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Commands.Core;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Commands;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;
using ToroSquad.Modules.Esports.Providers.Valve;

namespace ToroSquad.Modules.Esports;

/// <summary>Counter-Strike 2 esports tracking and notifications — the first TSQ Bot feature module.</summary>
public sealed class EsportsModule : IToroModule
{
    public const string ModuleIdValue = "esports";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.esports.name",
        "module.esports.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation after setup + ping-free preview
        RequiredBotChannelPermissions: GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks,
        OptionalBotPermissions: GuildPermission.ReadMessageHistory | GuildPermission.ManageRoles | GuildPermission.MentionEveryone,
        AdminCommands: ["esports-admin"]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } =
    [
        typeof(EsportsCommands),
        typeof(EsportsAdminCommands),
        typeof(EsportsSetupComponents),
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(EsportsOptions.Section);
        services.AddOptions<EsportsOptions>().Bind(section);
        services.AddOptions<LiquipediaOptions>().Bind(section.GetSection("Liquipedia"));
        services.AddOptions<ValveStandingsOptions>().Bind(section.GetSection("Valve"));
        var mode = section.GetSection("Provider").GetValue("Mode", ProviderMode.Fixture);
        services.AddSingleton(new EsportsDataMode(mode));

        services.AddSingleton<LocalizationSource>(new LocalizationSource(typeof(EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"));
        services.AddSingleton<IModelContributor, EsportsModelContributor>();

        services.AddSingleton<RequestBudget>();
        var liquipediaBase = section.GetSection("Liquipedia").GetValue("BaseUrl", new LiquipediaOptions().BaseUrl)!;
        var liquipedia = services.AddHttpClient<LiquipediaClient>(c => c.BaseAddress = new Uri(liquipediaBase));
        var valve = services.AddHttpClient<IRankingsProvider, ValveStandingsProvider>();
        if (mode == ProviderMode.Live)
        {
            // Live: real network only. There is deliberately NO fallback to fixture data on errors.
            liquipedia.ConfigurePrimaryHttpMessageHandler(LiveHandler);
            valve.ConfigurePrimaryHttpMessageHandler(LiveHandler);
        }
        else
        {
            liquipedia.ConfigurePrimaryHttpMessageHandler(sp => new FixtureHttpHandler(sp.GetRequiredService<TimeProvider>()));
            valve.ConfigurePrimaryHttpMessageHandler(sp => new FixtureHttpHandler(sp.GetRequiredService<TimeProvider>()));
        }

        services.AddSingleton<IEsportsDataProvider>(sp => new LiquipediaProvider(sp.GetRequiredService<LiquipediaClient>(), sp.GetRequiredService<EsportsDataMode>()));

        services.AddSingleton<EsportsCache>();
        services.AddSingleton<NotificationRenderer>();
        services.AddScoped<NotificationPlanner>();
        services.AddScoped<EsportsConfigService>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<RoleMappingService>();
        services.AddScoped<EsportsDoctor>();
        services.AddScoped<EsportsPreviewService>();
        services.AddScoped<IDeliveryPolicy, EsportsDeliveryPolicy>();
        services.AddScoped<IModuleLifecycleHandler, EsportsLifecycle>();
        services.AddScoped<IUserDataContributor, EsportsUserData>();
        services.AddSingleton<IModuleHealthCheck, EsportsHealthCheck>();
        services.AddScoped<IModuleSetupFlow, EsportsSetupFlow>();
        services.AddSingleton<EsportsPoller>();
    }

    // Long-lived singleton clients: recycle pooled connections so DNS changes are picked up.
    private static HttpMessageHandler LiveHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    };

    /// <summary>Background polling is registered separately so one-shot CLI verbs do not start it.</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<EsportsPoller>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(EsportsOptions.Section);
        var esports = section.Get<EsportsOptions>() ?? new EsportsOptions();
        var liquipedia = section.GetSection("Liquipedia").Get<LiquipediaOptions>() ?? new LiquipediaOptions();
        var errors = new List<string>(esports.Validate(liquipedia));
        if (esports.Provider.Mode == ProviderMode.Live && LiquipediaClient.ConfigurationProblem(liquipedia, requireKey: true) is { } problem)
            errors.Add("Live provider mode: " + problem);
        return errors;
    }
}
