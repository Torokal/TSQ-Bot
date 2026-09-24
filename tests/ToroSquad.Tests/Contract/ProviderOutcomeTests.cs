using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// Criterion 6/7: every failure kind stays distinguishable from "successfully empty"; pagination is complete and
/// bounded; quota is enforced locally.
/// </summary>
public sealed class ProviderOutcomeTests
{
    private static readonly MatchWindow Window = new(TestHost.T0.AddHours(-12), TestHost.T0.AddHours(48));

    private static (LiquipediaProvider Provider, StubHttpHandler Handler) Create(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond,
        Action<LiquipediaOptions>? configure = null, ProviderMode mode = ProviderMode.Live, TimeProvider? clock = null) =>
        Create(new StubHttpHandler(respond), configure, mode, clock);

    private static (LiquipediaProvider Provider, StubHttpHandler Handler) Create(StubHttpHandler handler,
        Action<LiquipediaOptions>? configure, ProviderMode mode, TimeProvider? clock)
    {
        var options = new LiquipediaOptions
        {
            ApiKey = "test-key-not-real",
            UserAgent = "ToroSquadBot-tests/0 (https://localhost; tests)",
            PageSize = 2,
            MaxPages = 5,
            MaxRetries = 1,
            TimeoutSeconds = 2,
        };
        configure?.Invoke(options);
        clock ??= TimeProvider.System; // real delays for retry/backoff paths (short)
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.liquipedia.test/api/v3/") };
        var client = new LiquipediaClient(http, Options.Create(options), new RequestBudget(clock), clock, NullLogger<LiquipediaClient>.Instance);
        return (new LiquipediaProvider(client, new EsportsDataMode(mode)), handler);
    }

    private static string Match(string id) => new JsonObject
    {
        ["match2id"] = id,
        ["pagename"] = "Cup/2026",
        ["date"] = "2026-09-24 18:00:00",
        ["finished"] = 0,
        ["match2opponents"] = new JsonArray(
            new JsonObject { ["type"] = "team", ["name"] = "A", ["teamtemplate"] = new JsonObject { ["page"] = "A", ["name"] = "A" } },
            new JsonObject { ["type"] = "team", ["name"] = "B", ["teamtemplate"] = new JsonObject { ["page"] = "B", ["name"] = "B" } }),
    }.ToJsonString();

    private static Func<HttpRequestMessage, int, Task<HttpResponseMessage>> Paged(int total)
    {
        var all = Enumerable.Range(1, total).Select(i => Match($"M{i}")).ToList();
        return (request, _) =>
        {
            var q = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var offset = int.Parse(q["offset"]!, CultureInfo.InvariantCulture);
            var limit = int.Parse(q["limit"]!, CultureInfo.InvariantCulture);
            var page = all.Skip(offset).Take(limit);
            return Task.FromResult(StubHttpHandler.Json("{\"result\":[" + string.Join(",", page) + "]}"));
        };
    }

