using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Ai;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Admin;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Application;
using ToroSquad.Modules.Sor.Commands;
using ToroSquad.Modules.Sor.Persistence;

namespace ToroSquad.Modules.Sor;

/// <summary>
/// TSQ Sor: /sor answers any question with ONE request to the shared OpenCode Go provider, continues the asker's own recent
/// conversation in the same channel/thread and visibility, and remembers short lasting things members say about themselves,
/// within explicit scopes (private, this channel, shared by the member) — /hafizam shows and manages them
/// (docs/sor/TSQ_SOR.md). Conversations, notes and request records live in this module's own tables; /privacy covers them.
/// A separate feature module: depends only on the shared TSQ layers (never on TSQ Özet). Off by default in every guild
/// (/modules enable sor); once enabled, normal members may use it unless /tsq-admin modul:sor limits channels or roles.
/// </summary>
public sealed class SorModule : IToroModule
{
    public const string ModuleIdValue = "sor";

    /// <summary>The <c>modul</c> value of this module's operations in <c>/tsq-admin</c>.</summary>
    public const string AdminId = "sor";

    public const string HttpClientName = "sor-opencode";

    public static readonly ModuleId ModuleIdTyped = new(ModuleIdValue);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleIdTyped,
        new Version(0, 1, 0),
        "module.sor.name",
        "module.sor.description",
        IsCore: false,
        EnabledByDefault: false, // explicit activation: /modules enable sor
        RequiredBotChannelPermissions: GuildPermission.None, // every answer is an interaction response or follow-up
        OptionalBotPermissions: GuildPermission.None,
        AdminCommands: [AdminCatalog.Entry(AdminId)]);

    public IReadOnlyList<Type> InteractionModuleTypes { get; } = [typeof(SorCommands), typeof(SorMemoryCommands)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SorOptions>().Bind(configuration.GetSection(SorOptions.Section));
        services.AddSingleton(new LocalizationSource(typeof(SorModule).Assembly, "ToroSquad.Modules.Sor.Localization"));
        services.AddSingleton<IModelContributor, SorModelContributor>();
        services.AddAdminOperations(SorAdminOperations.Definition);

        // The ONE shared key and endpoint (the same as TSQ Özet), with this module's own client name and honest User-Agent.
        services.AddOpenCodeGoApiKey(configuration[OpenCodeGo.ApiKeyVariable]);
        services.AddOpenCodeGoHttpClient(HttpClientName, sp =>
        {
            var options = sp.GetRequiredService<IOptions<SorOptions>>().Value;
            return (options.BaseUrl, options.RequestTimeout + TimeSpan.FromSeconds(5), UserAgent(sp.GetService<ProductInfo>()?.Version));
        });
        services.AddKeyedSingleton<IAiChatClient>(ModuleIdValue, (sp, _) => new OpenCodeGoChatClient(
            sp.GetRequiredService<IHttpClientFactory>(), HttpClientName, sp.GetRequiredService<OpenCodeGoApiKey>(), sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<SorThrottle>();
        services.AddSingleton<SorStatus>();
        services.AddScoped<SorSettingsReader>();
        services.AddScoped<SorMemoryStore>();
        services.AddScoped<SorStore>();
        services.AddScoped<SorService>();
        services.AddScoped<SorMemoryService>();
        services.AddScoped<SorConfigService>();
        services.AddScoped<SorCleanup>();
        services.AddScoped<IUserDataContributor, SorUserData>();
        services.AddSingleton<IModuleHealthCheck, SorHealthCheck>();
    }

    /// <summary>The retention cleanup, registered only in the long-running host (never by one-shot CLI verbs or tests).</summary>
    public static void AddBackgroundJobs(IServiceCollection services) => services.AddHostedService<SorCleanupWorker>();

    /// <summary>Who is asking, honestly: TSQ Bot's Sor module (no contact data, no secret).</summary>
    public static string UserAgent(string? version)
    {
        var token = new string((version ?? "").TakeWhile(c => c != '+').Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());
        return $"{ProductInfo.UserAgentProduct}/{(token.Length == 0 ? "0" : token)} SorModule (+https://github.com/Torokal/TSQ-Bot)";
    }

    public IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration) =>
        (configuration.GetSection(SorOptions.Section).Get<SorOptions>() ?? new SorOptions()).Validate();
}
