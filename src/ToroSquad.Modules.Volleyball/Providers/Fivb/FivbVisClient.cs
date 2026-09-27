using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ToroSquad.Modules.Volleyball.Providers.Fivb;

/// <summary>
/// HTTP access to the FIVB VIS web service: one GET per request (<c>XmlRequest.asmx?Request=&lt;Request …/&gt;</c>, JSON via
/// <c>Accept: application/json</c>). Every attempt (retries included) spends a local budget token. Transient failures
/// (5xx, timeouts, transport errors) are retried a bounded number of times with jitter; 429 honours Retry-After and is never
/// retried inline; 400 means VIS rejected our request (contract change) and is never "no data". The optional application id
/// travels in a header only — never in a URL or a log line.
/// </summary>
public sealed class FivbVisClient(HttpClient http, VbRequestBudget budget, IOptions<FivbVisOptions> options, TimeProvider clock, ILogger<FivbVisClient> logger)
{
    public const string Bucket = "fivb-vis";

    /// <summary>Discovery: every match of senior women's national-team tournaments in a date window (server-side filtered).</summary>
    public static string MatchListRequest(DateOnly first, DateOnly last, IEnumerable<string> tournamentTypes, long? version = null) =>
        Request(
            $"<Filter FirstDate=\"{first:yyyy-MM-dd}\" LastDate=\"{last:yyyy-MM-dd}\" TournamentGenders=\"W\" TournamentTypes=\"{Escape(string.Join(' ', tournamentTypes))}\" />",
            version);

    /// <summary>Live polling: exactly the given matches (incremental with <paramref name="version"/>).</summary>
    public static string LiveRequest(IEnumerable<string> matchNumbers, long? version = null) =>
        Request($"<Filter NoMatches=\"{Escape(string.Join(' ', matchNumbers))}\" />", version);

    private static string Request(string filter, long? version) =>
        string.Create(CultureInfo.InvariantCulture,
            $"<Request Type=\"GetVolleyMatchList\" Fields=\"{FivbVisParser.MatchFields}\"{(version is { } v and > 0 ? $" Version=\"{v}\"" : "")}>{filter}<Relation Name=\"Tournament\" Fields=\"{FivbVisParser.TournamentFields}\" /></Request>");

    private static string Escape(string value) => SecurityElement.Escape(value);

    public async Task<VbProviderResult<JsonDocument>> GetAsync(string request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var url = "XmlRequest.asmx?Request=" + Uri.EscapeDataString(request);
        for (var attempt = 0; ; attempt++)
        {
            if (!budget.TryAcquire(Bucket, o.RequestsPerMinute, TimeSpan.FromMinutes(1), out var wait))
                return Fail(VbProviderOutcome.RateLimited, "local request budget exhausted", wait);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                message.Headers.TryAddWithoutValidation("User-Agent", o.UserAgent);
                if (!string.IsNullOrWhiteSpace(o.AppId))
                    message.Headers.TryAddWithoutValidation("X-FIVB-App-ID", o.AppId);

                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var status = response.StatusCode;
                var json = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
                if (status == HttpStatusCode.OK)
                {
                    if (!json)
                        return Fail(VbProviderOutcome.SchemaChanged, "HTTP 200 without JSON (" + (response.Content.Headers.ContentType?.MediaType ?? "no content type") + ")");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    try
                    {
                        return VbProviderResult<JsonDocument>.Ok(await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token), clock.GetUtcNow());
                    }
                    catch (JsonException ex)
                    {
                        return Fail(VbProviderOutcome.SchemaChanged, "malformed JSON: " + ex.Message);
                    }
                }

                if (status == HttpStatusCode.BadRequest)
                    return Fail(VbProviderOutcome.SchemaChanged, "HTTP 400: VIS rejected the request");
                if (status == HttpStatusCode.Unauthorized)
                    return Fail(VbProviderOutcome.Unauthorized, "HTTP 401");
                if (status == HttpStatusCode.Forbidden)
                    return Fail(json ? VbProviderOutcome.Unauthorized : VbProviderOutcome.Unavailable, json ? "HTTP 403" : "HTTP 403 (blocked by the provider's edge)");
                if (status == HttpStatusCode.TooManyRequests)
                    return Fail(VbProviderOutcome.RateLimited, "HTTP 429", response.Headers.RetryAfter?.Delta ?? RetryAfterDate(response) ?? TimeSpan.FromMinutes(5));
                if ((int)status >= 500 && attempt < o.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                return Fail((int)status >= 500 ? VbProviderOutcome.Unavailable : VbProviderOutcome.SchemaChanged, $"HTTP {(int)status}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < o.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                return Fail(VbProviderOutcome.Timeout, $"timed out after {o.TimeoutSeconds}s");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < o.MaxRetries)
                {
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                logger.LogWarning("FIVB VIS transport error: {Error}", ex.GetType().Name);
                return Fail(VbProviderOutcome.TransportError, ex.GetType().Name);
            }
        }
    }

    private TimeSpan? RetryAfterDate(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Date is { } date && date > clock.GetUtcNow() ? date - clock.GetUtcNow() : null;

    private VbProviderResult<JsonDocument> Fail(VbProviderOutcome outcome, string detail, TimeSpan? retryAfter = null) =>
        VbProviderResult<JsonDocument>.Fail(outcome, detail, clock.GetUtcNow(), retryAfter);

    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
#pragma warning disable CA5394 // jitter, not security relevant
        var ms = 750 * Math.Pow(2, attempt) * (0.75 + (Random.Shared.NextDouble() * 0.5));
#pragma warning restore CA5394
        return Task.Delay(TimeSpan.FromMilliseconds(ms), clock, cancellationToken);
    }
}
