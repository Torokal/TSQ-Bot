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
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Commands;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Persistence;
using ToroSquad.Modules.Volleyball.Providers;
using ToroSquad.Modules.Volleyball.Providers.Fivb;
using ToroSquad.Modules.Volleyball.Providers.Fixtures;

namespace ToroSquad.Modules.Volleyball;

/// <summary>
/// Volleyball: notifications for ONE team only — Türkiye women's senior national team ("Filenin Sultanları"). A separate
/// feature module: depends only on the shared TSQ layers, never on another feature module. Off by default in every guild.
/// </summary>
public sealed class VolleyballModule : IToroModule
{
    public const string ModuleIdValue = "volleyball";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public const string FivbHttpClient = "vb-fivb";

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.volleyball.name",
        "module.volleyball.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation after setup + ping-free preview
        RequiredBotChannelPermissions: GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks,
        OptionalBotPermissions: GuildPermission.MentionEveryone,
        AdminCommands: ["volleyball-admin"]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } =
    [
        typeof(VolleyballCommands),
        typeof(VolleyballAdminCommands),
        typeof(VolleyballSetupComponents),
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(VolleyballOptions.Section);
        services.AddOptions<VolleyballOptions>().Bind(section);
        services.AddOptions<FivbVisOptions>().Bind(section.GetSection("Fivb"));
        var providers = section.GetSection("Provider").Get<VolleyballOptions.ProviderSection>() ?? new VolleyballOptions.ProviderSection();
        var mode = new VbDataMode(providers.Mode);
        services.AddSingleton(mode);

        services.AddSingleton(new LocalizationSource(typeof(VolleyballModule).Assembly, "ToroSquad.Modules.Volleyball.Localization"));
        services.AddSingleton<IModelContributor, VolleyballModelContributor>();
        services.AddSingleton<VbRequestBudget>();

        // The one followed team. Explicit, structured identity; provider team ids only where a provider has stable ones
        // (FIVB VIS team numbers are per-tournament registrations, so none are configured by default).
        var teamIds = section.GetSection("ProviderTeamIds").Get<string[]>() ?? [];
        services.AddSingleton(TrackedTeamIdentity.TurkeyWomenSenior(teamIds));

        var fivbBase = section.GetSection("Fivb").GetValue("BaseUrl", new FivbVisOptions().BaseUrl)!;
        var fivb = services.AddHttpClient(FivbHttpClient, c => c.BaseAddress = new Uri(fivbBase));
        if (mode.IsDemo)
        {
            // Explicit fixture mode: synthetic TEST/DEMO data through the real VIS client and parser.
            services.AddSingleton<VbFixtureAnchor>();
            fivb.ConfigurePrimaryHttpMessageHandler(sp => new VbFixtureHttpHandler(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<VbFixtureAnchor>()));
        }
        else
        {
            // Live: real network only. There is deliberately NO fallback to fixture data on errors.
            fivb.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });
        }

        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<FivbVisClient>(sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(FivbHttpClient)));
        services.AddSingleton<FivbVisProvider>();

        // Provider-independent from here on: everything else only sees IVolleyballDataProvider.
        services.AddSingleton<IVolleyballDataProvider>(sp => providers.Name switch
        {
            VbProviderName.None => new NoVolleyballProvider(sp.GetRequiredService<TimeProvider>()),
            _ => sp.GetRequiredService<FivbVisProvider>(),
        });
        services.AddSingleton(sp => VbSources.From(sp.GetRequiredService<IVolleyballDataProvider>()));
        // No provider supplies logos today: flags (plain text) only. A provider with licensed logos adds its image host here.
        services.AddSingleton(new VbLogoHosts([]));
        services.AddSingleton<VolleyballCache>();
        services.AddSingleton<VolleyballNotificationRenderer>();
        services.AddSingleton<VolleyballPoller>();
        services.AddScoped<VolleyballWorkflow>();
        services.AddScoped<VolleyballNotificationPlanner>();
        services.AddScoped<VolleyballConfigService>();
        services.AddScoped<VolleyballDoctor>();
        services.AddScoped<VolleyballPreviewService>();
        services.AddScoped<IDeliveryPolicy, VolleyballDeliveryPolicy>();
        services.AddScoped<IModuleLifecycleHandler, VolleyballLifecycle>();
        services.AddScoped<IUserDataContributor, VolleyballUserData>();
        services.AddSingleton<IModuleHealthCheck, VolleyballHealthCheck>();
        services.AddScoped<IModuleSetupFlow, VolleyballSetupFlow>();
    }

    /// <summary>Background polling, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<VolleyballPoller>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(VolleyballOptions.Section);
        var errors = new List<string>((section.Get<VolleyballOptions>() ?? new VolleyballOptions()).Validate());
        if ((section.GetSection("Fivb").Get<FivbVisOptions>() ?? new FivbVisOptions()).Problem() is { } fivb)
            errors.Add(fivb);
        return errors;
    }
}
