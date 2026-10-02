using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Settling with the creator role (any prediction, automatic ones included), lock and cancel unchanged, and the public
/// result announcement in the commands channel: staged with the settlement in one transaction, once per prediction, the
/// winners' net gains and the total payout, pings for exactly the listed winners, a clean "nobody won", bounded parts for
/// many winners, outbox retries and channel failures that never touch the settlement, and no removal by the card cleanup.
/// Real SQLite, real outbox, fake Discord.
/// </summary>
public sealed class PredictionSettlementAnnouncementTests : IAsyncLifetime
{
    private const string Outcomes = "Galatasaray kazanır | 1.85\nBeraberlik | 3.20\nFenerbahçe kazanır | 4.00";

    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await PredictionTestKit.CreateAsync();

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    private List<ToroSquad.Discord.Transport.FakeMessageTransport.FakeMessage> Announcements() =>
        [.. _kit.Transport.Messages.Where(m => m.Channel == Commands && m.Message.Content?.StartsWith("🏆 TSQ Öngörü Sonuçlandı", StringComparison.Ordinal) == true)];

    private Task<int> SettlementOutboxAsync() => _kit.Db(db => db.Outbox.CountAsync(o => o.Kind == PredictionService.KindSettlement));

    /// <summary>Members 1 (100), 2 (200) and 3 (50) on Galatasaray @ 1.85, member 4 (100) on the draw.</summary>
    private async Task<PredictionView> ThreeWinnersAsync(ActorContext? creator = null)
    {
        var prediction = await _kit.CreatePredictionAsync(Outcomes, creator: creator);
        foreach (var (user, outcome, amount) in new (ulong, int, string)[] { (1, 1, "100"), (2, 1, "200"), (3, 1, "50"), (4, 2, "100") })
            (await _kit.EnterAsync(Member(user), prediction, outcome, amount)).Result.Succeeded.Should().BeTrue();
        return prediction;
    }

    // ---- who may settle ----

    [Fact]
    public async Task The_creator_role_settles_its_own_and_another_creators_prediction_administrators_and_the_owner_too()
    {
        var own = await _kit.CreatePredictionAsync(creator: Creator(10));
        (await _kit.SettleAsync(Creator(10), own, 1)).Result.Succeeded.Should().BeTrue("A");
        var others = await ThreeWinnersAsync(Creator(10));
        (await _kit.SettleAsync(Creator(11), others, 1)).Result.Succeeded.Should().BeTrue("B: not their own card");
        ((await _kit.WalletAsync(1))!.BalanceMinor).Should().Be(100_000 - 10_000 + 18_500, "the payout is the normal one");
        var byAdmin = await _kit.CreatePredictionAsync(creator: Creator(10), title: "Yöneticinin sonuçlandıracağı");
        (await _kit.SettleAsync(Admin(), byAdmin, 1)).Result.Succeeded.Should().BeTrue("E: Administrator without the role");
        var byOwner = await _kit.CreatePredictionAsync(creator: Creator(10), title: "Sahibin sonuçlandıracağı");
        (await _kit.SettleAsync(Owner(), byOwner, 1)).Result.Succeeded.Should().BeTrue("F");
        var refused = await _kit.CreatePredictionAsync(creator: Creator(10), title: "Üyenin dokunamayacağı");
        (await _kit.SettleAsync(Member(5), refused, 1)).Result.MessageKey.Should().Be("predictions.not_manager", "D");
        (await _kit.SettleAsync(TestHost.Admin(Guild), refused, 1)).Result.MessageKey.Should().Be("predictions.not_manager", "Manage Server alone is not enough");
    }

    [Fact]
    public async Task A_role_removed_before_the_final_confirmation_refuses_the_settlement()
    {
        var prediction = await ThreeWinnersAsync(Creator(10));
        (await _kit.SettlePreviewAsync(Creator(11), prediction, 1)).Confirm.Should().NotBeNull();
        (await _kit.ConfirmSettleAsync(Member(11), prediction, 1)).Result.MessageKey.Should().Be("predictions.not_manager", "G: the interaction's current roles decide");
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open);
        (await SettlementOutboxAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_creator_role_gains_no_lock_or_cancel_right_on_another_creators_prediction()
    {
        var prediction = await _kit.CreatePredictionAsync(creator: Creator(10));
        (await _kit.LockAsync(Creator(11), prediction)).MessageKey.Should().Be("predictions.not_manager", "H");
        (await _kit.CancelAsync(Creator(11), prediction)).Result.MessageKey.Should().Be("predictions.not_manager");
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open);
    }

