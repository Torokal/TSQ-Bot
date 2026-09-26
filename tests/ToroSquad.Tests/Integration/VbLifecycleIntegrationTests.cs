using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Persistence;
using ToroSquad.Modules.Volleyball.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Volleyball notifications end to end on a real SQLite database with the real poller, workflow, planner, outbox and
/// dispatcher (only the provider and Discord are fakes). A wrong notification is worse than a missing one: every test
/// checks both that the right message exists and that nothing else was sent.
/// </summary>
public sealed class VbLifecycleIntegrationTests
{
    internal static readonly GuildId Guild = new(777);
    internal static readonly ChannelId Channel = new(7770);
    internal static readonly RoleId Role = new(7771);
    internal static readonly DateTimeOffset T0 = TestHost.T0;

    /// <summary>A Türkiye–Italy match 60 minutes after T0.</summary>
    internal static readonly DateTimeOffset Start = T0.AddMinutes(60);

    internal static async Task TickAsync(TestHost host) => await host.Services.GetRequiredService<VolleyballPoller>().TickAsync(CancellationToken.None);

    internal static async Task DeliverAsync(TestHost host) => await host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

    internal static async Task StepAsync(TestHost host, TimeSpan advance)
    {
        host.Clock.Advance(advance);
        await TickAsync(host);
        await DeliverAsync(host);
    }

