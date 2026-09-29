using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Öngörü create → publish → enter → lock → settle/cancel on the real SQLite database and the production wiring: the
/// channel and role gates (and that a refusal changes nothing), the form and the single card, the atomic entry, the exact
/// deadline, settlement and refund arithmetic, the races that must never pay twice, deleted and failing cards, restarts,
/// and module disable/enable. Concurrency tests start their actions together on separate scopes (own DbContext and
/// connection) — no sleeps, no "ran 100 times".
/// </summary>
public sealed class PredictionLifecycleTests : IAsyncLifetime
{
    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await PredictionTestKit.CreateAsync();

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    private async Task AssertNothingStoredAsync()
    {
        (await _kit.CountAsync<PredictionTournamentEntity>()).Should().Be(0);
        (await _kit.CountAsync<PredictionWalletEntity>()).Should().Be(0);
        (await _kit.CountAsync<PredictionEntity>()).Should().Be(0);
        (await _kit.CountAsync<PredictionLedgerEntity>()).Should().Be(0);
        (await _kit.CountAsync<PredictionDailyClaimEntity>()).Should().Be(0);
        _kit.Transport.Messages.Should().BeEmpty();
    }

    // ---- channel and role gates ----

    [Fact]
    public async Task Creating_is_refused_outside_the_predictions_channel_in_threads_and_without_the_role_and_changes_nothing()
    {
        async Task<OperationResult?> Open(ActorContext actor, ChannelId channel) =>
            (await _kit.Service(s => s.OpenFormAsync(actor, channel, Ct))).Refusal;

        var wrong = await Open(Creator(), Commands);
        wrong!.MessageKey.Should().Be("predictions.wrong_channel");
        wrong.Args.Should().Equal("<#1048525775919390840>");
        (await Open(Creator(), Elsewhere))!.MessageKey.Should().Be("predictions.wrong_channel", "a thread has its own id, never its parent's");
        (await Open(Admin(), Commands))!.MessageKey.Should().Be("predictions.wrong_channel", "the channel applies to administrators too");

        var role = await Open(Admin(), Predictions);
        role!.MessageKey.Should().Be("predictions.missing_role", "Administrator alone does not grant creating");
        role.Args.Should().Equal("<@&1233057768408350741>");
        (await Open(Owner(), Predictions))!.MessageKey.Should().Be("predictions.missing_role");
        (await Open(Member(), Predictions))!.MessageKey.Should().Be("predictions.missing_role");

        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Member_commands_answer_only_in_the_commands_channel_privately_and_change_nothing()
    {
        var replies = new[]
        {
            await _kit.Economy(e => e.WalletAsync(Member(), Predictions, Ct)),
            await _kit.Economy(e => e.ClaimDailyAsync(Member(), Predictions, Ct)),
            await _kit.Economy(e => e.MyEntriesAsync(Member(), Elsewhere, 0, Ct)),
            await _kit.Economy(e => e.LeaderboardAsync(Member(), Predictions, Ct)),
            await _kit.Economy(e => e.TournamentStatusAsync(Member(), Elsewhere, Ct)),
            await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Predictions, Ct)),
        };
        foreach (var reply in replies)
        {
            reply.Result.MessageKey.Should().Be("predictions.wrong_channel");
            reply.Result.Args.Should().Equal("<#689814679056547857>");
            reply.View.Should().BeNull();
            reply.Public.Should().BeFalse("a refusal is never public");
        }

