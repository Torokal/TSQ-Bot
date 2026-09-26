using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Discord;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Application;
using ToroSquad.Modules.Live.Commands;
using ToroSquad.Modules.Live.Persistence;
using ToroSquad.Modules.Live.Providers;
using ToroSquad.Modules.Live.Providers.Kick;
using ToroSquad.Modules.Live.Providers.Twitch;

namespace ToroSquad.Modules.Live;

/// <summary>
/// TSQ Live: Twitch + Kick live-stream announcements for the configured creators — one @everyone per new creator session,
/// one message per session (multistream, title changes, platform changes and the end of the stream edit it), restart and
/// flap safe (docs/live/TSQ_LIVE.md). A separate feature module: depends only on the shared TSQ layers. Inert unless
/// Live:Enabled=true; also gated per guild like every optional module (/modules enable live).
/// </summary>
public sealed class LiveModule : IToroModule
{
    public const string ModuleIdValue = "live";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.live.name",
        "module.live.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable live (after Live:* is configured)
        RequiredBotChannelPermissions: GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks,
        OptionalBotPermissions: GuildPermission.MentionEveryone | GuildPermission.ReadMessageHistory,
        AdminCommands: ["live-admin"]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(LiveAdminCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(LiveOptions.Section);
        services.AddOptions<LiveOptions>().Bind(section);
        services.AddOptions<TwitchOptions>().Bind(section.GetSection("Twitch"));
        services.AddOptions<KickOptions>().Bind(section.GetSection("Kick"));

        services.AddSingleton(new LocalizationSource(typeof(LiveModule).Assembly, "ToroSquad.Modules.Live.Localization"));
        services.AddSingleton<IModelContributor, LiveModelContributor>();

        // Official APIs only; real network only (there is no fixture/demo mode for live streams).
        foreach (var name in new[] { TwitchStatusProvider.HttpClientName, TwitchStatusProvider.AuthHttpClientName, KickStatusProvider.HttpClientName, KickStatusProvider.AuthHttpClientName })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });
        }

        services.AddSingleton<TwitchStatusProvider>();
        services.AddSingleton<KickStatusProvider>();
        services.AddSingleton<ILiveStatusProvider>(sp => sp.GetRequiredService<TwitchStatusProvider>());
        services.AddSingleton<ILiveStatusProvider>(sp => sp.GetRequiredService<KickStatusProvider>());

        services.AddSingleton<LiveHealth>();
        services.AddSingleton<LiveCardRenderer>();
        services.AddSingleton<LiveCoordinator>();
        services.AddSingleton<LivePoller>();
        services.AddScoped<LiveAnnouncementPlanner>();
        services.AddScoped<LiveDoctor>();
        services.AddScoped<IDeliveryPolicy, LiveDeliveryPolicy>();
        services.AddScoped<IUserDataContributor, LiveUserData>();
        services.AddSingleton<IModuleHealthCheck, LiveHealthCheck>();
    }

    /// <summary>Reconciliation loop, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<LivePoller>());

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(LiveOptions.Section);
        var allowed = configuration.GetSection(DiscordOptions.Section).Get<DiscordOptions>()?.AllowedGuildIds ?? [];
        var errors = new List<string>((section.Get<LiveOptions>() ?? new LiveOptions()).Validate(allowed));
        if ((section.GetSection("Twitch").Get<TwitchOptions>() ?? new TwitchOptions()).Problem() is { } twitch)
            errors.Add(twitch);
        if ((section.GetSection("Kick").Get<KickOptions>() ?? new KickOptions()).Problem() is { } kick)
            errors.Add(kick);
        return errors;
    }
}
