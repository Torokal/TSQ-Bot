using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Valve;

namespace ToroSquad.Tests.Unit;

/// <summary>Criterion 8: filters and ambiguous VRS matching follow the documented rules.</summary>
public sealed class FilterAndRankingTests
{
    private static TeamRef Team(string key, string name, string? shortName = null) => new("liquipedia", key, name, shortName);

    internal static EsportsMatch Match(string id, TeamRef? a, TeamRef? b, string tournament = "Cup/2026", string? tier = "1", string? parent = null) => new(
        new MatchKey("liquipedia:counterstrike", id),
        new TournamentRef("liquipedia", tournament, tournament, tier, null, null, parent),
        new DateTimeOffset(2026, 9, 24, 18, 0, 0, TimeSpan.Zero), true, 3, MatchStatus.Scheduled, "test",
        a is null ? MatchOpponent.Tbd : new MatchOpponent(OpponentKind.Team, a, null, OpponentResult.None),
        b is null ? MatchOpponent.Tbd : new MatchOpponent(OpponentKind.Team, b, null, OpponentResult.None),
        null, false, false, [], null, "https://liquipedia.net/counterstrike/Cup", []);

    private static GuildFilterSet Filters(string[]? teams = null, string[]? tournaments = null, string[]? tiers = null, int? vrs = null) =>
        new((teams ?? []).ToHashSet(), (tournaments ?? []).ToHashSet(), (tiers ?? []).ToHashSet(), vrs);

    private static readonly RankingSnapshot Vrs = new("valve-vrs", "global", new DateOnly(2026, 9, 7), DateTimeOffset.UnixEpoch, "https://github.com/x",
    [
        new(1, 2000, "Spirit", []),
        new(2, 1900, "MOUZ", []),
        new(10, 1500, "Nova", []),
        new(11, 1490, "Nova Esports", []),
        new(40, 900, "Crimson", []),
    ]);

    [Fact]
    public void Empty_filter_set_passes_everything()
    {
        MatchFilter.Evaluate(Match("1", Team("a", "A"), null), GuildFilterSet.None, null).Passes.Should().BeTrue();
    }

    [Fact]
    public void Team_filter_passes_when_either_side_is_selected_and_values_are_ored()
    {
        var f = Filters(teams: ["x", "b"]);
        MatchFilter.Evaluate(Match("1", Team("a", "A"), Team("b", "B")), f, null).Passes.Should().BeTrue();
        MatchFilter.Evaluate(Match("2", Team("x", "X"), Team("c", "C")), f, null).Passes.Should().BeTrue();
        MatchFilter.Evaluate(Match("3", Team("c", "C"), null), f, null).Passes.Should().BeFalse();
    }

    [Fact]
    public void Different_dimensions_are_anded()
    {
        var f = Filters(teams: ["a"], tiers: ["1"]);
        MatchFilter.Evaluate(Match("1", Team("a", "A"), null, tier: "1"), f, null).Passes.Should().BeTrue();
        var rejected = MatchFilter.Evaluate(Match("2", Team("a", "A"), null, tier: "3"), f, null);
        rejected.Passes.Should().BeFalse();
        rejected.Reasons.Should().Equal("tier");
    }

    [Fact]
    public void Tournament_filter_matches_page_or_parent_page()
    {
        var f = Filters(tournaments: ["Major/2026"]);
        MatchFilter.Evaluate(Match("1", null, null, tournament: "Major/2026/Playoffs", parent: "Major/2026"), f, null).Passes.Should().BeTrue();
        MatchFilter.Evaluate(Match("2", null, null, tournament: "Other/2026"), f, null).Passes.Should().BeFalse();
    }

    [Fact]
    public void Missing_tier_does_not_pass_an_active_tier_filter()
    {
        MatchFilter.Evaluate(Match("1", null, null, tier: null), Filters(tiers: ["1"]), null).Passes.Should().BeFalse();
    }

    [Fact]
    public void Vrs_filter_passes_if_at_least_one_team_is_reliably_in_top_n()
    {
        var resolver = new TeamRankingResolver(Vrs, new Dictionary<string, string>());
        var f = Filters(vrs: 5);
        MatchFilter.Evaluate(Match("1", Team("t/Team_Spirit", "Team Spirit"), Team("t/X", "Unknown X")), f, resolver).Passes.Should().BeTrue();
        MatchFilter.Evaluate(Match("2", Team("t/Crimson_Esports", "Crimson Esports"), null), f, resolver).Passes.Should().BeFalse("rank 40 > 5");
    }

