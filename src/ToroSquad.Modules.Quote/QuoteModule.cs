using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Quote.Application;
using ToroSquad.Modules.Quote.Commands;

namespace ToroSquad.Modules.Quote;

/// <summary>
/// TSQ Quote: /quote turns one message of this server into a black-and-white quote card (docs/quote/TSQ_QUOTE.md). A
/// stateless utility: no tables, no background jobs, no provider, no configuration. A separate feature module: depends
/// only on the shared TSQ layers. Off by default in every guild (/modules enable quote).
/// </summary>
public sealed class QuoteModule : IToroModule
{
    public const string ModuleIdValue = "quote";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.quote.name",
        "module.quote.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable quote
        RequiredBotChannelPermissions: QuoteMessageResolver.RequiredToRead, // in the SOURCE channel, checked before every quote
        OptionalBotPermissions: GuildPermission.AttachFiles, // the card is a follow-up with a file (servers may restrict files)
        AdminCommands: []);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(QuoteCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new LocalizationSource(typeof(QuoteModule).Assembly, "ToroSquad.Modules.Quote.Localization"));

        // Discord CDN only (QuoteAvatarClient checks the url): no redirects, no cookies, a short connect timeout.
        services.AddHttpClient(QuoteAvatarClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });

        services.AddSingleton(_ => QuoteFonts.Load());
        services.AddSingleton<QuoteImageRenderer>();
        services.AddSingleton<QuoteAvatarClient>();
        services.AddSingleton<QuoteMessageResolver>();
    }

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) => [];
}
