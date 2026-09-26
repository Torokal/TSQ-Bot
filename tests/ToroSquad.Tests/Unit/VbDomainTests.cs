using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Team identity (only Türkiye women's senior national team) and the match state machine: transitions happen once, history
/// is never replayed, regressions/stale/malformed data change nothing.
/// </summary>
public sealed class VbDomainTests
{
    private static readonly TrackedTeamIdentity Sultanlar = TrackedTeamIdentity.TurkeyWomenSenior();
    private static readonly DateTimeOffset Start = new(2026, 7, 26, 11, 30, 0, TimeSpan.Zero);

    private static VolleyballMatch Match(VolleyballTeam home, VolleyballTeam away) => VbFakeProvider.Scheduled("m1", Start, home, away);

    // ------------------------------------------------------------------ identity / filtering

    [Fact]
    public void Turkey_women_senior_is_accepted_on_either_side()
    {
        Sultanlar.Evaluate(Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent())).Should().Be(new MatchFilterResult(MatchFilterReason.Accepted, FollowedSide.Home));
        Sultanlar.Evaluate(Match(VbFakeProvider.Opponent(), VbFakeProvider.Turkey())).Should().Be(new MatchFilterResult(MatchFilterReason.Accepted, FollowedSide.Away));
    }

    [Fact]
    public void Turkey_men_is_rejected() =>
        Sultanlar.Evaluate(Match(VbFakeProvider.Turkey(gender: TeamGender.Men), VbFakeProvider.Opponent(gender: TeamGender.Men))).Reason.Should().Be(MatchFilterReason.NotInvolved);

    [Theory]
    [InlineData(17)]
    [InlineData(19)]
    [InlineData(21)]
    public void Turkey_age_group_women_are_rejected(int age)
    {
        var youth = VbFakeProvider.Turkey(level: TeamLevel.AgeGroup, name: $"Türkiye U{age}") with { AgeLimit = age };
        Sultanlar.Classify(youth).Should().Be(TeamVerdict.NoMatch);
        Sultanlar.Evaluate(Match(youth, VbFakeProvider.Opponent())).Accepted.Should().BeFalse();
    }

    [Fact]
    public void A_turkish_club_is_rejected_even_with_a_TUR_like_code() =>
        Sultanlar.Classify(new VolleyballTeam("x", "99", "Fenerbahçe Medicana", "TUR", TeamGender.Women, TeamLevel.Senior, TeamKind.Club)).Should().Be(TeamVerdict.NoMatch);

    [Fact]
    public void Italy_vs_Brazil_women_is_rejected() =>
        Sultanlar.Evaluate(Match(VbFakeProvider.Opponent(), VbFakeProvider.Opponent("BRA", "Brazil"))).Reason.Should().Be(MatchFilterReason.NotInvolved);

    [Fact]
    public void Name_alone_never_identifies_the_team()
    {
        // "Turkey W" with no structured country/gender/level: not followed (no string matching).
        var nameOnly = new VolleyballTeam("x", null, "Turkey W", null, TeamGender.Unknown, TeamLevel.Unknown, TeamKind.Unknown);
        Sultanlar.Classify(nameOnly).Should().Be(TeamVerdict.NoMatch);
    }

    [Theory]
    [InlineData(TeamGender.Unknown, TeamLevel.Senior, TeamKind.NationalTeam)]
    [InlineData(TeamGender.Women, TeamLevel.Unknown, TeamKind.NationalTeam)]
    [InlineData(TeamGender.Women, TeamLevel.Senior, TeamKind.Unknown)]
    public void Incomplete_turkish_identity_is_ambiguous_and_the_match_is_rejected(TeamGender gender, TeamLevel level, TeamKind kind)
    {
        var team = new VolleyballTeam("x", "1", "Türkiye", "TUR", gender, level, kind);
        Sultanlar.Classify(team).Should().Be(TeamVerdict.Ambiguous);
        Sultanlar.Evaluate(Match(team, VbFakeProvider.Opponent())).Reason.Should().Be(MatchFilterReason.AmbiguousIdentity);
    }

    [Fact]
    public void A_configured_provider_id_is_used_but_never_trusted_against_contradicting_data()
    {
        var withId = TrackedTeamIdentity.TurkeyWomenSenior(["t1"]);
        var incomplete = new VolleyballTeam("x", "t1", "Türkiye", "TUR", TeamGender.Unknown, TeamLevel.Unknown, TeamKind.Unknown);
        withId.Classify(incomplete).Should().Be(TeamVerdict.Match, "a stable provider id completes a partially known identity");
        var men = incomplete with { Gender = TeamGender.Men };
        withId.Classify(men).Should().Be(TeamVerdict.Ambiguous);
        withId.Evaluate(Match(men, VbFakeProvider.Opponent())).Reason.Should().Be(MatchFilterReason.ProviderIdConflict);
    }

    [Fact]
    public void Turkey_against_another_turkish_side_is_rejected()
    {
        Sultanlar.Evaluate(Match(VbFakeProvider.Turkey(), VbFakeProvider.Turkey(providerId: "t2"))).Reason.Should().Be(MatchFilterReason.BothSidesMatch);
        var unclear = new VolleyballTeam("x", "9", "Türkiye B", "TUR", TeamGender.Women, TeamLevel.Unknown, TeamKind.NationalTeam);
        Sultanlar.Evaluate(Match(VbFakeProvider.Turkey(), unclear)).Accepted.Should().BeFalse();
    }

    // ------------------------------------------------------------------ state machine

    private static MatchProgressState Baseline(VolleyballMatch m) => MatchProgress.Advance(null, m, continuous: false).State;

    [Fact]
    public void First_observation_is_a_baseline_without_transitions_even_mid_match()
    {
        var live = VbFakeProvider.WithSets(Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent()), VolleyballMatchStatus.Live, (25, 20), (20, 25), (25, 18));
        var result = MatchProgress.Advance(null, live, continuous: true);
        result.Transitions.Should().BeEmpty("a restart/first boot at 2-1 must not replay started + set 1/2/3");
        result.State.Started.Should().BeTrue();
        result.State.Sets.Should().HaveCount(3);
    }

    [Fact]
    public void Scheduled_to_live_is_started_once_and_repeating_the_same_response_changes_nothing()
    {
        var scheduled = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var state = Baseline(scheduled);
        var live = scheduled with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 };
        var first = MatchProgress.Advance(state, live, continuous: true);
        first.Transitions.Should().Equal(new ProgressTransition(ProgressSignal.Started, null, true));
        var again = MatchProgress.Advance(first.State, live, continuous: true);
        again.Transitions.Should().BeEmpty();
        again.Changed.Should().BeFalse();
    }

    [Fact]
    public void Each_completed_set_transitions_once()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var state = MatchProgress.Advance(Baseline(m), m with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 }, true).State;
        var set1 = VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21));
        var r1 = MatchProgress.Advance(state, set1, true);
        r1.Transitions.Should().Equal(new ProgressTransition(ProgressSignal.SetCompleted, 1, true));
        MatchProgress.Advance(r1.State, set1, true).Transitions.Should().BeEmpty("the same set count again is not a new set");
    }

    [Fact]
    public void Set_score_regression_2_1_to_1_1_to_2_1_produces_no_duplicate()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var state = MatchProgress.Advance(Baseline(m), VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (21, 25)), true).State;
        var twoOne = VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (21, 25), (25, 18));
        var r1 = MatchProgress.Advance(state, twoOne, true);
        r1.Transitions.Should().ContainSingle(t => t.Signal == ProgressSignal.SetCompleted && t.SetNumber == 3);

        var back = VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (21, 25));
        var r2 = MatchProgress.Advance(r1.State, back, true);
        r2.Problem.Should().Be(ObservationProblem.Regression);
        r2.State.Should().Be(r1.State, "a regression never lowers the recorded state");

        MatchProgress.Advance(r2.State, twoOne, true).Transitions.Should().BeEmpty("set 3 was already recorded");
    }

    [Fact]
    public void A_set_changing_winner_is_a_conflict_and_ignored()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var state = MatchProgress.Advance(Baseline(m), VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21)), true).State;
        var flipped = VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (21, 25), (25, 20));
        var r = MatchProgress.Advance(state, flipped, true);
        r.Problem.Should().Be(ObservationProblem.Regression);
        r.Transitions.Should().BeEmpty();
    }

    [Fact]
    public void Finished_is_final_once_and_supersedes_the_last_set_card()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var state = MatchProgress.Advance(Baseline(m), VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (21, 25), (25, 18)), true).State;
        var final = VbFakeProvider.WithSets(m, VolleyballMatchStatus.Finished, (25, 21), (21, 25), (25, 18), (25, 23));
        var r = MatchProgress.Advance(state, final, true);
        r.Transitions.Should().Equal(new ProgressTransition(ProgressSignal.SetCompleted, 4, false), new ProgressTransition(ProgressSignal.Finished, null, true));
        MatchProgress.Advance(r.State, final, true).Transitions.Should().BeEmpty("finished repeated = final once");
    }

    [Fact]
    public void A_finished_match_is_never_reopened()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var done = Baseline(VbFakeProvider.WithSets(m, VolleyballMatchStatus.Finished, (25, 21), (25, 21), (25, 21)));
        var r = MatchProgress.Advance(done, VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (25, 21)), true);
        r.Problem.Should().Be(ObservationProblem.ReopenAfterEnd);
        r.State.Finished.Should().BeTrue();
    }

    [Fact]
    public void Without_continuity_started_and_set_transitions_are_recorded_but_not_announced()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var r = MatchProgress.Advance(Baseline(m), VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 20), (20, 25), (25, 18)), continuous: false);
        r.Transitions.Select(t => t.Signal).Should().Equal(ProgressSignal.Started, ProgressSignal.SetCompleted, ProgressSignal.SetCompleted, ProgressSignal.SetCompleted);
        r.Transitions.Should().OnlyContain(t => !t.Announce, "no catch-up spam after an outage");
        var final = MatchProgress.Advance(r.State, VbFakeProvider.WithSets(m, VolleyballMatchStatus.Finished, (25, 20), (20, 25), (25, 18), (25, 22)), continuous: false);
        final.Transitions.Should().ContainSingle(t => t.Signal == ProgressSignal.Finished && t.Announce, "the final stays announceable (bounded by the planner)");
    }

    [Fact]
    public void Several_sets_in_one_observation_announce_only_the_latest()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var state = MatchProgress.Advance(Baseline(m), m with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 }, true).State;
        var r = MatchProgress.Advance(state, VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 20), (20, 25)), true);
        r.Transitions.Should().Equal(new ProgressTransition(ProgressSignal.SetCompleted, 1, false), new ProgressTransition(ProgressSignal.SetCompleted, 2, true));
    }

    [Fact]
    public void Stale_data_creates_no_transition()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var r = MatchProgress.Advance(Baseline(m), VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 20)) with { IsStale = true }, true);
        r.Problem.Should().Be(ObservationProblem.Stale);
        r.Transitions.Should().BeEmpty();
        r.State.Started.Should().BeFalse();
    }

    [Theory]
    [InlineData(25, 24)] // not decided (lead < 2)
    [InlineData(14, 12)] // too few points
    [InlineData(-1, 25)]
    public void Malformed_set_scores_are_rejected(int home, int away)
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var bad = m with { Status = VolleyballMatchStatus.Live, HomeSets = home > away ? 1 : 0, AwaySets = home > away ? 0 : 1, Sets = [new VolleyballSet(1, home, away, true)] };
        MatchProgress.Validate(bad).Should().NotBeNull();
        MatchProgress.Advance(Baseline(m), bad, true).Problem.Should().Be(ObservationProblem.Malformed);
    }

    [Fact]
    public void Inconsistent_counts_and_missing_sets_are_malformed()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        MatchProgress.Validate(m with { Status = VolleyballMatchStatus.Live, HomeSets = 2, AwaySets = 0, Sets = [new VolleyballSet(1, 25, 20, true)] })
            .Should().Contain("do not cover");
        MatchProgress.Validate(m with { Status = VolleyballMatchStatus.Live, HomeSets = 1, AwaySets = 0, Sets = [new VolleyballSet(1, 20, 25, true)] })
            .Should().Contain("winners");
        MatchProgress.Validate(m with { Status = VolleyballMatchStatus.Finished, HomeSets = 2, AwaySets = 0, Sets = [] }).Should().Contain("finished");
        MatchProgress.Validate(m with { Status = VolleyballMatchStatus.Live, HomeSets = null, AwaySets = null }).Should().Contain("missing");
        MatchProgress.Validate(m with { Status = VolleyballMatchStatus.Live, HomeSets = 4, AwaySets = 2 }).Should().Contain("impossible");
    }

    [Fact]
    public void Counts_without_set_points_track_progress_but_never_create_set_transitions()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var state = MatchProgress.Advance(Baseline(m), m with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 }, true).State;
        var r = MatchProgress.Advance(state, m with { Status = VolleyballMatchStatus.Live, HomeSets = 1, AwaySets = 0 }, true);
        r.Transitions.Should().BeEmpty("a set card needs the set's points");
        r.State.HomeSets.Should().Be(1);
    }

    [Fact]
    public void Postponed_repeated_is_announced_once_and_a_started_match_cannot_be_postponed()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var postponed = m with { Status = VolleyballMatchStatus.Postponed };
        var r1 = MatchProgress.Advance(Baseline(m), postponed, true);
        r1.Transitions.Should().Equal(new ProgressTransition(ProgressSignal.Postponed, null, true));
        MatchProgress.Advance(r1.State, postponed, true).Transitions.Should().BeEmpty();
        var rescheduled = MatchProgress.Advance(r1.State, m, true);
        rescheduled.State.Status.Should().Be(VolleyballMatchStatus.Scheduled);
        MatchProgress.Advance(rescheduled.State, postponed, true).Transitions.Should().BeEmpty("postponed is a once-per-match card");

        var live = MatchProgress.Advance(Baseline(m), m with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 }, true).State;
        MatchProgress.Advance(live, postponed, true).Problem.Should().Be(ObservationProblem.Regression);
    }

    [Fact]
    public void Cancelled_is_announced_once_and_is_final()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var cancelled = m with { Status = VolleyballMatchStatus.Cancelled };
        var r = MatchProgress.Advance(Baseline(m), cancelled, true);
        r.Transitions.Should().Equal(new ProgressTransition(ProgressSignal.Cancelled, null, true));
        MatchProgress.Advance(r.State, cancelled, true).Transitions.Should().BeEmpty();
        MatchProgress.Advance(r.State, m with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 }, true).Problem.Should().Be(ObservationProblem.ReopenAfterEnd);
    }

    [Fact]
    public void Unknown_status_never_overrides_a_known_state()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var live = MatchProgress.Advance(Baseline(m), m with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 }, true).State;
        var r = MatchProgress.Advance(live, m with { Status = VolleyballMatchStatus.Unknown }, true);
        r.State.Should().Be(live);
        r.Transitions.Should().BeEmpty();
    }

    [Fact]
    public void Final_score_corrections_update_the_state_without_a_new_transition()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var done = Baseline(VbFakeProvider.WithSets(m, VolleyballMatchStatus.Finished, (25, 21), (25, 21), (25, 21)));
        var corrected = MatchProgress.Advance(done, VbFakeProvider.WithSets(m, VolleyballMatchStatus.Finished, (25, 21), (25, 21), (26, 24)), true);
        corrected.Transitions.Should().BeEmpty();
        corrected.State.Sets[2].HomePoints.Should().Be(26);
    }

    [Theory]
    [InlineData("TUR", "🇹🇷")]
    [InlineData("ITA", "🇮🇹")]
    [InlineData("SRB", "🇷🇸")]
    [InlineData("NED", "🇳🇱")]
    [InlineData("tst", null)]
    [InlineData(null, null)]
    public void Flags_come_from_explicit_federation_codes_only(string? code, string? flag) => CountryFlags.For(code).Should().Be(flag);

    [Theory]
    [InlineData(1, 25, 23, true)]
    [InlineData(1, 26, 24, true)]
    [InlineData(1, 31, 29, true)]
    [InlineData(1, 25, 24, false)] // no 2-point lead
    [InlineData(1, 27, 24, false)] // beyond 25 only by exactly 2
    [InlineData(1, 15, 13, false)] // sets 1-4 go to 25
    [InlineData(4, 21, 19, false)]
    [InlineData(5, 15, 13, true)]  // the 5th set goes to 15
    [InlineData(5, 17, 15, true)]
    [InlineData(5, 14, 12, false)]
    public void Set_scores_follow_the_fivb_best_of_five_rules(int set, int winner, int loser, bool finished) =>
        MatchProgress.IsFinishedSet(set, winner, loser).Should().Be(finished);

    [Fact]
    public void Impossible_best_of_five_results_are_rejected()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        MatchProgress.Validate(VbFakeProvider.WithSets(m, VolleyballMatchStatus.Finished, (25, 20), (25, 20), (20, 25))).Should().Contain("finished at 2-1");
        MatchProgress.Validate(VbFakeProvider.WithSets(m, VolleyballMatchStatus.Finished, (25, 20), (25, 20), (20, 25), (20, 25))).Should().Contain("finished at 2-2");
        MatchProgress.Validate(VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 20), (25, 20), (25, 20), (25, 20))).Should().Contain("impossible");
        MatchProgress.Validate(VbFakeProvider.WithSets(m, VolleyballMatchStatus.Finished, (25, 20), (20, 25), (25, 20), (20, 25), (15, 11))).Should().BeNull();
    }

    [Fact]
    public void The_deciding_set_is_never_announced_as_a_set_card()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var state = MatchProgress.Advance(Baseline(m), VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (25, 21)), true).State;
        var r = MatchProgress.Advance(state, VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (25, 21), (25, 20)), true);
        r.Transitions.Should().Equal(new ProgressTransition(ProgressSignal.SetCompleted, 3, false));
    }

    [Fact]
    public void Corrections_only_apply_to_a_running_match_and_never_go_back_to_not_started()
    {
        var m = Match(VbFakeProvider.Turkey(), VbFakeProvider.Opponent());
        var live = MatchProgress.Advance(Baseline(m), VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (25, 22)), true).State;
        MatchProgress.Correct(live, VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21), (22, 25)))!.AwaySets.Should().Be(1);
        MatchProgress.Correct(live, m).Should().BeNull("'scheduled' is never a correction");
        MatchProgress.Correct(Baseline(m), VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 21))).Should().BeNull("not started yet");
        MatchProgress.Correct(live, VbFakeProvider.WithSets(m, VolleyballMatchStatus.Live, (25, 24))).Should().BeNull("invalid data is never a correction");
    }
}
