using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Commands;
using ToroSquad.Modules.News.Persistence;
using ToroSquad.Modules.News.Providers;

namespace ToroSquad.Modules.News;

/// <summary>
/// TSQ News: HLTV news about Aurora's main CS2 team, read from the official HLTV RSS feed only (headline + link, never the
/// article or its image) and posted once to one configured channel (docs/news/TSQ_NEWS.md). A separate feature module:
/// depends only on the shared TSQ layers. Inert unless News:Mode is DryRun or Live; also gated per guild like every
/// optional module (/modules enable news) and needs a channel (/news-admin configure).
/// </summary>
public sealed class NewsModule : IToroModule
{
    public const string ModuleIdValue = "news";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.news.name",
        "module.news.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable news (after /news-admin configure)
        RequiredBotChannelPermissions: NewsConfigService.RequiredChannelPermissions,
        OptionalBotPermissions: GuildPermission.ReadMessageHistory, // reconciling an uncertain send
        AdminCommands: ["news-admin"]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(NewsAdminCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NewsOptions>().Bind(configuration.GetSection(NewsOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(NewsModule).Assembly, "ToroSquad.Modules.News.Localization"));
        services.AddSingleton<IModelContributor, NewsModelContributor>();

        services.AddHttpClient(HltvRssClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(HltvRssClient.CreateHandler);
        services.AddHttpClient(LiquipediaRosterClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(HltvRssClient.CreateHandler);
        services.AddSingleton<HltvRssClient>();
        services.AddSingleton<LiquipediaRosterClient>();

        services.AddSingleton<NewsCardRenderer>();
        services.AddSingleton<NewsPoller>();
        services.AddScoped<NewsPlanner>();
        services.AddScoped<NewsConfigService>();
        services.AddScoped<IDeliveryPolicy, NewsDeliveryPolicy>();
        services.AddSingleton<IModuleHealthCheck, NewsHealthCheck>();
    }

    /// <summary>The feed loop, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<NewsPoller>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) =>
        (configuration.GetSection(NewsOptions.Section).Get<NewsOptions>() ?? new NewsOptions()).Validate();
}
