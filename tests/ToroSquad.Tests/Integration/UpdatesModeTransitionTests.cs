using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Bot Updates mode changes across restarts on the SAME database (a second host over the first host's data directory,
/// as a Railway restart with another Updates:Mode would be). Queued live cards, queued edits and reconciliation resends
/// must never reach Discord while the mode is Off or DryRun; DryRun keeps simulating; and nothing seen under DryRun or
/// published while Off is ever posted when Live starts — with or without the pause → Live → resume rollout.
/// </summary>
public sealed class UpdatesModeTransitionTests
{
    private static readonly GuildId Guild = UpdatesRig.Guild;
    private static readonly ChannelId Channel = UpdatesRig.Channel;
    private static readonly DateTimeOffset Start = UpdatesRig.Start;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static SteamPost Old => SteamNews.Announcement(1, Start.AddDays(-3));

    private static SteamPost Update(long gid, DateTimeOffset at, string title = "Counter-Strike 2 Update") => SteamNews.Update(gid, at, title);

    private static string Link(long gid) => "(" + SteamNews.CardUrl(gid.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ")";

    private static async Task<UpdatesRig> BaselinedAsync(string mode, Dictionary<string, string?>? extra = null)
    {
        var rig = await UpdatesRig.CreateAsync(mode, extra);
        rig.Steam.Serve([Old]);
        (await rig.PollAsync()).Should().Be(1);
        return rig;
    }

    /// <summary>Baseline, then one update staged (not delivered yet) in Live.</summary>
    private static async Task<UpdatesRig> LiveWithQueuedCardAsync()
    {
        var live = await BaselinedAsync("Live");
        live.Steam.Serve([Old, Update(10, live.Now.AddMinutes(1))]);
        await live.PollAsync();
        (await live.OutboxAsync()).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Pending);
        return live;
    }

    // ---------- Off ----------

    [Fact]
    public async Task Off_requests_nothing_and_plans_nothing()
    {
        await using var off = await UpdatesRig.CreateAsync("Off");
        off.Steam.Serve([Old]);
        (await off.PollAsync()).Should().Be(0);
        off.Host.Clock.Advance(TimeSpan.FromHours(3));
        (await off.Poller.TickAsync(Ct)).Should().Be(0);
        off.SteamRequests.Should().Be(0);
        (await off.StatesAsync()).Should().BeEmpty();
        (await off.OutboxAsync()).Should().BeEmpty();

        var (_, status) = await off.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct));
        status!.Mode.Should().Be(UpdatesMode.Off);
        (await off.ConfigAsync(c => c.DoctorAsync(TestHost.Admin(Guild), Ct))).Checks.Should().Contain(c => c.DetailKey == "updates.doctor.mode_off");
        var health = await off.Host.Services.GetServices<IModuleHealthCheck>().Single(h => h.Module == new ModuleId("updates")).CheckAsync(Ct);
        health.Overall.Should().Be(HealthState.NotConfigured);
    }

    [Fact]
    public async Task Off_cancels_a_queued_live_card_and_live_does_not_send_it_later()
    {
        await using var live = await LiveWithQueuedCardAsync();
        await using (var off = await UpdatesRig.CreateAsync("Off", shareWith: live))
        {
            await off.DeliverAsync();
            off.Host.Transport.SendCalls.Should().Be(0, "Updates Off never reaches Discord, even for a card queued under Live");
            var row = (await off.OutboxAsync()).Single();
            (row.Status, row.LastError).Should().Be((OutboxStatus.Cancelled, "updates_mode_off"));
        }

        await using var again = await UpdatesRig.CreateAsync("Live", shareWith: live);
        await again.DeliverAsync();
        again.Steam.Serve([Old, Update(10, Start.AddMinutes(1))]);
        await again.PollAsync();
        await again.DeliverAsync();
        again.Host.Transport.SendCalls.Should().Be(0, "a card cancelled while Off is not revived when Live returns");
        (await again.OutboxAsync()).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Cancelled);
    }

    [Fact]
    public async Task A_queued_live_edit_under_Off_is_dropped_and_the_posted_card_stays()
    {
        await using var live = await BaselinedAsync("Live");
        var t = live.Now.AddMinutes(1);
        live.Steam.Serve([Old, Update(15, t)]);
        await live.PollAsync();
        await live.DeliverAsync();
        live.Steam.Serve([Old, Update(15, t, "Counter-Strike 2 Pre-Release Update")]);
        await live.PollAsync();
        (await live.OutboxAsync()).Single().EditPending.Should().BeTrue();

        await using var off = await UpdatesRig.CreateAsync("Off", shareWith: live);
        await off.DeliverAsync();
        off.Host.Transport.EditCalls.Should().Be(0);
        off.Host.Transport.DeleteCalls.Should().Be(0);
        var row = (await off.OutboxAsync()).Single();
        row.Status.Should().Be(OutboxStatus.Sent, "the message already in Discord stays; Off cannot take it back");
        row.EditPending.Should().BeFalse();
        row.LastError.Should().Be("edit_skipped_updates_mode_off");
    }

    [Fact]
    public async Task A_reconciliation_resend_after_a_restart_into_Off_never_sends()
    {
        await using var live = await LiveWithQueuedCardAsync();
        live.Host.Transport.ScriptAcceptedButTimedOut();
        await live.DeliverAsync();
        (await live.OutboxAsync()).Single().Status.Should().Be(OutboxStatus.DeliveryUnknown);

        await using var off = await UpdatesRig.CreateAsync("Off", shareWith: live);
        for (var i = 0; i < 4; i++)
        {
            off.Host.Clock.Advance(TimeSpan.FromMinutes(1));
            await off.DeliverAsync();
        }

        off.Host.Transport.SendCalls.Should().Be(0, "the new process cannot see the lost message; its resend is stopped by the Updates mode");
        var row = (await off.OutboxAsync()).Single();
        (row.Status, row.LastError).Should().Be((OutboxStatus.Cancelled, "updates_mode_off"));
    }

    [Fact]
    public async Task The_hosted_service_records_the_mode_at_startup_even_when_Off_and_then_stops()
    {
        await using var live = await BaselinedAsync("Live");
        (await live.GuildConfigAsync(Guild))!.PlannedMode.Should().Be((int)UpdatesMode.Live);

        await using var off = await UpdatesRig.CreateAsync("Off", shareWith: live, enterMode: false);
        await off.Poller.StartAsync(Ct);
        await off.Poller.ExecuteTask!;
        (await off.GuildConfigAsync(Guild))!.PlannedMode.Should().Be((int)UpdatesMode.Off);
        off.SteamRequests.Should().Be(0);
        await off.Poller.StopAsync(Ct);
    }

    [Fact]
    public async Task Recording_the_mode_can_fail_and_is_retried_until_it_is_stored()
    {
        await using var live = await BaselinedAsync("Live");
        await using var off = await UpdatesRig.CreateAsync("Off", shareWith: live, enterMode: false);
        await off.Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Database.ExecuteSqlRawAsync("ALTER TABLE updates_guild_config RENAME TO updates_guild_config_away", Ct));
        (await off.Poller.TryEnterModeAsync(Ct)).Should().BeFalse("the database refused; the failure is reported, not thrown");
        (await off.Poller.TryEnterModeAsync(Ct)).Should().BeFalse();

        await off.Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Database.ExecuteSqlRawAsync("ALTER TABLE updates_guild_config_away RENAME TO updates_guild_config", Ct));
        (await off.Poller.TryEnterModeAsync(Ct)).Should().BeTrue();
        (await off.GuildConfigAsync(Guild))!.PlannedMode.Should().Be((int)UpdatesMode.Off, "so a later Live start begins a new window");
    }

    [Fact]
    public async Task A_queued_live_card_is_cancelled_when_the_host_stops_sending()
    {
        await using var live = await LiveWithQueuedCardAsync();
        await using var held = await UpdatesRig.CreateAsync("Live", new Dictionary<string, string?> { ["Delivery:Mode"] = "DryRun" }, shareWith: live);
        await held.DeliverAsync();
        var row = (await held.OutboxAsync()).Single();
        (row.Status, row.LastError).Should().Be((OutboxStatus.Cancelled, "updates_mode_dry_run"), "not held for a later send: a host that does not send is a dry run");

        held.Host.Clock.Advance(TimeSpan.FromMinutes(30));
        await using var again = await UpdatesRig.CreateAsync("Live", shareWith: held);
        await again.DeliverAsync();
        again.Host.Transport.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_planner_itself_starts_a_new_window_when_it_meets_a_guild_planned_in_another_mode()
    {
        await using var dry = await BaselinedAsync("DryRun");
        var underDryRun = dry.Now.AddMinutes(2);
        dry.Host.Clock.Advance(TimeSpan.FromMinutes(10));

        // A Live host whose startup step did not run: the planner is called directly.
        await using var live = await UpdatesRig.CreateAsync("Live", shareWith: dry, enterMode: false);
        var game = ToroSquad.Modules.Updates.Domain.Games.Cs2Game.Definition;
        var posts = ToroSquad.Modules.Updates.Providers.SteamNewsParser.Parse(SteamNews.Bytes(SteamNews.Json([Old, Update(70, underDryRun)])), game, live.Now).Items;
        var summary = await live.Host.InScopeAsync(sp => sp.GetRequiredService<UpdatesPlanner>().ApplyAsync(game,
            new ToroSquad.Modules.Updates.Domain.UpdateFetchResult(ToroSquad.Modules.Updates.Domain.UpdateFetchOutcome.Ok, 200, posts, 0, null, null, null), live.Now.AddMinutes(5), Ct));
        summary.NewItems.Should().Be(1);
        summary.Staged.Should().Be(0, "published before Live started for this guild");
        var config = await live.GuildConfigAsync(Guild);
        (config!.PlannedMode, config.LiveSince).Should().Be(((int)UpdatesMode.Live, live.Now));
        (await live.OutboxAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Other_modules_are_not_affected_by_the_updates_mode()
    {
        await using var off = await UpdatesRig.CreateAsync("Off");
        await off.Host.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<INotificationOutbox>().StageAsync(new NotificationRequest(Guild, new ModuleId("core"), "x", Channel, "test",
                new OutgoingMessage("hello", null, MentionPolicy.None), Start.AddHours(1), false), Ct);
            await sp.GetRequiredService<ToroDbContext>().SaveChangesAsync(Ct);
        });
        await off.DeliverAsync();
        off.Host.Transport.SendCalls.Should().Be(1);
    }

    // ---------- DryRun ----------

    [Fact]
    public async Task DryRun_simulates_and_never_reaches_discord()
    {
        await using var dry = await BaselinedAsync("DryRun");
        dry.Steam.Serve([Old, Update(20, dry.Now.AddMinutes(1))]);
        await dry.PollAsync();
        await dry.DeliverAsync();

        dry.Host.Transport.SendCalls.Should().Be(0);
        var row = (await dry.OutboxAsync()).Should().ContainSingle().Subject;
        (row.IsDryRun, row.Status, row.Kind, row.SourceKey).Should().Be((true, OutboxStatus.Simulated, "update-dry:cs2", "steam:730:20"));
        var status = await dry.GameStatusAsync(Guild);
        status.LastCardAt.Should().BeNull("a simulated card is not a live card");
        status.LastDiscovered!.ExternalId.Should().Be("20");
        (await dry.ConfigAsync(c => c.DoctorAsync(TestHost.Admin(Guild), Ct))).Checks.Should().Contain(c => c.DetailKey == "updates.doctor.mode_dry");
    }

    [Fact]
    public async Task DryRun_cancels_queued_live_sends_drops_queued_live_edits_and_keeps_simulating()
    {
        await using var live = await BaselinedAsync("Live");
        var t = live.Now.AddMinutes(1);
        live.Steam.Serve([Old, Update(30, t)]);
        await live.PollAsync();
        await live.DeliverAsync();
        live.Host.Transport.SendCalls.Should().Be(1);
        live.Steam.Serve([Old, Update(30, t, "Counter-Strike 2 Pre-Release Update"), Update(31, live.Now.AddMinutes(1))]);
        await live.PollAsync();
        var queued = await live.OutboxAsync();
        queued.Single(r => r.SourceKey == "steam:730:30").EditPending.Should().BeTrue();
        queued.Single(r => r.SourceKey == "steam:730:31").Status.Should().Be(OutboxStatus.Pending);

        await using var dry = await UpdatesRig.CreateAsync("DryRun", shareWith: live);
        await dry.DeliverAsync();
        dry.Host.Transport.SendCalls.Should().Be(0);
        dry.Host.Transport.EditCalls.Should().Be(0);
        var rows = await dry.OutboxAsync();
        var edited = rows.Single(r => r.SourceKey == "steam:730:30");
        edited.Status.Should().Be(OutboxStatus.Sent);
        edited.EditPending.Should().BeFalse();
        edited.LastError.Should().Be("edit_skipped_updates_mode_dry_run");
        var cancelled = rows.Single(r => r.SourceKey == "steam:730:31");
        (cancelled.Status, cancelled.LastError).Should().Be((OutboxStatus.Cancelled, "updates_mode_dry_run"));

        // Simulation continues — for updates published after DryRun started.
        dry.Steam.Serve([Old, Update(30, t, "Counter-Strike 2 Pre-Release Update"), Update(31, t.AddMinutes(5)), Update(32, dry.Now.AddMinutes(1))]);
        await dry.PollAsync();
        await dry.DeliverAsync();
        var simulated = (await dry.OutboxAsync()).Where(r => r.IsDryRun).ToList();
        simulated.Should().ContainSingle().Which.Should().Match<OutboxMessageEntity>(r => r.Status == OutboxStatus.Simulated && r.SourceKey == "steam:730:32" && r.Kind == "update-dry:cs2");
        dry.Host.Transport.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_queued_dry_run_card_is_only_ever_simulated_also_after_a_restart_into_Live()
    {
        await using var dry = await BaselinedAsync("DryRun");
        dry.Steam.Serve([Old, Update(35, dry.Now.AddMinutes(1))]);
        await dry.PollAsync();
        (await dry.OutboxAsync()).Single().Status.Should().Be(OutboxStatus.Pending);

        await using var live = await UpdatesRig.CreateAsync("Live", shareWith: dry);
        await live.DeliverAsync();
        live.Host.Transport.SendCalls.Should().Be(0);
        (await live.OutboxAsync()).Single().Status.Should().Be(OutboxStatus.Simulated);
    }

    [Fact]
    public async Task Live_with_a_non_sending_host_is_a_dry_run_and_switching_delivery_on_posts_nothing_old()
    {
        var noSend = new Dictionary<string, string?> { ["Delivery:Mode"] = "DryRun" };
        await using var dry = await BaselinedAsync("Live", noSend);
        dry.Steam.Serve([Old, Update(38, dry.Now.AddMinutes(1))]);
        await dry.PollAsync();
        await dry.DeliverAsync();
        dry.Host.Transport.SendCalls.Should().Be(0);
        (await dry.OutboxAsync()).Single().Kind.Should().Be("update-dry:cs2");
        var (_, status) = await dry.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct));
        (status!.Mode, status.EffectiveMode).Should().Be((UpdatesMode.Live, UpdatesMode.DryRun));

        dry.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        await using var live = await UpdatesRig.CreateAsync("Live", shareWith: dry);
        live.Steam.Serve([Old, Update(38, Start.AddMinutes(1))]);
        await live.PollAsync();
        await live.DeliverAsync();
        live.Host.Transport.SendCalls.Should().Be(0);
    }

    // ---------- DryRun → Live ----------

    [Fact]
    public async Task Switching_straight_from_DryRun_to_Live_never_posts_an_update_seen_under_DryRun()
    {
        await using var dry = await BaselinedAsync("DryRun");
        var simulatedAt = dry.Now.AddMinutes(1);
        dry.Steam.Serve([Old, Update(40, simulatedAt)]);
        await dry.PollAsync();
        await dry.DeliverAsync();
        (await dry.OutboxAsync()).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Simulated);
        var drySince = (await dry.GuildConfigAsync(Guild))!.LiveSince;

        dry.Host.Clock.Advance(TimeSpan.FromMinutes(1)); // the restart
        await using var live = await UpdatesRig.CreateAsync("Live", shareWith: dry);
        var config = await live.GuildConfigAsync(Guild);
        config!.PlannedMode.Should().Be((int)UpdatesMode.Live);
        config.LiveSince.Should().Be(live.Now).And.BeAfter(drySince!.Value, "the live window starts when Live starts");

        live.Steam.Serve([Old, Update(40, simulatedAt)]);
        await live.PollAsync();
        await live.DeliverAsync();
        await live.PollAsync();
        await live.DeliverAsync();
        live.Host.Transport.SendCalls.Should().Be(0, "an update simulated under DryRun is not posted live, although it is well inside the 24 h catch-up window");

        live.Steam.Serve([Old, Update(40, simulatedAt), Update(41, live.Now.AddMinutes(1))]);
        await live.PollAsync();
        await live.DeliverAsync();
        live.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(41));

        // A plain restart in the same mode keeps the window (so an outage is caught up, see UpdatesTests).
        var since = (await live.GuildConfigAsync(Guild))!.LiveSince;
        live.Host.Clock.Advance(TimeSpan.FromHours(1));
        await using var sameMode = await UpdatesRig.CreateAsync("Live", shareWith: live);
        (await sameMode.GuildConfigAsync(Guild))!.LiveSince.Should().Be(since);
    }

    [Fact]
    public async Task Pause_then_Live_then_resume_starts_with_updates_published_after_the_resume()
    {
        await using var dry = await BaselinedAsync("DryRun");
        var simulatedAt = dry.Now.AddMinutes(1);
        dry.Steam.Serve([Old, Update(50, simulatedAt)]);
        await dry.PollAsync();
        await dry.DeliverAsync();
        (await dry.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), true, Ct))).Succeeded.Should().BeTrue();

        dry.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        await using var live = await UpdatesRig.CreateAsync("Live", shareWith: dry);
        live.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        (await live.Poller.TickAsync(Ct)).Should().Be(0, "the only guild is paused: not even a request");
        var duringPause = live.Now.AddMinutes(-10);
        (await live.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), false, Ct))).MessageKey.Should().Be("updates.resume.done");

        live.Steam.Serve([Old, Update(50, simulatedAt), Update(51, duringPause), Update(52, live.Now.AddMinutes(2))]);
        await live.PollAsync();
        await live.DeliverAsync();
        await live.PollAsync();
        await live.DeliverAsync();
        live.CardTexts.Should().ContainSingle("the DryRun-period update and the one published during the pause are skipped").Which.Should().Contain(Link(52));
    }

    [Fact]
    public async Task An_update_published_while_Off_is_not_posted_when_Live_returns()
    {
        await using var live = await BaselinedAsync("Live");
        await using var off = await UpdatesRig.CreateAsync("Off", shareWith: live);
        off.Host.Clock.Advance(TimeSpan.FromHours(2));
        var whileOff = off.Now.AddHours(-1);

        await using var again = await UpdatesRig.CreateAsync("Live", shareWith: off);
        again.Steam.Serve([Old, Update(60, whileOff)]);
        await again.PollAsync();
        await again.DeliverAsync();
        again.Host.Transport.SendCalls.Should().Be(0, "turning the module Off is a decision, not an outage: nothing from that time is caught up");

        again.Steam.Serve([Old, Update(60, whileOff), Update(61, again.Now.AddMinutes(1))]);
        await again.PollAsync();
        await again.DeliverAsync();
        again.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(61));
    }
}
