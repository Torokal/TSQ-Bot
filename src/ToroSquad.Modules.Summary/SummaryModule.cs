using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Summary.Application;
using ToroSquad.Modules.Summary.Commands;
using ToroSquad.Modules.Summary.Providers;

namespace ToroSquad.Modules.Summary;

/// <summary>
/// TSQ Özet: /ozetle summarizes the latest member messages of the channel or thread it ran in (docs/summary/TSQ_SUMMARY.md).
/// The messages are read over REST when asked — no message events, no cache, no listener, nothing stored; the application
/// needs Message Content access (Developer Portal) for other members' text, the gateway intents stay Guilds. One summary =
/// one AI request to the configured OpenCode Go model (<see cref="SummaryOptions.Model"/>): no retry, no fallback model, no
/// second pass. The API key comes only from <see cref="SummaryOptions.ApiKeyVariable"/>; without it /ozetle answers
/// "not configured" and the rest of the bot is unaffected. A stateless module: no tables, no migration, no background jobs.
/// Off by default in every guild (/modules enable summary), like every other optional module.
/// </summary>
public sealed class SummaryModule : IToroModule
{
    public const string ModuleIdValue = "summary";
    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.summary.name",
        "module.summary.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable summary
        RequiredBotChannelPermissions: SummaryService.RequiredToRead, // the answer is an interaction follow-up: reading is what the bot needs
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: []);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(SummaryCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SummaryOptions>().Bind(configuration.GetSection(SummaryOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(SummaryModule).Assembly, "ToroSquad.Modules.Summary.Localization"));

        // The key: user-secrets/configuration first (development), then the plain environment variable (Railway Variables).
        services.AddSingleton(new SummaryApiKey(configuration[SummaryOptions.ApiKeyVariable] ?? Environment.GetEnvironmentVariable(SummaryOptions.ApiKeyVariable)));

        // Fixed base URL from configuration, no redirects (a moved endpoint is a visible failure), no cookies, bounded answer
        // size; the per-request timeout is applied by OpenCodeSummaryAiClient on the injected clock (this one is a backstop).
        services.AddHttpClient(OpenCodeSummaryAiClient.HttpClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<SummaryOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl);
                client.Timeout = options.RequestTimeout + TimeSpan.FromSeconds(5);
                client.MaxResponseContentBufferSize = OpenCodeSummaryAiClient.MaxResponseBytes;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent(sp.GetService<ProductInfo>()?.Version));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        services.AddSingleton<ISummaryAiClient, OpenCodeSummaryAiClient>();
        services.AddSingleton<SummaryThrottle>();
        services.AddSingleton<SummaryService>();
        services.AddSingleton<IModuleHealthCheck, SummaryHealthCheck>();
    }

    /// <summary>Who is asking, honestly: TSQ Bot's summary module (no contact data, no secret).</summary>
    public static string UserAgent(string? version)
    {
        var token = new string((version ?? "").TakeWhile(c => c != '+').Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());
        return $"{ProductInfo.UserAgentProduct}/{(token.Length == 0 ? "0" : token)} SummaryModule (+https://github.com/Torokal/TSQ-Bot)";
    }

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) =>
        (configuration.GetSection(SummaryOptions.Section).Get<SummaryOptions>() ?? new SummaryOptions()).Validate();
}
