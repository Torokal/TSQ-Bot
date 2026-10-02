using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Notifications;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The automatic weekly leaderboard on the real SQLite database, the real outbox and the fake Discord transport with a fake
/// clock (default Sunday 20:00 Europe/Istanbul = 17:00 UTC): one post per guild and week in the commands channel, never
/// pinging, from the CURRENT active tournament with the same boards as /ongoru liderlik (Top 10, eligibility), restart- and
/// race-safe, late by at most the catch-up window, nothing for an empty board, nothing while disabled.
/// </summary>
public sealed class PredictionWeeklyLeaderboardTests : IAsyncLifetime
{
    private const string WeeklyTitle = "🏆 TSQ Öngörü · Haftalık Liderlik";

    /// <summary>Sunday 4 October 2026, 20:00 in Türkiye.</summary>
    private static readonly DateTimeOffset Slot = new(2026, 10, 4, 17, 0, 0, TimeSpan.Zero);

    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await CreateAtAsync(Slot - TimeSpan.FromHours(2));

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    private static Task<PredictionTestKit> CreateAtAsync(DateTimeOffset start, Dictionary<string, string?>? settings = null, string? directory = null,
        ToroSquad.Discord.Transport.FakeMessageTransport? transport = null) =>
        PredictionTestKit.CreateAsync(start, settings, directory, transport);

    private List<OutgoingMessage> Weekly() =>
        _kit.Transport.Messages.Where(m => m.Channel == Commands && m.Message.Embed?.Title == WeeklyTitle).Select(m => m.Message).ToList();

    private Task<List<PredictionWeeklyBoardEntity>> WeeksAsync() => _kit.Db(db => db.Set<PredictionWeeklyBoardEntity>().AsNoTracking().OrderBy(w => w.Id).ToListAsync());

    private Task<int> WeeklyOutboxAsync() => _kit.Db(db => db.Outbox.CountAsync(o => o.Kind == PredictionEconomy.KindWeeklyLeaderboard));

    private async Task<long> TournamentAsync()
    {
        await _kit.OpenFormAsync(); // makes sure the tournament exists
        return await _kit.Db(db => db.Set<PredictionTournamentEntity>().Where(t => t.Status == PredictionTournamentStatus.Active).Select(t => t.Id).SingleAsync());
    }

    /// <summary>Eligible members (one settled entry each, as the settlement leaves the wallet) with chosen coins, in the active tournament.</summary>
    private async Task SeedAsync(params (ulong User, long Coins)[] members)
    {
        var tournament = await TournamentAsync();
        await _kit.Db(async db =>
        {
            foreach (var (user, coins) in members)
            {
                db.Add(new PredictionWalletEntity
                {
                    TournamentId = tournament,
                    GuildId = Guild.Value,
                    UserId = user,
                    BalanceMinor = coins * 100,
                    SettledCount = 1,
                    CreatedAt = TestHost.T0,
                    UpdatedAt = TestHost.T0,
                });
            }

            return await db.SaveChangesAsync();
        });
    }

    private static List<string> Order(string field) => field.Split('\n').Select(l => l.Split(' ')[1]).ToList();

