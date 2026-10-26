using System.Net;
using System.Text;
using Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ToroSquad.Ai;
using ToroSquad.Core;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor;
using ToroSquad.Modules.Sor.Application;
using ToroSquad.Modules.Sor.Commands;
using ToroSquad.Modules.Sor.Domain;
using ToroSquad.Modules.Sor.Persistence;
using ToroSquad.Modules.Summary.Application;
using ToroSquad.Modules.Summary.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.SorTestData;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The closing review of TSQ Sor: what the validator refuses when the model proposes a wrong note, the conversation session
/// id, derived context after B changes a shared note, every gap between the AI answer, Discord and the database, the scope
/// of the concurrency limit next to TSQ Özet, a start without a key, and the real Discord adapter's calls and payloads.
/// </summary>
public sealed class SorClosureTests
{
    private const ulong A = 101, B = 102;

    // ------------------------------------------------------------------ wrong proposals from the model

    [Theory]
    [InlineData("Mert, ‘Ben strateji oyunlarını seviyorum’ dedi.", "oyun", "Strateji oyunlarını seviyor.", "Ben strateji oyunlarını seviyorum")]
    [InlineData("Farz et C# biliyorum. Bir API nasıl yazılır?", "tercih", "C# biliyor.", "C# biliyorum")]
    [InlineData("Şakaydı; beni Galatasaraylı diye kaydetme.", "ilgi", "Galatasaraylı.", "beni Galatasaraylı")]
    [InlineData("aynen", "oyun", "Strateji oyunlarını seviyor.", "aynen")]
    [InlineData("Artık kısa cevap istemiyorum; ayrıntılı anlat.", "cevap_tarzi", "Kısa cevap istiyor.", "kısa cevap istemiyorum")]
    public async Task A_wrong_proposal_of_the_model_is_not_saved(string question, string kind, string note, string evidence)
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("Tamam.", Memory(("add", kind, note, evidence, null)));
        var result = await kit.AskAsync(SorKit.User(A), question);
        result.Delivered.Should().ContainSingle("the answer is still shown");
        result.Footers.Should().BeEmpty("nothing was saved, so nothing claims it");
        (await NotesAsync(kit, A)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_clear_style_correction_replaces_the_old_preference()
    {
        await using var kit = await SorKit.CreateAsync();
        await SeedNoteAsync(kit, A, "Kısa cevap istiyor.", SorMemoryScope.Channel, SorKit.Channel, SorMemoryKind.AnswerStyle);
        kit.Ai.Answer("Olur, ayrıntılı anlatırım.", Memory(("update", "cevap_tarzi", "Ayrıntılı cevap istiyor.", "ayrıntılı anlat", null)));
        var result = await kit.AskAsync(SorKit.User(A), "Artık kısa cevap istemiyorum; ayrıntılı anlat.");
        result.Footers.Should().Equal("🧠 1 not güncellendi");
        (await NotesAsync(kit, A)).Should().ContainSingle().Which.Text.Should().Be("Ayrıntılı cevap istiyor.");
    }

    // ------------------------------------------------------------------ x-opencode-session

    [Fact]
    public async Task The_session_id_is_stable_per_conversation_also_after_a_restart_and_differs_everywhere_else()
    {
        await using var kit = await SorKit.CreateAsync();
        await kit.AskAsync(SorKit.User(A), "Bir?");
        await kit.AskAsync(SorKit.User(A), "İki?");
        var first = kit.Ai.Requests[0].SessionId;
        first.Should().NotBeNull().And.MatchRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        OpenCodeGo.IsValidSessionId(first).Should().BeTrue();
        kit.Ai.Requests[1].SessionId.Should().Be(first, "the same conversation");
        first.Should().NotContain(A.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.NotContain(SorKit.Guild.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await using (var restarted = await SorKit.CreateAsync(directory: kit.Host.Directory))
        {
            await restarted.AskAsync(SorKit.User(A), "Üç?");
            restarted.Ai.Requests.Single().SessionId.Should().Be(first, "the conversation continues after a restart");
        }

        await kit.AskAsync(SorKit.User(B), "Başka üye?", name: "Mert");
        await kit.AskAsync(kit.Ask(SorKit.User(A), "Gizli?", isPrivate: true));
        await kit.AskAsync(kit.Ask(SorKit.User(A, SorKit.OtherGuild), "Başka sunucu?"));
        await kit.AskAsync(SorKit.User(A), "Yeni?", isNew: true);
        kit.Ai.Requests.Skip(2).Select(r => r.SessionId).Should().OnlyHaveUniqueItems().And.NotContain(first);
    }

    // ------------------------------------------------------------------ derived context after B changes a shared note

    [Theory]
    [InlineData("delete")]
    [InlineData("edit")]
    [InlineData("unshare")]
    [InlineData("disable")]
    public async Task After_b_changes_a_shared_note_neither_the_note_nor_any_derived_turn_comes_back(string change)
    {
        await using var kit = await SorKit.CreateAsync();
        var id = await SeedNoteAsync(kit, B, "Valorant oynuyor.", SorMemoryScope.Shared);
        var mert = new SorPerson(B, "Mert", false);
        kit.Ai.Answer("Mert Valorant oynuyor; Valorant temalı bir hediye olur.").Answer("Valorant için bir kulaklık da düşünebilirsin.");
        await kit.AskAsync(kit.Ask(SorKit.User(A), "Mert'e ne hediye alayım?", about: mert));
        await kit.AskAsync(SorKit.User(A), "Başka fikir?"); // the note is not selected here, but turn 1 is in the history
        kit.Ai.LastSeen.Should().Contain("Valorant", "turn 2 used turn 1, which was derived from B's note");

        var stamp = (await NotesAsync(kit, B)).Single().Stamp;
        var actorB = SorKit.User(B);
        (await kit.MemoryAsync(m => change switch
        {
            "delete" => m.DeleteAsync(actorB, id, stamp, CancellationToken.None),
            "edit" => m.EditAsync(actorB, id, stamp, "Artık CS2 oynuyor.", CancellationToken.None),
            "unshare" => m.UnshareAsync(actorB, id, stamp, CancellationToken.None),
            _ => m.SetEnabledAsync(actorB, false, CancellationToken.None),
        })).Succeeded.Should().BeTrue();

        (await ConversationsAsync(kit, A)).Should().BeEmpty("the whole conversation that used the note is cleared, not only its first turn");
        await kit.AskAsync(SorKit.User(A), "Devam edelim?");
        await kit.AskAsync(kit.Ask(SorKit.User(A), "Mert ne sever?", about: mert));
        kit.Ai.Requests.TakeLast(2).Should().OnlyContain(r => !FakeSorAi.Seen(r).Contains("Valorant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_memory_off_and_on_again_during_a_request_never_makes_the_old_request_valid()
    {
        await using var kit = await SorKit.CreateAsync();
        await SeedNoteAsync(kit, B, "Valorant oynuyor.", SorMemoryScope.Shared);
        kit.Ai.Answer("Mert Valorant oynuyor.");
        kit.Ai.During = async () =>
        {
            await kit.MemoryAsync(m => m.SetEnabledAsync(SorKit.User(B), false, CancellationToken.None));
            await kit.MemoryAsync(m => m.SetEnabledAsync(SorKit.User(B), true, CancellationToken.None));
        };
        var result = await kit.AskAsync(kit.Ask(SorKit.User(A), "Mert ne oynar?", about: new SorPerson(B, "Mert", false)));
        result.Delivered.Should().BeEmpty("a new stamp after switching on again never equals the one the request started with");
        (await RequestsAsync(kit, A)).Single().State.Should().Be(SorRequestState.Discarded);
    }

    // ------------------------------------------------------------------ AI → Discord → database

    [Fact]
    public async Task A_deletion_between_delivery_and_saving_saves_neither_the_turn_nor_the_note()
    {
        await using var kit = await SorKit.CreateAsync();
        var id = await SeedNoteAsync(kit, A, "Satranç oynuyor.", SorMemoryScope.Channel, SorKit.Channel);
        var stamp = (await NotesAsync(kit, A)).Single().Stamp;
        kit.Ai.Answer("Satranç için bir öneri.", Memory(Add("hobi", "Bulmaca çözüyor.", "bulmaca çözüyorum")));
        var responder = new RecordingSorResponder
        {
            AfterDelivery = () => kit.MemoryAsync(m => m.DeleteAsync(SorKit.User(A), id, stamp, CancellationToken.None)),
        };
        await kit.Host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(kit.Ask(SorKit.User(A), "Bulmaca çözüyorum, ne dersin?"), responder));

        responder.Delivered.Should().ContainSingle("already shown on Discord — a sent message cannot be taken back");
        responder.Footers.Should().BeEmpty();
        (await NotesAsync(kit, A)).Should().BeEmpty("the deleted note is not written back and the new one is not saved");
        (await TurnsAsync(kit, A)).Should().BeEmpty();
        (await RequestsAsync(kit, A)).Single().Should().Match<SorRequestEntity>(r => r.State == SorRequestState.Completed && r.Failure == "stale-after-delivery" && r.PendingAnswer == null);
        kit.Ai.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_database_failure_after_delivery_never_generates_again_and_the_pending_answer_expires()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("Gösterildi.", Memory(Add("oyun", "Satranç oynuyor.", "satranç oynuyorum")));
        var responder = new RecordingSorResponder
        {
            AfterDelivery = () => kit.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Database.ExecuteSqlRawAsync("ALTER TABLE sor_turn RENAME TO sor_turn_broken")),
        };
        await kit.Host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(kit.Ask(SorKit.User(A), "Ben satranç oynuyorum."), responder));
        await kit.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Database.ExecuteSqlRawAsync("ALTER TABLE sor_turn_broken RENAME TO sor_turn"));

        responder.Delivered.Should().ContainSingle();
        responder.Footers.Should().BeEmpty("the save failed: no 'saved' line");
        (await NotesAsync(kit, A)).Should().BeEmpty("the note was in the rolled-back transaction");
        (await RequestsAsync(kit, A)).Single().State.Should().Be(SorRequestState.Generated);
        kit.Ai.Requests.Should().ContainSingle("the saving step is retried without the model");

        kit.Host.Clock.Advance(SorCleanup.PendingLifetime + TimeSpan.FromMinutes(1));
        await kit.InScopeAsync(sp => sp.GetRequiredService<SorCleanup>().RunOnceAsync(CancellationToken.None));
        (await RequestsAsync(kit, A)).Single().Should().Match<SorRequestEntity>(r => r.State == SorRequestState.Unknown && r.PendingAnswer == null && r.PendingMemory == null);
        (await UsageAsync(kit)).Single().Requests.Should().Be(1, "it reached the provider: still counted");
        kit.Ai.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_saved_line_edit_repeats_neither_the_save_nor_the_generation()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("Tamam.", Memory(Add("oyun", "Satranç oynuyor.", "satranç oynuyorum")));
        var responder = new RecordingSorResponder { FooterFails = true };
        await kit.Host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(kit.Ask(SorKit.User(A), "Ben satranç oynuyorum."), responder));
        (await NotesAsync(kit, A)).Should().ContainSingle();
        (await RequestsAsync(kit, A)).Single().State.Should().Be(SorRequestState.Completed);
        kit.Ai.Requests.Should().ContainSingle();
    }

    // ------------------------------------------------------------------ concurrency scope and start-up

    [Fact]
    public async Task Sors_single_slot_is_its_own_while_a_sor_call_waits_tsq_ozet_still_runs_within_its_own_limits()
    {
        var handler = new OkHandler();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var kit = await SorKit.CreateAsync(
            new() { [OpenCodeGo.ApiKeyVariable] = "test-key-not-a-real-secret-0123456789" },
            replace: s => s.AddHttpClient(OpenCodeSummaryAiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));
        kit.Ai.Answer("Bekledim.");
        kit.Ai.During = async () =>
        {
            started.SetResult();
            await release.Task;
        };

        var running = kit.AskAsync(SorKit.User(A), "Uzun sürecek soru?");
        await started.Task;

        var busy = await kit.AskAsync(kit.Ask(SorKit.User(B), "Ben de sorayım?", channel: SorKit.OtherChannel, name: "Mert"));
        busy.Refusals.Should().ContainSingle().Which.Should().Contain("başka bir soruyu cevaplıyor", "Sor's own limit is 1");

        var summary = kit.Host.Services.GetRequiredService<ISummaryAiClient>();
        (await summary.SummarizeAsync(SummaryPrompt.Build("Mert: selam\nAyşe: naber"), CancellationToken.None)).Succeeded.Should().BeTrue(
            "TSQ Özet shares the transport, not Sor's limiter");
        var (admission, ticket, _) = kit.Host.Services.GetRequiredService<SummaryThrottle>().TryEnter(7, 8);
        admission.Should().Be(SummaryAdmission.Admitted);
        ticket!.Dispose();
        handler.Calls.Should().Be(1);

        release.SetResult();
        (await running).Delivered.Should().ContainSingle();
    }

    [Fact]
    public async Task Without_a_key_the_bot_starts_sor_refuses_without_a_request_and_the_doctor_says_so_without_spending_quota()
    {
        await using var host = await TestHost.CreateAsync();
        var lines = await host.InScopeAsync(sp => sp.GetRequiredService<SorConfigService>().DoctorAsync(TestHost.Admin(SorKit.Guild), CancellationToken.None));
        lines[0].Should().Match<SorDoctorLine>(l => !l.Ok && l.Key == "admin.sor.doctor.key_missing");
        lines[^1].Key.Should().Be("admin.sor.doctor.no_request");
        lines.Where(l => l.Key != "admin.sor.doctor.key_missing").Should().OnlyContain(l => l.Ok);

        var responder = new RecordingSorResponder();
        await host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(new SorAsk(SorKit.User(A), 1, SorKit.Channel, null, "Merhaba?", false, false, null,
            "Ada", null, "tr", TimeZoneInfo.Utc), responder));
        responder.Refusals.Should().ContainSingle().Which.Should().Contain("yapılandırılmamış");
        responder.Deferred.Should().BeEmpty();
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"# Özet"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    // ------------------------------------------------------------------ the real Discord adapter (offline)

    private static (SorInteractionResponder Responder, InteractionRecorder Recorder) Adapter(ulong id)
    {
        var interaction = InteractionRecorder.Create(out var recorder, new() { ["Id"] = id });
        return (new SorInteractionResponder(interaction, key => key, NullLogger.Instance), recorder);
    }

    private static void NoPings(AllowedMentions? mentions)
    {
        mentions.Should().NotBeNull();
        mentions!.AllowedTypes.Should().Be(AllowedMentionTypes.None);
        (mentions.UserIds ?? []).Should().BeEmpty();
        (mentions.RoleIds ?? []).Should().BeEmpty();
        mentions.MentionRepliedUser.Should().BeFalse();
    }

    [Fact]
    public async Task A_private_question_is_deferred_privately_and_every_later_call_stays_private_and_ping_free()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("Gizli cevap @everyone <@123456789012345678>\n\n" + new string('x', 5000));
        var (responder, recorder) = Adapter(1);
        await kit.Host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(kit.Ask(SorKit.User(A), "Gizli soru", isPrivate: true), responder));

