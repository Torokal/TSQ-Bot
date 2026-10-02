using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// /ongoru liderlik's private note on the real SQLite database: the asking member's own rank on each board where they are
/// outside the public Top 10 (nothing when they are in it on both, a plain "not ranked yet" without any number when
/// nothing of theirs is settled), computed from live wealth with exactly the board orders (also at the #10/#11 border and
/// with ties), by counting in SQLite — never the whole tournament in memory. SYNTHETIC wallet counters.
/// </summary>
public sealed class PredictionLeaderboardRankTests : IAsyncLifetime
{
    private readonly SqlLog _sql = new();
    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() =>
        _kit = await PredictionTestKit.CreateAsync(replace: s => s.ConfigureDbContext<ToroDbContext>(o => o.AddInterceptors(_sql)));

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    /// <summary>Every SQL command the database runs (the text only).</summary>
    private sealed class SqlLog : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Eligible wallets (Settled ≥ 1) in the active tournament: (user, available, open principal, correct, settled).</summary>
    private async Task<long> SeedAsync(params (ulong User, long Available, long Pending, int Correct, int Settled)[] wallets)
    {
        await _kit.OpenFormAsync();
        var tournament = await _kit.Db(db => db.Set<PredictionTournamentEntity>().Where(t => t.Status == PredictionTournamentStatus.Active).Select(t => t.Id).SingleAsync());
        await _kit.Db(async db =>
        {
            foreach (var w in wallets)
            {
                db.Add(new PredictionWalletEntity
                {
                    TournamentId = tournament,
                    GuildId = Guild.Value,
                    UserId = w.User,
                    BalanceMinor = w.Available * 100,
                    PendingMinor = w.Pending * 100,
                    CorrectCount = w.Correct,
                    SettledCount = w.Settled,
                    CreatedAt = TestHost.T0,
                    UpdatedAt = TestHost.T0,
                });
            }

            return await db.SaveChangesAsync();
        });
        return tournament;
    }

    private Task<PredictionReply> AskAsync(ulong user) => _kit.Economy(e => e.LeaderboardAsync(Member(user), Commands, Ct));

    private static IReadOnlyList<(string Name, string Value)> Note(PredictionReply reply)
    {
        reply.Private!.Embed!.Title.Should().Be("📊 Senin Sıralaman");
        reply.Private.Mentions.Should().Be(MentionPolicy.None);
        return reply.Private.Embed.Fields.Select(f => (f.Name, f.Value)).ToList();
    }

    /// <summary>Users 1..n, user i with live wealth 3000 − 10·i coins (coin rank i), correct/settled from <paramref name="correct"/>.</summary>
    private Task<long> SeedRanksAsync(int n, Func<int, (int Correct, int Settled)> correct) =>
        SeedAsync([.. Enumerable.Range(1, n).Select(i => ((ulong)i, 3000L - 10 * i, 0L, correct(i).Correct, correct(i).Settled))]);

    [Fact]
    public async Task Fifteen_eligible_members_show_ten_per_board_and_coin_14_sees_only_its_coin_rank()
    {
        // Correct board: user 14 first (9 of 9), everyone else 0 of 1 (then by wealth).
        await SeedRanksAsync(15, i => i == 14 ? (9, 9) : (0, 1));
        var reply = await AskAsync(14);
        reply.View!.Embed!.Fields[0].Value.Split('\n').Should().HaveCount(10, "S: Top 10 only");
        reply.View.Embed.Fields[1].Value.Split('\n').Should().HaveCount(10);
        reply.View.Embed.Fields[0].Value.Should().NotContain("<@14>");
        reply.Public.Should().BeTrue();
        Note(reply).Should().Equal([("💰 TSQ Coin", "#14 · **2860 TSQ Coin**")], "T: the correct board already shows them first");
    }

    [Fact]
    public async Task Correct_17_sees_only_its_correct_rank_with_the_board_wording()
    {
        // Correct board: users 1..16 have 2 of 2, user 17 has 1 of 2 — 17th; coin rank 3 for user 17 needs it rich: use user 3.
        await SeedRanksAsync(20, i => i == 3 ? (1, 2) : i <= 17 ? (2, 2) : (0, 1));
        Note(await AskAsync(3)).Should().Equal([("🎯 Doğru Tahmin", "#17 · **1** doğru / 2 sonuçlanan (%50)")], "U and W: coin #3 is on the public card");
    }

    [Fact]
    public async Task Coin_7_and_correct_18_sees_only_the_correct_rank()
    {
        // 17 others have 1 correct; among the 0-correct ones user 7 is the richest: correct #18.
        await SeedRanksAsync(20, i => i == 7 ? (0, 3) : i <= 18 ? (1, 1) : (0, 1));
        Note(await AskAsync(7)).Should().Equal([("🎯 Doğru Tahmin", "#18 · **0** doğru / 3 sonuçlanan (%0)")], "V: coin #7 is on the public card");
    }

