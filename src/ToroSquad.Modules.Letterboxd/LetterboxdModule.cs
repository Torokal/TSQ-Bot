using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Admin;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Letterboxd.Application;
using ToroSquad.Modules.Letterboxd.Commands;
using ToroSquad.Modules.Letterboxd.Persistence;
using ToroSquad.Modules.Letterboxd.Providers;

namespace ToroSquad.Modules.Letterboxd;

/// <summary>
/// TSQ Letterboxd: new reviews, rated diary entries and rewatches of ONE followed Letterboxd member (Letterboxd:Username),
/// read from the member's official public RSS feed and posted once to one configured channel; a later change of a posted
/// entry edits its card (docs/letterboxd/TSQ_LETTERBOXD.md). Standalone star ratings (given without logging an entry) are not
/// in that feed and no permitted source exists for them: the coverage is partial and status/doctor say so. A separate
/// feature module: depends only on the shared TSQ layers. Inert unless Letterboxd:Mode is DryRun or Live; also gated per
/// guild like every optional module (/modules enable letterboxd) and needs a channel (/tsq-admin modul:letterboxd islem:configure).
/// </summary>
public sealed class LetterboxdModule : IToroModule
{
    public const string ModuleIdValue = "letterboxd";

    /// <summary>The <c>modul</c> value of this module's operations in <c>/tsq-admin</c>.</summary>
    public const string AdminId = "letterboxd";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.letterboxd.name",
        "module.letterboxd.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable letterboxd (after /tsq-admin modul:letterboxd islem:configure)
        RequiredBotChannelPermissions: LetterboxdConfigService.RequiredChannelPermissions,
        OptionalBotPermissions: GuildPermission.ReadMessageHistory, // reconciling an uncertain send
        AdminCommands: [AdminCatalog.Entry(AdminId)]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAdminOperations(LetterboxdAdminOperations.Definition);
        services.AddOptions<LetterboxdOptions>().Bind(configuration.GetSection(LetterboxdOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(LetterboxdModule).Assembly, "ToroSquad.Modules.Letterboxd.Localization"));
        services.AddSingleton<IModelContributor, LetterboxdModelContributor>();

        services.AddHttpClient(LetterboxdRssClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(LetterboxdRssClient.CreateHandler);
        services.AddSingleton<LetterboxdRssClient>();

        services.AddSingleton<LetterboxdCardRenderer>();
        services.AddSingleton<LetterboxdPoller>();
        services.AddScoped<LetterboxdPlanner>();
        services.AddScoped<LetterboxdConfigService>();
        services.AddScoped<IDeliveryPolicy, LetterboxdDeliveryPolicy>();
        services.AddScoped<IModuleLifecycleHandler, LetterboxdLifecycle>();
        services.AddScoped<IUserDataContributor, LetterboxdUserData>();
        services.AddSingleton<IModuleHealthCheck, LetterboxdHealthCheck>();
    }

    /// <summary>The feed loop, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<LetterboxdPoller>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) =>
        (configuration.GetSection(LetterboxdOptions.Section).Get<LetterboxdOptions>() ?? new LetterboxdOptions()).Validate();
}
