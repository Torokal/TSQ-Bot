using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Commands.Core;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Commands;
using ToroSquad.Modules.Formula1.Persistence;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Modules.Formula1.Providers.Fixtures;
using ToroSquad.Modules.Formula1.Providers.Jolpica;
using ToroSquad.Modules.Formula1.Providers.OpenF1;

namespace ToroSquad.Modules.Formula1;

/// <summary>
/// Formula 1: schedule, confirmed session starts, results (with corrections), championship standings. A separate feature
/// module: depends only on the shared TSQ layers, never on another feature module.
/// </summary>
public sealed class Formula1Module : IToroModule
{
    public const string ModuleIdValue = "formula1";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public const string JolpicaHttpClient = "f1-jolpica";
    public const string OpenF1HttpClient = "f1-openf1";

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.formula1.name",
        "module.formula1.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation after setup + ping-free preview
        RequiredBotChannelPermissions: GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks,
        OptionalBotPermissions: GuildPermission.ReadMessageHistory | GuildPermission.MentionEveryone,
        AdminCommands: ["f1-admin"]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } =
    [
        typeof(Formula1Commands),
        typeof(Formula1AdminCommands),
        typeof(Formula1SetupComponents),
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(Formula1Options.Section);
        services.AddOptions<Formula1Options>().Bind(section);
        services.AddOptions<JolpicaOptions>().Bind(section.GetSection("Jolpica"));
        services.AddOptions<OpenF1Options>().Bind(section.GetSection("OpenF1"));
        var providers = section.GetSection("Provider").Get<Formula1Options.ProviderSection>() ?? new Formula1Options.ProviderSection();
        var mode = new F1DataMode(providers.Mode);
        services.AddSingleton(mode);

        services.AddSingleton(new LocalizationSource(typeof(Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"));
        services.AddSingleton<IModelContributor, Formula1ModelContributor>();
        services.AddSingleton<F1RequestBudget>();

        var jolpicaBase = section.GetSection("Jolpica").GetValue("BaseUrl", new JolpicaOptions().BaseUrl)!;
        var openF1Base = section.GetSection("OpenF1").GetValue("BaseUrl", new OpenF1Options().BaseUrl)!;
        var jolpica = services.AddHttpClient(JolpicaHttpClient, c => c.BaseAddress = new Uri(jolpicaBase));
        var openF1 = services.AddHttpClient(OpenF1HttpClient, c => c.BaseAddress = new Uri(openF1Base));
        var token = services.AddHttpClient(OpenF1TokenProvider.HttpClientName);
        if (mode.IsDemo)
        {
            // Explicit fixture mode: synthetic TEST/DEMO data through the real clients and parsers.
            services.AddSingleton<IF1FixtureAnchorStore, F1ProviderStateFixtureAnchorStore>();
            services.AddSingleton<F1FixtureAnchor>();
            jolpica.ConfigurePrimaryHttpMessageHandler(sp => new F1FixtureHttpHandler(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<F1FixtureAnchor>()));
            openF1.ConfigurePrimaryHttpMessageHandler(sp => new F1FixtureHttpHandler(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<F1FixtureAnchor>()));
            token.ConfigurePrimaryHttpMessageHandler(sp => new F1FixtureHttpHandler(sp.GetRequiredService<TimeProvider>()));
        }
        else
        {
            // Live: real network only. There is deliberately NO fallback to fixture data on errors.
            jolpica.ConfigurePrimaryHttpMessageHandler(LiveHandler);
            openF1.ConfigurePrimaryHttpMessageHandler(LiveHandler);
            token.ConfigurePrimaryHttpMessageHandler(LiveHandler);
        }

        // One client instance per provider (the OpenF1 session list is cached inside the client).
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<JolpicaClient>(sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(JolpicaHttpClient)));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<OpenF1Client>(sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenF1HttpClient)));
        services.AddSingleton<OpenF1TokenProvider>();

        // Provider-independent from here on: everything else only sees the capability interfaces.
        // Each capability names its provider in configuration (Formula1:Provider:*); today one implementation exists per
        // capability except lifecycle (OpenF1 or None). A new provider is added here without touching anything else.
        services.AddSingleton<IF1ScheduleProvider>(sp => new JolpicaScheduleProvider(sp.GetRequiredService<JolpicaClient>()));
        services.AddSingleton<IF1StandingsProvider>(sp => new JolpicaStandingsProvider(sp.GetRequiredService<JolpicaClient>()));
        services.AddSingleton<IF1ResultsProvider>(sp => ActivatorUtilities.CreateInstance<OpenF1ResultsProvider>(sp));
        services.AddSingleton<IF1LifecycleProvider>(sp => providers.Lifecycle switch
        {
            F1LifecycleProviderName.None => new NoLifecycleProvider(sp.GetRequiredService<TimeProvider>()),
            _ => ActivatorUtilities.CreateInstance<OpenF1LifecycleProvider>(sp),
        });
        services.AddSingleton<IF1LiveTransport>(sp => !mode.IsDemo && providers.Lifecycle == F1LifecycleProviderName.OpenF1
            ? ActivatorUtilities.CreateInstance<OpenF1LiveClient>(sp)
            : new NoLiveTransport());

        services.AddSingleton(sp => F1Sources.From(sp.GetRequiredService<IF1ScheduleProvider>(), sp.GetRequiredService<IF1LifecycleProvider>(),
            sp.GetRequiredService<IF1ResultsProvider>(), sp.GetRequiredService<IF1StandingsProvider>()));
        services.AddSingleton<Formula1Cache>();
        services.AddSingleton<Formula1NotificationRenderer>();
        services.AddSingleton<Formula1LiveListener>();
        services.AddSingleton<Formula1Poller>();
        services.AddScoped<Formula1Workflow>();
        services.AddScoped<Formula1NotificationPlanner>();
        services.AddScoped<Formula1ConfigService>();
        services.AddScoped<Formula1Doctor>();
        services.AddScoped<Formula1PreviewService>();
        services.AddScoped<IDeliveryPolicy, Formula1DeliveryPolicy>();
        services.AddScoped<IModuleLifecycleHandler, Formula1Lifecycle>();
        services.AddScoped<IUserDataContributor, Formula1UserData>();
        services.AddSingleton<IModuleHealthCheck, Formula1HealthCheck>();
        services.AddScoped<IModuleSetupFlow, Formula1SetupFlow>();
    }

    // Long-lived clients: recycle pooled connections so DNS changes are picked up.
    private static HttpMessageHandler LiveHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Background polling / live listener, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<Formula1Poller>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(Formula1Options.Section);
        var errors = new List<string>((section.Get<Formula1Options>() ?? new Formula1Options()).Validate());
        if ((section.GetSection("Jolpica").Get<JolpicaOptions>() ?? new JolpicaOptions()).Problem() is { } jolpica)
            errors.Add(jolpica);
        // Missing OpenF1 credentials are NOT a startup error: live lifecycle is then honestly NOT_CONFIGURED (doctor
        // reports it) while schedule, results and standings keep working.
        if ((section.GetSection("OpenF1").Get<OpenF1Options>() ?? new OpenF1Options()).Problem(requireCredentials: false) is { } openF1)
            errors.Add(openF1);
        return errors;
    }
}

/// <summary>Formula1:Provider:Lifecycle=None — no lifecycle source at all; start notifications are impossible.</summary>
public sealed class NoLifecycleProvider(TimeProvider clock) : IF1LifecycleProvider
{
    public string Id => "none";
    public string AttributionKey => "f1.source.none";
    public bool IsConfigured => false;

    public Task<F1ProviderResult<IReadOnlyList<F1ProviderSession>>> GetSessionsAsync(int season, CancellationToken cancellationToken) =>
        Task.FromResult(F1ProviderResult<IReadOnlyList<F1ProviderSession>>.Fail(F1ProviderOutcome.NotConfigured, "no lifecycle provider", clock.GetUtcNow()));

    public Task<F1ProviderResult<IReadOnlyList<Domain.F1LifecycleEvent>>> GetLifecycleEventsAsync(string providerSessionRef, CancellationToken cancellationToken) =>
        Task.FromResult(F1ProviderResult<IReadOnlyList<Domain.F1LifecycleEvent>>.Fail(F1ProviderOutcome.NotConfigured, "no lifecycle provider", clock.GetUtcNow()));
}
