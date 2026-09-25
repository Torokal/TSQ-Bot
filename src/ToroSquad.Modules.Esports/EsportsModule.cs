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
using ToroSquad.Modules.Esports.Providers.PandaScore;
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
        services.AddOptions<PandaScoreOptions>().Bind(configuration.GetSection(PandaScoreOptions.Section));
        services.AddOptions<ValveStandingsOptions>().Bind(section.GetSection("Valve"));
        var mode = section.GetSection("Provider").GetValue("Mode", ProviderMode.Fixture);
        var providerName = section.GetSection("Provider").GetValue("Name", MatchProviderName.PandaScore);
        services.AddSingleton(new EsportsDataMode(mode));

        services.AddSingleton<LocalizationSource>(new LocalizationSource(typeof(EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"));
        services.AddSingleton<IModelContributor, EsportsModelContributor>();

        services.AddSingleton<RequestBudget>();
        var liquipediaBase = section.GetSection("Liquipedia").GetValue("BaseUrl", new LiquipediaOptions().BaseUrl)!;
        var liquipedia = services.AddHttpClient<LiquipediaClient>(c => c.BaseAddress = new Uri(liquipediaBase));
        var pandaBase = configuration.GetSection(PandaScoreOptions.Section).GetValue("BaseUrl", new PandaScoreOptions().BaseUrl)!;
        var panda = services.AddHttpClient<PandaScoreClient>(c => c.BaseAddress = new Uri(pandaBase));
        var valve = services.AddHttpClient<IRankingsProvider, ValveStandingsProvider>();
        if (mode == ProviderMode.Live)
        {
            // Live: real network only. There is deliberately NO fallback to fixture data on errors.
            liquipedia.ConfigurePrimaryHttpMessageHandler(LiveHandler);
            panda.ConfigurePrimaryHttpMessageHandler(LiveHandler);
            valve.ConfigurePrimaryHttpMessageHandler(LiveHandler);
        }
        else
        {
            // One anchor for the process (kept across restarts): IHttpClientFactory recycles handlers, so it cannot live in the handler.
            services.AddSingleton<IFixtureAnchorStore, ProviderStateFixtureAnchorStore>();
            services.AddSingleton<FixtureAnchor>();
            liquipedia.ConfigurePrimaryHttpMessageHandler(sp => new FixtureHttpHandler(sp.GetRequiredService<TimeProvider>(), anchor: sp.GetRequiredService<FixtureAnchor>()));
            panda.ConfigurePrimaryHttpMessageHandler(sp => new FixtureHttpHandler(sp.GetRequiredService<TimeProvider>(), anchor: sp.GetRequiredService<FixtureAnchor>()));
            valve.ConfigurePrimaryHttpMessageHandler(sp => new FixtureHttpHandler(sp.GetRequiredService<TimeProvider>(), anchor: sp.GetRequiredService<FixtureAnchor>()));
        }

        // Provider-independent from here on: the planner, cache, commands and renderer only see IEsportsDataProvider.
        if (providerName == MatchProviderName.Liquipedia)
            services.AddSingleton<IEsportsDataProvider>(sp => new LiquipediaProvider(sp.GetRequiredService<LiquipediaClient>(), sp.GetRequiredService<EsportsDataMode>()));
        else
            services.AddSingleton<IEsportsDataProvider>(sp => new PandaScoreProvider(sp.GetRequiredService<PandaScoreClient>(), sp.GetRequiredService<EsportsDataMode>()));
        services.AddSingleton<MatchLinkCatalog>();
        services.AddSingleton<TeamDirectory>();
        services.AddSingleton<LiquipediaHltvLinkSource>();

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
        var panda = configuration.GetSection(PandaScoreOptions.Section).Get<PandaScoreOptions>() ?? new PandaScoreOptions();
        var errors = new List<string>(esports.Validate(liquipedia, panda));
        if (esports.Provider.Mode == ProviderMode.Live)
        {
            var problem = esports.Provider.Name == MatchProviderName.Liquipedia
                ? LiquipediaClient.ConfigurationProblem(liquipedia, requireKey: true)
                : PandaScoreOptions.ConfigurationProblem(panda, requireToken: true);
            if (problem is not null)
                errors.Add("Live provider mode: " + problem);
        }
        return errors;
    }
}
