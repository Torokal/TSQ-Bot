using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Domain;
using ToroSquad.Modules.News.Providers;

namespace ToroSquad.Bot;

public static partial class Cli
{
    /// <summary>
    /// news check [--roster]: READ-ONLY. One request to the official HLTV RSS feed (and with --roster one request to the
    /// team's Liquipedia page), then the per-item decision with a short reason. Nothing is stored, staged or sent: a throw-away
    /// data directory, fake Discord transport, dry-run delivery, and the bot database is never opened.
    /// </summary>
    private static async Task<int> NewsAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "check")
            return PrintUsage();
        var options = ParseOptions(args.Skip(1).ToArray());
        var temp = Path.Combine(Path.GetTempPath(), "tsq-news-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TOROSQUAD_Bot__DataDirectory", temp);
        Environment.SetEnvironmentVariable("TOROSQUAD_Discord__Transport", "Fake");
        Environment.SetEnvironmentVariable("TOROSQUAD_Delivery__Mode", "DryRun");
        try
        {
            var builder = ToroHost.CreateBuilder([], longRunning: false);
            using var host = builder.Build();
            var feed = host.Services.GetRequiredService<HltvRssClient>();
            var news = host.Services.GetRequiredService<IOptions<NewsOptions>>().Value;
            Console.WriteLine($"{ProductInfo.ProductName} — READ-ONLY news check (no database, no Discord, nothing stored or sent)");
            Console.WriteLine($"Feed: {HltvRssClient.FeedUrl}");
            var result = await feed.FetchAsync(null, CancellationToken.None);
            Console.WriteLine($"Result: {result.Outcome} HTTP {result.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"}{(result.Detail is null ? "" : " — " + result.Detail)}");
            if (!result.Succeeded)
                return Blocked;
            var dated = result.Items.Where(i => i.PublishedAt is not null).Select(i => i.PublishedAt!.Value).ToList();
            Console.WriteLine($"Items: {result.Items.Count} usable, {result.SkippedItems} skipped; ttl {result.TtlMinutes?.ToString(CultureInfo.InvariantCulture) ?? "-"} min; " +
                              $"ETag {(result.ETag is null ? "none" : "present")}; Last-Modified {(result.LastModified is null ? "none" : "present")}");
            if (dated.Count > 0)
                Console.WriteLine($"Published: {dated.Min():yyyy-MM-dd HH:mm}Z .. {dated.Max():yyyy-MM-dd HH:mm}Z ({result.Items.Count - dated.Count} without a date)");

            var now = DateTimeOffset.UtcNow;
            RosterSnapshot? roster = news.Roster.SeedPlayers.Length > 0 && news.Roster.SeedVerifiedAtValue is { } seedAt ? new(news.Roster.SeedPlayers, seedAt, "seed") : null;
            if (options.ContainsKey("roster"))
            {
                var fetched = await host.Services.GetRequiredService<LiquipediaRosterClient>().FetchAsync(CancellationToken.None);
                Console.WriteLine($"Roster (Liquipedia {news.Roster.LiquipediaPage}): {fetched.Outcome}{(fetched.Detail is null ? "" : " — " + fetched.Detail)}" +
                                  (fetched.Outcome == RosterOutcome.Ok ? " — " + string.Join(", ", fetched.Players) : ""));
                if (fetched.Outcome == RosterOutcome.Ok)
                    roster = new RosterSnapshot(fetched.Players, now, "liquipedia");
            }

            var maxAge = TimeSpan.FromDays(news.Roster.MaxAgeDays);
            Console.WriteLine($"Roster used: {(roster is null ? "none" : $"{roster.Source}, {roster.Players.Count} players, verified {roster.VerifiedAt:yyyy-MM-dd}, fresh {roster.IsFresh(now, maxAge)}")}");
            var matcher = new AuroraNewsMatcher(news.Team.Aliases, news.Team.ExcludedNames, news.Roster.AmbiguousNames);
            var relevant = 0;
            foreach (var item in result.Items.OrderByDescending(i => i.PublishedAt ?? DateTimeOffset.MinValue))
            {
                var match = matcher.Evaluate(item, roster, now, maxAge);
                if (match.Relevant)
                    relevant++;
                Console.WriteLine($"  {(match.Relevant ? "WOULD POST" : "skip      ")}  #{item.ArticleId} {item.PublishedAt:yyyy-MM-dd HH:mm}Z  {match.Reason} ({match.Evidence})  {item.Title}");
            }

            Console.WriteLine($"{relevant} of {result.Items.Count} items match the team. On first activation every current item is baseline (never posted); " +
                              "only items first seen afterwards are posted.");
            return Ok;
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
