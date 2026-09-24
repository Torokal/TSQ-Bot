using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Modules.Esports.Providers.Liquipedia;

/// <summary>Section "Esports:Liquipedia". ApiKey is a secret (env var / user-secrets only).</summary>
public sealed class LiquipediaOptions
{
    public string BaseUrl { get; set; } = "https://api.liquipedia.net/api/v3/";
    public string Wiki { get; set; } = "counterstrike";

    /// <summary>Extra LPDB condition restricting to CS2 (same as upstream BOT-Greg-v2 query).</summary>
    public string GameCondition { get; set; } = "[[game::cs2]]";

    public string? ApiKey { get; set; }

    /// <summary>
    /// REQUIRED for live access: "ToroSquadBot/&lt;version&gt; (&lt;your contact URL or e-mail&gt;)". Liquipedia's terms
    /// require contact information. Never reuse another operator's contact.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>Verified baseline limit from the API terms: 60 requests/hour (per table, see docs/PROVIDERS.md).</summary>
    public int RequestsPerHourPerTable { get; set; } = 60;

    /// <summary>We plan to use at most this share of the hourly budget.</summary>
    public double BudgetShare { get; set; } = 0.8;

    public int PageSize { get; set; } = 200;
    public int MaxPages { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxRetries { get; set; } = 2;
}

/// <summary>Token bucket per LPDB table so polling can never exceed the verified quota.</summary>
public sealed class RequestBudget(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (double Tokens, DateTimeOffset At)> _buckets = new();
    private readonly object _gate = new();

    public bool TryAcquire(string bucket, int perHour, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var capacity = Math.Max(1, perHour);
            var now = clock.GetUtcNow();
            var (tokens, at) = _buckets.GetValueOrDefault(bucket, (capacity, now));
            tokens = Math.Min(capacity, tokens + ((now - at).TotalHours * capacity));
            if (tokens >= 1)
            {
                _buckets[bucket] = (tokens - 1, now);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            _buckets[bucket] = (tokens, now);
            retryAfter = TimeSpan.FromHours((1 - tokens) / capacity);
            return false;
        }
    }
}

/// <summary>
/// LiquipediaDB v3 REST client. Distinguishes: success (incl. legitimately empty), auth failure, quota, timeout,
/// transport error, schema error and partial results. Pagination is bounded by MaxPages and stops if a page does not
/// advance. Retries only transient failures, a bounded number of times, with jittered backoff.
/// </summary>
public sealed class LiquipediaClient(HttpClient http, IOptions<LiquipediaOptions> options, RequestBudget budget, TimeProvider clock, ILogger<LiquipediaClient> logger)
{
    private readonly LiquipediaOptions _options = options.Value;

    public LiquipediaOptions Options => _options;

    public static string? ConfigurationProblem(LiquipediaOptions o, bool requireKey)
    {
        if (requireKey && string.IsNullOrWhiteSpace(o.ApiKey))
            return "Esports:Liquipedia:ApiKey is not set";
        if (string.IsNullOrWhiteSpace(o.UserAgent))
            return "Esports:Liquipedia:UserAgent is not set (must include operator contact)";
        if (!o.UserAgent.Contains('(', StringComparison.Ordinal) || !(o.UserAgent.Contains('@', StringComparison.Ordinal) || o.UserAgent.Contains("http", StringComparison.OrdinalIgnoreCase)))
            return "Esports:Liquipedia:UserAgent must contain contact info, e.g. 'ToroSquadBot/0.1 (https://example.org; ops@example.org)'";
        if (o.UserAgent.Contains("gmeinder", StringComparison.OrdinalIgnoreCase) || o.UserAgent.Contains("BOT-Greg", StringComparison.OrdinalIgnoreCase))
            return "Esports:Liquipedia:UserAgent must identify YOUR bot and contact, not the upstream developer's";
        return null;
    }

