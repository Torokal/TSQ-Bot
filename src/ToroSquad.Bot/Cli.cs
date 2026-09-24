using System.Globalization;
using System.Text.RegularExpressions;
using Discord;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Discord;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Hosting;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Bot;

public static partial class Cli
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int Blocked = 3;

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args.Length == 0 || args[0].StartsWith('-') ? "run" : args[0].ToLowerInvariant();
        var rest = verb == "run" && (args.Length == 0 || args[0].StartsWith('-')) ? args : args.Skip(1).ToArray();
        try
        {
            return verb switch
            {
                "run" => await RunBotAsync(rest),
                "commands" => await CommandsAsync(rest),
                "esports" => await EsportsAsync(rest),
                "doctor" => await DoctorAsync(rest),
                "db" => await DbAsync(rest),
                "simulate" => await Simulation.RunAsync(rest),
                "help" or "--help" or "-h" => PrintUsage(),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex) when (DescribeConfigurationError(ex) is { } problem)
        {
            await Console.Error.WriteLineAsync("CONFIG: " + problem);
            return Failed;
        }
        catch (Exception ex)
        {
            // Last line of defence: never print secrets.
            var redactor = new SecretRedactor([Environment.GetEnvironmentVariable("TOROSQUAD_Discord__Token"), Environment.GetEnvironmentVariable("TOROSQUAD_Esports__Liquipedia__ApiKey"), Environment.GetEnvironmentVariable("TOROSQUAD_PandaScore__Token")]);
            await Console.Error.WriteLineAsync("FATAL: " + redactor.Redact(ex.ToString()));
            return Failed;
        }
    }

    /// <summary>
    /// Turns a configuration binding failure into a one-line message naming the key and expected type. The offending
    /// value is never echoed (it may be a secret pasted into the wrong key).
    /// </summary>
    public static string? DescribeConfigurationError(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            var match = ConfigurationConversionError().Match(e.Message);
            if (match.Success)
            {
                var type = match.Groups["type"].Value;
                return $"'{match.Groups["key"].Value}' has an invalid value for {type[(type.LastIndexOf('.') + 1)..]} (value not shown). " +
                       "Remove placeholder characters such as < > or quotes and set it again (user-secrets or TOROSQUAD_ environment variables).";
            }
        }

        return null;
    }

    private static int PrintUsage()
    {
        Console.WriteLine(ProductInfo.ProductName);
        Console.WriteLine("""
              run                                     start the bot (default)
              commands export [--out FILE]            build + validate slash-command manifest (offline)
              commands sync --guild ID [--apply] [--prune]
              commands sync --global [--apply]        needs Discord:AllowGlobalCommandSync=true
              doctor                                  configuration diagnosis
              db migrate | db backup [--out DIR] | db restore FILE --yes
              simulate                                offline end-to-end demo (fixture data, fake Discord, temp DB)
              esports demo-cards --guild ID [--apply] TEST/DEMO match cards into the outbox of an authorized test guild
            """);
        return Usage;
    }

    private static async Task<int> RunBotAsync(string[] args)
    {
        var builder = ToroHost.CreateBuilder(args, longRunning: true);
        var problems = ToroHost.ValidateConfiguration(builder.Configuration);
        if (problems.Count > 0)
        {
            foreach (var p in problems)
                await Console.Error.WriteLineAsync("CONFIG: " + p);
            return Failed;
        }

        var bot = builder.Configuration.GetSection(BotOptions.Section).Get<BotOptions>() ?? new BotOptions();
        var dataDir = Path.GetDirectoryName(bot.DatabasePath(builder.Environment.ContentRootPath))!;
        using var instanceLock = SingleInstanceLock.Acquire(dataDir);

        using var host = builder.Build();
        await MigrateAsync(host.Services);
        await host.RunAsync();
        return Ok;
    }

    private static async Task MigrateAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await DatabaseMaintenance.MigrateAsync(scope.ServiceProvider.GetRequiredService<ToroDbContext>(), CancellationToken.None);
    }

    /// <summary>
    /// Stages one TEST/DEMO card of every match-card kind into the durable outbox for an authorized test guild's esports
    /// channel; the running bot delivers them. Re-running stages nothing new (same logical keys). Dry-run by default.
    /// </summary>
    private static async Task<int> EsportsAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "demo-cards")
            return PrintUsage();
        var options = ParseOptions(args.Skip(1).ToArray());
        if (!options.TryGetValue("guild", out var g) || !ulong.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out var guildId))
            return PrintUsage();

        var builder = ToroHost.CreateBuilder([], longRunning: false);
        using var host = builder.Build();
        await MigrateAsync(host.Services);
        var discord = host.Services.GetRequiredService<IOptions<DiscordOptions>>().Value;
        if (!discord.TestGuildIds.Contains(guildId))
        {
            await Console.Error.WriteLineAsync("BLOCKED: demo cards only go to guilds listed in Discord:TestGuildIds. Nothing was staged.");
            return Blocked;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ToroDbContext>();
        var config = await db.Set<ToroSquad.Modules.Esports.Persistence.EsportsGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId);
        if (config?.ChannelId is not { } channelId)
        {
            await Console.Error.WriteLineAsync("BLOCKED: this guild has no esports channel (run /setup). Nothing was staged.");
            return Blocked;
        }

        var guild = new ToroSquad.Core.GuildId(guildId);
        var settings = await sp.GetRequiredService<ToroSquad.Core.Guilds.IGuildSettingsStore>().GetAsync(guild, CancellationToken.None);
        if (!ToroSquad.Core.Guilds.GuildTime.TryResolve(settings.TimeZoneId, out var zone))
            ToroSquad.Core.Guilds.GuildTime.TryResolve(ToroSquad.Core.Guilds.GuildSettings.DefaultTimeZoneId, out zone);
        // Always the demo renderer: cards are labelled TEST/DEMO whatever the configured provider mode is.
        var renderer = new ToroSquad.Modules.Esports.Application.NotificationRenderer(sp.GetRequiredService<ToroSquad.Core.Localization.ILocalizer>(),
            new EsportsDataMode(ProviderMode.Fixture));
        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();
        var cards = ToroSquad.Modules.Esports.Application.EsportsDemoCards.Build(renderer, settings.Language, zone, now);
        foreach (var (kind, message) in cards)
            Console.WriteLine($"  {kind,-20} {message.Embed!.Title} | {message.Embed.Description!.Split('\n')[0]}");

        if (!options.ContainsKey("apply"))
        {
            Console.WriteLine("Dry-run only. Re-run with --apply to stage these cards (the running bot delivers them).");
            return Ok;
        }

        var outbox = sp.GetRequiredService<ToroSquad.Core.Notifications.INotificationOutbox>();
        foreach (var request in ToroSquad.Modules.Esports.Application.EsportsDemoCards.Requests(guild, new ToroSquad.Core.ChannelId(channelId), cards, now))
            Console.WriteLine($"  {request.Kind,-20} {await outbox.StageAsync(request, CancellationToken.None)}");
        await db.SaveChangesAsync();
        Console.WriteLine($"Staged for guild {guildId}, channel {channelId}. Delivery needs the running bot with Delivery:Mode=Send.");
        return Ok;
    }

    private static async Task<int> CommandsAsync(string[] args)
    {
        if (args.Length == 0)
            return PrintUsage();
        var sub = args[0];
        var options = ParseOptions(args.Skip(1).ToArray());

        var builder = ToroHost.CreateBuilder([], longRunning: false);
        using var host = builder.Build();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        var problems = CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames);

        if (sub == "export")
        {
            Console.WriteLine($"Manifest: {manifest.Commands.Count} top-level commands, hash {manifest.Hash}");
            foreach (var c in manifest.Commands.OrderBy(c => c.Name, StringComparer.Ordinal))
                Console.WriteLine($"  /{c.Name,-15} module={c.OwnerModule,-8} perms={c.DefaultMemberPermissions ?? "-",-6} options={c.Options.Count}");
            if (options.TryGetValue("out", out var outFile))
            {
                await File.WriteAllTextAsync(outFile, manifest.ToDocumentJson() + "\n");
                Console.WriteLine($"Written: {outFile}");
            }

            foreach (var p in problems)
                await Console.Error.WriteLineAsync("INVALID: " + p);
            return problems.Count == 0 ? Ok : Failed;
        }

        if (sub != "sync")
            return PrintUsage();

        if (problems.Count > 0)
        {
            foreach (var p in problems)
                await Console.Error.WriteLineAsync("INVALID: " + p);
            await Console.Error.WriteLineAsync("Sync refused: manifest invalid (nothing was changed remotely).");
            return Failed;
        }

        var discord = host.Services.GetRequiredService<IOptions<DiscordOptions>>().Value;
        SyncScope scope;
        if (options.ContainsKey("global"))
            scope = new SyncScope.Global();
        else if (options.TryGetValue("guild", out var g) && ulong.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out var guildId))
            scope = new SyncScope.Guild(guildId);
        else
            return PrintUsage();

        if (string.IsNullOrWhiteSpace(discord.Token))
        {
            await Console.Error.WriteLineAsync("BLOCKED: no bot token configured (TOROSQUAD_Discord__Token or user-secrets). Nothing was changed.");
            return Blocked;
        }

        await MigrateAsync(host.Services);
        var apply = options.ContainsKey("apply");
        using var rest = new DiscordRestClient(new DiscordRestConfig { DefaultRetryMode = RetryMode.RetryRatelimit });
        await rest.LoginAsync(TokenType.Bot, discord.Token);
        await using var dbScope = host.Services.CreateAsyncScope();
        var service = new CommandSyncService(dbScope.ServiceProvider.GetRequiredService<IManagedCommandStore>(),
            dbScope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CommandSyncService>>());
        var request = new SyncRequest(scope, discord.ApplicationId, 0, discord.CommandSyncGuildIds.ToHashSet(), discord.AllowGlobalCommandSync,
            options.ContainsKey("prune"), interactions.AdminCommandNames);
        var report = await service.RunAsync(manifest, new DiscordCommandRegistrar(rest), request, apply, CancellationToken.None);

        Console.WriteLine($"Scope: {scope.Key}  mode: {(apply ? "APPLY" : "DRY-RUN")}  manifest hash: {manifest.Hash[..12]}");
        foreach (var e in report.Plan.BlockingErrors)
            await Console.Error.WriteLineAsync("BLOCKED: " + e);
        foreach (var item in report.Plan.Items)
            Console.WriteLine($"  {item.Action,-26} /{item.Name}" + (item.Detail is null ? "" : $"\n      {item.Detail}"));
        foreach (var p in report.Performed)
            Console.WriteLine("  done: " + p);
        foreach (var f in report.Failures)
            await Console.Error.WriteLineAsync("  FAILED: " + f);
        if (!apply && !report.Plan.IsBlocked)
            Console.WriteLine(report.Plan.HasChanges ? "Dry-run only. Re-run with --apply to perform these changes." : "Nothing to change.");
        await rest.LogoutAsync();
        return report.Plan.IsBlocked ? Blocked : report.Failures.Count > 0 ? Failed : Ok;
    }

    private static async Task<int> DoctorAsync(string[] args)
    {
        var builder = ToroHost.CreateBuilder(args, longRunning: false);
        var config = builder.Configuration;
        var lines = new List<(string State, string Text)>();
        void Add(string state, string text) => lines.Add((state, text));

        Add("OK", $".NET runtime {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Add("OK", $"Environment: {builder.Environment.EnvironmentName}; content root: {builder.Environment.ContentRootPath}");

        var discord = config.GetSection(DiscordOptions.Section).Get<DiscordOptions>() ?? new DiscordOptions();
        Add("OK", $"Discord transport: {discord.Transport}; Delivery mode: {config.GetValue("Delivery:Mode", "DryRun")}; Esports provider: {config.GetValue("Esports:Provider:Mode", "Fixture")}");
        Add(string.IsNullOrWhiteSpace(discord.Token) ? (discord.Transport == DiscordTransportMode.Gateway ? "FAIL" : "BLOCKED") : "OK",
            "Discord bot token: " + (string.IsNullOrWhiteSpace(discord.Token) ? "NOT SET (needed for live Discord / command sync)" : "set (value hidden)"));
        Add(discord.ApplicationId == 0 ? "BLOCKED" : "OK", "Discord application id: " + (discord.ApplicationId == 0 ? "NOT SET" : discord.ApplicationId.ToString(CultureInfo.InvariantCulture)));
        Add(discord.CommandSyncGuildIds.Length == 0 ? "BLOCKED" : "OK", $"Command sync guild allow-list: {discord.CommandSyncGuildIds.Length} guild(s)");
        Add("OK", $"Global command sync allowed: {discord.AllowGlobalCommandSync}");
        Add(discord.TestGuildIds.Length == 0 ? "WARN" : "OK", $"Authorized test guilds: {discord.TestGuildIds.Length}");

        var providerName = config.GetValue("Esports:Provider:Name", "PandaScore");
        var liquipediaSelected = string.Equals(providerName, "Liquipedia", StringComparison.OrdinalIgnoreCase);
        Add("OK", "Esports match provider: " + (liquipediaSelected ? "Liquipedia (legacy/optional)" : "PandaScore (default)"));
        var pandaTokenSet = !string.IsNullOrWhiteSpace(config["PandaScore:Token"]);
        Add(pandaTokenSet ? "OK" : liquipediaSelected ? "INFO" : "BLOCKED",
            "PandaScore token: " + (pandaTokenSet ? "set (value hidden)" : "NOT SET (live PandaScore data BLOCKED)"));
        var apiKeySet = !string.IsNullOrWhiteSpace(config["Esports:Liquipedia:ApiKey"]);
        Add(apiKeySet ? "OK" : liquipediaSelected ? "BLOCKED" : "INFO", "Liquipedia API key: " + (apiKeySet ? "set (value hidden)" : "NOT SET" + (liquipediaSelected ? " (live esports data NOT_CONFIGURED)" : " (not needed: provider is PandaScore)")));
        var ua = config["Esports:Liquipedia:UserAgent"];
        if (liquipediaSelected)
            Add(string.IsNullOrWhiteSpace(ua) ? "BLOCKED" : "OK", "Liquipedia User-Agent: " + (string.IsNullOrWhiteSpace(ua) ? "NOT SET (required for live)" : ua));
        var bot = config.GetSection(BotOptions.Section).Get<BotOptions>() ?? new BotOptions();
        Add(string.IsNullOrWhiteSpace(bot.SourceUrl) ? "WARN" : "OK", "Bot:SourceUrl (AGPL Corresponding Source): " + (bot.SourceUrl ?? "NOT SET"));

        foreach (var p in ToroHost.ValidateConfiguration(config))
            Add("FAIL", "Config: " + p);

        foreach (var file in Directory.GetFiles(builder.Environment.ContentRootPath, "appsettings*.json"))
        {
            var text = await File.ReadAllTextAsync(file);
            if (SecretRedactor.DiscordTokenPattern().IsMatch(text) || ApiKeyValue().IsMatch(text))
                Add("FAIL", $"Possible secret committed in {Path.GetFileName(file)} — move it to env vars/user-secrets");
        }

        using var host = builder.Build();
        var dbPath = bot.DatabasePath(builder.Environment.ContentRootPath);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            Add(pending.Count == 0 ? "OK" : "WARN", $"Database {dbPath}: {(pending.Count == 0 ? "up to date" : $"{pending.Count} pending migration(s) — run: db migrate")}");
        }
        catch (Exception ex)
        {
            Add("FAIL", $"Database {dbPath}: {ex.GetType().Name}");
        }

        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        var manifestProblems = CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames);
        Add(manifestProblems.Count == 0 ? "OK" : "FAIL", $"Slash command manifest: {manifest.Commands.Count} commands, {manifestProblems.Count} problem(s), hash {manifest.Hash[..12]}");
        foreach (var p in manifestProblems.Take(10))
            Add("FAIL", "  " + p);

        var provider = host.Services.GetRequiredService<IEsportsDataProvider>();
        var mode = host.Services.GetRequiredService<EsportsDataMode>();
        Add(provider.IsConfigured ? "OK" : "BLOCKED", $"Esports provider '{provider.Id}' ({(mode.IsDemo ? "FIXTURE/DEMO" : "LIVE")}): {(provider.IsConfigured ? "configured" : "NOT_CONFIGURED")}; verified live status supported: {(provider.Capabilities & ProviderCapability.VerifiedLiveStatus) != 0}");

        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        Add("OK", "Modules: " + string.Join(", ", registry.All.Select(m => $"{m.Descriptor.Id} v{m.Descriptor.Version}{(m.Descriptor.IsCore ? " (core)" : "")}")));

        var redactor = host.Services.GetRequiredService<SecretRedactor>();
        foreach (var (state, text) in lines)
            Console.WriteLine($"[{state,-7}] {redactor.Redact(text)}");
        return lines.Any(l => l.State == "FAIL") ? Failed : Ok;
    }

    private static async Task<int> DbAsync(string[] args)
    {
        if (args.Length == 0)
            return PrintUsage();
        var builder = ToroHost.CreateBuilder([], longRunning: false);
        var bot = builder.Configuration.GetSection(BotOptions.Section).Get<BotOptions>() ?? new BotOptions();
        var dbPath = bot.DatabasePath(builder.Environment.ContentRootPath);
        var options = ParseOptions(args.Skip(1).ToArray());
        using var host = builder.Build();

        switch (args[0])
        {
            case "migrate":
                await MigrateAsync(host.Services);
                Console.WriteLine($"Migrated: {dbPath}");
                return Ok;
            case "backup":
                await MigrateAsync(host.Services);
                var dir = options.GetValueOrDefault("out") ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
                Console.WriteLine("Backup written: " + DatabaseMaintenance.Backup(dbPath, dir, TimeProvider.System));
                return Ok;
            case "restore":
                var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
                if (file is null || !options.ContainsKey("yes"))
                {
                    await Console.Error.WriteLineAsync("Restore overwrites the current database. Usage: db restore FILE --yes (bot must be stopped).");
                    return Usage;
                }

                // Refuses while the bot is running (same lock the bot holds).
                using (SingleInstanceLock.Acquire(Path.GetDirectoryName(dbPath)!))
                {
                    var safety = DatabaseMaintenance.Restore(file, dbPath, TimeProvider.System);
                    Console.WriteLine($"Restored {file} → {dbPath}. Previous database kept at {safety}");
                }

                return Ok;
            default:
                return PrintUsage();
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            result[key] = value;
        }

        return result;
    }

    [GeneratedRegex("\"ApiKey\"\\s*:\\s*\"[^\"]{8,}\"", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyValue();

    [GeneratedRegex(@"^Failed to convert configuration value .* at '(?<key>[^']+)' to type '(?<type>[^']+)'", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ConfigurationConversionError();
}
