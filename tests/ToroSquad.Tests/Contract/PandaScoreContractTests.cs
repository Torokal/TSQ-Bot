using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.Liquipedia;
using ToroSquad.Modules.Esports.Providers.PandaScore;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// PandaScore parsing against a fixed-date contract fixture (shape per the official OpenAPI definition read 2026-09-25),
/// plus client outcomes: every failure kind stays distinguishable from "successfully empty"; pagination is complete and
/// bounded; the local budget counts retries. TESTED_OFFLINE — real payloads still need a live token.
/// </summary>
public sealed class PandaScoreContractTests
{
    private static readonly MatchWindow Window = new(TestHost.T0.AddHours(-12), TestHost.T0.AddHours(48));

    private static (Dictionary<string, EsportsMatch> Matches, List<string> Warnings) Contract()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pandascore", "matches-contract.json"));
        using var doc = JsonDocument.Parse(json);
        var warnings = new List<string>();
        var matches = doc.RootElement.EnumerateArray().Select(e => PandaScoreParser.ParseMatch(e.Clone(), warnings)).OfType<EsportsMatch>()
            .ToDictionary(m => m.Key.Id);
        return (matches, warnings);
    }

    [Fact]
    public void Upcoming_match_is_scheduled_with_exact_time_format_and_no_score()
    {
        var m = Contract().Matches["1001"];
        m.Key.Should().Be(new MatchKey("pandascore", "1001"));
        m.Status.Should().Be(MatchStatus.Scheduled);
        m.ScheduledStartUtc.Should().Be(new DateTimeOffset(2026, 9, 24, 18, 0, 0, TimeSpan.Zero));
        m.StartTimeExact.Should().BeTrue();
        m.BeginAtUtc.Should().BeNull("for not-started matches begin_at only mirrors scheduled_at");
        m.BestOf.Should().Be(3);
        m.SeriesScoreKnown.Should().BeFalse("a scheduled 0-0 is not a result");
        m.A.Team!.Name.Should().Be("Alpha Squad");
        m.A.Team.Key.Should().Be("ps-team:11");
        m.A.Team.ShortName.Should().Be("ALP");
        m.Tournament.Name.Should().Be("StarLadder StarSeries Fall 2026");
        m.Tournament.Tier.Should().Be("1", "PandaScore tier s maps to the existing 1..5 scale");
        m.Stage.Should().Be("Playoffs");
        m.Streams.Select(s => s.Url).First().Should().Be("https://www.twitch.tv/example_main", "main stream first");
        m.Links.Should().Be(MatchLinks.None, "PandaScore exposes no public match page");
        m.SourceUrl.Should().BeNull();
    }

    [Fact]
    public void Running_match_is_live_with_the_actual_begin_time()
    {
        var m = Contract().Matches["1002"];
        m.Status.Should().Be(MatchStatus.Live);
        m.BeginAtUtc.Should().Be(new DateTimeOffset(2026, 9, 24, 11, 12, 0, TimeSpan.Zero));
        m.A.Score.Should().Be(1);
        m.B.Score.Should().Be(0);
        m.WinnerIndex.Should().BeNull();
        m.A.Team!.ShortName.Should().BeNull();
    }

    [Fact]
    public void Finished_match_has_winner_from_winner_id_series_score_and_end_time()
    {
        var m = Contract().Matches["1003"];
        m.Status.Should().Be(MatchStatus.Finished);
        m.WinnerIndex.Should().Be(1);
        m.WinnerName.Should().Be("Foxtrot");
        (m.A.Score, m.B.Score).Should().Be((0, 2));
        m.A.Result.Should().Be(OpponentResult.Loss);
        m.B.Result.Should().Be(OpponentResult.Win);
        m.EndAtUtc.Should().Be(new DateTimeOffset(2026, 9, 24, 10, 20, 0, TimeSpan.Zero));
        m.IsForfeit.Should().BeFalse();
        m.Maps.Should().HaveCount(3);
        m.Maps.Count(g => g.Status == GameStatus.Played).Should().Be(2);
        m.Tournament.Tier.Should().Be("2");
    }

    [Fact]
    public void Postponed_match_keeps_its_old_time_and_status()
    {
        var m = Contract().Matches["1004"];
        m.Status.Should().Be(MatchStatus.Postponed);
        m.ScheduledStartUtc.Should().Be(new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero));
        m.BestOf.Should().Be(1);
    }

    [Fact]
    public void Rescheduled_match_is_scheduled_with_flag_and_original_time()
    {
        var m = Contract().Matches["1005"];
        m.Status.Should().Be(MatchStatus.Scheduled);
        m.Rescheduled.Should().BeTrue();
        m.ScheduledStartUtc.Should().Be(new DateTimeOffset(2026, 9, 25, 19, 0, 0, TimeSpan.Zero));
        m.OriginalScheduledStartUtc.Should().Be(new DateTimeOffset(2026, 9, 24, 19, 0, 0, TimeSpan.Zero));
        m.Tournament.Name.Should().Be("StarLadder StarSeries Fall 2026", "a serie name that already contains the league is not doubled");
        m.Tournament.Tier.Should().BeNull("unranked");
    }

    [Fact]
    public void Canceled_match_without_forfeit_is_cancelled_without_winner()
    {
        var m = Contract().Matches["1006"];
        m.Status.Should().Be(MatchStatus.Cancelled);
        m.WinnerIndex.Should().BeNull();
        m.IsForfeit.Should().BeFalse();
    }

    [Fact]
    public void Forfeit_is_a_finished_forfeit_with_the_stated_winner_and_no_invented_score()
    {
        var m = Contract().Matches["1007"];
        m.Status.Should().Be(MatchStatus.Finished);
        m.IsForfeit.Should().BeTrue();
        m.WinnerIndex.Should().Be(1);
        m.A.Result.Should().Be(OpponentResult.Forfeit);
        m.SeriesScoreKnown.Should().BeFalse("results of a forfeit are not a played score");
    }

    [Fact]
    public void Unknown_status_stays_unknown_and_nothing_is_guessed()
    {
        var (matches, warnings) = Contract();
        var m = matches["1008"];
        m.Status.Should().Be(MatchStatus.Unknown);
        m.WinnerIndex.Should().BeNull("winner_id 99 is not one of the opponents");
        m.BestOf.Should().BeNull("first_to is not a best-of");
        m.B.Kind.Should().Be(OpponentKind.Unknown, "a player opponent is not a team");
        warnings.Should().Contain(w => w.Contains("unknown status", StringComparison.Ordinal));
        warnings.Should().Contain(w => w.Contains("winner_id is not one of the opponents", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_items_are_skipped_or_degraded_with_warnings_never_invented()
    {
        var (matches, warnings) = Contract();
        matches.Should().NotContainKey("", "an item without id is skipped");
        warnings.Should().Contain("match without id skipped");
        var badDate = matches["1009"];
        badDate.ScheduledStartUtc.Should().BeNull();
        badDate.StartTimeExact.Should().BeFalse();
        badDate.B.Kind.Should().Be(OpponentKind.Tbd, "a missing second opponent is TBD");
        warnings.Should().Contain(w => w.Contains("invalid scheduled_at", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- client outcomes

    private static (PandaScoreProvider Provider, StubHttpHandler Handler, PandaScoreClient Client) Create(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond, Action<PandaScoreOptions>? configure = null,
        ProviderMode mode = ProviderMode.Live, TimeProvider? clock = null)
    {
        var options = new PandaScoreOptions { Token = "test-token-not-real", PageSize = 2, MaxPages = 5, MaxRetries = 1, TimeoutSeconds = 2 };
        configure?.Invoke(options);
        clock ??= TimeProvider.System;
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.pandascore.test/") };
        var client = new PandaScoreClient(http, Options.Create(options), new RequestBudget(clock), clock, NullLogger<PandaScoreClient>.Instance);
        return (new PandaScoreProvider(client, new EsportsDataMode(mode)), handler, client);
    }

    private static string Item(int id) => new JsonObject
    {
        ["id"] = id,
        ["status"] = "not_started",
        ["scheduled_at"] = "2026-09-24T18:00:00Z",
        ["match_type"] = "best_of",
        ["number_of_games"] = 3,
        ["opponents"] = new JsonArray(
            new JsonObject { ["type"] = "Team", ["opponent"] = new JsonObject { ["id"] = 1, ["name"] = "A" } },
            new JsonObject { ["type"] = "Team", ["opponent"] = new JsonObject { ["id"] = 2, ["name"] = "B" } }),
    }.ToJsonString();

    private static Func<HttpRequestMessage, int, Task<HttpResponseMessage>> Paged(int total, bool sendTotal = true) => (request, _) =>
    {
        var q = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var number = int.Parse(q["page[number]"]!, System.Globalization.CultureInfo.InvariantCulture);
        var size = int.Parse(q["page[size]"]!, System.Globalization.CultureInfo.InvariantCulture);
        var items = Enumerable.Range(1, total).Skip((number - 1) * size).Take(size).Select(Item);
        var response = StubHttpHandler.Json("[" + string.Join(",", items) + "]");
        if (sendTotal)
            response.Headers.TryAddWithoutValidation("X-Total", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(response);
    };

    [Fact]
    public async Task Empty_array_is_a_successful_empty_result_not_a_failure()
    {
        var (provider, _, _) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("[]")));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Value.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, ProviderOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.NotFound, ProviderOutcome.SchemaError)]
    [InlineData(HttpStatusCode.BadRequest, ProviderOutcome.SchemaError)]
    public async Task Http_errors_are_failures_never_an_empty_schedule(HttpStatusCode status, ProviderOutcome expected)
    {
        var (provider, _, _) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("{\"error\":\"x\",\"message\":\"y\"}", status)));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.HasData.Should().BeFalse();
    }

    [Fact]
    public async Task Rate_limit_is_quota_exceeded_with_retry_after()
    {
        var (provider, handler, _) = Create((_, _) =>
        {
            var r = StubHttpHandler.Json("{\"error\":\"Too Many Requests\"}", HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
            return Task.FromResult(r);
        });
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.QuotaExceeded);
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(7));
        handler.Requests.Should().HaveCount(1, "429 is not retried blindly");
    }

    [Fact]
    public async Task Server_errors_are_retried_boundedly_then_reported_as_transport_error()
    {
        var (provider, handler, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.TransportError);
        handler.Requests.Should().HaveCount(2, "one retry (MaxRetries = 1)");
    }

    [Fact]
    public async Task Timeout_is_reported_as_timeout()
    {
        var (provider, _, _) = Create(async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return StubHttpHandler.Json("[]");
        }, o => { o.TimeoutSeconds = 1; o.MaxRetries = 0; });
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Timeout);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"matches\":[]}")]
    public async Task Malformed_or_non_array_body_is_a_schema_error(string body)
    {
        var (provider, _, _) = Create((_, _) => Task.FromResult(StubHttpHandler.Json(body)));
        (await provider.GetMatchesAsync(Window, CancellationToken.None)).Outcome.Should().Be(ProviderOutcome.SchemaError);
    }

    [Fact]
    public async Task Pagination_reads_every_page_and_stops_at_x_total()
    {
        var (provider, handler, _) = Create(Paged(4));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Value.Should().HaveCount(4);
        handler.Requests.Should().HaveCount(2, "X-Total says 4 and two full pages of 2 were read");
    }

    [Fact]
    public async Task Pagination_without_total_stops_on_a_short_page()
    {
        var (provider, handler, _) = Create(Paged(5, sendTotal: false));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Value.Should().HaveCount(5);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Hitting_the_page_limit_is_partial_not_complete()
    {
        var (provider, _, _) = Create(Paged(20), o => o.MaxPages = 2);
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Partial);
        result.Value.Should().HaveCount(4);
    }

    [Fact]
    public async Task Failure_after_some_pages_is_partial_and_labelled()
    {
        var (provider, _, _) = Create((r, n) => n == 1 ? Paged(10)(r, n) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var result = await provider.GetMatchesAsync(Window, CancellationToken.None);
        result.Outcome.Should().Be(ProviderOutcome.Partial);
        result.Detail.Should().Contain("page 2 failed");
    }

    [Fact]
    public async Task Request_uses_bearer_header_window_range_sort_and_the_csgo_prefix_never_a_token_in_the_url()
    {
        HttpRequestMessage? seen = null;
        var (provider, handler, _) = Create((r, _) =>
        {
            seen = r;
            return Task.FromResult(StubHttpHandler.Json("[]"));
        });
        await provider.GetMatchesAsync(Window, CancellationToken.None);
        seen!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        var url = handler.Requests.Single().ToString();
        url.Should().NotContain("token").And.NotContain("test-token-not-real");
        url.Should().StartWith("https://api.pandascore.test/csgo/matches?");
        var q = HttpUtility.ParseQueryString(handler.Requests.Single().Query);
        q["range[scheduled_at]"].Should().Be("2026-09-24T00:00:00Z,2026-09-26T12:00:00Z");
        q["sort"].Should().Be("scheduled_at");
        q["page[size]"].Should().Be("2");
    }

    [Fact]
    public async Task Live_mode_without_token_is_not_configured_and_makes_no_request()
    {
        var (provider, handler, _) = Create((_, _) => Task.FromResult(StubHttpHandler.Json("[]")), o => o.Token = null);
        provider.IsConfigured.Should().BeFalse();
        (await provider.GetMatchesAsync(Window, CancellationToken.None)).Outcome.Should().Be(ProviderOutcome.NotConfigured);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Retries_spend_the_local_budget_too()
    {
        var (provider, handler, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            o => { o.RequestsPerHour = 2; o.BudgetShare = 1.0; o.MaxRetries = 5; });
        var first = await provider.GetMatchesAsync(Window, CancellationToken.None);
        handler.Requests.Should().HaveCount(2, "the 2-token budget allows exactly two HTTP attempts");
        first.Outcome.Should().Be(ProviderOutcome.QuotaExceeded);
    }

    [Fact]
    public void Planned_budget_is_half_of_the_documented_free_plan_limit_by_default()
    {
        var o = new PandaScoreOptions();
        o.RequestsPerHour.Should().Be(1000);
        o.PlannedRequestsPerHour.Should().Be(500);
        o.PageSize.Should().Be(100);
    }

    [Fact]
    public async Task Fixture_mode_runs_the_real_pandascore_client_parser_and_pagination_end_to_end()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore", ["PandaScore:PageSize"] = "3" });
        host.Services.GetRequiredService<IEsportsDataProvider>().Should().BeOfType<PandaScoreProvider>();
        var poller = host.Services.GetRequiredService<EsportsPoller>();
        await poller.RefreshMatchesAsync(CancellationToken.None);
        await poller.RefreshEventsAsync(CancellationToken.None);

        var cache = host.Services.GetRequiredService<EsportsCache>();
        cache.Matches.LastOutcome.Should().Be(ProviderOutcome.Success);
        var data = cache.Matches.Data!;
        data.Should().HaveCount(8, "three pages of up to 3 through the real pagination loop");
        data.Select(m => m.Status).Should().Contain([MatchStatus.Scheduled, MatchStatus.Live, MatchStatus.Finished, MatchStatus.Postponed, MatchStatus.Cancelled]);
        data.Should().Contain(m => m.Rescheduled && m.OriginalScheduledStartUtc < m.ScheduledStartUtc);
        data.Should().Contain(m => m.IsForfeit && m.WinnerName == "Nordic Owls");
        cache.Events.Data.Should().ContainSingle(e => e.Tournament.Name == "Demo Masters 2026" && e.Participants == 6 && e.PrizePoolUsd == 250000);
        host.Services.GetRequiredService<PandaScoreClient>().Options.Token.Should().BeNull("fixture mode needs no token");
    }
}
