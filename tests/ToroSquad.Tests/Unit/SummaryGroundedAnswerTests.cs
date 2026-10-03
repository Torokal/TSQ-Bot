using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The grounded answer's structural checks and rendering: a valid answer becomes the usual Markdown; an unknown source, a
/// quote that is not in its record, a broken or cut-off JSON, oversized fields, context-only support, hidden content in an
/// open text or a technical reference in a visible text refuse the whole answer. These checks prove that sources exist and
/// quotes are intact — not that the model understood them.
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

    private static object E(string message, string quote) => new { message, quote };

    private static object Claim(string text, object[] evidence, string? spoilerTopic = null) => new { text, evidence, spoiler_topic = spoilerTopic };

    private static readonly object[] MainEvidence = [E("m002", "Eski config'i koydum ama çalışmadı.")];
    private static readonly object[] AtmosphereEvidence = [E("m003", "Oyunu yeniden başlattın mı?")];

    private static string Answer(object? main = null, object[]? points = null, object[]? plans = null, object? atmosphere = null, int version = 1) =>
        JsonSerializer.Serialize(new
        {
            version,
            main = main ?? new { text = "Oyun ayarı sorunu ve dizi sohbeti konuşuldu.", evidence = MainEvidence },
            points = points ??
            [
                new
                {
                    topic = "Oyun ayarları",
                    claims = new[]
                    {
                        Claim("Monfy eski config'in önce çalışmadığını, oyunu yeniden başlatınca düzeldiğini söyledi.",
                            [E("m002", "Eski config'i koydum ama çalışmadı."), E("m004", "Evet, şimdi oldu.")]),
                    },
                },
            ],
            plans = plans ?? [],
            atmosphere = atmosphere ?? new { text = "Yardımlaşmalı, sakin bir sohbet.", evidence = AtmosphereEvidence },
        });

    private static SummaryGroundedResult Read(string raw, string? finish = "stop") => SummaryGroundedAnswer.Read(raw, finish, Input);

    [Fact]
    public void A_valid_answer_is_rendered_as_the_usual_markdown_without_any_technical_field()
    {
        var result = Read(Answer(plans: [Claim("Hasom'un önerisiyle yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım, tamam.")])]));

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        result.Markdown.Should().Be(
            "# Son Mesajların Özeti\n\n" +
            "## Ana konu\nOyun ayarı sorunu ve dizi sohbeti konuşuldu.\n\n" +
            "## Önemli noktalar\n- **Oyun ayarları:** Monfy eski config'in önce çalışmadığını, oyunu yeniden başlatınca düzeldiğini söyledi.\n\n" +
            "## Planlar / Kararlar\n- Hasom'un önerisiyle yarın 21.00'de oynanacak.\n\n" +
            "## Genel atmosfer\nYardımlaşmalı, sakin bir sohbet.");
        result.Markdown.Should().NotContain("m00").And.NotContain("quote").And.NotContain("evidence").And.NotContain("{").And.NotContain("\"");
        result.EvidenceCount.Should().Be(5);
    }

    [Fact]
    public void Without_plans_the_plans_section_is_left_out()
    {
        Read(Answer()).Markdown.Should().NotContain("Planlar / Kararlar").And.Contain("## Genel atmosfer");
        Read(Answer().Replace(",\"plans\":[]", "", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.None, "plans may be omitted");
    }

    [Fact]
    public void An_unknown_source_refuses_the_answer()
    {
        Read(Answer(main: new { text = "Konu.", evidence = new[] { E("m099", "Evet, şimdi oldu.") } })).Failure.Should().Be(SummaryGroundedFailure.UnknownSource);
        Read(Answer(main: new { text = "Konu.", evidence = new[] { E("41", "Eski config'i koydum") } })).Failure.Should().Be(SummaryGroundedFailure.UnknownSource, "a Discord id is not a reference");
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
        Read(Answer(main: new { text = "Konu.", evidence = new[] { E(message, quote) } })).Failure.Should().Be(SummaryGroundedFailure.QuoteNotFound);
    }

    [Fact]
    public void Only_whitespace_runs_are_tolerated_in_a_quote()
    {
        Read(Answer(main: new { text = "Konu.", evidence = new[] { E("m002", "  Eski   config'i koydum\nama çalışmadı. ") } })).Failure.Should().Be(SummaryGroundedFailure.None);
    }

    [Fact]
    public void Model_supplied_verification_fields_prove_nothing()
    {
        var raw = Answer(main: new { text = "Konu.", verified = true, confidence = 0.99, evidence = new[] { E("m004", "Hayır, hâlâ olmadı.") } });

        Read(raw).Failure.Should().Be(SummaryGroundedFailure.QuoteNotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Özet hazır değil.")]
    [InlineData("{\"version\":1,\"main\":{\"text\":\"Konu.\"")] // cut off
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
    public void Wrong_version_missing_fields_and_wrong_types_break_the_contract()
    {
        Read(Answer(version: 2)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer().Replace("\"atmosphere\"", "\"mood\"", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: new { text = "Konu." })).Failure.Should().Be(SummaryGroundedFailure.Contract, "evidence is required");
        Read(Answer(main: new { text = "Konu.", evidence = Array.Empty<object>() })).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: new { text = "   ", evidence = MainEvidence })).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(points: [])).Failure.Should().Be(SummaryGroundedFailure.Contract, "at least one point");
        Read(Answer(points: ["metin"])).Failure.Should().Be(SummaryGroundedFailure.Contract);
    }

    [Fact]
    public void Oversized_lists_and_fields_are_refused()
    {
        object Point(int i) => new { topic = "Konu " + i, claims = new[] { Claim("Bilgi.", MainEvidence) } };

        Read(Answer(points: Enumerable.Range(1, 8).Select(Point).ToArray())).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(points: [new { topic = "Konu", claims = Enumerable.Range(1, 4).Select(_ => Claim("Bilgi.", MainEvidence)).ToArray() }])).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(main: new { text = new string('a', 600), evidence = MainEvidence })).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(main: new { text = "Konu.", evidence = Enumerable.Repeat(MainEvidence[0], 4).ToArray() })).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(plans: Enumerable.Range(1, 6).Select(_ => Claim("Plan.", MainEvidence)).ToArray())).Failure.Should().Be(SummaryGroundedFailure.Limit);
    }

    [Fact]
    public void Support_from_context_only_records_is_refused()
    {
        var onlyContext = Answer(points: [new { topic = "Config", claims = new[] { Claim("Hasom config'i sordu.", [E("m001", "Config dosyasını değiştiren var mı?")]) } }]);
        var withWindow = Answer(points: [new { topic = "Config", claims = new[] { Claim("Hasom'un sorusuna Monfy yanıt verdi.", [E("m001", "Config dosyasını değiştiren var mı?"), E("m002", "çalışmadı")]) } }]);

        Read(onlyContext).Failure.Should().Be(SummaryGroundedFailure.ContextOnly);
        Read(withWindow).Failure.Should().Be(SummaryGroundedFailure.None, "context may support a window message");
    }

    [Fact]
    public void A_record_reference_in_a_visible_text_is_refused()
    {
        Read(Answer(main: new { text = "Monfy m004 mesajında sorunun çözüldüğünü söyledi.", evidence = MainEvidence })).Failure.Should().Be(SummaryGroundedFailure.TechnicalLeak);
        Read(Answer(main: new { text = "m416 ile oynadıklarını konuştular.", evidence = MainEvidence })).Failure.Should().Be(SummaryGroundedFailure.None, "not a reference of this request");
    }

    [Fact]
    public void A_claim_quoting_a_hidden_span_is_published_as_a_labelled_native_spoiler()
    {
        var raw = Answer(points:
        [
            new
            {
                topic = "Vinland Saga",
                claims = new[]
                {
                    Claim("Oykeli diziyi bitirdiğini ve çok güzel bulduğunu söyledi.", [E("m005", "bence çok güzel")]),
                    Claim("Finalde Thorfinn'in affettiği konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "Vinland Saga finali"),
                },
            },
        ]);

        var result = Read(raw);

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        result.Markdown.Should().Contain("- **Vinland Saga:** Oykeli diziyi bitirdiğini ve çok güzel bulduğunu söyledi. " +
                                         "**Spoiler (Vinland Saga finali):** ||Finalde Thorfinn'in affettiği konuşuldu.||");
        result.SpoilerClaimCount.Should().Be(1);
        Regex.Count(result.Markdown!, @"\|\|").Should().Be(2, "only the spoiler claim is hidden; the open part of the same message is not");
    }

    [Fact]
    public void A_missing_spoiler_topic_is_not_permission_to_publish_hidden_content_openly()
    {
        var raw = Answer(points: [new { topic = "Dizi", claims = new[] { Claim("Finalde affetme sahnesi olduğu konuşuldu.", [E("m005", "<spoiler>Thorfinn sonunda affediyor</spoiler>")]) } }]);

        Read(raw).Markdown.Should().Contain("**Spoiler (konu belirtilmemiş):** ||Finalde affetme sahnesi olduğu konuşuldu.||");
    }

    [Fact]
    public void A_spoiler_topic_given_for_the_whole_point_only_labels_claims_that_are_protected_anyway()
    {
        var raw = Answer(points:
        [
            new
            {
                topic = "Vinland Saga",
                spoiler_topic = "Vinland Saga finali",
                claims = new[]
                {
                    Claim("Oykeli diziyi çok güzel bulduğunu söyledi.", [E("m005", "bence çok güzel")]),
                    Claim("Finalde affetme sahnesi olduğu konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")]),
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
        Read(Answer(main: new { text = "Dizi finali konuşuldu.", evidence = new[] { E("m005", "Thorfinn sonunda affediyor") } })).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        Read(Answer(atmosphere: new { text = "Duygusal.", evidence = new[] { E("m005", "sonunda affediyor") } })).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // An open text repeating the hidden content verbatim, whatever its evidence.
        Read(Answer(main: new { text = "Thorfinn sonunda affediyor diye konuşuldu.", evidence = MainEvidence })).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        Read(Answer(points: [new { topic = "thorfinn sonunda affediyor", claims = new[] { Claim("Dizi konuşuldu.", [E("m005", "bence çok güzel")]) } }])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // A spoiler label that gives the content away.
        Read(Answer(points: [new { topic = "Dizi", claims = new[] { Claim("Final konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "Thorfinn sonunda affediyor") } }])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
    }

    [Fact]
    public void Visible_texts_are_defused_and_keep_one_line()
    {
        var raw = Answer(
            main: new { text = "## Başlık\n@everyone ||gizli|| <spoiler>etiket</spoiler> konuşuldu.", evidence = MainEvidence },
            points: [new { topic = "**Konu:**", claims = new[] { Claim("- madde\nikinci satır", MainEvidence) } }]);

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
        object Point(int i) => new
        {
            topic = "Konu " + i,
            claims = new[]
            {
                Claim(string.Join(" ", Enumerable.Repeat("açıklama", 45)), MainEvidence),
                Claim(string.Join(" ", Enumerable.Repeat("gizli", 30)), [E("m005", "Thorfinn sonunda affediyor")], "dizi finali"),
            },
        };
        var markdown = Read(Answer(points: Enumerable.Range(1, 5).Select(Point).ToArray())).Markdown!;
        markdown.Length.Should().BeGreaterThan(2000);

        var parts = SummaryOutput.Split(markdown);

        parts.Should().HaveCountGreaterThan(1).And.OnlyContain(p => p.Length <= 2000 && Regex.Count(p, @"\|\|") % 2 == 0);
        parts[0].Should().StartWith("# Son Mesajların Özeti");
    }
}
