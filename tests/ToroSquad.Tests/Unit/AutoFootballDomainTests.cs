using ToroSquad.Core.Guilds;
using ToroSquad.Modules.Predictions;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The automatic football opener's pure rules, network-free and deterministic (SYNTHETIC data only — nothing here proves
/// what the real provider returns): club matching, the Türkiye-day schedule, the 1-X-2 selection and conversion, and the
/// options' own checks.
/// </summary>
public sealed class AutoFootballDomainTests
{
    private static readonly TimeZoneInfo Turkey = GuildTime.TryResolve("Europe/Istanbul", out var zone) ? zone : throw new InvalidOperationException();

    private static readonly AutoTiming Timing = new(new TimeOnly(9, 0), TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15), Turkey);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    // ---- clubs ----

    [Theory]
    [InlineData("Galatasaray", "GS")]
    [InlineData("  galatasaray   sk ", "GS")]
    [InlineData("Fenerbahce", "FB")]
    [InlineData("Fenerbahçe", "FB")]
    [InlineData("FENERBAHÇE SK", "FB")]
    [InlineData("Besiktas JK", "BJK")]
    [InlineData("Beşiktaş", "BJK")]
    public void The_three_clubs_match_by_their_exact_known_names(string name, string code) => TrackedTeams.Match(name)!.Code.Should().Be(code);

    [Theory]
    [InlineData("Fenerbahce U19")]
    [InlineData("Galatasaray W")]
    [InlineData("Galatasaray Women")]
    [InlineData("Besiktas Women")]
    [InlineData("Fener")]
    [InlineData("Galatasaray Petrol")]
    [InlineData("Trabzonspor")]
    [InlineData("Fenerbahçe Beko")]
    [InlineData("")]
    [InlineData(null)]
    public void Similar_youth_women_other_sports_and_partial_names_never_match(string? name) => TrackedTeams.Match(name).Should().BeNull();

    // ---- the schedule (Europe/Istanbul, whatever the machine's zone) ----

    [Fact]
    public void An_evening_match_publishes_at_nine_turkiye_time_and_locks_two_minutes_before_kickoff()
    {
        var kickoff = Utc(10, 5, 17); // 20:00 in Türkiye
        AutoSchedule.PublishAt(kickoff, Timing).Should().Be(Utc(10, 5, 6)); // 09:00 in Türkiye
        AutoSchedule.LockAt(kickoff, Timing).Should().Be(Utc(10, 5, 16, 58));
        AutoSchedule.Deadline(kickoff, Timing).Should().Be(Utc(10, 5, 16, 45));
    }

    [Fact]
    public void The_match_day_is_the_turkiye_day_not_the_utc_date()
    {
        var kickoff = Utc(10, 5, 22, 30); // 01:30 on 6 October in Türkiye, still 5 October in UTC
        AutoSchedule.LocalDay(kickoff, Turkey).Should().Be(new DateOnly(2026, 10, 6));
        AutoSchedule.PublishAt(kickoff, Timing).Should().Be(Utc(10, 5, 21), "local midnight of 6 October: the early rule never reaches the previous day");
        AutoSchedule.Window(Utc(10, 5, 20, 59), kickoff, Timing).Should().Be(PublishWindow.NotYet, "23:59 on 5 October in Türkiye");
        AutoSchedule.Window(Utc(10, 5, 21), kickoff, Timing).Should().Be(PublishWindow.Open);
    }

    [Theory]
    [InlineData(7, 0, 5, 0)] // kickoff 10:00 TR → 08:00 TR (two hours before)
    [InlineData(6, 30, 4, 30)] // kickoff 09:30 TR → 07:30 TR
    [InlineData(8, 0, 6, 0)] // kickoff 11:00 TR → 09:00 TR (the two rules meet)
    [InlineData(12, 0, 6, 0)] // kickoff 15:00 TR → 09:00 TR
    [InlineData(22, 0, 21, 0)] // kickoff 01:00 TR next day → that day's local midnight
    public void An_early_kickoff_publishes_two_hours_before_but_never_before_local_midnight(int kickHour, int kickMinute, int publishHour, int publishMinute)
    {
        var kickoff = Utc(10, 5, kickHour, kickMinute);
        AutoSchedule.PublishAt(kickoff, Timing).Should().Be(Utc(10, 5, publishHour, publishMinute));
    }

    [Fact]
    public void A_late_start_opens_todays_missed_card_but_never_within_fifteen_minutes_or_after_kickoff_or_for_yesterday()
    {
        var kickoff = Utc(10, 5, 17);
        AutoSchedule.Window(Utc(10, 5, 5, 59), kickoff, Timing).Should().Be(PublishWindow.NotYet);
        AutoSchedule.Window(Utc(10, 5, 7), kickoff, Timing).Should().Be(PublishWindow.Open, "the bot was down at 09:00 and came back at 10:00");
        AutoSchedule.Window(Utc(10, 5, 16, 44, 59), kickoff, Timing).Should().Be(PublishWindow.Open);
        AutoSchedule.Window(Utc(10, 5, 16, 45), kickoff, Timing).Should().Be(PublishWindow.TooLate, "exactly 15 minutes before kickoff");
        AutoSchedule.Window(Utc(10, 5, 18), kickoff, Timing).Should().Be(PublishWindow.TooLate);
        AutoSchedule.Window(Utc(10, 6, 7), kickoff, Timing).Should().Be(PublishWindow.TooLate, "yesterday's match is never opened today");
    }

    private static DateTimeOffset Utc(int month, int day, int hour, int minute, int second) => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void Odds_attempts_are_spread_before_the_deadline_never_in_the_same_second()
    {
        var now = Utc(10, 5, 6);
        var deadline = Utc(10, 5, 16, 45);
        var attempts = new List<DateTimeOffset>();
        var at = now;
        for (var used = 1; used <= 4; used++)
        {
            if (AutoSchedule.NextAttempt(at, deadline, used, 4) is not { } next)
                break;
            next.Should().BeAfter(at).And.BeBefore(deadline);
            (next - at).Should().BeGreaterThanOrEqualTo(AutoSchedule.MinAttemptGap).And.BeLessThanOrEqualTo(AutoSchedule.MaxAttemptGap);
            attempts.Add(next);
            at = next;
        }

        attempts.Should().HaveCount(3, "after the 4th attempt there is none left");
        AutoSchedule.NextAttempt(Utc(10, 5, 16, 43), deadline, 1, 4).Should().BeNull("no attempt fits before the deadline");
    }

    [Fact]
    public void The_schedule_does_not_depend_on_the_offset_notation_of_the_same_instant()
    {
        var utc = Utc(10, 5, 17);
        var sameInstant = new DateTimeOffset(2026, 10, 5, 20, 0, 0, TimeSpan.FromHours(3));
        (utc == sameInstant).Should().BeTrue();
        AutoSchedule.PublishAt(sameInstant, Timing).Should().Be(AutoSchedule.PublishAt(utc, Timing));
    }

    // ---- odds ----

    private static readonly DateTimeOffset Now = Utc(10, 5, 6);

    /// <summary>SYNTHETIC test data (not a provider response).</summary>
    private static ProviderOddsEvent Match(params ProviderBookmaker[] bookmakers) =>
        new("synthetic0001", "soccer_turkey_super_league", Utc(10, 5, 17), "Galatasaray", "Fenerbahce", bookmakers);

    private static ProviderBookmaker Book(string key, decimal home, decimal draw, decimal away, DateTimeOffset? updated = null, string market = "h2h",
        string drawName = "Draw", string homeName = "Galatasaray", string awayName = "Fenerbahce") =>
        new(key, key.ToUpperInvariant(), [new ProviderMarket(market, updated ?? Now - TimeSpan.FromMinutes(5),
            [new ProviderPrice(awayName, away), new ProviderPrice(drawName, draw), new ProviderPrice(homeName, home)])]);

    private static readonly string[] Priority = ["pinnacle", "onexbet", "marathonbet"];

    [Fact]
    public void The_first_bookmaker_of_the_priority_with_a_complete_set_wins_and_the_outcomes_are_matched_by_name_not_order()
    {
        var (odds, reason) = OddsSelector.Select(Match(Book("marathonbet", 1.9m, 3.5m, 4.0m), Book("onexbet", 1.85m, 3.4m, 4.2m)), Priority, Now, TimeSpan.FromMinutes(30));
        reason.Should().Be(AutoBlockReason.None);
        (odds!.BookmakerKey, odds.HomeX100, odds.DrawX100, odds.AwayX100).Should().Be(("onexbet", 185, 340, 420), "onexbet comes before marathonbet; home/draw/away by name");
    }

    [Fact]
    public void A_set_is_never_mixed_from_several_bookmakers_or_improved_to_the_best_price()
    {
        var incomplete = new ProviderBookmaker("pinnacle", "Pinnacle", [new ProviderMarket("h2h", Now, [new ProviderPrice("Galatasaray", 9m), new ProviderPrice("Fenerbahce", 9m)])]);
        var (odds, _) = OddsSelector.Select(Match(incomplete, Book("onexbet", 1.85m, 3.4m, 4.2m)), Priority, Now, TimeSpan.FromMinutes(30));
        (odds!.BookmakerKey, odds.HomeX100, odds.DrawX100, odds.AwayX100).Should().Be(("onexbet", 185, 340, 420), "the whole set of ONE bookmaker");
    }

    [Theory]
    [InlineData("Beraberlik")] // a draw not named "Draw"
    [InlineData("Galatasaray")] // a duplicate outcome
    public void A_missing_draw_or_a_duplicate_outcome_is_an_incomplete_market(string drawName) =>
        OddsSelector.Select(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, drawName: drawName)), Priority, Now, TimeSpan.FromMinutes(30)).Reason
            .Should().Be(AutoBlockReason.IncompleteMarket);

    [Fact]
    public void Outcomes_of_another_match_are_refused()
    {
        OddsSelector.Select(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, homeName: "Besiktas")), Priority, Now, TimeSpan.FromMinutes(30)).Reason
            .Should().Be(AutoBlockReason.IncompleteMarket);
    }

    [Theory]
    [InlineData("h2h_lay")]
    [InlineData("h2h_3_way")]
    [InlineData("draw_no_bet")]
    [InlineData("totals")]
    public void Only_the_plain_h2h_market_is_read(string market) =>
        OddsSelector.Select(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, market: market)), Priority, Now, TimeSpan.FromMinutes(30)).Should()
            .Be(((SelectedOdds?)null, AutoBlockReason.NoOdds));

    [Fact]
    public void A_bookmaker_outside_the_priority_is_never_used_and_no_set_is_never_a_default_price()
    {
        OddsSelector.Select(Match(Book("betfair_ex_eu", 1.8m, 3.3m, 4.0m), Book("williamhill", 1.8m, 3.3m, 4.0m)), Priority, Now, TimeSpan.FromMinutes(30))
            .Should().Be(((SelectedOdds?)null, AutoBlockReason.NoOdds), "no 2.00, no other bookmaker");
        OddsSelector.Select(Match(), Priority, Now, TimeSpan.FromMinutes(30)).Reason.Should().Be(AutoBlockReason.NoOdds);
    }

    [Fact]
    public void Stale_future_or_missing_market_times_are_refused()
    {
        var maxAge = TimeSpan.FromMinutes(30);
        OddsSelector.Select(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, Now - TimeSpan.FromMinutes(31))), Priority, Now, maxAge).Reason.Should().Be(AutoBlockReason.StaleOdds);
        OddsSelector.Select(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, Now + TimeSpan.FromMinutes(10))), Priority, Now, maxAge).Reason.Should().Be(AutoBlockReason.StaleOdds);
        var noTime = new ProviderBookmaker("pinnacle", "Pinnacle", [new ProviderMarket("h2h", null,
            [new ProviderPrice("Galatasaray", 1.8m), new ProviderPrice("Draw", 3.3m), new ProviderPrice("Fenerbahce", 4m)])]);
        OddsSelector.Select(Match(noTime), Priority, Now, maxAge).Reason.Should().Be(AutoBlockReason.StaleOdds);
        OddsSelector.Select(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, Now - TimeSpan.FromMinutes(30))), Priority, Now, maxAge).Odds.Should().NotBeNull("exactly 30 minutes is still fresh");
    }

    [Theory]
    [InlineData("1.856", 185)] // rounded DOWN: never more than the source quoted
    [InlineData("1.85", 185)]
    [InlineData("1.01", 101)]
    [InlineData("1000", 100000)]
    [InlineData("2.999999", 299)]
    public void Prices_convert_to_the_fixed_model_rounding_down(string price, int x100) =>
        OddsSelector.ToX100(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(x100);

    [Theory]
    [InlineData("1")]
    [InlineData("1.005")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("1000.01")]
    [InlineData("5000")]
    public void Prices_outside_the_domain_are_refused_never_clamped(string price) =>
        OddsSelector.ToX100(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture)).Should().BeNull();

    [Fact]
    public void A_price_outside_the_domain_makes_the_set_invalid()
    {
        OddsSelector.Select(Match(Book("pinnacle", 1.0m, 3.3m, 4.0m)), Priority, Now, TimeSpan.FromMinutes(30)).Reason.Should().Be(AutoBlockReason.InvalidOdds);
        OddsSelector.Select(Match(Book("pinnacle", 1.8m, 3.3m, 1500m)), Priority, Now, TimeSpan.FromMinutes(30)).Reason.Should().Be(AutoBlockReason.InvalidOdds);
    }

    [Fact]
    public void The_reason_codes_are_the_documented_ones()
    {
        new[] { AutoBlockReason.NoOdds, AutoBlockReason.IncompleteMarket, AutoBlockReason.StaleOdds, AutoBlockReason.UnsupportedCompetition, AutoBlockReason.AmbiguousMatch,
                AutoBlockReason.QuotaPaused, AutoBlockReason.AuthError, AutoBlockReason.ProviderUnavailable, AutoBlockReason.TooLateToPublish }
            .Select(AutoBlockCodes.Code).Should().Equal("NO_ODDS", "INCOMPLETE_MARKET", "STALE_ODDS", "UNSUPPORTED_COMPETITION", "AMBIGUOUS_MATCH", "QUOTA_PAUSED",
                "AUTH_ERROR", "PROVIDER_UNAVAILABLE", "TOO_LATE_TO_PUBLISH");
        Enum.GetValues<AutoBlockReason>().Select(AutoBlockCodes.Code).Should().OnlyHaveUniqueItems().And.NotContain("UNKNOWN");
    }

    // ---- options ----

    [Fact]
    public void The_defaults_are_valid_and_disabled()
    {
        var o = new AutoFootballOptions();
        o.Problems().Should().BeEmpty();
        o.ParsedMode.Should().Be(AutomationMode.Disabled);
        o.Competitions.Should().Equal("soccer_turkey_super_league", "soccer_uefa_champs_league", "soccer_uefa_champs_league_qualification",
            "soccer_uefa_europa_league", "soccer_uefa_europa_conference_league");
        o.Bookmakers.Should().NotContain(b => AutoFootballOptions.IsExchange(b));
        o.Timing()!.PublishLocalTime.Should().Be(new TimeOnly(9, 0));
        typeof(AutoFootballOptions).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Key", StringComparison.Ordinal) && n != nameof(AutoFootballOptions.CompetitionKeys),
            "the API key is a secret and never part of the options");
    }

    [Theory]
    [InlineData("Mode", "Sometimes")]
    [InlineData("Mode", "3")]
    [InlineData("Provider", "OddsApiIo")]
    [InlineData("BaseUrl", "https://api.odds-api.io/")]
    [InlineData("BaseUrl", "http://api.the-odds-api.com/")]
    [InlineData("PublishLocalTime", "9")]
    [InlineData("TimeZone", "Mars/Olympus")]
    [InlineData("Region", "eu,uk")]
    [InlineData("MinLeadTimeToPublishMinutes", "1")]
    [InlineData("MaxOddsAttemptsPerEvent", "50")]
    [InlineData("CompetitionKeys", "soccer_turkey_cup")]
    [InlineData("BookmakerPriority", "betfair_ex_eu")]
    [InlineData("BookmakerPriority", "matchbook")]
    public void A_bad_value_is_reported_and_keeps_the_automation_off(string property, string value)
    {
        var o = new AutoFootballOptions { Mode = "Live" };
        var p = typeof(AutoFootballOptions).GetProperty(property)!;
        if (p.PropertyType == typeof(string[]))
            p.SetValue(o, new[] { value });
        else if (p.PropertyType == typeof(int))
            p.SetValue(o, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        else
            p.SetValue(o, value);
        o.Problems().Should().NotBeEmpty(property + "=" + value);
    }
}
