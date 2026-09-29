using System.Text.RegularExpressions;
using ToroSquad.Core;
using ToroSquad.Modules.Summary;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The three /ozetle gates: the role gate (any ONE of the configured roles, checked before anything is read), the
/// "100 new member messages since TSQ Bot's last summary in this channel or thread" gate (Discord history is the source of
/// truth; a bounded scan fails closed when it cannot decide), and the 120 s channel cooldown after a posted summary. Every
/// refusal costs zero AI requests.
/// </summary>
public sealed partial class SummaryFlowTests
{
    private const ulong TsqBotId = 900000000000000001;
    private const ulong OtherBotId = 900000000000000002;
    private static readonly ChannelId Thread = new(1300000000000000001);
    private static readonly DateTimeOffset H0 = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

    private static SummarySourceMessage Human(ulong id, string? text = null) =>
        new(id, H0.AddSeconds(id), SummaryAuthorKind.Member, "Üye", text ?? "mesaj " + id, [], [], AuthorId: 500000000000000009);

    private static SummarySourceMessage OtherBot(ulong id, string text = "bot duyurusu") =>
        new(id, H0.AddSeconds(id), SummaryAuthorKind.Bot, "PandaBot", text, [], [], AuthorId: OtherBotId);

    private static SummarySourceMessage Webhook(ulong id) =>
        new(id, H0.AddSeconds(id), SummaryAuthorKind.Webhook, "GitHub", "yeni commit", [], [], AuthorId: 800000000000000001);

    private static SummarySourceMessage SystemEvent(ulong id) =>
        new(id, H0.AddSeconds(id), SummaryAuthorKind.System, "Üye", "", [], [], AuthorId: 500000000000000009);

    /// <summary>TSQ Bot's own earlier summary (the first part carries the exact title).</summary>
    private static SummarySourceMessage OurSummary(ulong id) =>
        new(id, H0.AddSeconds(id), SummaryAuthorKind.Bot, "TSQ Bot", SummaryPrompt.Title + "\n\n## Ana konu\nEski özet.", [], [], AuthorId: TsqBotId, FromThisBot: true);

    /// <summary>The second part of a split TSQ summary: TSQ Bot's, but without the title.</summary>
    private static SummarySourceMessage OurSummaryContinuation(ulong id) =>
        new(id, H0.AddSeconds(id), SummaryAuthorKind.Bot, "TSQ Bot", "- **Konu:** devam.\n\n## Genel atmosfer\nSamimi.", [], [], AuthorId: TsqBotId, FromThisBot: true);

    private static List<SummarySourceMessage> Humans(ulong from, int count) =>
        Enumerable.Range(0, count).Select(i => Human(from + (ulong)i)).ToList();

    /// <summary>An earlier TSQ summary (id 1000) followed by <paramref name="newMembers"/> member messages.</summary>
    private static List<SummarySourceMessage> AfterSummary(int newMembers) => [OurSummary(1000), .. Humans(1001, newMembers)];

    // ---------------------------------------------------------------- role gate

