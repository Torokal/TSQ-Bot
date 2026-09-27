using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Commands;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg;

/// <summary>
/// TSQ LFG — Oyuncu Bul: /ekip opens a form for a group-finder listing for ANY game or activity (free-text game name and
/// details, team size, start, duration); the owner can edit it later (✏️ Düzenle); members join/leave with buttons, the owner
/// or a moderator closes it, and it expires on
/// its own (docs/lfg/TSQ_LFG.md). One generic lifecycle — there is no game-specific model, handler or field, and a new game
/// never needs a code change. A separate feature module: depends only on the shared TSQ layers. Off by default in every
/// guild (/modules enable lfg).
/// </summary>
public sealed class LfgModule : IToroModule
{
    public const string ModuleIdValue = "lfg";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    /// <summary>
    /// Read Message History: notice a card deleted in Discord early. Move Members (+ Connect on the voice channel): move a
    /// member who is already in voice with one click. Without them those features fall back gracefully.
    /// </summary>
    public const GuildPermission OptionalPermissions = GuildPermission.ReadMessageHistory | GuildPermission.MoveMembers | GuildPermission.Connect;

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.lfg.name",
        "module.lfg.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable lfg
        RequiredBotChannelPermissions: GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks, // the bot edits its cards on expiry/close
        OptionalBotPermissions: OptionalPermissions,
        AdminCommands: ["lfg-admin"]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(LfgFormCommands), typeof(LfgCommands), typeof(LfgAdminCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LfgOptions>().Bind(configuration.GetSection(LfgOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(LfgModule).Assembly, "ToroSquad.Modules.Lfg.Localization"));
        services.AddSingleton<IModelContributor, LfgModelContributor>();

        services.AddSingleton<LfgCardRenderer>();
        services.AddSingleton<LfgNoticeRenderer>();
        services.AddSingleton<LfgExpiryWorker>();
        services.AddScoped<LfgService>();
        services.AddSingleton<LfgFormDrafts>();
        services.AddScoped<LfgCardSync>();
        services.AddScoped<LfgNoticePlanner>();
        services.AddScoped<LfgConfigService>();
        services.AddScoped<IUserDataContributor, LfgUserData>();
    }

    /// <summary>Expiry + card recovery loop, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<LfgExpiryWorker>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) =>
        (configuration.GetSection(LfgOptions.Section).Get<LfgOptions>() ?? new LfgOptions()).Validate();
}
