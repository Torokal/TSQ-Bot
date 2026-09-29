using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Discord;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Giveaway.Application;
using ToroSquad.Modules.Giveaway.Commands;
using ToroSquad.Modules.Giveaway.Domain;
using ToroSquad.Modules.Giveaway.Persistence;

namespace ToroSquad.Modules.Giveaway;

/// <summary>
/// TSQ Çekiliş (Giveaway): /giveaway create opens a form (prize, duration, winners, description); the bot posts the card in
/// that channel and adds 🎉; members enter with Discord's own 🎉 reaction; when the time is up the worker reads the
/// reactions, draws the winners and turns the same card into the result, with one short message that pings only the winners
/// (docs/giveaway/TSQ_GIVEAWAY.md). /giveaway end|cancel|reroll for admins. Restart-safe: giveaways are rows, one loop draws
/// every due one. A separate feature module: depends only on the shared TSQ layers. Off by default in every guild
/// (/modules enable giveaway).
/// </summary>
public sealed class GiveawayModule : IToroModule
{
    public const string ModuleIdValue = "giveaway";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.giveaway.name",
        "module.giveaway.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable giveaway
        RequiredBotChannelPermissions: GiveawayRules.RequiredChannelPermissions, // card, 🎉, reading the reactions, later edits
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: ["giveaway"]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(GiveawayCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new LocalizationSource(typeof(GiveawayModule).Assembly, "ToroSquad.Modules.Giveaway.Localization"));
        services.AddSingleton<IModelContributor, GiveawayModelContributor>();

        // Transport mode decides real vs offline reactions, like the Discord boundary itself — never both.
        var discord = configuration.GetSection(DiscordOptions.Section).Get<DiscordOptions>() ?? new DiscordOptions();
        if (discord.Transport == DiscordTransportMode.Gateway)
        {
            services.AddSingleton<IGiveawayReactions, DiscordGiveawayReactions>();
        }
        else
        {
            services.AddSingleton<OfflineGiveawayReactions>();
            services.AddSingleton<IGiveawayReactions>(sp => sp.GetRequiredService<OfflineGiveawayReactions>());
        }

        services.AddSingleton<IGiveawayRandom, SecureGiveawayRandom>();
        services.AddSingleton<GiveawayCards>();
        services.AddSingleton<GiveawayAnnouncementRenderer>();
        services.AddSingleton<GiveawayWorker>();
        services.AddScoped<GiveawayService>();
        services.AddScoped<GiveawayCardSync>();
        services.AddScoped<IUserDataContributor, GiveawayUserData>();
        services.AddScoped<IDeliveryPolicy, GiveawayDeliveryPolicy>();
    }

    /// <summary>Draw + card loop, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<GiveawayWorker>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
}