        // defer → the card (original response) → its second part → the first-use note; nothing else.
        recorder.Calls.Select(c => c.Method).Should().Equal("DeferAsync", "ModifyOriginalResponseAsync", "FollowupAsync", "FollowupAsync");
        InteractionRecorder.Arg(recorder.Calls[0], "ephemeral", typeof(IDiscordInteraction)).Should().Be(true);
        foreach (var followup in recorder.Calls.Where(c => c.Method == "FollowupAsync"))
        {
            InteractionRecorder.Arg(followup, "ephemeral", typeof(IDiscordInteraction)).Should().Be(true, "never a channel message");
            NoPings((AllowedMentions?)InteractionRecorder.Arg(followup, "allowedMentions", typeof(IDiscordInteraction)));
        }

        foreach (var edit in recorder.Calls.Where(c => c.Method == "ModifyOriginalResponseAsync"))
            NoPings(edit.Edit!.AllowedMentions.GetValueOrDefault());
        var card = recorder.Calls[1].Edit!;
        card.Embeds.Value.Should().ContainSingle().Which.Title.Should().Be(SorUi.Title);
        card.Components.Value.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Select(b => b.CustomId)
            .Should().Contain(SorUi.MemoryId).And.Contain(id => id!.StartsWith(SorUi.ContinuePrefix, StringComparison.Ordinal) && id.EndsWith(":101:p", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_after_a_channel_deferral_ends_the_original_with_a_neutral_line_and_sends_the_detail_privately()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Fail(AiFailure.ServerError, 503);
        var (responder, recorder) = Adapter(2);
        await kit.Host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(kit.Ask(SorKit.User(A), "Açık soru"), responder));

        recorder.Calls.Select(c => c.Method).Should().Equal("DeferAsync", "ModifyOriginalResponseAsync", "FollowupAsync");
        InteractionRecorder.Arg(recorder.Calls[0], "ephemeral", typeof(IDiscordInteraction)).Should().Be(false);
        var original = recorder.Calls[1].Edit!;
        original.Content.Value.Should().Be("TSQ Sor bu isteği tamamlayamadı.");
        original.Embeds.Value.Should().BeEmpty();
        original.Components.Value.Components.Should().BeEmpty("the loading state is closed, nothing half-done stays");
        InteractionRecorder.Arg(recorder.Calls[2], "ephemeral", typeof(IDiscordInteraction)).Should().Be(true);
        ((string)InteractionRecorder.Arg(recorder.Calls[2], "text", typeof(IDiscordInteraction))!).Should().Contain("ulaşılamıyor");
    }

