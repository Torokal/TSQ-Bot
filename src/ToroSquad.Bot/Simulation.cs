using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;

namespace ToroSquad.Bot;

/// <summary>
/// Offline end-to-end run: fixture provider → real parser → planner → outbox → dispatcher → FAKE transport.
/// Uses a throw-away database in a temp folder. Nothing is sent to Discord; output is clearly local.
/// </summary>
public static class Simulation
{
    private static readonly GuildId Guild = new(900_000_000_000_000_001);
    private static readonly ChannelId Channel = new(900_000_000_000_000_101);
    private static readonly RoleId NotifyRole = new(900_000_000_000_000_201);

    public static async Task<int> RunAsync(string[] args)
    {
        var temp = Path.Combine(Path.GetTempPath(), "torosquad-sim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TOROSQUAD_Bot__DataDirectory", temp);
        Environment.SetEnvironmentVariable("TOROSQUAD_Discord__Transport", "Fake");
        Environment.SetEnvironmentVariable("TOROSQUAD_Esports__Provider__Mode", "Fixture");
        Environment.SetEnvironmentVariable("TOROSQUAD_Delivery__Mode", "Send");
        Environment.SetEnvironmentVariable("TOROSQUAD_Esports__Liquipedia__UserAgent", "ToroSquadBot-simulation/0 (https://localhost; local)");

        var builder = ToroHost.CreateBuilder(args, longRunning: false);
        using var host = builder.Build();
        var services = host.Services;
        await using (var scope = services.CreateAsyncScope())
            await DatabaseMaintenance.MigrateAsync(scope.ServiceProvider.GetRequiredService<ToroDbContext>(), CancellationToken.None);

        Console.WriteLine("=== ToroSquad Bot — OFFLINE SIMULATION (fixture data, fake Discord, temp DB) ===");
        Console.WriteLine("Database: " + temp);

        var guilds = services.GetRequiredService<FakeGuildGateway>();
        guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild,
            new RoleInfo(NotifyRole, "CS2 Bildirim", 2, GuildPermission.None, false, false, true)));
        guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory));

        var admin = new ActorContext(Guild, new UserId(900_000_000_000_000_301), GuildPermission.ManageGuild | GuildPermission.ManageRoles, [], true, 100);
        await using (var scope = services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            Print(await sp.GetRequiredService<EsportsConfigService>().ConfigureAsync(admin, Channel.Value, true, 30, true, false, CancellationToken.None));
            Print(await sp.GetRequiredService<RoleMappingService>().MapAsync(admin, NotifyRole, null, true, false, CancellationToken.None));
            Print(await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(admin, "esports", true, CancellationToken.None));
        }

        var poller = services.GetRequiredService<EsportsPoller>();
        await poller.RefreshRankingsAsync(CancellationToken.None);
        var report = await poller.RefreshMatchesAsync(CancellationToken.None);
        Console.WriteLine($"Poll 1: {report}");
        var report2 = await poller.RefreshMatchesAsync(CancellationToken.None);
        Console.WriteLine($"Poll 2 (same data again): {report2}  ← no duplicates expected");

        var processor = services.GetRequiredService<OutboxProcessor>();
        await processor.ProcessOnceAsync(CancellationToken.None);

        var transport = services.GetRequiredService<FakeMessageTransport>();
        Console.WriteLine($"\n--- Messages delivered to the FAKE transport: {transport.Messages.Count} ---");
        foreach (var m in transport.Messages)
            PrintMessage(m.Message);

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            var byStatus = await db.Outbox.GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
            Console.WriteLine("\nOutbox: " + string.Join(", ", byStatus.Select(s => $"{s.Key}={s.Count}")));
            var baseline = await db.Set<ToroSquad.Modules.Esports.Persistence.MatchSnapshotEntity>().CountAsync(s => s.IsBaseline);
            Console.WriteLine($"Finished matches recorded as silent baseline on first run: {baseline} (no backlog flood)");
        }

        // Render-only preview of result formatting (NOT sent): plain and spoiler variants.
        var renderer = services.GetRequiredService<NotificationRenderer>();
        var finished = services.GetRequiredService<EsportsCache>().Matches.Data?.FirstOrDefault(m => m.Status == MatchStatus.Finished && m.MapsComplete);
        if (finished is not null)
        {
            Console.WriteLine("\n--- Result rendering preview (not sent) ---");
            PrintMessage(renderer.Result(finished, "tr", spoiler: false, MentionPolicy.None, DateTimeOffset.UtcNow));
            PrintMessage(renderer.Result(finished, "tr", spoiler: true, MentionPolicy.None, DateTimeOffset.UtcNow));
        }

        await host.StopAsync();
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
            // temp folder cleanup is best effort
        }

        return Cli.Ok;
    }

    private static void Print(OperationResult result) =>
        Console.WriteLine($"  setup: {(result.Succeeded ? "ok" : "FAILED")} {result.MessageKey}");

    private static void PrintMessage(OutgoingMessage message)
    {
        Console.WriteLine(new string('-', 60));
        if (message.Content is not null)
            Console.WriteLine("content: " + message.Content + $"   (allowed role pings: {message.Mentions.Roles.Count})");
        if (message.Embed is { } e)
        {
            Console.WriteLine("title:   " + e.Title);
            Console.WriteLine(e.Description);
            foreach (var f in e.Fields)
                Console.WriteLine($"[{f.Name}] {f.Value}");
            Console.WriteLine("footer:  " + e.Footer);
        }
    }
}
