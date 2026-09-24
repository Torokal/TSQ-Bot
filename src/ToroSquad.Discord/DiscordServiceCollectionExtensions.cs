using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Roles;
using ToroSquad.Discord.Commands.Core;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Guilds;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;

namespace ToroSquad.Discord;

public static class DiscordServiceCollectionExtensions
{
    /// <summary>Discord boundary services. Transport mode decides real vs fake implementations — never both.</summary>
    public static IServiceCollection AddToroDiscord(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(DiscordOptions.Section);
        services.AddOptions<DiscordOptions>().Bind(section);
        var options = section.Get<DiscordOptions>() ?? new DiscordOptions();

        services.AddScoped<InteractionServices>();
        services.AddSingleton<InteractionHost>();
        services.AddScoped<CommandSyncService>();

        if (options.Transport == DiscordTransportMode.Gateway)
        {
            services.AddSingleton(_ => new DiscordSocketClient(GatewayBotService.CreateSocketConfig()));
            services.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>(), InteractionHost.CreateConfig()));
            services.AddSingleton<IMessageTransport, DiscordMessageTransport>();
            services.AddSingleton<IGuildGateway, DiscordGuildGateway>();
            services.AddSingleton<IBotConnectionStatus, GatewayConnectionStatus>();
        }
        else
        {
            // Not logged in: enough to load modules and build the manifest offline.
            services.AddSingleton(_ => new InteractionService(new DiscordRestClient(), InteractionHost.CreateConfig()));
            services.AddSingleton<FakeMessageTransport>();
            services.AddSingleton<IMessageTransport>(sp => sp.GetRequiredService<FakeMessageTransport>());
            services.AddSingleton<FakeGuildGateway>();
            services.AddSingleton<IGuildGateway>(sp => sp.GetRequiredService<FakeGuildGateway>());
            services.AddSingleton<IBotConnectionStatus, FakeConnectionStatus>();
        }

        return services;
    }

    /// <summary>Long-running Discord host services (not used by one-shot CLI verbs).</summary>
    public static IServiceCollection AddToroDiscordHosting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DiscordOptions.Section).Get<DiscordOptions>() ?? new DiscordOptions();
        if (options.Transport == DiscordTransportMode.Gateway)
            services.AddHostedService<GatewayBotService>();
        else
            services.AddHostedService<OfflineInteractionService>();
        return services;
    }
}
