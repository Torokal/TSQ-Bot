using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Agenda.Application;
using ToroSquad.Modules.Agenda.Commands;

namespace ToroSquad.Modules.Agenda;

/// <summary>
/// TSQ Ajanda: /ajanda (F1, CS2 and volleyball programme for 1–14 local days) and /gundem (today's rest and tomorrow:
/// sports and LFG), docs/agenda/TSQ_AGENDA.md. A VIEW over what the source modules already hold: each source module
/// registers its own <see cref="ToroSquad.Core.Agenda.IAgendaSource"/> (this module references none of them). No tables, no
/// migration, no background job, no provider call, no notification, no configuration. Off by default in every guild
/// (/modules enable agenda); a source module that is off is not read.
/// </summary>
public sealed class AgendaModule : IToroModule
{
    public const string ModuleIdValue = "agenda";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.agenda.name",
        "module.agenda.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable agenda
        RequiredBotChannelPermissions: GuildPermission.None, // interaction replies only; the bot never posts on its own
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: []);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(AgendaCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new LocalizationSource(typeof(AgendaModule).Assembly, "ToroSquad.Modules.Agenda.Localization"));
        services.AddSingleton<AgendaRenderer>();
        services.AddSingleton<AgendaCooldown>();
        services.AddScoped<AgendaService>();
        services.AddScoped<AgendaFlow>();
    }

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
}
