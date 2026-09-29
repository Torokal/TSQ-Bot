using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Infrastructure.Hosting;
using ToroSquad.Modules.Predictions;
using ToroSquad.Modules.Predictions.Application.Automation;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Providers.TheOddsApi;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// The Odds API client against a scripted HTTP handler — SYNTHETIC bodies shaped like the v4 documentation, never a
/// recorded provider response (this proves the parsing and the safety rules, not the provider's real coverage): the
/// documented paths and parameters (one region, h2h, decimal, eventIds), strict parsing with decimal prices, every failure
/// class (401/403, 429 with Retry-After, 5xx, timeout, broken JSON, wrong shape), the usage headers, no call without a key —
/// and the key (a query parameter, as the provider requires) never appearing in a log line, a result or an error.
/// </summary>
public sealed class TheOddsApiClientTests
{
    private const string Key = "synthetic-test-key-0123456789abcdef";
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be(TheOddsApiClient.HttpClientName);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(new AutoFootballOptions().BaseUrl) };
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    private static (TheOddsApiClient Client, List<HttpRequestMessage> Requests, ListLogger<TheOddsApiClient> Log) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, string? key = Key, FakeTimeProvider? clock = null)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new Handler((request, ct) =>
        {
            requests.Add(request);
            return respond(request, ct);
        });
        var log = new ListLogger<TheOddsApiClient>();
        var client = new TheOddsApiClient(new Factory(handler), new FootballOddsApiKey(key), Options.Create(new AutoFootballOptions { TimeoutSeconds = 5 }),
            clock ?? new FakeTimeProvider(T0), log);
        return (client, requests, log);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, int? remaining = 480, int? used = 20, int? last = 1)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (remaining is { } r)
            response.Headers.Add("x-requests-remaining", r.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (used is { } u)
            response.Headers.Add("x-requests-used", u.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (last is { } l)
            response.Headers.Add("x-requests-last", l.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    // SYNTHETIC bodies (documented v4 shape; team names and ids are made up for the test).
    private const string SportsBody = """
        [{"key":"soccer_turkey_super_league","group":"Soccer","title":"Turkey Super League","description":"Turkish Soccer","active":true,"has_outrights":false},
         {"key":"soccer_uefa_europa_league","group":"Soccer","title":"UEFA Europa League","description":"","active":false,"has_outrights":false},
         {"key":"soccer_fifa_world_cup_winner","group":"Soccer","title":"FIFA World Cup Winner","description":"","active":true,"has_outrights":true},
         {"key":"broken","title":"no active flag"}]
        """;

    private const string EventsBody = """
        [{"id":"aaaa0000bbbb1111cccc2222dddd3333","sport_key":"soccer_turkey_super_league","sport_title":"Turkey Super League",
          "commence_time":"2026-10-05T17:00:00Z","home_team":"Galatasaray","away_team":"Fenerbahce"},
         {"id":"no-time","sport_key":"soccer_turkey_super_league","home_team":"A","away_team":"B"},
         {"id":"eeee4444","sport_key":"soccer_turkey_super_league","commence_time":"2026-10-05T17:00:00","home_team":"A","away_team":"B"}]
        """;

    private const string OddsBody = """
        [{"id":"aaaa0000bbbb1111cccc2222dddd3333","sport_key":"soccer_turkey_super_league","sport_title":"Turkey Super League",
          "commence_time":"2026-10-05T17:00:00Z","home_team":"Galatasaray","away_team":"Fenerbahce",
          "bookmakers":[
            {"key":"onexbet","title":"1xBet","last_update":"2026-10-05T05:58:00Z","markets":[
              {"key":"h2h","last_update":"2026-10-05T05:57:00Z","outcomes":[
                {"name":"Fenerbahce","price":4.2},{"name":"Draw","price":3.40},{"name":"Galatasaray","price":1.856}]}]},
            {"key":"betfair_ex_eu","title":"Betfair","markets":[{"key":"h2h_lay","last_update":"2026-10-05T05:57:00Z","outcomes":[
                {"name":"Fenerbahce","price":4.4},{"name":"Draw","price":3.5},{"name":"Galatasaray","price":1.9}]}]},
            {"key":"weird","title":"Weird","markets":[{"key":"h2h","last_update":"2026-10-05T05:57:00Z","outcomes":[
                {"name":"Galatasaray","price":"1.9"},{"name":"Draw","price":null},{"name":"Fenerbahce","price":4.4}]}]}]}]
        """;

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sports_events_and_odds_use_the_documented_paths_and_parameters_and_parse_strictly()
    {
        var (client, requests, _) = Create((r, _) => Task.FromResult(r.RequestUri!.AbsolutePath switch
        {
            "/v4/sports" => Json(SportsBody, last: 0),
            "/v4/sports/soccer_turkey_super_league/events" => Json(EventsBody, last: 0),
            _ => Json(OddsBody),
        }));

        var sports = await client.GetSportsAsync(includeInactive: true, Ct);
        sports.Value!.Select(s => (s.Key, s.Active, s.HasOutrights)).Should().Equal(("soccer_turkey_super_league", true, false), ("soccer_uefa_europa_league", false, false),
            ("soccer_fifa_world_cup_winner", true, true));
        sports.Dropped.Should().Be(1, "the malformed entry is reported, not silently lost");

        var events = await client.GetEventsAsync("soccer_turkey_super_league", T0, T0.AddHours(48), Ct);
        events.Dropped.Should().Be(2);
        events.Value!.Should().ContainSingle("an event without a time or with a time without its offset is dropped").Which
            .Should().Be(new ProviderEvent("aaaa0000bbbb1111cccc2222dddd3333", "soccer_turkey_super_league", new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero),
                "Galatasaray", "Fenerbahce"));

        var odds = await client.GetOddsAsync("soccer_turkey_super_league", ["aaaa0000bbbb1111cccc2222dddd3333"], Ct);
        odds.Quota.Should().Be(new ProviderQuota(480, 20, 1));
        var match = odds.Value!.Single();
        match.Bookmakers.Select(b => b.Key).Should().Equal("onexbet", "betfair_ex_eu", "weird");
        var h2h = match.Bookmakers[0].Markets.Single();
        (h2h.Key, h2h.LastUpdate).Should().Be(("h2h", new DateTimeOffset(2026, 10, 5, 5, 57, 0, TimeSpan.Zero)), "the market-level time, not the deprecated bookmaker one");
        h2h.Outcomes.Should().Equal(new ProviderPrice("Fenerbahce", 4.2m), new ProviderPrice("Draw", 3.40m), new ProviderPrice("Galatasaray", 1.856m));
        match.Bookmakers[2].Markets.Single().Outcomes.Should().ContainSingle("a string or null price is not a price");
        OddsSelector.Select(match, ["betfair_ex_eu", "weird", "onexbet"], T0, TimeSpan.FromMinutes(30)).Odds!.BookmakerKey.Should().Be("onexbet");

        requests.Select(r => r.RequestUri!.AbsolutePath).Should().Equal("/v4/sports", "/v4/sports/soccer_turkey_super_league/events", "/v4/sports/soccer_turkey_super_league/odds");
        var q = requests.Select(r => System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)).ToList();
        q.Should().OnlyContain(x => x["apiKey"] == Key, "the provider takes the key only as a query parameter");
        q[0]["all"].Should().Be("true", "the whole catalog, inactive competitions included");
        (q[1]["commenceTimeFrom"], q[1]["commenceTimeTo"], q[1]["dateFormat"]).Should().Be(("2026-10-05T06:00:00Z", "2026-10-07T06:00:00Z", "iso"));
        (q[2]["regions"], q[2]["markets"], q[2]["oddsFormat"], q[2]["eventIds"]).Should().Be(("eu", "h2h", "decimal", "aaaa0000bbbb1111cccc2222dddd3333"));
        requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && r.RequestUri!.Host == "api.the-odds-api.com");
    }

    [Fact]
    public async Task Without_a_key_nothing_is_requested()
    {
        var (client, requests, _) = Create((_, _) => throw new InvalidOperationException("no call expected"), key: " ");
        client.IsConfigured.Should().BeFalse();
        (await client.GetSportsAsync(true, Ct)).Outcome.Should().Be(ProviderCallOutcome.NotConfigured);
        (await client.GetOddsAsync("soccer_turkey_super_league", ["abc"], Ct)).Outcome.Should().Be(ProviderCallOutcome.NotConfigured);
        requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderCallOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, ProviderCallOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderCallOutcome.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderCallOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, ProviderCallOutcome.Unavailable)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ProviderCallOutcome.BadResponse)]
    [InlineData(HttpStatusCode.NotFound, ProviderCallOutcome.BadResponse)]
    public async Task Every_failure_status_is_classified_and_carries_no_data(HttpStatusCode status, ProviderCallOutcome outcome)
    {
        var (client, requests, log) = Create((_, _) =>
        {
            var response = Json("{\"message\":\"x\"}", status, remaining: 12, used: 488, last: 0);
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        });
        var result = await client.GetOddsAsync("soccer_turkey_super_league", ["abc123"], Ct);
        (result.Outcome, result.Value, result.HttpStatus, result.Quota.Remaining).Should().Be((outcome, (IReadOnlyList<ProviderOddsEvent>?)null, (int)status, 12));
        if (status == HttpStatusCode.TooManyRequests)
            result.RetryAfter.Should().Be(TimeSpan.FromSeconds(90));
        requests.Should().ContainSingle("one attempt: no hidden retry — every credit-consuming attempt is visible to the quota guard");
        string.Join("\n", log.Lines).Should().NotContain(Key);
    }

    [Fact]
    public async Task Broken_json_a_wrong_shape_and_missing_usage_headers_are_handled()
    {
        var bodies = new Queue<string>(["[{\"id\": ", "{\"events\":[]}", "[]"]);
        var (client, _, _) = Create((_, _) => Task.FromResult(Json(bodies.Dequeue(), remaining: null, used: null, last: null)));
        var broken = await client.GetOddsAsync("soccer_turkey_super_league", ["abc"], Ct);
        (broken.Outcome, broken.MayHaveCost).Should().Be((ProviderCallOutcome.BadResponse, true), "no header: the cost is unknown");
        (await client.GetOddsAsync("soccer_turkey_super_league", ["abc"], Ct)).Outcome.Should().Be(ProviderCallOutcome.BadResponse);
        var empty = await client.GetOddsAsync("soccer_turkey_super_league", ["abc"], Ct);
        (empty.Outcome, empty.Value!.Count, empty.Quota.Known).Should().Be((ProviderCallOutcome.Ok, 0, false));
    }

    [Fact]
    public async Task A_timeout_is_a_timeout_that_may_have_cost_and_never_mentions_the_url()
    {
        var clock = new FakeTimeProvider(T0);
        var (client, _, log) = Create(async (_, ct) =>
        {
            clock.Advance(TimeSpan.FromSeconds(6)); // past the 5 s per-request timeout, on the injected clock
            await Task.Delay(Timeout.Infinite, ct);
            return Json("[]");
        }, clock: clock);
        var result = await client.GetOddsAsync("soccer_turkey_super_league", ["abc"], Ct);
        (result.Outcome, result.MayHaveCost).Should().Be((ProviderCallOutcome.Timeout, true));
        string.Join("\n", log.Lines).Should().NotContain(Key).And.NotContain("apiKey");
    }

    [Fact]
    public async Task A_transport_error_whose_message_holds_the_url_is_recorded_by_type_only()
    {
        var (client, requests, log) = Create((r, _) => throw new HttpRequestException("connection reset while requesting " + r.RequestUri));
        var result = await client.GetEventsAsync("soccer_turkey_super_league", T0, T0.AddHours(1), Ct);
        result.Outcome.Should().Be(ProviderCallOutcome.Unavailable);
        requests.Single().RequestUri!.ToString().Should().Contain(Key, "the key is in the request itself …");
        string.Join("\n", log.Lines).Should().NotContain(Key, "… but never in a log line").And.Contain("HttpRequestException");
        result.ToString().Should().NotContain(Key);
    }

    [Fact]
    public async Task Unsafe_ids_or_sport_keys_are_never_sent()
    {
        var (client, requests, _) = Create((_, _) => Task.FromResult(Json("[]")));
        (await client.GetOddsAsync("soccer_turkey_super_league", ["abc&apiKey=x"], Ct)).Outcome.Should().Be(ProviderCallOutcome.BadResponse);
        (await client.GetOddsAsync("soccer_turkey_super_league", [], Ct)).Outcome.Should().Be(ProviderCallOutcome.BadResponse);
        await FluentActions.Awaiting(() => client.GetEventsAsync("../admin", T0, T0, Ct)).Should().ThrowAsync<ArgumentException>();
        requests.Should().BeEmpty();
    }

    [Fact]
    public void The_redactor_masks_a_query_key_even_when_its_value_is_not_registered()
    {
        var redactor = new SecretRedactor([Key]);
        redactor.Redact("GET https://api.the-odds-api.com/v4/sports?apiKey=" + Key + "&x=1").Should().NotContain(Key);
        new SecretRedactor([]).Redact("GET /v4/sports/x/odds?regions=eu&apiKey=abcdef0123456789&markets=h2h")
            .Should().Be("GET /v4/sports/x/odds?regions=eu&apiKey=" + SecretRedactor.Mask + "&markets=h2h");
        ToroSquad.Infrastructure.InfrastructureServiceCollectionExtensions.SecretConfigurationKeys.Should().Contain(AutoFootballOptions.ApiKeySetting);
    }
}