    public async Task<ProviderResult<IReadOnlyList<JsonElement>>> QueryAsync(string table, string conditions, string order, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        var warnings = new List<string>();
        string? previousFirst = null;
        var perHour = (int)Math.Floor(_options.RequestsPerHourPerTable * Math.Clamp(_options.BudgetShare, 0.1, 1.0));

        for (var page = 0; page < _options.MaxPages; page++)
        {
            if (!budget.TryAcquire("lpdb:" + table, perHour, out var wait))
            {
                return rows.Count > 0
                    ? ProviderResult<IReadOnlyList<JsonElement>>.PartialData(rows, "local request budget exhausted mid-pagination", clock.GetUtcNow(), warnings)
                    : ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.QuotaExceeded, "local request budget exhausted", clock.GetUtcNow(), wait);
            }

            var offset = page * _options.PageSize;
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{table}?wiki={Uri.EscapeDataString(_options.Wiki)}&limit={_options.PageSize}&offset={offset}&order={Uri.EscapeDataString(order)}&conditions={Uri.EscapeDataString(conditions)}");

            var response = await SendWithRetryAsync(url, cancellationToken);
            if (response.Failure is { } failure)
            {
                // Never degrade a failure into "empty": partial data is labelled as partial.
                return rows.Count > 0
                    ? ProviderResult<IReadOnlyList<JsonElement>>.PartialData(rows, $"page {page + 1} failed: {failure.Detail}", clock.GetUtcNow(), warnings)
                    : failure;
            }

            using var doc = response.Document!;
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.SchemaError, "response is not a JSON object", clock.GetUtcNow());
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Array && error.GetArrayLength() > 0)
                return ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.SchemaError, "api error: " + Truncate(error.GetRawText()), clock.GetUtcNow());
            if (root.TryGetProperty("warning", out var warning) && warning.ValueKind == JsonValueKind.Array)
                warnings.AddRange(warning.EnumerateArray().Select(w => Truncate(w.ToString())));
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.SchemaError, "missing 'result' array", clock.GetUtcNow());

            var pageRows = result.EnumerateArray().Select(e => e.Clone()).ToList();
            var first = pageRows.Count > 0 ? pageRows[0].GetRawText() : null;
            if (page > 0 && first is not null && first == previousFirst)
            {
                warnings.Add("pagination did not advance (offset ignored?)");
                return ProviderResult<IReadOnlyList<JsonElement>>.PartialData(rows, "pagination did not advance", clock.GetUtcNow(), warnings);
            }

            previousFirst = first;
            rows.AddRange(pageRows);
            if (pageRows.Count < _options.PageSize)
                return ProviderResult<IReadOnlyList<JsonElement>>.Ok(rows, clock.GetUtcNow(), warnings);
        }

        return ProviderResult<IReadOnlyList<JsonElement>>.PartialData(rows, $"stopped after {_options.MaxPages} pages (more data exists)", clock.GetUtcNow(), warnings);
    }

    private sealed record SendResult(JsonDocument? Document, ProviderResult<IReadOnlyList<JsonElement>>? Failure);

    private async Task<SendResult> SendWithRetryAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Apikey", _options.ApiKey);
                if (!string.IsNullOrWhiteSpace(_options.UserAgent))
                    request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var status = response.StatusCode;
                if (status == HttpStatusCode.OK)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    try
                    {
                        return new SendResult(await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token), null);
                    }
                    catch (JsonException ex)
                    {
                        return new SendResult(null, ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.SchemaError, "malformed JSON: " + ex.Message, clock.GetUtcNow()));
                    }
                }

                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new SendResult(null, ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.AuthFailed, $"HTTP {(int)status}", clock.GetUtcNow()));
                if (status == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(10);
                    return new SendResult(null, ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.QuotaExceeded, "HTTP 429", clock.GetUtcNow(), retryAfter));
                }

                if ((int)status >= 500 && attempt < _options.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                // 404 is NOT "no data": a wrong URL/table also answers 404.
                return new SendResult(null, ProviderResult<IReadOnlyList<JsonElement>>.Fail(
                    (int)status >= 500 ? ProviderOutcome.TransportError : ProviderOutcome.SchemaError, $"HTTP {(int)status}", clock.GetUtcNow()));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < _options.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                return new SendResult(null, ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.Timeout, $"timed out after {_options.TimeoutSeconds}s", clock.GetUtcNow()));
            }
            catch (HttpRequestException ex)
            {
                if (attempt < _options.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                logger.LogWarning("Liquipedia transport error: {Error}", ex.Message);
                return new SendResult(null, ProviderResult<IReadOnlyList<JsonElement>>.Fail(ProviderOutcome.TransportError, ex.GetType().Name, clock.GetUtcNow()));
            }
        }
    }

    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
