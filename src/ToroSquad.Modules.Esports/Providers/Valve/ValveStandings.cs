// The idea of reading Valve's published standings markdown comes from BOT-Greg-v2_API by Julius Gmeinder
// (https://github.com/julius-gmeinder/BOT-Greg-v2_API, commit 3898b4ebfd4ed26ec077f4167a780f1684d91331,
// Services/VrsService.cs), licensed under the GNU AGPL v3. Modified for TSQ Bot (formerly ToroSquad Bot): header-based column mapping,
// publication date taken from the file name, host allow-list, failure types instead of empty lists, bounded listing.
// See docs/PROVENANCE.md.
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Modules.Esports.Domain;

namespace ToroSquad.Modules.Esports.Providers.Valve;

public sealed class ValveStandingsOptions
{
    public string ListingBaseUrl { get; set; } = "https://api.github.com/repos/ValveSoftware/counter-strike_regional_standings/contents/live/";
    public string RepositoryUrl { get; set; } = "https://github.com/ValveSoftware/counter-strike_regional_standings";
    public string UserAgent { get; set; } = ProductInfo.UserAgentProduct;
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>Parses Valve's standings markdown tables. Columns are located by header name, not position.</summary>
public static partial class ValveStandingsParser
{
    public static (IReadOnlyList<RankingEntry> Entries, int SkippedRows) Parse(string markdown)
    {
        var entries = new List<RankingEntry>();
        var skipped = 0;
        int rankCol = -1, pointsCol = -1, nameCol = -1, rosterCol = -1;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|'))
                continue;
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();

            if (cells.Any(c => c.Equals("Standing", StringComparison.OrdinalIgnoreCase)) &&
                cells.Any(c => c.Equals("Team Name", StringComparison.OrdinalIgnoreCase)))
            {
                rankCol = Array.FindIndex(cells, c => c.Equals("Standing", StringComparison.OrdinalIgnoreCase));
                pointsCol = Array.FindIndex(cells, c => c.Equals("Points", StringComparison.OrdinalIgnoreCase));
                nameCol = Array.FindIndex(cells, c => c.Equals("Team Name", StringComparison.OrdinalIgnoreCase));
                rosterCol = Array.FindIndex(cells, c => c.Equals("Roster", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (rankCol < 0 || nameCol < 0 || cells.All(c => c.Length == 0 || c.All(ch => ch is ':' or '-')))
                continue;

            if (cells.Length <= Math.Max(rankCol, nameCol) ||
                !int.TryParse(cells[rankCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank) || rank <= 0 ||
                string.IsNullOrWhiteSpace(cells[nameCol]))
            {
                skipped++;
                continue;
            }

            var points = pointsCol >= 0 && pointsCol < cells.Length &&
                         double.TryParse(cells[pointsCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                ? (int)Math.Round(p)
                : 0;
            var roster = rosterCol >= 0 && rosterCol < cells.Length
                ? cells[rosterCol].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            entries.Add(new RankingEntry(rank, points, cells[nameCol], roster));
        }

        // The published files repeat the heading; de-duplicate by rank+name.
        var unique = entries.GroupBy(e => (e.Rank, e.TeamName)).Select(g => g.First()).OrderBy(e => e.Rank).ToList();
        return (unique, skipped);
    }

    /// <summary>standings_global_2026_09_07.md → 2026-09-07 (the standings date Valve publishes).</summary>
    public static DateOnly? DateFromFileName(string fileName)
    {
        var m = GlobalFilePattern().Match(fileName);
        if (!m.Success)
            return null;
        return DateOnly.TryParseExact($"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    [GeneratedRegex(@"^standings_global_(\d{4})[_-](\d{2})[_-](\d{2})\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex GlobalFilePattern();
}

/// <summary>Fetches the newest global standings file from Valve's public GitHub repository.</summary>
public sealed class ValveStandingsProvider(HttpClient http, IOptions<ValveStandingsOptions> options, TimeProvider clock) : IRankingsProvider
{
    private static readonly string[] AllowedRawHosts = ["raw.githubusercontent.com"];

    public string Id => "valve-vrs";
    public bool IsConfigured => true;

    public async Task<ProviderResult<RankingSnapshot>> GetLatestAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));

            (string Name, string DownloadUrl, string HtmlUrl, DateOnly Date)? latest = null;
            // Current year first; early in a year the newest file may still be in the previous year's folder.
            foreach (var year in new[] { now.Year, now.Year - 1 })
            {
                var listing = await GetAsync(o.ListingBaseUrl + year.ToString(CultureInfo.InvariantCulture), o.UserAgent, timeout.Token);
                if (listing.StatusCode == HttpStatusCode.NotFound)
                    continue;
                if (listing.Failure is { } failure)
                    return failure;

                using var doc = JsonDocument.Parse(listing.Body!);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return ProviderResult<RankingSnapshot>.Fail(ProviderOutcome.SchemaError, "listing is not an array", now);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var download = item.TryGetProperty("download_url", out var d) ? d.GetString() : null;
                    var html = item.TryGetProperty("html_url", out var h) ? h.GetString() : null;
                    if (name is null || download is null || ValveStandingsParser.DateFromFileName(name) is not { } date)
                        continue;
                    if (latest is null || date > latest.Value.Date)
                        latest = (name, download, html ?? o.RepositoryUrl, date);
                }

                if (latest is not null)
                    break;
            }

            if (latest is null)
                return ProviderResult<RankingSnapshot>.Fail(ProviderOutcome.SchemaError, "no standings_global file found", now);

            if (!Uri.TryCreate(latest.Value.DownloadUrl, UriKind.Absolute, out var rawUri) || rawUri.Scheme != Uri.UriSchemeHttps ||
                !AllowedRawHosts.Contains(rawUri.Host, StringComparer.OrdinalIgnoreCase) ||
                !rawUri.AbsolutePath.StartsWith("/ValveSoftware/counter-strike_regional_standings/", StringComparison.Ordinal))
                return ProviderResult<RankingSnapshot>.Fail(ProviderOutcome.SchemaError, "unexpected download host/path", now);

            var file = await GetAsync(rawUri.AbsoluteUri, o.UserAgent, timeout.Token);
            if (file.Failure is { } fileFailure)
                return fileFailure;

            var (entries, skipped) = ValveStandingsParser.Parse(file.Body!);
            if (entries.Count == 0)
                return ProviderResult<RankingSnapshot>.Fail(ProviderOutcome.SchemaError, "standings table not recognised", now);

            var snapshot = new RankingSnapshot(Id, "global", latest.Value.Date, clock.GetUtcNow(), latest.Value.HtmlUrl, entries);
            return skipped > 0
                ? ProviderResult<RankingSnapshot>.PartialData(snapshot, $"{skipped} malformed rows skipped", clock.GetUtcNow())
                : ProviderResult<RankingSnapshot>.Ok(snapshot, clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderResult<RankingSnapshot>.Fail(ProviderOutcome.Timeout, "timeout", now);
        }
        catch (HttpRequestException ex)
        {
            return ProviderResult<RankingSnapshot>.Fail(ProviderOutcome.TransportError, ex.GetType().Name, now);
        }
        catch (JsonException ex)
        {
            return ProviderResult<RankingSnapshot>.Fail(ProviderOutcome.SchemaError, "malformed listing: " + ex.Message, now);
        }
    }

    private sealed record Fetch(HttpStatusCode StatusCode, string? Body, ProviderResult<RankingSnapshot>? Failure);

    private async Task<Fetch> GetAsync(string url, string userAgent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var response = await http.SendAsync(request, ct);
        var now = clock.GetUtcNow();
        if (response.StatusCode == HttpStatusCode.OK)
            return new Fetch(response.StatusCode, await response.Content.ReadAsStringAsync(ct), null);
        var outcome = response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => ProviderOutcome.QuotaExceeded, // GitHub unauthenticated limit
            HttpStatusCode.NotFound => ProviderOutcome.SchemaError,
            _ when (int)response.StatusCode >= 500 => ProviderOutcome.TransportError,
            _ => ProviderOutcome.SchemaError,
        };
        return new Fetch(response.StatusCode, null, ProviderResult<RankingSnapshot>.Fail(outcome, $"HTTP {(int)response.StatusCode}", now, response.Headers.RetryAfter?.Delta));
    }
}