    [Fact]
    public async Task Outside_both_top_10s_shows_both_ranks_and_inside_both_shows_nothing()
    {
        await SeedRanksAsync(20, i => (20 - i, 20)); // user i: correct 20 − i → correct rank i as well
        Note(await AskAsync(14)).Should().Equal([("💰 TSQ Coin", "#14 · **2860 TSQ Coin**"), ("🎯 Doğru Tahmin", "#14 · **6** doğru / 20 sonuçlanan (%30)")]);
        (await AskAsync(10)).Private.Should().BeNull("X: #10 on both boards is on the public card");
        (await AskAsync(11)).Private.Should().NotBeNull();
    }

    [Fact]
    public async Task A_member_with_nothing_settled_gets_the_not_ranked_note_without_any_number()
    {
        await SeedRanksAsync(3, _ => (0, 1));
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(50), prediction, 1, "100")).Result.Succeeded.Should().BeTrue(); // open entry only
        (await _kit.ClaimDailyAsync(Member(50))).Result.Succeeded.Should().BeTrue(); // and a daily reward

        var reply = await AskAsync(50);
        reply.View!.Embed!.Fields[0].Value.Split('\n').Should().HaveCount(3, "the public card is sent as usual");
        reply.Private!.Embed.Should().BeNull();
        reply.Private.Content.Should().Be("Henüz liderlik sıralamasında değilsin. Sıralamaya girmek için aktif turnuvada en az bir tahmininin sonuçlanması gerekiyor.");
        reply.Private.Content.Should().NotContain("#");
    }

    [Fact]
    public async Task The_personal_rank_uses_live_wealth_not_the_available_balance()
    {
        await SeedAsync([
            .. Enumerable.Range(1, 12).Select(i => ((ulong)i, 1200L + i, 0L, 0, 1)), // 12 above
            .. Enumerable.Range(20, 5).Select(i => ((ulong)i, 1050L, 0L, 0, 1)), // above 600, below 1100
            (40UL, 600L, 500L, 1, 1)]); // 600 available + 500 in open predictions = 1100; the only correct one (correct #1)
        Note(await AskAsync(40)).Should().ContainSingle().Which.Should().Be(("💰 TSQ Coin", "#13 · **1100 TSQ Coin**"), "AA: by available coins it would be #18");
    }

    [Fact]
    public async Task Every_members_personal_rank_matches_their_place_on_the_full_board_also_with_ties_and_at_10_11()
    {
        // 40 members with many ties in every key (wealth, correct, settled): the count predicates must reproduce the orders exactly.
        var tournament = await SeedAsync([.. Enumerable.Range(1, 40).Select(i =>
        {
            var settled = 1 + i % 4;
            return ((ulong)(100 + (i * 7 % 40)), 1000L + 500 * (i % 3), 100L * (i % 2), Math.Min(i % 3, settled), settled);
        })]);
        var coins = await _kit.Economy(e => e.CoinBoardAsync(tournament, 100, Ct));
        var correct = await _kit.Economy(e => e.CorrectBoardAsync(tournament, 100, Ct));
        coins.Should().HaveCount(40);
        foreach (var row in coins)
        {
            var mine = (await _kit.Economy(e => e.PersonalRankAsync(tournament, row.User, Ct)))!;
            (mine.Coin.Rank, mine.Correct.Rank).Should().Be((row.Rank, correct.Single(c => c.User == row.User).Rank), "user {0}", row.User);
        }

        var tenth = coins[9].User.Value;
        var eleventh = coins[10].User.Value;
        (await AskAsync(tenth)).Private?.Embed?.Fields.Should().NotContain(f => f.Name == "💰 TSQ Coin", "Z: the 10th is on the public card");
        Note(await AskAsync(eleventh)).Should().Contain(f => f.Name == "💰 TSQ Coin" && f.Value.StartsWith("#11 ·"));
    }

    [Fact]
    public async Task With_500_eligible_members_the_boards_and_the_personal_rank_are_a_few_bounded_queries()
    {
        await SeedAsync([.. Enumerable.Range(1, 500).Select(i => ((ulong)i, 100_000L - i, 0L, i % 7, 7))]);
        _sql.Commands.Clear();
        var reply = await AskAsync(437);

        Note(reply).Should().Contain(("💰 TSQ Coin", "#437 · **99.563 TSQ Coin**"));
        var commands = _sql.Commands.ToList();
        commands.Count.Should().BeLessThanOrEqualTo(12, "no query per member");
        foreach (var sql in commands.Where(c => c.Contains("prediction_wallet", StringComparison.Ordinal)))
            sql.Should().Match(c => c.Contains("COUNT(", StringComparison.Ordinal) || c.Contains("LIMIT", StringComparison.Ordinal), "never the whole tournament: {0}", sql);
    }
}
