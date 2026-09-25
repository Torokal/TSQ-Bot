using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Formula1.Providers;

/// <summary>Per-client request policy (timeouts, retries, budget buckets) for <see cref="F1JsonHttp"/>.</summary>
public sealed record F1HttpPolicy(
    string Name,
    string? UserAgent,
    int TimeoutSeconds,
    int MaxRetries,
    IReadOnlyList<(string Bucket, int Capacity, TimeSpan Period)> Budgets);

/// <summary>
/// GET-JSON helper shared by the F1 provider clients. Every HTTP attempt (retries included) spends a budget token, so a
/// provider's documented limits cannot be exceeded. Only transient failures (5xx, timeouts, transport errors) are retried,
/// a bounded number of times with jitter; 429 honours Retry-After and is never retried inline; 401/403 are auth failures
/// (not retried here); other 4xx are schema/contract errors — never "no data". <paramref name="notFoundIsEmpty"/> lets a
/// provider declare its documented "empty result" 404 body (OpenF1: {"detail":"No results found."}).
/// </summary>
public sealed class F1JsonHttp(HttpClient http, F1RequestBudget budget, TimeProvider clock, ILogger logger)
{
    public async Task<F1ProviderResult<JsonDocument>> GetAsync(string relativeUrl, F1HttpPolicy policy, string? bearerToken,
        Func<HttpStatusCode, string, bool>? notFoundIsEmpty, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            foreach (var (bucket, capacity, period) in policy.Budgets)
            {
                if (!budget.TryAcquire(bucket, capacity, period, out var wait))
                    return Fail(F1ProviderOutcome.QuotaExceeded, "local request budget exhausted (" + bucket + ")", wait);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(policy.TimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrWhiteSpace(policy.UserAgent))
                    request.Headers.TryAddWithoutValidation("User-Agent", policy.UserAgent);
                // Header auth only: a token never appears in a URL, log line or trace.
                if (!string.IsNullOrWhiteSpace(bearerToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var status = response.StatusCode;
                if (status == HttpStatusCode.OK)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    try
                    {
                        return F1ProviderResult<JsonDocument>.Ok(await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token), clock.GetUtcNow());
                    }
                    catch (JsonException ex)
                    {
                        return Fail(F1ProviderOutcome.SchemaError, "malformed JSON: " + ex.Message);
                    }
                }

                if (status == HttpStatusCode.NotFound && notFoundIsEmpty is not null)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (notFoundIsEmpty(status, body))
                        return F1ProviderResult<JsonDocument>.Ok(null, clock.GetUtcNow());
                }

                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return Fail(F1ProviderOutcome.AuthFailed, $"HTTP {(int)status}");
                if (status == HttpStatusCode.TooManyRequests)
                    return Fail(F1ProviderOutcome.QuotaExceeded, "HTTP 429", response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                if ((int)status >= 500 && attempt < policy.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                return Fail((int)status >= 500 ? F1ProviderOutcome.TransportError : F1ProviderOutcome.SchemaError, $"HTTP {(int)status}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < policy.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                return Fail(F1ProviderOutcome.Timeout, $"timed out after {policy.TimeoutSeconds}s");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < policy.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                logger.LogWarning("{Provider} transport error: {Error}", policy.Name, ex.GetType().Name);
                return Fail(F1ProviderOutcome.TransportError, ex.GetType().Name);
            }
        }
    }

    private F1ProviderResult<JsonDocument> Fail(F1ProviderOutcome outcome, string detail, TimeSpan? retryAfter = null) =>
        F1ProviderResult<JsonDocument>.Fail(outcome, detail, clock.GetUtcNow(), retryAfter);

    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
#pragma warning disable CA5394 // jitter, not security relevant
        var ms = 500 * Math.Pow(2, attempt) * (0.75 + (Random.Shared.NextDouble() * 0.5));
#pragma warning restore CA5394
        return Task.Delay(TimeSpan.FromMilliseconds(ms), clock, cancellationToken);
    }
}
