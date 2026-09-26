using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;
using ToroSquad.Modules.Volleyball.Providers.Fivb;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// FIVB VIS contract, on responses RECORDED from the real public service on 2026-09-26 (Fixtures/volleyball/*.json,
/// trimmed, otherwise verbatim) plus hand-made malformed/error cases. Passing these tests is TESTED_OFFLINE, not a live
/// verification.
/// </summary>
public sealed class VbProviderContractTests
{
    private static readonly TrackedTeamIdentity Sultanlar = TrackedTeamIdentity.TurkeyWomenSenior();

    private static JsonDocument Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "volleyball", name)));

    private static FivbMatchList Parse(string name, out string? problem) => FivbVisParser.Parse(Fixture(name), FivbVisParser.SeniorTypeValues, out problem);

    // ------------------------------------------------------------------ parsing real responses

    [Fact]
    public void Vnl_2026_recorded_response_parses_turkey_women_senior_matches_with_utc_times_and_set_points()
    {
        var list = Parse("fivb-vnl2026w-tur.json", out var problem);
        problem.Should().BeNull();
        list.Matches.Should().HaveCount(3);

        var final = list.Matches.Single(m => m.ProviderMatchId == "26665");
        final.MatchId.Should().Be("fivb:26665");
        final.CompetitionName.Should().Be("Women's Volleyball Nations League 2026");
        final.StartTimeUtc.Should().Be(new DateTimeOffset(2026, 7, 26, 11, 30, 0, TimeSpan.Zero), "dateTimeUtc is used; the offset-less local time never is");
        final.Status.Should().Be(VolleyballMatchStatus.Finished);
        final.HomeTeam.Should().BeEquivalentTo(new { CountryCode = "TUR", Name = "Türkiye", Gender = TeamGender.Women, Level = TeamLevel.Senior, Kind = TeamKind.NationalTeam, AgeLimit = (int?)null });
        final.AwayTeam.CountryCode.Should().Be("BRA");
        (final.HomeSets, final.AwaySets).Should().Be((3, 1));
        final.Sets.Select(s => (s.HomePoints, s.AwayPoints, s.Completed)).Should().Equal((23, 25, true), (25, 23, true), (26, 24, true), (25, 21, true));
        final.Venue.Should().Be("East Asian Games Dome");
        final.Broadcasts.Should().BeEmpty("VIS has no broadcaster data; nothing is invented");
        MatchProgress.Validate(final).Should().BeNull();
        Sultanlar.Evaluate(final).Should().Be(new MatchFilterResult(MatchFilterReason.Accepted, FollowedSide.Home));

        var fiveSets = list.Matches.Single(m => m.ProviderMatchId == "26554");
        (fiveSets.HomeSets, fiveSets.AwaySets).Should().Be((2, 3));
        fiveSets.Sets.Should().HaveCount(5);
        Sultanlar.Evaluate(fiveSets).Side.Should().Be(FollowedSide.Away);
    }

    [Fact]
    public void Eurovolley_2026_discovery_response_is_accepted_and_the_final_is_valid()
    {
        var list = Parse("fivb-discovery-eurovolley2026w.json", out var problem);
        problem.Should().BeNull();
        list.Matches.Should().OnlyContain(m => Sultanlar.Evaluate(m).Accepted);
        var final = list.Matches.Single(m => m.ProviderMatchId == "28900");
        (final.HomeTeam.CountryCode, final.AwayTeam.CountryCode, final.HomeSets, final.AwaySets).Should().Be(("ITA", "TUR", 2, 3));
        final.Sets[4].Should().Be(new VolleyballSet(5, 10, 15, true));
        MatchProgress.Validate(final).Should().BeNull();
    }

    [Theory]
    [InlineData("fivb-u17-women-tur.json", "U17 girls (age-group world championship)")]
    [InlineData("fivb-men-tur.json", "Türkiye MEN")]
    [InlineData("fivb-test-tournament-tur.json", "the VIS TEST tournament 'VNL 2026 - WOMEN (TEST ONLY)'")]
    public void Real_decoys_with_the_TUR_code_are_never_accepted(string fixture, string what)
    {
        var list = Parse(fixture, out _);
        list.Matches.Should().NotBeEmpty();
        list.Matches.Should().OnlyContain(m => m.HomeTeam.CountryCode == "TUR" || m.AwayTeam.CountryCode == "TUR");
        list.Matches.Should().OnlyContain(m => !Sultanlar.Evaluate(m).Accepted, what + " must be rejected");
    }

    [Fact]
    public void Recorded_in_play_status_maps_to_live_with_the_current_set()
    {
        // The TEST tournament recording contains a match frozen at "Set 1 finished" (status 6), 0-1.
        var match = Parse("fivb-test-tournament-tur.json", out _).Matches.Single(m => m.ProviderMatchId == "27826");
        match.Status.Should().Be(VolleyballMatchStatus.Live);
        (match.HomeSets, match.AwaySets).Should().Be((0, 1));
        match.Sets.Should().ContainSingle().Which.Should().Be(new VolleyballSet(1, 13, 25, true));
        (match.CurrentSetHomePoints, match.CurrentSetAwayPoints).Should().Be(((int?)null, (int?)null), "between sets there is no running set score");
    }

    [Theory]
    [InlineData("VNL 2027 - WOMEN (TEST ONLY)")]
    [InlineData("Women's Club World Championship 2027")]
    public void Test_or_club_tournaments_are_never_senior_national_teams_even_with_a_senior_type(string name)
    {
        var m = ParseText("{\"data\":[" + Item().Replace("Women's Volleyball Nations League 2027", name, StringComparison.Ordinal) + "]}", out _).Matches.Single();
        Sultanlar.Evaluate(m).Accepted.Should().BeFalse();
        var nameless = ParseText("{\"data\":[" + Item().Replace("\"name\":\"Women's Volleyball Nations League 2027\",", "", StringComparison.Ordinal) + "]}", out _).Matches.Single();
        Sultanlar.Evaluate(nameless).Accepted.Should().BeFalse("a tournament without a name is not trusted");
    }

    [Fact]
    public async Task A_frozen_vis_version_keeps_the_old_update_time_so_the_feed_turns_stale()
    {
        var body = "{\"data\":[" + Item().Replace("\"status\":1", "\"status\":5", StringComparison.Ordinal).Replace("\"matchPointsA\":null,\"matchPointsB\":null", "\"matchPointsA\":0,\"matchPointsB\":0,\"version\":42", StringComparison.Ordinal) + "],\"nbItems\":1,\"version\":900}";
        var clock = new FakeTimeProvider(new DateTimeOffset(2027, 6, 3, 16, 5, 0, TimeSpan.Zero));
        var (provider, _) = Provider(_ => Json(body), new FivbVisOptions { MaxRetries = 0, FullLiveRefreshEvery = 1 }, clock);
        var first = (await provider.GetLiveStateAsync(["1"], CancellationToken.None)).Value!.Single();
        clock.Advance(TimeSpan.FromMinutes(25));
        var later = (await provider.GetLiveStateAsync(["1"], CancellationToken.None)).Value!.Single();
        later.LastProviderUpdateUtc.Should().Be(first.LastProviderUpdateUtc, "the same VIS item version means nothing changed");
    }

    [Theory]
    [InlineData(1, 0, VolleyballMatchStatus.Scheduled)]
    [InlineData(3, 0, VolleyballMatchStatus.Scheduled)]
    [InlineData(4, 0, VolleyballMatchStatus.Live)]
    [InlineData(12, 0, VolleyballMatchStatus.Live)]
    [InlineData(23, 0, VolleyballMatchStatus.Live)]
    [InlineData(24, 0, VolleyballMatchStatus.Finished)]
    [InlineData(26, 0, VolleyballMatchStatus.Finished)]
    [InlineData(27, 0, VolleyballMatchStatus.Finished)]
    [InlineData(25, 2, VolleyballMatchStatus.Unknown)] // forfeit: not a played result
    [InlineData(0, 0, VolleyballMatchStatus.Unknown)]
    [InlineData(99, 0, VolleyballMatchStatus.Unknown)]
    public void Status_mapping_follows_the_documented_vis_order(int status, int resultType, VolleyballMatchStatus expected) =>
        FivbVisParser.MapStatus(status, resultType).Should().Be(expected);

    // ------------------------------------------------------------------ malformed / partial items

    private static string Item(string overrides = "") =>
        "{\"no\":1,\"status\":1,\"teamACode\":\"TUR\",\"teamBCode\":\"ITA\",\"teamAName\":\"Türkiye\",\"teamBName\":\"Italy\",\"matchPointsA\":null,\"matchPointsB\":null," +
        "\"dateTimeUtc\":\"2027-06-03T16:00:00Z\",\"scheduleInfo\":4,\"tournament\":{\"no\":5,\"name\":\"Women's Volleyball Nations League 2027\",\"season\":\"2027\",\"gender\":1,\"type\":12}" + overrides + "}";

    private static FivbMatchList ParseText(string json, out string? problem) => FivbVisParser.Parse(JsonDocument.Parse(json), FivbVisParser.SeniorTypeValues, out problem);

    [Fact]
    public void A_scheduled_future_match_parses_and_a_tbc_time_is_not_a_start_time()
    {
        var ok = ParseText("{\"data\":[" + Item() + "],\"nbItems\":1,\"version\":7}", out var problem);
        problem.Should().BeNull();
        ok.Matches.Single().StartTimeUtc.Should().Be(new DateTimeOffset(2027, 6, 3, 16, 0, 0, TimeSpan.Zero));
        ok.Matches.Single().Status.Should().Be(VolleyballMatchStatus.Scheduled);
        ok.Version.Should().Be(7);

        var tbc = ParseText("{\"data\":[" + Item().Replace("\"scheduleInfo\":4", "\"scheduleInfo\":3", StringComparison.Ordinal) + "]}", out _);
        tbc.Matches.Single().StartTimeUtc.Should().BeNull("a 'date/time TBC' never drives a reminder");
        var local = ParseText("{\"data\":[" + Item().Replace("16:00:00Z", "16:00:00", StringComparison.Ordinal) + "]}", out _);
        local.Matches.Single().StartTimeUtc.Should().BeNull("an offset-less time is never guessed into a zone");
    }

    [Fact]
    public void Missing_fields_are_a_schema_change_not_no_data()
    {
        var missing = ParseText("{\"data\":[" + Item().Replace("\"matchPointsA\":null,", "", StringComparison.Ordinal) + "]}", out var problem);
        missing.Matches.Should().BeEmpty();
        problem.Should().Contain("matchPointsA");

        ParseText("{\"items\":[]}", out problem);
        problem.Should().Be("response has no data array");
    }

    [Fact]
    public void An_empty_list_is_a_valid_answer()
    {
        var empty = ParseText("{\"data\":[],\"nbItems\":0,\"version\":57446011}", out var problem);
        problem.Should().BeNull();
        empty.Matches.Should().BeEmpty();
    }

    [Fact]
    public void Age_markers_in_tournament_or_team_names_make_the_team_an_age_group()
    {
        var u19 = ParseText("{\"data\":[" + Item().Replace("Women's Volleyball Nations League 2027", "CEV U19 Volleyball European Championship Women", StringComparison.Ordinal)
            .Replace("\"type\":12", "\"type\":10", StringComparison.Ordinal) + "]}", out _).Matches.Single();
        u19.HomeTeam.Level.Should().Be(TeamLevel.AgeGroup);
        u19.HomeTeam.AgeLimit.Should().Be(19);
        Sultanlar.Evaluate(u19).Accepted.Should().BeFalse();

        var juniorTeam = ParseText("{\"data\":[" + Item().Replace("\"Türkiye\"", "\"Türkiye Junior\"", StringComparison.Ordinal) + "]}", out _).Matches.Single();
        Sultanlar.Evaluate(juniorTeam).Accepted.Should().BeFalse();
    }

    // ------------------------------------------------------------------ HTTP behaviour

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string?> AppIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
            AppIds.Add(request.Headers.TryGetValues("X-FIVB-App-ID", out var values) ? values.Single() : null);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Retry back-off waits on the clock: tests that retry use the real clock (sub-second delays), the rest a fixed fake clock.</summary>
    private static (FivbVisProvider Provider, StubHandler Handler) Provider(Func<HttpRequestMessage, HttpResponseMessage> respond, FivbVisOptions? o = null, TimeProvider? time = null)
    {
        var clock = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(respond);
        var options = Options.Create(o ?? new FivbVisOptions { MaxRetries = 0 });
        var client = new FivbVisClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.fivb.org/Vis2009/") }, new VbRequestBudget(clock), options, clock,
            NullLogger<FivbVisClient>.Instance);
        return (new FivbVisProvider(client, options, new VbDataMode(VbProviderMode.Live), NullLogger<FivbVisProvider>.Instance), handler);
    }

    private static readonly DateTimeOffset From = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Discovery_asks_vis_for_senior_women_tournaments_only_and_keeps_turkish_matches()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "volleyball", "fivb-vnl2026w-tur.json"));
        var (provider, handler) = Provider(_ => Json(body));
        var result = await provider.GetMatchesAsync(Sultanlar, From, From.AddDays(60), CancellationToken.None);
        result.Outcome.Should().Be(VbProviderOutcome.Success);
        result.Value.Should().HaveCount(3);
        var request = handler.Requests.Single();
        request.Should().Contain("GetVolleyMatchList").And.Contain("TournamentGenders=\"W\"").And.Contain("FirstDate=\"2026-09-23\"").And.Contain("LastDate=\"2026-11-22\"")
            .And.Contain("NationsLeague").And.NotContain("Test").And.NotContain("AgeGroup").And.Contain("Relation Name=\"Tournament\"");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, VbProviderOutcome.SchemaChanged)]
    [InlineData(HttpStatusCode.Unauthorized, VbProviderOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, VbProviderOutcome.Unavailable)]
    public async Task Http_errors_are_classified_and_never_become_an_empty_list(HttpStatusCode status, VbProviderOutcome outcome)
    {
        var (provider, _) = Provider(_ => new HttpResponseMessage(status));
        var result = await provider.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None);
        result.Outcome.Should().Be(outcome);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Rate_limit_keeps_the_providers_retry_after()
    {
        var (provider, handler) = Provider(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return r;
        }, new FivbVisOptions { MaxRetries = 3 });
        var result = await provider.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None);
        result.Outcome.Should().Be(VbProviderOutcome.RateLimited);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
        handler.Requests.Should().ContainSingle("429 is never retried inline");
    }

    [Fact]
    public async Task A_cloudflare_style_html_block_is_unavailable_not_data()
    {
        var (provider, _) = Provider(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>challenge</html>", Encoding.UTF8, "text/html") });
        (await provider.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None)).Outcome.Should().Be(VbProviderOutcome.Unavailable);
        var (html200, _) = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html/>", Encoding.UTF8, "text/html") });
        (await html200.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None)).Outcome.Should().Be(VbProviderOutcome.SchemaChanged);
    }

    [Fact]
    public async Task Malformed_json_and_truncated_lists_are_schema_errors()
    {
        var (broken, _) = Provider(_ => Json("{\"data\":[{"));
        (await broken.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None)).Outcome.Should().Be(VbProviderOutcome.SchemaChanged);
        var (truncated, _) = Provider(_ => Json("{\"data\":[" + Item() + "],\"nbItems\":250,\"version\":1}"));
        var result = await truncated.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None);
        result.Outcome.Should().Be(VbProviderOutcome.SchemaChanged, "a silently truncated list must not look complete");
    }

    [Fact]
    public async Task Server_errors_are_retried_a_bounded_number_of_times()
    {
        var calls = 0;
        var (provider, handler) = Provider(_ => ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Json("{\"data\":[],\"nbItems\":0,\"version\":1}"),
            new FivbVisOptions { MaxRetries = 2 }, TimeProvider.System);
        (await provider.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None)).Outcome.Should().Be(VbProviderOutcome.Success);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Live_polling_uses_the_version_and_no_changes_returns_the_last_known_state()
    {
        var live = "{\"data\":[" + Item().Replace("\"status\":1", "\"status\":8", StringComparison.Ordinal).Replace("\"matchPointsA\":null,\"matchPointsB\":null",
            "\"matchPointsA\":1,\"matchPointsB\":0,\"pointsTeamASet1\":25,\"pointsTeamBSet1\":20,\"pointsTeamASet2\":10,\"pointsTeamBSet2\":8,\"nbSets\":2", StringComparison.Ordinal) +
            "],\"nbItems\":1,\"version\":100}";
        var responses = new Queue<string>([live, "{\"data\":[],\"nbItems\":0,\"version\":100}"]);
        var (provider, handler) = Provider(_ => Json(responses.Dequeue()));

        var first = await provider.GetLiveStateAsync(["1"], CancellationToken.None);
        first.Value.Should().ContainSingle().Which.Should().Match<VolleyballMatch>(m => m.Status == VolleyballMatchStatus.Live && m.CurrentSet == 2 && m.CurrentSetHomePoints == 10);
        handler.Requests[0].Should().Contain("NoMatches=\"1\"").And.NotContain("Version=");

        var second = await provider.GetLiveStateAsync(["1"], CancellationToken.None);
        handler.Requests[1].Should().Contain("Version=\"100\"");
        second.Value.Should().ContainSingle().Which.Should().Be(first.Value![0], "\"no changes\" means the last full state is still current");
    }

    [Fact]
    public async Task Live_polling_does_a_periodic_full_refresh()
    {
        var body = "{\"data\":[" + Item() + "],\"nbItems\":1,\"version\":5}";
        var (provider, handler) = Provider(_ => Json(body), new FivbVisOptions { MaxRetries = 0, FullLiveRefreshEvery = 3 });
        for (var i = 0; i < 6; i++)
            await provider.GetLiveStateAsync(["1"], CancellationToken.None);
        handler.Requests.Count(r => !r.Contains("Version=", StringComparison.Ordinal)).Should().Be(3, "first call + every 3rd call");
    }

    [Fact]
    public async Task Requests_escape_values_and_send_the_application_id_only_as_a_header()
    {
        FivbVisClient.LiveRequest(["1\" /><Evil"]).Should().NotContain("<Evil").And.Contain("&lt;Evil");
        var (provider, handler) = Provider(_ => Json("{\"data\":[],\"nbItems\":0}"), new FivbVisOptions { AppId = "secret-app-id", MaxRetries = 0 });
        await provider.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None);
        handler.Requests.Single().Should().NotContain("secret-app-id", "the application id never appears in a URL");
        handler.AppIds.Single().Should().Be("secret-app-id");
        var (anonymous, anonymousHandler) = Provider(_ => Json("{\"data\":[],\"nbItems\":0}"));
        await anonymous.GetMatchesAsync(Sultanlar, From, From.AddDays(1), CancellationToken.None);
        anonymousHandler.AppIds.Single().Should().BeNull();
    }
}
