using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ToroSquad.Modules.Esports.Providers.Liquipedia;

/// <summary>
/// Read-only client for Liquipedia's free MediaWiki API (<c>https://liquipedia.net/&lt;wiki&gt;/api.php</c>), used only to
/// find editor-entered HLTV match ids when no LiquipediaDB key is available. Follows the API terms (verified 2026-09-26):
/// at most 1 request per <see cref="LiquipediaOptions.WikiMinIntervalSeconds"/> (≥ 2 s), a custom User-Agent with contact
/// information, gzip, and only <c>action=query</c> (no <c>action=parse</c>, no HTML pages). Every request also spends a
/// token from its own hourly budget. Only the configured Liquipedia host is ever contacted.
/// </summary>
public sealed class LiquipediaWikiClient
{
    /// <summary>Hourly request budget bucket of the MediaWiki API (separate from the LPDB bucket).</summary>
    public const string BudgetBucket = "liquipedia-wiki";

    private readonly HttpClient _http;
    private readonly LiquipediaOptions _options;
    private readonly RequestBudget _budget;
    private readonly TimeProvider _clock;
    private readonly ILogger<LiquipediaWikiClient> _logger;
    private readonly Lock _gate = new();
    private readonly List<DateTimeOffset> _sent = [];
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

    public LiquipediaWikiClient(HttpClient http, IOptions<LiquipediaOptions> options, RequestBudget budget, TimeProvider clock, ILogger<LiquipediaWikiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _budget = budget;
        _clock = clock;
        _logger = logger;
    }

    public LiquipediaOptions Options => _options;

    /// <summary>Send times of all requests (diagnostics and tests: proves the spacing).</summary>
    public IReadOnlyList<DateTimeOffset> SentAt
    {
        get
        {
            lock (_gate)
                return _sent.ToList();
        }
    }

