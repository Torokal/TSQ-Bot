using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Timezone.Application;
using ToroSquad.Modules.Timezone.Commands;

namespace ToroSquad.Modules.Timezone;

/// <summary>
/// TSQ Saat Dönüştürücü: /saat 21:00 reads the time as today in Türkiye (Europe/Istanbul) and shows the same instant in a
/// fixed list of time zones (<see cref="TimeZoneBoard"/>) plus one Discord timestamp for every viewer's own clock. Conversion
/// uses the OS time zone database through <see cref="TimeZoneInfo"/> (IANA ids, DST automatic), never a fixed offset. A
/// stateless utility: no tables, no migration, no background jobs, no HTTP, no configuration, nothing stored or logged about
/// the time asked. Results are public interaction replies. Off by default in every guild (/modules enable timezone), like
/// every other optional module.
/// </summary>
public sealed class TimezoneModule : IToroModule
{
    public const string ModuleIdValue = "timezone";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.timezone.name",
        "module.timezone.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable timezone
        RequiredBotChannelPermissions: GuildPermission.None, // interaction replies only; the bot never posts on its own
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: []);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(TimezoneCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new LocalizationSource(typeof(TimezoneModule).Assembly, "ToroSquad.Modules.Timezone.Localization"));
        services.AddSingleton<TimezoneCards>();
    }

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
}
