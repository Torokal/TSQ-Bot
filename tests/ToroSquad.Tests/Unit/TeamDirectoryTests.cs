using System.Net;
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

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Teams without a match in the poll window must still be pickable for filters and follows (owner request, 2026-09-25:
/// Aurora had no match in 48 h and did not appear in autocomplete). Search is time-boxed, cached and never breaks the
/// picker; same-named teams are told apart by acronym and location.
/// </summary>
public sealed class TeamDirectoryTests
{
    private static readonly TeamSearchHit AuroraGaming = new(new TeamRef("pandascore", "ps-team:131505", "Aurora Gaming", "AUR"), "RU");
    private static readonly TeamSearchHit AuroraIs = new(new TeamRef("pandascore", "ps-team:134704", "AURORA", "AUR"), "IS");
    private static readonly TeamSearchHit YoungBlood = new(new TeamRef("pandascore", "ps-team:133125", "Aurora Young Blood", "AUR.YB"), "RU");

    private sealed class FakeSearch(Func<string, CancellationToken, Task<ProviderResult<IReadOnlyList<TeamSearchHit>>>> search) : IEsportsDataProvider, ITeamSearchProvider
    {
        public List<string> Queries { get; } = [];

        public string Id => "pandascore";

        public ProviderCapability Capabilities => ProviderCapability.Teams;

        public bool IsConfigured => true;

        public Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderResult<IReadOnlyList<TeamSearchHit>>> SearchTeamsAsync(string query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return search(query, cancellationToken);
        }
    }

    private static ProviderResult<IReadOnlyList<TeamSearchHit>> Ok(params TeamSearchHit[] hits) => ProviderResult<IReadOnlyList<TeamSearchHit>>.Ok(hits, TestHost.T0);

    private static (TeamDirectory Directory, EsportsCache Cache) Create(IEsportsDataProvider provider, TimeProvider? clock = null, TimeSpan? timeout = null)
    {
        var cache = new EsportsCache(Options.Create(new EsportsOptions()));
        return (new TeamDirectory(cache, provider, clock ?? new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TestHost.T0), NullLogger<TeamDirectory>.Instance)
        {
            SearchTimeout = timeout ?? TimeSpan.FromSeconds(2),
        }, cache);
    }

    [Fact]
    public async Task A_team_without_a_match_in_the_window_is_found_and_same_names_are_told_apart()
    {
        var provider = new FakeSearch((_, _) => Task.FromResult(Ok(AuroraIs, AuroraGaming, YoungBlood)));
        var (directory, cache) = Create(provider);

        var suggestions = await directory.SuggestAsync("aurora", CancellationToken.None);

        suggestions.Select(s => (s.Display, s.Key)).Should().Equal(
            ("AURORA (AUR · IS)", "ps-team:134704"),
            ("Aurora Gaming (AUR · RU)", "ps-team:131505"),
            ("Aurora Young Blood (AUR.YB · RU)", "ps-team:133125"));
        cache.Teams.Should().Contain(t => t.Key == "ps-team:131505" && t.Name == "Aurora Gaming", "a picked team resolves to its name (labels, follows)");
    }

    [Fact]
    public async Task Known_teams_come_first_and_short_input_does_not_search()
    {
        var provider = new FakeSearch((_, _) => Task.FromResult(Ok(AuroraGaming)));
        var (directory, cache) = Create(provider);
        cache.RememberTeams([new TeamRef("pandascore", "ps-team:129413", "Eternal Fire", "EF")]);

        (await directory.SuggestAsync("et", CancellationToken.None)).Select(s => s.Key).Should().Equal("ps-team:129413");
        provider.Queries.Should().BeEmpty($"fewer than {TeamDirectory.MinSearchLength} characters never call the provider");

        var mixed = await directory.SuggestAsync("aur", CancellationToken.None);
        mixed.Select(s => s.Key).Should().Equal("ps-team:131505");
    }

    [Fact]
    public async Task Searches_are_cached_and_a_complete_shorter_result_is_reused()
    {
        var provider = new FakeSearch((_, _) => Task.FromResult(Ok(AuroraIs, AuroraGaming, YoungBlood)));
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TestHost.T0);
        var (directory, _) = Create(provider, clock);

        await directory.SuggestAsync("aur", CancellationToken.None);
        (await directory.SuggestAsync("auror", CancellationToken.None)).Should().HaveCount(3);
        (await directory.SuggestAsync("aurora young", CancellationToken.None)).Select(s => s.Key).Should().Equal("ps-team:133125");
        await directory.SuggestAsync("aur", CancellationToken.None);
        provider.Queries.Should().Equal(["aur"], "typing further or repeating reuses the complete cached result");

        clock.Advance(directory.CacheTtl + TimeSpan.FromMinutes(1));
        await directory.SuggestAsync("aur", CancellationToken.None);
        provider.Queries.Should().HaveCount(2, "the cache expires");
    }

    [Fact]
    public async Task Provider_failure_or_timeout_shows_known_teams_and_is_not_cached()
    {
        var failing = new FakeSearch((_, _) => Task.FromResult(ProviderResult<IReadOnlyList<TeamSearchHit>>.Fail(ProviderOutcome.QuotaExceeded, "budget", TestHost.T0)));
        var (directory, cache) = Create(failing);
        cache.RememberTeams([new TeamRef("pandascore", "ps-team:1", "Auroral Known", null)]);
        (await directory.SuggestAsync("aur", CancellationToken.None)).Select(s => s.Key).Should().Equal("ps-team:1");
        await directory.SuggestAsync("aur", CancellationToken.None);
        failing.Queries.Should().HaveCount(2, "a failure is not cached as 'no teams'");

        var slow = new FakeSearch(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        });
        var (slowDirectory, _) = Create(slow, timeout: TimeSpan.FromMilliseconds(100));
        (await slowDirectory.SuggestAsync("aurora", CancellationToken.None)).Should().BeEmpty("autocomplete answers in time without the search");
    }

    [Fact]
    public async Task Pandascore_team_search_reads_one_page_and_parses_key_name_acronym_location()
    {
        HttpRequestMessage? seen = null;
        var requests = 0;
        var options = new PandaScoreOptions { Token = "test-token-not-real", PageSize = 2, MaxPages = 5, MaxRetries = 0, TimeoutSeconds = 2 };
        var handler = new StubHttpHandler((r, _) =>
        {
            seen = r;
            requests++;
            var body = new JsonArray(
                new JsonObject { ["id"] = 131505, ["name"] = "Aurora Gaming", ["acronym"] = "AUR", ["location"] = "RU" },
                new JsonObject { ["id"] = 134704, ["name"] = "AURORA", ["acronym"] = null, ["location"] = "" },
                new JsonObject { ["name"] = "no id" }).ToJsonString();
            return Task.FromResult(StubHttpHandler.Json(body));
        });
        var client = new PandaScoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.pandascore.test/") },
            Options.Create(options), new RequestBudget(TimeProvider.System), TimeProvider.System, NullLogger<PandaScoreClient>.Instance);
        var provider = new PandaScoreProvider(client, new EsportsDataMode(ProviderMode.Live));

        var result = await provider.SearchTeamsAsync("aurora", CancellationToken.None);

        requests.Should().Be(1, "one page only, even when the page is full");
        seen!.RequestUri!.AbsolutePath.Should().EndWith("/csgo/teams");
        HttpUtility.ParseQueryString(seen.RequestUri.Query)["search[name]"].Should().Be("aurora");
        seen.Headers.Authorization!.Scheme.Should().Be("Bearer");
        result.Value!.Select(h => (h.Team.Key, h.Team.Name, h.Team.ShortName, h.Location)).Should().Equal(
            ("ps-team:131505", "Aurora Gaming", "AUR", "RU"),
            ("ps-team:134704", "AURORA", (string?)null, (string?)null));

        var unauthorized = new PandaScoreProvider(new PandaScoreClient(new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))))
        {
            BaseAddress = new Uri("https://api.pandascore.test/"),
        }, Options.Create(options), new RequestBudget(TimeProvider.System), TimeProvider.System, NullLogger<PandaScoreClient>.Instance), new EsportsDataMode(ProviderMode.Live));
        (await unauthorized.SearchTeamsAsync("aurora", CancellationToken.None)).Outcome.Should().Be(ProviderOutcome.AuthFailed);
    }

    [Fact]
    public async Task Fixture_mode_searches_the_synthetic_teams_end_to_end()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" });
        var directory = host.Services.GetRequiredService<TeamDirectory>();

        var suggestions = await directory.SuggestAsync("nordic", CancellationToken.None);

        suggestions.Should().ContainSingle().Which.Display.Should().StartWith("Nordic Owls");
        host.Services.GetRequiredService<EsportsCache>().Teams.Should().Contain(t => t.Key == suggestions[0].Key);
    }
}