    [Fact]
    public async Task The_creator_role_settles_an_automatic_prediction()
    {
        var odds = new FakeFootballOdds();
        odds.Price(odds.Add(1, "Galatasaray", "Fenerbahce", new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero)));
        await using var kit = await AutoFootballKit.CreateAsync(odds, "Live", new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero));
        await kit.PassAsync();
        var auto = (await kit.AutoPredictionsAsync()).Single();
        var view = (await kit.Service(s => s.GetAsync(auto.Id, Ct)))!;
        (await kit.LockAsync(Creator(11), view)).MessageKey.Should().Be("predictions.not_manager", "lock stays Administrator/owner on automatic cards");
        (await kit.SettleAsync(Creator(11), view, 1)).Result.Succeeded.Should().BeTrue("C");
        await kit.TickAsync();
        kit.Transport.Messages.Should().ContainSingle(m => m.Channel == Commands && m.Message.Content!.Contains("Fenerbahçe maç sonucu ne olur?"));
    }

    // ---- the announcement ----

    [Fact]
    public async Task One_unpinged_role_free_announcement_lists_the_winners_net_gains_and_the_total_payout()
    {
        var prediction = await ThreeWinnersAsync();
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        (await SettlementOutboxAsync()).Should().Be(1, "I: staged with the settlement");
        await _kit.TickAsync();

        var post = Announcements().Should().ContainSingle().Subject;
        var lines = post.Message.Content!.Split('\n');
        lines.Should().Contain(["✅ Kazanan Sonuç", "Galatasaray kazanır · 1.85", "🎉 Kazananlar (net kazanç)"]);
        lines.Should().ContainInConsecutiveOrder(["<@2> — +170 TSQ Coin", "<@1> — +85 TSQ Coin", "<@3> — +42,50 TSQ Coin"], "L/P: 100 @ 1.85 nets +85 (185 back)");
        lines.Should().Contain("👥 3 kazanan · 🪙 Toplam ödeme (yatırılan dahil): 647,50 TSQ Coin", "185 + 370 + 92,50 credited");
        lines.Should().Contain("-# TSQ Öngörü #" + prediction.Id);
        post.Message.Content.Should().NotContain("<@4>", "the loser is not listed");
        post.Message.Mentions.Users.Should().BeEquivalentTo([new UserId(1), new UserId(2), new UserId(3)], "N: exactly the winners");
        post.Message.Mentions.Roles.Should().BeEmpty("O: no role");
        post.Message.Mentions.Everyone.Should().BeFalse("O: no @everyone/@here");
        post.Message.Embed.Should().BeNull();
        Shown(_kit.Card(prediction)).Embed!.Title.Should().Be("✅ Sonuçlandı", "the card in the predictions channel is still edited");
    }

    [Fact]
    public async Task A_second_settlement_a_restart_and_an_outbox_retry_never_give_a_second_payout_or_announcement()
    {
        var prediction = await ThreeWinnersAsync();
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        var ledger = await _kit.CountAsync<PredictionLedgerEntity>();
        (await _kit.ConfirmSettleAsync(Creator(), prediction, 2)).Result.MessageKey.Should().Be("predictions.state.already_settled", "J");
        (await _kit.CountAsync<PredictionLedgerEntity>()).Should().Be(ledger);

        _kit.Transport.ScriptSend(() => new SendOutcome.Transient("503"));
        await _kit.TickAsync(); // the first attempt fails
        Announcements().Should().BeEmpty();
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Settled, "R: the settlement stays");

        await using (var restarted = await PredictionTestKit.CreateAsync(TestHost.T0 + TimeSpan.FromMinutes(10), directory: _kit.Host.Directory, transport: _kit.Transport))
        {
            for (var i = 0; i < 3; i++)
                await restarted.TickAsync(TimeSpan.FromMinutes(5));
        }

        await _kit.TickAsync(TimeSpan.FromMinutes(30));
        Announcements().Should().ContainSingle("K: retried until delivered, once");
        (await SettlementOutboxAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Nobody_on_the_winning_outcome_still_gets_an_announcement_without_any_ping()
    {
        var prediction = await ThreeWinnersAsync();
        (await _kit.SettleAsync(Creator(), prediction, 3)).Result.Succeeded.Should().BeTrue();
        await _kit.TickAsync();
        var post = Announcements().Should().ContainSingle().Subject;
        post.Message.Content.Should().Contain("Fenerbahçe kazanır · 4.00\n\n🎉 Kazananlar\nBu öngörüde kazanan olmadı.")
            .And.Contain("👥 0 kazanan · 🪙 Toplam ödeme (yatırılan dahil): 0 TSQ Coin");
        (post.Pinged, post.Message.Mentions.PingsAnything).Should().Be((false, false), "M");
    }

    [Fact]
    public async Task Untrusted_text_never_becomes_a_role_or_everyone_ping()
    {
        var prediction = await _kit.CreatePredictionAsync("@everyone kazanır | 1.50\n<@&123456789012345678> | 2.50", title: "@here kim kazanır <@&123456789012345678>?");
        (await _kit.EnterAsync(Member(1), prediction, 1, "10")).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        await _kit.TickAsync();
        var post = Announcements().Single();
        post.Message.Content.Should().NotContain("@everyone").And.NotContain("@here").And.NotContain("<@&123456789012345678>");
        (post.Message.Mentions.Everyone, post.Message.Mentions.Roles.Count, post.Message.Mentions.Users!.Count).Should().Be((false, 0, 1));
    }

    [Fact]
    public async Task Many_winners_are_listed_in_a_few_bounded_parts_and_the_rest_is_counted_never_dropped()
    {
        var prediction = await _kit.CreatePredictionAsync(Outcomes);
        var winners = PredictionSettlementRenderer.WinnersPerMessage * PredictionSettlementRenderer.MaxMessages + 5; // 80
        for (var user = 1; user <= winners; user++)
            (await _kit.EnterAsync(Member((ulong)(500 + user)), prediction, 1, "1")).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        await _kit.TickAsync();

        var parts = _kit.Transport.Messages.Where(m => m.Channel == Commands).Select(m => m.Message).ToList();
        parts.Should().HaveCount(PredictionSettlementRenderer.MaxMessages);
        parts[0].Content.Should().Contain("👥 80 kazanan");
        parts[1].Content.Should().StartWith("🎉 Kazananlar · 2/5 — TSQ Öngörü #" + prediction.Id);
        parts[^1].Content.Should().EndWith("+ 5 kazanan daha");
        parts.Should().OnlyContain(p => p.Content!.Length <= DiscordLimits.ContentMax && p.Mentions.Users!.Count == PredictionSettlementRenderer.WinnersPerMessage);
        parts.SelectMany(p => p.Mentions.Users!).Distinct().Should().HaveCount(75, "every listed winner pinged once, the 5 counted ones not invented");
        (await SettlementOutboxAsync()).Should().Be(PredictionSettlementRenderer.MaxMessages);
    }

    [Fact]
    public void The_longest_title_label_ids_and_amounts_stay_within_discords_limit()
    {
        var renderer = _kit.Host.Services.GetRequiredService<PredictionSettlementRenderer>();
        var title = string.Concat(Enumerable.Repeat("*_~`|>", 40))[..PredictionRules.TitleMaxLength];
        var label = string.Concat(Enumerable.Repeat("*_~`|>", 20))[..PredictionRules.OutcomeLabelMaxLength];
        var winners = Enumerable.Range(0, 200).Select(i => new SettlementWinner(new UserId(9_223_372_036_854_775_000UL + (ulong)i), 1, Coins.WalletCeilingMinor)).ToList();
        var parts = renderer.Render(new SettlementResult(long.MaxValue, title, label, Odds.MaxX100, winners, Coins.WalletCeilingMinor), "tr");
        parts.Should().HaveCount(PredictionSettlementRenderer.MaxMessages).And.OnlyContain(p => p.Content!.Length <= DiscordLimits.ContentMax);
        parts[^1].Content.Should().EndWith("+ 125 kazanan daha");
    }

    [Fact]
    public async Task A_channel_without_permission_fails_the_announcement_shows_it_in_the_doctor_and_keeps_the_settlement()
    {
        var prediction = await ThreeWinnersAsync();
        for (var i = 0; i < 3; i++)
            _kit.Transport.ScriptSend(() => new SendOutcome.Permanent(PermanentFailureKind.MissingPermissions, "Missing Permissions"));
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        for (var i = 0; i < 3; i++)
            await _kit.TickAsync(TimeSpan.FromMinutes(10));

        Announcements().Should().BeEmpty();
        (await _kit.Db(db => db.Outbox.Where(o => o.Kind == PredictionService.KindSettlement).Select(o => o.Status).ToListAsync())).Should().Equal(OutboxStatus.Failed);
        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.PayoutTotalMinor).Should().Be((PredictionStatus.Settled, (long?)64_750), "S");
        var report = await _kit.Host.Services.GetServices<IModuleHealthCheck>().Single(c => c.Module.Value == "predictions").CheckAsync(Ct);
        report.Entries.Should().Contain(e => e.Component == "predictions.health.announcements" && e.State == HealthState.Degraded);
    }

    [Fact]
    public async Task The_card_cleanup_removes_the_prediction_card_but_never_the_announcement()
    {
        var prediction = await ThreeWinnersAsync();
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
        await _kit.TickAsync();
        var announcement = Announcements().Single().Id;

        await _kit.TickAsync(TimeSpan.FromHours(12));
        _kit.Transport.Messages.Should().NotContain(m => m.Id == prediction.Message, "T: the card is removed");
        _kit.Transport.Messages.Should().Contain(m => m.Id == announcement, "the announcement stays");
        _kit.Transport.DeleteCalls.Should().Be(1);
    }
}