#pragma warning disable CA5394 // jitter, not security relevant
        var ms = (500 * Math.Pow(2, attempt)) * (0.75 + (Random.Shared.NextDouble() * 0.5));
#pragma warning restore CA5394
        return Task.Delay(TimeSpan.FromMilliseconds(ms), clock, cancellationToken);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}

/// <summary>IEsportsDataProvider backed by LiquipediaDB (live API or the fixture handler — same code path).</summary>
public sealed class LiquipediaProvider(LiquipediaClient client, EsportsDataMode mode) : IEsportsDataProvider
{
    public string Id => LiquipediaParser.Source;

    /// <summary>Liquipedia offers no verified live status (VERIFIED: no live field in LPDB match2).</summary>
    public ProviderCapability Capabilities => ProviderCapability.Fixtures | ProviderCapability.Results | ProviderCapability.Tournaments | ProviderCapability.Teams;

    public bool IsConfigured => LiquipediaClient.ConfigurationProblem(client.Options, requireKey: mode.Mode == ProviderMode.Live) is null;

    public async Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken)
    {
        var problem = LiquipediaClient.ConfigurationProblem(client.Options, requireKey: mode.Mode == ProviderMode.Live);
        if (problem is not null)
            return ProviderResult<IReadOnlyList<EsportsMatch>>.Fail(ProviderOutcome.NotConfigured, problem, window.FromUtc);

        var conditions = Join(client.Options.GameCondition,
            $"[[date::>{Lpdb(window.FromUtc)}]]",
            $"[[date::<{Lpdb(window.ToUtc)}]]");
        var raw = await client.QueryAsync("match", conditions, "date ASC", cancellationToken);
        if (!raw.HasData)
            return raw.WithoutValue<IReadOnlyList<EsportsMatch>>();

        var warnings = new List<string>(raw.Warnings ?? []);
        var matches = raw.Value!.Select(e => LiquipediaParser.ParseMatch(e, client.Options.Wiki, warnings)).OfType<EsportsMatch>().ToList();
        return raw.Outcome == ProviderOutcome.Partial
            ? ProviderResult<IReadOnlyList<EsportsMatch>>.PartialData(matches, raw.Detail!, raw.At, warnings)
            : ProviderResult<IReadOnlyList<EsportsMatch>>.Ok(matches, raw.At, warnings);
    }

    public async Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var problem = LiquipediaClient.ConfigurationProblem(client.Options, requireKey: mode.Mode == ProviderMode.Live);
        if (problem is not null)
            return ProviderResult<IReadOnlyList<EsportsEvent>>.Fail(ProviderOutcome.NotConfigured, problem, DateTimeOffset.MinValue);

        var conditions = Join(client.Options.GameCondition,
            $"[[enddate::>{from:yyyy-MM-dd}]]",
            $"[[startdate::<{to:yyyy-MM-dd}]]");
        var raw = await client.QueryAsync("tournament", conditions, "startdate ASC", cancellationToken);
        if (!raw.HasData)
            return raw.WithoutValue<IReadOnlyList<EsportsEvent>>();
        var warnings = new List<string>(raw.Warnings ?? []);
        var events = raw.Value!.Select(e => LiquipediaParser.ParseEvent(e, client.Options.Wiki, warnings)).OfType<EsportsEvent>().ToList();
        return raw.Outcome == ProviderOutcome.Partial
            ? ProviderResult<IReadOnlyList<EsportsEvent>>.PartialData(events, raw.Detail!, raw.At, warnings)
            : ProviderResult<IReadOnlyList<EsportsEvent>>.Ok(events, raw.At, warnings);
    }

    private static string Lpdb(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Join(params string?[] parts) => string.Join(" AND ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
