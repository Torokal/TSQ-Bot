using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Interactions;
using GuildPermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Modules.Example;

/// <summary>
/// Minimal reference module used by docs/ADDING_A_MODULE.md and the extensibility tests. It is NOT registered in
/// production (composition root registers it only when Modules:Example:Enabled=true) and, even when registered,
/// it is disabled per guild until an admin enables it.
/// </summary>
public sealed class ExampleModule : IToroModule
{
    public const string ModuleIdValue = "example";

    public ModuleDescriptor Descriptor { get; } = new(
        new ModuleId(ModuleIdValue),
        new Version(0, 1, 0),
        "module.example.name",
        "module.example.description",
        IsCore: false,
        EnabledByDefault: false,
        RequiredBotChannelPermissions: GuildPermission.None,
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: []);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(ExampleCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new LocalizationSource(typeof(ExampleModule).Assembly, "ToroSquad.Modules.Example.Localization"));

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
}

/// <summary>/example ping — replies with the module version. Gated by the module toggle like every module.</summary>
[ToroModule(ExampleModule.ModuleIdValue)]
[Group("example", "Example module (development only)")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class ExampleCommands(InteractionServices services) : ToroInteractionModule(services)
{
    [SlashCommand("ping", "Check that the example module answers")]
    public Task PingAsync() => ReplyTextAsync("example.pong");
}
