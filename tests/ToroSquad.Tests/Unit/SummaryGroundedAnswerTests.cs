using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The grounded answer's structural checks and rendering, on the compact contract (v2: "t" text, "e" evidence pairs
/// [reference, quote], optional "s" spoiler topic): a valid answer becomes the usual Markdown; an unknown source, a quote
/// that is not in its record, a broken or cut-off JSON, the earlier contract, oversized fields or too many claims,
/// context-only support, hidden content in an open text or a technical reference in a visible text refuse the whole answer.
/// These checks prove that sources exist and quotes are intact — not that the model understood them.
/// </summary>
public sealed class SummaryGroundedAnswerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private static SummarySourceMessage Msg(ulong id, ulong author, string name, string text, ulong? replyTo = null) =>
        new(id, T0.AddSeconds(id), SummaryAuthorKind.Member, name, text, [], [], AuthorId: author, ReplyToId: replyTo);

    /// <summary>m001 context (Hasom) · m002 Monfy · m003 Toro · m004 Monfy · m005 Oykeli (spoiler) · m006 Hasom.</summary>
    private static readonly SummaryGroundedInput Input = SummaryGrounded.Build(
        [
            Msg(41, 1, "Monfy", "Eski config'i koydum ama çalışmadı.", replyTo: 5),
            Msg(42, 2, "Toro", "Oyunu yeniden başlattın mı?", replyTo: 41),
            Msg(43, 1, "Monfy", "Evet, şimdi oldu.", replyTo: 42),
            Msg(44, 3, "Oykeli", "Vinland Saga'yı bitirdim ||Thorfinn sonunda affediyor|| bence çok güzel"),
            Msg(45, 4, "Hasom", "Yarın 21.00'de oynayalım, tamam."),
        ],
        [Msg(5, 4, "Hasom", "Config dosyasını değiştiren var mı?")],
        SummaryMentionNames.Empty, Istanbul, 100);

    /// <summary>One evidence pair: [record reference, verbatim quote].</summary>
    private static object E(string message, string quote) => new[] { message, quote };

    /// <summary>A block {"t", "e"} with an optional "s" — the field is absent (not null) when there is no spoiler topic.</summary>
    private static Dictionary<string, object?> Block(string text, object[]? evidence = null, string? spoilerTopic = null)
    {
        var block = new Dictionary<string, object?> { ["t"] = text };
        if (evidence is not null)
            block["e"] = evidence;
        if (spoilerTopic is not null)
            block["s"] = spoilerTopic;
        return block;
    }

    private static object Point(string topic, params object[] claims) => new { topic, claims };

    private static readonly object[] MainEvidence = [E("m002", "ama çalışmadı")];
    private static readonly object[] AtmosphereEvidence = [E("m003", "yeniden başlattın mı?")];

    private static string Answer(object? main = null, object[]? points = null, object[]? plans = null, object? atmosphere = null, int version = 2)
    {
        var answer = new Dictionary<string, object?>
        {
            ["v"] = version,
            ["main"] = main ?? Block("Oyun ayarı sorunu ve dizi sohbeti konuşuldu.", MainEvidence),
            ["points"] = points ??
            [
                Point("Oyun ayarları", Block("Monfy eski config'in önce çalışmadığını, oyunu yeniden başlatınca düzeldiğini söyledi.",
                    [E("m002", "ama çalışmadı"), E("m003", "yeniden başlattın mı?"), E("m004", "şimdi oldu")])),
            ],
        };
        if (plans is not null)
            answer["plans"] = plans;
        answer["atmosphere"] = atmosphere ?? Block("Yardımlaşmalı, sakin bir sohbet.", AtmosphereEvidence);
        return JsonSerializer.Serialize(answer);
    }

    private static SummaryGroundedResult Read(string raw, string? finish = "stop") => SummaryGroundedAnswer.Read(raw, finish, Input);

    [Fact]
    public void A_valid_compact_answer_is_rendered_as_the_usual_markdown_without_any_technical_field()
    {
        var result = Read(Answer(plans: [Block("Hasom'un önerisiyle yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım, tamam.")])]));

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        result.Markdown.Should().Be(
            "# Son Mesajların Özeti\n\n" +
            "## Ana konu\nOyun ayarı sorunu ve dizi sohbeti konuşuldu.\n\n" +
            "## Önemli noktalar\n- **Oyun ayarları:** Monfy eski config'in önce çalışmadığını, oyunu yeniden başlatınca düzeldiğini söyledi.\n\n" +
            "## Planlar / Kararlar\n- Hasom'un önerisiyle yarın 21.00'de oynanacak.\n\n" +
            "## Genel atmosfer\nYardımlaşmalı, sakin bir sohbet.");
        result.Markdown.Should().NotContain("m00").And.NotContain("{").And.NotContain("[").And.NotContain("\"");
        result.EvidenceCount.Should().Be(6, "one for main, three for the correction that needs them, one plan, one atmosphere");
    }

    [Fact]
    public void Without_plans_the_plans_section_is_left_out()
    {
        Read(Answer()).Markdown.Should().NotContain("Planlar / Kararlar").And.Contain("## Genel atmosfer");
        Read(Answer(plans: [])).Failure.Should().Be(SummaryGroundedFailure.None, "an empty list is accepted too");
    }

    [Fact]
    public void An_answer_in_the_earlier_contract_is_not_mistaken_for_the_current_one()
    {
        var v1 = JsonSerializer.Serialize(new
        {
            version = 1,
            main = new { text = "Konu.", evidence = new[] { new { message = "m002", quote = "ama çalışmadı" } } },
            points = new[] { new { topic = "Konu", claims = new[] { new { text = "Bilgi.", evidence = new[] { new { message = "m002", quote = "ama çalışmadı" } }, spoiler_topic = (string?)null } } } },
            plans = Array.Empty<object>(),
            atmosphere = new { text = "Sakin.", evidence = new[] { new { message = "m003", quote = "yeniden başlattın mı?" } } },
        });

        Read(v1).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(version: 1)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer().Replace("\"v\":2", "\"version\":2", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        // The compact version number with the old long field names is not the compact contract either.
        Read(Answer(main: new { text = "Konu.", evidence = MainEvidence })).Failure.Should().Be(SummaryGroundedFailure.Contract);
        SummaryGroundedPrompt.ContractVersion.Should().Be(2);
    }

    [Fact]
    public void An_unknown_source_refuses_the_answer()
    {
        Read(Answer(main: Block("Konu.", [E("m099", "Evet, şimdi oldu.")]))).Failure.Should().Be(SummaryGroundedFailure.UnknownSource);
        Read(Answer(main: Block("Konu.", [E("41", "Eski config'i koydum")]))).Failure.Should().Be(SummaryGroundedFailure.UnknownSource, "a Discord id is not a reference");
    }

    [Theory]
    [InlineData("m003", "Evet, şimdi oldu.")] // a real quote, but of another record
    [InlineData("m004", "Hayır, hâlâ olmadı.")] // invented
    [InlineData("m002", "Eski config'i koydum ama çalıştı.")] // the negation changed
    [InlineData("m004", "Evet şimdi oldu.")] // punctuation dropped
    [InlineData("m004", "evet, şimdi oldu.")] // case changed
    [InlineData("m004", "ol")] // too short to be a quote
    public void A_quote_that_is_not_in_its_record_refuses_the_answer(string message, string quote)
    {
        Read(Answer(main: Block("Konu.", [E(message, quote)]))).Failure.Should().Be(SummaryGroundedFailure.QuoteNotFound);
    }

    [Fact]
    public void Short_meaningful_quotes_are_fine_and_only_whitespace_runs_are_tolerated()
    {
        Read(Answer(main: Block("Konu.", [E("m004", "oldu")]))).Failure.Should().Be(SummaryGroundedFailure.None, "a short quote is not refused for its length");
        Read(Answer(main: Block("Konu.", [E("m002", "  Eski   config'i koydum\nama çalışmadı. ")]))).Failure.Should().Be(SummaryGroundedFailure.None);
    }

    [Fact]
    public void Model_supplied_verification_fields_prove_nothing()
    {
        var main = Block("Konu.", [E("m004", "Hayır, hâlâ olmadı.")]);
        main["verified"] = true;
        main["confidence"] = 0.99;

        Read(Answer(main: main)).Failure.Should().Be(SummaryGroundedFailure.QuoteNotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Özet hazır değil.")]
    [InlineData("{\"v\":2,\"main\":{\"t\":\"Konu.\"")] // cut off
    [InlineData("[1,2,3]")]
    public void Anything_that_is_not_one_complete_json_object_is_refused(string raw)
    {
        Read(raw).Failure.Should().Be(SummaryGroundedFailure.NotJson);
    }

    [Fact]
    public void Text_around_the_object_is_not_cut_away_but_one_code_fence_is()
    {
        Read("İşte özet:\n" + Answer()).Failure.Should().Be(SummaryGroundedFailure.NotJson);
        Read(Answer() + "\nUmarım yardımcı olur.").Failure.Should().Be(SummaryGroundedFailure.NotJson);
        Read("```json\n" + Answer() + "\n```").Failure.Should().Be(SummaryGroundedFailure.None);
    }

    [Fact]
    public void An_answer_cut_by_the_token_limit_is_never_published_even_if_it_parses()
    {
        var result = Read(Answer(), finish: "length");

        (result.Failure, result.Markdown).Should().Be((SummaryGroundedFailure.Truncated, (string?)null));
    }

    [Fact]
    public void Missing_fields_and_wrong_shapes_break_the_contract()
    {
        Read(Answer().Replace("\"atmosphere\"", "\"mood\"", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu."))).Failure.Should().Be(SummaryGroundedFailure.Contract, "main keeps its evidence: no unchecked text");
        Read(Answer(atmosphere: Block("Sakin."))).Failure.Should().Be(SummaryGroundedFailure.Contract, "atmosphere keeps its evidence too");
        Read(Answer(main: Block("Konu.", []))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("   ", MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(points: [])).Failure.Should().Be(SummaryGroundedFailure.Contract, "at least one point");
        Read(Answer(points: ["metin"])).Failure.Should().Be(SummaryGroundedFailure.Contract);
        // Evidence must be [reference, quote] pairs — nothing positional beyond that, no objects.
        Read(Answer(main: Block("Konu.", [new[] { "m002" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", [new[] { "m002", "ama çalışmadı", "fazla" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", [new { message = "m002", quote = "ama çalışmadı" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", ["m002"]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
    }

    [Fact]
    public void The_total_number_of_claims_is_bounded_not_only_each_list()
    {
        object Topic(int i, int claims) => Point("Konu " + i, Enumerable.Range(1, claims).Select(_ => (object)Block("Bilgi.", MainEvidence)).ToArray());
        object[] Plans(int count) => Enumerable.Range(1, count).Select(_ => (object)Block("Plan.", MainEvidence)).ToArray();

        Read(Answer(points: Enumerable.Range(1, 6).Select(i => Topic(i, 1)).ToArray(), plans: Plans(2))).Failure.Should().Be(SummaryGroundedFailure.None, "6 + 2 = 8");
        Read(Answer(points: Enumerable.Range(1, 4).Select(i => Topic(i, 2)).ToArray())).Failure.Should().Be(SummaryGroundedFailure.None, "4 × 2 = 8");
        Read(Answer(points: Enumerable.Range(1, 6).Select(i => Topic(i, 1)).ToArray(), plans: Plans(3))).Failure.Should().Be(SummaryGroundedFailure.Limit, "6 + 3 = 9");
        Read(Answer(points: Enumerable.Range(1, 5).Select(i => Topic(i, 2)).ToArray())).Failure.Should().Be(SummaryGroundedFailure.Limit, "5 × 2 = 10");
        Read(Answer(points: [Topic(1, 3)])).Failure.Should().Be(SummaryGroundedFailure.Limit, "at most two claims per point");
        Read(Answer(points: Enumerable.Range(1, 8).Select(i => Topic(i, 1)).ToArray())).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(plans: Plans(4))).Failure.Should().Be(SummaryGroundedFailure.Limit);
        (SummaryGroundedAnswer.MaxTotalClaims, SummaryGroundedAnswer.MaxClaimsPerPoint, SummaryGroundedAnswer.MaxEvidence).Should().Be((8, 2, 3));
    }

    [Fact]
    public void Oversized_fields_and_too_many_quotes_are_refused()
    {
        Read(Answer(main: Block(new string('a', 600), MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(main: Block("Konu.", Enumerable.Repeat(MainEvidence[0], 4).ToArray()))).Failure.Should().Be(SummaryGroundedFailure.Limit, "three quotes are the most a text may need");
        Read(Answer(points: [Point(new string('k', 200), Block("Bilgi.", MainEvidence))])).Failure.Should().Be(SummaryGroundedFailure.Limit);
    }

    [Fact]
    public void Support_from_context_only_records_is_refused()
    {
        var onlyContext = Answer(points: [Point("Config", Block("Hasom config'i sordu.", [E("m001", "değiştiren var mı?")]))]);
        var withWindow = Answer(points: [Point("Config", Block("Hasom'un sorusuna Monfy yanıt verdi.", [E("m001", "değiştiren var mı?"), E("m002", "çalışmadı")]))]);

        Read(onlyContext).Failure.Should().Be(SummaryGroundedFailure.ContextOnly);
        Read(withWindow).Failure.Should().Be(SummaryGroundedFailure.None, "context may support a window message");
    }

    [Fact]
    public void A_record_reference_in_a_visible_text_is_refused()
    {
        Read(Answer(main: Block("Monfy m004 mesajında sorunun çözüldüğünü söyledi.", MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.TechnicalLeak);
        Read(Answer(main: Block("m416 ile oynadıklarını konuştular.", MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.None, "not a reference of this request");
    }

    [Fact]
    public void A_claim_quoting_a_hidden_span_is_published_as_a_labelled_native_spoiler()
    {
        var raw = Answer(points:
        [
            Point("Vinland Saga",
                Block("Oykeli diziyi bitirdiğini ve çok güzel bulduğunu söyledi.", [E("m005", "bence çok güzel")]),
                Block("Finalde Thorfinn'in affettiği konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "Vinland Saga finali")),
        ]);

        var result = Read(raw);

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        result.Markdown.Should().Contain("- **Vinland Saga:** Oykeli diziyi bitirdiğini ve çok güzel bulduğunu söyledi. " +
                                         "**Spoiler (Vinland Saga finali):** ||Finalde Thorfinn'in affettiği konuşuldu.||");
        result.SpoilerClaimCount.Should().Be(1);
        Regex.Count(result.Markdown!, @"\|\|").Should().Be(2, "only the spoiler claim is hidden; the open part of the same message is not");
    }

    [Fact]
    public void A_missing_or_null_spoiler_field_is_not_permission_to_publish_hidden_content_openly()
    {
        // No "s" at all (the compact contract's normal case for non-spoilers) — but the quote comes from a hidden span.
        var omitted = Answer(points: [Point("Dizi", Block("Finalde affetme sahnesi olduğu konuşuldu.", [E("m005", "<spoiler>Thorfinn sonunda affediyor</spoiler>")]))]);
        omitted.Should().NotContain("\"s\"");
        Read(omitted).Markdown.Should().Contain("**Spoiler (konu belirtilmemiş):** ||Finalde affetme sahnesi olduğu konuşuldu.||");

        // An explicit null is treated the same way; a quote only partly inside the span protects the claim too.
        var explicitNull = Answer(points: [Point("Dizi", new Dictionary<string, object?> { ["t"] = "Final konuşuldu.", ["e"] = new[] { E("m005", "bitirdim Thorfinn sonunda") }, ["s"] = null })]);
        Read(explicitNull).Markdown.Should().Contain("**Spoiler (konu belirtilmemiş):** ||Final konuşuldu.||");

        // The same holds for a plan.
        Read(Answer(plans: [Block("Finali birlikte izleyecekler.", [E("m005", "sonunda affediyor")])])).Markdown
            .Should().Contain("- **Spoiler (konu belirtilmemiş):** ||Finali birlikte izleyecekler.||");
    }

    [Fact]
    public void A_spoiler_topic_given_for_the_whole_point_only_labels_claims_that_are_protected_anyway()
    {
        var raw = Answer(points:
        [
            new
            {
                topic = "Vinland Saga",
                s = "Vinland Saga finali",
                claims = new[]
                {
                    Block("Oykeli diziyi çok güzel bulduğunu söyledi.", [E("m005", "bence çok güzel")]),
                    Block("Finalde affetme sahnesi olduğu konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")]),
                },
            },
        ]);

        Read(raw).Markdown.Should().Contain("- **Vinland Saga:** Oykeli diziyi çok güzel bulduğunu söyledi. " +
                                            "**Spoiler (Vinland Saga finali):** ||Finalde affetme sahnesi olduğu konuşuldu.||");
    }

    [Fact]
    public void Hidden_content_in_an_open_text_is_refused()
    {
        // main / atmosphere relying on a hidden span.
        Read(Answer(main: Block("Dizi finali konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")]))).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        Read(Answer(atmosphere: Block("Duygusal.", [E("m005", "sonunda affediyor")]))).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // An open text repeating the hidden content verbatim, whatever its evidence.
        Read(Answer(main: Block("Thorfinn sonunda affediyor diye konuşuldu.", MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        Read(Answer(points: [Point("thorfinn sonunda affediyor", Block("Dizi konuşuldu.", [E("m005", "bence çok güzel")]))])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // A spoiler label that gives the content away.
        Read(Answer(points: [Point("Dizi", Block("Final konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "Thorfinn sonunda affediyor"))])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
    }

    [Fact]
    public void Visible_texts_are_defused_and_keep_one_line()
    {
        var raw = Answer(
            main: Block("## Başlık\n@everyone ||gizli|| <spoiler>etiket</spoiler> konuşuldu.", MainEvidence),
            points: [Point("**Konu:**", Block("- madde\nikinci satır", MainEvidence))]);

        var markdown = Read(raw).Markdown!;

        markdown.Should().Contain("## Ana konu\nBaşlık @" + (char)0x200B + "everyone gizli etiket konuşuldu.");
        markdown.Should().Contain("- **Konu:** madde ikinci satır");
        Regex.Matches(markdown, "^#{1,2} .+$", RegexOptions.Multiline).Select(m => m.Value)
            .Should().Equal("# Son Mesajların Özeti", "## Ana konu", "## Önemli noktalar", "## Genel atmosfer");
        markdown.Should().NotContain("||").And.NotMatchRegex("@(everyone|here)");
    }

    [Fact]
    public void A_long_grounded_summary_is_split_without_breaking_a_spoiler()
    {
        object Topic(int i) => Point("Konu " + i,
            Block(string.Join(" ", Enumerable.Repeat("açıklama", 50)), MainEvidence),
            Block(string.Join(" ", Enumerable.Repeat("gizli", 35)), [E("m005", "Thorfinn sonunda affediyor")], "dizi finali"));
        var markdown = Read(Answer(points: Enumerable.Range(1, 4).Select(Topic).ToArray())).Markdown!;
        markdown.Length.Should().BeGreaterThan(2000);

        var parts = SummaryOutput.Split(markdown);

        parts.Should().HaveCountGreaterThan(1).And.OnlyContain(p => p.Length <= 2000 && Regex.Count(p, @"\|\|") % 2 == 0);
        parts[0].Should().StartWith("# Son Mesajların Özeti");
    }
}
