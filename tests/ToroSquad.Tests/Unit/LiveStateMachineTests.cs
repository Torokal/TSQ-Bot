using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>The pure creator-session state machine: transitions, dedupe, ordering, grace and baseline rules.</summary>
public sealed class LiveStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly LiveRules Rules = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3), AnnounceExistingLiveOnBootstrap: false);
    private static readonly LivePlatform[] Both = [LivePlatform.Twitch, LivePlatform.Kick];

    private sealed class World
    {
        public CreatorState Creator { get; } = new() { CreatorKey = "lordtoro" };
        public List<PlatformState> Platforms { get; } =
        [
            new() { CreatorKey = "lordtoro", Platform = LivePlatform.Twitch, Login = "lordtoro" },
            new() { CreatorKey = "lordtoro", Platform = LivePlatform.Kick, Login = "lordtoro" },
        ];

        public PlatformState Twitch => Platforms[0];
        public PlatformState Kick => Platforms[1];

        public IReadOnlyList<LiveEffect> See(LivePlatform platform, bool live, DateTimeOffset at, DateTimeOffset? started = null, string? title = null,
            string? eventId = null, bool allowed = true, LiveRules? rules = null, string? stream = null) =>
            LiveStateMachine.Apply(Creator, Platforms, new LiveObservation(platform, "lordtoro", ObservationKind.Status, live, at, stream, started, title, EventId: eventId),
                rules ?? Rules, allowed, at);

        public IReadOnlyList<LiveEffect> Meta(LivePlatform platform, DateTimeOffset at, string title, string? eventId = null) =>
            LiveStateMachine.Apply(Creator, Platforms, new LiveObservation(platform, "lordtoro", ObservationKind.Metadata, false, at, Title: title, EventId: eventId), Rules, true, at);

        public IReadOnlyList<LiveEffect> Tick(DateTimeOffset now) => LiveStateMachine.Tick(Creator, Platforms, Both, Rules, now);

        /// <summary>Both channels observed offline once (normal operation, not a bootstrap).</summary>
        public static World Watching()
        {
            var w = new World();
            w.See(LivePlatform.Twitch, false, T0);
            w.See(LivePlatform.Kick, false, T0);
            return w;
        }
    }

    private static DateTimeOffset At(int seconds) => T0 + TimeSpan.FromSeconds(seconds);

    [Fact]
    public void First_platform_opens_an_announced_session_and_the_second_joins_it()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20), "Başlık").Should().ContainSingle(e => e.Kind == LiveEffectKind.SessionStarted && e.Detail == "announce");
        w.See(LivePlatform.Kick, true, At(60), At(40), "Başlık").Should().ContainSingle(e => e.Kind == LiveEffectKind.PlatformJoined);

        w.Creator.Phase.Should().Be(CreatorPhase.Live);
        w.Creator.SessionNumber.Should().Be(1);
        w.Creator.Announced.Should().BeTrue();
        w.Creator.SessionPlatforms.Should().Be("twitch,kick");
        w.Creator.SessionStartedAt.Should().Be(At(20));
    }

    [Fact]
    public void A_creator_stays_live_until_every_platform_is_offline()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20));
        w.See(LivePlatform.Kick, true, At(30), At(25));
        w.See(LivePlatform.Twitch, false, At(60)).Should().ContainSingle(e => e.Kind == LiveEffectKind.PlatformWentOffline);
        w.Creator.Phase.Should().Be(CreatorPhase.Live);
        w.See(LivePlatform.Kick, false, At(90)).Select(e => e.Kind).Should().Equal(LiveEffectKind.PlatformWentOffline, LiveEffectKind.GraceEntered);
        w.Creator.Phase.Should().Be(CreatorPhase.ReconnectGrace);
        w.Creator.GraceSince.Should().Be(At(90));
    }

    [Fact]
    public void The_session_ends_only_after_the_grace_and_a_confirmed_offline_observation_of_every_tracked_platform()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20));
        w.See(LivePlatform.Twitch, false, At(60));
        w.Tick(At(170)).Should().BeEmpty("grace not over");
        w.Tick(At(200)).Should().BeEmpty("over, but offline not confirmed after the deadline (180 s)");
        w.See(LivePlatform.Twitch, false, At(200));
        w.Tick(At(200)).Should().BeEmpty("Kick not confirmed after the deadline either");
        w.See(LivePlatform.Kick, false, At(205));
        w.Tick(At(205)).Should().ContainSingle(e => e.Kind == LiveEffectKind.SessionEnded);
        w.Creator.Phase.Should().Be(CreatorPhase.Offline);
        w.Creator.SessionEndedAt.Should().Be(At(60), "the stream ended when the last platform went offline");
    }

    [Fact]
    public void An_untracked_or_never_observed_platform_does_not_block_the_end()
    {
        var w = new World();
        w.See(LivePlatform.Twitch, false, T0); // Kick never observed (e.g. no credentials)
        w.See(LivePlatform.Twitch, true, At(30), At(20));
        w.See(LivePlatform.Twitch, false, At(60));
        w.See(LivePlatform.Twitch, false, At(190));
        LiveStateMachine.Tick(w.Creator, w.Platforms, [LivePlatform.Twitch], Rules, At(190)).Should().ContainSingle(e => e.Kind == LiveEffectKind.SessionEnded);
    }

    [Fact]
    public void A_comeback_that_started_inside_the_grace_is_the_same_session()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20));
        w.See(LivePlatform.Twitch, false, At(60));
        w.See(LivePlatform.Twitch, true, At(120), At(100)).Should().ContainSingle(e => e.Kind == LiveEffectKind.ReconnectedWithinGrace);
        w.Creator.Phase.Should().Be(CreatorPhase.Live);
        w.Creator.SessionNumber.Should().Be(1);
    }

    [Fact]
    public void A_stream_that_started_after_the_grace_deadline_is_a_new_session_even_if_the_end_was_not_confirmed()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20));
        w.See(LivePlatform.Twitch, false, At(60));
        // No confirmation (provider outage) — then the streamer is back with a stream that started 10 minutes later.
        var effects = w.See(LivePlatform.Twitch, true, At(700), At(660));
        effects.Select(e => e.Kind).Should().Equal(LiveEffectKind.SessionEnded, LiveEffectKind.SessionStarted);
        w.Creator.SessionNumber.Should().Be(2);
        w.Creator.Announced.Should().BeTrue("after the gap the new stream is fresh (started 40 s ago)");
    }

    [Fact]
    public void A_restart_between_two_observations_is_detected_from_the_start_time()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20));
        // Next observation: still live, but the stream started after the previous observation — quick restart.
        w.See(LivePlatform.Twitch, true, At(60), At(45)).Should().ContainSingle(e => e.Kind == LiveEffectKind.ReconnectedWithinGrace);
        w.Creator.SessionNumber.Should().Be(1);

        // Long unseen gap (bot switched off for an hour, frozen "live"): a stream that started 5 minutes ago is news.
        var effects = w.See(LivePlatform.Twitch, true, At(3660), At(3360));
        effects.Select(e => e.Kind).Should().Equal(LiveEffectKind.SessionEnded, LiveEffectKind.SessionStarted);
        w.Creator.SessionNumber.Should().Be(2);
        w.Creator.Announced.Should().BeTrue();
    }

    [Fact]
    public void The_same_provider_stream_id_is_always_the_same_session()
    {
        // Seen live, then a long unseen gap with a later start time — but Twitch says it is the same stream.
        var gap = World.Watching();
        gap.See(LivePlatform.Twitch, true, At(30), At(20), stream: "tw-1");
        gap.See(LivePlatform.Twitch, true, At(3660), At(3360), stream: "tw-1").Should().NotContain(e => e.Kind == LiveEffectKind.SessionStarted);
        gap.Creator.SessionNumber.Should().Be(1);

        // Offline, grace expired and confirmed, session ended — then the same stream id is back: the session is reopened.
        var ended = World.Watching();
        ended.See(LivePlatform.Twitch, true, At(30), At(20), stream: "tw-1");
        ended.See(LivePlatform.Twitch, false, At(60));
        ended.See(LivePlatform.Twitch, false, At(200));
        ended.See(LivePlatform.Kick, false, At(200));
        ended.Tick(At(200)).Should().ContainSingle(e => e.Kind == LiveEffectKind.SessionEnded);
        ended.See(LivePlatform.Twitch, true, At(400), At(20), stream: "tw-1").Should().ContainSingle(e => e.Kind == LiveEffectKind.ReconnectedWithinGrace && e.Detail == "same_stream");
        ended.Creator.Phase.Should().Be(CreatorPhase.Live);
        ended.Creator.SessionNumber.Should().Be(1, "never a second announcement for the same stream");
        ended.Creator.SessionEndedAt.Should().BeNull();

        // A different stream id after the grace is a new session.
        ended.See(LivePlatform.Twitch, false, At(430));
        ended.See(LivePlatform.Twitch, false, At(600));
        ended.See(LivePlatform.Kick, false, At(600));
        ended.Tick(At(600));
        ended.See(LivePlatform.Twitch, true, At(900), At(880), stream: "tw-2").Should().ContainSingle(e => e.Kind == LiveEffectKind.SessionStarted);
        ended.Creator.SessionNumber.Should().Be(2);
    }

    [Fact]
    public void Metadata_never_starts_a_session_or_changes_live_status()
    {
        var w = World.Watching();
        for (var i = 1; i <= 20; i++)
            w.Meta(LivePlatform.Twitch, At(i * 10), "Başlık " + i, "m" + i).Should().NotContain(e => e.Kind == LiveEffectKind.SessionStarted);
        w.Creator.SessionNumber.Should().Be(0);
        w.Twitch.Status.Should().Be(PlatformStatus.Offline);

        w.See(LivePlatform.Twitch, true, At(300), At(290), stream: "tw-1");
        for (var i = 1; i <= 20; i++)
        {
            w.Meta(LivePlatform.Twitch, At(300 + (i * 10)), "Yeni " + i).Should().NotContain(e => e.Kind == LiveEffectKind.SessionStarted);
            w.See(LivePlatform.Twitch, true, At(305 + (i * 10)), At(290), "Yeni " + i, stream: "tw-1").Should().NotContain(e => e.Kind == LiveEffectKind.SessionStarted);
        }

        w.Creator.SessionNumber.Should().Be(1);
    }

    [Fact]
    public void The_first_observation_of_a_live_channel_is_a_baseline_unless_configured_otherwise()
    {
        var w = new World();
        w.See(LivePlatform.Twitch, true, T0, T0 - TimeSpan.FromHours(3)).Should().ContainSingle(e => e.Kind == LiveEffectKind.SessionStarted && e.Detail == "bootstrap");
        w.Creator.Announced.Should().BeFalse();

        var announce = new World();
        announce.See(LivePlatform.Twitch, true, T0, T0 - TimeSpan.FromHours(3), rules: Rules with { AnnounceExistingLiveOnBootstrap = true });
        announce.Creator.Announced.Should().BeTrue();
    }

    [Fact]
    public void After_a_gap_a_stream_that_started_after_the_last_offline_statement_is_announced_whatever_its_age()
    {
        // Known offline at T0; the bot was down; back 30 minutes later, the stream started 2 minutes after T0 (28 minutes old).
        var down = World.Watching();
        down.See(LivePlatform.Twitch, true, At(1800), At(120)).Should().ContainSingle(e => e.Kind == LiveEffectKind.SessionStarted && e.Detail == "announce");
        down.Creator.Announced.Should().BeTrue("it provably started while the bot was not looking — no age cutoff");

        var days = World.Watching();
        days.See(LivePlatform.Twitch, true, At(3 * 86400), At(2 * 86400)).Should().ContainSingle(e => e.Detail == "announce");

        var startedBefore = World.Watching();
        startedBefore.See(LivePlatform.Twitch, true, At(1800), At(-60)).Should().ContainSingle(e => e.Detail == "gap",
            "it started before the statement that saw the channel offline: not provably new");

        var unknownStart = World.Watching();
        unknownStart.See(LivePlatform.Twitch, true, At(1800)).Should().ContainSingle(e => e.Detail == "gap", "no provider start time after a gap: not provably new");
    }

    [Fact]
    public void Delivery_off_suppresses_the_announcement_of_the_session_for_good()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20), allowed: false).Should().ContainSingle(e => e.Detail == "delivery_off");
        w.See(LivePlatform.Kick, true, At(60), At(50), allowed: true);
        w.Creator.Announced.Should().BeFalse("joining the session later does not announce it");
    }

    [Fact]
    public void Duplicate_event_ids_and_older_statements_are_ignored()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20), eventId: "e1");
        w.See(LivePlatform.Twitch, true, At(30), At(20), eventId: "e1").Should().ContainSingle(e => e.Kind == LiveEffectKind.DuplicateIgnored);
        w.See(LivePlatform.Twitch, false, At(10)).Should().ContainSingle(e => e.Kind == LiveEffectKind.StaleIgnored, "an older offline must not end a newer live");
        w.Twitch.Status.Should().Be(PlatformStatus.Live);
        w.Creator.SessionNumber.Should().Be(1);
    }

    [Fact]
    public void Titles_are_normalized_and_the_newest_statement_wins()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20), "  VALHEIM   SERVERA\tGİRİYORUZ ");
        w.Twitch.Title.Should().Be("VALHEIM SERVERA GİRİYORUZ");
        w.Meta(LivePlatform.Twitch, At(40), "VALHEIM SERVERA GİRİYORUZ").Should().BeEmpty("same normalized title: no change, no edit");
        w.Meta(LivePlatform.Twitch, At(50), "CS2 FACEIT | !discord").Should().ContainSingle(e => e.Kind == LiveEffectKind.TitleChanged);
        w.Meta(LivePlatform.Twitch, At(45), "ESKİ").Should().ContainSingle(e => e.Kind == LiveEffectKind.StaleIgnored);
        w.See(LivePlatform.Twitch, true, At(48), At(20), "POLL BAŞLIĞI").Should().BeEmpty(
            "the status part is applied (newer than the last status), its title is older than the metadata event and is dropped");
        w.Twitch.Title.Should().Be("CS2 FACEIT | !discord");
        w.Twitch.TitleChangedAt.Should().Be(At(50));
    }

    [Fact]
    public void Title_normalization_handles_blank_control_and_long_text()
    {
        LiveText.NormalizeTitle("   ").Should().BeNull();
        LiveText.NormalizeTitle("a\u0000b\r\nc").Should().Be("ab c");
        LiveText.NormalizeTitle(new string('x', 500))!.Length.Should().Be(LiveText.TitleMax);
        LiveText.NormalizeTitle(new string('x', LiveText.TitleMax - 1) + "😀")!.Length.Should().Be(LiveText.TitleMax - 1, "a surrogate pair is never split");
    }

    [Fact]
    public void A_live_creator_without_any_live_platform_is_repaired_into_the_grace()
    {
        var w = World.Watching();
        w.See(LivePlatform.Twitch, true, At(30), At(20));
        w.Twitch.Status = PlatformStatus.Offline; // inconsistent persisted state
        w.Tick(At(40)).Should().ContainSingle(e => e.Kind == LiveEffectKind.GraceEntered);
        w.Creator.Phase.Should().Be(CreatorPhase.ReconnectGrace);
    }
}
