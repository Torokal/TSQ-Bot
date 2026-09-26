using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Discord;
using ToroSquad.Infrastructure;
using ToroSquad.Infrastructure.Hosting;
using ToroSquad.Modules.Esports;
using ToroSquad.Modules.Example;
using ToroSquad.Modules.Formula1;
using ToroSquad.Modules.Live;
using ToroSquad.Modules.Volleyball;

namespace ToroSquad.Bot;

/// <summary>
/// Composition root. The module list below is the ONLY place a new module is registered
/// (see docs/ADDING_A_MODULE.md). No runtime DLL loading.
/// </summary>
public static class ToroHost
{
    public const string EnvironmentPrefix = "TOROSQUAD_";

    public static IReadOnlyList<IToroModule> Modules(IConfiguration configuration)
    {
        var modules = new List<IToroModule> { new CoreBotModule(), new EsportsModule(), new Formula1Module(), new VolleyballModule(), new LiveModule() };
        // Example module: development/tests only. Off unless explicitly enabled.
        if (configuration.GetValue("Modules:Example:Enabled", false))
            modules.Add(new ExampleModule());
        return modules;
    }

    public static HostApplicationBuilder CreateBuilder(string[] args, bool longRunning)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Configuration precedence: appsettings.json < appsettings.{Env}.json < user-secrets (Development) < env vars.
        // Secrets (Discord:Token, Esports:Liquipedia:ApiKey) must come from env vars or user-secrets only.
        // NOTE: .env files are NOT read.
        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.FormatterName = RedactingConsoleFormatter.Name)
            .AddConsoleFormatter<RedactingConsoleFormatter, RedactingConsoleFormatterOptions>();
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        AddToroSquad(builder.Services, builder.Configuration, builder.Environment.ContentRootPath, longRunning);
        return builder;
    }

    /// <summary>
    /// Registers everything (shared by the real host and by the integration tests, so tests exercise the same wiring).
    /// </summary>
    public static void AddToroSquad(IServiceCollection services, IConfiguration configuration, string contentRoot, bool longRunning)
    {
        var modules = Modules(configuration);
        foreach (var module in modules)
            services.AddSingleton(module);

        services.AddToroInfrastructure(configuration, contentRoot);
        services.AddToroDiscord(configuration);
        foreach (var module in modules)
            module.ConfigureServices(services, configuration);

        var discord = configuration.GetSection(DiscordOptions.Section).Get<DiscordOptions>() ?? new DiscordOptions();
        services.AddSingleton(new DeploymentPolicy(discord.Transport == DiscordTransportMode.Gateway, discord.TestGuildIds.ToHashSet(),
            discord.AllowedGuildIds.ToHashSet(), HostingChecks.OnRailway(Environment.GetEnvironmentVariable) ? "Railway" : "local"));
        services.AddSingleton(_ => BuildProductInfo(configuration));

        if (longRunning)
        {
            // Order matters: Discord comes up before background jobs start delivering.
            services.AddToroDiscordHosting(configuration);
            services.AddToroBackgroundJobs();
            EsportsModule.AddBackgroundJobs(services);
            Formula1Module.AddBackgroundJobs(services);
            VolleyballModule.AddBackgroundJobs(services);
            LiveModule.AddBackgroundJobs(services);
        }
    }

    /// <summary>Startup validation across all modules + deployment rules. Returns problems (no secret values).</summary>
    public static IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration)
    {
        var problems = new List<string>();
        foreach (var module in Modules(configuration))
            problems.AddRange(module.ValidateConfiguration(configuration).Select(p => $"[{module.Descriptor.Id}] {p}"));

        var discord = configuration.GetSection(DiscordOptions.Section).Get<DiscordOptions>() ?? new DiscordOptions();
        var bot = configuration.GetSection(BotOptions.Section).Get<BotOptions>() ?? new BotOptions();
        if (discord.Transport == DiscordTransportMode.Gateway)
        {
            if (string.IsNullOrWhiteSpace(discord.Token))
                problems.Add("[discord] Transport=Gateway requires a bot token (TOROSQUAD_Discord__Token or user-secrets)");
            if (discord.ApplicationId == 0)
                problems.Add("[discord] Discord:ApplicationId must be set to YOUR application id");
            if (string.IsNullOrWhiteSpace(bot.SourceUrl) && !configuration.GetValue("Bot:AllowMissingSourceUrlForPrivateTesting", false))
                problems.Add("[license] Bot:SourceUrl must point to the Corresponding Source of this running version (AGPL-3.0 §13). " +
                             "For a private test guild only, set Bot:AllowMissingSourceUrlForPrivateTesting=true.");
        }

        if (discord.AllowedGuildIds.Length > 0)
        {
            // Everything the bot may touch must be inside the runtime allow-list (single-guild operation).
            var outside = discord.TestGuildIds.Concat(discord.CommandSyncGuildIds).Where(g => !discord.AllowedGuildIds.Contains(g)).Distinct().ToList();
            if (outside.Count > 0)
                problems.Add($"[discord] Discord:TestGuildIds/CommandSyncGuildIds contain guild(s) outside Discord:AllowedGuildIds: {string.Join(", ", outside)}");
            if (discord.AllowGlobalCommandSync)
                problems.Add("[discord] Discord:AllowGlobalCommandSync must be false while Discord:AllowedGuildIds restricts the bot to specific guilds");
        }

        var mode = configuration.GetValue("Esports:Provider:Mode", "Fixture");
        var delivery = configuration.GetValue("Delivery:Mode", "DryRun");
        if (discord.Transport == DiscordTransportMode.Gateway && string.Equals(mode, "Fixture", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(delivery, "Send", StringComparison.OrdinalIgnoreCase) && discord.TestGuildIds.Length == 0)
            problems.Add("[esports] Fixture data + real Discord + Send requires Discord:TestGuildIds (demo data only in authorized test guilds)");
        return problems;
    }

    public static ProductInfo BuildProductInfo(IConfiguration configuration)
    {
        var informational = typeof(ToroHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var version = plus > 0 ? informational[..plus] : informational;
        var commit = HostingChecks.Commit(plus > 0 ? informational[(plus + 1)..] : null, Environment.GetEnvironmentVariable);
        var bot = configuration.GetSection(BotOptions.Section).Get<BotOptions>() ?? new BotOptions();
        return new ProductInfo(
            ProductInfo.ProductName,
            version,
            commit,
            ProductInfo.LicenseId,
            bot.SourceUrl,
            bot.OperatorContact,
            [
                new Attribution("PandaScore", "https://pandascore.co", "about.attr.pandascore_terms", "about.attr.pandascore"),
                new Attribution("Liquipedia", "https://liquipedia.net/counterstrike", "CC BY-SA 3.0", "about.attr.liquipedia"),
                new Attribution("Valve Regional Standings", "https://github.com/ValveSoftware/counter-strike_regional_standings", "about.attr.valve_license", "about.attr.valve"),
                new Attribution("Jolpica F1", "https://github.com/jolpica/jolpica-f1", "about.attr.f1_data_license", "about.attr.jolpica"),
                new Attribution("OpenF1", "https://openf1.org", "about.attr.f1_data_license", "about.attr.openf1"),
                new Attribution("BOT-Greg-v2_API (Julius Gmeinder)", "https://github.com/julius-gmeinder/BOT-Greg-v2_API", "AGPL-3.0", "about.attr.upstream"),
                new Attribution("Discord.Net", "https://github.com/discord-net/Discord.Net", "MIT", "about.attr.discordnet"),
                new Attribution("MQTTnet", "https://github.com/dotnet/MQTTnet", "MIT", "about.attr.mqttnet"),
                new Attribution("FIVB VIS", "https://www.fivb.org/VisSDK/VisWebService/", "about.attr.fivb_terms", "about.attr.fivb"),
                new Attribution("Twitch API", "https://dev.twitch.tv/docs/api/", "about.attr.twitch_terms", "about.attr.twitch"),
                new Attribution("Kick Public API", "https://docs.kick.com", "about.attr.kick_terms", "about.attr.kick"),
            ]);
    }
}