    [Fact]
    public async Task A_refusal_is_the_first_response_and_private_and_an_unconfirmed_delivery_is_never_counted_as_history()
    {
        await using var kit = await SorKit.CreateAsync();
        var (refuser, refused) = Adapter(3);
        await kit.Host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(kit.Ask(SorKit.User(A), "   "), refuser));
        refused.Calls.Select(c => c.Method).Should().Equal("RespondAsync");
        InteractionRecorder.Arg(refused.Calls[0], "ephemeral", typeof(IDiscordInteraction)).Should().Be(true);

        kit.Ai.Answer("Belki gitti.");
        var (responder, recorder) = Adapter(4);
        recorder.Throws["ModifyOriginalResponseAsync"] = new TaskCanceledException("no confirmation (test)");
        await kit.Host.InScopeAsync(sp => sp.GetRequiredService<SorService>().AskAsync(kit.Ask(SorKit.User(A), "Belirsiz?"), responder));
        recorder.Calls.Select(c => c.Method).Should().StartWith(["DeferAsync", "ModifyOriginalResponseAsync", "GetOriginalResponseAsync"]);
        (await TurnsAsync(kit, A)).Should().BeEmpty();
        (await RequestsAsync(kit, A)).Single().State.Should().Be(SorRequestState.Unknown);
        kit.Ai.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Only_the_owner_passes_the_form_gate_and_opening_or_dismissing_a_form_changes_nothing()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("Bir.").Answer("İki.");
        var first = await kit.AskAsync(SorKit.User(A), "Bir?");
        var conversation = first.Delivered.Single().ConversationId;
        var args = SorUi.Args(conversation, A, false);

