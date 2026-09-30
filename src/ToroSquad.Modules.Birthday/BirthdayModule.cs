using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Admin;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Application;
using ToroSquad.Modules.Birthday.Commands;
using ToroSquad.Modules.Birthday.Persistence;

namespace ToroSquad.Modules.Birthday;

/// <summary>
/// TSQ Birthday (Doğum Günü): members save their own birthday as day + month (/birthday set 14.03 — never a year). When the
/// day starts in the configured time zone (Europe/Istanbul) the bot posts one announcement for everyone celebrating that
/// day and gives them the birthday role until the next day starts (docs/birthday/TSQ_BIRTHDAY.md). Restart-safe by
/// reconciliation, not by a midnight timer. A separate feature module: depends only on the shared TSQ layers. Off by
/// default in every guild (/modules enable birthday).
/// </summary>
public sealed class BirthdayModule : IToroModule
{
    public const string ModuleIdValue = "birthday";

    /// <summary>The <c>modul</c> value of this module's operations in <c>/tsq-admin</c> (the former <c>/birthday-admin</c>).</summary>
    public const string AdminId = "birthday";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.birthday.name",
        "module.birthday.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable birthday (after /tsq-admin modul:birthday islem:configure)
        RequiredBotChannelPermissions: BirthdayConfigService.RequiredChannelPermissions, // a plain text message
        OptionalBotPermissions: GuildPermission.ManageRoles, // the temporary birthday role
        AdminCommands: [AdminCatalog.Entry(AdminId)]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(BirthdayCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAdminOperations(BirthdayAdminOperations.Definition);
        services.AddOptions<BirthdayOptions>().Bind(configuration.GetSection(BirthdayOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(BirthdayModule).Assembly, "ToroSquad.Modules.Birthday.Localization"));
        services.AddSingleton<IModelContributor, BirthdayModelContributor>();

        services.AddSingleton<BirthdayHealth>();
        services.AddSingleton<BirthdayAnnouncementRenderer>();
        services.AddSingleton<BirthdayReconciler>();
        services.AddSingleton<BirthdayWorker>();
        services.AddScoped<BirthdayService>();
        services.AddScoped<BirthdayConfigService>();
        services.AddScoped<BirthdayDoctor>();
        services.AddScoped<IDeliveryPolicy, BirthdayDeliveryPolicy>();
        services.AddScoped<IUserDataContributor, BirthdayUserData>();
        services.AddSingleton<IModuleHealthCheck, BirthdayHealthCheck>();
    }

    /// <summary>Reconciliation loop, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<BirthdayWorker>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) =>
        (configuration.GetSection(BirthdayOptions.Section).Get<BirthdayOptions>() ?? new BirthdayOptions()).Validate();
}
