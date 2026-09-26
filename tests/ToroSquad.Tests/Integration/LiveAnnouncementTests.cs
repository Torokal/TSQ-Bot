using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Notifications;
using ToroSquad.Modules.Live.Application;
using ToroSquad.Modules.Live.Domain;
using ToroSquad.Modules.Live.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Support.LiveBed;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Live end to end: fake Twitch/Kick (official-API shaped answers), the real coordinator, planner, outbox, dispatcher
/// and a fake Discord transport on a real SQLite database. Scenario letters follow the TSQ Live specification.
/// </summary>
public sealed class LiveAnnouncementTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    /// <summary>Bed with a first reconciliation already done (every channel observed offline = normal operation).</summary>
    private static async Task<LiveBed> WatchingAsync(Dictionary<string, string?>? overrides = null)
    {
        var bed = await CreateAsync(overrides);
        await bed.PollAsync();
        bed.Messages.Should().BeEmpty("nothing is live");
        return bed;
    }

    private static async Task StepsAsync(LiveBed bed, int count)
    {
        for (var i = 0; i < count; i++)
            await bed.StepAsync(Poll);
    }

    [Fact]
    public async Task A_twitch_start_sends_exactly_one_announcement_with_exactly_one_everyone()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "VALHEIM SERVERA GİRİYORUZ", "Valheim"));
        await bed.StepAsync(Poll);
        await StepsAsync(bed, 4);

        var message = bed.Messages.Should().ContainSingle().Subject;
        bed.EveryonePings.Should().Be(1);
        message.Message.Content.Should().Be("@everyone 🔴 **LORDTORO** yayında!");
        message.Message.Mentions.Everyone.Should().BeTrue();
        message.Message.Mentions.Roles.Should().BeEmpty();
        message.Message.Embed!.Title.Should().Be("VALHEIM SERVERA GİRİYORUZ");
        message.Message.Embed.Description.Should().Contain("🔴 **CANLI** · Twitch").And.Contain("🎮 Valheim").And.Contain("<t:");
        message.Message.Embed.Url.Should().Be("https://www.twitch.tv/lordtoro");
        message.Message.Buttons!.Select(b => (b.Label, b.Url)).Should().Equal(("Twitch'te İzle", "https://www.twitch.tv/lordtoro"));
        message.Edits.Should().BeEmpty("an unchanged state never causes an edit");
        (await bed.CreatorAsync(Toro)).Should().Match<CreatorState>(s => s.Phase == CreatorPhase.Live && s.Announced && s.AnnouncementMessageId == message.Id.Value);
    }

    [Fact]
    public async Task B_kick_start_sends_exactly_one_announcement_with_exactly_one_everyone()
    {
        await using var bed = await WatchingAsync();
        bed.Kick.GoLive(Nasil, Stream("k1", bed.Now, "Sohbet", "Just Chatting"));
        await bed.StepAsync(Poll);
        await StepsAsync(bed, 3);

        var message = bed.Messages.Should().ContainSingle().Subject;
        bed.EveryonePings.Should().Be(1);
        message.Message.Content.Should().Be("@everyone 🔴 **NASILYANI69** yayında!");
        message.Message.Buttons!.Select(b => (b.Label, b.Url)).Should().Equal(("Kick'te İzle", "https://kick.com/nasilyani69"));
        message.Message.Embed!.Description.Should().Contain("🔴 **CANLI** · Kick");
    }

    [Fact]
    public async Task C_multistream_twitch_then_kick_edits_the_same_message_and_never_pings_twice()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "VALHEIM SERVERA GİRİYORUZ"));
        await bed.StepAsync(Poll);
        bed.Kick.GoLive(Toro, Stream("k1", bed.Now + TimeSpan.FromSeconds(20), "VALHEIM SERVERA GİRİYORUZ"));
        await StepsAsync(bed, 3);

        var message = bed.Messages.Should().ContainSingle("one creator session = one message").Subject;
        bed.EveryonePings.Should().Be(1);
        var edit = message.Edits.Should().ContainSingle().Subject;
        edit.Mentions.PingsAnything.Should().BeFalse("edits never ping");
        edit.Buttons!.Select(b => b.Label).Should().Equal("Twitch'te İzle", "Kick'te İzle");
        edit.Embed!.Description.Should().Contain("Twitch + Kick");
        (await bed.CreatorAsync(Toro)).SessionNumber.Should().Be(1);
    }

    [Fact]
    public async Task D_reverse_multistream_kick_then_twitch_still_pings_once()
    {
        await using var bed = await WatchingAsync();
        bed.Kick.GoLive(Toro, Stream("k1", bed.Now, "Kick önce"));
        await bed.StepAsync(Poll);
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Kick önce"));
        await StepsAsync(bed, 3);

        var message = bed.Messages.Should().ContainSingle().Subject;
        bed.EveryonePings.Should().Be(1);
        message.Edits[^1].Buttons!.Select(b => b.Label).Should().Equal("Twitch'te İzle", "Kick'te İzle");
    }

    [Fact]
    public async Task E_twitch_title_change_edits_the_same_message_without_a_new_message_or_mention()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "VALHEIM SERVERA GİRİYORUZ"));
        await bed.StepAsync(Poll);

        // Reconciliation sees the new title (what channel.update would report) …
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now - Poll, "CS2 FACEIT | !discord", "Counter-Strike"));
        await StepsAsync(bed, 2);

        var message = bed.Messages.Should().ContainSingle().Subject;
        message.Edits.Should().ContainSingle();
        message.Edits[^1].Embed!.Title.Should().Be("CS2 FACEIT | !discord");
        message.Edits[^1].Embed!.Description.Should().Contain("🎮 Counter\\-Strike");
        message.Edits[^1].Mentions.PingsAnything.Should().BeFalse();
        bed.EveryonePings.Should().Be(1);

        // … and an event-shaped metadata update (channel.update) edits the same message again.
        await bed.ApplyAsync(new LiveObservation(LivePlatform.Twitch, Toro, ObservationKind.Metadata, false, bed.Now, Title: "RANKED | !discord", EventId: "evt-1"));
        message = bed.Messages.Should().ContainSingle().Subject;
        message.Edits.Should().HaveCount(2);
        message.Edits[^1].Embed!.Title.Should().Be("RANKED | !discord");
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task F_kick_title_change_edits_the_same_message_without_a_new_message_or_mention()
    {
        await using var bed = await WatchingAsync();
        bed.Kick.GoLive(Toro, Stream("k1", bed.Now, "VALHEIM SERVERA GİRİYORUZ"));
        await bed.StepAsync(Poll);
        bed.Kick.GoLive(Toro, Stream("k1", bed.Now - Poll, "CS2 FACEIT | !discord"));
        await bed.StepAsync(Poll);
        // livestream.metadata.updated equivalent
        await bed.ApplyAsync(new LiveObservation(LivePlatform.Kick, Toro, ObservationKind.Metadata, false, bed.Now, Title: "CS2 FACEIT | !discord", Category: "Counter-Strike 2"));

        var message = bed.Messages.Should().ContainSingle().Subject;
        message.Edits.Should().HaveCount(2, "title edit, then category edit (same title → no extra title edit)");
        message.Edits[0].Embed!.Title.Should().Be("CS2 FACEIT | !discord");
        message.Edits[^1].Embed!.Description.Should().Contain("🎮 Counter\\-Strike 2");
        message.Edits.Should().OnlyContain(e => !e.Mentions.PingsAnything);
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task G_duplicate_online_statements_and_events_give_one_announcement_one_ping_one_session()
    {
        await using var bed = await WatchingAsync();
        var online = new LiveObservation(LivePlatform.Twitch, Toro, ObservationKind.Status, true, bed.Now, "t1", bed.Now, "Yayın", EventId: "online-1");
        await bed.ApplyAsync(online);
        await bed.ApplyAsync(online);
        await bed.ApplyAsync(online with { EventId = "online-2" }); // at-least-once redelivery under a new id, same content
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await StepsAsync(bed, 3);

        bed.Messages.Should().ContainSingle();
        bed.EveryonePings.Should().Be(1);
        bed.Messages[0].Edits.Should().BeEmpty();
        (await bed.CreatorAsync(Toro)).SessionNumber.Should().Be(1);
        (await bed.OutboxAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task H_restart_midstream_sends_no_duplicate_announcement_and_no_everyone()
    {
        await using var first = await WatchingAsync();
        var start = first.Now;
        first.Twitch.GoLive(Toro, Stream("t1", start, "Uzun yayın"));
        first.Kick.GoLive(Toro, Stream("k1", start, "Uzun yayın"));
        await first.StepAsync(Poll);
        await StepsAsync(first, 2);
        first.Messages.Should().ContainSingle();

        // Railway deploy: new process on the same database and the same Discord channel, provider still live.
        await using var second = await CreateAsync(restartOf: first, start: first.Now + TimeSpan.FromMinutes(2));
        await second.PollAsync();
        await StepsAsync(second, 5);

        second.Messages.Should().ContainSingle("no duplicate announcement after a restart");
        second.EveryonePings.Should().Be(1, "still only the original ping");
        second.Transport.EditCalls.Should().Be(0, "nothing changed");
        (await second.CreatorAsync(Toro)).SessionNumber.Should().Be(1);

        // A title change after the restart still edits the original message.
        second.Twitch.GoLive(Toro, Stream("t1", start, "Restart sonrası başlık"));
        second.Kick.GoLive(Toro, Stream("k1", start, "Restart sonrası başlık"));
        await second.StepAsync(Poll);
        second.Messages.Should().ContainSingle();
        second.Messages[0].Edits[^1].Embed!.Title.Should().Be("Restart sonrası başlık");
        second.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task I_short_reconnect_within_the_grace_keeps_the_session_and_sends_no_new_ping()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await bed.StepAsync(Poll);
        bed.Twitch.GoOffline(Toro); // OBS reconnect
        await bed.StepAsync(Poll);
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.ReconnectGrace);
        bed.Twitch.GoLive(Toro, Stream("t2", bed.Now + TimeSpan.FromSeconds(10), "Yayın"));
        await StepsAsync(bed, 4);

        bed.Messages.Should().ContainSingle();
        bed.EveryonePings.Should().Be(1);
        bed.Messages[0].Edits.Should().BeEmpty("a flap inside the grace window does not even edit the card");
        var state = await bed.CreatorAsync(Toro);
        state.Phase.Should().Be(CreatorPhase.Live);
        state.SessionNumber.Should().Be(1);
    }

    [Fact]
    public async Task J_real_new_stream_after_the_grace_gets_a_new_session_announcement_and_ping()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Birinci yayın"));
        await bed.StepAsync(Poll);
        bed.Twitch.GoOffline(Toro);
        await StepsAsync(bed, 6); // 180 s > 120 s grace, confirmed offline after the deadline

        var ended = await bed.CreatorAsync(Toro);
        ended.Phase.Should().Be(CreatorPhase.Offline);
        var first = bed.Messages.Should().ContainSingle().Subject;
        var endCard = first.Edits.Should().ContainSingle("the card is edited to 'ended'").Subject;
        endCard.Mentions.PingsAnything.Should().BeFalse();
        endCard.Content.Should().Be("⚫ **LORDTORO** yayını sona erdi.");
        endCard.Buttons.Should().BeNullOrEmpty();
        endCard.Embed!.Description.Should().Contain("Yayın sona erdi").And.Contain("Süre:");

        bed.Twitch.GoLive(Toro, Stream("t9", bed.Now + TimeSpan.FromMinutes(40), "İkinci yayın"));
        bed.Host.Clock.Advance(TimeSpan.FromMinutes(40));
        await StepsAsync(bed, 2);

        bed.Messages.Should().HaveCount(2);
        bed.EveryonePings.Should().Be(2, "a real new stream is announced again");
        bed.Messages[1].Message.Embed!.Title.Should().Be("İkinci yayın");
        (await bed.CreatorAsync(Toro)).SessionNumber.Should().Be(2);
    }

    [Fact]
    public async Task K_twitch_api_failure_does_not_stop_kick_tracking()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.Failure = LiveProviderOutcome.TransportError;
        bed.Kick.GoLive(Nasil, Stream("k1", bed.Now, "Kick yayını"));
        await StepsAsync(bed, 3);

        bed.Messages.Should().ContainSingle();
        bed.EveryonePings.Should().Be(1);
        (await bed.PlatformAsync(Nasil, LivePlatform.Twitch)).Status.Should().Be(PlatformStatus.Offline, "last known state kept, not guessed");
        (await bed.PlatformAsync(Nasil, LivePlatform.Kick)).Status.Should().Be(PlatformStatus.Live);
    }

    [Fact]
    public async Task L_http_500_and_timeouts_keep_the_previous_state_and_never_end_the_session()
    {
        await using var bed = await WatchingAsync();
        var start = bed.Now;
        bed.Twitch.GoLive(Toro, Stream("t1", start, "Yayın"));
        await bed.StepAsync(Poll);
        bed.Twitch.Failure = LiveProviderOutcome.TransportError; // HTTP 500 (after the bounded retry)
        bed.Twitch.GoOffline(Toro); // what the provider would say — but it cannot answer
        await StepsAsync(bed, 10);
        bed.Twitch.Failure = LiveProviderOutcome.Timeout;
        await StepsAsync(bed, 10);

        (await bed.PlatformAsync(Toro, LivePlatform.Twitch)).Status.Should().Be(PlatformStatus.Live, "a failed request is never 'offline'");
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.Live);
        bed.Messages.Should().ContainSingle();
        bed.Messages[0].Edits.Should().BeEmpty("no premature 'ended' card");

        // Provider back, streamer still live: same session, nothing new.
        bed.Twitch.Failure = null;
        bed.Twitch.GoLive(Toro, Stream("t1", start, "Yayın"));
        bed.Host.Clock.Advance(TimeSpan.FromMinutes(15)); // past any backoff
        await StepsAsync(bed, 2);
        bed.Messages.Should().ContainSingle();
        bed.EveryonePings.Should().Be(1);
        (await bed.CreatorAsync(Toro)).SessionNumber.Should().Be(1);
    }

    [Fact]
    public async Task L2_an_outage_during_the_grace_postpones_the_end_until_offline_is_confirmed()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await bed.StepAsync(Poll);
        bed.Twitch.GoOffline(Toro);
        await bed.StepAsync(Poll); // grace entered
        bed.Twitch.Failure = LiveProviderOutcome.TransportError;
        bed.Host.Clock.Advance(TimeSpan.FromMinutes(5));
        await StepsAsync(bed, 3);
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.ReconnectGrace, "offline is not confirmed after the deadline");

        bed.Twitch.Failure = null;
        bed.Host.Clock.Advance(TimeSpan.FromMinutes(15));
        await StepsAsync(bed, 2);
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.Offline);
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task M_bootstrap_while_live_records_a_baseline_and_never_pings()
    {
        await using var bed = await CreateAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now - TimeSpan.FromHours(3), "3 saattir yayındayız"));
        bed.Kick.GoLive(Toro, Stream("k1", bed.Now - TimeSpan.FromHours(3), "3 saattir yayındayız"));
        await bed.PollAsync();
        await StepsAsync(bed, 5);

        bed.Messages.Should().BeEmpty();
        var state = await bed.CreatorAsync(Toro);
        state.Phase.Should().Be(CreatorPhase.Live);
        state.Announced.Should().BeFalse();
        state.NotAnnouncedReason.Should().Be("bootstrap");

        // The baseline session ends normally; the NEXT real stream is announced.
        bed.Twitch.GoOffline(Toro);
        bed.Kick.GoOffline(Toro);
        await StepsAsync(bed, 6);
        bed.Twitch.GoLive(Toro, Stream("t2", bed.Now, "Yeni yayın"));
        await bed.StepAsync(Poll);
        bed.Messages.Should().ContainSingle();
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task M2_announce_existing_live_on_bootstrap_can_be_switched_on()
    {
        await using var bed = await CreateAsync(new() { ["Live:AnnounceExistingLiveOnBootstrap"] = "true" });
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now - TimeSpan.FromMinutes(5), "Açık"));
        await bed.PollAsync();

        bed.Messages.Should().ContainSingle();
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task N_a_deleted_announcement_is_replaced_once_without_any_mention()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await bed.StepAsync(Poll);
        var original = bed.Messages.Should().ContainSingle().Subject;
        bed.Transport.DeleteMessage(original.Id); // a moderator deleted it

        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now - Poll, "Yeni başlık")); // the next edit finds it missing
        await StepsAsync(bed, 3);

        var replacement = bed.Messages.Should().ContainSingle().Subject;
        replacement.Id.Should().NotBe(original.Id);
        replacement.Message.Mentions.PingsAnything.Should().BeFalse();
        replacement.Message.Content.Should().Be("🔴 **LORDTORO** yayında!").And.NotContain("@everyone");
        replacement.Message.Embed!.Title.Should().Be("Yeni başlık");
        var state = await bed.CreatorAsync(Toro);
        state.Replacements.Should().Be(1);
        state.AnnouncementMessageId.Should().Be(replacement.Id.Value);

        // Deleted again: no second replacement (spam-safe), and never a ping.
        bed.Transport.DeleteMessage(replacement.Id);
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now - Poll, "Bir başlık daha"));
        await StepsAsync(bed, 4);
        bed.Messages.Should().BeEmpty();
        bed.Transport.Messages.Should().NotContain(m => m.Message.Mentions.PingsAnything, "only the original (deleted) announcement ever pinged");
        bed.Transport.SendCalls.Should().Be(2, "the original and exactly one replacement");
    }

    [Fact]
    public async Task O_rapid_and_out_of_order_title_updates_end_on_the_newest_title()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Başlık 0"));
        await bed.StepAsync(Poll);
        var t = bed.Now;
        LiveObservation Title(string title, int seconds, string id) =>
            new(LivePlatform.Twitch, Toro, ObservationKind.Metadata, false, t + TimeSpan.FromSeconds(seconds), Title: title, EventId: id);

        await bed.ApplyAsync(Title("Başlık 1", 1, "e1"), Title("Başlık 3", 3, "e3"), Title("Başlık 2", 2, "e2"));
        await bed.ApplyAsync(Title("Başlık 3", 3, "e3")); // duplicate delivery
        await bed.ApplyAsync(Title("Başlık 1", 1, "e1b")); // late, older event under a new id

        var message = bed.Messages.Should().ContainSingle().Subject;
        message.Edits[^1].Embed!.Title.Should().Be("Başlık 3");
        (await bed.PlatformAsync(Toro, LivePlatform.Twitch)).Title.Should().Be("Başlık 3");
        message.Edits.Should().ContainSingle("the three updates were applied in one round; the stale ones changed nothing");
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task A_second_platform_going_offline_edits_the_card_and_the_session_continues()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Çift yayın"));
        bed.Kick.GoLive(Toro, Stream("k1", bed.Now, "Çift yayın"));
        await bed.StepAsync(Poll);
        bed.Kick.GoOffline(Toro);
        await StepsAsync(bed, 8);

        var message = bed.Messages.Should().ContainSingle().Subject;
        message.Edits.Should().ContainSingle();
        message.Edits[0].Buttons!.Select(b => b.Label).Should().Equal("Twitch'te İzle");
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.Live);
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task Two_creators_are_independent_sessions_with_one_ping_each()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Toro"));
        bed.Kick.GoLive(Nasil, Stream("k1", bed.Now, "Nasıl"));
        await StepsAsync(bed, 3);

        bed.Messages.Should().HaveCount(2);
        bed.EveryonePings.Should().Be(2);
        bed.Messages.Select(m => m.Message.Content).Should().BeEquivalentTo("@everyone 🔴 **LORDTORO** yayında!", "@everyone 🔴 **NASILYANI69** yayında!");
    }

    [Fact]
    public async Task A_stream_that_started_while_the_bot_was_down_is_announced_after_the_restart_whatever_its_age()
    {
        // Known offline, then the bot stops.
        await using var first = await WatchingAsync();
        await StepsAsync(first, 2);
        var stoppedAt = first.Now;

        // LORDTORO goes live 2 minutes after the stop; the bot is back 22 minutes after the stop (stream 20 minutes old).
        first.Twitch.GoLive(Toro, Stream("t1", stoppedAt + TimeSpan.FromMinutes(2), "Bot kapalıyken başladı"));
        await using var second = await CreateAsync(restartOf: first, start: stoppedAt + TimeSpan.FromMinutes(22));
        await second.PollAsync();
        await StepsAsync(second, 4);

        var message = second.Messages.Should().ContainSingle("a genuine new session, even though the bot missed its start").Subject;
        message.Message.Embed!.Title.Should().Be("Bot kapalıyken başladı");
        second.EveryonePings.Should().Be(1);
        (await second.CreatorAsync(Toro)).Should().Match<CreatorState>(s => s.Announced && s.SessionNumber == 1);
        (await second.CreatorAsync(Nasil)).SessionNumber.Should().Be(0, "nothing started for the other creator");
    }

    [Fact]
    public async Task Switching_live_tracking_off_rebaselines_so_re_enabling_never_announces_a_stale_stream()
    {
        await using var first = await WatchingAsync();
        first.Twitch.GoLive(Toro, Stream("t1", first.Now, "Birinci"));
        await first.StepAsync(Poll);
        first.Messages.Should().ContainSingle();

        // Host restarted with Live:Enabled=false: tracking pauses (channels forgotten, open session closed).
        await using (var off = await CreateAsync(new() { ["Live:Enabled"] = "false" }, restartOf: first, start: first.Now + TimeSpan.FromMinutes(1)))
            await off.Host.Services.GetRequiredService<LiveCoordinator>().PauseTrackingAsync(CancellationToken.None);

        // Two hours later tracking is switched on again; a different stream started while it was off.
        first.Twitch.GoLive(Toro, Stream("t2", first.Now + TimeSpan.FromMinutes(90), "Kapalıyken başlayan"));
        await using var on = await CreateAsync(restartOf: first, start: first.Now + TimeSpan.FromHours(2));
        await on.PollAsync();
        await StepsAsync(on, 3);

        on.Messages.Should().ContainSingle("only the original announcement; no stale @everyone");
        on.EveryonePings.Should().Be(1);
        on.Messages[0].Edits.Should().ContainSingle().Which.Content.Should().Be("⚫ **LORDTORO** yayını sona erdi.", "the old session was closed");
        var state = await on.CreatorAsync(Toro);
        state.Announced.Should().BeFalse();
        state.NotAnnouncedReason.Should().Be("bootstrap");
    }

    // ------------------------------------------------------------------ @everyone at most once (ambiguous delivery)

    [Fact]
    public async Task Ambiguous_create_that_Discord_accepted_is_reconciled_and_pings_exactly_once()
    {
        await using var bed = await WatchingAsync();
        bed.Transport.ScriptAcceptedButTimedOut(); // Discord accepts the message, the response is lost
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await bed.StepAsync(Poll);
        (await bed.OutboxAsync()).Single().Status.Should().Be(OutboxStatus.DeliveryUnknown);
        await StepsAsync(bed, 4); // reconciliation finds the message

        bed.Messages.Should().ContainSingle();
        bed.EveryonePings.Should().Be(1, "maximum one actual @everyone");
        (await bed.OutboxAsync()).Single().Status.Should().Be(OutboxStatus.Sent);
        bed.Transport.SendCalls.Should().Be(1, "no blind resend");
    }

    [Fact]
    public async Task Ambiguous_create_whose_message_cannot_be_recovered_is_resent_once_without_any_mention()
    {
        await using var bed = await WatchingAsync();
        bed.Transport.ScriptAcceptedButTimedOut();          // it DID ping …
        bed.Transport.ScriptedReconcile = new ReconcileOutcome.NotFound(); // … but scrolled out of the scanned history
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await bed.StepAsync(Poll);
        await StepsAsync(bed, 4);

        bed.Messages.Should().HaveCount(2, "the original (unseen by us) and one replacement");
        bed.EveryonePings.Should().Be(1, "maximum one actual @everyone");
        var replacement = bed.Messages[1].Message;
        replacement.Mentions.PingsAnything.Should().BeFalse("allowed_mentions = none after an uncertain delivery");
        replacement.Content.Should().NotContain("@everyone");
        bed.Transport.SendCalls.Should().Be(2, "exactly one resend, never a loop");
    }

    [Fact]
    public async Task Ambiguous_create_that_never_reached_Discord_is_resent_without_mention_rather_than_risking_a_second_ping()
    {
        await using var bed = await WatchingAsync();
        bed.Transport.ScriptSend(() => new SendOutcome.Ambiguous("HTTP 502 on create")); // nothing was created
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await bed.StepAsync(Poll);
        await StepsAsync(bed, 4);

        var message = bed.Messages.Should().ContainSingle().Subject.Message;
        message.Mentions.PingsAnything.Should().BeFalse("we cannot prove the first attempt did not ping");
        bed.EveryonePings.Should().Be(0);
    }

    [Fact]
    public async Task Proven_non_delivery_is_retried_normally_with_the_single_everyone()
    {
        await using var bed = await WatchingAsync();
        bed.Transport.ScriptSend(() => new SendOutcome.RateLimited(TimeSpan.FromSeconds(1)));
        bed.Transport.ScriptSend(() => new SendOutcome.Transient("discord client not logged in yet"));
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await bed.StepAsync(Poll);
        await StepsAsync(bed, 4);

        bed.Messages.Should().ContainSingle();
        bed.EveryonePings.Should().Be(1, "a 429 / not-connected attempt provably created nothing");
    }

    [Fact]
    public async Task Restart_during_an_in_flight_send_never_produces_a_second_mention()
    {
        await using var first = await WatchingAsync();
        first.Twitch.GoLive(Toro, Stream("t1", first.Now, "Yayın"));
        first.Host.Clock.Advance(Poll);
        await first.Host.Services.GetRequiredService<LivePoller>().TickAsync(CancellationToken.None); // staged, not delivered
        var row = (await first.OutboxAsync()).Single();

        // The process dies right after Discord accepted the create: the message exists, the row is still InFlight.
        var payload = ToroSquad.Infrastructure.Delivery.PayloadSerializer.Deserialize(row.PayloadJson);
        (await first.Transport.SendAsync(Channel, payload, CancellationToken.None)).Should().BeOfType<SendOutcome.Sent>();
        await first.Host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroSquad.Infrastructure.Persistence.ToroDbContext>();
            var tracked = await db.Outbox.SingleAsync(o => o.Id == row.Id);
            tracked.Status = OutboxStatus.InFlight;
            tracked.Attempts = 1;
            tracked.LastAttemptAt = first.Now;
            tracked.DeliveredPayloadHash = tracked.PayloadHash;
            tracked.DeliveredFingerprint = MessageFingerprint.Of(payload);
            await db.SaveChangesAsync();
        });

        // Restart; the message scrolled out of the scanned history, so reconciliation cannot find it.
        first.Transport.ScriptedReconcile = new ReconcileOutcome.NotFound();
        await using var second = await CreateAsync(restartOf: first, start: first.Now + TimeSpan.FromMinutes(1));
        await second.Host.Services.GetRequiredService<ToroSquad.Infrastructure.Delivery.OutboxProcessor>().RecoverAsync(CancellationToken.None);
        await second.PollAsync();
        await StepsAsync(second, 4);

        second.EveryonePings.Should().Be(1, "only the message sent before the crash pinged");
        second.Messages.Should().HaveCount(2);
        second.Messages[1].Message.Mentions.PingsAnything.Should().BeFalse();
        (await second.CreatorAsync(Toro)).SessionNumber.Should().Be(1);
    }

    [Fact]
    public async Task Switching_dry_run_to_send_mid_session_never_announces_the_session_again()
    {
        await using var first = await WatchingAsync(new() { ["Delivery:Mode"] = "DryRun" });
        first.Twitch.GoLive(Toro, Stream("t1", first.Now, "Yayın"));
        await first.StepAsync(Poll);
        first.Messages.Should().BeEmpty("dry run");

        await using var second = await CreateAsync(restartOf: first, start: first.Now + TimeSpan.FromMinutes(1));
        await second.PollAsync();
        await StepsAsync(second, 3);
        second.Messages.Should().BeEmpty("the session was already announced (in dry run): no late @everyone");
    }

    [Fact]
    public async Task A_session_that_starts_while_the_module_is_disabled_is_never_announced_later()
    {
        await using var bed = await CreateAsync(enableModule: false);
        await bed.PollAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Kapalıyken"));
        await bed.StepAsync(Poll);
        await bed.SetModuleAsync(true);
        await StepsAsync(bed, 3);

        bed.Messages.Should().BeEmpty("enabling mid-stream never sends a late @everyone");
        (await bed.CreatorAsync(Toro)).NotAnnouncedReason.Should().Be("delivery_off");
    }

    [Fact]
    public async Task Provider_title_text_cannot_inject_mentions_markdown_or_links()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "@everyone <@&1> **bold** https://evil.example [x](https://evil.example)", "@here `game`"));
        await bed.StepAsync(Poll);

        var message = bed.Messages.Should().ContainSingle().Subject.Message;
        var providerText = string.Join("\n", message.Embed!.Title, message.Embed.Description);
        DiscordText.RawMentionPattern().IsMatch(providerText).Should().BeFalse();
        providerText.Should().NotContain("://");
        message.Buttons!.Should().OnlyContain(b => b.Url!.StartsWith("https://www.twitch.tv/", StringComparison.Ordinal) || b.Url.StartsWith("https://kick.com/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_announcement_not_delivered_in_time_expires_instead_of_pinging_late()
    {
        await using var bed = await WatchingAsync();
        for (var i = 0; i < 10; i++)
            bed.Transport.ScriptSend(() => new SendOutcome.Transient("discord down"));
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın"));
        await bed.StepAsync(Poll);
        bed.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await StepsAsync(bed, 2);

        bed.Messages.Should().BeEmpty();
        (await bed.OutboxAsync()).Should().ContainSingle().Which.Status.Should().BeOneOf(OutboxStatus.Expired, OutboxStatus.Failed);
    }
}