    public static string? ConfigurationProblem(LiquipediaOptions o)
    {
        if (!Uri.TryCreate(o.WikiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Esports:Liquipedia:WikiBaseUrl must be an https URL";
        if (!string.Equals(uri.Host, "liquipedia.net", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
            return "Esports:Liquipedia:WikiBaseUrl must point to liquipedia.net";
        if (o.WikiMinIntervalSeconds < 2)
            return "Esports:Liquipedia:WikiMinIntervalSeconds must be >= 2 (MediaWiki API terms: 1 request per 2 seconds)";
        if (o.WikiRequestsPerHour is < 1 or > 1800)
            return "Esports:Liquipedia:WikiRequestsPerHour must be 1..1800";
        return null;
    }

    /// <summary>Full-text search (CirrusSearch) in the main and Match namespaces, most recently edited first. Returns page titles.</summary>
    public async Task<ProviderResult<IReadOnlyList<string>>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = Api(new()
        {
            ["action"] = "query",
            ["list"] = "search",
            ["srsearch"] = query,
            ["srnamespace"] = "0|130",
            ["srlimit"] = Math.Clamp(limit, 1, 10).ToString(CultureInfo.InvariantCulture),
            ["srsort"] = "last_edit_desc",
            ["srprop"] = "timestamp",
        });
        var sent = await SendAsync(url, cancellationToken);
        if (sent.Failure is { } failure)
            return failure.WithoutValue<IReadOnlyList<string>>();
        using var doc = sent.Document!;
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("search", out var search) || search.ValueKind != JsonValueKind.Array)
            return Schema<IReadOnlyList<string>>("search: missing query.search");
        var titles = search.EnumerateArray()
            .Select(e => e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
            .OfType<string>()
            .ToList();
        return ProviderResult<IReadOnlyList<string>>.Ok(titles, _clock.GetUtcNow());
    }

    /// <summary>Current wikitext of up to 5 pages in ONE request (<c>prop=revisions</c>, main slot).</summary>
    public async Task<ProviderResult<IReadOnlyList<(string Title, string Wikitext)>>> GetWikitextAsync(IReadOnlyList<string> titles, CancellationToken cancellationToken)
    {
        if (titles.Count == 0)
            return ProviderResult<IReadOnlyList<(string, string)>>.Ok([], _clock.GetUtcNow());
        var url = Api(new()
        {
            ["action"] = "query",
            ["prop"] = "revisions",
            ["rvprop"] = "content",
            ["rvslots"] = "main",
            ["titles"] = string.Join('|', titles.Take(5)),
        });
        var sent = await SendAsync(url, cancellationToken);
        if (sent.Failure is { } failure)
            return failure.WithoutValue<IReadOnlyList<(string, string)>>();
        using var doc = sent.Document!;
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Object)
            return Schema<IReadOnlyList<(string, string)>>("revisions: missing query.pages");
        var result = new List<(string, string)>();
        foreach (var page in pages.EnumerateObject())
        {
            var p = page.Value;
            if (!p.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String)
                continue;
            if (!p.TryGetProperty("revisions", out var revs) || revs.ValueKind != JsonValueKind.Array || revs.GetArrayLength() == 0)
                continue;
            var rev = revs[0];
            string? content = null;
            if (rev.TryGetProperty("slots", out var slots) && slots.TryGetProperty("main", out var main) && main.TryGetProperty("*", out var star) && star.ValueKind == JsonValueKind.String)
                content = star.GetString();
            else if (rev.TryGetProperty("*", out var legacy) && legacy.ValueKind == JsonValueKind.String)
                content = legacy.GetString();
            if (content is not null)
                result.Add((title.GetString()!, content));
        }

        return ProviderResult<IReadOnlyList<(string, string)>>.Ok(result, _clock.GetUtcNow());
    }

    private string Api(Dictionary<string, string> query)
    {
        query["format"] = "json";
        var qs = string.Join('&', query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        return $"{Uri.EscapeDataString(_options.Wiki)}/api.php?{qs}";
    }

    private sealed record Sent(JsonDocument? Document, ProviderResult<object>? Failure);

    private async Task<Sent> SendAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        // Hourly budget first (a refused request never waits), then the per-request spacing.
        if (!_budget.TryAcquire(BudgetBucket, _options.WikiRequestsPerHour, out var budgetWait))
            return new Sent(null, ProviderResult<object>.Fail(ProviderOutcome.QuotaExceeded, "local MediaWiki request budget exhausted", _clock.GetUtcNow(), budgetWait));

        TimeSpan wait;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var slot = _nextSlot > now ? _nextSlot : now;
            _nextSlot = slot + TimeSpan.FromSeconds(Math.Max(2, _options.WikiMinIntervalSeconds));
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _clock, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(_options.UserAgent) ? LiquipediaClient.DefaultUserAgent : _options.UserAgent);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            lock (_gate)
                _sent.Add(_clock.GetUtcNow());

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var status = response.StatusCode;
            if (status == HttpStatusCode.OK)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                try
                {
                    var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.TryGetProperty("error", out _))
                    {
                        doc.Dispose();
                        return new Sent(null, ProviderResult<object>.Fail(ProviderOutcome.SchemaError, "api error or non-object response", _clock.GetUtcNow()));
                    }

                    return new Sent(doc, null);
                }
                catch (JsonException ex)
                {
                    return new Sent(null, ProviderResult<object>.Fail(ProviderOutcome.SchemaError, "malformed JSON: " + ex.Message, _clock.GetUtcNow()));
                }
            }

            if (status == HttpStatusCode.TooManyRequests)
                return new Sent(null, ProviderResult<object>.Fail(ProviderOutcome.QuotaExceeded, "HTTP 429", _clock.GetUtcNow(), response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(30)));
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new Sent(null, ProviderResult<object>.Fail(ProviderOutcome.AuthFailed, $"HTTP {(int)status}", _clock.GetUtcNow()));
            return new Sent(null, ProviderResult<object>.Fail((int)status >= 500 ? ProviderOutcome.TransportError : ProviderOutcome.SchemaError, $"HTTP {(int)status}", _clock.GetUtcNow()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Sent(null, ProviderResult<object>.Fail(ProviderOutcome.Timeout, $"timed out after {_options.TimeoutSeconds}s", _clock.GetUtcNow()));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Liquipedia MediaWiki transport error: {Error}", ex.Message);
            return new Sent(null, ProviderResult<object>.Fail(ProviderOutcome.TransportError, ex.GetType().Name, _clock.GetUtcNow()));
        }
    }

    private ProviderResult<T> Schema<T>(string detail) => ProviderResult<T>.Fail(ProviderOutcome.SchemaError, detail, _clock.GetUtcNow());
}
