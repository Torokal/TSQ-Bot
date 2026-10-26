using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Ai;
using ToroSquad.Modules.Sor.Application;
using ToroSquad.Modules.Sor.Domain;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.SorTestData;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Sor end to end against the real database with a scripted provider: answers and follow-ups, new conversations,
/// persistence and retention, preference updates, identity (same names, renames, other guilds) and the memory scopes. The
/// assertions look at what the model was actually sent and at what was actually stored — not only at call counts.
/// </summary>
public sealed class SorFlowTests
{
    private const ulong A = 101, B = 102, C = 103;

    // ------------------------------------------------------------------ product

    [Fact]
    public async Task A_question_is_answered_and_a_follow_up_continues_the_same_members_conversation()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("En sevilen üç strateji oyunu: Civilization VI, Age of Empires II ve Stellaris.").Answer("İkincisi, yani Age of Empires II, gerçek zamanlıdır.");

        var first = await kit.AskAsync(SorKit.User(A), "En iyi üç strateji oyunu hangileri?");
        first.Deferred.Should().Equal(false);
        first.Delivered.Should().ContainSingle();
        first.Answer.Should().Contain("Stellaris");
        first.Delivered[0].QuestionExcerpt.Should().Be("En iyi üç strateji oyunu hangileri?");

        var second = await kit.AskAsync(SorKit.User(A), "Peki ikincisi?");
        second.Answer.Should().Contain("Age of Empires II");
        second.Delivered[0].ConversationId.Should().Be(first.Delivered[0].ConversationId, "the same owner, channel and visibility within the idle window");

        var messages = kit.Ai.Requests[1].Messages;
        messages[0].Role.Should().Be(AiRole.System);
        messages.Select(m => m.Role).Should().Equal(AiRole.System, AiRole.User, AiRole.Assistant, AiRole.User);
        messages[1].Content.Should().Contain("En iyi üç strateji oyunu hangileri?");
        messages[2].Content.Should().Contain("Stellaris", "the assistant's earlier answer is an assistant message, never the member's statement");
        messages[3].Content.Should().Contain("<soru>\nPeki ikincisi?\n</soru>");
        kit.Ai.Requests.Should().HaveCount(2, "one generation per question");
        (await TurnsAsync(kit, A)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_new_conversation_drops_the_topic_but_keeps_the_allowed_preferences()
    {
        await using var kit = await SorKit.CreateAsync();
        await SeedNoteAsync(kit, A, "Kısa cevapları seviyor.", SorMemoryScope.Channel, SorKit.Channel, SorMemoryKind.AnswerStyle);
        kit.Ai.Answer("Roma 753'te kuruldu.").Answer("Fotosentez ışıkla besin üretimidir.");

        var first = await kit.AskAsync(SorKit.User(A), "Roma ne zaman kuruldu?");
        var second = await kit.AskAsync(SorKit.User(A), "Fotosentez nedir?", isNew: true);

        second.Delivered[0].ConversationId.Should().NotBe(first.Delivered[0].ConversationId);
        var seen = kit.Ai.LastSeen;
        seen.Should().NotContain("Roma", "the previous topic is not context any more");
        seen.Should().Contain("Kısa cevapları seviyor.", "lasting preferences survive a new conversation");
        (await NotesAsync(kit, A)).Should().ContainSingle();
    }

    [Fact]
    public async Task Memory_survives_a_restart_while_history_obeys_the_idle_window_the_turn_limit_and_its_retention()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("Tamam, strateji oyunlarını not ettim mi bilmem ama öneririm: Stellaris.",
            Memory(Add("oyun", "Strateji oyunlarını seviyor.", "Strateji oyunlarını seviyorum")));
        await kit.AskAsync(SorKit.User(A), "Strateji oyunlarını seviyorum, ne önerirsin?");
        (await NotesAsync(kit, A)).Should().ContainSingle().Which.Text.Should().Be("Strateji oyunlarını seviyor.");

        // A second process on the same database: in-memory state is new, the stored memory is the same.
        await using (var restarted = await SorKit.CreateAsync(directory: kit.Host.Directory))
        {
            await restarted.AskAsync(SorKit.User(A), "Bu akşam ne oynasam?");
            restarted.Ai.LastSeen.Should().Contain("Strateji oyunlarını seviyor.");
            restarted.Ai.LastSeen.Should().Contain("Stellaris", "within 60 minutes the conversation continues");
        }

        // Ten more questions: only the last 8 completed pairs are kept and sent.
        for (var i = 0; i < 10; i++)
            await kit.AskAsync(kit.Ask(SorKit.User(A), "Soru " + i, channel: SorKit.OtherChannel));
        var turns = (await TurnsAsync(kit, A)).Where(t => t.Question.StartsWith("Soru ", StringComparison.Ordinal)).ToList();
        turns.Should().HaveCount(8).And.OnlyContain(t => t.Seq > 2);
        kit.Ai.Requests[^1].Messages.Count(m => m.Role == AiRole.Assistant).Should().Be(8);

        // 61 minutes idle: a new conversation; 25 hours: the cleanup deletes conversations and turns, never the notes.
        kit.Host.Clock.Advance(TimeSpan.FromMinutes(61));
        await kit.AskAsync(kit.Ask(SorKit.User(A), "Yeni konu", channel: SorKit.OtherChannel));
        kit.Ai.LastSeen.Should().NotContain("Soru 9");
        kit.Host.Clock.Advance(TimeSpan.FromHours(25));
        var report = await kit.InScopeAsync(sp => sp.GetRequiredService<SorCleanup>().RunOnceAsync(CancellationToken.None));
        report.Conversations.Should().BeGreaterThan(0);
        (await TurnsAsync(kit, A)).Should().BeEmpty();
        (await ConversationsAsync(kit, A)).Should().BeEmpty();
        (await NotesAsync(kit, A)).Should().ContainSingle("lasting notes are not part of the 24-hour history");
    }

