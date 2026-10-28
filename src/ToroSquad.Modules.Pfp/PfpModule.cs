using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Pfp.Application;
using ToroSquad.Modules.Pfp.Commands;

namespace ToroSquad.Modules.Pfp;

/// <summary>
/// TSQ Profil Görseli: /pfp shows a member's profile picture and /banner their profile banner, large, in one public card with
/// a "Tam boy aç" link (docs/pfp/TSQ_PFP.md). <c>tur</c> picks the global profile, this server's own member profile, or
/// automatic (this server's first). Pictures are shown straight from Discord's CDN — nothing is downloaded, processed,
/// generated, cached or stored by the bot. Stateless: no tables, no migration, no background jobs, no configuration, no new
/// intent, no package. Off by default in every guild (/modules enable pfp), like every other optional module.
/// </summary>
public sealed class PfpModule : IToroModule
{
    public const string ModuleIdValue = "pfp";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.pfp.name",
        "module.pfp.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable pfp
        RequiredBotChannelPermissions: GuildPermission.None, // interaction replies only; the bot never posts on its own
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: []);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(PfpCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new LocalizationSource(typeof(PfpModule).Assembly, "ToroSquad.Modules.Pfp.Localization"));
        services.AddSingleton<PfpCooldown>();
        services.AddSingleton<PfpCards>();
        services.AddSingleton<PfpResolver>();
        services.AddSingleton<PfpCommandFlow>();
    }

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
}
