using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ToroSquad.Core;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Bot;

public static partial class Cli
{
    /// <summary>
    /// updates check [--game KEY]: READ-ONLY. One request per registered game (or only the named one) to its provider, then
    /// every post's id, publication time, classification and reason, and title. No post text is printed. Nothing is stored,
    /// staged or sent: a throw-away data directory, fake Discord transport, dry-run delivery, the bot database is never
    /// opened, and no API key is used.
    /// </summary>
    private static async Task<int> UpdatesAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "check")
            return PrintUsage();
        var options = ParseOptions(args.Skip(1).ToArray());
        var temp = Path.Combine(Path.GetTempPath(), "tsq-updates-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TOROSQUAD_Bot__DataDirectory", temp);
        Environment.SetEnvironmentVariable("TOROSQUAD_Discord__Transport", "Fake");
        Environment.SetEnvironmentVariable("TOROSQUAD_Delivery__Mode", "DryRun");
        try
        {
            var builder = ToroHost.CreateBuilder([], longRunning: false);
            using var host = builder.Build();
            var catalog = host.Services.GetRequiredService<GameUpdateCatalog>();
            var games = catalog.Games;
            if (options.TryGetValue("game", out var wanted))
            {
                if (catalog.Find(wanted) is not { } only)
                {
                    await Console.Error.WriteLineAsync($"Unknown game '{wanted}'. Registered: {string.Join(", ", catalog.Games.Select(g => g.Key))}");
                    return Usage;
                }

                games = [only];
            }

            Console.WriteLine($"{ProductInfo.ProductName} — READ-ONLY updates check (no database, no Discord, no API key, nothing stored or sent)");
            var failed = false;
            foreach (var game in games)
            {
                var provider = catalog.ProviderOf(game);
                Console.WriteLine();
                Console.WriteLine($"Game: {game.DisplayName} ({game.Key}) — provider {provider.DisplayName}, id {game.ProviderGameId}");
                var result = await provider.FetchAsync(game, CancellationToken.None);
                Console.WriteLine($"Result: {result.Outcome} HTTP {result.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"}{(result.Detail is null ? "" : " — " + result.Detail)}");
                if (game.WatchedThreadIds.Count > 0)
                    Console.WriteLine($"Watched threads: {string.Join(", ", game.WatchedThreadIds)}");
                if (!result.Succeeded)
                {
                    failed = true;
                    continue;
                }

                Console.WriteLine($"Posts: {result.Items.Count} usable, {result.SkippedItems} skipped; source cache " +
                                  (result.CacheLifetime is { } cache ? $"{cache.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} min" : "not declared"));
                Console.WriteLine("  ID                   | Published (UTC)  | Classification (reason)                 | Title");
                var counts = new Dictionary<UpdateClassification, int>();
                foreach (var item in result.Items.OrderByDescending(i => i.PublishedAt ?? DateTimeOffset.MinValue))
                {
                    var verdict = game.Classifier.Classify(item);
                    counts[verdict.Classification] = counts.GetValueOrDefault(verdict.Classification) + 1;
                    var published = item.PublishedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
                    // Version, build and the size of the change list when the provider supplies them — never the text itself.
                    var facts = item.Highlights is { IsEmpty: false } h
                        ? "  [" + string.Join(" · ", new[] { h.Version, h.Build is null ? null : "Build " + h.Build, h.ChangeCount + " changes" }.Where(p => p is not null)) + "]"
                        : "";
                    Console.WriteLine($"  {item.ExternalId,-20} | {published,-16} | {verdict.Classification + " (" + verdict.Reason + ")",-39} | {item.Title}{facts}");
                }

                Console.WriteLine($"{counts.GetValueOrDefault(UpdateClassification.Update)} update(s), {counts.GetValueOrDefault(UpdateClassification.Ambiguous)} ambiguous, " +
                                  $"{counts.GetValueOrDefault(UpdateClassification.NotUpdate)} not an update. On first activation every current post is baseline (never posted); " +
                                  "only updates published afterwards are posted.");
            }

            return failed ? Blocked : Ok;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