    [Fact]
    public async Task A_clear_new_preference_replaces_the_old_one_and_the_saved_line_comes_only_from_a_real_write()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("Olur, kısa tutarım.", Memory(Add("cevap_tarzi", "Kısa cevap istiyor.", "bana kısa cevap ver")));
        var first = await kit.AskAsync(SorKit.User(A), "Bundan sonra bana kısa cevap ver lütfen.");
        first.Footers.Should().Equal("🧠 1 not kaydedildi");

        kit.Ai.Answer("Anlaşıldı.", Memory(("update", "cevap_tarzi", "Ayrıntılı cevap istiyor.", "Artık ayrıntılı cevap istiyorum", null)));
        var second = await kit.AskAsync(SorKit.User(A), "Artık ayrıntılı cevap istiyorum.");
        second.Footers.Should().Equal("🧠 1 not güncellendi");
        var notes = await NotesAsync(kit, A);
        notes.Should().ContainSingle().Which.Text.Should().Be("Ayrıntılı cevap istiyor.");
        notes[0].Version.Should().Be(2);

        // A proposal whose evidence is not in the member's message saves nothing — and nothing claims it did.
        kit.Ai.Answer("Tamam.", Memory(Add("oyun", "Satranç seviyor.", "satranç seviyorum")));
        var third = await kit.AskAsync(SorKit.User(A), "Hava nasıl olacak sence?");
        third.Footers.Should().BeEmpty();
        (await NotesAsync(kit, A)).Should().ContainSingle();
    }

    // ------------------------------------------------------------------ identity and access

    [Fact]
    public async Task Two_members_with_the_same_name_never_share_memory_and_a_rename_keeps_it()
    {
        await using var kit = await SorKit.CreateAsync();
        await SeedNoteAsync(kit, A, "Satranç oynuyor.", SorMemoryScope.Private);
        await SeedNoteAsync(kit, C, "Basketbol oynuyor.", SorMemoryScope.Private);

        await kit.AskAsync(kit.Ask(SorKit.User(C), "Ne oynasam?", isPrivate: true, name: "Ada"));
        kit.Ai.LastSeen.Should().Contain("Basketbol").And.NotContain("Satranç");

        await kit.AskAsync(kit.Ask(SorKit.User(A), "Ne oynasam?", isPrivate: true, name: "Ada Yeni İsim"));
        kit.Ai.LastSeen.Should().Contain("Satranç").And.NotContain("Basketbol");
    }

    [Fact]
    public async Task The_same_account_in_another_guild_has_separate_memory_and_history()
    {
        await using var kit = await SorKit.CreateAsync();
        await SeedNoteAsync(kit, A, "Sunucu-1 notu: Satranç oynuyor.", SorMemoryScope.Private);
        kit.Ai.Answer("Bir.").Answer("İki.");
        await kit.AskAsync(kit.Ask(SorKit.User(A), "Gizli tarih sorusu", isPrivate: true));
        await kit.AskAsync(kit.Ask(SorKit.User(A, SorKit.OtherGuild), "Ne oynasam?", isPrivate: true));
        kit.Ai.LastSeen.Should().NotContain("Satranç").And.NotContain("Gizli tarih sorusu");
    }

    [Fact]
    public async Task In_the_same_channel_a_follow_up_never_uses_another_members_conversation()
    {
        await using var kit = await SorKit.CreateAsync();
        kit.Ai.Answer("Mars kırmızıdır.").Answer("Jüpiter en büyüktür.").Answer("Devam.");
        var a = await kit.AskAsync(SorKit.User(A), "Mars neden kırmızı?");
        await kit.AskAsync(SorKit.User(B), "En büyük gezegen?", name: "Mert");

        // A's follow-up, even pointing at B's conversation id (a forged button), never sees B's history.
        var bConversation = (await ConversationsAsync(kit, B)).Single().Id;
        await kit.AskAsync(kit.Ask(SorKit.User(A), "Biraz aç", conversation: bConversation));
        kit.Ai.LastSeen.Should().NotContain("Jüpiter").And.NotContain("En büyük gezegen");
        (await TurnsAsync(kit, B)).Should().ContainSingle();
        a.Delivered[0].OwnerUserId.Should().Be(A);
    }

    [Fact]
    public async Task Private_notes_stay_out_of_channel_answers_and_channel_notes_out_of_other_channels()
    {
        await using var kit = await SorKit.CreateAsync();
        await SeedNoteAsync(kit, A, "Gizli: piyano çalıyor.", SorMemoryScope.Private, kind: SorMemoryKind.Hobby);
        await SeedNoteAsync(kit, A, "Kanal-1: satranç oynuyor.", SorMemoryScope.Channel, SorKit.Channel);

        await kit.AskAsync(kit.Ask(SorKit.User(A), "Hobim ne olabilir?", channel: SorKit.Channel));
        kit.Ai.LastSeen.Should().Contain("Kanal-1").And.NotContain("piyano");

        await kit.AskAsync(kit.Ask(SorKit.User(A), "Hobim ne olabilir?", channel: SorKit.OtherChannel));
        kit.Ai.LastSeen.Should().NotContain("Kanal-1").And.NotContain("piyano");
    }

    [Fact]
    public async Task A_private_answer_uses_the_members_own_private_channel_and_shared_notes()
    {
        await using var kit = await SorKit.CreateAsync();
        await SeedNoteAsync(kit, A, "Gizli: piyano çalıyor.", SorMemoryScope.Private, kind: SorMemoryKind.Hobby);
        await SeedNoteAsync(kit, A, "Kanal-1: satranç oynuyor.", SorMemoryScope.Channel, SorKit.Channel);
        await SeedNoteAsync(kit, A, "Paylaşılan: C# öğreniyor.", SorMemoryScope.Shared, kind: SorMemoryKind.Goal);

        await kit.AskAsync(kit.Ask(SorKit.User(A), "Bana ne önerirsin?", isPrivate: true, channel: SorKit.OtherChannel));
        kit.Ai.LastSeen.Should().Contain("piyano").And.Contain("Kanal-1").And.Contain("C# öğreniyor");
    }

    [Fact]
    public async Task What_a_says_about_b_never_changes_bs_memory_and_is_not_saved_as_as_note()
    {
        await using var kit = await SorKit.CreateAsync();
        var mert = new SorPerson(B, "Mert", false);
        kit.Ai.Answer("Belki öyledir.", Memory(Add("oyun", "Mert strateji oyunlarını seviyor.", "Mert strateji oyunlarını seviyor")));
        await kit.AskAsync(kit.Ask(SorKit.User(A), "Mert strateji oyunlarını seviyor, ona ne hediye alayım?", about: mert));

        (await NotesAsync(kit, B)).Should().BeEmpty("only the asker's own statements are ever saved, and only to the asker");
        (await NotesAsync(kit, A)).Should().BeEmpty("a note naming the member the question was about is refused");
    }

    [Fact]
    public async Task About_another_member_only_their_explicitly_shared_notes_are_used()
    {
        await using var kit = await SorKit.CreateAsync();
        await SeedNoteAsync(kit, B, "Paylaşılan: Valorant oynuyor.", SorMemoryScope.Shared);
        await SeedNoteAsync(kit, B, "Gizli: sınav haftasında.", SorMemoryScope.Private, kind: SorMemoryKind.Goal);
        await SeedNoteAsync(kit, B, "Kanal: Minecraft seviyor.", SorMemoryScope.Channel, SorKit.Channel);

        await kit.AskAsync(kit.Ask(SorKit.User(A), "Mert ne oynamayı sever?", about: new SorPerson(B, "Mert", false)));
        var seen = kit.Ai.LastSeen;
        seen.Should().Contain("Valorant").And.NotContain("sınav").And.NotContain("Minecraft");

        // Nobody shared anything: the prompt says so instead of leaving room for an invented profile.
        await kit.AskAsync(kit.Ask(SorKit.User(A), "Peki Can?", about: new SorPerson(C, "Can", false)));
        kit.Ai.LastSeen.Should().Contain("paylaştığı bir bilgi yok");
    }

    [Fact]
    public async Task Another_members_note_id_or_stale_version_cannot_be_used_to_read_change_or_delete_it()
    {
        await using var kit = await SorKit.CreateAsync();
        var id = await SeedNoteAsync(kit, B, "Gizli: piyano çalıyor.", SorMemoryScope.Private);
        var stamp = (await NotesAsync(kit, B)).Single().Stamp;

        var actor = SorKit.User(A);
        (await kit.InScopeAsync(sp => sp.GetRequiredService<SorMemoryService>().GetAsync(actor, id, stamp, CancellationToken.None))).Should().BeNull();
        (await kit.MemoryAsync(m => m.DeleteAsync(actor, id, stamp, CancellationToken.None))).Succeeded.Should().BeFalse();
        (await kit.MemoryAsync(m => m.EditAsync(actor, id, stamp, "değişti", CancellationToken.None))).Succeeded.Should().BeFalse();
        (await kit.MemoryAsync(m => m.ShareAsync(actor, id, stamp, CancellationToken.None))).Succeeded.Should().BeFalse();
        (await kit.MemoryAsync(m => m.DeleteAsync(SorKit.User(B), id, "0000000000000000", CancellationToken.None))).Succeeded.Should().BeFalse("an old version");
        (await NotesAsync(kit, B)).Should().ContainSingle().Which.Text.Should().Be("Gizli: piyano çalıyor.");
        (await kit.PanelAsync(actor)).Notes.Should().BeEmpty("the panel only ever lists the caller's own notes");
    }

    [Fact]
    public async Task Channel_and_role_restrictions_are_enforced_before_any_request_and_a_thread_follows_its_parent()
    {
        await using var kit = await SorKit.CreateAsync();
        var admin = TestHost.Admin(SorKit.Guild);
        await kit.InScopeAsync(sp => sp.GetRequiredService<SorConfigService>().AllowChannelAsync(admin, SorKit.Channel, true, CancellationToken.None));

        var refused = await kit.AskAsync(kit.Ask(SorKit.User(A), "Merhaba?", channel: SorKit.OtherChannel));
        refused.Refusals.Should().ContainSingle().Which.Should().Contain("kanalda kullanılamıyor");
        refused.Deferred.Should().BeEmpty("refused privately before any acknowledgement");
        kit.Ai.Requests.Should().BeEmpty();

        await kit.AskAsync(kit.Ask(SorKit.User(A), "Merhaba?", channel: 9999, parent: SorKit.Channel));
        kit.Ai.Requests.Should().ContainSingle("a thread of an allowed channel is allowed");

        (await kit.InScopeAsync(sp => sp.GetRequiredService<SorConfigService>().AllowChannelAsync(SorKit.User(A), SorKit.OtherChannel, true, CancellationToken.None)))
            .Succeeded.Should().BeFalse("only Manage Server may change access");
        (await kit.InScopeAsync(sp => sp.GetRequiredService<SorConfigService>().GetAsync(SorKit.User(A), CancellationToken.None))).Should().BeNull();
        (await kit.InScopeAsync(sp => sp.GetRequiredService<SorConfigService>().UsageAsync(SorKit.User(A), 7, CancellationToken.None))).Should().BeEmpty();

        // Roles: any one listed role is enough; without one the refusal is private and immediate.
        var role = new ToroSquad.Core.RoleId(5555);
        await kit.InScopeAsync(sp => sp.GetRequiredService<SorConfigService>().AllowRoleAsync(admin, role.Value, true, CancellationToken.None));
        var noRole = await kit.AskAsync(kit.Ask(SorKit.User(B), "Merhaba?", channel: SorKit.Channel));
        noRole.Refusals.Should().ContainSingle().Which.Should().Contain("rollerden");
        await kit.AskAsync(kit.Ask(SorKit.User(B, null, role), "Merhaba?", channel: SorKit.Channel));
        kit.Ai.Requests.Should().HaveCount(2);
    }
}
