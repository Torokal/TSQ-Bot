using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Esports.Providers.Liquipedia;

namespace ToroSquad.Modules.Esports.Providers.PandaScore;

/// <summary>
/// Section "PandaScore". <see cref="Token"/> is a secret: user-secrets / TOROSQUAD_PandaScore__Token only, never in
/// repository files. Limits verified against developers.pandascore.co on 2026-09-25 (docs/PROVIDERS.md).
/// </summary>
public sealed class PandaScoreOptions
{
    public const string Section = "PandaScore";

    public string BaseUrl { get; set; } = "https://api.pandascore.co/";

    /// <summary>All CS2 endpoints use the legacy "/csgo/" prefix (PandaScore CS2 migration guide).</summary>
    public string Game { get; set; } = "csgo";

    public string? Token { get; set; }

    /// <summary>Free "Schedules, Results &amp; Context Data" plan: 1,000 requests/hour (rate-and-connections-limits).</summary>
    public int RequestsPerHour { get; set; } = 1000;

    /// <summary>Share of the hourly limit TSQ Bot plans to use (the rest is headroom for retries and manual tools).</summary>
    public double BudgetShare { get; set; } = 0.5;

    /// <summary>Documented maximum page size is 100.</summary>
    public int PageSize { get; set; } = 100;

    public int MaxPages { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxRetries { get; set; } = 2;

    public int PlannedRequestsPerHour => (int)Math.Floor(RequestsPerHour * Math.Clamp(BudgetShare, 0.05, 1.0));

    public static string? ConfigurationProblem(PandaScoreOptions o, bool requireToken)
    {
        if (requireToken && string.IsNullOrWhiteSpace(o.Token))
            return "PandaScore:Token is not set (user-secrets: dotnet user-secrets set \"PandaScore:Token\" \"<TOKEN>\" --project src\\ToroSquad.Bot)";
        if (o.PageSize is < 1 or > 100)
            return "PandaScore:PageSize must be 1..100";
        if (o.MaxPages < 1)
            return "PandaScore:MaxPages must be >= 1";
        return null;
    }
}

/// <summary>
/// PandaScore REST client for list endpoints (JSON array bodies). Keeps success-with-zero-items apart from every
/// failure kind, paginates with a hard page limit, retries only transient failures, and spends one local budget token
/// per HTTP attempt (retries included) so the documented hourly limit can never be exceeded by TSQ Bot.
/// </summary>
public sealed class PandaScoreClient(HttpClient http, IOptions<PandaScoreOptions> options, RequestBudget budget, TimeProvider clock, ILogger<PandaScoreClient> logger)
{
    public const string BudgetBucket = "pandascore";

    private readonly PandaScoreOptions _options = options.Value;

    public PandaScoreOptions Options => _options;

    /// <summary>Last X-Rate-Limit-Remaining value PandaScore reported (diagnostics only).</summary>
    public int? LastRateLimitRemaining { get; private set; }

    public async Task<ProviderResult<IReadOnlyList<JsonElement>>> ListAsync(string path, IReadOnlyList<KeyValuePair<string, string>> query, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        var warnings = new List<string>();
        string? previousFirst = null;

        for (var page = 1; page <= _options.MaxPages; page++)
        {
            var parts = query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}")
                .Append(string.Create(CultureInfo.InvariantCulture, $"page%5Bnumber%5D={page}"))
                .Append(string.Create(CultureInfo.InvariantCulture, $"page%5Bsize%5D={_options.PageSize}"));
            var url = path + "?" + string.Join("&", parts);

            var response = await SendWithRetryAsync(url, cancellationToken);
            if (response.Failure is { } failure)
            {
                return rows.Count > 0
                    ? ProviderResult<IReadOnlyList<JsonElement>>.PartialData(rows, $"page {page} failed: {failure.Detail}", clock.GetUtcNow(), warnings)
                    : failure;
            }

            using var doc = response.Document!;
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.SchemaError, "response is not a JSON array", clock.GetUtcNow());

            var pageRows = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            var first = pageRows.Count > 0 ? pageRows[0].GetRawText() : null;
            if (page > 1 && first is not null && first == previousFirst)
            {
                warnings.Add("pagination did not advance");
                return ProviderResult<IReadOnlyList<JsonElement>>.PartialData(rows, "pagination did not advance", clock.GetUtcNow(), warnings);
            }

            previousFirst = first;
            rows.AddRange(pageRows);
            var reportedTotal = response.Total;
            if (pageRows.Count < _options.PageSize || (reportedTotal is { } total && rows.Count >= total))
                return ProviderResult<IReadOnlyList<JsonElement>>.Ok(rows, clock.GetUtcNow(), warnings);
        }

        return ProviderResult<IReadOnlyList<JsonElement>>.PartialData(rows, $"stopped after {_options.MaxPages} pages (more data exists)", clock.GetUtcNow(), warnings);
    }

    private sealed record SendResult(JsonDocument? Document, int? Total, ProviderResult<IReadOnlyList<JsonElement>>? Failure);

    private async Task<SendResult> SendWithRetryAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!budget.TryAcquire(BudgetBucket, _options.PlannedRequestsPerHour, out var wait))
                return Fail(ProviderOutcome.QuotaExceeded, "local request budget exhausted", wait);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                // Header auth (documented alternative to ?token=): the token never appears in a URL, log line or trace.
                if (!string.IsNullOrWhiteSpace(_options.Token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
                LastRateLimitRemaining = Header(response, "X-Rate-Limit-Remaining") ?? LastRateLimitRemaining;
                var status = response.StatusCode;
                if (status == HttpStatusCode.OK)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    try
                    {
                        return new SendResult(await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token), Header(response, "X-Total"), null);
                    }
                    catch (JsonException ex)
                    {
                        return Fail(ProviderOutcome.SchemaError, "malformed JSON: " + ex.Message);
                    }
                }

                // 401 = missing/invalid token; 403 = endpoint not in the current plan. Neither is "no matches".
                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return Fail(ProviderOutcome.AuthFailed, $"HTTP {(int)status}");
                if (status == HttpStatusCode.TooManyRequests)
                    return Fail(ProviderOutcome.QuotaExceeded, "HTTP 429", response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(10));
                if ((int)status >= 500 && attempt < _options.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                // 404/400 mean a wrong path or query — a bug or API change, never an empty schedule.
                return Fail((int)status >= 500 ? ProviderOutcome.TransportError : ProviderOutcome.SchemaError, $"HTTP {(int)status}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < _options.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                return Fail(ProviderOutcome.Timeout, $"timed out after {_options.TimeoutSeconds}s");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < _options.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                logger.LogWarning("PandaScore transport error: {Error}", ex.Message);
                return Fail(ProviderOutcome.TransportError, ex.GetType().Name);
            }
        }
    }

    private SendResult Fail(ProviderOutcome outcome, string detail, TimeSpan? retryAfter = null) =>
        new(null, null, ProviderResult<IReadOnlyList<JsonElement>>.Fail(outcome, detail, clock.GetUtcNow(), retryAfter));

    private static int? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
#pragma warning disable CA5394 // jitter, not security relevant
        var ms = (500 * Math.Pow(2, attempt)) * (0.75 + (Random.Shared.NextDouble() * 0.5));
#pragma warning restore CA5394
        return Task.Delay(TimeSpan.FromMilliseconds(ms), clock, cancellationToken);
    }
}
