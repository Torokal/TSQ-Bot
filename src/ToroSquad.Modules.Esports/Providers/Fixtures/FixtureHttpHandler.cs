using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace ToroSquad.Modules.Esports.Providers.Fixtures;

/// <summary>Esports:Provider:Mode. Fixture is the safe local default; Live needs an approved Liquipedia key.</summary>
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
/// Serves LiquipediaDB-shaped and GitHub-shaped responses from embedded fixture files so that fixture mode runs the
/// real HTTP client, pagination and parsers. Placeholders like {{T+00:30}} / {{T-02:00}} / {{D+3}} are rebased on the
/// current clock so a demo always has upcoming matches. Synthetic data only — no copied third-party content.
/// </summary>
public sealed partial class FixtureHttpHandler(TimeProvider clock, IFixtureSource? source = null) : HttpMessageHandler
{
    private readonly IFixtureSource _source = source ?? new EmbeddedFixtureSource();

    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestCount++;
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;

        if (path.EndsWith("/match", StringComparison.Ordinal) || path.EndsWith("/tournament", StringComparison.Ordinal))
        {
            var table = path.EndsWith("/match", StringComparison.Ordinal) ? "liquipedia-matches.json" : "liquipedia-tournaments.json";
            var query = HttpUtility.ParseQueryString(uri.Query);
            var offset = int.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
            var limit = int.Parse(query["limit"] ?? "20", CultureInfo.InvariantCulture);
            var all = JsonNode.Parse(Rebase(_source.Read(table)))!.AsArray();
            var page = new JsonArray(all.Skip(offset).Take(limit).Select(n => n?.DeepClone()).ToArray());
            return Json(new JsonObject { ["result"] = page }.ToJsonString());
        }

        if (uri.Host == "api.github.com" && path.Contains("/contents/live/", StringComparison.Ordinal))
        {
            var year = path[(path.LastIndexOf('/') + 1)..];
            var date = clock.GetUtcNow().AddDays(-10);
            if (year != date.Year.ToString(CultureInfo.InvariantCulture))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private string Rebase(string json)
    {
        var now = clock.GetUtcNow();
        return Placeholder().Replace(json, m =>
        {
            var sign = m.Groups[2].Value == "-" ? -1 : 1;
            if (m.Groups[1].Value == "D")
                return now.AddDays(sign * int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var offset = TimeSpan.ParseExact(m.Groups[3].Value, @"hh\:mm", CultureInfo.InvariantCulture);
            var at = now + (sign * offset);
            at = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);
            return at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        });
    }

    private static Task<HttpResponseMessage> Json(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    private static Task<HttpResponseMessage> Text(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });

    [GeneratedRegex(@"\{\{([TD])([+-])([0-9:]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
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
