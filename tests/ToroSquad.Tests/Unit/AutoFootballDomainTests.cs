using ToroSquad.Core.Guilds;
using ToroSquad.Modules.Predictions;
using ToroSquad.Modules.Predictions.Application.Automation;
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

    private static AutoTiming Hours(int publishBefore) => new(TimeSpan.FromHours(publishBefore), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15), Turkey);

    private static readonly AutoTiming Timing = Hours(24);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute, int second) => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    // ---- the schedule: durations before the planned kickoff (no calendar day, no 09:00, no machine zone) ----

    [Fact]
    public void A_match_publishes_exactly_24_hours_before_kickoff_and_locks_two_minutes_before_it()
    {
        var kickoff = Utc(10, 12, 17); // Monday 12 October, 20:00 in Türkiye
        AutoSchedule.PublishAt(kickoff, Timing).Should().Be(Utc(10, 11, 17), "11 October 20:00 in Türkiye");
        AutoSchedule.LockAt(kickoff, Timing).Should().Be(Utc(10, 12, 16, 58), "19:58: the lock is unchanged");
        AutoSchedule.Deadline(kickoff, Timing).Should().Be(Utc(10, 12, 16, 45));
        AutoSchedule.Window(Utc(10, 11, 16, 59, 59), kickoff, Timing).Should().Be(PublishWindow.NotYet, "A: 19:59:59 the day before");
        AutoSchedule.Window(Utc(10, 11, 17), kickoff, Timing).Should().Be(PublishWindow.Open, "A: exactly 24 × 60 minutes before");
    }

    [Theory]
    [InlineData(21, 30, 4, 21, 30)] // B: kickoff 00:30 TR on the 5th (21:30 UTC on the 4th) → 00:30 TR the day before, across the date change
    [InlineData(19, 0, 4, 19, 0)] // C: kickoff 22:00 TR → 22:00 TR the day before, never 09:00
    [InlineData(6, 0, 4, 6, 0)] // a 09:00 TR kickoff: the old rules do not exist
    [InlineData(4, 30, 4, 4, 30)] // an early 07:30 TR kickoff: no "two hours before", no local midnight
    public void The_publish_time_is_the_kickoff_minus_24_hours_whatever_the_hour_or_the_calendar_day(int kickHour, int kickMinute, int publishDay, int publishHour, int publishMinute)
    {
        var kickoff = Utc(10, 5, kickHour, kickMinute);
        AutoSchedule.PublishAt(kickoff, Timing).Should().Be(Utc(10, publishDay, publishHour, publishMinute));
        (kickoff - AutoSchedule.PublishAt(kickoff, Timing)).Should().Be(TimeSpan.FromMinutes(24 * 60));
    }

    [Theory]
    [InlineData(12, 12, 5)] // D: 12 h → the same day 08:00 TR for a 20:00 TR kickoff
    [InlineData(36, 11, 5)]
    [InlineData(48, 10, 17)] // E: 48 h → two days before, 20:00 TR
    public void The_lead_follows_the_setting(int hours, int publishDay, int publishHourUtc)
    {
        var kickoff = Utc(10, 12, 17);
        AutoSchedule.PublishAt(kickoff, Hours(hours)).Should().Be(Utc(10, publishDay, publishHourUtc));
        AutoSchedule.LockAt(kickoff, Hours(hours)).Should().Be(Utc(10, 12, 16, 58), "the lock never depends on the publish lead");
    }

    [Fact]
    public void A_late_discovery_or_a_late_start_opens_the_card_at_once_but_never_within_fifteen_minutes_or_after_kickoff()
    {
        var kickoff = Utc(10, 12, 17);
        AutoSchedule.Window(Utc(10, 10, 17), kickoff, Timing).Should().Be(PublishWindow.NotYet, "F: found 48 h ahead — not before its publish time");
        AutoSchedule.Window(Utc(10, 12, 6), kickoff, Timing).Should().Be(PublishWindow.Open, "G: first seen at 09:00 on the match day, after the publish time");
        AutoSchedule.Window(Utc(10, 12, 16, 44, 59), kickoff, Timing).Should().Be(PublishWindow.Open);
        AutoSchedule.Window(Utc(10, 12, 16, 45), kickoff, Timing).Should().Be(PublishWindow.TooLate, "exactly 15 minutes before kickoff");
        AutoSchedule.Window(Utc(10, 12, 16, 50), kickoff, Timing).Should().Be(PublishWindow.TooLate, "H: 10 minutes before kickoff");
        AutoSchedule.Window(Utc(10, 12, 18), kickoff, Timing).Should().Be(PublishWindow.TooLate, "a started match");
        AutoSchedule.Window(Utc(10, 13, 7), kickoff, Timing).Should().Be(PublishWindow.TooLate, "yesterday's match is never opened afterwards");
    }

    [Fact]
    public void Odds_attempts_halve_the_time_left_so_four_of_them_reach_from_the_publish_time_to_close_to_kickoff()
    {
        var kickoff = Utc(10, 12, 17);
        var deadline = AutoSchedule.Deadline(kickoff, Timing);
        var at = AutoSchedule.PublishAt(kickoff, Timing);
        var attempts = new List<DateTimeOffset> { at };
        for (var used = 1; used <= 4; used++)
        {
            if (AutoSchedule.NextAttempt(at, deadline, used, 4) is not { } next)
                break;
            next.Should().BeAfter(at).And.BeBefore(deadline);
            (next - at).Should().BeGreaterThanOrEqualTo(AutoSchedule.MinAttemptGap);
            attempts.Add(next);
            at = next;
        }

        attempts.Should().HaveCount(4, "after the 4th attempt there is none left");
        attempts.Select(a => Math.Round((kickoff - a).TotalHours, 1)).Should().Equal([24.0, 12.1, 6.2, 3.2], "about 24, 12, 6 and 3 hours before kickoff — never all in the first hours");
        AutoSchedule.NextAttempt(Utc(10, 12, 16, 43), deadline, 1, 4).Should().BeNull("no attempt fits before the deadline");
        AutoSchedule.NextAttempt(Utc(10, 12, 16, 30), deadline, 1, 4).Should().Be(Utc(10, 12, 16, 37, 30), "a late discovery: still spread, never in the same second");
        AutoSchedule.NextAttempt(Utc(10, 12, 16, 41), deadline, 1, 4).Should().Be(Utc(10, 12, 16, 44), "never sooner than the minimum gap");
    }

    [Fact]
    public void The_read_only_check_says_for_each_match_whether_it_waits_is_due_now_or_is_too_late()
    {
        var kickoff = Utc(10, 12, 17);
        AutoFootballVerification.PublishPlan(Utc(10, 10, 17), kickoff, Timing).Should()
            .Be("publish target 11.10.2026 20:00 TR (24 h before kickoff), lock 12.10.2026 19:58 TR: WAITS (in 24 h 00 min)");
        AutoFootballVerification.PublishPlan(Utc(10, 12, 6, 30), kickoff, Timing).Should()
            .StartWith("publish target 11.10.2026 20:00 TR (24 h before kickoff), lock 12.10.2026 19:58 TR: DUE NOW — publish time passed 13 h 30 min ago");
        AutoFootballVerification.PublishPlan(Utc(10, 12, 16, 50), kickoff, Timing).Should().EndWith("TOO LATE — less than the minimum lead before kickoff (or started): no new card");
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
    public void A_set_of_a_bookmaker_whose_rule_is_not_verified_is_never_chosen_but_is_reported()
    {
        var match = Match(Book("pinnacle", 1.8m, 3.3m, 4.0m), Book("onexbet", 1.85m, 3.4m, 4.2m));
        var none = OddsSelector.Choose(match, Priority, [], Now, TimeSpan.FromMinutes(30));
        (none.Odds, none.Reason, none.Unapproved!.BookmakerKey).Should().Be(((SelectedOdds?)null, AutoBlockReason.MarketRuleUnverified, "pinnacle"),
            "valid data rejected by OUR rule — not \"no odds\"");
        var onlyMarathon = OddsSelector.Choose(match, Priority, ["marathonbet"], Now, TimeSpan.FromMinutes(30));
        (onlyMarathon.Odds, onlyMarathon.Reason).Should().Be(((SelectedOdds?)null, AutoBlockReason.BookmakerNotApproved));
        OddsSelector.Choose(match, Priority, ["onexbet"], Now, TimeSpan.FromMinutes(30)).Odds!.BookmakerKey.Should().Be("onexbet", "the first APPROVED bookmaker");
        AutoBlockCodes.Code(AutoBlockReason.MarketRuleUnverified).Should().Be("MARKET_RULE_UNVERIFIED");
        AutoBlockCodes.Code(AutoBlockReason.BookmakerNotApproved).Should().Be("BOOKMAKER_NOT_APPROVED");
    }

    // ---- the production approval: Pinnacle only ----

    private static OddsChoice Production(ProviderOddsEvent match, DateTimeOffset? now = null) =>
        OddsSelector.Choose(match, AutoFootballOptions.DefaultBookmakerPriority, FootballMarketRules.Production.ApprovedBookmakers, now ?? Now, TimeSpan.FromMinutes(30));

    [Fact]
    public void Production_approves_pinnacle_only()
    {
        FootballMarketRules.Production.ApprovedBookmakers.Should().Equal(["pinnacle"]);
        FootballMarketRules.Production.Usable(AutoFootballOptions.DefaultBookmakerPriority).Should().Equal(["pinnacle"]);
        FootballMarketRules.None.Usable(AutoFootballOptions.DefaultBookmakerPriority).Should().BeEmpty();
    }

    [Fact]
    public void A_complete_fresh_pinnacle_h2h_set_passes_the_production_rule()
    {
        var choice = Production(Match(Book("onexbet", 1.9m, 3.5m, 4.0m), Book("pinnacle", 1.8m, 3.3m, 4.0m)));
        (choice.Odds!.BookmakerKey, choice.Odds.HomeX100, choice.Odds.DrawX100, choice.Odds.AwayX100, choice.Reason)
            .Should().Be(("pinnacle", 180, 330, 400, AutoBlockReason.None));
    }

    [Fact]
    public void The_same_set_from_onexbet_alone_is_refused_and_only_reported()
    {
        var choice = Production(Match(Book("onexbet", 1.8m, 3.3m, 4.0m)));
        (choice.Odds, choice.Reason, choice.Unapproved!.BookmakerKey).Should().Be(((SelectedOdds?)null, AutoBlockReason.BookmakerNotApproved, "onexbet"));
    }

    [Theory]
    [InlineData("h2h_lay")]
    [InlineData("draw_no_bet")]
    [InlineData("h2h_h1")]
    [InlineData("outrights")]
    public void A_pinnacle_set_of_another_market_is_refused_and_never_replaced_by_an_unapproved_bookmaker(string market)
    {
        var choice = Production(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, market: market), Book("onexbet", 1.85m, 3.4m, 4.2m)));
        (choice.Odds, choice.Reason).Should().Be(((SelectedOdds?)null, AutoBlockReason.BookmakerNotApproved), "no fallback to a bookmaker whose rule is not verified");
    }

    [Fact]
    public void A_pinnacle_draw_no_bet_shaped_h2h_with_two_outcomes_is_incomplete()
    {
        var two = new ProviderBookmaker("pinnacle", "Pinnacle", [new ProviderMarket("h2h", Now - TimeSpan.FromMinutes(5),
            [new ProviderPrice("Galatasaray", 1.5m), new ProviderPrice("Fenerbahce", 2.5m)])]);
        Production(Match(two)).Should().Be(new OddsChoice(null, AutoBlockReason.IncompleteMarket, null));
    }

    [Fact]
    public void A_stale_incomplete_or_invalid_pinnacle_set_is_refused()
    {
        Production(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, Now - TimeSpan.FromMinutes(31)))).Reason.Should().Be(AutoBlockReason.StaleOdds);
        Production(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, drawName: "Galatasaray"))).Reason.Should().Be(AutoBlockReason.IncompleteMarket);
        Production(Match(Book("pinnacle", 1.0m, 3.3m, 4.0m))).Reason.Should().Be(AutoBlockReason.InvalidOdds);
        Production(Match(Book("pinnacle", 1.8m, 3.3m, 4.0m, homeName: "Besiktas"))).Reason.Should().Be(AutoBlockReason.IncompleteMarket, "another match's outcomes");
    }

    [Fact]
    public void Club_and_national_competitions_are_allow_listed_match_keys_never_outrights()
    {
        AutoFootballOptions.NationalCompetitions.Should().Equal("soccer_uefa_nations_league", "soccer_uefa_euro_qualification", "soccer_uefa_european_championship",
            "soccer_fifa_world_cup_qualifiers_europe", "soccer_fifa_world_cup");
        AutoFootballOptions.NationalCompetitions.Should().OnlyContain(k => AutoFootballOptions.ScopeOf(k) == TeamScope.National);
        AutoFootballOptions.ClubCompetitions.Should().OnlyContain(k => AutoFootballOptions.ScopeOf(k) == TeamScope.Club);
        AutoFootballOptions.KnownCompetitions.Should().NotContain(k => k.EndsWith("_winner", StringComparison.Ordinal) || k.Contains("friendl", StringComparison.Ordinal));
        AutoFootballOptions.ScopeOf("soccer_fifa_world_cup_winner").Should().BeNull();
        new AutoFootballOptions { CompetitionKeys = ["soccer_fifa_world_cup_winner"] }.Problems().Should().NotBeEmpty();
        new AutoFootballOptions { CompetitionKeys = ["soccer_uefa_nations_league"] }.Problems().Should().BeEmpty();
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
        o.Competitions.Should().Equal(AutoFootballOptions.KnownCompetitions);
        o.Competitions.Should().HaveCount(10);
        o.Bookmakers.Should().NotContain(b => AutoFootballOptions.IsExchange(b));
        (o.PublishBeforeKickoffHours, o.Timing()!.PublishBefore, o.Timing()!.LockBefore, o.Timing()!.MinLead)
            .Should().Be((24, TimeSpan.FromHours(24), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15)));
        o.DiscoveryHorizon.Should().Be(TimeSpan.FromHours(48), "matches are known a day before their publish time");
        new AutoFootballOptions { PublishBeforeKickoffHours = 48 }.DiscoveryHorizon.Should().Be(TimeSpan.FromHours(72), "the horizon follows a longer lead");
        typeof(AutoFootballOptions).GetProperties().Select(p => p.Name).Should().NotContain(AutoFootballOptions.RetiredSettings, "the 09:00 schedule is gone, not a second system");
        typeof(AutoFootballOptions).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Key", StringComparison.Ordinal) && n != nameof(AutoFootballOptions.CompetitionKeys),
            "the API key is a secret and never part of the options");
    }

    [Theory]
    [InlineData("Mode", "Sometimes")]
    [InlineData("Mode", "3")]
    [InlineData("Provider", "OddsApiIo")]
    [InlineData("BaseUrl", "https://api.odds-api.io/")]
    [InlineData("BaseUrl", "http://api.the-odds-api.com/")]
    [InlineData("PublishBeforeKickoffHours", "0")]
    [InlineData("PublishBeforeKickoffHours", "169")]
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
