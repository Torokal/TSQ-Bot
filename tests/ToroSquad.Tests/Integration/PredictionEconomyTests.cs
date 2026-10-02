using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The TSQ Öngörü economy on the real SQLite database: no way for a member to reset their own coins (repeated commands,
/// restarts, leaving and rejoining, daily rewards, cancels and results never bring the starting balance back — only
/// /ongoru turnuva bitir does, as a new tournament), the daily reward (10–100, one per Türkiye calendar day, atomic under
/// parallel claims), the tournament cycle (administrators only, blocked by unresolved predictions, one close under
/// simultaneous confirmations, stale and expired confirmations, both podiums frozen with names, fresh starting balances,
/// archived old wallets, the announcement rendered from the snapshot and retried unchanged, consistent with a daily claim or
/// a settlement racing it) and the leaderboards (only members who entered or created a prediction in THIS tournament).
/// </summary>
public sealed class PredictionEconomyTests : IAsyncLifetime
{
    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await PredictionTestKit.CreateAsync();

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    private Task<PredictionReply> ClaimAsync(ulong user) => _kit.ClaimDailyAsync(Member(user));

    private Task<int> InitialGrantsAsync(ulong user) => _kit.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.UserId == user && l.Kind == PredictionLedgerKind.Initial));

    // ---- no member reset ----

    [Fact]
    public async Task Repeated_wallet_views_daily_rewards_entries_and_leaving_and_rejoining_never_grant_the_start_again()
    {
        for (var i = 0; i < 3; i++)
            (await _kit.Economy(e => e.WalletAsync(Member(1), Commands, Ct))).View!.Embed!.Fields[0].Value.Should().Be("1000 TSQ Coin");
        (await _kit.CountAsync<PredictionWalletEntity>()).Should().Be(0, "looking creates nothing");

        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 2, "300")).Result.Succeeded.Should().BeTrue();
        _kit.Host.Guilds.RemoveMember(Guild, new UserId(1)); // leaves the server …
        _kit.Host.Guilds.AddMember(Guild, new UserId(1)); // … and comes back
        _kit.Host.Clock.Advance(TimeSpan.FromDays(1));
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();

        (await InitialGrantsAsync(1)).Should().Be(1);
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(100_000 + 1_000 - 30_000 + 1_000);
    }

    [Fact]
    public async Task Losses_stay_lost_daily_rewards_cancels_and_results_never_reset_the_wallet()
    {
        var lost = await _kit.CreatePredictionAsync(title: "Kaybedilen öngörü başlığı");
        (await _kit.EnterAsync(Member(1), lost, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Admin(), lost, 2)).Result.Succeeded.Should().BeTrue();
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(90_000);

        _kit.Random.Next.Enqueue(10);
        (await ClaimAsync(1)).Result.Args.Take(2).Should().Equal("10", "910");

        var cancelled = await _kit.CreatePredictionAsync(title: "İptal edilen öngörü başlığı");
        (await _kit.EnterAsync(Member(1), cancelled, 3, "50")).Result.Succeeded.Should().BeTrue();
        (await _kit.CancelAsync(Creator(), cancelled)).Result.Succeeded.Should().BeTrue();
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(91_000, "only the 50 staked came back — never 1000");

        var won = await _kit.CreatePredictionAsync(title: "Kazanılan öngörü başlığı");
        (await _kit.EnterAsync(Member(1), won, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Admin(), won, 1)).Result.Succeeded.Should().BeTrue();
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(92_000, "910 − 100 + 110: a normal payout, not a reset");
        (await InitialGrantsAsync(1)).Should().Be(1);
    }

    [Fact]
    public async Task The_starting_balance_is_granted_once_across_restarts()
    {
        await _kit.ParticipateAsync(1);
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();

        await using var second = await PredictionTestKit.CreateAsync(TestHost.T0.AddDays(1), directory: _kit.Host.Directory, transport: _kit.Transport);
        (await second.ClaimDailyAsync(Member(1))).Result.Succeeded.Should().BeTrue();
        (await second.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.UserId == 1 && l.Kind == PredictionLedgerKind.Initial))).Should().Be(1);
        (await second.WalletAsync(1))!.BalanceMinor.Should().Be(100_000 - 100 + 1_000 + 1_000); // the participation lost its 1 coin
    }

    [Fact]
    public async Task Only_ending_the_tournament_starts_everyone_again_at_1000_in_a_new_tournament()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "400")).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Admin(), prediction, 2)).Result.Succeeded.Should().BeTrue();
        var old = (await _kit.WalletAsync(1))!;
        old.BalanceMinor.Should().Be(60_000);

        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.Args.Should().Equal(1, 2);
        (await _kit.Economy(e => e.WalletAsync(Member(1), Commands, Ct))).View!.Embed!.Fields[0].Value.Should().Be("1000 TSQ Coin");
        var next = await _kit.CreatePredictionAsync(title: "Yeni turnuvanın ilk öngörüsü");
        (await _kit.EnterAsync(Member(1), next, 1, "1")).Result.Succeeded.Should().BeTrue();
        var fresh = (await _kit.WalletAsync(1))!;
        (fresh.TournamentId, fresh.BalanceMinor).Should().Be((next.TournamentId, 99_900L));
        (await _kit.WalletAsync(1, old.TournamentId))!.BalanceMinor.Should().Be(60_000, "the old wallet is the archive, never rewritten");
        (await InitialGrantsAsync(1)).Should().Be(2, "one starting balance per tournament");
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
        (await kit.ClaimDailyAsync(Member(1))).Result.Succeeded.Should().BeTrue();
        kit.Host.Clock.Advance(TimeSpan.FromSeconds(59));
        var already = await kit.ClaimDailyAsync(Member(1));
        already.Result.MessageKey.Should().Be("predictions.daily.already");
        already.Result.Args[1].Should().Be(DiscordText.Timestamp(new DateTimeOffset(2026, 9, 29, 21, 0, 0, TimeSpan.Zero), 'R'));
        kit.Host.Clock.Advance(TimeSpan.FromSeconds(1)); // 00:00 in Türkiye, one minute after the first claim
        (await kit.ClaimDailyAsync(Member(1))).Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Ending_the_tournament_gives_no_second_daily_claim_the_same_day()
    {
        await _kit.ParticipateAsync(2);
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
    public async Task Only_administrators_or_the_owner_can_end_a_tournament_and_only_in_the_commands_channel()
    {
        await _kit.ParticipateAsync(1);
        foreach (var actor in new[] { Creator(), Member(1), TestHost.Admin(Guild) }) // TestHost.Admin has Manage Server + Manage Roles, not Administrator
            (await _kit.Economy(e => e.PreviewTournamentEndAsync(actor, Commands, Ct))).Result.MessageKey.Should().Be("predictions.tournament.admin_only");
        (await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Predictions, Ct))).Result.MessageKey.Should().Be("predictions.wrong_channel");
        var token = await _kit.EndTokenAsync(Owner());
        token.Should().NotBeNull();
        (await _kit.Economy(e => e.ConfirmTournamentEndAsync(Owner(), Predictions, token!, Ct))).Result.MessageKey.Should().Be("predictions.wrong_channel");
        (await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync())).Status.Should().Be(PredictionTournamentStatus.Active);
        (await _kit.EndTokenAsync(Admin())).Should().NotBeNull();
    }

    [Fact]
    public async Task Unresolved_predictions_block_ending_are_listed_briefly_and_nothing_changes()
    {
        await _kit.ParticipateAsync(1);
        var open = await _kit.CreatePredictionAsync();
        var locked = await _kit.CreatePredictionAsync(title: "Kilitli öngörü başlığı");
        (await _kit.LockAsync(Admin(), locked)).Succeeded.Should().BeTrue();

        var blocked = await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct));
        blocked.Result.MessageKey.Should().Be("predictions.tournament.unresolved");
        blocked.Public.Should().BeFalse();
        var lines = blocked.View!.Content!.Split('\n');
        lines[0].Should().Be("Turnuvayı bitirmeden önce sonuçlanmamış öngörüleri sonuçlandırmalı veya iptal etmelisiniz.");
        lines.Should().Contain(l => l.StartsWith("• **#" + open.Id + "** · Galatasaray \\- Fenerbahçe", StringComparison.Ordinal) && l.Contains(" · Açık — https://discord.com/channels/", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("• **#" + locked.Id + "** · Kilitli öngörü başlığı · Kilitli", StringComparison.Ordinal));

        (await _kit.CountAsync<PredictionTournamentEntity>()).Should().Be(1);
        (await _kit.CountAsync<PredictionStandingEntity>()).Should().Be(0);
        (await _kit.Db(db => db.Outbox.CountAsync())).Should().Be(0, "no announcement");
    }

    [Fact]
    public async Task A_prediction_published_after_the_preview_blocks_the_confirmation()
    {
        await _kit.ParticipateAsync(1);
        var token = await _kit.EndTokenAsync(Admin());
        await _kit.CreatePredictionAsync();

        var result = await _kit.ConfirmEndAsync(Admin(), token!);
        result.Result.MessageKey.Should().Be("predictions.tournament.unresolved");
        (await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync())).Status.Should().Be(PredictionTournamentStatus.Active);
        (await _kit.CountAsync<PredictionStandingEntity>()).Should().Be(0);
    }

    [Fact]
    public async Task Two_admins_confirming_together_close_the_tournament_once()
    {
        await _kit.ParticipateAsync(1, 2);
        var a = await _kit.EndTokenAsync(Admin());
        var b = await _kit.EndTokenAsync(Owner());
        var results = await _kit.TogetherAsync(() => _kit.ConfirmEndAsync(Admin(), a!), () => _kit.ConfirmEndAsync(Owner(), b!));

        results.Count(r => r.Result.Succeeded).Should().Be(1);
        results.Single(r => !r.Result.Succeeded).Result.MessageKey.Should().Be("predictions.tournament.stale");
        var tournaments = await _kit.Db(db => db.Set<PredictionTournamentEntity>().OrderBy(t => t.Number).ToListAsync());
        tournaments.Select(t => (t.Number, t.Status)).Should().Equal((1, PredictionTournamentStatus.Closed), (2, PredictionTournamentStatus.Active));
        (await _kit.Db(db => db.Outbox.CountAsync(o => o.ModuleId == "predictions"))).Should().Be(1, "one announcement");
        (await _kit.Db(db => db.Set<PredictionStandingEntity>().Select(s => s.TournamentId).Distinct().CountAsync())).Should().Be(1, "one snapshot set");
        (await _kit.CountAsync<PredictionWalletEntity>()).Should().Be(3, "no wallet is created by closing");
    }

    [Fact]
    public async Task An_old_confirmation_never_closes_the_next_tournament()
    {
        await _kit.ParticipateAsync(1);
        var old = await _kit.EndTokenAsync(Admin());
        (await _kit.ConfirmEndAsync(Owner(), (await _kit.EndTokenAsync(Owner()))!)).Result.Succeeded.Should().BeTrue();
        await _kit.ParticipateAsync(2); // tournament 2 has a participant (within the confirmation's lifetime)

        (await _kit.ConfirmEndAsync(Admin(), old!)).Result.MessageKey.Should().Be("predictions.tournament.stale");
        (await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync(t => t.Status == PredictionTournamentStatus.Active))).Number.Should().Be(2);
    }

    [Fact]
    public async Task An_end_confirmation_is_short_lived_single_use_and_bound_to_its_admin()
    {
        await _kit.ParticipateAsync(1);
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
    public async Task Wallets_without_prediction_activity_are_no_participants_and_end_nothing()
    {
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue(); // a wallet, but only a daily reward
        (await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct))).Result.MessageKey.Should().Be("predictions.tournament.no_participants");
        (await _kit.CountAsync<PredictionTournamentEntity>()).Should().Be(1);
    }

    [Fact]
    public async Task A_pending_stake_with_nothing_unresolved_refuses_the_end_instead_of_freezing_a_wrong_podium()
    {
        await _kit.ParticipateAsync(1);
        var token = await _kit.EndTokenAsync(Admin());
        await _kit.Db(db => db.Set<PredictionWalletEntity>().Where(w => w.UserId == 1).ExecuteUpdateAsync(s => s.SetProperty(w => w.PendingMinor, 5_000L))); // SYNTHETIC corruption

        (await _kit.ConfirmEndAsync(Admin(), token!)).Result.MessageKey.Should().Be("predictions.tournament.pending_stakes");
        (await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct))).Result.MessageKey.Should().Be("predictions.tournament.pending_stakes");
        (await _kit.CountAsync<PredictionTournamentEntity>()).Should().Be(1);
        (await _kit.CountAsync<PredictionStandingEntity>()).Should().Be(0);
    }

    [Fact]
    public async Task Closing_freezes_both_podiums_with_names_restarts_everyone_at_1000_keeps_the_archive_and_announces_the_frozen_values()
    {
        var prediction = await _kit.CreatePredictionAsync(); // Creator(10), "Kaan": creating makes nobody eligible
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue(); // wins 10
        (await _kit.EnterAsync(Member(2), prediction, 2, "50")).Result.Succeeded.Should().BeTrue(); // loses 50
        _kit.Random.Next.Enqueue(10);
        (await ClaimAsync(3)).Result.Succeeded.Should().BeTrue(); // 1010 but no prediction activity: never on a board
        (await _kit.SettleAsync(Admin(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        var oldTournament = (await _kit.WalletAsync(1))!.TournamentId;

        var preview = await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct));
        var fields = preview.View!.Embed!.Fields;
        preview.View.Embed.Title.Should().Be("🏁 Turnuva 1 bitirilsin mi?");
        preview.View.Embed.Description.Should().Contain("**1000 TSQ Coin**");
        fields.Select(f => (f.Name, f.Value)).Skip(1).Take(3).Should().Equal(("Katılımcı", "2"), ("Öngörü", "1"), ("Sonuçlandırılmış", "1"));
        fields[4].Value.Should().Be("🥇 <@1> — **1010 TSQ Coin**\n🥈 <@2> — **950 TSQ Coin**");
        fields[5].Value.Should().Be("🥇 <@1> — **1** doğru / 1 sonuçlanan (%100)\n🥈 <@2> — **0** doğru / 1 sonuçlanan (%0)");
        preview.View.Buttons!.Select(b => b.Label).Should().Equal("🏁 Turnuvayı Bitir", "Vazgeç");

        _kit.Transport.ScriptSend(() => new SendOutcome.Transient("503")); // the first delivery attempt fails
        var token = Token(preview, PredictionMessages.EndConfirmPrefix)!;
        (await _kit.ConfirmEndAsync(Admin(), token)).Result.Args.Should().Equal(1, 2);
        await _kit.TickAsync();
        _kit.Transport.Messages.Should().NotContain(m => m.Channel == Commands);

        (await _kit.Db(db => db.Set<PredictionStandingEntity>().OrderBy(s => s.Board).ThenBy(s => s.Rank).ToListAsync()))
            .Select(s => (s.Board, s.Rank, s.UserId, s.DisplayName, s.BalanceMinor, s.CorrectCount)).Should().Equal(
                (PredictionBoard.Coins, 1, 1UL, "Üye 1", 101_000L, 1), (PredictionBoard.Coins, 2, 2UL, "Üye 2", 95_000L, 0),
                (PredictionBoard.Correct, 1, 1UL, "Üye 1", 101_000L, 1), (PredictionBoard.Correct, 2, 2UL, "Üye 2", 95_000L, 0));
        (await _kit.WalletAsync(1, oldTournament))!.BalanceMinor.Should().Be(101_000, "the old tournament's wallets stay as its archive");
        var closed = await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync(t => t.Id == oldTournament));
        (closed.Status, closed.FinalParticipantCount, closed.FinalPredictionCount, closed.ClosedByUserId).Should().Be((PredictionTournamentStatus.Closed, 2, 1, 20UL));

        // The new tournament: everyone starts again at 1000 and plays; the announcement retry still shows the frozen values.
        var next = await _kit.CreatePredictionAsync(title: "Yeni turnuvanın ilk öngörüsü");
        (await _kit.EnterAsync(Member(1), next, 1, "500")).Result.Succeeded.Should().BeTrue();
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(50_000);

        await _kit.TickAsync(TimeSpan.FromMinutes(30));
        var announcement = _kit.Transport.Messages.Should().ContainSingle(m => m.Channel == Commands).Subject;
        announcement.Pinged.Should().BeFalse();
        var embed = announcement.Message.Embed!;
        embed.Title.Should().Be("🏁 TSQ Öngörü · Turnuva 1 Sona Erdi");
        embed.Fields.Select(f => f.Name).Should().Equal("💰 En Çok TSQ Coin", "🎯 En Çok Doğru Tahmin", "🔄 Yeni Turnuva Başladı");
        embed.Fields[0].Value.Should().Be("🥇 **Üye 1** — **1010 TSQ Coin**\n🥈 **Üye 2** — **950 TSQ Coin**");
        embed.Fields[1].Value.Should().StartWith("🥇 **Üye 1** — **1** doğru / 1 sonuçlanan (%100)");
        embed.Fields[2].Value.Should().Be("Herkes yeni turnuvaya 1000 TSQ Coin ile başlar.");
        string.Join("", embed.Fields.Select(f => f.Value)).Should().NotContain("<@", "no mention in the announcement");

        var status = await _kit.Economy(e => e.TournamentStatusAsync(Member(1), Commands, Ct));
        status.Public.Should().BeTrue();
        status.View!.Embed!.Title.Should().Be("🏟️ TSQ Öngörü · Turnuva 2");
    }

    [Fact]
    public async Task An_entry_form_opened_in_the_old_tournament_cannot_spend_in_the_new_one()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.OpenEntryAsync(Member(1), prediction)).Form.Should().NotBeNull();
        (await _kit.CancelAsync(Creator(), prediction)).Result.Succeeded.Should().BeTrue();
        await _kit.ParticipateAsync(5); // someone with a settled entry to end the tournament for
        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.Succeeded.Should().BeTrue();

        (await _kit.SubmitEntryAsync(Member(1), prediction, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.Db(db => db.Set<PredictionEntryEntity>().CountAsync(e => e.UserId == 1))).Should().Be(0);
        (await _kit.WalletAsync(1)).Should().BeNull();
    }

    [Fact]
    public async Task A_daily_claim_racing_the_close_lands_in_exactly_one_tournament_and_the_snapshot_matches_the_archive()
    {
        await _kit.ParticipateAsync(1);
        var token = await _kit.EndTokenAsync(Admin());
        var results = await _kit.TogetherAsync(() => _kit.ConfirmEndAsync(Admin(), token!), () => ClaimAsync(1));
        results.Should().OnlyContain(r => r.Result.Succeeded);

        var claim = await _kit.Db(db => db.Set<PredictionDailyClaimEntity>().SingleAsync());
        var tournaments = await _kit.Db(db => db.Set<PredictionTournamentEntity>().OrderBy(t => t.Number).ToListAsync());
        var old = tournaments[0];
        var archived = (await _kit.WalletAsync(1, old.Id))!;
        var frozen = await _kit.Db(db => db.Set<PredictionStandingEntity>().SingleAsync(s => s.Board == PredictionBoard.Coins && s.UserId == 1));
        frozen.BalanceMinor.Should().Be(archived.BalanceMinor + archived.PendingMinor, "the frozen value is the archived one");
        if (claim.TournamentId == old.Id)
            archived.BalanceMinor.Should().Be(100_900); // 1000 - the lost 1 coin + the 10-coin claim
        else
            ((await _kit.WalletAsync(1, tournaments[1].Id))!.BalanceMinor, archived.BalanceMinor).Should().Be((101_000L, 99_900L));
    }

    [Fact]
    public async Task A_settlement_racing_the_close_either_finishes_first_and_is_in_the_snapshot_or_blocks_the_close()
    {
        await _kit.ParticipateAsync(1);
        var token = await _kit.EndTokenAsync(Admin());
        var prediction = await _kit.CreatePredictionAsync(title: "Kapanışla yarışan öngörü");
        (await _kit.EnterAsync(Member(2), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();

        var results = await _kit.TogetherAsync(() => _kit.ConfirmEndAsync(Admin(), token!), () => _kit.ConfirmSettleAsync(Admin(), prediction, 1));
        results[1].Result.MessageKey.Should().Be("predictions.settle.done");
        (await _kit.CountAsync<PredictionEntryEntity>(), await _kit.Db(db => db.Set<PredictionEntryEntity>().CountAsync(e => e.Status == PredictionEntryStatus.Pending)))
            .Should().Be((2, 0));
        if (results[0].Result.Succeeded)
        {
            var frozen = await _kit.Db(db => db.Set<PredictionStandingEntity>().SingleAsync(s => s.Board == PredictionBoard.Coins && s.UserId == 2));
            (frozen.BalanceMinor, frozen.CorrectCount).Should().Be((101_000L, 1), "the snapshot contains the settled payout, never a half state");
        }
        else
        {
            results[0].Result.MessageKey.Should().Be("predictions.tournament.unresolved");
            (await _kit.CountAsync<PredictionTournamentEntity>()).Should().Be(1);
            (await _kit.CountAsync<PredictionStandingEntity>()).Should().Be(0);
        }
    }

    // ---- leaderboards: who is on them ----

    private async Task<long> ActiveTournamentAsync() =>
        await _kit.Db(db => db.Set<PredictionTournamentEntity>().Where(t => t.Status == PredictionTournamentStatus.Active).Select(t => t.Id).SingleAsync());

    /// <summary>
    /// Wallets with chosen numbers (SYNTHETIC counters, as the settlement would leave them): eligible = at least one settled
    /// entry, so the data must say so — an eligible wallet has Settled ≥ 1, an ineligible one Settled 0.
    /// </summary>
    private async Task SeedAsync(long tournamentId, params (ulong User, long Balance, long Pending, int Correct, int Settled, bool Eligible)[] wallets) =>
        await _kit.Db(async db =>
        {
            foreach (var w in wallets)
            {
                (w.Settled > 0).Should().Be(w.Eligible, "eligibility is a settled entry (user {0})", w.User);
                db.Add(new PredictionWalletEntity
                {
                    TournamentId = tournamentId,
                    GuildId = Guild.Value,
                    UserId = w.User,
                    DisplayName = "Üye " + w.User,
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

    private async Task<IReadOnlyList<MessageEmbed>> BoardsAsync() =>
        [(await _kit.Economy(e => e.LeaderboardAsync(Member(99), Commands, Ct))).View!.Embed!];

    private static List<string> Order(string field) => field.Split('\n').Select(l => l.Split(' ')[1]).ToList();

    private const string Empty = "Henüz bu turnuvada tahmini sonuçlanmış kimse yok.";

    [Fact]
    public async Task Only_members_with_a_settled_own_entry_are_on_the_boards_won_or_lost()
    {
        await _kit.OpenFormAsync(); // tournament 1
        await SeedAsync(await ActiveTournamentAsync(), (5, 5000, 0, 0, 0, false)); // A: a wallet only (even 5000 coins)
        (await ClaimAsync(6)).Result.Succeeded.Should().BeTrue(); // B: daily only
        var open = await _kit.CreatePredictionAsync(creator: Creator(7)); // C: creator only
        (await _kit.EnterAsync(Member(8), open, 1, "100")).Result.Succeeded.Should().BeTrue(); // D: an open entry
        var locked = await _kit.CreatePredictionAsync(title: "Kilitlenecek öngörü");
        (await _kit.EnterAsync(Member(9), locked, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.LockAsync(Creator(), locked)).Succeeded.Should().BeTrue(); // E: a locked, unresolved entry
        (await _kit.EnterAsync(Member(11), open, 2, "50")).Result.Succeeded.Should().BeTrue();
        (await _kit.WithdrawAsync(Member(11), open)).Result.Succeeded.Should().BeTrue(); // F: withdrawn only
        var cancelled = await _kit.CreatePredictionAsync(title: "İptal edilecek öngörü");
        (await _kit.EnterAsync(Member(12), cancelled, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.CancelAsync(Creator(), cancelled)).Result.Succeeded.Should().BeTrue(); // G: refunded only
        (await BoardsAsync())[0].Fields.Select(f => f.Value).Should().Equal(Empty, Empty);
        (await _kit.Economy(e => e.TournamentStatusAsync(Member(1), Commands, Ct))).View!.Embed!.Fields.Single(f => f.Name == "Katılımcı").Value
            .Should().Be("4", "participants are members with an entry (8, 9, 11, 12), not the leaderboard");

        var settled = await _kit.CreatePredictionAsync(title: "Sonuçlanacak öngörü");
        (await _kit.EnterAsync(Member(13), settled, 1, "100")).Result.Succeeded.Should().BeTrue(); // I: wins
        (await _kit.EnterAsync(Member(14), settled, 2, "100")).Result.Succeeded.Should().BeTrue(); // H: loses
        (await _kit.SettleAsync(Creator(), settled, 1)).Result.Succeeded.Should().BeTrue();
        var boards = (await BoardsAsync())[0];
        Order(boards.Fields[0].Value).Should().Equal("<@13>", "<@14>"); // exactly the two settled ones, no invented third
        boards.Fields[1].Value.Should().Be("🥇 <@13> — **1** doğru / 1 sonuçlanan (%100)\n🥈 <@14> — **0** doğru / 1 sonuçlanan (%0)", "0% is meaningful once something is settled");
    }

    [Fact]
    public async Task A_creator_who_also_bets_appears_once_and_creating_earns_no_correct_prediction()
    {
        var prediction = await _kit.CreatePredictionAsync(creator: Creator(7));
        (await _kit.EnterAsync(Creator(7), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Admin(), prediction, 2)).Result.Succeeded.Should().BeTrue();
        var boards = (await BoardsAsync())[0];
        boards.Fields[0].Value.Split('\n').Should().ContainSingle();
        boards.Fields[1].Value.Should().Be("🥇 <@7> — **0** doğru / 1 sonuçlanan (%0)");
    }

    [Fact]
    public async Task A_settled_entry_in_an_earlier_tournament_does_not_carry_over_the_first_settled_one_in_the_new_one_does()
    {
        await _kit.ParticipateAsync(5); // settled in tournament 1
        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.Succeeded.Should().BeTrue();
        (await ClaimAsync(5)).Result.Succeeded.Should().BeTrue();
        var prediction = await _kit.CreatePredictionAsync(title: "İkinci turnuvanın öngörüsü");
        (await _kit.EnterAsync(Member(5), prediction, 1, "10")).Result.Succeeded.Should().BeTrue(); // J: only open in tournament 2
        (await BoardsAsync())[0].Fields[0].Value.Should().Be(Empty);

        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue(); // K: the first settled one
        Order((await BoardsAsync())[0].Fields[0].Value).Should().Equal("<@5>");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(25)]
    public async Task Each_board_lists_at_most_the_top_10_eligible_members_and_never_invents_a_row(int eligible)
    {
        await _kit.OpenFormAsync(); // tournament 1
        var active = await ActiveTournamentAsync();
        await SeedAsync(active, Enumerable.Range(1, eligible).Select(i => ((ulong)(1000 + i), 1000L, 0L, 0, 1, true)).ToArray()); // all tied: the user id decides
        await SeedAsync(active, (5, 9000, 0, 0, 0, false)); // the most coins, but nothing settled: never listed

        var fields = (await _kit.Economy(e => e.LeaderboardAsync(Member(50), Commands, Ct))).View!.Embed!.Fields;
        if (eligible == 0)
        {
            fields.Select(f => f.Value).Should().Equal(Empty, Empty);
            return;
        }

        var top = Enumerable.Range(1, Math.Min(eligible, PredictionRules.LeaderboardSize)).Select(i => "<@" + (1000 + i) + ">").ToList();
        Order(fields[0].Value).Should().Equal(top, "💰 En Çok TSQ Coin: the first ten by the documented order");
        Order(fields[1].Value).Should().Equal(top, "🎯 En Çok Doğru Tahmin: the same limit");
        (await _kit.Economy(e => e.CoinBoardAsync(active, PredictionRules.LeaderboardSize, Ct))).Should().HaveCount(Math.Min(eligible, 10), "the limit is in the query");
    }

    [Fact]
    public async Task Leaderboards_follow_the_documented_tie_order_top_10_of_25_within_the_active_tournament_only()
    {
        await _kit.OpenFormAsync(); // tournament 1
        var active = await ActiveTournamentAsync();
        var old = await _kit.Db(async db =>
        {
            var closed = new PredictionTournamentEntity { GuildId = Guild.Value, Number = 0, Status = PredictionTournamentStatus.Closed, StartedAt = TestHost.T0 };
            db.Add(closed);
            await db.SaveChangesAsync();
            return closed.Id;
        });
        await SeedAsync(old, (99, 1_000_000, 0, 50, 50, true));
        await SeedAsync(active,
            (1, 1000, 500, 0, 1, true), // 1500 live (500 in open predictions), 0 of 1
            (3, 1500, 0, 2, 4, true), // 1500, 2 of 4 (50%)
            (2, 1400, 100, 2, 2, true), // 1500, 2 of 2 (100%)
            (4, 2000, 0, 0, 2, true), // 2000, 0 of 2
            (5, 900, 0, 1, 1, true), // 1 correct, 100%, 900
            (6, 1200, 0, 1, 1, true), // 1 correct, 100%, 1200
            (7, 1000, 0, 0, 3, true), // 0 of 3
            (8, 9000, 0, 0, 0, false)); // the most coins, but nothing settled: never listed
        await SeedAsync(active, Enumerable.Range(0, 18).Select(i => ((ulong)(100 + i), 100L + i, 0L, 0, 1, true)).ToArray()); // 25 eligible in total
        var wallets = await _kit.CountAsync<PredictionWalletEntity>();

        var board = await _kit.Economy(e => e.LeaderboardAsync(Member(50), Commands, Ct));
        board.Public.Should().BeTrue();
        board.View!.Mentions.Should().Be(MentionPolicy.None);
        var fields = board.View.Embed!.Fields;
        Order(fields[0].Value).Should().Equal("<@4>", "<@2>", "<@3>", "<@1>", "<@6>", "<@7>", "<@5>", "<@117>", "<@116>", "<@115>");
        Order(fields[1].Value).Should().Equal("<@2>", "<@3>", "<@6>", "<@5>", "<@4>", "<@1>", "<@7>", "<@117>", "<@116>", "<@115>");
        fields[0].Value.Split('\n')[3].Should().Be("`4.` <@1> — **1500 TSQ Coin**", "live wealth: available + principal in open predictions");
        fields[1].Value.Split('\n')[1].Should().Be("🥈 <@3> — **2** doğru / 4 sonuçlanan (%50)");
        fields[1].Value.Split('\n')[4].Should().Be("`5.` <@4> — **0** doğru / 2 sonuçlanan (%0)", "with 0 correct every rate is 0%: live wealth decides");
        string.Join("", fields.Select(f => f.Value)).Should().NotContain("<@99>", "another tournament never mixes in").And.NotContain("<@8>");
        (await _kit.CountAsync<PredictionWalletEntity>()).Should().Be(wallets, "reading creates no wallet");

        var again = await _kit.Economy(e => e.LeaderboardAsync(Member(51), Commands, Ct));
        again.View!.Embed!.Fields.Select(f => f.Value).Should().Equal(fields.Select(f => f.Value), "deterministic");
        var status = await _kit.Economy(e => e.TournamentStatusAsync(Member(1), Commands, Ct));
        status.View!.Embed!.Fields.Single(f => f.Name == "Katılımcı").Value.Should().Be("0", "participants count entries (these wallets are synthetic, without any): not the 25 ranked");
    }

    [Fact]
    public async Task Live_wealth_counts_open_and_locked_principal_once_and_never_a_possible_payout()
    {
        await _kit.ParticipateAsync(1); // eligible, 999 available
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 3, "200")).Result.Succeeded.Should().BeTrue(); // 3.10: a possible 620
        async Task<string> CoinsAsync() => (await BoardsAsync())[0].Fields[0].Value;
        (await CoinsAsync()).Should().Be("🥇 <@1> — **999 TSQ Coin**", "L: 799 available + 200 open");
        (await _kit.LockAsync(Creator(), prediction)).Succeeded.Should().BeTrue();
        (await CoinsAsync()).Should().Be("🥇 <@1> — **999 TSQ Coin**", "M: 799 available + 200 locked");

        (await _kit.SettleAsync(Admin(), prediction, 3)).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(1), prediction, 3, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        var wallet = (await _kit.WalletAsync(1))!;
        (wallet.BalanceMinor, wallet.PendingMinor, wallet.CorrectCount, wallet.SettledCount).Should().Be((141_900L, 0L, 1, 2));
        (await CoinsAsync()).Should().Be("🥇 <@1> — **1419 TSQ Coin**", "O: after the win the old stake is not counted again (not 1619)");
        var mine = await _kit.Economy(e => e.MyEntriesAsync(Member(1), Commands, 0, Ct));
        mine.View!.Embed!.Description.Should().Contain("✅ Kazandı (+620 TSQ Coin)");
    }

    [Fact]
    public async Task Changing_or_withdrawing_a_stake_moves_coins_between_available_and_pending_without_changing_live_wealth()
    {
        await _kit.ParticipateAsync(1); // 999
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        async Task<(long Available, long Pending, string Board)> StateAsync()
        {
            var w = (await _kit.WalletAsync(1))!;
            return (w.BalanceMinor, w.PendingMinor, (await BoardsAsync())[0].Fields[0].Value);
        }

        const string Board = "🥇 <@1> — **999 TSQ Coin**";
        (await StateAsync()).Should().Be((89_900L, 10_000L, Board));
        (await _kit.ChangeAsync(Member(1), prediction, 1, "200")).Result.Succeeded.Should().BeTrue();
        (await StateAsync()).Should().Be((79_900L, 20_000L, Board), "P: 100 → 200");
        (await _kit.ChangeAsync(Member(1), prediction, 2, "50")).Result.Succeeded.Should().BeTrue();
        (await StateAsync()).Should().Be((94_900L, 5_000L, Board), "200 → 50 on another outcome");
        (await _kit.WithdrawAsync(Member(1), prediction)).Result.Succeeded.Should().BeTrue();
        (await StateAsync()).Should().Be((99_900L, 0L, Board), "N: the withdrawn stake is back in the balance, not counted twice");
    }

    [Fact]
    public async Task A_daily_reward_adds_to_an_eligible_members_live_wealth_but_never_makes_anyone_eligible()
    {
        await _kit.ParticipateAsync(1); // 999
        _kit.Random.Next.Enqueue(47);
        (await ClaimAsync(1)).Result.Succeeded.Should().BeTrue();
        (await BoardsAsync())[0].Fields[0].Value.Should().Be("🥇 <@1> — **1046 TSQ Coin**", "Q: +47");

        for (var day = 0; day < 3; day++)
        {
            _kit.Random.Next.Enqueue(100);
            _kit.Host.Clock.Advance(TimeSpan.FromDays(1));
            (await ClaimAsync(2)).Result.Succeeded.Should().BeTrue();
        }

        (await _kit.WalletAsync(2))!.BalanceMinor.Should().Be(130_000);
        (await BoardsAsync())[0].Fields[0].Value.Should().NotContain("<@2>", "R: daily rewards alone (1300 coins) are no settled prediction");
    }

    [Fact]
    public async Task My_entries_are_paged()
    {
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
