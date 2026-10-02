using Microsoft.EntityFrameworkCore;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Ending a tournament is decided by its own rule, not by the leaderboard: nothing unresolved, no pending stake, and
/// something happened in it. A tournament whose predictions were all cancelled (or whose entries were all withdrawn) ends
/// with an empty podium and a clean announcement; the podium itself still lists only members with a settled entry. Real
/// SQLite, fake Discord.
/// </summary>
public sealed class PredictionTournamentCloseTests : IAsyncLifetime
{
    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await PredictionTestKit.CreateAsync();

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    private Task<PredictionReply> PreviewAsync() => _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct));

    private async Task<PredictionReply> EndAsync() => await _kit.ConfirmEndAsync(Admin(), Token(await PreviewAsync(), PredictionMessages.EndConfirmPrefix)!);

    private async Task<string> CoinBoardAsync() => (await _kit.Economy(e => e.LeaderboardAsync(Member(99), Commands, Ct))).View!.Embed!.Fields[0].Value;

    private Task<List<PredictionTournamentEntity>> TournamentsAsync() =>
        _kit.Db(db => db.Set<PredictionTournamentEntity>().AsNoTracking().OrderBy(t => t.Number).ToListAsync());

    private const string EmptyBoard = "Henüz bu turnuvada tahmini sonuçlanmış kimse yok.";

    [Fact]
    public async Task One_settled_player_is_on_the_board_and_the_tournament_ends()
    {
        await _kit.ParticipateAsync(1);
        (await CoinBoardAsync()).Should().Be("🥇 <@1> — **999 TSQ Coin**");
        (await EndAsync()).Result.Succeeded.Should().BeTrue();
        (await TournamentsAsync()).Select(t => t.Status).Should().Equal(PredictionTournamentStatus.Closed, PredictionTournamentStatus.Active);
    }

    [Fact]
    public async Task A_tournament_whose_predictions_were_all_cancelled_ends_with_an_empty_podium_and_a_clean_announcement()
    {
        var first = await _kit.CreatePredictionAsync(title: "Birinci öngörü");
        var second = await _kit.CreatePredictionAsync(title: "İkinci öngörü");
        foreach (var user in new ulong[] { 1, 2, 3 })
            (await _kit.EnterAsync(Member(user), first, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(1), second, 2, "50")).Result.Succeeded.Should().BeTrue();
        (await _kit.CancelAsync(Creator(), first)).Result.Succeeded.Should().BeTrue();
        (await _kit.CancelAsync(Creator(), second)).Result.Succeeded.Should().BeTrue();
        (await _kit.Db(db => db.Set<PredictionWalletEntity>().SumAsync(w => w.PendingMinor))).Should().Be(0);
        (await CoinBoardAsync()).Should().Be(EmptyBoard, "B: nobody has a settled prediction");

        var preview = await PreviewAsync();
        preview.Result.Succeeded.Should().BeTrue("an empty leaderboard never blocks the end");
        var fields = preview.View!.Embed!.Fields;
        fields.Single(f => f.Name == "Katılımcı").Value.Should().Be("3", "participants: members with an entry");
        fields.Should().ContainSingle(f => f.Name == "📊 Liderlik").Which.Value.Should().Be("Bu turnuvada sonuçlanmış tahmini olan oyuncu bulunmadı.");
        fields.Should().NotContain(f => f.Name == "💰 En Çok TSQ Coin");

        var token = Token(preview, PredictionMessages.EndConfirmPrefix)!;
        var results = await _kit.TogetherAsync(() => _kit.ConfirmEndAsync(Admin(), token), () => _kit.ConfirmEndAsync(Admin(), token));
        results.Count(r => r.Result.Succeeded).Should().Be(1, "G: the new tournament is opened once");
        var tournaments = await TournamentsAsync();
        tournaments.Select(t => (t.Number, t.Status)).Should().Equal((1, PredictionTournamentStatus.Closed), (2, PredictionTournamentStatus.Active));
        (tournaments[0].FinalParticipantCount, tournaments[0].FinalPredictionCount).Should().Be((3, 2));
        (await _kit.CountAsync<PredictionStandingEntity>()).Should().Be(0, "no invented podium");

        await _kit.TickAsync();
        var announcement = _kit.Transport.Messages.Single(m => m.Channel == Commands).Message.Embed!;
        announcement.Title.Should().Be("🏁 TSQ Öngörü · Turnuva 1 Sona Erdi");
        announcement.Description.Should().Be("3 katılımcı · 2 öngörü");
        announcement.Fields.Select(f => (f.Name, f.Value)).Should().Equal(
            ("📊 Liderlik", "Bu turnuvada sonuçlanmış tahmini olan oyuncu bulunmadı."), ("🔄 Yeni Turnuva Başladı", "Herkes yeni turnuvaya 1000 TSQ Coin ile başlar."));

        var next = await _kit.CreatePredictionAsync(title: "Yeni turnuva öngörüsü");
        (await _kit.EnterAsync(Member(1), next, 1, "10")).Result.Succeeded.Should().BeTrue();
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(99_000, "the new tournament starts at 1000");
    }

    [Fact]
    public async Task Withdrawn_entries_leave_an_empty_board_and_the_tournament_can_still_end()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 2, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.WithdrawAsync(Member(1), prediction)).Result.Succeeded.Should().BeTrue();
        (await _kit.WithdrawAsync(Member(2), prediction)).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue(); // settled with no active entry

        (await CoinBoardAsync()).Should().Be(EmptyBoard, "C: a withdrawn entry is no settled prediction");
        (await EndAsync()).Result.Succeeded.Should().BeTrue();
        (await _kit.CountAsync<PredictionStandingEntity>()).Should().Be(0);
    }

    [Fact]
    public async Task An_open_or_a_locked_prediction_blocks_the_end()
    {
        await _kit.ParticipateAsync(1);
        var open = await _kit.CreatePredictionAsync(title: "Açık öngörü");
        (await PreviewAsync()).Result.MessageKey.Should().Be("predictions.tournament.unresolved", "D");

        (await _kit.LockAsync(Creator(), open)).Succeeded.Should().BeTrue();
        var locked = await PreviewAsync();
        (locked.Result.MessageKey, locked.View!.Content).Should().Match<(string, string?)>(r => r.Item1 == "predictions.tournament.unresolved" && r.Item2!.Contains("Açık öngörü"), "E");
        (await TournamentsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task The_podium_lists_only_the_two_members_with_a_settled_entry()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue(); // wins
        (await _kit.EnterAsync(Member(2), prediction, 2, "100")).Result.Succeeded.Should().BeTrue(); // loses
        (await _kit.ClaimDailyAsync(Member(3))).Result.Succeeded.Should().BeTrue(); // daily only
        var refunded = await _kit.CreatePredictionAsync(title: "İade edilecek öngörü");
        (await _kit.EnterAsync(Member(4), refunded, 1, "100")).Result.Succeeded.Should().BeTrue(); // refunded only
        (await _kit.CancelAsync(Creator(), refunded)).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();

        (await EndAsync()).Result.Succeeded.Should().BeTrue();
        var podium = await _kit.Db(db => db.Set<PredictionStandingEntity>().AsNoTracking().OrderBy(s => s.Board).ThenBy(s => s.Rank).ToListAsync());
        podium.Select(s => (s.Board, s.Rank, s.UserId)).Should().Equal(
            (PredictionBoard.Coins, 1, 1UL), (PredictionBoard.Coins, 2, 2UL), (PredictionBoard.Correct, 1, 1UL), (PredictionBoard.Correct, 2, 2UL));
        (await TournamentsAsync())[0].FinalParticipantCount.Should().Be(3, "H/I: three members entered (1, 2, 4); two are ranked");
    }

    [Fact]
    public async Task The_participant_count_is_members_with_an_entry_and_independent_of_the_leaderboard()
    {
        await _kit.ParticipateAsync(1); // settled: ranked
        var open = await _kit.CreatePredictionAsync(title: "Açık kalan öngörü");
        (await _kit.EnterAsync(Member(2), open, 1, "100")).Result.Succeeded.Should().BeTrue(); // pending
        (await _kit.EnterAsync(Member(3), open, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.WithdrawAsync(Member(3), open)).Result.Succeeded.Should().BeTrue(); // withdrawn

        var status = (await _kit.Economy(e => e.TournamentStatusAsync(Member(1), Commands, Ct))).View!.Embed!;
        status.Fields.Single(f => f.Name == "Katılımcı").Value.Should().Be("3", "I: three members entered");
        (await CoinBoardAsync()).Should().Be("🥇 <@1> — **999 TSQ Coin**", "J: the board keeps the settled-entry filter");
    }

    [Fact]
    public async Task An_empty_tournament_is_still_not_ended_but_a_published_and_cancelled_prediction_is_activity()
    {
        await _kit.OpenFormAsync(); // tournament 1, nothing happened
        (await _kit.ClaimDailyAsync(Member(1))).Result.Succeeded.Should().BeTrue(); // a daily reward is no prediction activity
        (await PreviewAsync()).Result.MessageKey.Should().Be("predictions.tournament.no_participants");

        var prediction = await _kit.CreatePredictionAsync(); // published by its creator, nobody entered
        (await _kit.CancelAsync(Creator(), prediction)).Result.Succeeded.Should().BeTrue();
        (await EndAsync()).Result.Succeeded.Should().BeTrue("a published prediction is activity, even with no entry and an empty leaderboard");
    }
}
