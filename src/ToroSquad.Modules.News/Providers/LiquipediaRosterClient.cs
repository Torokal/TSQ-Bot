using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.News.Application;

namespace ToroSquad.Modules.News.Providers;

public enum RosterOutcome
{
    Ok = 0,
    RateLimited = 1,
    Failed = 2,

    /// <summary>The page answered but no plausible Active squad was found (never an empty roster).</summary>
    SchemaError = 3,
}

public sealed record RosterFetchResult(RosterOutcome Outcome, IReadOnlyList<string> Players, string? Detail, TimeSpan? RetryAfter = null);

/// <summary>
/// The current players of the target team from its Liquipedia page (official MediaWiki API, read-only, one request at most
/// once per refresh period, contact User-Agent, gzip). Only the "===Active===" squad of the first (CS2) roster tab is read,
/// and only people without a staff role. A failure never empties the stored roster (the caller keeps the last good one).
/// Liquipedia content is CC BY-SA; the names are used for matching only and are not shown.
/// </summary>
public sealed partial class LiquipediaRosterClient(IHttpClientFactory factory, IOptions<NewsOptions> options, ILogger<LiquipediaRosterClient> logger)
{
    public const string HttpClientName = "news-liquipedia";
    public const string ApiUrl = "https://liquipedia.net/counterstrike/api.php";
    public const int MaxBytes = 2 * 1024 * 1024;

    public async Task<RosterFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var url = ApiUrl + "?action=query&prop=revisions&rvprop=content&rvslots=main&format=json&formatversion=2&titles=" +
                  Uri.EscapeDataString(o.Roster.LiquipediaPage);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.RequestTimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.UserAgent.ParseAdd(o.UserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await factory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(RosterOutcome.RateLimited, [], "HTTP 429", response.Headers.RetryAfter?.Delta ?? TimeSpan.FromHours(6));
            if (response.StatusCode != HttpStatusCode.OK)
                return new(RosterOutcome.Failed, [], $"HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, timeout.Token);
            if (buffer.Length > MaxBytes)
                return new(RosterOutcome.Failed, [], "response too large");
            buffer.Position = 0;
            string? content;
            try
            {
                using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
                content = WikitextOf(json.RootElement);
            }
            catch (JsonException)
            {
                return new(RosterOutcome.SchemaError, [], "malformed JSON");
            }

            if (content is null)
                return new(RosterOutcome.SchemaError, [], "page or revision missing");
            var players = ParseActivePlayers(content);
            return players.Count is >= 3 and <= 10
                ? new(RosterOutcome.Ok, players, null)
                : new(RosterOutcome.SchemaError, [], $"implausible Active squad ({players.Count} players)");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(RosterOutcome.Failed, [], "timeout");
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("news roster transport error: {Error}", ex.GetType().Name);
            return new(RosterOutcome.Failed, [], ex.HttpRequestError.ToString());
        }
    }

    private static string? WikitextOf(JsonElement root)
    {
        if (!root.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var page in pages.EnumerateArray())
        {
            if (page.TryGetProperty("revisions", out var revisions) && revisions.ValueKind == JsonValueKind.Array && revisions.GetArrayLength() > 0 &&
                revisions[0].TryGetProperty("slots", out var slots) && slots.TryGetProperty("main", out var main) &&
                main.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
        }

        return null;
    }

    /// <summary>
    /// Player ids of the first "===Active===" section: {{Person|…|id=NAME|…}} entries without a role (role=Coach, Analyst,
    /// Manager … are staff). Distinct, in page order.
    /// </summary>
    public static IReadOnlyList<string> ParseActivePlayers(string wikitext)
    {
        var start = ActiveHeading().Match(wikitext);
        if (!start.Success)
            return [];
        var rest = wikitext[(start.Index + start.Length)..];
        var end = NextHeading().Match(rest);
        var section = end.Success ? rest[..end.Index] : rest;
        var players = new List<string>();
        foreach (Match person in PersonTemplate().Matches(section))
        {
            var body = RefTag().Replace(person.Groups[1].Value, "");
            string? id = null;
            var staff = false;
            foreach (var part in body.Split('|'))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                    continue;
                var key = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim();
                if (key == "id")
                    id = value;
                else if (key == "role" && value.Length > 0)
                    staff = true;
            }

            if (!staff && id is { Length: >= 2 and <= 32 } && !players.Contains(id, StringComparer.OrdinalIgnoreCase))
                players.Add(id);
        }

        return players;
    }

    [GeneratedRegex(@"^===\s*Active\s*===\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveHeading();

    [GeneratedRegex(@"^==", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NextHeading();

    [GeneratedRegex(@"\{\{Person\|([^{}]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PersonTemplate();

    [GeneratedRegex(@"<ref\b[^>]*/>|<ref\b[^>]*>.*?</ref>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RefTag();
}
