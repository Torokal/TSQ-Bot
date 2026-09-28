using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Randomizer.Application;
using ToroSquad.Modules.Randomizer.Commands;

namespace ToroSquad.Modules.Randomizer;

/// <summary>
/// TSQ Randomizer: /zarat (dice), /randomsayi (a number from a range), /sec (one of the given options) and /yazitura (coin
/// flip) (docs/randomizer/TSQ_RANDOMIZER.md). Every draw comes from one cryptographically secure source
/// (<see cref="SecureRandomSource"/>). A stateless utility: no tables, no migration, no background jobs, no HTTP, no API key,
/// no AI, no configuration, nothing stored or logged about a result. Results are public interaction replies (no channel
/// permission needed beyond what Discord grants an interaction). A separate feature module: depends only on the shared TSQ
/// layers. Off by default in every guild (/modules enable randomizer), like every other optional module.
/// </summary>
public sealed class RandomizerModule : IToroModule
{
    public const string ModuleIdValue = "randomizer";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.randomizer.name",
        "module.randomizer.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable randomizer
        RequiredBotChannelPermissions: GuildPermission.None, // interaction replies only; the bot never posts on its own
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: []);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(RandomizerCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new LocalizationSource(typeof(RandomizerModule).Assembly, "ToroSquad.Modules.Randomizer.Localization"));
        services.AddSingleton<IRandomSource, SecureRandomSource>();
        services.AddSingleton<RandomizerService>();
        services.AddSingleton<RandomizerCards>();
    }

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
}
