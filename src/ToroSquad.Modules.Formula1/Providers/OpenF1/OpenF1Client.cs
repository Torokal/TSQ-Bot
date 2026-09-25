using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Modules.Formula1.Providers.OpenF1;

/// <summary>
/// Section "Formula1:OpenF1". <see cref="Username"/>/<see cref="Password"/> are the operator's OpenF1 account (live data
/// is a paid sponsor tier). Both are secrets: user-secrets or TOROSQUAD_Formula1__OpenF1__Username / __Password only,
/// never repository files. Documented (verified 2026-09-25): token POST https://api.openf1.org/token (form fields
/// username, password; expires_in 3600 s); REST "Authorization: Bearer"; MQTT mqtt.openf1.org:8883 (TLS) with the
/// access token as MQTT password; free 3 req/s + 30 req/min, sponsor 6 req/s + 60 req/min; data CC BY-NC-SA 4.0;
/// "live" = 30 minutes before a session until 30 minutes after it ends.
/// </summary>
public sealed class OpenF1Options
{
    public string BaseUrl { get; set; } = "https://api.openf1.org/";
    public string TokenUrl { get; set; } = "https://api.openf1.org/token";
    public string MqttHost { get; set; } = "mqtt.openf1.org";
    public int MqttPort { get; set; } = 8883;

