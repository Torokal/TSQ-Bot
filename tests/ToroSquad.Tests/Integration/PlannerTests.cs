using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;

namespace ToroSquad.Tests.Integration;

/// <summary>Planner policies on a real SQLite DB: criteria 3, 6, 9 and the backlog/catch-up rules.</summary>
public sealed class PlannerTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(333);
    private static readonly ChannelId Channel = new(3330);
    private static readonly RoleId TeamRole = new(3331);
    private static readonly TeamRef Alpha = new("liquipedia", "counterstrike/Alpha", "Alpha", "ALP");
    private static readonly TeamRef Bravo = new("liquipedia", "counterstrike/Bravo", "Bravo", "BRV");

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        await _host.SetUpEsportsGuildAsync(Guild, Channel, new RoleInfo(TeamRole, "Alpha fans", 3, GuildPermission.None, false, false, true));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static EsportsMatch Upcoming(string id, DateTimeOffset start) =>
        FilterAndRankingTests.Match(id, Alpha, Bravo) with { ScheduledStartUtc = start };

    private static EsportsMatch Finished(string id, DateTimeOffset start, int a = 2, int b = 1) =>
        FilterAndRankingTests.Match(id, Alpha, Bravo) with
        {
            ScheduledStartUtc = start,
            Status = MatchStatus.Finished,
            A = new MatchOpponent(OpponentKind.Team, Alpha, a, OpponentResult.Scored),
            B = new MatchOpponent(OpponentKind.Team, Bravo, b, OpponentResult.Scored),
            WinnerIndex = a > b ? 0 : 1,
        };

    private Task<PlanReport> PlanAsync(IReadOnlyList<EsportsMatch> matches, bool afterGap = false) =>
        _host.InScopeAsync(sp => sp.GetRequiredService<NotificationPlanner>().PlanAsync(matches, _host.Clock.GetUtcNow(), afterGap, CancellationToken.None));

    private Task<List<OutboxMessageEntity>> OutboxAsync() =>
        _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    /// <summary>First ever run records a baseline; use a dummy run so tests exercise steady state.</summary>
    private Task BootstrapAsync() => PlanAsync([Upcoming("BOOT", TestHost.T0.AddDays(5))]);

    [Fact]
    public async Task First_run_treats_already_finished_matches_as_silent_baseline()
    {
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await PlanAsync([Finished("OLD1", TestHost.T0.AddHours(-3)), Finished("OLD2", TestHost.T0.AddHours(-2))]);
        (await OutboxAsync()).Should().BeEmpty("no backlog flood on first connection");
    }

    [Fact]
    public async Task Reminder_is_created_once_across_repeated_polls_and_restarts()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var match = Upcoming("R1", _host.Clock.GetUtcNow().AddMinutes(20)); // lead is 30 min → due now
        await PlanAsync([match]);
        await PlanAsync([match]);

        // "Restart": a fresh cache + new scopes; state comes only from the database.
        _host.Services.GetRequiredService<EsportsCache>().Restore(null, null, null, null, null, []);
        await PlanAsync([match]);

        var rows = await OutboxAsync();
        rows.Should().ContainSingle(r => r.Kind == NotificationPlanner.KindReminder);
    }

    [Fact]
    public async Task Two_followed_teams_in_one_match_produce_one_message_with_combined_pings()
    {
        await BootstrapAsync();
        await _host.InScopeAsync(async sp =>
        {
            var roles = sp.GetRequiredService<RoleMappingService>();
            await roles.MapAsync(TestHost.Admin(Guild), TeamRole, "counterstrike/Alpha", true, false, CancellationToken.None);
        });
        _host.Guilds.SetSnapshot(Discord.Guilds.FakeGuildGateway.DemoSnapshot(Guild,
            new RoleInfo(TeamRole, "Alpha fans", 3, GuildPermission.None, false, false, true),
            new RoleInfo(new RoleId(3332), "Bravo fans", 3, GuildPermission.None, false, false, true)));
        await _host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>().MapAsync(TestHost.Admin(Guild), new RoleId(3332), "counterstrike/Bravo", true, false, CancellationToken.None));

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await PlanAsync([Upcoming("R2", _host.Clock.GetUtcNow().AddMinutes(10))]);
        var row = (await OutboxAsync()).Should().ContainSingle().Subject;
        var payload = PayloadSerializer.Deserialize(row.PayloadJson);
        payload.Mentions.Roles.Select(r => r.Value).Should().Equal(3331UL, 3332UL);
    }

    [Fact]
    public async Task Passing_the_planned_start_is_not_live_evidence_and_creates_nothing_new()
    {
        await BootstrapAsync();
        var start = TestHost.T0.AddHours(2);
        var match = Upcoming("L1", start);
        await PlanAsync([match]);
        (await OutboxAsync()).Should().BeEmpty("reminder not due yet");

        _host.Clock.Advance(TimeSpan.FromHours(3)); // way past start (+grace), still finished=0 at the source
        var report = await PlanAsync([match]);
        report.Created.Should().Be(0);
        (await OutboxAsync()).Should().BeEmpty("a missed reminder window is not replayed, and no 'live' message exists");

        var renderer = _host.Services.GetRequiredService<NotificationRenderer>();
        renderer.MatchLine(match, "en", false, _host.Clock.GetUtcNow()).Should().Contain("awaiting result").And.NotContain("live");
    }

    [Fact]
    public async Task Start_time_change_edits_the_existing_reminder_and_says_only_that_the_time_changed()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var start = _host.Clock.GetUtcNow().AddMinutes(20);
        await PlanAsync([Upcoming("T1", start)]);
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await PlanAsync([Upcoming("T1", start.AddMinutes(45))]);
        var row = (await OutboxAsync()).Single();
        row.EditPending.Should().BeTrue();
        var payload = PayloadSerializer.Deserialize(row.PayloadJson);
        payload.Embed!.Description.Should().Contain("Başlangıç saati güncellendi").And.NotContainAny("ertelendi", "iptal");
    }

    [Fact]
    public async Task New_result_is_announced_and_later_corrections_edit_it()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await PlanAsync([Finished("F1", _host.Clock.GetUtcNow().AddHours(-2), 2, 1)]);
        var rows = await OutboxAsync();
        rows.Should().ContainSingle(r => r.Kind == NotificationPlanner.KindResult);
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

        _host.Clock.Advance(TimeSpan.FromMinutes(10));
        await PlanAsync([Finished("F1", _host.Clock.GetUtcNow().AddHours(-2), 2, 0)]); // score corrected at the source
        var row = (await OutboxAsync()).Single();
        row.Status.Should().Be(OutboxStatus.Sent);
        row.EditPending.Should().BeTrue("corrections edit the same message");
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);
        _host.Transport.SendCalls.Should().Be(1);
        _host.Transport.EditCalls.Should().Be(1);
    }

    [Fact]
    public async Task Unchanged_data_on_later_polls_causes_no_edits()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var match = Finished("F2", _host.Clock.GetUtcNow().AddHours(-1));
        await PlanAsync([match]);
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            _host.Clock.Advance(TimeSpan.FromMinutes(10));
            await PlanAsync([match]);
        }

        (await OutboxAsync()).Single().EditPending.Should().BeFalse();
    }

    [Fact]
    public async Task Catch_up_after_a_gap_is_limited_in_age_and_count()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromHours(10));
        var now = _host.Clock.GetUtcNow();
        var matches = Enumerable.Range(1, 8).Select(i => Finished($"G{i}", now.AddHours(-1).AddMinutes(-i))).ToList();
        matches.Add(Finished("TOO_OLD", now.AddHours(-9)));
        await PlanAsync(matches, afterGap: true);

        var rows = await OutboxAsync();
        rows.Count(r => r.ExpiresAt > now).Should().Be(5, "MaxCatchUpPerGuildPerPoll");
        rows.Should().NotContain(r => r.SourceKey.EndsWith("TOO_OLD", StringComparison.Ordinal));

        // The overflow is recorded as never-to-be-sent, so the next normal poll does not deliver the rest of the backlog.
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);
        _host.Clock.Advance(TimeSpan.FromMinutes(10));
        await PlanAsync(matches, afterGap: false);
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);
        _host.Transport.SendCalls.Should().Be(5);
    }

    [Fact]
    public async Task Changing_the_channel_or_re_enabling_results_does_not_re_announce_recent_results()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var match = Finished("CH1", _host.Clock.GetUtcNow().AddHours(-1));
        await PlanAsync([match]);
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);
        _host.Transport.SendCalls.Should().Be(1);

        var newChannel = new ChannelId(3339);
        _host.Guilds.SetChannel(Guild, newChannel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks));
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await _host.InScopeAsync(sp => sp.GetRequiredService<EsportsConfigService>().ConfigureAsync(TestHost.Admin(Guild), newChannel.Value, null, null, null, null, CancellationToken.None));
        await PlanAsync([match]);
        await _host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<EsportsConfigService>();
            await config.ConfigureAsync(TestHost.Admin(Guild), null, null, null, false, null, CancellationToken.None);
            _host.Clock.Advance(TimeSpan.FromMinutes(1));
            await config.ConfigureAsync(TestHost.Admin(Guild), null, null, null, true, null, CancellationToken.None);
        });
        await PlanAsync([match]);
        await _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);
        _host.Transport.SendCalls.Should().Be(1, "no re-announcement in the new channel or after toggling results");
    }

    [Fact]
    public async Task Correction_dropped_while_paused_is_applied_after_resume()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await PlanAsync([Finished("PZ", _host.Clock.GetUtcNow().AddHours(-1), 2, 1)]);
        var processor = _host.Services.GetRequiredService<OutboxProcessor>();
        await processor.ProcessOnceAsync(CancellationToken.None);

        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await PlanAsync([Finished("PZ", _host.Clock.GetUtcNow().AddHours(-1), 2, 0)]); // correction staged
        await _host.InScopeAsync(sp => sp.GetRequiredService<EsportsConfigService>().PauseAsync(TestHost.Admin(Guild), true, CancellationToken.None));
        await processor.ProcessOnceAsync(CancellationToken.None); // edit dropped while paused
        _host.Transport.EditCalls.Should().Be(0);

        await _host.InScopeAsync(sp => sp.GetRequiredService<EsportsConfigService>().PauseAsync(TestHost.Admin(Guild), false, CancellationToken.None));
        _host.Clock.Advance(TimeSpan.FromMinutes(10));
        await PlanAsync([Finished("PZ", _host.Clock.GetUtcNow().AddHours(-1), 2, 0)]);
        await processor.ProcessOnceAsync(CancellationToken.None);
        _host.Transport.EditCalls.Should().Be(1, "the visible message still shows the old score, so the correction is re-staged");
        _host.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task Results_that_finished_before_the_guild_enabled_esports_are_never_sent()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await _host.InScopeAsync(sp => sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "esports", false, CancellationToken.None));
        await PlanAsync([Finished("W1", _host.Clock.GetUtcNow().AddHours(-1))]); // observed while disabled
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await _host.InScopeAsync(sp => sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "esports", true, CancellationToken.None));
        await PlanAsync([Finished("W1", _host.Clock.GetUtcNow().AddHours(-1))]);
        (await OutboxAsync()).Should().BeEmpty("watermark moved on re-enable");
    }

    [Fact]
    public async Task Disabled_or_paused_guilds_get_no_planned_notifications()
    {
        await BootstrapAsync();
        await _host.InScopeAsync(sp => sp.GetRequiredService<EsportsConfigService>().PauseAsync(TestHost.Admin(Guild), true, CancellationToken.None));
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await PlanAsync([Upcoming("P1", _host.Clock.GetUtcNow().AddMinutes(10))])).GuildsConsidered.Should().Be(0);
        (await OutboxAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Stale_data_never_creates_notifications()
    {
        await BootstrapAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var fetchedAt = _host.Clock.GetUtcNow().AddHours(-2);
        var report = await _host.InScopeAsync(sp => sp.GetRequiredService<NotificationPlanner>()
            .PlanAsync([Upcoming("S1", _host.Clock.GetUtcNow().AddMinutes(10))], fetchedAt, false, CancellationToken.None));
        report.SkippedStale.Should().BeTrue();
        (await OutboxAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Vrs_filter_without_rankings_blocks_and_is_counted()
    {
        await BootstrapAsync();
        await _host.InScopeAsync(sp => sp.GetRequiredService<EsportsConfigService>().SetVrsTopNAsync(TestHost.Admin(Guild), 20, CancellationToken.None));
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var report = await PlanAsync([Upcoming("V1", _host.Clock.GetUtcNow().AddMinutes(10))]);
        report.BlockedMissingData.Should().Be(1);
        (await OutboxAsync()).Should().BeEmpty();
    }
}
