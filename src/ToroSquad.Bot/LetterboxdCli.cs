using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Modules.Letterboxd.Application;
using ToroSquad.Modules.Letterboxd.Domain;
using ToroSquad.Modules.Letterboxd.Providers;

namespace ToroSquad.Bot;

public static partial class Cli
{
    /// <summary>
    /// letterboxd check: READ-ONLY. One request to the followed member's official RSS feed, then the per-entry decision
    /// (which card it would get, or none). Nothing is stored, staged or sent: a throw-away data directory, fake Discord
    /// transport, dry-run delivery, and the bot database is never opened. Review text is never printed.
    /// </summary>
    private static async Task<int> LetterboxdAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "check")
            return PrintUsage();
        var temp = Path.Combine(Path.GetTempPath(), "tsq-letterboxd-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TOROSQUAD_Bot__DataDirectory", temp);
        Environment.SetEnvironmentVariable("TOROSQUAD_Discord__Transport", "Fake");
        Environment.SetEnvironmentVariable("TOROSQUAD_Delivery__Mode", "DryRun");
        try
        {
            var builder = ToroHost.CreateBuilder([], longRunning: false);
            using var host = builder.Build();
            var feed = host.Services.GetRequiredService<LetterboxdRssClient>();
            var options = host.Services.GetRequiredService<IOptions<LetterboxdOptions>>().Value;
            Console.WriteLine($"{ProductInfo.ProductName} — READ-ONLY Letterboxd check (no database, no Discord, nothing stored or sent)");
            Console.WriteLine($"Feed: {feed.FeedUrl}");
            var result = await feed.FetchAsync(null, CancellationToken.None);
            Console.WriteLine($"Result: {result.Outcome} HTTP {result.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"}{(result.Detail is null ? "" : " — " + result.Detail)}");
            if (!result.Succeeded)
                return Blocked;
            var dated = result.Items.Where(i => i.View.PublishedAt is not null).Select(i => i.View.PublishedAt!.Value).ToList();
            Console.WriteLine($"Items: {result.TotalItems} in feed, {result.Items.Count} entries, skipped: {result.Skipped}; ttl {result.TtlMinutes?.ToString(CultureInfo.InvariantCulture) ?? "-"} min; " +
                              $"ETag {(result.ETag is null ? "none" : "present")}; Last-Modified {(result.LastModified is null ? "none" : "present")}");
            if (dated.Count > 0)
                Console.WriteLine($"Published: {dated.Min():yyyy-MM-dd HH:mm}Z .. {dated.Max():yyyy-MM-dd HH:mm}Z ({result.Items.Count - dated.Count} without a date)");
            foreach (var item in result.Items)
            {
                var v = item.View;
                var card = v.Postable ? "CARD " + v.CardKind : "none (unrated first watch)";
                Console.WriteLine($"  {item.GuidKind,-6} #{item.ViewingId}  {v.WatchedDate:yyyy-MM-dd}  rating {(v.RatingHalves is { } r ? EntryRating.Number(r, CultureInfo.InvariantCulture) : "-"),-3} " +
                                  $"rewatch {(v.Rewatch is { } rw ? (rw ? "yes" : "no") : "?"),-3} review {(v.HasReview ? $"{v.Review!.Length} chars" : "-"),-9} " +
                                  $"spoilers {(v.Spoilers == true ? "yes" : "?"),-3} poster {(v.PosterUrl is null ? "-" : "ok")}  {card}  {v.FilmTitle} ({v.FilmYear})");
            }

            Console.WriteLine($"Account {options.Account}: on first activation every current entry is baseline (never posted); only later entries and changes are posted. " +
                              "Standalone ratings (without a diary entry) are not in this feed.");
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
