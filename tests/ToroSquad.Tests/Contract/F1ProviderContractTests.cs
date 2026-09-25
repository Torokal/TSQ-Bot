using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Modules.Formula1.Providers.Fixtures;
using ToroSquad.Modules.Formula1.Providers.Jolpica;
using ToroSquad.Modules.Formula1.Providers.OpenF1;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// Provider contracts on saved, sanitized fixtures (tests/ToroSquad.Tests/Fixtures/f1; synthetic names, payload shapes as
/// verified against Jolpica and OpenF1 on 2026-09-25). No test here touches the network.
/// </summary>
public sealed class F1ProviderContractTests
{
    private static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "f1", name);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static string Raw(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "f1", name));

    // ---------------------------------------------------------------- Jolpica

    [Fact]
    public void Jolpica_standard_weekend_has_five_exact_sessions_and_tolerates_unknown_fields()
    {
        var schedule = JolpicaParser.ParseSchedule(Load("jolpica-races-standard.json"), 2030);
        var meeting = schedule.Meetings.Should().ContainSingle().Subject;
        meeting.MeetingName.Should().Be("Harbour Grand Prix");
        meeting.CircuitName.Should().Be("Harbour Park Circuit");
        meeting.Country.Should().Be("Testland");
        meeting.Location.Should().Be("Harbour City");
        meeting.Key.Should().Be("2030-07");
        meeting.IsSprintWeekend.Should().BeFalse();
        meeting.Sessions.Select(s => s.Type).Should().Equal(F1SessionType.Practice1, F1SessionType.Practice2, F1SessionType.Practice3, F1SessionType.Qualifying, F1SessionType.Race);
        meeting.Find(F1SessionType.Race)!.ScheduledStartUtc.Should().Be(new DateTimeOffset(2030, 5, 26, 13, 0, 0, TimeSpan.Zero));
        meeting.Sessions.Should().OnlyContain(s => s.ScheduledStartUtc.Offset == TimeSpan.Zero);
    }

    [Fact]
    public void Jolpica_sprint_weekends_normalize_sprint_qualifying_and_shootout_and_skip_sessions_without_a_time()
    {
        var schedule = JolpicaParser.ParseSchedule(Load("jolpica-races-sprint.json"), 2030);
        var sprint = schedule.Meeting(8)!;
        sprint.IsSprintWeekend.Should().BeTrue();
        sprint.Sessions.Select(s => s.Type).Should().Equal(F1SessionType.Practice1, F1SessionType.SprintQualifying, F1SessionType.Sprint, F1SessionType.Qualifying, F1SessionType.Race);
        schedule.Meeting(9)!.Sessions.Should().Contain(s => s.Type == F1SessionType.SprintQualifying, "2023-style SprintShootout is the same normalized session");
        schedule.Meeting(10)!.Sessions.Should().BeEmpty("a date without a time is never turned into an invented start time");
    }

    [Fact]
    public void Jolpica_empty_season_is_a_valid_empty_calendar()
    {
        JolpicaParser.ParseSchedule(Load("jolpica-races-empty.json"), 2031).Meetings.Should().BeEmpty();
        JolpicaParser.ParseDriverStandings(Load("jolpica-standings-empty.json"), 2031).Count.Should().Be(0);
    }

    [Fact]
    public void Jolpica_driver_standings_keep_published_points_positions_and_current_team()
    {
        var table = JolpicaParser.ParseDriverStandings(Load("jolpica-driverstandings.json"), 2030);
        table.Kind.Should().Be(F1StandingsKind.Drivers);
        table.Round.Should().Be(7);
        table.Drivers.Should().HaveCount(5);
        table.Drivers[0].Should().Be(new F1DriverStanding(1, "fast", "Alex Fast", "FST", "Rapid Racing", 120m, 3));
        table.Drivers[1].Points.Should().Be(98.5m, "half points are kept exactly");
        table.Drivers[3].TeamName.Should().Be("Comet GP", "the last listed constructor is the current team");
        table.Drivers[4].Position.Should().BeNull("an excluded driver has no numeric position — not invented");
    }

    [Fact]
    public void Jolpica_constructor_standings_parse()
    {
        var table = JolpicaParser.ParseConstructorStandings(Load("jolpica-constructorstandings.json"), 2030);
        table.Constructors.Select(c => (c.Position, c.ConstructorId, c.Points)).Should().Equal((1, "rapid", 218.5m), (2, "comet", 80m));
    }

    [Fact]
    public void Jolpica_standings_for_another_season_are_rejected()
    {
        var act = () => JolpicaParser.ParseDriverStandings(Load("jolpica-driverstandings.json"), 2031);
        act.Should().Throw<JsonException>();
    }

    // ---------------------------------------------------------------- OpenF1

    [Fact]
    public void OpenF1_sessions_normalize_names_and_keep_unknown_types_unknown()
    {
        var sessions = OpenF1Parser.ParseSessions(Load("openf1-sessions.json"));
        sessions.Select(s => s.Type).Should().Equal(F1SessionType.Practice1, F1SessionType.SprintQualifying, F1SessionType.Sprint, F1SessionType.Qualifying, F1SessionType.Race, F1SessionType.Unknown);
        var race = sessions[4];
        (race.Ref, race.Season, race.StartUtc, race.EndUtc, race.IsCancelled).Should().Be(("8805", 2030,
            new DateTimeOffset(2030, 6, 9, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 6, 9, 9, 0, 0, TimeSpan.Zero), false));
    }

    [Fact]
    public void OpenF1_red_flag_lifecycle_normalizes_to_start_suspend_resume_finish_and_ignores_other_messages()
    {
        var events = OpenF1Parser.ParseLifecycleEvents(Load("openf1-race-control-redflag.json"));
        events.Select(e => e.Signal).Should().Equal(F1LifecycleSignal.Started, F1LifecycleSignal.Suspended, F1LifecycleSignal.Started, F1LifecycleSignal.Finished, F1LifecycleSignal.Finished);
        events.Should().OnlyContain(e => e.ProviderSessionRef == "8805" && e.ProviderId == "openf1");
        events[0].OccurredAt.Should().Be(new DateTimeOffset(2030, 6, 9, 7, 3, 38, 697, TimeSpan.Zero));

        // Through the state machine: exactly one logical start, the red-flag restart is a resume, the duplicate FINISHED is ignored.
        var state = F1SessionState.Scheduled;
        DateTimeOffset? last = null;
        var kinds = new List<F1TransitionKind>();
        foreach (var e in events)
        {
            if (last is { } l && e.OccurredAt <= l)
            {
                kinds.Add(F1TransitionKind.Ignored);
                continue;
            }

            var t = F1LifecycleMachine.Apply(F1SessionType.Race, state, last, e);
            kinds.Add(t.Kind);
            state = t.NewState;
            last = e.OccurredAt;
        }

        kinds.Should().Equal(F1TransitionKind.FirstStart, F1TransitionKind.Suspend, F1TransitionKind.Resume, F1TransitionKind.Finish, F1TransitionKind.Ignored);
        state.Should().Be(F1SessionState.FinishedPendingResults);
    }

    [Fact]
    public void OpenF1_qualifying_emits_a_finish_per_segment_and_only_the_last_one_finishes()
    {
        var events = OpenF1Parser.ParseLifecycleEvents(Load("openf1-race-control-qualifying.json"));
        events.Select(e => e.QualifyingPhase).Should().Equal(1, 1, 2, 2, 3, 3);
        var state = F1SessionState.Scheduled;
        DateTimeOffset? last = null;
        foreach (var e in events)
        {
            var t = F1LifecycleMachine.Apply(F1SessionType.Qualifying, state, last, e);
            if (e.QualifyingPhase < 3)
                t.NewState.Should().Be(F1SessionState.Started);
            state = t.NewState;
            last = e.OccurredAt;
        }

        state.Should().Be(F1SessionState.FinishedPendingResults);
    }

    private static F1Session Session(F1SessionType type) => new(2030, 8, type, new DateTimeOffset(2030, 6, 9, 7, 0, 0, TimeSpan.Zero));

    [Fact]
    public void OpenF1_race_result_parses_classified_lapped_dnf_dns_and_dsq_without_inventing_times()
    {
        var result = OpenF1Parser.ParseSessionResult(Load("openf1-session-result-race.json"), Load("openf1-drivers.json"), Session(F1SessionType.Race));
        result.SessionKey.Should().Be("2030-08-race");
        result.Entries.Should().HaveCount(12);
        var winner = result.Entries.Single(e => e.Position == 1);
        winner.Should().BeEquivalentTo(new { DriverName = "Alex Fast", DriverCode = "FST", TeamName = "Rapid Racing", Status = F1ResultStatus.Classified, Laps = 56, Points = 25.0 });
        winner.TimeSeconds.Should().BeApproximately(5455.026, 0.0001);
        result.Entries.Single(e => e.Position == 8).Should().BeEquivalentTo(new { GapLaps = (int?)1, GapSeconds = (double?)null, TimeSeconds = (double?)null });
        result.Entries.Single(e => e.Position == 9).GapLaps.Should().Be(2);
        result.Entries.Single(e => e.Status == F1ResultStatus.Dnf).Should().BeEquivalentTo(new { Position = (int?)null, Laps = (int?)4, TimeSeconds = (double?)null });
        result.Entries.Should().ContainSingle(e => e.Status == F1ResultStatus.Dns);
        result.Entries.Should().ContainSingle(e => e.Status == F1ResultStatus.Dsq);
        F1ResultValidator.Problem(result, 10).Should().BeNull();
    }

    [Fact]
    public void OpenF1_sprint_result_parses_with_points()
    {
        var result = OpenF1Parser.ParseSessionResult(Load("openf1-session-result-sprint.json"), Load("openf1-drivers.json"), Session(F1SessionType.Sprint));
        result.Type.Should().Be(F1SessionType.Sprint);
        result.Entries.Single(e => e.Position == 1).Points.Should().Be(8.0);
        F1ResultValidator.Problem(result, 10).Should().BeNull();
    }

    [Fact]
    public void OpenF1_practice_result_keeps_best_laps_and_gaps_and_leaves_missing_times_missing()
    {
        var result = OpenF1Parser.ParseSessionResult(Load("openf1-session-result-practice.json"), Load("openf1-drivers.json"), Session(F1SessionType.Practice1));
        result.Entries.Single(e => e.Position == 1).TimeSeconds.Should().BeApproximately(91.504, 0.0001);
        result.Entries.Single(e => e.Position == 2).GapSeconds.Should().BeApproximately(0.121, 0.0001);
        result.Entries.Single(e => e.Position == 6).Should().BeEquivalentTo(new { TimeSeconds = (double?)null, GapSeconds = (double?)null });
        result.Entries.Should().OnlyContain(e => e.Points == null);
    }

    [Fact]
    public void OpenF1_qualifying_result_uses_the_last_segment_with_a_time()
    {
        var result = OpenF1Parser.ParseSessionResult(Load("openf1-session-result-qualifying.json"), Load("openf1-drivers.json"), Session(F1SessionType.Qualifying));
        result.Entries.Single(e => e.Position == 1).TimeSeconds.Should().BeApproximately(90.641, 0.0001, "Q3 time");
        result.Entries.Single(e => e.Position == 11).TimeSeconds.Should().BeApproximately(92.2, 0.0001, "eliminated in Q2: Q2 time");
    }

    [Fact]
    public void OpenF1_result_without_driver_list_is_not_publishable()
    {
        var result = OpenF1Parser.ParseSessionResult(Load("openf1-session-result-race.json"), null, Session(F1SessionType.Race));
        F1ResultValidator.Problem(result, 10).Should().Contain("name");
    }

    [Fact]
    public void OpenF1_mqtt_message_parses_with_its_identity_and_other_topics_are_ignored()
    {
        var (e, id) = OpenF1Parser.ParseMqttMessage(OpenF1Topics.RaceControl, Raw("openf1-mqtt-race-control.json"));
        e!.Signal.Should().Be(F1LifecycleSignal.Started);
        id.Should().Be("1749452618697_None#1749452618800");
        OpenF1Parser.ParseMqttMessage("v1/laps", Raw("openf1-mqtt-race-control.json")).Event.Should().BeNull();
        OpenF1Parser.ParseMqttMessage(OpenF1Topics.RaceControl, "{not json").Event.Should().BeNull();
        OpenF1Parser.ParseMqttMessage(OpenF1Topics.RaceControl, "{\"category\":\"Flag\",\"message\":\"GREEN LIGHT\"}").Event.Should().BeNull();
    }

    [Fact]
    public void OpenF1_documented_empty_404_is_recognized()
    {
        OpenF1Parser.IsDocumentedEmptyResult(Raw("openf1-not-found.json")).Should().BeTrue();
        OpenF1Parser.IsDocumentedEmptyResult("{\"detail\":\"Not Found\"}").Should().BeFalse("a wrong path is an error, not 'no data'");
    }

    // ---------------------------------------------------------------- HTTP clients (stubbed transport)

    private static (OpenF1Client Client, StubHttpHandler Stub) OpenF1(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond, FakeTimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider(new DateTimeOffset(2030, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var stub = new StubHttpHandler(respond);
        var options = Options.Create(new OpenF1Options { MaxRetries = 0 });
        var tokens = new OpenF1TokenProvider(new SingleClientFactory(new HttpClient(stub)), options, clock, NullLogger<OpenF1TokenProvider>.Instance);
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.openf1.org/") };
        return (new OpenF1Client(http, options, tokens, new F1RequestBudget(clock), clock, NullLogger<OpenF1Client>.Instance), stub);
    }

    [Fact]
    public async Task OpenF1_not_published_yet_is_success_without_value_and_a_wrong_path_is_a_schema_error()
    {
        var (client, _) = OpenF1((r, _) => Task.FromResult(StubHttpHandler.Json(Raw("openf1-not-found.json"), HttpStatusCode.NotFound)));
        var provider = new OpenF1ResultsProvider(client, NoTokens(), Options.Create(new OpenF1Options()), new F1DataMode(F1ProviderMode.Live));
        var result = await provider.GetResultAsync(Session(F1SessionType.Race), "8805", CancellationToken.None);
        result.Outcome.Should().Be(F1ProviderOutcome.Success);
        result.Value.Should().BeNull("not published yet — retry later, never an empty classification");

        var (wrong, _) = OpenF1((r, _) => Task.FromResult(StubHttpHandler.Json("{\"detail\":\"Not Found\"}", HttpStatusCode.NotFound)));
        (await wrong.GetSessionResultAsync("8805", CancellationToken.None)).Outcome.Should().Be(F1ProviderOutcome.SchemaError);
    }

    [Fact]
    public async Task OpenF1_results_provider_joins_results_with_drivers()
    {
        var (client, stub) = OpenF1((r, _) => Task.FromResult(StubHttpHandler.Json(Raw(r.RequestUri!.AbsolutePath.EndsWith("/drivers", StringComparison.Ordinal)
            ? "openf1-drivers.json" : "openf1-session-result-race.json"))));
        var provider = new OpenF1ResultsProvider(client, NoTokens(), Options.Create(new OpenF1Options()), new F1DataMode(F1ProviderMode.Live));
        var result = await provider.GetResultAsync(Session(F1SessionType.Race), "8805", CancellationToken.None);
        result.Value!.Entries.Single(e => e.Position == 1).DriverName.Should().Be("Alex Fast");
        stub.Requests.Select(u => u.AbsolutePath).Should().Equal("/v1/session_result", "/v1/drivers");
        stub.Requests[0].Query.Should().Contain("session_key=8805");
        provider.AvailabilityDelay.Should().Be(TimeSpan.FromMinutes(35), "without live access results are read after OpenF1's live window");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, F1ProviderOutcome.QuotaExceeded)]
    [InlineData(HttpStatusCode.Unauthorized, F1ProviderOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, F1ProviderOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.BadGateway, F1ProviderOutcome.TransportError)]
    [InlineData(HttpStatusCode.BadRequest, F1ProviderOutcome.SchemaError)]
    public async Task Provider_http_failures_are_classified_and_never_look_like_no_data(HttpStatusCode status, F1ProviderOutcome expected)
    {
        var (client, _) = OpenF1((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("{}") };
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        });
        var result = await client.GetLifecycleEventsAsync("8805", CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.HasData.Should().BeFalse();
        if (expected == F1ProviderOutcome.QuotaExceeded)
            result.RetryAfter.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task Malformed_json_is_a_schema_error()
    {
        var (client, _) = OpenF1((_, _) => Task.FromResult(StubHttpHandler.Json("[{\"broken\": ")));
        (await client.GetSessionsAsync(2030, CancellationToken.None)).Outcome.Should().Be(F1ProviderOutcome.SchemaError);
    }

    [Fact]
    public async Task The_local_request_budget_is_never_exceeded()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var (client, stub) = OpenF1((_, _) => Task.FromResult(StubHttpHandler.Json("[]")), clock);
        var outcomes = new List<F1ProviderOutcome>();
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1)); // stays under 3 req/s; the per-minute share (15) is the limit
            outcomes.Add((await client.GetLifecycleEventsAsync("8805", CancellationToken.None)).Outcome);
        }

        // Capacity plus what refills during the 40 simulated seconds — never the 40 requests asked for.
        var perMinute = new OpenF1Options().PlannedRequestsPerMinute;
        stub.Requests.Count.Should().BeLessThanOrEqualTo(perMinute + (int)Math.Ceiling(40 / 60.0 * perMinute) + 1).And.BeLessThan(40);
        outcomes.Should().Contain(F1ProviderOutcome.QuotaExceeded);
    }

    [Fact]
    public async Task OpenF1_token_is_requested_with_form_credentials_cached_and_sent_as_bearer_header()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var calls = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var stub = new StubHttpHandler(async (r, _) =>
        {
            calls.Add(r);
            if (r.RequestUri!.AbsolutePath == "/token")
            {
                bodies.Add(await r.Content!.ReadAsStringAsync());
                return StubHttpHandler.Json("{\"expires_in\":\"3600\",\"access_token\":\"tok-123456789\",\"token_type\":\"bearer\"}");
            }

            return StubHttpHandler.Json("[]");
        });
        var options = Options.Create(new OpenF1Options { Username = "user@example.com", Password = "secret-password", MaxRetries = 0 });
        var tokens = new OpenF1TokenProvider(new SingleClientFactory(new HttpClient(stub)), options, clock, NullLogger<OpenF1TokenProvider>.Instance);
        var client = new OpenF1Client(new HttpClient(stub) { BaseAddress = new Uri("https://api.openf1.org/") }, options, tokens, new F1RequestBudget(clock), clock, NullLogger<OpenF1Client>.Instance);

        await client.GetLifecycleEventsAsync("8805", CancellationToken.None);
        await client.GetLifecycleEventsAsync("8805", CancellationToken.None);

        calls.Count(c => c.RequestUri!.AbsolutePath == "/token").Should().Be(1, "cached until shortly before expiry");
        bodies.Single().Should().Contain("username=user%40example.com").And.Contain("password=secret-password");
        calls.Where(c => c.RequestUri!.AbsolutePath != "/token").Should().OnlyContain(c => c.Headers.Authorization!.Scheme == "Bearer" && c.Headers.Authorization.Parameter == "tok-123456789");
        calls.Should().OnlyContain(c => !c.RequestUri!.Query.Contains("tok-", StringComparison.Ordinal), "the token never appears in a URL");

        clock.Advance(TimeSpan.FromMinutes(56));
        await client.GetLifecycleEventsAsync("8805", CancellationToken.None);
        calls.Count(c => c.RequestUri!.AbsolutePath == "/token").Should().Be(2, "refreshed before the 1 h expiry");
    }

    [Fact]
    public async Task Rejected_credentials_are_an_auth_failure_and_lifecycle_without_credentials_is_not_configured()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var stub = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var bad = new OpenF1TokenProvider(new SingleClientFactory(new HttpClient(stub)), Options.Create(new OpenF1Options { Username = "u", Password = "wrong" }), clock, NullLogger<OpenF1TokenProvider>.Instance);
        (await bad.GetAsync(CancellationToken.None)).Outcome.Should().Be(F1ProviderOutcome.AuthFailed);

        var (client, requests) = OpenF1((_, _) => Task.FromResult(StubHttpHandler.Json("[]")));
        var lifecycle = new OpenF1LifecycleProvider(client, NoTokens(), new F1DataMode(F1ProviderMode.Live), clock);
        lifecycle.IsConfigured.Should().BeFalse();
        (await lifecycle.GetLifecycleEventsAsync("8805", CancellationToken.None)).Outcome.Should().Be(F1ProviderOutcome.NotConfigured);
        requests.Requests.Should().BeEmpty("no request without live access");
    }

    [Fact]
    public async Task Jolpica_client_sends_a_user_agent_and_uses_documented_routes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 6, 9, 12, 0, 0, TimeSpan.Zero));
        string? agent = null;
        var stub = new StubHttpHandler((r, _) =>
        {
            agent = string.Join(" ", r.Headers.UserAgent.Select(u => u.ToString()));
            var file = r.RequestUri!.AbsolutePath.Contains("driverstandings", StringComparison.Ordinal) ? "jolpica-driverstandings.json" : "jolpica-races-sprint.json";
            return Task.FromResult(StubHttpHandler.Json(Raw(file)));
        });
        var client = new JolpicaClient(new HttpClient(stub) { BaseAddress = new Uri("https://api.jolpi.ca/ergast/f1/") }, Options.Create(new JolpicaOptions()),
            new F1RequestBudget(clock), clock, NullLogger<JolpicaClient>.Instance);
        (await client.GetScheduleAsync(2030, CancellationToken.None)).Value!.Meetings.Should().HaveCount(3);
        (await client.GetStandingsAsync(F1StandingsKind.Drivers, 2030, CancellationToken.None)).Value!.Drivers.Should().HaveCount(5);
        stub.Requests.Select(u => u.PathAndQuery).Should().Equal("/ergast/f1/2030/races/?limit=100", "/ergast/f1/2030/driverstandings/?limit=100");
        agent.Should().StartWith("TSQBot/");
    }

    [Fact]
    public async Task Fixture_handler_serves_the_demo_weekend_through_the_real_parsers()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var anchor = new F1FixtureAnchor(clock);
        var http = new HttpClient(new F1FixtureHttpHandler(clock, anchor)) { BaseAddress = new Uri("https://api.openf1.org/") };
        var jolpica = new JolpicaClient(new HttpClient(new F1FixtureHttpHandler(clock, anchor)) { BaseAddress = new Uri("https://api.jolpi.ca/ergast/f1/") },
            Options.Create(new JolpicaOptions()), new F1RequestBudget(clock), clock, NullLogger<JolpicaClient>.Instance);
        var schedule = (await jolpica.GetScheduleAsync(2030, CancellationToken.None)).Value!;
        schedule.Meetings.Should().HaveCount(3);
        schedule.Meetings.Should().OnlyContain(m => m.MeetingName.StartsWith("TSQ ", StringComparison.Ordinal), "fictional names only");

        var options = Options.Create(new OpenF1Options());
        var tokens = new OpenF1TokenProvider(new SingleClientFactory(http), options, clock, NullLogger<OpenF1TokenProvider>.Instance);
        var openF1 = new OpenF1Client(http, options, tokens, new F1RequestBudget(clock), clock, NullLogger<OpenF1Client>.Instance);
        (await openF1.GetLifecycleEventsAsync("9201", CancellationToken.None)).Value.Should().BeEmpty("nothing happened yet");
        clock.Advance(TimeSpan.FromMinutes(22));
        (await openF1.GetLifecycleEventsAsync("9201", CancellationToken.None)).Value!.Select(e => e.Signal).Should().Equal(F1LifecycleSignal.Started);
        (await openF1.GetSessionResultAsync("9201", CancellationToken.None)).Value.Should().BeNull("results appear only after the session");
        clock.Advance(TimeSpan.FromMinutes(70));
        using var doc = (await openF1.GetSessionResultAsync("9201", CancellationToken.None)).Value!;
        doc.RootElement.GetArrayLength().Should().Be(20);
    }

    private static OpenF1TokenProvider NoTokens() =>
        new(new SingleClientFactory(new HttpClient()), Options.Create(new OpenF1Options()), TimeProvider.System, NullLogger<OpenF1TokenProvider>.Instance);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
