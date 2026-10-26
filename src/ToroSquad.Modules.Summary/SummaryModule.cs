using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Ai;
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

        // The key (shared with every OpenCode Go feature): user-secrets/configuration first (development), then the plain
        // environment variable (Railway Variables).
        services.AddOpenCodeGoApiKey(configuration[SummaryOptions.ApiKeyVariable]);

        // The shared transport settings (fixed base URL, no redirects, no cookies, bounded answer size) with this module's own
        // User-Agent; the per-request timeout is applied on the injected clock (the client's timeout is a backstop).
        services.AddOpenCodeGoHttpClient(OpenCodeSummaryAiClient.HttpClientName, sp =>
        {
            var options = sp.GetRequiredService<IOptions<SummaryOptions>>().Value;
            return (options.BaseUrl, options.LongestRequestTimeout + TimeSpan.FromSeconds(5), UserAgent(sp.GetService<ProductInfo>()?.Version));
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
