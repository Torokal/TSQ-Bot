using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The TSQ Öngörü economy on the real SQLite database: the starting balance (once), the daily reward (10–100, one per
/// Türkiye calendar day, atomic under parallel claims, not renewed by a tournament reset), the tournament cycle (blocked by
/// unresolved predictions, one close under simultaneous confirmations, stale confirmations, frozen podium, fresh starting
/// balances, archived old wallets, an announcement rendered from the snapshot and retried unchanged) and the leaderboards
/// (documented tie order, pending stakes at principal, one correct per prediction, tournament isolation, nothing created by
/// reading).
/// </summary>
public sealed class PredictionEconomyTests : IAsyncLifetime
{
    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await PredictionTestKit.CreateAsync();

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    private Task<PredictionReply> ClaimAsync(ulong user) => _kit.Economy(e => e.ClaimDailyAsync(Member(user), Commands, Ct));

    // ---- starting balance ----

    [Fact]
    public async Task The_starting_balance_is_granted_once_across_commands_restarts_and_rejoins()
    {
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 2, "10")).Result.Succeeded.Should().BeTrue();

        await using var second = await PredictionTestKit.CreateAsync(TestHost.T0.AddDays(1), directory: _kit.Host.Directory, transport: _kit.Transport);
        (await second.Economy(e => e.ClaimDailyAsync(Member(1), Commands, Ct))).Result.Succeeded.Should().BeTrue();
        (await second.LedgerAsync(1)).Count(l => l.Kind == PredictionLedgerKind.Initial).Should().Be(1);
        (await second.WalletAsync(1))!.BalanceMinor.Should().Be(100_000 + 1_000 - 1_000 + 1_000, "1000 + 10 daily − 10 staked + 10 daily");
    }

    // ---- daily ----

    [Fact]
    public async Task The_daily_reward_is_10_to_100_coins_credited_with_its_journal_row()
    {
        _kit.Random.Next.Enqueue(10);
        var first = await ClaimAsync(1);
        first.Result.MessageKey.Should().Be("predictions.daily.done");
        first.Result.Args.Take(2).Should().Equal("10", "1010");
        _kit.Random.Calls.Should().Equal((10, 100));

        _kit.Host.Clock.Advance(TimeSpan.FromDays(1));
        _kit.Random.Next.Enqueue(100);
        (await ClaimAsync(1)).Result.Args.Take(2).Should().Equal("100", "1110");
        (await _kit.LedgerAsync(1)).Select(l => (l.Kind, l.AmountMinor, l.BalanceAfterMinor)).Should().Equal(
            (PredictionLedgerKind.Initial, 100_000L, 100_000L), (PredictionLedgerKind.Daily, 1_000L, 101_000L), (PredictionLedgerKind.Daily, 10_000L, 111_000L));
    }

    [Fact]
    public async Task Two_simultaneous_claims_pay_once_and_the_repeat_shows_the_same_amount()
    {
        _kit.Random.Next.Enqueue(47);
        _kit.Random.Next.Enqueue(99);
        var results = await _kit.TogetherAsync(() => ClaimAsync(1), () => ClaimAsync(1));

        results.Count(r => r.Result.Succeeded).Should().Be(1);
        var repeat = results.Single(r => !r.Result.Succeeded).Result;
        (repeat.MessageKey, repeat.Args[0]).Should().Be(("predictions.daily.already", (object)"47"));
        _kit.Random.Calls.Should().ContainSingle("the second claim found the first one and drew nothing");
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(104_700);
        (await _kit.CountAsync<PredictionDailyClaimEntity>()).Should().Be(1);
        (await ClaimAsync(1)).Result.Args[0].Should().Be("47", "a retry never draws a new amount");
    }

    [Fact]
    public async Task A_new_claim_opens_at_midnight_turkiye_time_not_after_24_hours()
    {
        await using var kit = await PredictionTestKit.CreateAsync(new DateTimeOffset(2026, 9, 29, 20, 59, 0, TimeSpan.Zero)); // 23:59 in Türkiye
        (await kit.Economy(e => e.ClaimDailyAsync(Member(1), Commands, Ct))).Result.Succeeded.Should().BeTrue();
        kit.Host.Clock.Advance(TimeSpan.FromSeconds(59));
        var already = await kit.Economy(e => e.ClaimDailyAsync(Member(1), Commands, Ct));
        already.Result.MessageKey.Should().Be("predictions.daily.already");
        already.Result.Args[1].Should().Be(DiscordText.Timestamp(new DateTimeOffset(2026, 9, 29, 21, 0, 0, TimeSpan.Zero), 'R'));
        kit.Host.Clock.Advance(TimeSpan.FromSeconds(1)); // 00:00 in Türkiye, one minute after the first claim
        (await kit.Economy(e => e.ClaimDailyAsync(Member(1), Commands, Ct))).Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Ending_the_tournament_gives_no_second_daily_claim_the_same_day()
    {
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.Succeeded.Should().BeTrue();
        (await ClaimAsync(1)).Result.MessageKey.Should().Be("predictions.daily.already");
        (await _kit.WalletAsync(1)).Should().BeNull("the new tournament has no wallet for the member yet");
        var view = await _kit.Economy(e => e.WalletAsync(Member(1), Commands, Ct));
        view.View!.Embed!.Title.Should().Be("💰 TSQ Coin Cüzdanı · Turnuva 2");
        view.View.Embed.Fields[0].Value.Should().Be("1000 TSQ Coin");
    }

    // ---- tournament ----

    [Fact]
    public async Task Unresolved_predictions_block_ending_and_are_listed_with_their_links()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();

        var blocked = await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct));
        blocked.Result.MessageKey.Should().Be("predictions.tournament.unresolved");
        blocked.View!.Content.Should().StartWith("Turnuvayı bitirmeden önce sonuçlanmamış öngörüleri sonuçlandırmalı veya iptal etmelisiniz.")
            .And.Contain("#" + prediction.Id).And.Contain($"https://discord.com/channels/{Guild.Value}/{Predictions.Value}/{prediction.Message!.Value.Value}");

        (await _kit.SettleAsync(Admin(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        (await _kit.EndTokenAsync(Admin())).Should().NotBeNull();
    }

    [Fact]
    public async Task Only_administrators_or_the_owner_can_end_a_tournament()
    {
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        foreach (var actor in new[] { Creator(), Member(1), TestHost.Admin(Guild) }) // TestHost.Admin has Manage Server + Manage Roles, not Administrator
            (await _kit.Economy(e => e.PreviewTournamentEndAsync(actor, Commands, Ct))).Result.MessageKey.Should().Be("predictions.tournament.admin_only");
        (await _kit.EndTokenAsync(Owner())).Should().NotBeNull();
    }

    [Fact]
    public async Task A_prediction_published_after_the_preview_blocks_the_confirmation()
    {
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        var token = await _kit.EndTokenAsync(Admin());
        await _kit.CreatePredictionAsync();

        var result = await _kit.ConfirmEndAsync(Admin(), token!);
        result.Result.MessageKey.Should().Be("predictions.tournament.unresolved");
        (await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync())).Status.Should().Be(PredictionTournamentStatus.Active);
    }

    [Fact]
    public async Task Two_admins_confirming_together_close_the_tournament_once()
    {
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        var a = await _kit.EndTokenAsync(Admin());
        var b = await _kit.EndTokenAsync(Owner());
        var results = await _kit.TogetherAsync(() => _kit.ConfirmEndAsync(Admin(), a!), () => _kit.ConfirmEndAsync(Owner(), b!));

        results.Count(r => r.Result.Succeeded).Should().Be(1);
        results.Single(r => !r.Result.Succeeded).Result.MessageKey.Should().Be("predictions.tournament.stale");
        var tournaments = await _kit.Db(db => db.Set<PredictionTournamentEntity>().OrderBy(t => t.Number).ToListAsync());
        tournaments.Select(t => (t.Number, t.Status)).Should().Equal((1, PredictionTournamentStatus.Closed), (2, PredictionTournamentStatus.Active));
        (await _kit.Db(db => db.Outbox.CountAsync(o => o.ModuleId == "predictions"))).Should().Be(1, "one announcement");
    }

    [Fact]
    public async Task An_old_confirmation_never_closes_the_next_tournament()
    {
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        var old = await _kit.EndTokenAsync(Admin());
        (await _kit.ConfirmEndAsync(Owner(), (await _kit.EndTokenAsync(Owner()))!)).Result.Succeeded.Should().BeTrue();
        (await ClaimAsync(2)).Result.Succeeded.Should().BeTrue(); // tournament 2 has a participant (within the confirmation's lifetime)

        (await _kit.ConfirmEndAsync(Admin(), old!)).Result.MessageKey.Should().Be("predictions.tournament.stale");
        (await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync(t => t.Status == PredictionTournamentStatus.Active))).Number.Should().Be(2);
    }

    [Fact]
    public async Task An_end_confirmation_is_short_lived_single_use_and_bound_to_its_admin()
    {
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        var token = await _kit.EndTokenAsync(Admin());
        (await _kit.ConfirmEndAsync(Admin(21), token!)).Result.MessageKey.Should().Be("predictions.confirm.expired", "another admin cannot use it");
        _kit.Host.Clock.Advance(PredictionTokens.TournamentEndLifetime + TimeSpan.FromSeconds(1));
        (await _kit.ConfirmEndAsync(Admin(), token!)).Result.MessageKey.Should().Be("predictions.confirm.expired");
        (await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync())).Status.Should().Be(PredictionTournamentStatus.Active);

        var fresh = await _kit.EndTokenAsync(Admin());
        (await _kit.ConfirmEndAsync(Admin(), fresh!)).Result.Succeeded.Should().BeTrue();
        (await _kit.ConfirmEndAsync(Admin(), fresh!)).Result.MessageKey.Should().Be("predictions.confirm.expired", "single use");
    }

    [Fact]
    public async Task No_participants_means_no_needless_close_and_reopen()
    {
        await _kit.OpenFormAsync(); // creates tournament 1, nobody plays
        (await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct))).Result.MessageKey.Should().Be("predictions.tournament.no_participants");
        (await _kit.CountAsync<PredictionTournamentEntity>()).Should().Be(1);
    }

    [Fact]
    public async Task Closing_freezes_the_podium_restarts_everyone_at_1000_keeps_the_archive_and_announces_the_frozen_values()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue(); // wins 10
        (await _kit.EnterAsync(Member(2), prediction, 2, "50")).Result.Succeeded.Should().BeTrue(); // loses 50
        _kit.Random.Next.Enqueue(10);
        (await ClaimAsync(3)).Result.Succeeded.Should().BeTrue(); // 1010 without a correct prediction
        (await _kit.SettleAsync(Admin(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        var oldTournament = (await _kit.WalletAsync(1))!.TournamentId;

        var preview = await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct));
        preview.View!.Embed!.Fields[0].Value.Should().Be("🥇 <@1> — **1010 TSQ Coin** · 1 doğru\n🥈 <@3> — **1010 TSQ Coin** · 0 doğru\n🥉 <@2> — **950 TSQ Coin** · 0 doğru");
        preview.View.Embed.Fields[1].Value.Should().Be("3");

        _kit.Transport.ScriptSend(() => new SendOutcome.Transient("503")); // the first delivery attempt fails
        var token = preview.View.Buttons![0].CustomId![PredictionMessages.EndConfirmPrefix.Length..];
        (await _kit.ConfirmEndAsync(Admin(), token)).Result.Args.Should().Equal(1, 2);
        await _kit.TickAsync();
        _kit.Transport.Messages.Should().NotContain(m => m.Channel == Commands);

        (await _kit.Db(db => db.Set<PredictionStandingEntity>().OrderBy(s => s.Rank).ToListAsync())).Select(s => (s.Rank, s.UserId, s.BalanceMinor, s.CorrectCount))
            .Should().Equal((1, 1UL, 101_000L, 1), (2, 3UL, 101_000L, 0), (3, 2UL, 95_000L, 0));
        (await _kit.WalletAsync(1, oldTournament))!.BalanceMinor.Should().Be(101_000, "the old tournament's wallets stay as its archive");

        // The new tournament: everyone starts again at 1000 and plays; the announcement retry still shows the frozen values.
        var next = await _kit.CreatePredictionAsync(title: "Yeni turnuvanın ilk öngörüsü");
        (await _kit.EnterAsync(Member(1), next, 1, "500")).Result.Succeeded.Should().BeTrue();
        var fresh = (await _kit.WalletAsync(1))!;
        (fresh.TournamentId, fresh.BalanceMinor, fresh.CorrectCount).Should().Be((next.TournamentId, 50_000L, 0));
        (await _kit.LedgerAsync(1)).Count(l => l.Kind == PredictionLedgerKind.Initial).Should().Be(2, "one starting balance per tournament");

        await _kit.TickAsync(TimeSpan.FromMinutes(30));
        var announcement = _kit.Transport.Messages.Should().ContainSingle(m => m.Channel == Commands).Subject;
        announcement.Pinged.Should().BeFalse();
        announcement.Message.Embed!.Title.Should().Be("🏁 TSQ Öngörü · Turnuva 1 sona erdi");
        announcement.Message.Embed.Fields[0].Value.Should().StartWith("🥇 <@1> — **1010 TSQ Coin**");
        announcement.Message.Embed.Fields[1].Value.Should().Be("Yeni turnuva başladı. Bakiyeler 1000 TSQ Coin olarak yenilendi.");

        var status = await _kit.Economy(e => e.TournamentStatusAsync(Member(1), Commands, Ct));
        status.Public.Should().BeTrue();
        status.View!.Embed!.Title.Should().Be("🏟️ TSQ Öngörü · Turnuva 2");
    }

    [Fact]
    public async Task A_confirmation_prepared_in_the_old_tournament_cannot_spend_in_the_new_one()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var (token, _) = await _kit.PreviewEntryAsync(Member(1), prediction, 1, "100");
        (await ClaimAsync(2)).Result.Succeeded.Should().BeTrue();
        (await _kit.CancelAsync(Creator(), prediction)).Result.Succeeded.Should().BeTrue();
        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.Succeeded.Should().BeTrue();

        (await _kit.ConfirmEntryAsync(Member(1), token!)).Result.MessageKey.Should().Be("predictions.entry.tournament_changed");
        (await _kit.CountAsync<PredictionEntryEntity>()).Should().Be(0);
        (await _kit.WalletAsync(1)).Should().BeNull();
    }

    // ---- leaderboards ----

    private async Task SeedWalletsAsync(long tournamentId, params (ulong User, long Balance, long Pending, int Correct, int Settled)[] wallets) =>
        await _kit.Db(async db =>
        {
            foreach (var w in wallets)
            {
                db.Add(new PredictionWalletEntity
                {
                    TournamentId = tournamentId,
                    GuildId = Guild.Value,
                    UserId = w.User,
                    BalanceMinor = w.Balance * 100,
                    PendingMinor = w.Pending * 100,
                    CorrectCount = w.Correct,
                    SettledCount = w.Settled,
                    CreatedAt = TestHost.T0,
                    UpdatedAt = TestHost.T0,
                });
            }

            return await db.SaveChangesAsync();
        });

    private static List<string> Order(string field) => field.Split('\n').Select(l => l.Split(' ')[1]).ToList();

    [Fact]
    public async Task Leaderboards_follow_the_documented_tie_order_within_the_active_tournament_only()
    {
        await _kit.OpenFormAsync(); // tournament 1
        var active = (await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync())).Id;
        var old = await _kit.Db(async db =>
        {
            var closed = new PredictionTournamentEntity { GuildId = Guild.Value, Number = 0, Status = PredictionTournamentStatus.Closed, StartedAt = TestHost.T0 };
            db.Add(closed);
            await db.SaveChangesAsync();
            return closed.Id;
        });
        await SeedWalletsAsync(old, (99, 1_000_000, 0, 50, 50));
        await SeedWalletsAsync(active,
            (1, 1000, 500, 0, 0), // 1500, 0 correct
            (3, 1500, 0, 2, 4), // 1500, 2 correct of 4 (50%)
            (2, 1400, 100, 2, 2), // 1500, 2 correct of 2 (100%)
            (4, 2000, 0, 0, 0),
            (5, 900, 0, 1, 1), // 1 correct, 100%, 900
            (6, 1200, 0, 1, 1), // 1 correct, 100%, 1200
            (7, 1000, 0, 0, 3)); // no correct prediction: not on the second board
        var wallets = await _kit.CountAsync<PredictionWalletEntity>();

        var board = await _kit.Economy(e => e.LeaderboardAsync(Member(50), Commands, Ct));
        board.Public.Should().BeTrue();
        board.View!.Mentions.Should().Be(MentionPolicy.None);
        var fields = board.View.Embed!.Fields;
        Order(fields[0].Value).Should().Equal("<@4>", "<@2>", "<@3>", "<@1>", "<@6>", "<@7>", "<@5>");
        Order(fields[1].Value).Should().Equal("<@2>", "<@3>", "<@6>", "<@5>");
        fields[1].Value.Split('\n')[1].Should().Be("🥈 <@3> — **2** doğru / 4 sonuçlanan (%50)");
        fields.Should().NotContain(f => f.Value.Contains("<@99>", StringComparison.Ordinal), "another tournament never mixes in");
        (await _kit.CountAsync<PredictionWalletEntity>()).Should().Be(wallets, "reading creates no wallet");
    }

    [Fact]
    public async Task Pending_stakes_count_at_principal_and_one_prediction_gives_at_most_one_correct()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 3, "100")).Result.Succeeded.Should().BeTrue(); // 3.10: a possible 310
        var board = await _kit.Economy(e => e.LeaderboardAsync(Member(1), Commands, Ct));
        board.View!.Embed!.Fields[0].Value.Should().Be("🥇 <@1> — **1000 TSQ Coin**");
        board.View.Embed.Fields[1].Value.Should().Be("Henüz doğru sonuçlanmış tahmin yok.");

        (await _kit.SettleAsync(Admin(), prediction, 3)).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(1), prediction, 3, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        var wallet = (await _kit.WalletAsync(1))!;
        (wallet.BalanceMinor, wallet.CorrectCount, wallet.SettledCount).Should().Be((121_000L, 1, 1));
        var mine = await _kit.Economy(e => e.MyEntriesAsync(Member(1), Commands, 0, Ct));
        mine.View!.Embed!.Description.Should().Contain("✅ Kazandı (+310 TSQ Coin)");
    }

    [Fact]
    public async Task My_entries_are_paged_and_empty_boards_say_so()
    {
        var empty = await _kit.Economy(e => e.LeaderboardAsync(Member(1), Commands, Ct));
        empty.View!.Embed!.Fields.Select(f => f.Value).Should().Equal("Henüz kimse yok.", "Henüz doğru sonuçlanmış tahmin yok.");
        (await _kit.Economy(e => e.MyEntriesAsync(Member(1), Commands, 0, Ct))).View!.Embed!.Description.Should().Be("Bu turnuvada henüz bir öngörüye katılmadın.");

        for (var i = 0; i < 12; i++)
        {
            var prediction = await _kit.CreatePredictionAsync(title: "Sayfalama öngörüsü " + i);
            (await _kit.EnterAsync(Member(1), prediction, 1, "1")).Result.Succeeded.Should().BeTrue();
        }

        var first = await _kit.Economy(e => e.MyEntriesAsync(Member(1), Commands, 0, Ct));
        first.View!.Embed!.Footer.Should().Be("Sayfa 1/2");
        first.View.Buttons!.Select(b => (b.CustomId, b.Disabled)).Should().Equal((PredictionMessages.MinePagePrefix + "-1", true), (PredictionMessages.MinePagePrefix + "1", false));
        var second = await _kit.Economy(e => e.MyEntriesAsync(Member(1), Commands, 1, Ct));
        second.View!.Embed!.Description!.Split("\n\n").Should().HaveCount(2);
        (await _kit.Economy(e => e.MyEntriesAsync(Member(2), Commands, 5, Ct))).View!.Embed!.Description.Should().Be("Bu turnuvada henüz bir öngörüye katılmadın.");
    }

    [Fact]
    public async Task The_wallet_shows_available_pending_and_total_separately()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "250")).Result.Succeeded.Should().BeTrue();
        var view = await _kit.Economy(e => e.WalletAsync(Member(1), Commands, Ct));
        view.Public.Should().BeFalse();
        view.View!.Embed!.Fields.Take(3).Select(f => f.Value).Should().Equal("750 TSQ Coin", "250 TSQ Coin", "1000 TSQ Coin");
        view.View.Embed.Fields[3].Value.Should().Be("Henüz sonuçlanan tahminin yok · 1 bekleyen");
    }
}