        await kit.InScopeAsync(async sp =>
        {
            var store = sp.GetRequiredService<SorStore>();
            (await SorConversationGate.RefusalAsync(store, SorKit.Guild.Value, A, args, CancellationToken.None)).Should().BeNull();
            (await SorConversationGate.RefusalAsync(store, SorKit.Guild.Value, B, args, CancellationToken.None)).Should().Be("sor.not_your_conversation");
            (await SorConversationGate.RefusalAsync(store, SorKit.Guild.Value, B, SorUi.Args(conversation, B, false), CancellationToken.None))
                .Should().Be("sor.not_your_conversation", "a forged owner in the button does not beat the stored owner");
            (await SorConversationGate.RefusalAsync(store, SorKit.Guild.Value, A, SorUi.Args(999_999, A, false), CancellationToken.None))
                .Should().BeNull("an expired conversation: the owner may start over");
            (await SorConversationGate.RefusalAsync(store, SorKit.Guild.Value, A, "x:y", CancellationToken.None)).Should().Be("sor.button_invalid");
            return 0;
        });

        // "Yeni sohbet" clicked and its form dismissed: only the gate and the form were built — the conversation stays open.
        SorUi.FormFor(args, isNew: true, key => key, 4000).CustomId.Should().Be(SorUi.NewModalPrefix + args);
        (await ConversationsAsync(kit, A)).Single().State.Should().Be(SorConversationState.Open);
        var next = await kit.AskAsync(SorKit.User(A), "İki?");
        next.Delivered.Single().ConversationId.Should().Be(conversation);
    }
}
