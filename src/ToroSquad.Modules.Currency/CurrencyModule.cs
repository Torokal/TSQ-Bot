using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Commands;
using ToroSquad.Modules.Currency.Providers;

namespace ToroSquad.Modules.Currency;

/// <summary>
/// TSQ Döviz &amp; Altın: /dolar, /euro and /altın show the current USD/TRY, EUR/TRY and gram gold buy/sell prices
/// (docs/currency/TSQ_CURRENCY.md). Primary provider Altınkaynak; fallbacks TCMB (indicative rates, USD/EUR) and Trunçgil
/// (gram gold); then the last good price for up to 15 minutes, clearly marked. The commands answer only in the currency
/// channel (<see cref="CurrencyOptions.ChannelId"/>); a combined card is posted there every day at 09:00 Türkiye time
/// through the shared outbox. Prices are fetched when needed and cached in memory: no own tables, no migration, no API
/// keys, no AI. A separate feature module: depends only on the shared TSQ layers. Off by default in every guild
/// (/modules enable currency) — and without it, no daily card.
/// </summary>
public sealed class CurrencyModule : IToroModule
{
    public const string ModuleIdValue = "currency";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    /// <summary>Sent to the providers so they can tell who is asking (no contact data, no secret).</summary>
    public const string UserAgent = "TSQBot (+https://github.com/Torokal/TSQ-Bot)";

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.currency.name",
        "module.currency.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable currency
        RequiredBotChannelPermissions: GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks, // the daily card
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: []);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(CurrencyCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CurrencyOptions>().Bind(configuration.GetSection(CurrencyOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(CurrencyModule).Assembly, "ToroSquad.Modules.Currency.Localization"));

        AddProviderClient(services, MarketDataClient.AltinkaynakHttpClient, o => o.AltinkaynakBaseUrl);
        AddProviderClient(services, MarketDataClient.TcmbHttpClient, o => o.TcmbBaseUrl);
        AddProviderClient(services, MarketDataClient.TruncgilHttpClient, o => o.TruncgilBaseUrl);

        services.AddSingleton<IMarketDataSource, MarketDataClient>();
        services.AddSingleton<MarketQuoteService>();
        services.AddSingleton<CurrencyCardRenderer>();
        services.AddSingleton<CurrencyCommandFlow>();
        services.AddSingleton<CurrencyDailyPoster>();
        services.AddSingleton<IModuleHealthCheck, CurrencyHealthCheck>();
    }

    /// <summary>
    /// One named client per provider: fixed base URL from configuration, no redirects (a moved endpoint is a visible
    /// failure, not a silent fetch elsewhere), no cookies, bounded response size; the per-request timeout is applied by
    /// <see cref="MarketDataClient"/> on the injected clock.
    /// </summary>
    private static void AddProviderClient(IServiceCollection services, string name, Func<CurrencyOptions, string> baseUrl) =>
        services.AddHttpClient(name, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<CurrencyOptions>>().Value;
                client.BaseAddress = new Uri(baseUrl(options));
                client.Timeout = options.Timeout + TimeSpan.FromSeconds(5); // backstop only
                client.MaxResponseContentBufferSize = MarketDataClient.MaxResponseBytes;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = sp.GetRequiredService<IOptions<CurrencyOptions>>().Value.Timeout,
            });

    /// <summary>The daily 09:00 card, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) => services.AddHostedService<CurrencyDailyWorker>();

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration)
    {
        var errors = new List<string>((configuration.GetSection(CurrencyOptions.Section).Get<CurrencyOptions>() ?? new CurrencyOptions()).Validate());
        if (!ProviderFormats.TryTurkeyZone(out _))
            errors.Add($"time zone {ProviderFormats.TurkeyTimeZoneId} is not available on this host (provider times are Türkiye local time)");
        return errors;
    }
}
