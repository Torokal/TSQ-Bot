using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Modules.Formula1.Providers.Jolpica;

/// <summary>
/// Section "Formula1:Jolpica". No credentials exist (token access is not rolled out). Documented limits
/// (docs/rate_limits.md, verified 2026-09-25): 4 requests/second burst, 500 requests/hour sustained, custom
/// User-Agent required. Terms (TERMS.md, 2025-08-27): non-commercial use, data CC BY-NC-SA 4.0.
/// </summary>
public sealed class JolpicaOptions
{
    public string BaseUrl { get; set; } = "https://api.jolpi.ca/ergast/f1/";

    /// <summary>"App/version" as Jolpica asks; defaults to the product token (no personal data).</summary>
    public string? UserAgent { get; set; }

    public int RequestsPerHour { get; set; } = 500;
    public int BurstPerSecond { get; set; } = 4;

    /// <summary>Share of the hourly limit TSQ Bot plans to use (Jolpica limits are per IP).</summary>
    public double BudgetShare { get; set; } = 0.5;

    public int TimeoutSeconds { get; set; } = 20;
    public int MaxRetries { get; set; } = 2;

    public int PlannedRequestsPerHour => (int)Math.Floor(RequestsPerHour * Math.Clamp(BudgetShare, 0.05, 1.0));

    public static string DefaultUserAgent => ProductInfo.UserAgentProduct + "/0.1 (+https://github.com/Torokal/TSQ-Bot)";

    public string? Problem()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Formula1:Jolpica:BaseUrl must be an https URL";
        if (RequestsPerHour < 1 || BurstPerSecond < 1)
            return "Formula1:Jolpica:RequestsPerHour and BurstPerSecond must be >= 1";
        return null;
    }
}

/// <summary>Jolpica-F1 REST client (Ergast-compatible routes). Commands never call it; only the background poller does.</summary>
public sealed class JolpicaClient(HttpClient http, IOptions<JolpicaOptions> options, F1RequestBudget budget, TimeProvider clock, ILogger<JolpicaClient> logger)
{
    public const string BudgetBucket = "jolpica";

    private readonly JolpicaOptions _options = options.Value;
    private readonly F1JsonHttp _json = new(http, budget, clock, logger);

    private F1HttpPolicy Policy => new("Jolpica", string.IsNullOrWhiteSpace(_options.UserAgent) ? JolpicaOptions.DefaultUserAgent : _options.UserAgent,
        _options.TimeoutSeconds, _options.MaxRetries,
        [
            (BudgetBucket + ":hour", _options.PlannedRequestsPerHour, TimeSpan.FromHours(1)),
            (BudgetBucket + ":second", _options.BurstPerSecond, TimeSpan.FromSeconds(1)),
        ]);

    public Task<F1ProviderResult<F1SeasonSchedule>> GetScheduleAsync(int season, CancellationToken ct) =>
        GetAsync(Inv($"{season}/races/?limit=100"), root => JolpicaParser.ParseSchedule(root, season), ct);

    public Task<F1ProviderResult<F1StandingsSnapshot>> GetStandingsAsync(F1StandingsKind kind, int season, CancellationToken ct) =>
        kind == F1StandingsKind.Drivers
            ? GetAsync(Inv($"{season}/driverstandings/?limit=100"), root => JolpicaParser.ParseDriverStandings(root, season), ct)
            : GetAsync(Inv($"{season}/constructorstandings/?limit=100"), root => JolpicaParser.ParseConstructorStandings(root, season), ct);

    private async Task<F1ProviderResult<T>> GetAsync<T>(string url, Func<JsonElement, T> parse, CancellationToken ct)
        where T : class
    {
        var response = await _json.GetAsync(url, Policy, null, null, ct);
        if (!response.HasData)
            return response.Succeeded ? F1ProviderResult<T>.Fail(F1ProviderOutcome.SchemaError, "empty body", response.At) : response.WithoutValue<T>();
        using var doc = response.Value!;
        try
        {
            return F1ProviderResult<T>.Ok(parse(doc.RootElement), response.At);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // A contract change is a schema error — never an empty calendar or table.
            return F1ProviderResult<T>.Fail(F1ProviderOutcome.SchemaError, "unexpected payload: " + ex.Message, response.At);
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

public sealed class JolpicaScheduleProvider(JolpicaClient client) : IF1ScheduleProvider
{
    public string Id => JolpicaParser.Source;
    public string AttributionKey => "f1.source.jolpica";
    public bool IsConfigured => true; // public API without credentials

    public Task<F1ProviderResult<F1SeasonSchedule>> GetScheduleAsync(int season, CancellationToken cancellationToken) =>
        client.GetScheduleAsync(season, cancellationToken);
}

public sealed class JolpicaStandingsProvider(JolpicaClient client) : IF1StandingsProvider
{
    public string Id => JolpicaParser.Source;
    public string AttributionKey => "f1.source.jolpica";
    public bool IsConfigured => true;

    public Task<F1ProviderResult<F1StandingsSnapshot>> GetStandingsAsync(F1StandingsKind kind, int season, CancellationToken cancellationToken) =>
        client.GetStandingsAsync(kind, season, cancellationToken);
}
