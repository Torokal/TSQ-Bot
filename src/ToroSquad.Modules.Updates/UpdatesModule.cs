using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Admin;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Commands;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Domain.Games;
using ToroSquad.Modules.Updates.Persistence;
using ToroSquad.Modules.Updates.Providers;

namespace ToroSquad.Modules.Updates;

/// <summary>
/// TSQ Bot Updates: one card when a followed game publishes an official update / patch notes post
/// (docs/updates/TSQ_UPDATES.md). Generic by construction: a game is a registered <see cref="GameUpdateDefinition"/>
/// (provider + provider game id + classifier) and a provider is an <see cref="IGameUpdateProvider"/>. Registered today:
/// Counter-Strike 2 on Steam, and World of Warcraft: Forever through Blizzard's posts on the official forum. A separate feature module that depends only on the shared TSQ layers. Inert unless
/// Updates:Mode is DryRun or Live; also gated per guild like every optional module (/modules enable updates), needs a
/// channel (/tsq-admin modul:updates islem:configure) and an explicitly enabled game (islem:game-enable).
/// </summary>
public sealed class UpdatesModule : IToroModule
{
    public const string ModuleIdValue = "updates";

    /// <summary>The <c>modul</c> value of this module's operations in <c>/tsq-admin</c>.</summary>
    public const string AdminId = "updates";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.updates.name",
        "module.updates.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable updates
        RequiredBotChannelPermissions: UpdatesConfigService.RequiredChannelPermissions,
        OptionalBotPermissions: GuildPermission.ReadMessageHistory, // reconciling an uncertain send
        AdminCommands: [AdminCatalog.Entry(AdminId)]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAdminOperations(UpdatesAdminOperations.Definition);
        services.AddOptions<UpdatesOptions>().Bind(configuration.GetSection(UpdatesOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(UpdatesModule).Assembly, "ToroSquad.Modules.Updates.Localization"));
        services.AddSingleton<IModelContributor, UpdatesModelContributor>();

        // Games and providers: the only place a game or a provider is added.
        services.AddSingleton<ProviderHttp>();
        services.AddSingleton(Cs2Game.Definition);
        services.AddHttpClient(SteamNewsUpdateProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(ProviderHttp.CreateHandler);
        services.AddSingleton<IGameUpdateProvider, SteamNewsUpdateProvider>();

        services.AddOptions<WowForeverSettings>().Bind(configuration.GetSection(WowForeverSettings.Section));
        services.AddOptions<BlizzardForumOptions>().Bind(configuration.GetSection(BlizzardForumOptions.Section));
        services.AddSingleton(sp => WowForeverGame.Create(sp.GetRequiredService<IOptions<WowForeverSettings>>().Value));
        services.AddHttpClient(BlizzardForumUpdateProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(ProviderHttp.CreateHandler);
        services.AddSingleton<IGameUpdateProvider, BlizzardForumUpdateProvider>();

        services.AddSingleton<GameUpdateCatalog>();

        services.AddSingleton<UpdateCardRenderer>();
        services.AddSingleton<UpdatesPoller>();
        services.AddScoped<UpdatesPlanner>();
        services.AddScoped<UpdatesConfigService>();
        services.AddScoped<IDeliveryPolicy, UpdatesDeliveryPolicy>();
        services.AddScoped<IModuleLifecycleHandler, UpdatesLifecycle>();
        services.AddScoped<IUserDataContributor, UpdatesUserData>();
        services.AddSingleton<IModuleHealthCheck, UpdatesHealthCheck>();
    }

    /// <summary>The polling loop, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<UpdatesPoller>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) =>
    [
        .. (configuration.GetSection(UpdatesOptions.Section).Get<UpdatesOptions>() ?? new UpdatesOptions()).Validate(),
        .. (configuration.GetSection(WowForeverSettings.Section).Get<WowForeverSettings>() ?? new WowForeverSettings()).Validate(),
        .. (configuration.GetSection(BlizzardForumOptions.Section).Get<BlizzardForumOptions>() ?? new BlizzardForumOptions()).Validate(),
    ];
}
