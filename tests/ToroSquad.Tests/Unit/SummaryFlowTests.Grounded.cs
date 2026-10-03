using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Core;
using ToroSquad.Modules.Summary;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The generation mode end to end: Legacy (default) keeps its request and output; Grounded sends records with reply links
/// and publishes only an answer whose sources and quotes check out. Either way one run is at most one inference — a refused
/// grounded answer is never retried and never replaced by a legacy request — and the gates, cooldowns and logs behave as
/// before.
/// </summary>
public sealed partial class SummaryFlowTests
{
    private const string GroundedSecret = "ÇOK-GİZLİ-CÜMLE-4f2a";

    /// <summary>m001–m006 filler, m007 Monfy, m008 Toro (reply to m007), m009 Monfy (reply to m008).</summary>
    private static List<SummarySourceMessage> ConfigTalk() =>
    [
        .. Humans(1, 6),
        Human(41, "Eski config'i koydum ama çalışmadı. " + GroundedSecret) with { AuthorId = 1, AuthorName = "Monfy" },
        Human(42, "Oyunu yeniden başlattın mı?") with { AuthorId = 2, AuthorName = "Toro", ReplyToId = 41 },
        Human(43, "Evet, şimdi oldu.") with { AuthorId = 1, AuthorName = "Monfy", ReplyToId = 42 },
    ];

    /// <summary>
    /// A valid answer in contract v4: open points with their own "t" and "e" pairs of [reference, quote]; the "spoilers" list
    /// is always there — empty, or one item quoting the given hidden part.
    /// </summary>
    private static string GroundedJson(string pointRef = "m009", string pointQuote = "şimdi oldu", int extraPoints = 0, string? extraRef = null, string? extraQuote = null, string? spoilerRef = null, string? spoilerQuote = null, string? pointText = null)
    {
        var first = new
        {
            topic = "Oyun ayarları",
            t = pointText ?? "Monfy config'in önce çalışmadığını, yeniden başlatınca düzeldiğini söyledi.",
            e = new[] { new[] { "m007", "koydum ama çalışmadı" }, new[] { pointRef, pointQuote } },
        };
        var extra = Enumerable.Range(1, extraPoints).Select(i => new
        {
            topic = "Ek konu " + i,
            t = "Ek bilgi " + i + ".",
            e = new[] { new[] { i == extraPoints && extraRef is not null ? extraRef : "m008", i == extraPoints && extraQuote is not null ? extraQuote : "yeniden başlattın mı?" } },
        });
        object[] spoilers = spoilerRef is null
            ? []
            : [new { topic = "Dizi finali", t = "Kahramanın son sahnede hayatını kaybettiği konuşuldu.", e = new[] { new[] { spoilerRef, spoilerQuote! } } }];
        return JsonSerializer.Serialize(new
        {
            v = 4,
            main = new { t = "Oyun ayarı sorunu konuşuldu.", e = new[] { new[] { "m007", "koydum ama çalışmadı" } } },
            points = new[] { first }.Concat(extra),
            spoilers,
            atmosphere = new { t = "Yardımlaşmalı bir sohbet.", e = new[] { new[] { "m008", "yeniden başlattın mı?" } } },
        });
    }

