using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Commands;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ToroSquad.Tests.Integration.PredictionTestKit;
using LabelComponent = Discord.LabelComponent;
using TextInputComponent = Discord.TextInputComponent;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Öngörü create → publish → enter (🎯 Tahmin Yap) → lock / settle / cancel from the card's buttons, on the real SQLite
/// database and the production wiring: the channel, role and manager gates (a refusal changes nothing), the card's scope
/// (guild, channel, its own message, a forged number), the form and the single card, the atomic entry, the exact deadline,
/// settlement and refund arithmetic, the races that must never pay twice, deleted and failing cards, restarts, module
/// disable/enable. Concurrency tests start their actions together on separate scopes (own DbContext and connection) —
/// no sleeps, no "ran 100 times".
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

    /// <summary>Everything a management click could change, to prove a refused click changed nothing.</summary>
    private async Task<string> SnapshotAsync(long predictionId) => await _kit.Db(async db =>
    {
        var p = await db.Set<PredictionEntity>().AsNoTracking().SingleAsync(x => x.Id == predictionId);
        var ledger = await db.Set<PredictionLedgerEntity>().CountAsync();
        var wallets = string.Join(",", await db.Set<PredictionWalletEntity>().AsNoTracking().OrderBy(w => w.Id).Select(w => w.BalanceMinor + ":" + w.PendingMinor).ToListAsync());
        return $"{p.Status}|{p.Version}|{p.WinningOutcomeId}|{p.CancelReason}|{ledger}|{wallets}";
    });

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
            await _kit.Economy(e => e.ClaimDailyAsync(Member(), Predictions, "x", Ct)),
            await _kit.Economy(e => e.MyEntriesAsync(Member(), Elsewhere, 0, Ct)),
            await _kit.Economy(e => e.LeaderboardAsync(Member(), Predictions, Ct)),
            await _kit.Economy(e => e.TournamentStatusAsync(Member(), Elsewhere, Ct)),
            await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Predictions, Ct)),
            await _kit.Economy(e => e.PreviewTournamentEndAsync(Owner(), Elsewhere, Ct)),
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

    // ---- managing from the card: who ----

    [Fact]
    public async Task Lock_and_cancel_are_for_the_creator_with_the_role_or_administrators_settling_also_for_any_role_holder_and_a_refusal_changes_nothing()
    {
        var prediction = await _kit.CreatePredictionAsync(creator: Creator(10));
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        var before = await SnapshotAsync(prediction.Id);

        // Lock and cancel: unchanged — another creator gets nothing new from the settlement rule (H).
        foreach (var outsider in new[] { Creator(11), Member(10), Member(1), TestHost.Admin(Guild) }) // another creator; the creator without the role; a member; Manage Server only
        {
            (await _kit.Service(s => s.PromptLockAsync(outsider, Predictions, prediction.Id, prediction.Message, Ct))).Result.MessageKey.Should().Be("predictions.not_manager");
            (await _kit.Service(s => s.LockAsync(outsider, Predictions, prediction.Id, Ct))).MessageKey.Should().Be("predictions.not_manager");
            (await _kit.Service(s => s.StartCancelAsync(outsider, Predictions, prediction.Id, prediction.Message, Ct)))!.MessageKey.Should().Be("predictions.not_manager");
            (await _kit.Service(s => s.PreviewCancelAsync(outsider, Predictions, prediction.Id, "yetkisiz", Ct))).Result.MessageKey.Should().Be("predictions.not_manager");
        }

        // Settle: anyone without the creator role, Administrator or ownership is refused (D) …
        foreach (var outsider in new[] { Member(10), Member(1), TestHost.Admin(Guild) })
        {
            (await _kit.Service(s => s.StartSettleAsync(outsider, Predictions, prediction.Id, prediction.Message, Ct))).Result.MessageKey.Should().Be("predictions.not_manager");
            (await _kit.Service(s => s.PreviewSettleAsync(outsider, Predictions, prediction.Id, Id(prediction.Outcomes[0].Id), Ct))).Result.MessageKey.Should().Be("predictions.not_manager");
            (await _kit.ConfirmSettleAsync(outsider, prediction, 1)).Result.MessageKey.Should().Be("predictions.not_manager");
        }

        // … while another creator may open the settlement steps of a card they did not create (B; confirmed in the settlement tests).
        (await _kit.SettlePreviewAsync(Creator(11), prediction, 1)).Confirm.Should().NotBeNull();

        (await SnapshotAsync(prediction.Id)).Should().Be(before, "no refused click or preview changed anything");
        _kit.Host.Services.GetRequiredService<ToroSquad.Core.Localization.ILocalizer>().Get("tr", "predictions.not_manager").Should().Be("Bu öngörüyü yönetme yetkiniz yok.");

        (await _kit.LockAsync(Creator(10), prediction)).MessageKey.Should().Be("predictions.lock.done", "the creator locks their own card");
        var other = await _kit.CreatePredictionAsync(creator: Creator(11), title: "Başka yaratıcının öngörüsü");
        (await _kit.LockAsync(Admin(), other)).MessageKey.Should().Be("predictions.lock.done", "Administrator without the creator role");
        var third = await _kit.CreatePredictionAsync(creator: Creator(11), title: "Üçüncü öngörü başlığı");
        (await _kit.CancelAsync(Owner(), third)).Result.MessageKey.Should().Be("predictions.cancel.done", "the server owner");
    }

    [Fact]
    public async Task A_settle_confirmation_is_refused_when_the_role_was_removed_meanwhile()
    {
        var prediction = await _kit.CreatePredictionAsync(creator: Creator(10));
        var (confirm, _) = await _kit.SettlePreviewAsync(Creator(10), prediction, 1);
        confirm.Should().NotBeNull();
        (await _kit.ConfirmSettleAsync(Member(10), prediction, 1)).Result.MessageKey.Should().Be("predictions.not_manager");
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open);
    }

    [Fact]
    public async Task A_click_from_another_guild_channel_or_message_or_with_a_forged_number_finds_nothing()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var other = await _kit.CreatePredictionAsync(title: "Diğer öngörünün başlığı");
        await _kit.Host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(OtherGuild), "predictions", true, Ct)).Succeeded.Should().BeTrue());
        var before = await SnapshotAsync(prediction.Id);

        var foreignAdmin = new ActorContext(OtherGuild, new UserId(20), GuildPermission.Administrator, [], false, 50);
        (await _kit.Service(s => s.PromptLockAsync(foreignAdmin, Predictions, prediction.Id, prediction.Message, Ct))).Result.MessageKey.Should().Be("predictions.not_found");
        (await _kit.Service(s => s.StartEntryAsync(Member(5) with { GuildId = OtherGuild }, Predictions, prediction.Id, prediction.Message, Ct)))
            .Reply!.Result.MessageKey.Should().Be("predictions.not_found");
        (await _kit.Service(s => s.PromptLockAsync(Admin(), Elsewhere, prediction.Id, prediction.Message, Ct))).Result.MessageKey.Should().Be("predictions.wrong_channel");
        (await _kit.Service(s => s.PromptLockAsync(Admin(), Predictions, prediction.Id, other.Message, Ct))).Result.MessageKey.Should().Be("predictions.not_found",
            "a button copied onto another message does not act on this prediction");
        (await _kit.Service(s => s.StartSettleAsync(Admin(), Predictions, 987_654, prediction.Message, Ct))).Result.MessageKey.Should().Be("predictions.not_found");
        (await _kit.Service(s => s.StartEntryAsync(Member(), Predictions, prediction.Id, other.Message, Ct))).Reply!.Result.MessageKey.Should().Be("predictions.not_found");
        (await _kit.Service(s => s.PreviewSettleAsync(Admin(), Predictions, prediction.Id, Id(other.Outcomes[0].Id), Ct))).Result.MessageKey
            .Should().Be("predictions.not_found", "an outcome of another prediction");
        (await _kit.Service(s => s.ConfirmSettleAsync(Admin(), Predictions, prediction.Id, other.Outcomes[0].Id, Ct))).Result.MessageKey
            .Should().Be("predictions.state.unavailable", "checked again inside the transaction");

        (await SnapshotAsync(prediction.Id)).Should().Be(before);
    }

    // ---- create ----

    [Fact]
    public async Task Publishing_posts_one_card_with_the_question_first_and_its_buttons_and_a_second_click_publishes_nothing()
    {
        var draft = await _kit.OpenFormAsync();
        var preview = await _kit.SubmitAsync(draft, rules: "Normal sürenin sonucu esas alınır; uzatmalar dahil değildir.");
        preview.View!.Embed!.Title.Should().Be("👀 Önizleme");
        preview.View.Content.Should().Contain("Sabit oran");
        preview.View.Buttons!.Select(b => b.CustomId).Should().Equal(PredictionMessages.PublishPrefix + draft, PredictionMessages.EditPrefix + draft,
            PredictionMessages.DiscardPrefix + draft);
        preview.View.Buttons!.Select(b => b.Label).Should().Equal("✅ Yayımla", "✏️ Düzenle", "❌ Vazgeç");
        preview.View.Embed.Fields.Should().Contain(f => f.Name == "⏳ Kilitlenme" && f.Value == "Manuel", "no lock date and time: locked by hand");
        _kit.Transport.Messages.Should().BeEmpty("the preview is private; nothing is public before Yayımla");

        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.publish.done");
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.form.unavailable");

        var card = _kit.Transport.Messages.Should().ContainSingle().Subject;
        card.Channel.Should().Be(Predictions);
        card.Pinged.Should().BeFalse();
        card.Message.Embed!.Title.Should().Be("🟢 Katılım Açık");
        card.Message.Embed.Description.Should().StartWith("## Galatasaray \\- Fenerbahçe Maç Sonucu Ne Olur?\n\n1️⃣ Galatasaray Kazanır\nOran: **1.10**\n\n2️⃣ Beraberlik\nOran: **2.30**");
        var row = await _kit.Db(db => db.Set<PredictionEntity>().AsNoTracking().SingleAsync());
        var id = Id(row.Id);
        card.Message.Buttons!.Select(b => (b.Label, b.CustomId, b.Disabled, b.NewRow)).Should().Equal(
            ("🎯 Tahmin Yap", PredictionCards.EnterPrefix + id, false, false), ("🔒 Kilitle", PredictionCards.LockPrefix + id, false, true),
            ("✅ Sonuçlandır", PredictionCards.SettlePrefix + id, false, false), ("↩️ İptal / İade", PredictionCards.CancelPrefix + id, false, false));
        card.Message.Select.Should().BeNull("no shared select state on the public card");
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
        var reply = await _kit.SubmitAsync(draft, "Kim", outcomes, "31.02.2026", "20:00", "kural");
        reply.Result.Succeeded.Should().BeFalse();
        reply.View!.Content.Should().Contain("**Başlık**").And.Contain("2. satır").And.Contain("3. satırdaki oran").And
            .Contain("**Kilitlenme tarihi** geçerli değil. Örnek: 05.10.2026");
        reply.View.Buttons!.Select(b => b.CustomId).Should().Equal(PredictionMessages.EditPrefix + draft, PredictionMessages.DiscardPrefix + draft);
        var kept = _kit.Host.Services.GetRequiredService<PredictionTokens>().Get<FormDraftStep>(draft, Creator())!.Values;
        kept.Should().Be(new PredictionFormValues("Kim", outcomes, "31.02.2026", "20:00", "kural"), "the date and the time are kept apart, as typed");

        // Düzenle reopens the five fields with exactly that input.
        var catalog = PredictionDomainTests.Localizer();
        var defaultOdds = await _kit.Service(s => Task.FromResult(s.DefaultOddsText));
        defaultOdds.Should().Be("2.00");
        var modal = PredictionFormUi.CreateModal(draft, kept, defaultOdds, key => catalog.Get("tr", key));
        modal.Component.Components.Select(c => ((TextInputComponent)((LabelComponent)c).Component).Value)
            .Should().Equal("Kim", outcomes, "31.02.2026", "20:00", "kural");

        (await _kit.SubmitAsync(draft, lockDate: "05.10.2026")).View!.Content.Should().Contain("Kilitlenme tarihi girdiysen saat de girmelisin.");
        (await _kit.SubmitAsync(draft, lockTime: "20:00")).View!.Content.Should().Contain("Kilitlenme saati girdiysen tarih de girmelisin.");
        var dated = await _kit.SubmitAsync(draft, lockDate: "05.10.2026", lockTime: "20:00");
        dated.Result.Succeeded.Should().BeTrue();
        dated.View!.Embed!.Fields.Should().Contain(f => f.Name == "⏳ Kilitlenme" && f.Value == "<t:1791219600:R>\n<t:1791219600:F>",
            "the two fields come back as one readable Türkiye time (20:00 = 17:00 UTC)");

        (await _kit.SubmitAsync(draft, outcomes: "A | 1.10\nB")).View!.Content.Should().Contain("**2.00**", "the default odds are named in the preview");
    }

    [Fact]
    public async Task The_lock_time_is_checked_again_when_publishing()
    {
        var draft = await _kit.OpenFormAsync();
        (await _kit.SubmitAsync(draft, lockDate: "24.09.2026", lockTime: "15:03")).Result.Succeeded.Should().BeTrue(); // T0 is 15:00 in Türkiye
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(5));
        var reply = await _kit.PublishAsync(draft);
        reply.View!.Content.Should().Contain("ileri bir tarih ve saat");
        (await _kit.CountAsync<PredictionEntity>()).Should().Be(0);
    }

    [Fact]
    public async Task A_draft_never_moves_into_a_new_tournament()
    {
        var draft = await _kit.OpenFormAsync();
        await _kit.ParticipateAsync(1);
        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.MessageKey.Should().Be("predictions.tournament.ended");

        (await _kit.SubmitAsync(draft)).Result.Succeeded.Should().BeTrue();
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.form.tournament_changed");
        (await _kit.CountAsync<PredictionEntity>()).Should().Be(1, "only the participation prediction");
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
        await _kit.ParticipateAsync(1);
        _kit.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        var draft = await _kit.OpenFormAsync();
        (await _kit.SubmitAsync(draft)).Result.Succeeded.Should().BeTrue();
        (await _kit.PublishAsync(draft)).Result.MessageKey.Should().Be("predictions.publish.uncertain");
        var row = await _kit.Db(db => db.Set<PredictionEntity>().AsNoTracking().SingleAsync(p => p.Status == PredictionStatus.Publishing));
        (await _kit.Service(s => s.StartEntryAsync(Member(), Predictions, row.Id, null, Ct))).Reply!.Result.MessageKey.Should().Be("predictions.entry.publishing");
        var blocked = await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct));
        blocked.Result.MessageKey.Should().Be("predictions.tournament.unresolved");
        blocked.View!.Content.Should().Contain("#" + row.Id + "** · Galatasaray").And.Contain("Yayımlanıyor");
        var messages = _kit.Transport.Messages.Count(m => m.Channel == Predictions);

        await _kit.TickAsync(TimeSpan.FromMinutes(2));
        (await _kit.RowAsync(row.Id)).Status.Should().Be(PredictionStatus.Publishing);
        await _kit.TickAsync(TimeSpan.FromMinutes(10));
        (await _kit.RowAsync(row.Id)).Status.Should().Be(PredictionStatus.Abandoned);
        _kit.Transport.Messages.Count(m => m.Channel == Predictions).Should().Be(messages, "never posted a second time");
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

    // ---- 🎯 Tahmin Yap ----

    [Fact]
    public async Task The_form_submit_is_the_entry_it_debits_once_books_the_ledger_snapshots_the_odds_and_updates_the_card()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var start = await _kit.OpenEntryAsync(Member(), prediction);
        start.Form.Should().NotBeNull();
        start.Reply.Should().BeNull();
        (await _kit.WalletAsync(Member().UserId.Value)).Should().BeNull("opening the form debits and creates nothing");

        var receipt = await _kit.SubmitEntryAsync(Member(), prediction, 1, "100");
        receipt.Result.MessageKey.Should().Be("predictions.entry.done");
        receipt.Public.Should().BeFalse("the receipt is private; nobody is told who staked what");
        receipt.View!.Embed!.Title.Should().Be("✅ Tahminin kaydedildi!");
        receipt.View.Embed.Description.Should().Contain("🎯 **Galatasaray Kazanır**").And.Contain("📈 Oran: 1.10").And.Contain("🪙 Yatırdığın: 100 TSQ Coin")
            .And.Contain("💰 Olası toplam dönüş: 110 TSQ Coin").And.Contain("👛 Kullanılabilir bakiyen: 900 TSQ Coin")
            .And.Contain("Öngörü kilitlenene kadar tahminini değiştirebilir veya geri çekebilirsin.");
        receipt.View.Buttons!.Select(b => (b.Label, b.CustomId)).Should().Equal(
            ("✏️ Tahminimi Değiştir", PredictionMessages.ChangePrefix + Id(prediction.Id)), ("↩️ Tahminimi Geri Çek", PredictionMessages.WithdrawPrefix + Id(prediction.Id)));
        receipt.View.Buttons.Should().NotContain(b => b.Label.Contains("Onayla"), "the submit was the confirmation; there is no second step");

        var wallet = (await _kit.WalletAsync(Member().UserId.Value))!;
        (wallet.BalanceMinor, wallet.PendingMinor, wallet.DisplayName).Should().Be((90_000L, 10_000L, "Üye 100"));
        var entry = (await _kit.EntriesAsync(prediction.Id)).Should().ContainSingle().Subject;
        (entry.StakeMinor, entry.OddsX100, entry.PotentialPayoutMinor, entry.Status).Should().Be((10_000L, 110, 11_000L, PredictionEntryStatus.Pending));
        (await _kit.LedgerAsync(Member().UserId.Value)).Select(l => (l.Kind, l.AmountMinor, l.BalanceAfterMinor)).Should().Equal(
            (PredictionLedgerKind.Initial, 100_000L, 100_000L), (PredictionLedgerKind.Stake, -10_000L, 90_000L));

        await _kit.TickAsync(TimeSpan.FromSeconds(11));
        Shown(_kit.Card(prediction)).Embed!.Fields.Should().Contain(f => f.Value == "1 katılımcı\n🪙 100 TSQ Coin yatırıldı");
        _kit.Transport.Messages.Should().ContainSingle("no new message or DM per entry");
    }

    [Fact]
    public async Task A_repeated_submit_or_a_second_tahmin_yap_shows_the_active_entry_from_the_database_and_changes_nothing()
    {
        var prediction = await _kit.CreatePredictionAsync();
        // The member opens the form and closes it without submitting: nothing happened, nothing is remembered on the card.
        (await _kit.OpenEntryAsync(Member(), prediction)).Form.Should().NotBeNull();
        (await _kit.WalletAsync(Member().UserId.Value)).Should().BeNull();
        (await _kit.SubmitEntryAsync(Member(), prediction, 2, "50")).Result.MessageKey.Should().Be("predictions.entry.done");

        // The same modal delivered twice, or a second form opened meanwhile: one entry, one debit, and the entry is shown.
        var repeat = await _kit.SubmitEntryAsync(Member(), prediction, 3, "60");
        repeat.Result.MessageKey.Should().Be("predictions.entry.already");
        repeat.View!.Content.Should().Be("Bu öngörüde zaten aktif bir tahminin var; aşağıdan değiştirebilir veya geri çekebilirsin.");
        repeat.View.Embed!.Description.Should().Contain("🎯 **Beraberlik**").And.Contain("🪙 Yatırdığın: 50 TSQ Coin");

        // The private answer is gone: 🎯 Tahmin Yap on the card is the way back, read from the database.
        var again = await _kit.OpenEntryAsync(Member(), prediction);
        again.Form.Should().BeNull("an active entry is shown instead of a new form");
        again.Reply!.View!.Embed!.Title.Should().Be("🎯 Mevcut Tahminin");
        again.Reply.View.Embed.Description.Should().Contain("🎯 **Beraberlik**").And.Contain("📈 Oran: 2.30").And.Contain("🪙 Yatırdığın: 50 TSQ Coin");
        again.Reply.View.Buttons!.Select(b => b.Label).Should().Equal("✏️ Tahminimi Değiştir", "↩️ Tahminimi Geri Çek");

        var entry = (await _kit.EntriesAsync(prediction.Id)).Should().ContainSingle().Subject;
        (entry.OutcomeId, entry.StakeMinor).Should().Be((prediction.Outcomes[1].Id, 5_000L));
        (await _kit.WalletAsync(Member().UserId.Value))!.BalanceMinor.Should().Be(95_000);
        (await _kit.LedgerAsync(Member().UserId.Value)).Count(l => l.Kind == PredictionLedgerKind.Stake).Should().Be(1);
        (await _kit.Service(s => s.SubmitEntryAsync(Member(2), Predictions, prediction.Id, null, "10", "x", Ct))).Result.MessageKey.Should().Be("predictions.entry.outcome_required");
        (await _kit.Service(s => s.SubmitEntryAsync(Member(2), Predictions, prediction.Id, Id(prediction.Outcomes[0].Id), "abc", "x", Ct))).Result.MessageKey
            .Should().Be("predictions.entry.amount_format");
        (await _kit.WalletAsync(2)).Should().BeNull("an invalid submit changes nothing");
    }

    [Fact]
    public async Task Stake_rules_are_enforced_before_anything_is_debited()
    {
        var prediction = await _kit.CreatePredictionAsync();
        async Task<string> Key(string amount) => (await _kit.EnterAsync(Member(), prediction, 2, amount)).Result.MessageKey;

        (await Key("0,5")).Should().Be("predictions.entry.amount_minimum");
        (await Key("12,345")).Should().Be("predictions.entry.amount_decimals");
        (await Key("abc")).Should().Be("predictions.entry.amount_format");
        (await Key("-10")).Should().Be("predictions.entry.amount_format");
        (await _kit.WalletAsync(Member().UserId.Value)).Should().BeNull("a refused amount creates nothing");
        (await Key("1000,01")).Should().Be("predictions.entry.insufficient");
        (await _kit.EntriesAsync(prediction.Id)).Should().BeEmpty();
        (await _kit.WalletAsync(Member().UserId.Value))!.BalanceMinor.Should().Be(100_000, "nothing was debited");
        (await Key("1000")).Should().Be("predictions.entry.done");
        (await _kit.WalletAsync(Member().UserId.Value))!.BalanceMinor.Should().Be(0);
    }

    [Fact]
    public async Task Two_simultaneous_submits_for_one_prediction_make_one_entry()
    {
        var prediction = await _kit.CreatePredictionAsync();
        var results = await _kit.TogetherAsync(() => _kit.SubmitEntryAsync(Member(), prediction, 1, "100"), () => _kit.SubmitEntryAsync(Member(), prediction, 2, "200"));

        results.Count(r => r.Result.Succeeded).Should().Be(1);
        results.Single(r => !r.Result.Succeeded).Result.MessageKey.Should().Be("predictions.entry.already");
        var entry = (await _kit.EntriesAsync(prediction.Id)).Should().ContainSingle().Subject;
        var wallet = (await _kit.WalletAsync(Member().UserId.Value))!;
        (wallet.BalanceMinor + wallet.PendingMinor).Should().Be(100_000);
        wallet.PendingMinor.Should().Be(entry.StakeMinor);
        (await _kit.LedgerAsync(Member().UserId.Value)).Count(l => l.Kind == PredictionLedgerKind.Initial).Should().Be(1);

        (await _kit.EnterAsync(Member(), prediction, 3, "10")).Result.MessageKey.Should().Be("predictions.entry.current");
    }

    [Fact]
    public async Task Simultaneous_entries_in_two_predictions_cannot_spend_more_than_the_balance()
    {
        var p1 = await _kit.CreatePredictionAsync();
        var p2 = await _kit.CreatePredictionAsync(title: "İkinci öngörü başlığı");
        var results = await _kit.TogetherAsync(() => _kit.SubmitEntryAsync(Member(), p1, 1, "700"), () => _kit.SubmitEntryAsync(Member(), p2, 1, "700"));

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
        (await _kit.OpenEntryAsync(Member(), prediction)).Form.Should().NotBeNull();
        var connectionString = await _kit.Db(db => Task.FromResult(db.Database.GetConnectionString()!));

        // Another writer holds the database write lock and closes the prediction; the confirmation starts meanwhile.
        await using var blocker = new SqliteConnection(connectionString);
        await blocker.OpenAsync(TestContext.Current.CancellationToken);
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE; UPDATE prediction SET Status = 2 WHERE Id = " + prediction.Id + ";";
            await begin.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var confirm = Task.Run(() => _kit.SubmitEntryAsync(Member(), prediction, 1, "100"));
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
        (await _kit.OpenEntryAsync(Member(), prediction)).Form.Should().NotBeNull();

        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(1)); // exactly the lock time; no worker pass has run
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open);
        (await _kit.SubmitEntryAsync(Member(), prediction, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.EnterAsync(Member(), prediction, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.WalletAsync(Member().UserId.Value)).Should().BeNull();

        await _kit.TickAsync();
        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.LockReason).Should().Be((PredictionStatus.Locked, PredictionLockReason.Deadline));
        var card = Shown(_kit.Card(prediction));
        card.Embed!.Title.Should().Be("🔒 Katılım Kapandı");
        card.Buttons!.Select(b => (b.Label, b.Disabled)).Should().Equal(("🔒 Katılım kapandı", true), ("✅ Sonuçlandır", false), ("↩️ İptal / İade", false));
    }

    [Fact]
    public async Task Lock_asks_first_changes_open_to_locked_once_and_a_form_opened_before_it_cannot_be_submitted_after_it()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.OpenEntryAsync(Member(), prediction)).Form.Should().NotBeNull();

        var prompt = await _kit.Service(s => s.PromptLockAsync(Creator(), Predictions, prediction.Id, prediction.Message, Ct));
        prompt.View!.Content.Should().Contain("Bu öngörüyü kilitlemek istediğinize emin misiniz?").And.Contain("V1'de tekrar açılamayacak");
        prompt.View.Buttons!.Select(b => b.CustomId).Should().Equal(PredictionMessages.LockConfirmPrefix + Id(prediction.Id), PredictionMessages.DismissPrefix + "-");
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open, "the prompt changes nothing");

        var results = await _kit.TogetherAsync(
            () => _kit.Service(s => s.LockAsync(Creator(), Predictions, prediction.Id, Ct)),
            () => _kit.Service(s => s.LockAsync(Admin(), Predictions, prediction.Id, Ct)));
        results.Select(r => r.MessageKey).Should().BeEquivalentTo("predictions.lock.done", "predictions.state.already_locked");

        (await _kit.SubmitEntryAsync(Member(), prediction, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.WalletAsync(Member().UserId.Value)).Should().BeNull();
        (await _kit.Service(s => s.PromptLockAsync(Creator(), Predictions, prediction.Id, prediction.Message, Ct))).Result.MessageKey.Should().Be("predictions.state.already_locked");
        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.LockReason).Should().Be((PredictionStatus.Locked, PredictionLockReason.Manual));
    }

    [Fact]
    public async Task After_a_restart_the_card_buttons_still_work_passed_deadlines_are_locked_and_an_open_entry_form_still_submits()
    {
        var due = await _kit.CreatePredictionAsync(lockAt: "24.09.2026 16:00");
        var open = await _kit.CreatePredictionAsync(title: "Kilidi olmayan öngörü");
        var settled = await _kit.CreatePredictionAsync(title: "Sonuçlanmış öngörü başlığı");
        (await _kit.EnterAsync(Member(3), settled, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.SettleAsync(Admin(), settled, 1)).Result.Succeeded.Should().BeTrue();
        (await _kit.OpenEntryAsync(Member(), open)).Form.Should().NotBeNull();

        await using var second = await PredictionTestKit.CreateAsync(TestHost.T0.AddHours(2), directory: _kit.Host.Directory, transport: _kit.Transport);
        await second.TickAsync();
        (await second.RowAsync(due.Id)).Status.Should().Be(PredictionStatus.Locked);
        (await second.SubmitEntryAsync(Member(), open, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.done", "the submit carries everything; nothing was in memory");
        (await second.EnterAsync(Member(), open, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.current", "Tahmin Yap shows the entry after a restart");
        (await second.EnterAsync(Member(4), open, 2, "10")).Result.MessageKey.Should().Be("predictions.entry.done", "Tahmin Yap works after a restart");
        (await second.EnterAsync(Member(2), due, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await second.LockAsync(Creator(), open)).MessageKey.Should().Be("predictions.lock.done", "management buttons carry only the number");
        (await second.SettleAsync(Admin(), settled, 2)).Result.MessageKey.Should().Be("predictions.state.already_settled");
        (await second.ConfirmSettleAsync(Admin(), settled, 2)).Result.MessageKey.Should().Be("predictions.state.already_settled");
        (await second.WalletAsync(3))!.BalanceMinor.Should().Be(101_000, "a terminal card never pays again");
    }

    // ---- settle / cancel ----

    [Fact]
    public async Task Settling_pays_stake_times_odds_exactly_once_and_a_repeated_confirmation_pays_nothing()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 2, "50")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(3), prediction, 1, "12,5")).Result.Succeeded.Should().BeTrue();

        var picker = await _kit.Service(s => s.StartSettleAsync(Admin(), Predictions, prediction.Id, prediction.Message, Ct));
        picker.View!.Select!.Options.Select(o => o.Label).Should().Equal("Galatasaray Kazanır — 1.10", "Beraberlik — 2.30", "Fenerbahçe Kazanır — 3.10");
        var (confirm, preview) = await _kit.SettlePreviewAsync(Admin(), prediction, 1);
        confirm.Should().Be(Id(prediction.Id) + "." + Id(prediction.Outcomes[0].Id));
        preview.View!.Embed!.Description.Should().Contain("“Galatasaray Kazanır” (1.10) kazanan sonuç olarak işaretlenecek.");
        preview.View.Embed.Fields.Select(f => (f.Name, f.Value)).Should().Contain(new[]
        {
            ("Kazanan tahmin", "2"), ("Kaybeden tahmin", "1"), ("Toplam ödeme", "123,75 TSQ Coin"),
        });
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Open, "the preview changes nothing");

        var done = await _kit.ConfirmSettleAsync(Admin(), prediction, 1);
        done.Result.MessageKey.Should().Be("predictions.settle.done");
        done.Result.Args.Should().Equal(Id(prediction.Id), 2, "123,75");

        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(101_000, "1000 − 100 + 110: the 110 includes the stake");
        (await _kit.WalletAsync(2))!.BalanceMinor.Should().Be(95_000, "the stake was debited once, on entry");
        (await _kit.WalletAsync(3))!.BalanceMinor.Should().Be(100_125);
        ((await _kit.WalletAsync(1))!.CorrectCount, (await _kit.WalletAsync(1))!.SettledCount).Should().Be((1, 1));
        ((await _kit.WalletAsync(2))!.CorrectCount, (await _kit.WalletAsync(2))!.SettledCount).Should().Be((0, 1));
        (await _kit.LedgerAsync(1)).Select(l => (l.Kind, l.AmountMinor)).Should().Equal(
            (PredictionLedgerKind.Initial, 100_000L), (PredictionLedgerKind.Stake, -10_000L), (PredictionLedgerKind.Payout, 11_000L));

        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.WinningOutcomeId, row.WinnerCount, row.PayoutTotalMinor, row.SettledByUserId).Should()
            .Be((PredictionStatus.Settled, prediction.Outcomes[0].Id, 2, 12_375L, Admin().UserId.Value));
        var card = Shown(_kit.Card(prediction));
        card.Embed!.Title.Should().Be("✅ Sonuçlandı");
        card.Embed.Description.Should().Contain("🏆 **Galatasaray Kazanır**");
        card.Embed.Fields.Single(f => f.Name == "🏆 Sonuç").Value.Should()
            .Be("🏆 **Galatasaray Kazanır** — 1.10\n2 kazanan · Toplam ödeme **123,75 TSQ Coin**\n👥 3 katılım · 🪙 162,50 TSQ Coin yatırılmıştı");
        card.Buttons.Should().BeNull("a settled card offers nothing more");

        (await _kit.ConfirmSettleAsync(Admin(), prediction, 1)).Result.MessageKey.Should().Be("predictions.state.already_settled", "a replayed confirmation");
        (await _kit.ConfirmSettleAsync(Admin(), prediction, 2)).Result.MessageKey.Should().Be("predictions.state.already_settled");
        (await _kit.Service(s => s.StartSettleAsync(Admin(), Predictions, prediction.Id, prediction.Message, Ct))).Result.MessageKey.Should().Be("predictions.state.already_settled");
        (await _kit.Service(s => s.StartCancelAsync(Admin(), Predictions, prediction.Id, prediction.Message, Ct)))!.MessageKey.Should().Be("predictions.state.settled_no_cancel");
        (await _kit.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.Kind == PredictionLedgerKind.Payout))).Should().Be(2, "no second payment");
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(101_000);
    }

    [Fact]
    public async Task Nobody_on_the_winning_outcome_settles_without_payouts_or_refunds()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 2, "100")).Result.Succeeded.Should().BeTrue();

        (await _kit.SettleAsync(Creator(), prediction, 3)).Result.MessageKey.Should().Be("predictions.settle.done_nobody");
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(90_000);
        (await _kit.WalletAsync(2))!.BalanceMinor.Should().Be(90_000);
        ((await _kit.WalletAsync(1))!.SettledCount, (await _kit.WalletAsync(1))!.CorrectCount).Should().Be((1, 0));
        (await _kit.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.Kind == PredictionLedgerKind.Payout || l.Kind == PredictionLedgerKind.Refund))).Should().Be(0);
    }

    [Fact]
    public async Task Settling_an_open_prediction_closes_entries_at_once()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.OpenEntryAsync(Member(), prediction)).Form.Should().NotBeNull();
        (await _kit.SettleAsync(Admin(), prediction, 2)).Result.Succeeded.Should().BeTrue();
        (await _kit.SubmitEntryAsync(Member(), prediction, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
    }

    [Fact]
    public async Task Cancelling_refunds_exactly_the_stakes_once_never_a_possible_payout_and_changes_no_statistics()
    {
        var prediction = await _kit.CreatePredictionAsync(outcomes: "Evet | 3.00\nHayır | 1.50");
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue(); // a possible 300
        (await _kit.EnterAsync(Member(2), prediction, 2, "33,33")).Result.Succeeded.Should().BeTrue();

        var (token, preview) = await _kit.CancelPreviewAsync(Creator(), prediction, "Maç ertelendi");
        preview.View!.Embed!.Description.Should().Contain("Bu öngörü iptal edilecek.").And.Contain("Toplam 133,33 TSQ Coin yatırımı oyunculara tam olarak iade edilecek.")
            .And.Contain("Bu işlem geri alınamaz.");
        preview.View.Buttons!.Select(b => b.Label).Should().Equal("↩️ İptal Et ve İade Et", "Vazgeç");
        var done = await _kit.Service(s => s.ConfirmCancelAsync(Creator(), Predictions, token!, Ct));
        done.Result.Args.Should().Equal(Id(prediction.Id), 2, "133,33");
        foreach (var user in new ulong[] { 1, 2 })
        {
            var wallet = (await _kit.WalletAsync(user))!;
            (wallet.BalanceMinor, wallet.PendingMinor, wallet.SettledCount, wallet.CorrectCount).Should().Be((100_000L, 0L, 0, 0), "the stake back, not 300");
        }

        (await _kit.Db(db => db.Set<PredictionLedgerEntity>().Where(l => l.Kind == PredictionLedgerKind.Refund).Select(l => l.AmountMinor).ToListAsync()))
            .Should().BeEquivalentTo([10_000L, 3_333L]);
        var card = Shown(_kit.Card(prediction));
        card.Embed!.Title.Should().Be("⚠️ Öngörü İptal Edildi");
        card.Embed.Fields.Single(f => f.Name == "Durum").Value.Should().Be("İptal nedeni: Maç ertelendi\nYatırılan **133,33 TSQ Coin** oyunculara tam olarak iade edildi.");
        card.Buttons.Should().BeNull();

        (await _kit.Service(s => s.ConfirmCancelAsync(Creator(), Predictions, token!, Ct))).Result.MessageKey.Should().Be("predictions.confirm.expired");
        (await _kit.Service(s => s.StartCancelAsync(Creator(), Predictions, prediction.Id, prediction.Message, Ct)))!.MessageKey.Should().Be("predictions.state.cancelled");
        (await _kit.Service(s => s.PreviewCancelAsync(Admin(), Predictions, prediction.Id, "tekrar", Ct))).Result.MessageKey.Should().Be("predictions.state.cancelled");
        (await _kit.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.Kind == PredictionLedgerKind.Refund))).Should().Be(2, "no second refund");
    }

    [Fact]
    public async Task A_cancel_reason_is_required()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.Service(s => s.PreviewCancelAsync(Creator(), Predictions, prediction.Id, "  a ", Ct))).Result.MessageKey.Should().Be("predictions.cancel.reason_length");
    }

    [Fact]
    public async Task Settle_and_cancel_racing_end_in_exactly_one_terminal_state_with_one_kind_of_booking()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.EnterAsync(Member(2), prediction, 2, "100")).Result.Succeeded.Should().BeTrue();
        var cancel = await _kit.CancelTokenAsync(Creator(), prediction);

        var results = await _kit.TogetherAsync(
            () => _kit.ConfirmSettleAsync(Admin(), prediction, 1),
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
        Shown(_kit.Card(prediction)).Embed!.Fields.Should().Contain(f => f.Value == "3 katılımcı\n🪙 30 TSQ Coin yatırıldı");
        Shown(_kit.Card(prediction)).Mentions.Should().Be(MentionPolicy.None);
    }

    [Fact]
    public async Task A_deleted_card_stops_entries_keeps_stakes_and_gets_one_replacement_card_to_manage_it_from()
    {
        var prediction = await _kit.CreatePredictionAsync();
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        _kit.Transport.DeleteMessage(prediction.Message!.Value);

        await _kit.TickAsync(); // presence check of open cards, then the replacement
        var row = await _kit.RowAsync(prediction.Id);
        (row.Status, row.LockReason, row.CardMissing).Should().Be((PredictionStatus.Locked, PredictionLockReason.CardMissing, false));
        row.MessageId.Should().NotBe(prediction.Message!.Value.Value);
        var replacement = _kit.Transport.Messages.Should().ContainSingle().Subject;
        replacement.Id.Value.Should().Be(row.MessageId!.Value);
        replacement.Message.Embed!.Fields.Should().Contain(f => f.Value == "Önceki kart silindiği için katılım durduruldu; öngörü bu karttan yönetilir.");
        replacement.Message.Buttons!.Select(b => (b.Label, b.Disabled)).Should().Equal(("🔒 Katılım kapandı", true), ("✅ Sonuçlandır", false), ("↩️ İptal / İade", false));
        var current = prediction with { Message = new MessageId(row.MessageId!.Value) };
        (await _kit.EnterAsync(Member(2), current, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.WalletAsync(1))!.PendingMinor.Should().Be(10_000, "stakes are kept");

        await _kit.TickAsync(TimeSpan.FromMinutes(30));
        _kit.Transport.Messages.Should().ContainSingle("one replacement, never a stream of cards");
        (await _kit.Service(s => s.StartSettleAsync(Admin(), Predictions, prediction.Id, prediction.Message, Ct))).Result.MessageKey
            .Should().Be("predictions.not_found", "the deleted card's buttons no longer act");
        (await _kit.SettleAsync(Admin(), current, 1)).Result.MessageKey.Should().Be("predictions.settle.done");
        (await _kit.WalletAsync(1))!.BalanceMinor.Should().Be(101_000);
    }

    [Fact]
    public async Task Rate_limits_server_errors_and_unreadable_cards_are_never_taken_for_deletion()
    {
        var prediction = await _kit.CreatePredictionAsync();
        _kit.Transport.ScriptedPresence = MessagePresence.Unknown;
        _kit.Transport.ScriptEdit(() => new SendOutcome.RateLimited(TimeSpan.FromSeconds(5)));
        _kit.Transport.ScriptEdit(() => new SendOutcome.Transient("503"));
        (await _kit.LockAsync(Creator(), prediction)).MessageKey.Should().Be("predictions.lock.done");

        await _kit.TickAsync();
        var row = await _kit.RowAsync(prediction.Id);
        (row.CardMissing, row.CardStale, row.Status).Should().Be((false, true, PredictionStatus.Locked));
        await _kit.TickAsync();
        (await _kit.RowAsync(prediction.Id)).CardStale.Should().BeFalse();
        Shown(_kit.Card(prediction)).Buttons![0].Disabled.Should().BeTrue();
    }

    [Fact]
    public async Task Disabling_the_module_stops_new_activity_but_keeps_coins_and_deadlines()
    {
        var prediction = await _kit.CreatePredictionAsync(lockAt: "24.09.2026 16:00");
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        await _kit.SetEnabledAsync(false);

        (await _kit.EnterAsync(Member(2), prediction, 1, "100")).Result.MessageKey.Should().Be("error.module_disabled");
        (await _kit.SubmitEntryAsync(Member(2), prediction, 1, "100")).Result.MessageKey.Should().Be("error.module_disabled");
        (await _kit.ChangeAsync(Member(1), prediction, 2, "100")).Result.MessageKey.Should().Be("error.module_disabled");
        (await _kit.WithdrawAsync(Member(1), prediction)).Result.MessageKey.Should().Be("error.module_disabled");
        (await _kit.ClaimDailyAsync(Member(1))).Result.MessageKey.Should().Be("error.module_disabled");
        (await _kit.Service(s => s.OpenFormAsync(Creator(), Predictions, Ct))).Refusal!.MessageKey.Should().Be("error.module_disabled");
        (await _kit.Service(s => s.PromptLockAsync(Creator(), Predictions, prediction.Id, prediction.Message, Ct))).Result.MessageKey.Should().Be("error.module_disabled");

        await _kit.TickAsync(TimeSpan.FromHours(2));
        (await _kit.RowAsync(prediction.Id)).Status.Should().Be(PredictionStatus.Locked, "a passed deadline is still honoured");
        await _kit.SetEnabledAsync(true);
        var wallet = (await _kit.WalletAsync(1))!;
        (wallet.BalanceMinor, wallet.PendingMinor).Should().Be((90_000L, 10_000L));
        (await _kit.SettleAsync(Creator(), prediction, 1)).Result.Succeeded.Should().BeTrue();
    }

    // ---- privacy ----

    [Fact]
    public async Task Privacy_export_lists_the_members_records_and_deletion_removes_names_but_never_resets_coins()
    {
        var prediction = await _kit.CreatePredictionAsync(creator: Creator(10));
        (await _kit.EnterAsync(Member(1), prediction, 1, "100")).Result.Succeeded.Should().BeTrue();
        (await _kit.ClaimDailyAsync(Member(1))).Result.Succeeded.Should().BeTrue();
        var before = (await _kit.WalletAsync(1))!;

        await _kit.Host.InScopeAsync(async sp =>
        {
            var privacy = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "predictions");
            var json = await privacy.ExportAsync(Guild, new UserId(1), Ct);
            json.ToJsonString().Should().Contain("\"wallets\"").And.Contain("\"stakeUnits\":10000").And.Contain("\"kind\":\"Daily\"");
            json["wallets"]![0]!["displayName"]!.GetValue<string>().Should().Be("Üye 1");
            (await privacy.PreviewDeletionAsync(Guild, new UserId(1), Ct)).Select(i => i.LabelKey).Should().Equal("predictions.privacy.names", "predictions.privacy.kept");
            var report = await privacy.DeleteAsync(Guild, new UserId(1), Ct);
            report.Warnings.Should().ContainSingle().Which.Should().Contain("TSQ Coin sıfırlanamaz");
            await privacy.DeleteAsync(Guild, new UserId(10), Ct);
        });

        var after = (await _kit.WalletAsync(1))!;
        (after.Id, after.BalanceMinor, after.PendingMinor, after.DisplayName).Should().Be((before.Id, before.BalanceMinor, before.PendingMinor, (string?)null));
        (await _kit.LedgerAsync(1)).Count(l => l.Kind == PredictionLedgerKind.Initial).Should().Be(1);
        (await _kit.ClaimDailyAsync(Member(1))).Result.MessageKey.Should().Be("predictions.daily.already", "the day's claim is kept");
        (await _kit.RowAsync(prediction.Id)).CreatorName.Should().BeEmpty();
        await _kit.TickAsync(TimeSpan.FromSeconds(11));
        Shown(_kit.Card(prediction)).Embed!.Footer.Should().EndWith("Oluşturan: — · Sabit oran");
        (await _kit.LockAsync(Creator(10), prediction)).MessageKey.Should().Be("predictions.lock.done", "the creator still manages their card");
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

        var report = await _kit.Host.Services.GetServices<IModuleHealthCheck>().Single(h => h.Module.Value == "predictions").CheckAsync(Ct);
        report.Entries.Should().Contain(e => e.Component == "predictions.health.channels" && e.State == HealthState.Degraded);
        report.Entries.Single(e => e.Component == "predictions.health.cards").Args.Should().Equal(1, 0, 0);
        report.Entries.Should().NotContain(e => e.Component == "predictions.health.consistency");
    }
}