        _kit.Random.Calls.Should().BeEmpty();
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Another_guilds_prediction_or_a_plain_message_is_not_found()
    {
        var prediction = await _kit.CreatePredictionAsync();
        await _kit.Host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(OtherGuild), "predictions", true, Ct)).Succeeded.Should().BeTrue());
        var outsider = Creator(10, OtherGuild);

        (await _kit.Service(s => s.LockAsync(outsider, Predictions, "#" + prediction.Id, Ct))).MessageKey.Should().Be("predictions.not_found");
        var link = $"https://discord.com/channels/{OtherGuild.Value}/{Predictions.Value}/{prediction.Message!.Value.Value}";
        (await _kit.Service(s => s.LockAsync(Admin(), Predictions, link, Ct))).MessageKey.Should().Be("predictions.not_found", "a link to another guild");
        (await _kit.Service(s => s.LockAsync(Admin(), Predictions, "1234567890123456789", Ct))).MessageKey.Should().Be("predictions.not_found", "a normal message");
        (await _kit.Service(s => s.StartEntryAsync(outsider, Predictions, prediction.Id, prediction.Outcomes[0].Id.ToString(), Ct))).Refusal!.MessageKey
            .Should().Be("predictions.not_found");
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open);
    }

    [Fact]
    public async Task The_creator_role_is_checked_again_on_submit_and_publish()
    {
        var draft = await _kit.OpenFormAsync();
        var withoutRole = Member(10); // the same member after the role was removed
        (await _kit.SubmitAsync(draft, actor: withoutRole)).Result.MessageKey.Should().Be("predictions.missing_role");
        (await _kit.SubmitAsync(draft)).Result.Succeeded.Should().BeTrue();
        (await _kit.PublishAsync(draft, withoutRole)).Result.MessageKey.Should().Be("predictions.missing_role");
        (await _kit.CountAsync<PredictionEntity>()).Should().Be(0);
        _kit.Transport.Messages.Should().BeEmpty();

        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.publish.done", "the refused publish kept the draft");
    }

    [Fact]
    public async Task Managing_is_for_the_creator_with_the_role_or_administrators()
    {
        var prediction = await _kit.CreatePredictionAsync(creator: Creator(10));

        (await _kit.Service(s => s.LockAsync(Creator(11), Predictions, "#" + prediction.Id, Ct))).MessageKey.Should().Be("predictions.not_manager");
        (await _kit.Service(s => s.LockAsync(Member(10), Predictions, "#" + prediction.Id, Ct))).MessageKey.Should().Be("predictions.missing_role", "role removed");
        (await _kit.Service(s => s.LockAsync(Admin(), Commands, "#" + prediction.Id, Ct))).MessageKey.Should().Be("predictions.wrong_channel");
        (await _kit.Service(s => s.StartSettleAsync(Creator(11), Predictions, "#" + prediction.Id, Ct))).Result.MessageKey.Should().Be("predictions.not_manager");
        (await _kit.Service(s => s.PreviewCancelAsync(Creator(11), Predictions, "#" + prediction.Id, "yanlış", Ct))).Result.MessageKey.Should().Be("predictions.not_manager");
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open);

        (await _kit.Service(s => s.LockAsync(Owner(), Predictions, "#" + prediction.Id, Ct))).MessageKey.Should().Be("predictions.lock.done");
    }

    [Fact]
    public async Task A_settle_confirmation_is_refused_when_the_role_was_removed_meanwhile()
    {
        var prediction = await _kit.CreatePredictionAsync(creator: Creator(10));
        var token = await _kit.SettleTokenAsync(Creator(10), prediction, 1);
        (await _kit.Service(s => s.ConfirmSettleAsync(Member(10), Predictions, token!, Ct))).Result.MessageKey.Should().Be("predictions.missing_role");
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open);
    }

    // ---- create ----

    [Fact]
    public async Task Publishing_posts_one_card_and_a_second_click_publishes_nothing()
    {
        var draft = await _kit.OpenFormAsync();
        var preview = await _kit.SubmitAsync(draft, rules: "Normal sürenin sonucu esas alınır; uzatmalar dahil değildir.");
        preview.View!.Embed!.Title.Should().Be("👀 Önizleme");
        preview.View.Content.Should().Contain("Sabit oran");
        preview.View.Buttons!.Select(b => b.CustomId).Should().Equal(PredictionMessages.PublishPrefix + draft, PredictionMessages.EditPrefix + draft,
            PredictionMessages.DiscardPrefix + draft);
        _kit.Transport.Messages.Should().BeEmpty("the preview is private; nothing is public before Yayımla");

        var published = await _kit.PublishAsync(draft);
        published.Result.MessageKey.Should().Be("predictions.publish.done");
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.form.unavailable");

        var card = _kit.Transport.Messages.Should().ContainSingle().Subject;
        card.Channel.Should().Be(Predictions);
        card.Pinged.Should().BeFalse();
        card.Message.Embed!.Description.Should().StartWith("## Galatasaray \\- Fenerbahçe Maç Sonucu Ne Olur?\n**1.** Galatasaray Kazanır — **1.10**");
        card.Message.Select!.Options.Select(o => o.Label).Should().Equal("1. Galatasaray Kazanır", "2. Beraberlik", "3. Fenerbahçe Kazanır");
        var row = await _kit.Db(db => db.Set<PredictionEntity>().AsNoTracking().SingleAsync());
        (row.Status, row.MessageId, row.Rules).Should().Be((PredictionStatus.Open, card.Id.Value, "Normal sürenin sonucu esas alınır; uzatmalar dahil değildir."));
        (await _kit.Db(db => db.Set<PredictionTournamentEntity>().SingleAsync())).Number.Should().Be(1);
    }

    [Fact]
    public async Task Two_simultaneous_publish_clicks_create_one_prediction_and_one_card()
    {
        var draft = await _kit.OpenFormAsync();
        (await _kit.SubmitAsync(draft)).Result.Succeeded.Should().BeTrue();
        var results = await _kit.TogetherAsync(() => _kit.PublishAsync(draft), () => _kit.PublishAsync(draft));
        results.Count(r => r.Result.MessageKey == "predictions.publish.done").Should().Be(1);
        (await _kit.CountAsync<PredictionEntity>()).Should().Be(1);
        _kit.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Form_errors_keep_everything_typed_and_the_form_reopens_with_it()
    {
        var draft = await _kit.OpenFormAsync();
        var outcomes = "A | 1.10\nA | 2\nB | 1,005";
        var reply = await _kit.SubmitAsync(draft, "Kim", outcomes, "31.02.2026 20:00", "kural");
        reply.Result.Succeeded.Should().BeFalse();
        reply.View!.Content.Should().Contain("**Başlık**").And.Contain("satır 2").And.Contain("satır 3").And.Contain("**Otomatik kilitlenme**");
        reply.View.Buttons!.Select(b => b.CustomId).Should().Equal(PredictionMessages.EditPrefix + draft, PredictionMessages.DiscardPrefix + draft);
        _kit.Host.Services.GetRequiredService<PredictionTokens>().Get<FormDraftStep>(draft, Creator())!.Values
            .Should().Be(new PredictionFormValues("Kim", outcomes, "31.02.2026 20:00", "kural"));

        (await _kit.SubmitAsync(draft, outcomes: "A | 1.10\nB")).View!.Content.Should().Contain("**2.00**", "the default odds are named in the preview");
    }

    [Fact]
    public async Task The_lock_time_is_checked_again_when_publishing()
    {
        var draft = await _kit.OpenFormAsync();
        (await _kit.SubmitAsync(draft, lockAt: "24.09.2026 15:03")).Result.Succeeded.Should().BeTrue(); // T0 is 15:00 in Türkiye
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(5));
        var reply = await _kit.PublishAsync(draft);
        reply.View!.Content.Should().Contain("gelecekte olmalı");
        (await _kit.CountAsync<PredictionEntity>()).Should().Be(0);
    }

    [Fact]
    public async Task A_draft_never_moves_into_a_new_tournament()
    {
        var draft = await _kit.OpenFormAsync();
        (await _kit.Economy(e => e.ClaimDailyAsync(Member(), Commands, Ct))).Result.Succeeded.Should().BeTrue();
        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.MessageKey.Should().Be("predictions.tournament.ended");

        (await _kit.SubmitAsync(draft)).Result.Succeeded.Should().BeTrue();
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.form.tournament_changed");
        (await _kit.CountAsync<PredictionEntity>()).Should().Be(0);
    }

    [Fact]
    public async Task An_uncertain_post_that_reached_discord_is_found_and_never_sent_twice()
    {
        _kit.Transport.ScriptAcceptedButTimedOut();
        var prediction = await _kit.CreatePredictionAsync();
        _kit.Transport.Messages.Should().ContainSingle();
        prediction.Status.Should().Be(PredictionStatus.Open);
    }

    [Fact]
    public async Task An_uncertain_post_that_cannot_be_found_is_abandoned_without_entries_and_stops_blocking()
    {
        _kit.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        var draft = await _kit.OpenFormAsync();
        (await _kit.SubmitAsync(draft)).Result.Succeeded.Should().BeTrue();
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.publish.uncertain");
        var row = await _kit.Db(db => db.Set<PredictionEntity>().AsNoTracking().SingleAsync());
        row.Status.Should().Be(PredictionStatus.Publishing);
        var outcome = await _kit.Db(db => db.Set<PredictionOutcomeEntity>().Where(o => o.PredictionId == row.Id).Select(o => o.Id).FirstAsync());
        (await _kit.Service(s => s.StartEntryAsync(Member(), Predictions, row.Id, outcome.ToString(), Ct))).Refusal!.MessageKey.Should().Be("predictions.entry.publishing");
        (await _kit.Economy(e => e.ClaimDailyAsync(Member(), Commands, Ct))).Result.Succeeded.Should().BeTrue();
        (await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct))).Result.MessageKey.Should().Be("predictions.tournament.unresolved");

        await _kit.TickAsync(TimeSpan.FromMinutes(2));
        (await _kit.RowAsync(row.Id)).Status.Should().Be(PredictionStatus.Publishing);
        await _kit.TickAsync(TimeSpan.FromMinutes(10));
        (await _kit.RowAsync(row.Id)).Status.Should().Be(PredictionStatus.Abandoned);
        _kit.Transport.Messages.Should().BeEmpty("never posted a second time");
        (await _kit.EndTokenAsync(Admin())).Should().NotBeNull("an abandoned prediction does not block the tournament");
    }

    [Fact]
    public async Task A_refused_card_stores_nothing_and_keeps_the_draft()
    {
        _kit.Transport.ScriptSend(() => new SendOutcome.Permanent(PermanentFailureKind.MissingPermissions, "50013"));
        var draft = await _kit.OpenFormAsync();
        (await _kit.SubmitAsync(draft)).Result.Succeeded.Should().BeTrue();
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.publish.failed");
        (await _kit.CountAsync<PredictionEntity>()).Should().Be(0);
        (await _kit.CountAsync<PredictionOutcomeEntity>()).Should().Be(0);
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.publish.done");
    }

    // ---- entries ----

    [Fact]
    public async Task An_entry_debits_once_books_the_ledger_snapshots_the_odds_and_updates_the_card()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var (token, preview) = await _kit.PreviewEntryAsync(Member(), prediction, 1, "100");
        preview.View!.Embed!.Fields.Select(f => f.Value).Should().Contain("110 TSQ Coin").And.Contain("10 TSQ Coin").And.Contain("900 TSQ Coin");
        (await _kit.WalletAsync(Member().UserId.Value)).Should().BeNull("nothing is debited or created before Onayla");

        var receipt = await _kit.ConfirmEntryAsync(Member(), token!);
        receipt.Result.MessageKey.Should().Be("predictions.entry.done");
        receipt.View!.Embed!.Fields.Single(f => f.Name == "Kullanılabilir bakiye").Value.Should().Be("900 TSQ Coin");
        (await _kit.ConfirmEntryAsync(Member(), token!)).Result.MessageKey.Should().Be("predictions.entry.expired", "a token is single use");

        var wallet = (await _kit.WalletAsync(Member().UserId.Value))!;
        (wallet.BalanceMinor, wallet.PendingMinor).Should().Be((90_000L, 10_000L));
        var entry = (await _kit.EntriesAsync(prediction.Id)).Should().ContainSingle().Subject;
        (entry.StakeMinor, entry.OddsX100, entry.PotentialPayoutMinor, entry.Status).Should().Be((10_000L, 110, 11_000L, PredictionEntryStatus.Pending));
        (await _kit.LedgerAsync(Member().UserId.Value)).Select(l => (l.Kind, l.AmountMinor, l.BalanceAfterMinor)).Should().Equal(
            (PredictionLedgerKind.Initial, 100_000L, 100_000L), (PredictionLedgerKind.Stake, -10_000L, 90_000L));

        await _kit.TickAsync(TimeSpan.FromSeconds(11));
        Shown(_kit.Card(prediction)).Embed!.Fields.Should().Contain(f => f.Value == "1 katılımcı\n100 TSQ Coin yatırıldı");
        _kit.Transport.Messages.Should().ContainSingle("no new message or DM per entry");
    }

    [Fact]
    public async Task Stake_rules_are_enforced_before_anything_is_debited()
    {
        var prediction = await _kit.CreatePredictionAsync();
        async Task<string> Key(string amount) => (await _kit.PreviewEntryAsync(Member(), prediction, 2, amount)).Reply.Result.MessageKey;

        (await Key("0,5")).Should().Be("predictions.entry.amount_minimum");
        (await Key("12,345")).Should().Be("predictions.entry.amount_decimals");
        (await Key("abc")).Should().Be("predictions.entry.amount_format");
        (await Key("-10")).Should().Be("predictions.entry.amount_format");
        (await Key("1000,01")).Should().Be("predictions.entry.insufficient");
        (await Key("1000")).Should().Be("predictions.entry.preview");
        (await _kit.CountAsync<PredictionWalletEntity>()).Should().Be(0);
    }

    [Fact]
    public async Task Two_simultaneous_confirmations_for_one_prediction_make_one_entry()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var (first, _) = await _kit.PreviewEntryAsync(Member(), prediction, 1, "100");
        var (second, _) = await _kit.PreviewEntryAsync(Member(), prediction, 2, "200");
        var results = await _kit.TogetherAsync(() => _kit.ConfirmEntryAsync(Member(), first!), () => _kit.ConfirmEntryAsync(Member(), second!));

        results.Count(r => r.Result.Succeeded).Should().Be(1);
        results.Single(r => !r.Result.Succeeded).Result.MessageKey.Should().Be("predictions.entry.already");
        var entry = (await _kit.EntriesAsync(prediction.Id)).Should().ContainSingle().Subject;
        var wallet = (await _kit.WalletAsync(Member().UserId.Value))!;
        (wallet.BalanceMinor + wallet.PendingMinor).Should().Be(100_000);
        wallet.PendingMinor.Should().Be(entry.StakeMinor);
        (await _kit.LedgerAsync(Member().UserId.Value)).Count(l => l.Kind == PredictionLedgerKind.Initial).Should().Be(1);

        var again = await _kit.PreviewEntryAsync(Member(), prediction, 3, "10");
        again.Reply.Result.MessageKey.Should().Be("predictions.entry.already_chosen");
    }

    [Fact]
    public async Task Simultaneous_entries_in_two_predictions_cannot_spend_more_than_the_balance()
    {
        var p1 = await _kit.CreatePredictionAsync();
        var p2 = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        var (t1, _) = await _kit.PreviewEntryAsync(Member(), p1, 1, "700");
        var (t2, _) = await _kit.PreviewEntryAsync(Member(), p2, 1, "700");
        var results = await _kit.TogetherAsync(() => _kit.ConfirmEntryAsync(Member(), t1!), () => _kit.ConfirmEntryAsync(Member(), t2!));

        results.Count(r => r.Result.Succeeded).Should().Be(1);
        results.Single(r => !r.Result.Succeeded).Result.MessageKey.Should().Be("predictions.entry.insufficient");
        var wallet = (await _kit.WalletAsync(Member().UserId.Value))!;
        (wallet.BalanceMinor, wallet.PendingMinor).Should().Be((30_000L, 70_000L));
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_entry_a_negative_balance_and_a_foreign_outcome()
    {
        var p1 = await _kit.CreatePredictionAsync();
        var p2 = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        (await _kit.EnterAsync(Member(), p1, 1, "100")).Result.Succeeded.Should().BeTrue();
        var entry = (await _kit.EntriesAsync(p1.Id)).Single();

        async Task<Exception?> TryAsync(Func<ToroDbContext, Task> write) => await _kit.Db(async db =>
        {
            try
            {
                await write(db);
                return (Exception?)null;
            }
            catch (DbUpdateException ex)
            {
                return ex;
            }
        });

        PredictionEntryEntity Copy(long outcome, long prediction) => new()
        {
            PredictionId = prediction,
            OutcomeId = outcome,
            TournamentId = entry.TournamentId,
            WalletId = entry.WalletId,
            GuildId = entry.GuildId,
            UserId = entry.UserId,
            StakeMinor = 100,
            OddsX100 = 200,
            PotentialPayoutMinor = 200,
            CreatedAt = entry.CreatedAt,
        };

        (await TryAsync(async db =>
        {
            db.Add(Copy(p1.Outcomes[1].Id, p1.Id));
            await db.SaveChangesAsync();
        })).Should().NotBeNull("one entry per member and prediction");
        (await TryAsync(async db =>
        {
            db.Add(Copy(p1.Outcomes[0].Id, p2.Id));
            await db.SaveChangesAsync();
        })).Should().NotBeNull("the outcome must belong to the prediction");
        (await TryAsync(async db =>
        {
            var wallet = await db.Set<PredictionWalletEntity>().SingleAsync(w => w.Id == entry.WalletId);
            wallet.BalanceMinor = -1;
            await db.SaveChangesAsync();
        })).Should().NotBeNull("no negative balance");
    }

    [Fact]
    public async Task The_entry_is_checked_inside_the_write_lock_so_a_change_committed_meanwhile_wins()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var (token, _) = await _kit.PreviewEntryAsync(Member(), prediction, 1, "100");
        var connectionString = await _kit.Db(db => Task.FromResult(db.Database.GetConnectionString()!));

        // Another writer holds the database write lock and closes the prediction; the confirmation starts meanwhile.
        await using var blocker = new SqliteConnection(connectionString);
        await blocker.OpenAsync(TestContext.Current.CancellationToken);
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE; UPDATE prediction SET Status = 2 WHERE Id = " + prediction.Id + ";";
            await begin.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var confirm = Task.Run(() => _kit.ConfirmEntryAsync(Member(), token!));
        await using (var commit = blocker.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await confirm).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.EntriesAsync(prediction.Id)).Should().BeEmpty();
        (await _kit.WalletAsync(Member().UserId.Value)).Should().BeNull("a refused entry creates no wallet");
    }

    [Fact]
    public async Task The_deadline_is_exact_and_never_waits_for_the_worker()
    {
        var prediction = await _kit.CreatePredictionAsync(lockAt: "24.09.2026 16:00"); // 13:00 UTC, one hour after T0
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(59));
        var (token, _) = await _kit.PreviewEntryAsync(Member(), prediction, 1, "100");
        token.Should().NotBeNull();

        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(1)); // exactly the lock time; no worker pass has run
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open);
        (await _kit.ConfirmEntryAsync(Member(), token!)).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.PreviewEntryAsync(Member(), prediction, 1, "100")).Reply.Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.WalletAsync(Member().UserId.Value)).Should().BeNull();

        await _kit.TickAsync();
        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.LockReason).Should().Be((PredictionStatus.Locked, PredictionLockReason.Deadline));
        Shown(_kit.Card(prediction)).Select!.Disabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_form_opened_before_a_manual_lock_cannot_be_confirmed_after_it()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var (token, _) = await _kit.PreviewEntryAsync(Member(), prediction, 1, "100");
        (await _kit.Service(s => s.LockAsync(Creator(), Predictions, "#" + prediction.Id, Ct))).MessageKey.Should().Be("predictions.lock.done");
        (await _kit.ConfirmEntryAsync(Member(), token!)).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.Service(s => s.LockAsync(Creator(), Predictions, "#" + prediction.Id, Ct))).MessageKey.Should().Be("predictions.state.already_locked");
    }

    [Fact]
    public async Task After_a_restart_passed_deadlines_are_locked_old_confirmations_are_gone_and_existing_cards_still_work()
    {
        var due = await _kit.CreatePredictionAsync(lockAt: "24.09.2026 16:00");
        var open = await _kit.CreatePredictionAsync(title: "Kilidi olmayan öngörü");
        var (token, _) = await _kit.PreviewEntryAsync(Member(), open, 1, "100");

        await using var second = await PredictionTestKit.CreateAsync(TestHost.T0.AddHours(2), directory: _kit.Host.Directory, transport: _kit.Transport);
        await second.TickAsync();
        (await second.RowAsync(due.Id)).Status.Should().Be(PredictionStatus.Locked);
        (await second.ConfirmEntryAsync(Member(), token!)).Result.MessageKey.Should().Be("predictions.entry.expired", "drafts and confirmations are memory only");
        (await second.EnterAsync(Member(), open, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.done", "the card's select works after a restart");
        (await second.EnterAsync(Member(2), due, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
    }

    // ---- settle / cancel ----

    [Fact]
    public async Task Settling_pays_stake_times_odds_exactly_once_and_counts_each_prediction_once()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 2, "50")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(3), prediction, 1, "12,5")).Result.Succeeded.Should().BeTrue();

        var token = await _kit.SettleTokenAsync(Admin(), prediction, 1);
        var preview = await _kit.Service(s => s.PreviewSettleAsync(Admin(), Predictions, token!, prediction.Outcomes[0].Id.ToString(), Ct));
        preview.View!.Embed!.Fields.Select(f => f.Value).Should().Contain("2").And.Contain("1").And.Contain("123,75 TSQ Coin");
        var done = await _kit.Service(s => s.ConfirmSettleAsync(Admin(), Predictions, token!, Ct));
        done.Result.MessageKey.Should().Be("predictions.settle.done");
        done.Result.Args.Should().Equal(prediction.Id.ToString(), 2, "123,75");

        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(101_000, "1000 − 100 + 110: the 110 includes the stake");
        (await _kit.WalletAsync(2))!.BalanceMinor.Should().Be(95_000, "the stake was debited once, on entry");
        (await _kit.WalletAsync(3))!.BalanceMinor.Should().Be(100_125);
        foreach (var user in new ulong[] { 1, 2, 3 })
            (await _kit.WalletAsync(user))!.PendingMinor.Should().Be(0);
        ((await _kit.WalletAsync(1))!.CorrectCount, (await _kit.WalletAsync(1))!.SettledCount).Should().Be((1, 1));
        ((await _kit.WalletAsync(2))!.CorrectCount, (await _kit.WalletAsync(2))!.SettledCount).Should().Be((0, 1));
        (await _kit.LedgerAsync(1)).Select(l => (l.Kind, l.AmountMinor)).Should().Equal(
            (PredictionLedgerKind.Initial, 100_000L), (PredictionLedgerKind.Stake, -10_000L), (PredictionLedgerKind.Payout, 11_000L));
        (await _kit.LedgerAsync(2)).Should().NotContain(l => l.Kind == PredictionLedgerKind.Payout);

        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.WinningOutcomeId, row.WinnerCount, row.PayoutTotalMinor, row.SettledByUserId).Should()
            .Be((PredictionStatus.Settled, prediction.Outcomes[0].Id, 2, 12_375L, Admin().UserId.Value));
        await _kit.TickAsync();
        Shown(_kit.Card(prediction)).Embed!.Fields.Single(f => f.Name == "🏆 Sonuç").Value.Should().Contain("Galatasaray Kazanır").And.Contain("123,75 TSQ Coin");
        Shown(_kit.Card(prediction)).Select.Should().BeNull();

        (await _kit.Service(s => s.StartSettleAsync(Admin(), Predictions, "#" + prediction.Id, Ct))).Result.MessageKey.Should().Be("predictions.state.settled");
        (await _kit.Service(s => s.PreviewCancelAsync(Admin(), Predictions, "#" + prediction.Id, "geri al", Ct))).Result.MessageKey.Should().Be("predictions.state.settled");
        (await _kit.Service(s => s.ConfirmSettleAsync(Admin(), Predictions, token!, Ct))).Result.MessageKey.Should().Be("predictions.confirm.expired");
    }

    [Fact]
    public async Task Nobody_on_the_winning_outcome_settles_without_payouts_or_refunds()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 2, "100")).Result.Succeeded.Should().BeTrue();

        var done = await _kit.SettleAsync(Creator(), prediction, 3);
        done.Result.MessageKey.Should().Be("predictions.settle.done_nobody");
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(90_000);
        (await _kit.WalletAsync(2))!.BalanceMinor.Should().Be(90_000);
        ((await _kit.WalletAsync(1))!.SettledCount, (await _kit.WalletAsync(1))!.CorrectCount).Should().Be((1, 0));
        (await _kit.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.Kind == PredictionLedgerKind.Payout || l.Kind == PredictionLedgerKind.Refund))).Should().Be(0);
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Settled);
    }

    [Fact]
    public async Task Settling_an_open_prediction_closes_entries_at_once()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var (late, _) = await _kit.PreviewEntryAsync(Member(), prediction, 1, "100");
        (await _kit.SettleAsync(Admin(), prediction, 2)).Result.Succeeded.Should().BeTrue();
        (await _kit.ConfirmEntryAsync(Member(), late!)).Result.MessageKey.Should().Be("predictions.entry.closed");
    }

    [Fact]
    public async Task Cancelling_refunds_every_stake_once_and_changes_no_statistics()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 3, "33,33")).Result.Succeeded.Should().BeTrue();

        var done = await _kit.CancelAsync(Creator(), prediction, "Maç ertelendi");
        done.Result.MessageKey.Should().Be("predictions.cancel.done");
        done.Result.Args.Should().Equal(prediction.Id.ToString(), 2, "133,33");
        foreach (var user in new ulong[] { 1, 2 })
        {
            var wallet = (await _kit.WalletAsync(user))!;
            (wallet.BalanceMinor, wallet.PendingMinor, wallet.SettledCount, wallet.CorrectCount).Should().Be((100_000L, 0L, 0, 0));
        }

        (await _kit.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.Kind == PredictionLedgerKind.Refund))).Should().Be(2);
        (await _kit.EntriesAsync(prediction.Id)).Should().OnlyContain(e => e.Status == PredictionEntryStatus.Refunded);
        await _kit.TickAsync();
        Shown(_kit.Card(prediction)).Embed!.Fields.Single(f => f.Name == "Durum").Value.Should().Contain("Maç ertelendi").And.Contain("133,33 TSQ Coin");
        (await _kit.CancelTokenAsync(Creator(), prediction)).Should().BeNull("a cancelled prediction cannot be cancelled again");
        (await _kit.Service(s => s.PreviewCancelAsync(Creator(), Predictions, "#" + prediction.Id, "x", Ct))).Result.MessageKey.Should().Be("predictions.state.cancelled");
    }

    [Fact]
    public async Task A_cancel_reason_is_required()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.Service(s => s.PreviewCancelAsync(Creator(), Predictions, "#" + prediction.Id, "  a ", Ct))).Result.MessageKey.Should().Be("predictions.cancel.reason_length");
    }

    [Fact]
    public async Task Settle_and_cancel_racing_end_in_exactly_one_terminal_state_with_one_kind_of_booking()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 2, "100")).Result.Succeeded.Should().BeTrue();
        var settle = await _kit.SettleTokenAsync(Admin(), prediction, 1);
        var cancel = await _kit.CancelTokenAsync(Creator(), prediction);

        var results = await _kit.TogetherAsync(
            () => _kit.Service(s => s.ConfirmSettleAsync(Admin(), Predictions, settle!, Ct)),
            () => _kit.Service(s => s.ConfirmCancelAsync(Creator(), Predictions, cancel!, Ct)));

        results.Count(r => r.Result.Succeeded).Should().Be(1);
        var row = await _kit.RowAsync(prediction.Id);
        var ledger = await _kit.Db(db => db.Set<PredictionLedgerEntity>().Where(l => l.PredictionId == prediction.Id && l.Kind != PredictionLedgerKind.Stake).ToListAsync());
        if (row.Status == PredictionStatus.Settled)
            ledger.Should().ContainSingle().Which.Kind.Should().Be(PredictionLedgerKind.Payout);
        else
            ledger.Select(l => l.Kind).Should().Equal(PredictionLedgerKind.Refund, PredictionLedgerKind.Refund);
        foreach (var user in new ulong[] { 1, 2 })
            (await _kit.WalletAsync(user))!.PendingMinor.Should().Be(0);
    }

    // ---- cards and Discord failures ----

    [Fact]
    public async Task Bursts_of_entries_become_one_card_edit()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var edits = _kit.Transport.EditCalls;
        foreach (var user in new ulong[] { 1, 2, 3 })
            (await _kit.EnterAsync(Member(user), prediction, 1, "10")).Result.Succeeded.Should().BeTrue();
        _kit.Transport.EditCalls.Should().Be(edits, "the card was edited moments ago; the edits are coalesced");

        await _kit.TickAsync(TimeSpan.FromSeconds(10));
        _kit.Transport.EditCalls.Should().Be(edits + 1);
        Shown(_kit.Card(prediction)).Embed!.Fields.Should().Contain(f => f.Value == "3 katılımcı\n30 TSQ Coin yatırıldı");
        Shown(_kit.Card(prediction)).Mentions.Should().Be(MentionPolicy.None);
    }

    [Fact]
    public async Task A_deleted_card_stops_entries_keeps_stakes_and_can_still_be_settled_by_number()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        _kit.Transport.DeleteMessage(prediction.Message!.Value);

        await _kit.TickAsync(); // the first pass includes the presence check of open cards
        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.LockReason, row.CardMissing).Should().Be((PredictionStatus.Locked, PredictionLockReason.CardMissing, true));
        (await _kit.PreviewEntryAsync(Member(2), prediction, 1, "100")).Reply.Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.WalletAsync(1))!.PendingMinor.Should().Be(10_000);

        (await _kit.SettleAsync(Admin(), prediction, 1)).Result.MessageKey.Should().Be("predictions.settle.done");
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(101_000);
    }

    [Fact]
    public async Task Rate_limits_server_errors_and_unreadable_cards_are_never_taken_for_deletion()
    {
        var prediction = await _kit.CreatePredictionAsync();
        _kit.Transport.ScriptedPresence = MessagePresence.Unknown;
        _kit.Transport.ScriptEdit(() => new SendOutcome.RateLimited(TimeSpan.FromSeconds(5)));
        _kit.Transport.ScriptEdit(() => new SendOutcome.Transient("503"));
        (await _kit.Service(s => s.LockAsync(Creator(), Predictions, "#" + prediction.Id, Ct))).MessageKey.Should().Be("predictions.lock.done");

        await _kit.TickAsync();
        var row = await _kit.RowAsync(prediction.Id);
        (row.CardMissing, row.CardStale, row.Status).Should().Be((false, true, PredictionStatus.Locked));
        await _kit.TickAsync();
        (await _kit.RowAsync(prediction.Id)).CardStale.Should().BeFalse();
        Shown(_kit.Card(prediction)).Select!.Disabled.Should().BeTrue();
    }

    [Fact]
    public async Task Disabling_the_module_stops_new_activity_but_keeps_coins_and_deadlines()
    {
        var prediction = await _kit.CreatePredictionAsync(lockAt: "24.09.2026 16:00");
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        await _kit.SetEnabledAsync(false);

        (await _kit.PreviewEntryAsync(Member(2), prediction, 1, "100")).Reply.Result.MessageKey.Should().Be("error.module_disabled");
        (await _kit.Economy(e => e.ClaimDailyAsync(Member(1), Commands, Ct))).Result.MessageKey.Should().Be("error.module_disabled");
        (await _kit.Service(s => s.OpenFormAsync(Creator(), Predictions, Ct))).Refusal!.MessageKey.Should().Be("error.module_disabled");

        await _kit.TickAsync(TimeSpan.FromHours(2));
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Locked, "a passed deadline is still honoured");
        await _kit.SetEnabledAsync(true);
        var wallet = (await _kit.WalletAsync(1))!;
        (wallet.BalanceMinor, wallet.PendingMinor).Should().Be((90_000L, 10_000L));
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
    }

    // ---- privacy ----

    [Fact]
    public async Task Privacy_export_and_delete_remove_the_members_coins_and_pending_stakes_without_breaking_totals()
    {
        var prediction = await _kit.CreatePredictionAsync(creator: Creator(10));
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 1, "50")).Result.Succeeded.Should().BeTrue();
        (await _kit.Economy(e => e.ClaimDailyAsync(Member(1), Commands, Ct))).Result.Succeeded.Should().BeTrue();

        await _kit.Host.InScopeAsync(async sp =>
        {
            var privacy = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "predictions");
            var export = (await privacy.ExportAsync(Guild, new UserId(1), Ct)).ToJsonString();
            export.Should().Contain("\"wallets\"").And.Contain("\"stakeUnits\":10000").And.Contain("\"kind\":\"Daily\"").And.Contain("\"dailyRewards\"");
            (await privacy.PreviewDeletionAsync(Guild, new UserId(1), Ct)).Select(i => i.LabelKey).Should().Contain("predictions.privacy.pending");
            (await privacy.DeleteAsync(Guild, new UserId(1), Ct)).RecordsDeleted.Should().BePositive();
            await privacy.DeleteAsync(Guild, new UserId(10), Ct);
        });

        (await _kit.WalletAsync(1)).Should().BeNull();
        (await _kit.LedgerAsync(1)).Should().BeEmpty();
        (await _kit.Db(db => db.Set<PredictionDailyClaimEntity>().CountAsync(c => c.UserId == 1))).Should().Be(0);
        var row = await _kit.RowAsync(prediction.Id);
        (row.EntryCount, row.StakeTotalMinor, row.CreatorUserId, row.CreatorName).Should().Be((1, 5_000L, 0UL, ""));

        await _kit.TickAsync(TimeSpan.FromSeconds(11));
        Shown(_kit.Card(prediction)).Embed!.Fields.Should().Contain(f => f.Value == "1 katılımcı\n50 TSQ Coin yatırıldı");
        Shown(_kit.Card(prediction)).Embed!.Footer.Should().Contain("Oluşturan: —");

        (await _kit.SettleAsync(Admin(), prediction, 1)).Result.Args.Should().Equal(prediction.Id.ToString(), 1, "55");
        (await _kit.WalletAsync(2))!.BalanceMinor.Should().Be(100_500);
        (await _kit.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.UserId == 1))).Should().Be(0, "nothing is paid to deleted data");
        await _kit.Host.InScopeAsync(async sp =>
        {
            var health = await sp.GetServices<IModuleHealthCheck>().Single(h => h.Module.Value == "predictions").CheckAsync(Ct);
            health.Entries.Should().NotContain(e => e.Component == "predictions.health.consistency");
        });
    }

    // ---- health ----

    [Fact]
    public async Task The_status_check_reports_missing_permissions_and_problem_cards_by_count()
    {
        _kit.Host.Guilds.SetChannel(Guild, Commands, new BotChannelAccess(true, true, GuildPermission.ViewChannel));
        _kit.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        var draft = await _kit.OpenFormAsync();
        (await _kit.SubmitAsync(draft)).Result.Succeeded.Should().BeTrue();
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.publish.uncertain");

        var report = await _kit.Host.InScopeAsync(sp => sp.GetServices<IModuleHealthCheck>().Single(h => h.Module.Value == "predictions").CheckAsync(Ct));
        report.Entries.Should().Contain(e => e.Component == "predictions.health.channels" && e.State == HealthState.Degraded);
        report.Entries.Single(e => e.Component == "predictions.health.cards").Args.Should().Equal(1, 0, 0);
    }
}