    /// <summary>A grounded world whose fake provider gives the same answer to the generator and to the reviewer.</summary>
    private static World GroundedWorld(string? answer = null, string finish = "stop")
    {
        var world = new World();
        world.Options.GenerationMode = SummaryGenerationMode.Grounded;
        world.Discord.Channels[Here.Value] = ConfigTalk();
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None, answer ?? GroundedJson(), finish,
            new SummaryAiUsage(3200, 900, 0), TimeSpan.FromSeconds(8), 200));
        return world;
    }

    [Fact]
    public async Task Grounded_sends_records_with_reply_links_and_publishes_the_rendered_markdown_after_a_draft_and_its_review()
    {
        var world = GroundedWorld();
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        world.Ai.Calls.Should().Be(2, "the draft and its review");
        var prompt = world.Ai.Prompts.First();
        prompt.System.Should().Be(SummaryGroundedPrompt.System);
        prompt.MaxOutputTokens.Should().Be(2000);
        prompt.User.Should().Contain("""{"m":"m008","u":"Toro","re":"m007","t":"Oyunu yeniden başlattın mı?"}""");
        prompt.User.Should().Contain("""{"m":"m009","u":"Monfy","re":"m008","t":"Evet, şimdi oldu."}""");

        var posted = string.Join("\n", responder.Public.Single());
        posted.Should().StartWith("# Son Mesajların Özeti\n\n## Ana konu\nOyun ayarı sorunu konuşuldu.");
        posted.Should().Contain("- **Oyun ayarları:** Monfy config'in önce çalışmadığını, yeniden başlatınca düzeldiğini söyledi.");
        posted.Should().NotContain("{").And.NotContain("[").And.NotContain("m00").And.NotContain("\"");
        responder.Private.Should().BeEmpty();
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Throttled, "a posted summary starts the channel cooldown as before");
    }

    [Fact]
    public async Task Legacy_is_the_default_and_keeps_its_transcript_request_and_output()
    {
        var world = new World();
        world.Discord.Channels[Here.Value] = ConfigTalk();
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        var prompt = world.Ai.Prompts.Single();
        prompt.System.Should().Be(SummaryPrompt.System);
        prompt.MaxOutputTokens.Should().BeNull();
        prompt.User.Should().Contain("\nToro: Oyunu yeniden başlattın mı?\nMonfy: Evet, şimdi oldu.\n</transcript>");
        prompt.User.Should().NotContain("\"m\":").And.NotContain("<records>");
        responder.Public.Single().Should().Equal(Answer);
        world.AllLogs.Should().Contain("generation_mode=Legacy");
    }

    [Theory]
    [InlineData("m009", "Hayır, hâlâ olmadı.", "QuoteNotFound")] // an invented quote
    [InlineData("m008", "şimdi oldu", "QuoteNotFound")] // a real quote tied to the wrong source
    [InlineData("m042", "şimdi oldu", "UnknownSource")]
    public async Task A_refused_grounded_answer_is_not_published_retried_or_replaced_by_legacy(string reference, string quote, string category)
    {
        var world = GroundedWorld(GroundedJson(reference, quote));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1, "no retry and no legacy request in the same run");
        world.Ai.Prompts.Should().OnlyContain(p => p.System == SummaryGroundedPrompt.System);
        responder.Public.Should().BeEmpty("no summary marker is created");
        var answer = responder.Private.Single();
        answer.Should().StartWith("Özet oluşturulamadı.").And.Contain("TS-").And.NotContain("{").And.NotContain("m00");
        world.AllLogs.Should().Contain("validation=" + category).And.Contain("failure=InvalidResponse").And.Contain("generation_mode=Grounded");

        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Throttled, "the short failure cooldown");
        world.Clock.Advance(SummaryThrottle.FailureCooldown);
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None, GroundedJson(), "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(5), 200));
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Posted, "no successful-summary cooldown was started");
        world.Ai.Calls.Should().Be(3, "the refused draft, then a draft and its review");
    }

    [Theory]
    [InlineData("stop", "{\"v\":4,\"main\":{\"t\":\"ÇOK-GİZLİ-CÜMLE-4f2a")] // broken JSON
    [InlineData("length", null)] // complete JSON, but the model reported it was cut off
    [InlineData("stop", "Özet: ÇOK-GİZLİ-CÜMLE-4f2a")]
    public async Task Broken_or_cut_off_grounded_answers_never_reach_the_channel_or_the_logs(string finish, string? raw)
    {
        var world = GroundedWorld(raw ?? GroundedJson(), finish);
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1);
        responder.Public.Should().BeEmpty();
        responder.Private.Single().Should().NotContain(GroundedSecret).And.NotContain("{");
        world.AllLogs.Should().Contain(finish == "length" ? "validation=Truncated" : "validation=NotJson");
    }

    [Fact]
    public async Task Grounded_logs_carry_counts_only()
    {
        var world = GroundedWorld();
        await world.RunAsync(new FakeResponder());

        var logs = world.AllLogs;
        logs.Should().Contain("validation=None").And.Contain("source_count=9").And.Contain("reply_count=2").And.Contain("context_count=0")
            .And.Contain("evidence_count=4").And.Contain("message_count=9").And.Contain("input_tokens=3200")
            .And.Contain("candidate_point_count=1").And.Contain("candidate_plan_count=0").And.Contain("shown_point_count=1").And.Contain("shown_plan_count=0")
            .And.Contain("stage=generator").And.Contain("stage=reviewer").And.Contain("inference_count=2").And.Contain("total_ai_latency_ms=16000");
        logs.Should().NotContain(GroundedSecret).And.NotContain("config").And.NotContain("Monfy").And.NotContain("şimdi oldu")
            .And.NotContain("Oyun ayarı").And.NotContain("\"quote\"");
    }

    [Fact]
    public async Task More_valid_points_than_are_shown_are_selected_and_posted_as_a_normal_summary()
    {
        var world = GroundedWorld(GroundedJson(extraPoints: 7));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted, "eight valid points are not a failure");

        var posted = string.Join("\n", responder.Public.Single());
        Regex.Count(posted, "(?m)^- ").Should().Be(6);
        posted.Should().Contain("- **Oyun ayarları:**").And.Contain("- **Ek konu 5:** Ek bilgi 5.").And.NotContain("Ek konu 6").And.NotContain("Ek konu 7");
        world.Ai.Calls.Should().Be(2);
        world.AllLogs.Should().Contain("validation=None").And.Contain("candidate_point_count=8").And.Contain("shown_point_count=6").And.Contain("evidence_count=11");
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Throttled, "a posted summary starts the channel cooldown as before");
    }

    [Fact]
    public async Task An_answer_that_leaves_a_required_spoiler_source_out_is_refused_after_one_inference()
    {
        var world = GroundedWorld(); // the default answer has an empty spoilers list
        world.Discord.Channels[Here.Value] = [.. ConfigTalk(), Human(44, "Dizi finali ||kahraman son sahnede ölüyor|| çok şaşırdım") with { AuthorId = 3, AuthorName = "Oykeli" }];
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        var user = world.Ai.Prompts.Single().User;
        user.Should().Contain("\"sp\":true,\"t\":\"Dizi finali <spoiler>kahraman son sahnede ölüyor</spoiler> çok şaşırdım\"");
        user.Should().Contain("\n</records>\n\nZorunlu spoiler kaynakları: m010\n\n");
        world.Ai.Calls.Should().Be(1, "no retry and no legacy request");
        responder.Public.Should().BeEmpty();
        responder.Private.Single().Should().StartWith("Özet oluşturulamadı.").And.NotContain("ölüyor");
        world.AllLogs.Should().Contain("validation=MissingRequiredSpoiler").And.Contain("generation_mode=Grounded");
        world.AllLogs.Should().NotContain("ölüyor").And.NotContain("kahraman");
    }

    [Fact]
    public async Task A_spoiler_item_is_posted_under_its_own_heading_as_a_native_spoiler_without_taking_a_point_place()
    {
        var world = GroundedWorld();
        world.Discord.Channels[Here.Value] = [.. ConfigTalk(), Human(44, "Dizi finali ||kahraman son sahnede ölüyor|| çok şaşırdım") with { AuthorId = 3, AuthorName = "Oykeli" }];
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None,
            GroundedJson(extraPoints: 7, spoilerRef: "m010", spoilerQuote: "kahraman son sahnede ölüyor"), "stop", new SummaryAiUsage(3200, 900, 0), TimeSpan.FromSeconds(8), 200));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        var posted = string.Join("\n", responder.Public.Single());
        Regex.Matches(posted, "(?m)^#{1,2} .+$").Select(m => m.Value)
            .Should().Equal("# Son Mesajların Özeti", "## Ana konu", "## Önemli noktalar", "## Spoilerlar", "## Genel atmosfer");
        posted.Should().Contain("- **Ek konu 5:** Ek bilgi 5.\n\n## Spoilerlar\n- **Dizi finali:** ||Kahramanın son sahnede hayatını kaybettiği konuşuldu.||\n\n## Genel atmosfer");
        Regex.Count(posted, "(?m)^- ").Should().Be(7, "six points and the spoiler item: they do not compete for places");
        Regex.Count(posted, @"\|\|").Should().Be(2);
        posted.Should().NotContain("Ek konu 6").And.NotContain("ölüyor").And.NotContain("{").And.NotContain("m010");
        world.Ai.Calls.Should().Be(2);
        world.AllLogs.Should().Contain("validation=None").And.Contain("candidate_point_count=8").And.Contain("shown_point_count=6").And.Contain("spoiler_claim_count=1");
        world.AllLogs.Should().NotContain("ölüyor").And.NotContain("hayatını kaybettiği");
    }

    [Fact]
    public async Task A_hidden_quote_under_an_open_point_refuses_the_run()
    {
        var world = GroundedWorld();
        world.Discord.Channels[Here.Value] = [.. ConfigTalk(), Human(44, "Dizi finali ||kahraman son sahnede ölüyor|| çok şaşırdım") with { AuthorId = 3, AuthorName = "Oykeli" }];
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None,
            GroundedJson(extraPoints: 2, extraRef: "m010", extraQuote: "kahraman son sahnede ölüyor", spoilerRef: "m010", spoilerQuote: "son sahnede ölüyor"), "stop",
            new SummaryAiUsage(3200, 900, 0), TimeSpan.FromSeconds(8), 200));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1);
        responder.Public.Should().BeEmpty();
        world.AllLogs.Should().Contain("validation=SpoilerInOpenText");
    }

    [Fact]
    public async Task A_source_error_in_a_point_that_would_not_be_shown_still_refuses_the_run_without_a_second_inference()
    {
        var world = GroundedWorld(GroundedJson(extraPoints: 7, extraRef: "m008", extraQuote: "yeniden baslattin mi?")); // the 8th point: not verbatim
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1, "no retry and no legacy request");
        responder.Public.Should().BeEmpty("nothing is posted, so no summary marker and no successful-summary cooldown exist");
        world.AllLogs.Should().Contain("validation=QuoteNotFound").And.Contain("shown_point_count=0");
        world.Clock.Advance(SummaryThrottle.FailureCooldown);
        world.Ai.Respond = _ => Task.FromResult(new SummaryAiResult(SummaryAiFailure.None, GroundedJson(), "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(5), 200));
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Posted, "only the short failure cooldown applied");
    }

    [Fact]
    public async Task The_mode_is_taken_once_at_the_start_of_a_run()
    {
        var world = GroundedWorld();
        world.Ai.Respond = _ =>
        {
            world.Options.GenerationMode = SummaryGenerationMode.Legacy; // a configuration change while the request is running
            return Task.FromResult(new SummaryAiResult(SummaryAiFailure.None, GroundedJson(), "stop", SummaryAiUsage.None, TimeSpan.FromSeconds(5), 200));
        };
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        string.Join("\n", responder.Public.Single()).Should().Contain("## Ana konu\nOyun ayarı sorunu konuşuldu.").And.NotContain("{");
        world.Ai.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Grounded_keeps_the_role_gate_the_100_message_rule_and_does_not_count_context()
    {
        var world = GroundedWorld();
        (await world.RunAsync(new FakeResponder(), roles: [])).Should().Be(SummaryOutcome.RoleMissing);

        // An earlier summary, then 99 new member messages — one of them replying to a message older than that summary.
        var newer = Humans(1001, 99);
        newer[5] = newer[5] with { ReplyToId = 500 };
        world.Discord.Channels[Here.Value] = [Human(500, "özetten önceki eski mesaj"), OurSummary(1000), .. newer];
        var responder = new FakeResponder();
        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.NotEnoughNewMessages);

        responder.Private.Single().Should().Contain("**99**").And.Contain("**1**");
        world.Ai.Calls.Should().Be(0);
        world.Discord.Fetches.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Reply_context_comes_from_this_channels_read_only_and_gets_a_display_name()
    {
        var world = GroundedWorld();
        // 150 member messages and 30 newer bot messages: the scan needs a second page, which also holds older member messages.
        var history = Humans(1, 150);
        history.AddRange(Enumerable.Range(200, 30).Select(i => OtherBot((ulong)i)));
        history[140] = history[140] with { ReplyToId = 20, AuthorId = 2, AuthorName = "toro_global" }; // id 141 → older message of THIS channel
        history[145] = history[145] with { ReplyToId = 9001 }; // id 146 → a message that exists only in another channel
        history[19] = history[19] with { AuthorId = 3, AuthorName = "hasom_global", Content = "Sunucu ne zaman açılıyor?" }; // id 20
        world.Discord.Channels[Here.Value] = history;
        world.Discord.Channels[Other.Value] = [Human(9001, "başka kanalın mesajı")];
        world.Discord.Names = new SummaryNames(new Dictionary<ulong, string> { [2] = "Toro", [3] = "Hasom" }, SummaryMentionNames.Empty);
        world.Ai.Respond = prompt => Task.FromResult(SummaryAiResult.Failed(SummaryAiFailure.ServerError, TimeSpan.FromSeconds(1), 500));

        await world.RunAsync(new FakeResponder());

        var user = world.Ai.Prompts.Single().User;
        user.Should().Contain("""{"m":"m001","u":"Hasom","ctx":true,"t":"Sunucu ne zaman açılıyor?"}""", "the older target is context, with its server display name");
        user.Should().Contain("\"u\":\"Toro\",\"re\":\"m001\"");
        user.Should().Contain("\"re\":\"" + SummaryGrounded.ReplyUnavailableText + "\"").And.NotContain("başka kanalın mesajı");
        world.Discord.ReadChannels.Should().OnlyContain(c => c == Here.Value, "no other channel is read for context");
        world.Discord.Fetches.Should().Be(2, "no extra read for replies");
        Regex.Count(user, "\"ctx\":true").Should().Be(1);
        Regex.Count(user, "\"m\":\"m").Should().Be(101, "100 window messages plus one context record");
    }
}
