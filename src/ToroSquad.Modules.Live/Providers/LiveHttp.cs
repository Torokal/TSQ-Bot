using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Live.Providers;

public sealed record LiveHttpResponse(LiveProviderOutcome Outcome, JsonDocument? Document, string? Detail, TimeSpan? RetryAfter = null);

/// <summary>
/// GET-JSON helper of the live providers. Only transient failures (5xx, timeouts, transport errors) are retried, a bounded
/// number of times with jitter; 429 honours Retry-After / Ratelimit-Reset and is never retried inline; 401/403 are auth
/// failures (the caller refreshes its token once); other 4xx are contract errors. Tokens travel in headers only — never in
/// a URL, log line or error detail.
/// </summary>
public sealed class LiveHttp(HttpClient http, TimeProvider clock, ILogger logger)
{
    public async Task<LiveHttpResponse> GetJsonAsync(string relativeUrl, string provider, string? bearerToken, IReadOnlyList<(string Name, string Value)> headers,
        int timeoutSeconds, int maxRetries, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                foreach (var (name, value) in headers)
                    request.Headers.TryAddWithoutValidation(name, value);
                if (!string.IsNullOrWhiteSpace(bearerToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var status = response.StatusCode;
                if (status == HttpStatusCode.OK)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    try
                    {
                        return new(LiveProviderOutcome.Ok, await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token), null);
                    }
                    catch (JsonException)
                    {
                        return new(LiveProviderOutcome.SchemaError, null, "malformed JSON");
                    }
                }

                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new(LiveProviderOutcome.AuthFailed, null, $"HTTP {(int)status}");
                if (status == HttpStatusCode.TooManyRequests)
                    return new(LiveProviderOutcome.RateLimited, null, "HTTP 429", RetryAfter(response));
                if ((int)status >= 500 && attempt < maxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                return new((int)status >= 500 ? LiveProviderOutcome.TransportError : LiveProviderOutcome.SchemaError, null, $"HTTP {(int)status}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < maxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                return new(LiveProviderOutcome.Timeout, null, $"timed out after {timeoutSeconds}s");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < maxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                logger.LogDebug("{Provider} transport error: {Error}", provider, ex.GetType().Name);
                return new(LiveProviderOutcome.TransportError, null, ex.GetType().Name);
            }
        }
    }

    /// <summary>Retry-After (seconds or date), else Twitch's Ratelimit-Reset (unix seconds), else one minute.</summary>
    public TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var now = clock.GetUtcNow();
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return Clamp(delta);
        if (response.Headers.RetryAfter?.Date is { } date)
            return Clamp(date - now);
        if (response.Headers.TryGetValues("Ratelimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
            return Clamp(DateTimeOffset.FromUnixTimeSeconds(reset) - now);
        return TimeSpan.FromMinutes(1);
    }

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : wait;

    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
#pragma warning disable CA5394 // jitter, not security relevant
        var ms = 500 * Math.Pow(2, attempt) * (0.75 + (Random.Shared.NextDouble() * 0.5));
#pragma warning restore CA5394
        return Task.Delay(TimeSpan.FromMilliseconds(ms), clock, cancellationToken);
    }
}

public sealed record LiveTokenResult(LiveProviderOutcome Outcome, string? Token, string? Detail, TimeSpan? RetryAfter = null);

/// <summary>
/// OAuth2 client-credentials (app access) token for Twitch or Kick: cached until 5 minutes before expiry, invalidated on a
/// 401, fetched by one caller at a time. The token and the client secret are never logged or returned in a detail text.
/// </summary>
#pragma warning disable CA1001 // process-lifetime singleton member; the semaphore needs no disposal
public sealed class ClientCredentialsTokens(HttpClient http, string provider, TimeProvider clock, ILogger logger)
{
#pragma warning disable CA2213 // lives as long as the process
    private readonly SemaphoreSlim _gate = new(1, 1);
#pragma warning restore CA2213
    private (string Token, DateTimeOffset ValidUntil)? _cached;

    public DateTimeOffset? ValidUntil => _cached?.ValidUntil;

    public LiveProviderOutcome? LastOutcome { get; private set; }

    public void Invalidate() => _cached = null;

    public async Task<LiveTokenResult> GetAsync(string tokenUrl, string clientId, string clientSecret, int timeoutSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_cached is { } c && c.ValidUntil > now)
                return new(LiveProviderOutcome.Ok, c.Token, null);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            {
                Content = new FormUrlEncodedContent([new("client_id", clientId), new("client_secret", clientSecret), new("grant_type", "client_credentials")]),
            };
            try
            {
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return Result(LiveProviderOutcome.AuthFailed, $"token endpoint HTTP {(int)response.StatusCode}");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return Result(LiveProviderOutcome.RateLimited, "token endpoint HTTP 429", response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                if (!response.IsSuccessStatusCode)
                    return Result(LiveProviderOutcome.TransportError, $"token endpoint HTTP {(int)response.StatusCode}");

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var root = doc.RootElement;
                // Kick wraps nothing; Twitch returns the fields at the top level. Accept both, and a "data" envelope.
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    root = data;
                var token = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(token))
                    return Result(LiveProviderOutcome.SchemaError, "token response without access_token");
                var seconds = root.TryGetProperty("expires_in", out var e) ? e.ValueKind switch
                {
                    JsonValueKind.Number when e.TryGetInt64(out var n) => n,
                    JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
                    _ => 3600,
                } : 3600;
                var validUntil = now + TimeSpan.FromSeconds(Math.Clamp(seconds - 300, 60, 60L * 24 * 3600));
                _cached = (token, validUntil);
                LastOutcome = LiveProviderOutcome.Ok;
                logger.LogInformation("{Provider} app access token obtained (valid until {ValidUntil:u})", provider, validUntil);
                return new(LiveProviderOutcome.Ok, token, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Result(LiveProviderOutcome.Timeout, "token endpoint timeout");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                return Result(LiveProviderOutcome.TransportError, "token endpoint: " + ex.GetType().Name);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Record(LiveProviderOutcome outcome) => LastOutcome = outcome;

    private LiveTokenResult Result(LiveProviderOutcome outcome, string detail, TimeSpan? retryAfter = null)
    {
        LastOutcome = outcome;
        return new(outcome, null, detail, retryAfter);
    }
}
#pragma warning restore CA1001

/// <summary>Small JSON helpers for provider parsers (missing/mistyped fields are null, never exceptions).</summary>
public static class LiveJson
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A string or a number rendered invariantly (Kick user ids are numbers, Twitch ids strings).</summary>
    public static string? Id(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number when v.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    public static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>RFC 3339 instant; placeholder dates (before 2000) are "not stated".</summary>
    public static DateTimeOffset? Instant(JsonElement e, string name) =>
        Str(e, name) is { Length: > 0 } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) &&
        t.Year >= 2000 ? t : null;
}
