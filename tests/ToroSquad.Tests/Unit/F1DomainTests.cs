using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>Formula 1 domain: normalization, deterministic identities and hashes, result validation, mapping and the lifecycle state machine.</summary>
public sealed class F1DomainTests
{
    private static readonly DateTimeOffset T = new(2030, 6, 9, 7, 0, 0, TimeSpan.Zero);

    private static F1LifecycleEvent E(F1LifecycleSignal signal, int minutes, int? phase = null) => new("openf1", "1", signal, T.AddMinutes(minutes), phase);

    [Theory]
    [InlineData("Practice 1", F1SessionType.Practice1)]
    [InlineData("FP2", F1SessionType.Practice2)]
    [InlineData("Third Practice", F1SessionType.Practice3)]
    [InlineData("Sprint Shootout", F1SessionType.SprintQualifying)]
    [InlineData("Sprint Qualifying", F1SessionType.SprintQualifying)]
    [InlineData("SprintQualifying", F1SessionType.SprintQualifying)]
    [InlineData("Sprint", F1SessionType.Sprint)]
    [InlineData("Qualifying", F1SessionType.Qualifying)]
    [InlineData("Race", F1SessionType.Race)]
    [InlineData("Day 1", F1SessionType.Unknown)]
    [InlineData("", F1SessionType.Unknown)]
    [InlineData(null, F1SessionType.Unknown)]
    public void Provider_session_names_normalize_to_domain_types(string? name, F1SessionType expected) =>
        F1SessionTypes.FromProviderName(name).Should().Be(expected);

    [Fact]
    public void Keys_are_deterministic_provider_independent_and_round_trip()
    {
        var a = new F1Session(2030, 8, F1SessionType.Race, T);
        var b = new F1Session(2030, 8, F1SessionType.Race, T.AddHours(2)); // rescheduled: same real-world session
        a.Key.Should().Be("2030-08-race").And.Be(b.Key);
        a.MeetingKey.Should().Be("2030-08");
        F1Keys.TryParseSession(a.Key, out var season, out var round, out var type).Should().BeTrue();
        (season, round, type).Should().Be((2030, 8, F1SessionType.Race));
        foreach (var t in Enum.GetValues<F1SessionType>().Where(t => t != F1SessionType.Unknown))
            F1SessionTypes.TryParseSlug(F1SessionTypes.Slug(t), out var back).Should().BeTrue();
    }

    [Fact]
    public void Only_sprint_and_race_award_championship_points_and_qualifying_formats_are_segmented()
    {
        Enum.GetValues<F1SessionType>().Where(F1SessionTypes.AwardsChampionshipPoints).Should().BeEquivalentTo([F1SessionType.Sprint, F1SessionType.Race]);
        Enum.GetValues<F1SessionType>().Where(F1SessionTypes.IsSegmented).Should().BeEquivalentTo([F1SessionType.Qualifying, F1SessionType.SprintQualifying]);
    }

    // ---- lifecycle state machine