    public string? Username { get; set; }
    public string? Password { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Free tier: 30/min and 3/s. Sponsor tier: 60/min and 6/s (set these when the account is a sponsor).</summary>
    public int RequestsPerMinute { get; set; } = 30;
    public int RequestsPerSecond { get; set; } = 3;
    public double BudgetShare { get; set; } = 0.5;

    public int TimeoutSeconds { get; set; } = 20;
    public int MaxRetries { get; set; } = 2;

    /// <summary>OpenF1 treats data as live until this long after a session's end (then it is free historical data).</summary>
    public int LiveWindowAfterEndMinutes { get; set; } = 30;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public int PlannedRequestsPerMinute => Math.Max(1, (int)Math.Floor(RequestsPerMinute * Math.Clamp(BudgetShare, 0.05, 1.0)));

    public static string DefaultUserAgent => ProductInfo.UserAgentProduct + "/0.1 (+https://github.com/Torokal/TSQ-Bot)";

    public string? Problem(bool requireCredentials)
    {
        foreach (var url in new[] { BaseUrl, TokenUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return "Formula1:OpenF1:BaseUrl/TokenUrl must be https URLs";
        }

        if (MqttPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(MqttHost))
            return "Formula1:OpenF1:MqttHost/MqttPort invalid";
        if (RequestsPerMinute < 1 || RequestsPerSecond < 1)
            return "Formula1:OpenF1:RequestsPerMinute and RequestsPerSecond must be >= 1";
        if (requireCredentials && !HasCredentials)
            return "Formula1:OpenF1:Username/Password are not set: live session lifecycle is NOT_CONFIGURED (no start notifications)";
        return null;
    }
}

/// <summary>
/// OAuth2 password-grant token for OpenF1 live access. Cached until 5 minutes before expiry; invalidated on a 401.
/// The token and credentials are never logged.
/// </summary>
#pragma warning disable CA1001 // process-lifetime singleton; the semaphore needs no disposal
public sealed class OpenF1TokenProvider(IHttpClientFactory httpFactory, IOptions<OpenF1Options> options, TimeProvider clock, ILogger<OpenF1TokenProvider> logger)
{
    public const string HttpClientName = "openf1-token";

#pragma warning disable CA2213 // singleton for the process lifetime; nothing to release
    private readonly SemaphoreSlim _gate = new(1, 1);
#pragma warning restore CA2213
    private (string Token, DateTimeOffset ValidUntil)? _cached;

    public bool IsConfigured => options.Value.HasCredentials;

    public DateTimeOffset? ValidUntil => _cached?.ValidUntil;

    public void Invalidate() => _cached = null;

    public async Task<F1ProviderResult<string>> GetAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.HasCredentials)
            return F1ProviderResult<string>.Fail(F1ProviderOutcome.NotConfigured, "OpenF1 credentials not set", clock.GetUtcNow());

        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_cached is { } c && c.ValidUntil > now)
                return F1ProviderResult<string>.Ok(c.Token, now);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, o.TokenUrl)
            {
                Content = new FormUrlEncodedContent([new("username", o.Username!), new("password", o.Password!)]),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", o.UserAgent ?? OpenF1Options.DefaultUserAgent);
            try
            {
                using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                    return F1ProviderResult<string>.Fail(F1ProviderOutcome.AuthFailed, $"token endpoint HTTP {(int)response.StatusCode}", now);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return F1ProviderResult<string>.Fail(F1ProviderOutcome.QuotaExceeded, "token endpoint HTTP 429", now, response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                if (!response.IsSuccessStatusCode)
                    return F1ProviderResult<string>.Fail(F1ProviderOutcome.TransportError, $"token endpoint HTTP {(int)response.StatusCode}", now);

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var token = doc.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(token))
                    return F1ProviderResult<string>.Fail(F1ProviderOutcome.SchemaError, "token response without access_token", now);
                // expires_in is documented as a string ("3600"); accept a number too.
                var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.ValueKind switch
                {
                    JsonValueKind.Number when e.TryGetInt32(out var n) => n,
                    JsonValueKind.String when int.TryParse(e.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
                    _ => 3600,
                } : 3600;
                var validUntil = now + TimeSpan.FromSeconds(Math.Max(60, seconds - 300));
                _cached = (token, validUntil);
                logger.LogInformation("OpenF1 access token obtained (valid until {ValidUntil:u})", validUntil);
                return F1ProviderResult<string>.Ok(token, now);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return F1ProviderResult<string>.Fail(F1ProviderOutcome.Timeout, "token endpoint timeout", now);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                return F1ProviderResult<string>.Fail(F1ProviderOutcome.TransportError, "token endpoint: " + ex.GetType().Name, now);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

#pragma warning restore CA1001

/// <summary>
/// OpenF1 REST client. Uses the access token when credentials exist (higher limits, live window); otherwise public
/// historical access only. The per-season session list is cached briefly (it is used only for mapping).
/// </summary>
public sealed class OpenF1Client(HttpClient http, IOptions<OpenF1Options> options, OpenF1TokenProvider tokens, F1RequestBudget budget, TimeProvider clock, ILogger<OpenF1Client> logger)
{
    public const string BudgetBucket = "openf1";
    private static readonly TimeSpan SessionListTtl = TimeSpan.FromMinutes(30);

    private readonly OpenF1Options _options = options.Value;
    private readonly F1JsonHttp _json = new(http, budget, clock, logger);
    private readonly Dictionary<int, (IReadOnlyList<F1ProviderSession> Sessions, DateTimeOffset At)> _sessions = [];
    private readonly Lock _gate = new();

    public OpenF1Options Options => _options;

    private F1HttpPolicy Policy => new("OpenF1", _options.UserAgent ?? OpenF1Options.DefaultUserAgent, _options.TimeoutSeconds, _options.MaxRetries,
        [
            (BudgetBucket + ":minute", _options.PlannedRequestsPerMinute, TimeSpan.FromMinutes(1)),
            (BudgetBucket + ":second", _options.RequestsPerSecond, TimeSpan.FromSeconds(1)),
        ]);

    public async Task<F1ProviderResult<IReadOnlyList<F1ProviderSession>>> GetSessionsAsync(int season, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(season, out var cached) && clock.GetUtcNow() - cached.At < SessionListTtl)
                return F1ProviderResult<IReadOnlyList<F1ProviderSession>>.Ok(cached.Sessions, cached.At);
        }

        var result = await GetAsync(Inv($"v1/sessions?year={season}"), OpenF1Parser.ParseSessions, emptyValue: [], ct);
        if (result.HasData)
        {
            lock (_gate)
                _sessions[season] = (result.Value!, result.At);
        }

        return result;
    }

    public Task<F1ProviderResult<IReadOnlyList<F1LifecycleEvent>>> GetLifecycleEventsAsync(string sessionKey, CancellationToken ct) =>
        GetAsync(Inv($"v1/race_control?session_key={Uri.EscapeDataString(sessionKey)}&category=SessionStatus"), OpenF1Parser.ParseLifecycleEvents, emptyValue: [], ct);

    /// <summary>Raw result rows (null value = not published yet).</summary>
    public Task<F1ProviderResult<JsonDocument>> GetSessionResultAsync(string sessionKey, CancellationToken ct) =>
        GetDocumentAsync(Inv($"v1/session_result?session_key={Uri.EscapeDataString(sessionKey)}"), ct);

    public Task<F1ProviderResult<JsonDocument>> GetDriversAsync(string sessionKey, CancellationToken ct) =>
        GetDocumentAsync(Inv($"v1/drivers?session_key={Uri.EscapeDataString(sessionKey)}"), ct);

    private async Task<F1ProviderResult<JsonDocument>> GetDocumentAsync(string url, CancellationToken ct)
    {
        string? token = null;
        if (tokens.IsConfigured)
        {
            var t = await tokens.GetAsync(ct);
            token = t.Value; // without a token the request is simply unauthenticated (historical access)
        }

        var result = await _json.GetAsync(url, Policy, token, (_, body) => OpenF1Parser.IsDocumentedEmptyResult(body), ct);
        if (result.Outcome == F1ProviderOutcome.AuthFailed && token is not null)
            tokens.Invalidate();
        return result;
    }

    private async Task<F1ProviderResult<T>> GetAsync<T>(string url, Func<JsonElement, T> parse, T emptyValue, CancellationToken ct)
        where T : class
    {
        var response = await GetDocumentAsync(url, ct);
        if (!response.Succeeded)
            return response.WithoutValue<T>();
        if (response.Value is null)
            return F1ProviderResult<T>.Ok(emptyValue, response.At);
        using var doc = response.Value;
        try
        {
            return F1ProviderResult<T>.Ok(parse(doc.RootElement), response.At);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return F1ProviderResult<T>.Fail(F1ProviderOutcome.SchemaError, "unexpected payload: " + ex.Message, response.At);
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