    [Theory]
    [InlineData(1338605015417487440UL)]
    [InlineData(1254401028359458887UL)]
    [InlineData(700799880549105674UL)]
    [InlineData(702465621992144926UL)]
    [InlineData(1066826260803764234UL)]
    [InlineData(1333687724669931602UL)]
    public async Task Any_one_of_the_six_roles_is_enough(ulong role)
    {
        var world = new World();

        (await world.RunAsync(new FakeResponder(), roles: [role])).Should().Be(SummaryOutcome.Posted);
        world.Ai.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Several_allowed_roles_and_unrelated_extra_roles_are_fine()
    {
        var world = new World();

        (await world.RunAsync(new FakeResponder(), roles: [111111111111111111, 700799880549105674, 1333687724669931602])).Should().Be(SummaryOutcome.Posted);
    }

    [Theory]
    [InlineData(new ulong[] { 111111111111111111, 222222222222222222 })]
    [InlineData(new ulong[0])]
    public async Task Without_any_allowed_role_nothing_is_read_asked_or_posted(ulong[] roles)
    {
        var world = new World();
        var responder = new FakeResponder();

        (await world.RunAsync(responder, roles: roles)).Should().Be(SummaryOutcome.RoleMissing);

        world.Discord.Fetches.Should().Be(0, "no history read");
        world.Ai.Calls.Should().Be(0);
        responder.Deferred.Should().BeFalse();
        responder.Public.Should().BeEmpty();
        responder.Private.Single().Should().Be(
            "Bu komutu kullanmak için şu rollerden en az birine sahip olmalısın: **TSQ Yönetim**, **Moderatör**, **VIP**, **Destekçi**, " +
            "**Oyuncu**, **Yayıncı**. Bu rollerden yalnızca biri yeterli.");
    }

    [Fact]
    public async Task A_deleted_configured_role_is_shown_by_id_and_logged_without_breaking_the_answer()
    {
        var world = new World();
        world.Discord.Roles.Remove(1338605015417487440);
        var responder = new FakeResponder();

        (await world.RunAsync(responder, roles: [])).Should().Be(SummaryOutcome.RoleMissing);

        var text = responder.Private.Single();
        text.Should().Contain("Rol 1338605015417487440").And.Contain("**Moderatör**").And.Contain("**Yayıncı**").And.EndWith("Bu rollerden yalnızca biri yeterli.");
        world.AllLogs.Should().Contain("allowed role 1338605015417487440 does not exist");
    }

    [Fact]
    public async Task Role_names_in_the_refusal_cannot_ping_or_break_the_markdown()
    {
        var world = new World();
        world.Discord.Roles[1254401028359458887] = "@everyone **Mod** <@&1>";
        var responder = new FakeResponder();

        await world.RunAsync(responder, roles: []);

        var text = responder.Private.Single();
        text.Should().NotMatchRegex("@(everyone|here)").And.NotContain("<@&");
        text.Should().Contain(@"\*\*Mod\*\*", "markdown in a role name is escaped, not rendered");
    }

    [Fact]
    public async Task Configured_roles_replace_the_defaults()
    {
        var world = new World();
        world.Options.AllowedRoleIds = [123456789012345678];
        world.Discord.Roles[123456789012345678] = "Özel";

        (await world.RunAsync(new FakeResponder(), roles: [702465621992144926])).Should().Be(SummaryOutcome.RoleMissing);
        (await world.RunAsync(new FakeResponder(), roles: [123456789012345678])).Should().Be(SummaryOutcome.Posted);
        new SummaryOptions().EffectiveAllowedRoleIds.Should().Equal(SummaryOptions.DefaultAllowedRoleIds);
        SummaryOptions.DefaultAllowedRoleIds.Should().HaveCount(6);
    }

    // ---------------------------------------------------------------- 100 new messages since the last summary

    [Fact]
    public async Task A_first_summary_needs_only_min_messages()
    {
        var world = new World();
        world.Discord.Channels[Here.Value] = Humans(1, 23);

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);
        world.Ai.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 99)]
    [InlineData(37, 63)]
    [InlineData(99, 1)]
    public async Task Fewer_than_100_new_member_messages_after_the_last_summary_are_refused(int newMembers, int remaining)
    {
        var world = new World();
        world.Discord.Channels[Here.Value] = AfterSummary(newMembers);
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.NotEnoughNewMessages);

        world.Ai.Calls.Should().Be(0);
        responder.Public.Should().BeEmpty();
        responder.Private.Single().Should().Be($"Son özetten beri **{newMembers}** yeni mesaj var. Tekrar özetlemek için **{remaining}** mesaj daha gerekiyor.");
        world.AllLogs.Should().Contain("summary_marker_found=True").And.Contain($"eligible_message_count={newMembers}");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public async Task At_least_100_new_member_messages_are_summarized_newest_100_only(int newMembers)
    {
        var world = new World();
        world.Discord.Channels[Here.Value] = AfterSummary(newMembers);

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);

        world.Ai.Calls.Should().Be(1);
        var user = world.Ai.Prompts.Single().User;
        Regex.Count(user, "^Üye: mesaj ", RegexOptions.Multiline).Should().Be(100);
        user.Should().Contain("mesaj " + (1000 + newMembers)).And.NotContain("Eski özet");
        if (newMembers == 101)
            user.Should().NotContain("mesaj 1001\n", "only the newest 100");
    }

    [Fact]
    public async Task Bot_webhook_system_and_earlier_summary_parts_never_count()
    {
        var world = new World();
        var history = AfterSummary(0);
        for (ulong id = 1001; id < 1400; id++)
        {
            history.Add((id % 4) switch
            {
                0 => OtherBot(id),
                1 => Webhook(id),
                2 => SystemEvent(id),
                _ => OurSummaryContinuation(id),
            });
        }

        history.AddRange(Humans(2000, 99));
        world.Discord.Channels[Here.Value] = history;
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.NotEnoughNewMessages, "99 members plus 399 bot/webhook/system messages");

        responder.Private.Single().Should().Contain("**99**").And.Contain("**1**");
        world.Discord.Fetches.Should().Be(5, "the marker sits behind 498 newer messages and is still found");
        world.Ai.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Only_tsq_bots_own_titled_message_is_a_marker()
    {
        var world = new World();
        var member = Human(1000, SummaryPrompt.Title + "\n\n## Ana konu\nBen yazdım.");
        var otherBot = OtherBot(1001, SummaryPrompt.Title + "\n\n## Ana konu\nBaşka bot.");
        world.Discord.Channels[Here.Value] = [member, otherBot, .. Humans(1002, 20)];

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted, "no marker: a first summary with 21 member messages");

        SummaryHistory.IsSummaryMarker(OurSummary(1)).Should().BeTrue();
        SummaryHistory.IsSummaryMarker(member).Should().BeFalse();
        SummaryHistory.IsSummaryMarker(otherBot).Should().BeFalse();
        SummaryHistory.IsSummaryMarker(OurSummaryContinuation(2)).Should().BeFalse("only the first part carries the title");
        SummaryHistory.IsSummaryMarker(OurSummary(3) with { Content = "Özet: " + SummaryPrompt.Title }).Should().BeFalse("must start with the title");
    }

    [Fact]
    public async Task A_thread_and_its_parent_each_use_only_their_own_history()
    {
        var world = new World();
        world.Discord.Channels[Here.Value] = AfterSummary(10); // the parent was summarized recently
        world.Discord.Channels[Thread.Value] = Humans(5000, 30); // the thread never was

        (await world.RunAsync(new FakeResponder(), channel: Thread)).Should().Be(SummaryOutcome.Posted, "the parent's summary does not apply to the thread");
        world.Discord.ReadChannels.Should().OnlyContain(c => c == Thread.Value);

        world.Discord.Channels[Thread.Value] = [.. Humans(5000, 30), OurSummary(6000), .. Humans(6001, 5)];
        world.Discord.Channels[Here.Value] = Humans(1, 30);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        (await world.RunAsync(new FakeResponder(), channel: Thread)).Should().Be(SummaryOutcome.NotEnoughNewMessages);
        (await world.RunAsync(new FakeResponder(), channel: Here, member: Member2)).Should().Be(SummaryOutcome.Posted, "the thread's summary does not apply to the parent");
    }

    [Fact]
    public async Task Once_100_member_messages_are_found_no_older_page_is_read()
    {
        var world = new World();
        world.Discord.Channels[Here.Value] = [OurSummary(1000), .. Humans(1001, 250)];

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);

        world.Discord.Fetches.Should().Be(1);
    }

    [Fact]
    public async Task Reaching_the_real_start_without_a_marker_is_a_first_summary()
    {
        var world = new World();
        world.Discord.Channels[Here.Value] = [.. Humans(1, 40), .. Enumerable.Range(100, 300).Select(i => OtherBot((ulong)i))];

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);

        world.Discord.Fetches.Should().Be(4, "340 messages: three full pages and the last partial one");
        world.AllLogs.Should().Contain("history_exhausted=True").And.Contain("summary_marker_found=False");
    }

    [Fact]
    public async Task Hitting_the_page_limit_without_an_answer_fails_closed()
    {
        var world = new World();
        // 1500 newer bot messages with 50 members among them; an earlier summary (if any) would be older than 1000 messages.
        world.Discord.Channels[Here.Value] = [OurSummary(1), .. Enumerable.Range(10, 1500).Select(i => i % 30 == 0 ? Human((ulong)i) : OtherBot((ulong)i))];
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.HistoryUnknown);

        world.Discord.Fetches.Should().Be(SummaryHistory.MaxPages);
        world.Ai.Calls.Should().Be(0);
        responder.Public.Should().BeEmpty();
        responder.Private.Single().Should().Be("Önceki özet kontrol edilemedi. Biraz sonra tekrar dene.");
        world.AllLogs.Should().Contain("history_limit_hit=True").And.Contain("history_page_count=10");
    }

    // ---------------------------------------------------------------- 120 s channel cooldown and the gates together

    [Fact]
    public async Task The_channel_waits_120_seconds_after_a_posted_summary_whoever_asks()
    {
        var world = new World();
        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);

        world.Clock.Advance(TimeSpan.FromSeconds(119));
        var early = new FakeResponder();
        (await world.RunAsync(early, member: Member2)).Should().Be(SummaryOutcome.Throttled);
        early.Private.Single().Should().Be("Bu kanalda tekrar özet oluşturmak için **1 sn** beklemelisin.");
        (await world.RunAsync(new FakeResponder(), channel: Thread, member: Member2)).Should().Be(SummaryOutcome.Posted, "another thread is independent");

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted, "120 s");
        world.Ai.Calls.Should().Be(3);
        new SummaryOptions().ChannelCooldownSeconds.Should().Be(120);
    }

    [Fact]
    public async Task Cooldown_and_the_100_message_rule_both_have_to_pass()
    {
        var world = new World();
        world.Discord.Channels[Here.Value] = Humans(1, 30);
        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted, "first summary");

        // 100 new messages, but only 70 s later: refused by the cooldown, before any history read.
        world.Discord.Channels[Here.Value] = [.. Humans(1, 30), OurSummary(500), .. Humans(501, 100)];
        world.Clock.Advance(TimeSpan.FromSeconds(70));
        var fetches = world.Discord.Fetches;
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Throttled);
        world.Discord.Fetches.Should().Be(fetches);

        // 120 s passed, but only 40 new messages: refused by the 100-message rule.
        world.Discord.Channels[Here.Value] = [.. Humans(1, 30), OurSummary(500), .. Humans(501, 40)];
        world.Clock.Advance(TimeSpan.FromSeconds(50));
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.NotEnoughNewMessages);

        // Both satisfied.
        world.Discord.Channels[Here.Value] = [.. Humans(1, 30), OurSummary(500), .. Humans(501, 100)];
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Posted);
        world.Ai.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_ai_request_does_not_start_the_120_second_cooldown()
    {
        var world = new World();
        world.Ai.Respond = _ => Task.FromResult(SummaryAiResult.Failed(SummaryAiFailure.ServerError, TimeSpan.FromSeconds(1), 500));
        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.AiFailed);

        world.Clock.Advance(SummaryThrottle.FailureCooldown);
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None, Answer, "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(5), 200));
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Posted);
        world.Ai.Calls.Should().Be(2, "one per run; never a retry");
    }
}