    [Fact]
    public void First_start_is_the_only_logical_start_and_a_restart_after_suspension_is_a_resume()
    {
        var t1 = F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.Scheduled, null, E(F1LifecycleSignal.Started, 3));
        t1.Kind.Should().Be(F1TransitionKind.FirstStart);
        var t2 = F1LifecycleMachine.Apply(F1SessionType.Race, t1.NewState, T.AddMinutes(3), E(F1LifecycleSignal.Suspended, 50));
        t2.Kind.Should().Be(F1TransitionKind.Suspend);
        var t3 = F1LifecycleMachine.Apply(F1SessionType.Race, t2.NewState, T.AddMinutes(50), E(F1LifecycleSignal.Started, 70));
        t3.Kind.Should().Be(F1TransitionKind.Resume, "SESSION STARTED after a red flag is a resume, never a second start");
        t3.NewState.Should().Be(F1SessionState.Started);
    }

    [Fact]
    public void Duplicates_and_out_of_order_replays_are_ignored()
    {
        var last = T.AddMinutes(50);
        F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.Suspended, last, E(F1LifecycleSignal.Suspended, 50)).Kind.Should().Be(F1TransitionKind.Ignored);
        // The first start replayed after the suspension (older provider time) must not "restart" anything.
        F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.Suspended, last, E(F1LifecycleSignal.Started, 3)).Kind.Should().Be(F1TransitionKind.Ignored);
        F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.Started, T.AddMinutes(3), E(F1LifecycleSignal.Started, 4)).Kind.Should().Be(F1TransitionKind.Ignored);
    }

    [Fact]
    public void Finished_means_results_pending_never_finalised_and_is_terminal_for_lifecycle()
    {
        var t = F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.Started, T, E(F1LifecycleSignal.Finished, 120));
        t.Kind.Should().Be(F1TransitionKind.Finish);
        t.NewState.Should().Be(F1SessionState.FinishedPendingResults);
        F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.FinishedPendingResults, T.AddMinutes(120), E(F1LifecycleSignal.Started, 130)).Kind.Should().Be(F1TransitionKind.Ignored);
        F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.Finalised, T.AddMinutes(120), E(F1LifecycleSignal.Suspended, 130)).Kind.Should().Be(F1TransitionKind.Ignored);
    }

    [Fact]
    public void Qualifying_segment_ends_do_not_finish_the_session_and_an_unknown_segment_fails_closed()
    {
        var q1End = F1LifecycleMachine.Apply(F1SessionType.Qualifying, F1SessionState.Started, T, E(F1LifecycleSignal.Finished, 18, phase: 1));
        q1End.Kind.Should().Be(F1TransitionKind.SegmentEnd);
        q1End.NewState.Should().Be(F1SessionState.Started);
        F1LifecycleMachine.Apply(F1SessionType.Qualifying, F1SessionState.Started, T.AddMinutes(18), E(F1LifecycleSignal.Started, 25, phase: 2)).Kind.Should().Be(F1TransitionKind.Ignored);
        F1LifecycleMachine.Apply(F1SessionType.SprintQualifying, F1SessionState.Started, T, E(F1LifecycleSignal.Finished, 40, phase: null)).Kind.Should().Be(F1TransitionKind.Ignored);
        F1LifecycleMachine.Apply(F1SessionType.Qualifying, F1SessionState.Started, T, E(F1LifecycleSignal.Finished, 60, phase: 3)).NewState.Should().Be(F1SessionState.FinishedPendingResults);
    }

    [Fact]
    public void A_suspension_or_finish_without_an_observed_start_never_creates_a_start()
    {
        F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.Scheduled, null, E(F1LifecycleSignal.Suspended, 5)).Kind.Should().Be(F1TransitionKind.Ignored);
        var finish = F1LifecycleMachine.Apply(F1SessionType.Race, F1SessionState.Scheduled, null, E(F1LifecycleSignal.Finished, 120));
        finish.Kind.Should().Be(F1TransitionKind.Finish);
        finish.Kind.Should().NotBe(F1TransitionKind.FirstStart);
    }

    // ---- results

    private static F1SessionResult Race(params F1DriverResult[] rows) => new("2030-08-race", F1SessionType.Race, "openf1", rows);

    private static F1DriverResult D(int? pos, int number, F1ResultStatus status = F1ResultStatus.Classified, double? time = null) =>
        new(pos, number, "Driver " + number, "D" + number, "Team", status, 50, time, null, null, null);

    [Fact]
    public void Result_hash_is_deterministic_order_independent_and_ignores_float_noise()
    {
        var a = Race(D(1, 1, time: 0.1 + 0.2), D(2, 2), D(null, 3, F1ResultStatus.Dnf));
        var b = Race(D(null, 3, F1ResultStatus.Dnf), D(2, 2), D(1, 1, time: 0.3));
        a.CanonicalHash().Should().Be(b.CanonicalHash());
        a.CanonicalHash().Should().HaveLength(64);
    }

    [Fact]
    public void Result_hash_changes_on_any_visible_correction()
    {
        var original = Race(D(1, 1), D(2, 2), D(3, 3));
        var penalty = Race(D(2, 1), D(1, 2), D(3, 3));
        var dsq = Race(D(null, 1, F1ResultStatus.Dsq), D(1, 2), D(2, 3));
        new[] { original.CanonicalHash(), penalty.CanonicalHash(), dsq.CanonicalHash() }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Validator_rejects_empty_partial_and_inconsistent_classifications()
    {
        F1ResultValidator.Problem(Race(), 1).Should().Contain("empty");
        F1ResultValidator.Problem(Race(D(1, 1), D(2, 2)), 10).Should().Contain("only 2");
        F1ResultValidator.Problem(Race(D(1, 1), D(3, 2)), 1).Should().Contain("contiguous");
        F1ResultValidator.Problem(Race(D(1, 1), D(1, 1)), 1).Should().Contain("duplicate");
        F1ResultValidator.Problem(Race(D(1, 1), D(2, 2, F1ResultStatus.Dsq)), 1).Should().Contain("DNS/DSQ");
        F1ResultValidator.Problem(Race(D(null, 1, F1ResultStatus.Dnf)), 1).Should().Contain("no classified");
        F1ResultValidator.Problem(Race(D(1, 1) with { DriverName = " " }), 1).Should().Contain("name");
        F1ResultValidator.Problem(new F1SessionResult("2030-08-fp1", F1SessionType.Practice1, "openf1", [D(1, 1)]), 1).Should().Contain("lap time");
        F1ResultValidator.Problem(Race(D(1, 1), D(2, 2), D(null, 3, F1ResultStatus.Dnf), D(null, 4, F1ResultStatus.Dns)), 1).Should().BeNull();
    }

    // ---- standings

    [Fact]
    public void Standings_hash_is_canonical_and_only_changes_with_the_table()
    {
        F1StandingsSnapshot Table(params F1DriverStanding[] rows) => new(F1StandingsKind.Drivers, 2030, 7, "jolpica", rows, []);
        var a = Table(new(1, "fast", "Alex Fast", "FST", "Rapid", 120m, 3), new(2, "quick", "Bo Quick", "QCK", "Rapid", 98.5m, 1));
        var reordered = Table(new(2, "quick", "Bo Quick", "QCK", "Rapid", 98.50m, 1), new(1, "fast", "Alex Fast", "FST", "Rapid", 120.0m, 3));
        reordered.CanonicalHash().Should().Be(a.CanonicalHash(), "row order and decimal scale are not content");
        Table(new(1, "fast", "Alex Fast", "FST", "Rapid", 145m, 4), new(2, "quick", "Bo Quick", "QCK", "Rapid", 98.5m, 1))
            .CanonicalHash().Should().NotBe(a.CanonicalHash());
        (a with { Round = 8 }).CanonicalHash().Should().NotBe(a.CanonicalHash());
        new F1StandingsSnapshot(F1StandingsKind.Constructors, 2030, 7, "jolpica", [], []).CanonicalHash()
            .Should().NotBe(new F1StandingsSnapshot(F1StandingsKind.Drivers, 2030, 7, "jolpica", [], []).CanonicalHash());
    }

    // ---- mapping

    [Fact]
    public void Session_matching_requires_one_unambiguous_candidate()
    {
        var schedule = new[]
        {
            new F1Session(2030, 8, F1SessionType.Race, T),
            new F1Session(2030, 8, F1SessionType.Sprint, T.AddDays(-1)),
            new F1Session(2030, 9, F1SessionType.Race, T.AddDays(14)),
        };
        var tolerance = TimeSpan.FromHours(6);
        F1SessionMatcher.Match(schedule, 2030, F1SessionType.Race, T.AddMinutes(5), tolerance)!.Round.Should().Be(8);
        F1SessionMatcher.Match(schedule, 2030, F1SessionType.Race, T.AddHours(7), tolerance).Should().BeNull("outside the tolerance");
        F1SessionMatcher.Match(schedule, 2031, F1SessionType.Race, T, tolerance).Should().BeNull("different season");
        F1SessionMatcher.Match(schedule, 2030, F1SessionType.Qualifying, T, tolerance).Should().BeNull("type never guessed");
        F1SessionMatcher.Match(schedule, 2030, F1SessionType.Unknown, T, tolerance).Should().BeNull();
        var ambiguous = schedule.Append(new F1Session(2030, 99, F1SessionType.Race, T.AddHours(1))).ToList();
        F1SessionMatcher.Match(ambiguous, 2030, F1SessionType.Race, T.AddMinutes(30), tolerance).Should().BeNull("two candidates: fail closed");
    }
}
