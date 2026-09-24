using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Commands.Core;

namespace ToroSquad.Discord;

/// <summary>
/// The always-on core module: /help, /bot, /privacy, /setup, /modules. It cannot be disabled, so these keep working
/// when every feature module is off.
/// </summary>
public sealed class CoreBotModule : IToroModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        ModuleId.Core,
        new Version(0, 1, 0),
        "module.core.name",
        "module.core.description",
        IsCore: true,
        EnabledByDefault: true,
        RequiredBotChannelPermissions: GuildPermission.None,
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: ["setup", "modules"]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } =
    [
        typeof(HelpCommands),
        typeof(BotCommands),
        typeof(PrivacyCommands),
        typeof(ModulesCommands),
        typeof(SetupCommands),
    ];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new LocalizationSource(typeof(CoreBotModule).Assembly, "ToroSquad.Discord.Localization"));

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
}