    [Fact]
    public async Task Successful_empty_result_is_success_not_failure()
    {
        var (provider, _) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("{\"result\":[]}")));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Value.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ProviderOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.Unauthorized, ProviderOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderOutcome.QuotaExceeded)]
    [InlineData(HttpStatusCode.NotFound, ProviderOutcome.SchemaError)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderOutcome.TransportError)]
    public async Task Http_errors_are_never_an_empty_match_list(HttpStatusCode status, ProviderOutcome expected)
    {
        var (provider, _) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("{}", status)));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.Value.Should().BeNull();
        result.HasData.Should().BeFalse();
    }

    [Fact]
    public async Task Quota_response_carries_retry_after()
    {
        var (provider, _) = Create((_, _) =>
        {
            var response = StubHttpHandler.Json("{\"error\":[\"limit\"]}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
            return Task.FromResult(response);
        });
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(7));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"error\":[\"Invalid conditions\"]}")]
    [InlineData("{\"results\":[]}")]
    public async Task Malformed_or_error_bodies_are_schema_errors(string body)
    {
        var (provider, _) = Create((_, _) => Task.FromResult(StubHttpHandler.Json(body)));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.SchemaError);
    }

    [Fact]
    public async Task Transient_500_is_retried_a_bounded_number_of_times()
    {
        var (provider, handler) = Create((_, n) => Task.FromResult(n == 1
            ? StubHttpHandler.Json("{}", HttpStatusCode.BadGateway)
            : StubHttpHandler.Json("{\"result\":[" + Match("X") + "]}")));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Success);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Timeout_is_reported_as_timeout()
    {
        var (provider, handler) = Create(new StubHttpHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // honours the client timeout
            return StubHttpHandler.Json("{}");
        }), o => o.TimeoutSeconds = 1, ProviderMode.Live, TimeProvider.System);
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Timeout);
        handler.Requests.Should().HaveCount(2, "one retry, then give up");
    }

    [Fact]
    public async Task Retries_spend_request_budget_too()
    {
        var clock = new FakeTimeProvider(TestHost.T0);
        var (provider, handler) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("{}", HttpStatusCode.BadGateway)),
            o => { o.RequestsPerHourPerTable = 2; o.BudgetShare = 1; o.MaxRetries = 5; }, clock: clock);
        var task = provider.GetMatchesAsync(Window, CancellationToken.None);
        for (var i = 0; i < 20 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10)); // release retry back-off delays
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var result = await task;
        handler.Requests.Should().HaveCount(2, "the budget (2/h) caps attempts, retries included");
        result.Outcome.Should().Be(ProviderOutcome.QuotaExceeded);
    }

    [Fact]
    public async Task Pagination_fetches_all_pages_beyond_the_first_page_limit()
    {
        var (provider, handler) = Create(Paged(5));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Value!.Select(m => m.Key.Id).Should().Equal("M1", "M2", "M3", "M4", "M5");
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Pagination_is_bounded_and_reports_partial_data()
    {
        var (provider, handler) = Create(Paged(50), o => o.MaxPages = 3);
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Partial);
        result.Value.Should().HaveCount(6);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Pagination_that_does_not_advance_stops_instead_of_looping()
    {
        var (provider, handler) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("{\"result\":[" + Match("SAME1") + "," + Match("SAME2") + "]}")));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Partial);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Failure_on_later_page_returns_partial_not_success()
    {
        var (provider, _) = Create((r, n) => n == 1 ? Paged(10)(r, n) : Task.FromResult(StubHttpHandler.Json("{}", HttpStatusCode.Forbidden)));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Partial);
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task Local_request_budget_blocks_before_calling_the_api()
    {
        var clock = new FakeTimeProvider(TestHost.T0);
        var (provider, handler) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("{\"result\":[]}")),
            o => { o.RequestsPerHourPerTable = 3; o.BudgetShare = 1; }, clock: clock);
        for (var i = 0; i < 3; i++)
            (await provider.GetMatchesAsync(Window, CancellationToken.None)).Outcome.Should().Be(ProviderOutcome.Success);

        var blocked = await provider.GetMatchesAsync(Window, CancellationToken.None);
        blocked.Outcome.Should().Be(ProviderOutcome.QuotaExceeded);
        blocked.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
        handler.Requests.Should().HaveCount(3);

        clock.Advance(TimeSpan.FromMinutes(21));
        (await provider.GetMatchesAsync(Window, CancellationToken.None)).Outcome.Should().Be(ProviderOutcome.Success);
    }

    [Theory]
    [InlineData(null, "ua (https://x)", true)]
    [InlineData("key", null, true)]
    [InlineData("key", "NoContactUA/1.0", true)]
    [InlineData("key", "BOT-Greg-v2/1.0 (julius.gmeinder@proton.me)", true)]
    [InlineData("key", "ToroSquadBot/0.1 (https://example.org; ops@example.org)", false)]
    public void Live_mode_requires_key_and_own_contact_user_agent(string? key, string? ua, bool problem)
    {
        LiquipediaClient.ConfigurationProblem(new LiquipediaOptions { ApiKey = key, UserAgent = ua }, requireKey: true)
            .Should().Match(p => (p != null) == problem);
    }

    [Fact]
    public async Task Not_configured_live_provider_returns_not_configured_without_network()
    {
        var (provider, handler) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("{\"result\":[]}")), o => o.ApiKey = null);
        provider.IsConfigured.Should().BeFalse();
        (await provider.GetMatchesAsync(Window, CancellationToken.None)).Outcome.Should().Be(ProviderOutcome.NotConfigured);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Requests_carry_api_key_user_agent_gzip_and_cs2_window_conditions()
    {
        HttpRequestMessage? seen = null;
        var (provider, _) = Create((r, _) =>
        {
            seen = r;
            return Task.FromResult(StubHttpHandler.Json("{\"result\":[]}"));
        });
        await provider.GetMatchesAsync(Window, CancellationToken.None);
        seen!.Headers.Authorization!.Scheme.Should().Be("Apikey");
        seen.Headers.UserAgent.ToString().Should().Contain("ToroSquadBot-tests");
        seen.Headers.AcceptEncoding.ToString().Should().Contain("gzip");
        var conditions = HttpUtility.ParseQueryString(seen.RequestUri!.Query)["conditions"]!;
        conditions.Should().Contain("[[game::cs2]]").And.Contain("[[date::>2026-09-24 00:00:00]]").And.Contain("[[date::<2026-09-26 12:00:00]]");
        conditions.Should().NotContain("finished", "the window includes started-but-unfinished and finished matches");
    }

    [Fact]
    public void Provider_does_not_claim_verified_live_status()
    {
        var (provider, _) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("{\"result\":[]}")));
        (provider.Capabilities & ProviderCapability.VerifiedLiveStatus).Should().Be(ProviderCapability.None);
    }
}
