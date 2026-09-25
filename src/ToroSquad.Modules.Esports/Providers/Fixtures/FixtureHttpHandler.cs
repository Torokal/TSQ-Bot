using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace ToroSquad.Modules.Esports.Providers.Fixtures;

/// <summary>Match data provider selected by Esports:Provider:Name.</summary>
public enum MatchProviderName
{
    PandaScore = 0,
    Liquipedia = 1,
}

/// <summary>Esports:Provider:Mode. Fixture is the safe local default; Live needs the selected provider's credentials.</summary>
public enum ProviderMode
{
    Fixture = 0,
    Live = 1,
}

/// <summary>The resolved data mode; <see cref="IsDemo"/> drives the visible TEST/DEMO label on every message.</summary>
public sealed record EsportsDataMode(ProviderMode Mode)
{
    public bool IsDemo => Mode == ProviderMode.Fixture;
}

/// <summary>
/// Serves PandaScore-, LiquipediaDB- and GitHub-shaped responses from embedded fixture files so that fixture mode runs the
/// real HTTP client, pagination and parsers. Placeholders like {{T+00:30}} / {{T-02:00}} / {{D+3}} are rebased on a
/// <see cref="FixtureAnchor"/> (kept across restarts for up to a day) when one is supplied, otherwise on the current clock.
/// Synthetic data only — no copied third-party content.
/// </summary>
public sealed partial class FixtureHttpHandler(TimeProvider clock, IFixtureSource? source = null, FixtureAnchor? anchor = null) : HttpMessageHandler
{
    private readonly IFixtureSource _source = source ?? new EmbeddedFixtureSource();

    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestCount++;
        var now = anchor is null ? clock.GetUtcNow() : await anchor.GetAsync(cancellationToken);
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;

        if (path.EndsWith("/match", StringComparison.Ordinal) || path.EndsWith("/tournament", StringComparison.Ordinal))
        {
            var table = path.EndsWith("/match", StringComparison.Ordinal) ? "liquipedia-matches.json" : "liquipedia-tournaments.json";
            var query = HttpUtility.ParseQueryString(uri.Query);
            var offset = int.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
            var limit = int.Parse(query["limit"] ?? "20", CultureInfo.InvariantCulture);
            var all = JsonNode.Parse(Rebase(_source.Read(table), now))!.AsArray();
            var page = new JsonArray(all.Skip(offset).Take(limit).Select(n => n?.DeepClone()).ToArray());
            return Json(new JsonObject { ["result"] = page }.ToJsonString());
        }

        if (uri.Host == "api.pandascore.co" && (path.EndsWith("/matches", StringComparison.Ordinal) || path.EndsWith("/tournaments", StringComparison.Ordinal)))
        {
            // PandaScore shape: a bare JSON array per page (page[number] from 1, page[size] <= 100) and an X-Total header.
            var file = path.EndsWith("/matches", StringComparison.Ordinal) ? "pandascore-matches.json" : "pandascore-tournaments.json";
            var query = HttpUtility.ParseQueryString(uri.Query);
            var number = int.Parse(query["page[number]"] ?? "1", CultureInfo.InvariantCulture);
            var size = int.Parse(query["page[size]"] ?? "50", CultureInfo.InvariantCulture);
            var all = JsonNode.Parse(Rebase(_source.Read(file), now, iso: true))!.AsArray();
            var page = new JsonArray(all.Skip((number - 1) * size).Take(size).Select(n => n?.DeepClone()).ToArray());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page.ToJsonString(), Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("X-Total", all.Count.ToString(CultureInfo.InvariantCulture));
            return response;
        }

        if (uri.Host == "api.github.com" && path.Contains("/contents/live/", StringComparison.Ordinal))
        {
            var year = path[(path.LastIndexOf('/') + 1)..];
            var date = clock.GetUtcNow().AddDays(-10);
            if (year != date.Year.ToString(CultureInfo.InvariantCulture))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var name = $"standings_global_{date:yyyy_MM_dd}.md";
            var listing = new JsonArray(new JsonObject
            {
                ["name"] = name,
                ["download_url"] = $"https://raw.githubusercontent.com/ValveSoftware/counter-strike_regional_standings/main/live/{year}/{name}",
                ["html_url"] = $"https://github.com/ValveSoftware/counter-strike_regional_standings/blob/main/live/{year}/{name}",
            });
            return Json(listing.ToJsonString());
        }

        if (uri.Host == "raw.githubusercontent.com")
            return Text(_source.Read("vrs-global.md"));

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Rebase(string json, DateTimeOffset now, bool iso = false) =>
        Placeholder().Replace(json, m =>
        {
            var sign = m.Groups[2].Value == "-" ? -1 : 1;
            if (m.Groups[1].Value == "D")
                return now.AddDays(sign * int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var offset = TimeSpan.ParseExact(m.Groups[3].Value, @"hh\:mm", CultureInfo.InvariantCulture);
            var at = now + (sign * offset);
            at = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);
            return at.ToString(iso ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    [GeneratedRegex(@"\{\{([TD])([+-])([0-9:]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

/// <summary>
/// The moment demo placeholders are rebased on. Rebasing on "now" at every poll made the demo match always 20 minutes
/// away (a reminder never became due); anchoring to the process start moved every demo time on each restart, so the
/// planner saw a new time and posted a new TEST/DEMO "time changed" card per restart (both found live, 2026-09-25).
/// The anchor is therefore persisted (<see cref="IFixtureAnchorStore"/>) and reused for up to <see cref="MaxAge"/>;
/// after that the demo timeline is renewed once, otherwise every demo match would lie in the past.
/// </summary>
public sealed class FixtureAnchor(TimeProvider clock, IFixtureAnchorStore? store = null)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private Task<DateTimeOffset>? _anchor;

    /// <summary>Resolved once per process (concurrent callers share the same task; a cancelled attempt is retried).</summary>
    public Task<DateTimeOffset> GetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_anchor is null || _anchor.IsFaulted || _anchor.IsCanceled)
                _anchor = ResolveAsync(cancellationToken);
            return _anchor;
        }
    }

    private async Task<DateTimeOffset> ResolveAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var stored = store is null ? null : await store.LoadAsync(cancellationToken);
        if (stored is { } s && s <= now && now - s < MaxAge)
            return s;
        if (store is not null)
            await store.SaveAsync(now, cancellationToken);
        return now;
    }
}

/// <summary>Where the fixture anchor survives restarts. Implementations never throw (a failure means "no anchor").</summary>
public interface IFixtureAnchorStore
{
    Task<DateTimeOffset?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(DateTimeOffset anchor, CancellationToken cancellationToken);
}

public interface IFixtureSource
{
    string Read(string name);
}

public sealed class EmbeddedFixtureSource : IFixtureSource
{
    public string Read(string name)
    {
        var resource = $"ToroSquad.Modules.Esports.Fixtures.{name}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing fixture {resource}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>Reads fixtures from a directory (used by contract tests with fixed-date fixtures).</summary>
public sealed class DirectoryFixtureSource(string directory) : IFixtureSource
{
    public string Read(string name) => File.ReadAllText(Path.Combine(directory, name), Encoding.UTF8);
}
