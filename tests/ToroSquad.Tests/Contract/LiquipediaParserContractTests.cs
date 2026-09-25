using System.Text.Json;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Liquipedia;

namespace ToroSquad.Tests.Contract;

/// <summary>Contract tests on the LPDB v3 wire shape (synthetic fixture with fixed dates). Criterion 7.</summary>
public sealed class LiquipediaParserContractTests
{
    private static readonly Lazy<(Dictionary<string, EsportsMatch> Matches, List<string> Warnings)> Parsed = new(() =>
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "liquipedia", "matches-contract.json"));
        using var doc = JsonDocument.Parse(json);
        var warnings = new List<string>();
        var matches = doc.RootElement.GetProperty("result").EnumerateArray()
            .Select(e => LiquipediaParser.ParseMatch(e.Clone(), "counterstrike", warnings))
            .OfType<EsportsMatch>()
            .ToDictionary(m => m.Key.Id);
        return (matches, warnings);
    });

    private static EsportsMatch M(string id) => Parsed.Value.Matches[id];

    [Fact]
    public void Hltv_match_page_comes_from_liquipedias_links_field()
    {
        M("C-BO3-FIN").Links!.HltvMatchUrl.Should().Be("https://www.hltv.org/matches/2399001/match");
        M("C-BO3-FIN").SourceUrl.Should().StartWith("https://liquipedia.net/");
        Parsed.Value.Matches.Values.Where(m => m.Key.Id != "C-BO3-FIN").Should().OnlyContain(m => m.Links == null, "no link is invented");
    }

    [Theory]
    [InlineData("""{"links":{"hltv":{"1":{"1":"https://www.hltv.org/matches/2399001/match","2":0}}}}""", "https://www.hltv.org/matches/2399001/match")]
    [InlineData("""{"links":{"hltv":"https://www.hltv.org/matches/2399002/match"}}""", "https://www.hltv.org/matches/2399002/match")]
    [InlineData("""{"links":{"hltv":[["https://www.hltv.org/matches/2399003/match",0]]}}""", "https://www.hltv.org/matches/2399003/match")]
    [InlineData("""{"links":{"hltv":{"1":{"1":"https://evil.example/matches/1/match"}}}}""", null)]
    [InlineData("""{"links":{"hltv":{"1":{"1":"javascript:alert(1)"}}}}""", null)]
    [InlineData("""{"links":{"faceit":{"1":{"1":"https://www.faceit.com/en/match/room/x"}}}}""", null)]
    [InlineData("""{"links":[]}""", null)]
    [InlineData("""{"links":null}""", null)]
    [InlineData("""{}""", null)]
    public void Liquipedia_hltv_link_shapes_are_read_and_validated(string json, string? expected)
    {
        using var doc = JsonDocument.Parse(json);
        LiquipediaParser.HltvLink(doc.RootElement).Should().Be(expected);
    }

    [Fact]
    public void Finished_bo3_keeps_series_score_winner_and_complete_maps()
    {
        var m = M("C-BO3-FIN");
        m.Key.Should().Be(new MatchKey("liquipedia:counterstrike", "C-BO3-FIN"));
        m.Status.Should().Be(MatchStatus.Finished);
        m.ScheduledStartUtc.Should().Be(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero), "LPDB date is UTC without offset");
        m.A.Team!.Key.Should().Be("counterstrike/Alpha_Team", "identity is the team page, not the display name");
        m.A.Score.Should().Be(2);
        m.B.Score.Should().Be(1);
        m.WinnerIndex.Should().Be(0);
        m.WinnerName.Should().Be("Alpha");
        m.MapsComplete.Should().BeTrue();
        m.Maps.Select(x => (x.ScoreA, x.ScoreB)).Should().Equal((13, 7), (11, 13), (13, 10));
        m.Streams.Should().ContainSingle(s => s.Platform == "twitch", "duplicate channels collapse, invalid channel names are dropped");
        m.Tournament.Tier.Should().Be("1");
    }

    [Fact]
    public void Tbd_opponent_is_explicit_and_has_no_score()
    {
        var m = M("C-TBD");
        m.B.Kind.Should().Be(OpponentKind.Tbd);
        m.B.Team.Should().BeNull();
        m.A.Score.Should().BeNull("a not-started match has no score, even if the source says -1/0");
        m.Status.Should().Be(MatchStatus.Scheduled);
    }

    [Fact]
    public void Php_empty_arrays_and_missing_games_do_not_break_parsing()
    {
        var m = M("C-EMPTY-ARRAYS");
        m.Streams.Should().BeEmpty();
        m.Maps.Should().BeEmpty();
        m.StartTimeExact.Should().BeFalse();
        m.BestOf.Should().Be(1);
    }

    [Fact]
    public void Forfeit_derives_winner_from_opponent_status_without_inventing_scores()
    {
        var m = M("C-FF");
        m.Status.Should().Be(MatchStatus.Finished);
        m.IsForfeit.Should().BeTrue();
        m.WinnerIndex.Should().Be(1);
        m.SeriesScoreKnown.Should().BeFalse();
        m.Maps.Should().ContainSingle().Which.Status.Should().Be(GameStatus.NotPlayed);
    }

    [Fact]
    public void Not_played_match_is_not_reported_as_finished_or_with_a_winner()
    {
        var m = M("C-NP");
        m.Status.Should().Be(MatchStatus.Cancelled);
        m.StatusEvidence.Should().Contain("not played");
        m.WinnerIndex.Should().BeNull();
    }

    [Fact]
    public void Missing_map_scores_keep_series_score_but_mark_maps_incomplete()
    {
        var m = M("C-MISSING-MAPSCORES");
        m.SeriesScoreKnown.Should().BeTrue();
        m.MapsComplete.Should().BeFalse();
        m.Maps[0].Status.Should().Be(GameStatus.Played);
        m.Maps[0].ScoreA.Should().BeNull("no score is produced from missing data");
        m.Maps[1].Status.Should().Be(GameStatus.Unknown);
    }

    [Fact]
    public void Numbers_as_strings_are_accepted()
    {
        var m = M("C-BO1-STRINGS");
        m.Status.Should().Be(MatchStatus.Finished);
        m.BestOf.Should().Be(1);
        m.StartTimeExact.Should().BeTrue();
        m.WinnerIndex.Should().Be(1);
        m.B.Score.Should().Be(1);
        m.MapsComplete.Should().BeTrue();
    }

    [Fact]
    public void Bo5_series_is_complete()
    {
        var m = M("C-BO5");
        (m.A.Score, m.B.Score).Should().Be((3, 2));
        m.MapsComplete.Should().BeTrue();
        m.Maps.Should().HaveCount(5);
    }

    [Fact]
    public void Draw_has_no_winner()
    {
        var m = M("C-DRAW");
        m.IsDraw.Should().BeTrue();
        m.WinnerIndex.Should().BeNull();
    }

    [Fact]
    public void Unidentifiable_and_non_1v1_records_are_skipped_with_warnings_not_exceptions()
    {
        Parsed.Value.Matches.Should().NotContainKey("C-THREE-OPP");
        Parsed.Value.Warnings.Should().Contain(w => w.Contains("without match2id", StringComparison.Ordinal));
        Parsed.Value.Warnings.Should().Contain(w => w.Contains("C-THREE-OPP", StringComparison.Ordinal));
        M("C-ONE-OPP").B.Kind.Should().Be(OpponentKind.Unknown);
    }

    [Fact]
    public void Bad_date_becomes_unknown_status_instead_of_a_guess()
    {
        var m = M("C-BAD-DATE");
        m.ScheduledStartUtc.Should().BeNull();
        m.Status.Should().Be(MatchStatus.Unknown);
        Parsed.Value.Warnings.Should().Contain(w => w.Contains("C-BAD-DATE", StringComparison.Ordinal));
    }

    [Fact]
    public void Minus_one_and_status_less_scores_are_not_scores()
    {
        var m = M("C-SCORE-MINUS1");
        m.A.Score.Should().BeNull();
        m.B.Score.Should().BeNull();
    }

    [Fact]
    public void Null_fields_everywhere_are_tolerated()
    {
        var m = M("C-NULLS");
        m.A.Kind.Should().Be(OpponentKind.Team);
        m.B.Kind.Should().Be(OpponentKind.Tbd);
        m.Tournament.Name.Should().NotBeNullOrEmpty();
        m.Status.Should().Be(MatchStatus.Scheduled);
        m.BestOf.Should().BeNull();
    }

    [Fact]
    public void Non_team_opponents_are_unknown_kind()
    {
        M("C-SOLO").A.Kind.Should().Be(OpponentKind.Unknown);
    }

    [Fact]
    public void Started_but_unfinished_match_is_not_live()
    {
        var m = M("C-UNFINISHED-PAST");
        m.Status.Should().Be(MatchStatus.Scheduled, "Liquipedia has no verified live flag; a passed start time is not live evidence");
    }

    [Theory]
    [InlineData("2026-09-24 09:00:00", "2026-09-24T09:00:00+00:00")]
    [InlineData("2026-09-24T09:00:00Z", "2026-09-24T09:00:00+00:00")]
    [InlineData("2026-09-24T12:00:00+03:00", "2026-09-24T09:00:00+00:00")]
    [InlineData("0000-00-00 00:00:00", null)]
    [InlineData("24.09.2026 09:00", null)]
    [InlineData("", null)]
    public void Dates_are_parsed_exactly_as_utc(string input, string? expected)
    {
        var parsed = LiquipediaParser.ParseUtc(input);
        if (expected is null)
            parsed.Should().BeNull();
        else
            parsed.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }
}