    /// <summary>Advances in 1-minute ticks (like the real 15 s tick with a 60 s live poll).</summary>
    internal static async Task RunAsync(TestHost host, TimeSpan duration)
    {
        for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromMinutes(1))
            await StepAsync(host, TimeSpan.FromMinutes(1));
    }

    internal static Task<List<OutboxMessageEntity>> OutboxAsync(TestHost host, string kindPrefix = "") => host.InScopeAsync(sp =>
        sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "volleyball" && o.Kind.StartsWith(kindPrefix)).ToListAsync());

    internal static Task<VbMatchSnapshotEntity> SnapshotAsync(TestHost host, string key = "fakevb:m1") => host.InScopeAsync(sp =>
        sp.GetRequiredService<ToroDbContext>().Set<VbMatchSnapshotEntity>().AsNoTracking().SingleAsync(s => s.MatchKey == key));

    internal static async Task<(TestHost Host, VbFakeProvider World, VolleyballMatch Match)> MatchDayAsync(bool pingRole = true, Dictionary<string, string?>? overrides = null)
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync(overrides);
        var match = VbFakeProvider.Scheduled("m1", Start);
        world.Set(match);
        await host.SetUpVbGuildAsync(Guild, Channel, pingRole ? Role : null);
        return (host, world, match);
    }

    private static List<FakeMessageTransport.FakeMessage> Sent(TestHost host) => host.Transport.Messages.ToList();

    [Fact]
    public async Task Main_flow_reminder_started_each_set_and_final_exactly_once()
    {
        var (host, world, match) = await MatchDayAsync();
        await using var _ = host;

        // Discovery; nothing to announce yet.
        await StepAsync(host, TimeSpan.Zero);
        Sent(host).Should().BeEmpty();

        // T-15: exactly one reminder, pinging the configured role.
        await RunAsync(host, TimeSpan.FromMinutes(45));
        var reminder = Sent(host).Should().ContainSingle().Subject;
        reminder.Pinged.Should().BeTrue();
        reminder.Message.Mentions.Roles.Should().Equal(Role);
        reminder.Message.Embed!.Title.Should().Be("🇹🇷 Türkiye vs İtalya 🇮🇹");
        reminder.Message.Embed.Description.Should().Contain("⏳").And.Contain("<t:" + Start.ToUnixTimeSeconds() + ":R>").And.Contain("Volleyball Nations League");
        reminder.Message.Embed.Footer.Should().Be("Kaynak: FIVB");

        // The scheduled time passes WITHOUT a provider signal: no "started".
        await RunAsync(host, TimeSpan.FromMinutes(20));
        Sent(host).Should().HaveCount(1, "a scheduled time is never a match start");

        // Provider says live: one "started" (no ping).
        world.Set(match with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 });
        await RunAsync(host, TimeSpan.FromMinutes(2));
        var started = Sent(host).Should().HaveCount(2).And.Subject.Last();
        started.Pinged.Should().BeFalse();
        started.Message.Embed!.Description.Should().Contain("Maç başladı");

        // Set 1 Türkiye, set 2 Italy, set 3 Türkiye — one card each, correct wording, no duplicates on repeated polls.
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21)));
        await RunAsync(host, TimeSpan.FromMinutes(5));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25)));
        await RunAsync(host, TimeSpan.FromMinutes(5));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25), (25, 19)));
        await RunAsync(host, TimeSpan.FromMinutes(5));
        var sets = Sent(host).Skip(2).ToList();
        sets.Should().HaveCount(3);
        sets[0].Message.Embed!.Description.Should().Contain("SET TÜRKİYE'NİN").And.Contain("1. Set: 25-21");
        sets[0].Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 1-0 İtalya 🇮🇹");
        sets[1].Message.Embed!.Description.Should().NotContain("SET TÜRKİYE'NİN", "Italy won set 2").And.Contain("2. seti **İtalya** aldı");
        sets[1].Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 1-1 İtalya 🇮🇹");
        sets[2].Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 2-1 İtalya 🇮🇹");
        sets[2].Message.Embed!.Description.Should().Contain("1. Set: 25-21").And.Contain("2. Set: 22-25").And.Contain("3. Set: 25-19");
        sets.Should().OnlyContain(s => !s.Pinged, "set cards never ping");

        // Final 3-1: one final card; set 4 is superseded by the final (no separate set-4 card).
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (22, 25), (25, 19), (25, 23)));
        await RunAsync(host, TimeSpan.FromMinutes(10));
        var final = Sent(host).Should().HaveCount(6).And.Subject.Last();
        final.Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 3-1 İtalya 🇮🇹");
        final.Message.Embed.Description.Should().Contain("Filenin Sultanları kazandı").And.Contain("4. Set: 25-23");
        final.Pinged.Should().BeFalse("final pings are off by default");

        // Hours of identical provider answers: nothing new, nothing edited.
        await RunAsync(host, TimeSpan.FromMinutes(90));
        Sent(host).Should().HaveCount(6);
        host.Transport.EditCalls.Should().Be(0);
        (await OutboxAsync(host)).Select(o => o.Kind).Should().BeEquivalentTo(
            VolleyballNotificationPlanner.ReminderKind(Start), "started", "set:1", "set:2", "set:3", "final");
    }

    [Fact]
    public async Task Turkey_losing_a_set_and_the_match_uses_neutral_wording_from_the_away_side()
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync();
        await using var _ = host;
        var match = VbFakeProvider.Scheduled("m1", Start, VbFakeProvider.Opponent("SRB", "Serbia"), VbFakeProvider.Turkey());
        world.Set(match);
        await host.SetUpVbGuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(62));
        world.Set(match with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 });
        await RunAsync(host, TimeSpan.FromMinutes(2));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 18)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        var set = Sent(host).Last();
        set.Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 0-1 Sırbistan 🇷🇸", "the followed team is always shown first");
        set.Message.Embed.Description.Should().Contain("1. seti **Sırbistan** aldı").And.Contain("1. Set: 18-25").And.NotContain("TÜRKİYE'NİN");

        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 18), (25, 20), (25, 22)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        var final = Sent(host).Last();
        final.Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 0-3 Sırbistan 🇷🇸");
        final.Message.Embed.Description.Should().Contain("Maç sona erdi").And.Contain("Sırbistan").And.NotContain("kazandı!");
        final.Message.Embed.Color.Should().Be(VolleyballNotificationRenderer.NeutralResultColor);
    }

    [Fact]
    public async Task First_boot_during_a_match_at_2_1_sends_no_history_and_only_later_transitions()
    {
        var (host, world, match) = await MatchDayAsync();
        await using var _ = host;
        host.Clock.Advance(TimeSpan.FromMinutes(120));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (22, 25), (25, 19)));
        await RunAsync(host, TimeSpan.FromMinutes(5));
        Sent(host).Should().BeEmpty("no reminder, started or set 1/2/3 replay after the module first sees the match at 2-1");
        (await SnapshotAsync(host)).IsBaseline.Should().BeTrue();

        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (22, 25), (25, 19), (25, 20)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        Sent(host).Should().ContainSingle().Which.Message.Embed!.Title.Should().Be("🇹🇷 Türkiye 3-1 İtalya 🇮🇹");
    }

    [Fact]
    public async Task Enabling_the_module_after_the_match_started_sends_no_started_card()
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync();
        await using var _ = host;
        var match = VbFakeProvider.Scheduled("m1", Start);
        world.Set(match);
        // Another guild keeps the module running so the match is watched continuously…
        await host.SetUpVbGuildAsync(new GuildId(1), new ChannelId(10));
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(61));
        world.Set(match with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 });
        await RunAsync(host, TimeSpan.FromMinutes(2));
        // …and our guild enables it only now (watermark = now).
        await host.SetUpVbGuildAsync(Guild, Channel);
        await RunAsync(host, TimeSpan.FromMinutes(3));
        host.Transport.Messages.Where(m => m.Channel == Channel).Should().BeEmpty("the start happened before this guild's watermark");
    }

    [Fact]
    public async Task Repeated_finished_responses_and_a_restarted_poller_announce_the_final_once()
    {
        var (host, world, match) = await MatchDayAsync(pingRole: false);
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(62));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21), (25, 21)));
        await RunAsync(host, TimeSpan.FromMinutes(2));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (25, 21), (25, 21)));
        for (var i = 0; i < 20; i++)
            await StepAsync(host, TimeSpan.FromMinutes(1));
        (await OutboxAsync(host, "final")).Should().ContainSingle();
        Sent(host).Count(m => m.Message.Embed!.Description!.Contains("kazandı", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Provider_failure_is_never_a_cancellation_and_sends_nothing()
    {
        var (host, world, match) = await MatchDayAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        world.Failure = VbProviderOutcome.Unavailable;
        await RunAsync(host, TimeSpan.FromMinutes(120));
        var snapshot = await SnapshotAsync(host);
        snapshot.Cancelled.Should().BeFalse();
        snapshot.Started.Should().BeFalse();
        Sent(host).Should().ContainSingle("only the reminder from the fresh schedule; nothing is inferred from the outage")
            .Which.Message.Embed!.Description.Should().Contain("⏳");
        host.Services.GetRequiredService<VolleyballCache>().Live.LastOutcome.Should().Be(VbProviderOutcome.Unavailable);
    }

    [Fact]
    public async Task Stale_fixture_data_sends_no_reminder()
    {
        var (host, world, match) = await MatchDayAsync(overrides: new() { ["Volleyball:FixtureStaleAfterHours"] = "2" });
        await using var _ = host;
        world.Set(match with { StartTimeUtc = T0.AddHours(4) });
        await StepAsync(host, TimeSpan.Zero);
        world.Failure = VbProviderOutcome.Timeout;
        await RunAsync(host, TimeSpan.FromHours(4));
        Sent(host).Should().BeEmpty("the start time is only known from data that went stale");
    }

    [Fact]
    public async Task A_same_day_time_change_edits_the_reminder_without_a_new_ping()
    {
        var (host, world, match) = await MatchDayAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(46));
        Sent(host).Should().ContainSingle();
        var later = Start.AddMinutes(30);
        world.Set(match with { StartTimeUtc = later });
        await RunAsync(host, TimeSpan.FromMinutes(61));
        Sent(host).Should().ContainSingle("a same-day time change never posts a second reminder");
        var edits = host.Transport.Messages.Single().Edits;
        edits.Should().NotBeEmpty();
        edits[^1].Embed!.Description.Should().Contain("<t:" + later.ToUnixTimeSeconds() + ":R>");
        edits[^1].Mentions.Roles.Should().BeEmpty("edits never ping");
    }

    [Fact]
    public async Task Postponed_repeated_is_one_card_and_the_new_date_gets_its_own_reminder()
    {
        var (host, world, match) = await MatchDayAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        world.Set(match with { Status = VolleyballMatchStatus.Postponed });
        await RunAsync(host, TimeSpan.FromMinutes(70));
        var postponed = Sent(host).Should().ContainSingle().Subject;
        postponed.Message.Embed!.Description.Should().Contain("ertelendi");
        postponed.Pinged.Should().BeFalse();

        var newDate = Start.AddDays(2);
        world.Set(match with { StartTimeUtc = newDate, Status = VolleyballMatchStatus.Scheduled });
        host.Clock.Advance(newDate - host.Clock.GetUtcNow() - TimeSpan.FromMinutes(20));
        await RunAsync(host, TimeSpan.FromMinutes(10));
        Sent(host).Should().HaveCount(2);
        Sent(host)[1].Message.Embed!.Description.Should().Contain("⏳");
        (await OutboxAsync(host, "postponed")).Should().ContainSingle();
    }

    [Fact]
    public async Task Cancelled_is_announced_once_without_a_ping()
    {
        var (host, world, match) = await MatchDayAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);
        world.Set(match with { Status = VolleyballMatchStatus.Cancelled });
        await RunAsync(host, TimeSpan.FromMinutes(35)); // seen when live polling starts (T-30), confirmed one poll later
        var card = Sent(host).Should().ContainSingle().Subject;
        card.Message.Embed!.Description.Should().Contain("iptal");
        card.Pinged.Should().BeFalse();
        await RunAsync(host, TimeSpan.FromMinutes(60));
        Sent(host).Should().ContainSingle("no reminder for a cancelled match and no second cancelled card");
    }

    [Fact]
    public async Task Youth_men_and_club_matches_from_the_provider_never_produce_a_card()
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync();
        await using var _ = host;
        world.Set(VbFakeProvider.Scheduled("u19", Start, VbFakeProvider.Turkey(level: TeamLevel.AgeGroup, name: "Türkiye U19"), VbFakeProvider.Opponent()));
        world.Set(VbFakeProvider.Scheduled("men", Start, VbFakeProvider.Turkey(gender: TeamGender.Men), VbFakeProvider.Opponent(gender: TeamGender.Men)));
        world.Set(VbFakeProvider.Scheduled("club", Start, new VolleyballTeam("c", "c", "VakıfBank", "TUR", TeamGender.Women, TeamLevel.Senior, TeamKind.Club), VbFakeProvider.Opponent()));
        world.Set(VbFakeProvider.Scheduled("other", Start, VbFakeProvider.Opponent(), VbFakeProvider.Opponent("BRA", "Brazil")));
        await host.SetUpVbGuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(70));
        Sent(host).Should().BeEmpty();
        (await host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<VbMatchSnapshotEntity>().CountAsync())).Should().Be(0, "rejected matches are not even stored");
    }

    [Fact]
    public async Task Ambiguous_identity_is_rejected_and_reported_to_doctor()
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync();
        await using var _ = host;
        world.Set(VbFakeProvider.Scheduled("amb", Start, new VolleyballTeam("x", "x", "Türkiye", "TUR", TeamGender.Unknown, TeamLevel.Senior, TeamKind.NationalTeam), VbFakeProvider.Opponent()));
        await host.SetUpVbGuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(70));
        Sent(host).Should().BeEmpty();
        host.Services.GetRequiredService<VolleyballCache>().AmbiguousRejected.Should().Be(1);
        var (_, checks) = await host.InScopeAsync(sp => sp.GetRequiredService<VolleyballDoctor>().RunAsync(TestHost.Admin(Guild), CancellationToken.None));
        checks.Should().Contain(c => c.LabelKey == "vb.doctor.identity" && c.State == VbCheckState.Warning);
    }

    [Fact]
    public async Task Ten_guilds_share_one_provider_fetch_and_each_gets_exactly_one_reminder()
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync();
        await using var _ = host;
        world.Set(VbFakeProvider.Scheduled("m1", Start));
        for (var g = 1; g <= 10; g++)
            await host.SetUpVbGuildAsync(new GuildId((ulong)(1000 + g)), new ChannelId((ulong)(10000 + g)));
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(50));
        host.Transport.Messages.Should().HaveCount(10);
        host.Transport.Messages.Select(m => m.Channel).Distinct().Should().HaveCount(10);

        var (single, singleWorld) = await VbTestHostExtensions.CreateVbHostAsync();
        await using var __ = single;
        singleWorld.Set(VbFakeProvider.Scheduled("m1", Start));
        await single.SetUpVbGuildAsync(Guild, Channel);
        await StepAsync(single, TimeSpan.Zero);
        await RunAsync(single, TimeSpan.FromMinutes(50));
        (world.FixtureCalls, world.LiveCalls).Should().Be((singleWorld.FixtureCalls, singleWorld.LiveCalls), "provider calls do not depend on the number of servers");
    }

    [Fact]
    public async Task No_provider_call_while_no_guild_has_the_module_enabled()
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync();
        await using var _ = host;
        world.Set(VbFakeProvider.Scheduled("m1", Start));
        await RunAsync(host, TimeSpan.FromMinutes(90));
        (world.FixtureCalls + world.LiveCalls).Should().Be(0);
    }

    [Fact]
    public async Task Live_polling_happens_only_in_the_match_window_and_at_the_configured_cadence()
    {
        var (host, world) = await VbTestHostExtensions.CreateVbHostAsync();
        await using var _ = host;
        world.Set(VbFakeProvider.Scheduled("m1", T0.AddHours(10)));
        await host.SetUpVbGuildAsync(Guild, Channel);
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromHours(9));
        world.LiveCalls.Should().Be(0, "no live polling hours before the match");
        world.FixtureCalls.Should().BeLessThanOrEqualTo(11, "hourly fixture refresh when a match is within 48 h");
        await RunAsync(host, TimeSpan.FromMinutes(60));
        world.LiveCalls.Should().BeInRange(25, 31, "one poll per minute from 30 minutes before the start");
    }

    [Fact]
    public async Task Disabled_notification_types_and_pause_send_nothing_and_resume_does_not_replay()
    {
        var (host, world, match) = await MatchDayAsync();
        await using var _ = host;
        await host.InScopeAsync(async sp => (await sp.GetRequiredService<VolleyballConfigService>().SetNotificationsAsync(TestHost.Admin(Guild),
            new VbNotificationChanges(Reminder: false, Sets: false), CancellationToken.None)).Succeeded.Should().BeTrue());
        await StepAsync(host, TimeSpan.Zero);
        await RunAsync(host, TimeSpan.FromMinutes(62));
        world.Set(match with { Status = VolleyballMatchStatus.Live, HomeSets = 0, AwaySets = 0 });
        await RunAsync(host, TimeSpan.FromMinutes(2));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Live, (25, 21)));
        await RunAsync(host, TimeSpan.FromMinutes(2));
        Sent(host).Should().ContainSingle("only 'started' is enabled").Which.Message.Embed!.Description.Should().Contain("Maç başladı");

        await host.InScopeAsync(async sp => await sp.GetRequiredService<VolleyballConfigService>().PauseAsync(TestHost.Admin(Guild), true, CancellationToken.None));
        world.Set(VbFakeProvider.WithSets(match, VolleyballMatchStatus.Finished, (25, 21), (25, 21), (25, 21)));
        await RunAsync(host, TimeSpan.FromMinutes(3));
        await host.InScopeAsync(async sp => await sp.GetRequiredService<VolleyballConfigService>().PauseAsync(TestHost.Admin(Guild), false, CancellationToken.None));
        await RunAsync(host, TimeSpan.FromMinutes(5));
        Sent(host).Should().ContainSingle("the final happened while paused and is not replayed after resume");
    }

    [Fact]
    public async Task Health_and_doctor_report_provider_state_without_secrets()
    {
        var (host, world, _) = await MatchDayAsync(overrides: new() { ["Volleyball:Fivb:AppId"] = "top-secret-app-id" });
        await using var __ = host;
        await StepAsync(host, TimeSpan.Zero);
        var report = await host.Services.GetServices<ToroSquad.Core.Modules.IModuleHealthCheck>().Single(h => h.Module.Value == "volleyball").CheckAsync(CancellationToken.None);
        report.Entries.Should().Contain(e => e.Component == "vb.health.fixtures" && e.State == ToroSquad.Core.Modules.HealthState.Healthy);
        report.Entries.Should().Contain(e => e.Component == "vb.health.next");
        var (auth, checks) = await host.InScopeAsync(sp => sp.GetRequiredService<VolleyballDoctor>().RunAsync(TestHost.Admin(Guild), CancellationToken.None));
        auth.Succeeded.Should().BeTrue();
        string.Join(" ", checks.SelectMany(c => c.Args.Select(a => a?.ToString()))).Should().NotContain("top-secret-app-id");
        checks.Should().Contain(c => c.LabelKey == "vb.doctor.fixtures" && c.State == VbCheckState.Ok);
        var (denied, none) = await host.InScopeAsync(sp => sp.GetRequiredService<VolleyballDoctor>().RunAsync(TestHost.Member(Guild), CancellationToken.None));
        denied.Succeeded.Should().BeFalse();
        none.Should().BeEmpty();
        world.Should().NotBeNull();
    }
}