    [Fact]
    public async Task Nothing_at_19_59_one_unpinged_post_at_20_00_and_no_second_one_that_week()
    {
        await SeedAsync((1001, 1200), (1002, 900), (1003, 1500));
        _kit.Host.Clock.SetUtcNow(Slot - TimeSpan.FromMinutes(1));
        await _kit.TickAsync();
        Weekly().Should().BeEmpty("19:59 in Türkiye");
        (await WeeksAsync()).Should().BeEmpty();

        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        var post = _kit.Transport.Messages.Should().ContainSingle(m => m.Message.Embed != null && m.Message.Embed.Title == WeeklyTitle).Subject;
        (post.Channel, post.Pinged, post.Message.Mentions.PingsAnything).Should().Be((Commands, false, false));
        var embed = post.Message.Embed!;
        embed.Description.Should().Contain("Güncel aktif turnuva sıralaması (Turnuva 1)");
        embed.Fields.Select(f => f.Name).Should().Equal("💰 En Çok TSQ Coin", "🎯 En Çok Doğru Tahmin");
        Order(embed.Fields[0].Value).Should().Equal("<@1003>", "<@1001>", "<@1002>");
        embed.Fields[0].Value.Should().StartWith("🥇 <@1003>");
        (await WeeksAsync()).Single().Should().Match<PredictionWeeklyBoardEntity>(w =>
            w.Status == WeeklyBoardStatus.Staged && w.WeekKey == 202640 && w.ScheduledAt == Slot && w.Participants == 3);

        for (var i = 0; i < 6; i++)
            await _kit.TickAsync(TimeSpan.FromHours(1));
        Weekly().Should().HaveCount(1);
        (await WeeklyOutboxAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Two_workers_at_the_same_moment_stage_one_post()
    {
        await SeedAsync((1001, 1000));
        _kit.Host.Clock.SetUtcNow(Slot);
        var staged = await _kit.TogetherAsync(
            () => _kit.Economy(e => e.PublishWeeklyLeaderboardAsync(Ct)),
            () => _kit.Economy(e => e.PublishWeeklyLeaderboardAsync(Ct)),
            () => _kit.Economy(e => e.PublishWeeklyLeaderboardAsync(Ct)));
        staged.Sum().Should().Be(1);
        (await WeeklyOutboxAsync()).Should().Be(1);
        (await WeeksAsync()).Should().ContainSingle();
        await _kit.TickAsync();
        Weekly().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_restart_right_after_the_post_or_across_20_00_posts_once()
    {
        await SeedAsync((1001, 1000));
        _kit.Host.Clock.SetUtcNow(Slot - TimeSpan.FromMinutes(1));
        await _kit.TickAsync(); // 19:59, then the bot goes down
        await using (var back = await CreateAtAsync(Slot + TimeSpan.FromMinutes(1), directory: _kit.Host.Directory, transport: _kit.Transport))
        {
            await back.TickAsync(); // 20:01
            await using var again = await CreateAtAsync(Slot + TimeSpan.FromMinutes(2), directory: _kit.Host.Directory, transport: _kit.Transport);
            await again.TickAsync();
        }

        Weekly().Should().HaveCount(1);
        (await WeeksAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_bot_back_at_23_00_catches_up_once()
    {
        await SeedAsync((1001, 1000));
        _kit.Host.Clock.SetUtcNow(Slot + TimeSpan.FromHours(3)); // 23:00 in Türkiye
        await _kit.TickAsync();
        await _kit.TickAsync(TimeSpan.FromMinutes(5));
        Weekly().Should().HaveCount(1, "inside the 12 h catch-up");
    }

    [Fact]
    public async Task A_bot_back_on_tuesday_does_not_post_last_sunday()
    {
        await using var late = await CreateAtAsync(Slot + TimeSpan.FromHours(37)); // Tuesday 6 October, 09:00 in Türkiye
        await late.ParticipateAsync(1001);
        await late.TickAsync();
        late.Transport.Messages.Should().NotContain(m => m.Message.Embed != null && m.Message.Embed.Title == WeeklyTitle);
        (await late.Db(db => db.Set<PredictionWeeklyBoardEntity>().CountAsync())).Should().Be(0, "last Sunday is outside the catch-up window");
    }

    [Fact]
    public async Task Outside_the_catch_up_window_nothing_is_posted_and_the_next_sunday_posts_once()
    {
        await SeedAsync((1001, 1000));
        _kit.Host.Clock.SetUtcNow(Slot + TimeSpan.FromHours(12)); // Monday 08:00 in Türkiye: the window [20:00, 08:00) is over
        await _kit.TickAsync();
        Weekly().Should().BeEmpty();
        (await WeeksAsync()).Should().BeEmpty("no old week is posted late");

        _kit.Host.Clock.SetUtcNow(Slot + TimeSpan.FromDays(7));
        await _kit.TickAsync();
        Weekly().Should().HaveCount(1);
        (await WeeksAsync()).Single().WeekKey.Should().Be(202641);
    }

    [Fact]
    public async Task The_catch_up_window_follows_its_setting()
    {
        await using var kit = await CreateAtAsync(Slot + TimeSpan.FromHours(2.5), new Dictionary<string, string?> { ["Predictions:WeeklyLeaderboard:CatchUpHours"] = "2" });
        await kit.ParticipateAsync(1001);
        await kit.TickAsync();
        (await kit.Db(db => db.Set<PredictionWeeklyBoardEntity>().CountAsync())).Should().Be(0, "2.5 h late with a 2 h window");
    }

    [Fact]
    public async Task An_empty_board_posts_nothing_marks_the_week_and_a_later_player_that_week_gets_no_late_post()
    {
        await TournamentAsync();
        await _kit.Economy(e => e.ClaimDailyAsync(Member(500), Commands, "Üye 500", Ct)); // daily only: not eligible
        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        Weekly().Should().BeEmpty();
        (await WeeksAsync()).Single().Should().Match<PredictionWeeklyBoardEntity>(w => w.Status == WeeklyBoardStatus.SkippedNoParticipants && w.Participants == 0);

        await SeedAsync((1001, 1000)); // someone plays on Monday
        await _kit.TickAsync(TimeSpan.FromHours(10));
        Weekly().Should().BeEmpty();
        (await WeeklyOutboxAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(15)]
    public async Task The_post_lists_the_eligible_members_top_10_per_board(int eligible)
    {
        await SeedAsync([.. Enumerable.Range(1, eligible).Select(i => ((ulong)(2000 + i), 3000L - i))]);
        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        var embed = Weekly().Single().Embed!;
        var expected = Enumerable.Range(1, Math.Min(eligible, PredictionRules.LeaderboardSize)).Select(i => "<@" + (2000 + i) + ">").ToList();
        Order(embed.Fields[0].Value).Should().Equal(expected);
        Order(embed.Fields[1].Value).Should().HaveCount(expected.Count);
    }

    [Fact]
    public async Task The_weekly_post_has_the_manual_boards_rows_with_the_same_eligibility_and_live_wealth()
    {
        await _kit.ParticipateAsync(303, 304); // settled entries (each lost 1 coin): eligible, 999
        var open = await _kit.CreatePredictionAsync(title: "Açık öngörü");
        (await _kit.EnterAsync(Member(301), open, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.WithdrawAsync(Member(301), open)).Result.Succeeded.Should().BeTrue(); // withdrawn only: not listed
        await _kit.Economy(e => e.ClaimDailyAsync(Member(302), Commands, "Üye 302", Ct)); // daily only: not listed
        (await _kit.EnterAsync(Member(303), open, 1, "500")).Result.Succeeded.Should().BeTrue(); // 499 available + 500 open = 999 live

        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        var weekly = Weekly().Single().Embed!;
        var manual = (await _kit.Economy(e => e.LeaderboardAsync(Member(99), Commands, Ct))).View!.Embed!;
        weekly.Fields.Select(f => f.Value).Should().Equal(manual.Fields.Select(f => f.Value), "the same query, order and lines");
        weekly.Fields[0].Value.Should().Be("🥇 <@303> — **999 TSQ Coin**\n🥈 <@304> — **999 TSQ Coin**");
        weekly.Fields[0].Value.Should().NotContain("<@301>").And.NotContain("<@302>").And.NotContain("<@10>");
    }

    [Fact]
    public async Task The_weekly_post_is_the_public_board_only_no_personal_rank_messages()
    {
        await SeedAsync([.. Enumerable.Range(1, 15).Select(i => ((ulong)(2000 + i), 3000L - i))]);
        _kit.Host.Clock.SetUtcNow(Slot);
        var sends = _kit.Transport.SendCalls;
        await _kit.TickAsync();
        _kit.Transport.SendCalls.Should().Be(sends + 1, "one public card, no message per member outside the Top 10");
        Weekly().Single().Embed!.Fields.Should().HaveCount(2);
        (await _kit.Db(db => db.Outbox.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task The_manual_leaderboard_and_the_weekly_post_are_independent()
    {
        await SeedAsync((1001, 1000));
        (await _kit.Economy(e => e.LeaderboardAsync(Member(99), Commands, Ct))).Result.Succeeded.Should().BeTrue();
        (await WeeksAsync()).Should().BeEmpty("a manual board is not the weekly post");

        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        Weekly().Should().HaveCount(1);
        var manual = await _kit.Economy(e => e.LeaderboardAsync(Member(99), Commands, Ct));
        (manual.Result.Succeeded, manual.Public).Should().Be((true, true));
        Weekly().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_tournament_ending_just_before_20_00_gives_one_post_from_the_new_active_tournament()
    {
        await _kit.ParticipateAsync(1001, 1002); // real entries, settled: tournament 1 has something to end
        _kit.Host.Clock.SetUtcNow(Slot - TimeSpan.FromMinutes(2));
        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.Succeeded.Should().BeTrue();
        await SeedAsync((3001, 1000)); // plays in tournament 2 at 19:59

        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        var embed = Weekly().Should().ContainSingle().Subject.Embed!;
        embed.Description.Should().Contain("(Turnuva 2)");
        embed.Fields[0].Value.Should().Contain("<@3001>").And.NotContain("<@1001>");
        (await WeeksAsync()).Single().TournamentId.Should().Be(await TournamentAsync());
    }

    [Fact]
    public async Task Disabled_module_posts_nothing_and_enabling_it_inside_the_window_posts_once()
    {
        await SeedAsync((1001, 1000));
        await _kit.SetEnabledAsync(false);
        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        Weekly().Should().BeEmpty();
        (await WeeksAsync()).Should().BeEmpty();

        await _kit.SetEnabledAsync(true);
        await _kit.TickAsync(TimeSpan.FromHours(1));
        Weekly().Should().HaveCount(1);
    }

    [Fact]
    public async Task Weekly_leaderboard_disabled_by_setting_posts_nothing()
    {
        await using var kit = await CreateAtAsync(Slot, new Dictionary<string, string?> { ["Predictions:WeeklyLeaderboard:Enabled"] = "false" });
        await kit.ParticipateAsync(1001);
        await kit.TickAsync(TimeSpan.FromMinutes(1));
        kit.Transport.Messages.Should().NotContain(m => m.Message.Embed != null && m.Message.Embed.Title == WeeklyTitle);
        (await kit.Db(db => db.Set<PredictionWeeklyBoardEntity>().CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_transient_send_failure_is_retried_by_the_outbox_without_a_second_plan()
    {
        await SeedAsync((1001, 1000));
        _kit.Transport.ScriptSend(() => new SendOutcome.Transient("503"));
        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        Weekly().Should().BeEmpty();

        for (var i = 0; i < 6; i++)
            await _kit.TickAsync(TimeSpan.FromMinutes(5));
        Weekly().Should().HaveCount(1);
        ((await WeeklyOutboxAsync()), (await WeeksAsync()).Count).Should().Be((1, 1));
    }

    [Fact]
    public async Task A_channel_without_permission_fails_the_delivery_without_planning_it_again()
    {
        await SeedAsync((1001, 1000));
        _kit.Transport.ScriptSend(() => new SendOutcome.Permanent(PermanentFailureKind.MissingPermissions, "Missing Permissions"));
        _kit.Host.Clock.SetUtcNow(Slot);
        await _kit.TickAsync();
        for (var i = 0; i < 4; i++)
            await _kit.TickAsync(TimeSpan.FromHours(1));

        Weekly().Should().BeEmpty();
        (await _kit.Db(db => db.Outbox.Where(o => o.Kind == PredictionEconomy.KindWeeklyLeaderboard).Select(o => o.Status).ToListAsync())).Should().Equal(OutboxStatus.Failed);
        (await WeeksAsync()).Should().ContainSingle();
        ((await _kit.WalletAsync(1001))!.BalanceMinor).Should().Be(100_000, "no economic state is touched");
    }
}