    [Fact]
    public void Missing_vrs_data_fails_closed_and_is_reported_separately()
    {
        var decision = MatchFilter.Evaluate(Match("1", Team("t/Spirit", "Spirit"), null), Filters(vrs: 10), rankings: null);
        decision.Verdict.Should().Be(FilterVerdict.BlockedMissingData);
        decision.Passes.Should().BeFalse();
    }

    [Fact]
    public void Ambiguous_vrs_match_is_never_used_for_filtering()
    {
        var resolver = new TeamRankingResolver(Vrs, new Dictionary<string, string>());
        var match = resolver.Resolve(Team("t/Team_Nova", "Team Nova"));
        match.Kind.Should().Be(TeamMatchKind.Ambiguous);
        match.Candidates.Select(c => c.TeamName).Should().BeEquivalentTo("Nova", "Nova Esports");
        MatchFilter.Evaluate(Match("1", Team("t/Team_Nova", "Team Nova"), null), Filters(vrs: 20), resolver).Passes.Should().BeFalse();
    }

    [Fact]
    public void Resolution_order_is_exact_then_alias_then_normalized()
    {
        var resolver = new TeamRankingResolver(Vrs, new Dictionary<string, string> { ["t/Mousesports"] = "MOUZ" });
        resolver.Resolve(Team("t/MOUZ", "mouz")).Kind.Should().Be(TeamMatchKind.Exact, "case-insensitive exact");
        resolver.Resolve(Team("t/Mousesports", "Mousesports")).Kind.Should().Be(TeamMatchKind.Alias);
        resolver.Resolve(Team("t/Team_Spirit", "Team Spirit")).Kind.Should().Be(TeamMatchKind.Normalized);
        resolver.Resolve(Team("t/Nothing", "Nothing Gaming")).Kind.Should().Be(TeamMatchKind.NotFound);
    }

    [Fact]
    public void Folding_is_culture_invariant_for_turkish_dotted_i()
    {
        TeamRankingResolver.Fold("İstanbul Wildcats").Should().Be(TeamRankingResolver.Fold("istanbul wildcats"));
        TeamRankingResolver.Fold("ISTANBUL").Should().Be("istanbul");
    }

    [Fact]
    public void Valve_markdown_is_parsed_by_header_with_duplicate_headings_and_bad_rows()
    {
        const string md = """
            ### Standings as of 2026_09_07<br />
            ### Standings as of 2026_09_07<br />

            | Standing | Points | Team Name | Roster |  |
            | :- | -: | :- | :- | :- |
            | 1 | 2031 | Spirit | donk, magixx | [details](x) |
            | two | 1 | Broken | a | x |
            | 2 |   1902.6 | MOUZ | PR, Spinx | [details](y) |
            | 1 | 2031 | Spirit | donk, magixx | [details](x) |
            """;
        var (entries, skipped) = ValveStandingsParser.Parse(md);
        entries.Select(e => (e.Rank, e.TeamName, e.Points)).Should().Equal((1, "Spirit", 2031), (2, "MOUZ", 1903));
        entries[0].Roster.Should().Equal("donk", "magixx");
        skipped.Should().Be(1);
    }

    [Fact]
    public void Valve_columns_are_found_by_name_even_if_reordered()
    {
        const string md = """
            | Team Name | Standing | Roster | Points |
            | - | - | - | - |
            | Vitality | 5 | apEX | 1841 |
            """;
        var entry = ValveStandingsParser.Parse(md).Entries.Should().ContainSingle().Subject;
        (entry.Rank, entry.Points, entry.TeamName).Should().Be((5, 1841, "Vitality"));
        entry.Roster.Should().Equal("apEX");
    }

    [Theory]
    [InlineData("standings_global_2026_09_07.md", "2026-09-07")]
    [InlineData("standings_global_2024-08-06.md", "2024-08-06")]
    [InlineData("standings_europe_2026_09_07.md", null)]
    [InlineData("standings_global_2026_13_07.md", null)]
    public void Publication_date_comes_from_the_file_name(string name, string? expected)
    {
        var date = ValveStandingsParser.DateFromFileName(name);
        (date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Should().Be(expected);
    }
}
