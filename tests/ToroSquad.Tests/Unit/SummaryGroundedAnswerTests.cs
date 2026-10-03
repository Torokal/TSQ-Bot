using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The grounded answer's checks, selection and rendering, on the flat contract (v3: every point carries its own "topic",
/// "t" text, "e" evidence pairs [reference, quote] and an optional "s" spoiler topic). Source safety is strict and covers
/// every candidate: an unknown source, a quote that is not in its record, a broken or cut-off JSON, an earlier contract,
/// oversized fields, context-only support, hidden content in an open text or a technical reference refuse the whole answer —
/// also when the faulty item would not have been shown. The display target is separate: a few more points or plans than
/// are shown is not a failure; the first ones are shown whole, in the model's order. These checks prove that sources exist
/// and quotes are intact — not that the model understood them or wrote each point as self-contained as it was asked to.
/// </summary>
public sealed class SummaryGroundedAnswerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private static SummarySourceMessage Msg(ulong id, ulong author, string name, string text, ulong? replyTo = null) =>
        new(id, T0.AddSeconds(id), SummaryAuthorKind.Member, name, text, [], [], AuthorId: author, ReplyToId: replyTo);

    /// <summary>m001 context (Hasom) · m002 Monfy · m003 Toro · m004 Monfy · m005 Oykeli · m006 Hasom.</summary>
    private static SummaryGroundedInput Window(string series) => SummaryGrounded.Build(
        [
            Msg(41, 1, "Monfy", "Eski config'i koydum ama çalışmadı.", replyTo: 5),
            Msg(42, 2, "Toro", "Oyunu yeniden başlattın mı?", replyTo: 41),
            Msg(43, 1, "Monfy", "Evet, şimdi oldu.", replyTo: 42),
            Msg(44, 3, "Oykeli", series),
            Msg(45, 4, "Hasom", "Yarın 21.00'de oynayalım, tamam."),
        ],
        [Msg(5, 4, "Hasom", "Config dosyasını değiştiren var mı?")],
        SummaryMentionNames.Empty, Istanbul, 100);

    /// <summary>No hidden part anywhere: nothing is required.</summary>
    private static readonly SummaryGroundedInput Input = Window("Vinland Saga'yı bitirdim, bence çok güzel");

    /// <summary>m005 has a hidden part, so it is a required spoiler source.</summary>
    private static readonly SummaryGroundedInput Hidden = Window("Vinland Saga'yı bitirdim ||Thorfinn sonunda affediyor|| bence çok güzel");

    /// <summary>
    /// m001 context (Hasom, hidden part — never required) · m002 Oykeli (Dizi A) · m003 Aiwen (Dizi A) · m004 Monfy (Film B) ·
    /// m005 Zel (Oyun C) · m006 Toro (open). Required: m002–m005.
    /// </summary>
    private static readonly SummaryGroundedInput Many = SummaryGrounded.Build(
        [
            Msg(51, 3, "Oykeli", "Dizi A finali ||kahraman ihanet ediyor|| şaşırdım", replyTo: 6),
            Msg(52, 5, "Aiwen", "Dizi A'da ||gemi son bölümde batıyor|| üzüldüm"),
            Msg(53, 1, "Monfy", "Film B'de ||katil aslında uşak çıkıyor|| vay"),
            Msg(54, 6, "Zel", "Oyun C'de ||son boss kahramanın kardeşi|| inanamadım"),
            Msg(55, 2, "Toro", "Yarın 21.00'de oynayalım, tamam."),
        ],
        [Msg(6, 4, "Hasom", "Eski sezonda ||baba karakteri ölüyor|| demiştim")],
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

    /// <summary>A flat point: the block's fields plus its own "topic".</summary>
    private static Dictionary<string, object?> Point(string topic, string text, object[]? evidence = null, string? spoilerTopic = null)
    {
        var point = Block(text, evidence, spoilerTopic);
        point["topic"] = topic;
        return point;
    }

    private static readonly object[] MainEvidence = [E("m002", "ama çalışmadı")];
    private static readonly object[] AtmosphereEvidence = [E("m003", "yeniden başlattın mı?")];
    private static readonly object[] Correction = [E("m002", "ama çalışmadı"), E("m003", "yeniden başlattın mı?"), E("m004", "şimdi oldu")];
    private const string CorrectionText = "Monfy eski config'in önce çalışmadığını, oyunu yeniden başlatınca düzeldiğini söyledi.";

    private static string Answer(object? main = null, object[]? points = null, object[]? plans = null, object? atmosphere = null, int version = 3)
    {
        var answer = new Dictionary<string, object?>
        {
            ["v"] = version,
            ["main"] = main ?? Block("Oyun ayarı sorunu ve dizi sohbeti konuşuldu.", MainEvidence),
            ["points"] = points ?? [Point("Oyun ayarları", CorrectionText, Correction)],
        };
        if (plans is not null)
            answer["plans"] = plans;
        answer["atmosphere"] = atmosphere ?? Block("Yardımlaşmalı, sakin bir sohbet.", AtmosphereEvidence);
        return JsonSerializer.Serialize(answer);
    }

    /// <summary>Distinct valid points "Konu 1" … "Konu n", each with its own text.</summary>
    private static object[] Points(int count) => Enumerable.Range(1, count).Select(i => (object)Point("Konu " + i, "Bilgi " + i + ".", MainEvidence)).ToArray();

    private static object[] Plans(int count) => Enumerable.Range(1, count).Select(i => (object)Block("Plan " + i + ".", [E("m006", "oynayalım, tamam")])).ToArray();

    private static SummaryGroundedResult Read(string raw, string? finish = "stop", SummaryGroundedInput? input = null) =>
        SummaryGroundedAnswer.Read(raw, finish, input ?? Input);

    private static SummaryGroundedResult ReadHidden(string raw) => Read(raw, input: Hidden);

    private static readonly object[] OpenEvidence = [E("m006", "oynayalım, tamam")];

    /// <summary>An answer for <see cref="Many"/>: main and atmosphere rest on the one open record.</summary>
    private static SummaryGroundedResult ReadMany(params object[] points) => Read(
        Answer(main: Block("Dizi, film ve oyun finalleri konuşuldu.", OpenEvidence), points: points, atmosphere: Block("Heyecanlı bir sohbet.", [E("m006", "Yarın 21.00'de")])),
        input: Many);

    private static object Open(int i) => Point("Konu " + i, "Bilgi " + i + ".", OpenEvidence);

    /// <summary>A spoiler point quoting the given records from their hidden parts.</summary>
    private static object Spoiler(string topic, string text, params (string Ref, string Quote)[] quotes) =>
        Point(topic, text, quotes.Select(q => E(q.Ref, q.Quote)).ToArray(), topic + " finali");

    private static readonly object DiziA = Spoiler("Dizi A", "Kahramanın ihanet ettiği ve geminin battığı konuşuldu.", ("m002", "kahraman ihanet ediyor"), ("m003", "gemi son bölümde batıyor"));
    private static readonly object FilmB = Spoiler("Film B", "Katilin uşak çıktığı söylendi.", ("m004", "katil aslında uşak çıkıyor"));
    private static readonly object OyunC = Spoiler("Oyun C", "Son boss'un kahramanın kardeşi olduğu söylendi.", ("m005", "son boss kahramanın kardeşi"));

    private static List<string> Bullets(string markdown) => Regex.Matches(markdown, "^- .+$", RegexOptions.Multiline).Select(m => m.Value).ToList();

    [Fact]
    public void A_valid_flat_answer_is_rendered_as_the_usual_markdown_without_any_technical_field()
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
        (result.CandidatePoints, result.CandidatePlans, result.ShownPoints, result.ShownPlans).Should().Be((1, 1, 1, 1));
    }

    [Fact]
    public void Without_plans_the_plans_section_is_left_out()
    {
        Read(Answer()).Markdown.Should().NotContain("Planlar / Kararlar").And.Contain("## Genel atmosfer");
        Read(Answer(plans: [])).Failure.Should().Be(SummaryGroundedFailure.None, "an empty list is accepted too");
    }

    [Fact]
    public void An_answer_in_an_earlier_contract_is_not_mistaken_for_the_current_one()
    {
        var v1 = JsonSerializer.Serialize(new
        {
            version = 1,
            main = new { text = "Konu.", evidence = new[] { new { message = "m002", quote = "ama çalışmadı" } } },
            points = new[] { new { topic = "Konu", claims = new[] { new { text = "Bilgi.", evidence = new[] { new { message = "m002", quote = "ama çalışmadı" } }, spoiler_topic = (string?)null } } } },
            plans = Array.Empty<object>(),
            atmosphere = new { text = "Sakin.", evidence = new[] { new { message = "m003", quote = "yeniden başlattın mı?" } } },
        });
        // v2: the same short keys, but claims nested under each point.
        object[] nested = [new { topic = "Oyun ayarları", claims = new[] { Block(CorrectionText, Correction) } }];

        Read(v1).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(points: nested, version: 2)).Failure.Should().Be(SummaryGroundedFailure.Contract, "a v2 answer is refused by its version");
        Read(Answer(points: nested)).Failure.Should().Be(SummaryGroundedFailure.Contract, "nested claims under the new version number are not the flat contract");
        Read(Answer(version: 2)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(version: 1)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer().Replace("\"v\":3", "\"version\":3", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: new { text = "Konu.", evidence = MainEvidence })).Failure.Should().Be(SummaryGroundedFailure.Contract);
        SummaryGroundedPrompt.ContractVersion.Should().Be(3);
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
    [InlineData("m002", "Eski config koydum ama çalışmadı.")] // a suffix dropped: close in meaning is not verbatim
    [InlineData("m004", "Evet şimdi oldu.")] // punctuation dropped
    [InlineData("m004", "evet, şimdi oldu.")] // case changed
    [InlineData("m004", "ol")] // too short to be a quote
    public void A_quote_that_is_not_in_its_record_refuses_the_answer(string message, string quote)
    {
        Read(Answer(main: Block("Konu.", [E(message, quote)]))).Failure.Should().Be(SummaryGroundedFailure.QuoteNotFound);
        Read(Answer(points: [Point("Konu", "Bilgi.", [E(message, quote)])])).Failure.Should().Be(SummaryGroundedFailure.QuoteNotFound);
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
    [InlineData("{\"v\":3,\"main\":{\"t\":\"Konu.\"")] // cut off
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
        var result = Read(Answer(points: Points(8)), finish: "length");

        (result.Failure, result.Markdown).Should().Be((SummaryGroundedFailure.Truncated, (string?)null), "selection is not a way to publish a cut-off answer");
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
        Read(Answer(points: [Block("Konusu olmayan bilgi.", MainEvidence)])).Failure.Should().Be(SummaryGroundedFailure.Contract, "a point carries its topic");
        Read(Answer(points: [Point("Konu", "Dayanaksız bilgi.")])).Failure.Should().Be(SummaryGroundedFailure.Contract, "a point carries its evidence");
        // Evidence must be [reference, quote] pairs — nothing positional beyond that, no objects.
        Read(Answer(main: Block("Konu.", [new[] { "m002" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", [new[] { "m002", "ama çalışmadı", "fazla" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", [new { message = "m002", quote = "ama çalışmadı" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", ["m002"]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
    }

    [Fact]
    public void Up_to_the_display_target_everything_is_shown()
    {
        var result = Read(Answer(points: Points(6), plans: Plans(2)));

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        Bullets(result.Markdown!).Should().Equal(
            "- **Konu 1:** Bilgi 1.", "- **Konu 2:** Bilgi 2.", "- **Konu 3:** Bilgi 3.", "- **Konu 4:** Bilgi 4.", "- **Konu 5:** Bilgi 5.", "- **Konu 6:** Bilgi 6.",
            "- Plan 1.", "- Plan 2.");
        (result.CandidatePoints, result.CandidatePlans, result.ShownPoints, result.ShownPlans).Should().Be((6, 2, 6, 2));
        (SummaryGroundedAnswer.ShownPoints, SummaryGroundedAnswer.ShownPlans).Should().Be((6, 2));
    }

    [Theory]
    [InlineData(7, 2)]
    [InlineData(8, 3)]
    [InlineData(12, 4)] // the safety bound itself
    public void A_few_more_valid_points_or_plans_than_are_shown_are_selected_in_the_models_order_not_refused(int points, int plans)
    {
        var result = Read(Answer(points: Points(points), plans: Plans(plans)));

        result.Failure.Should().Be(SummaryGroundedFailure.None, "the display target is not a safety rule");
        Bullets(result.Markdown!).Should().Equal(
            "- **Konu 1:** Bilgi 1.", "- **Konu 2:** Bilgi 2.", "- **Konu 3:** Bilgi 3.", "- **Konu 4:** Bilgi 4.", "- **Konu 5:** Bilgi 5.", "- **Konu 6:** Bilgi 6.",
            "- Plan 1.", "- Plan 2.");
        result.Markdown.Should().NotContain("Konu 7").And.NotContain("Plan 3");
        (result.CandidatePoints, result.CandidatePlans, result.ShownPoints, result.ShownPlans).Should().Be((points, plans, 6, 2));
        result.EvidenceCount.Should().Be(2 + points + plans, "every candidate was checked, shown or not");
    }

    [Fact]
    public void More_candidates_than_the_safety_bound_are_refused()
    {
        Read(Answer(points: Points(13))).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(points: Points(40))).Failure.Should().Be(SummaryGroundedFailure.Limit, "no unbounded list");
        Read(Answer(plans: Plans(5))).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer() + new string(' ', 10) + "\n").Failure.Should().Be(SummaryGroundedFailure.None);
        Read(Answer(main: Block("Konu.", MainEvidence)).Replace("\"v\":3", "\"v\":3,\"pad\":\"" + new string('x', SummaryGroundedAnswer.MaxAnswerChars) + "\"", StringComparison.Ordinal))
            .Failure.Should().Be(SummaryGroundedFailure.Limit, "the whole answer is bounded too");
        (SummaryGroundedAnswer.MaxCandidatePoints, SummaryGroundedAnswer.MaxCandidatePlans, SummaryGroundedAnswer.MaxEvidence).Should().Be((12, 4, 5));
    }

    [Theory]
    [InlineData("m004", "Hayır, hâlâ olmadı.", SummaryGroundedFailure.QuoteNotFound)]
    [InlineData("m002", "Eski config koydum", SummaryGroundedFailure.QuoteNotFound)] // "close enough" is still not verbatim
    [InlineData("m099", "şimdi oldu", SummaryGroundedFailure.UnknownSource)]
    [InlineData("m001", "değiştiren var mı?", SummaryGroundedFailure.ContextOnly)]
    public void A_source_error_in_an_item_that_would_not_be_shown_still_refuses_the_whole_answer(string message, string quote, SummaryGroundedFailure expected)
    {
        var points = Points(8);
        points[7] = Point("Konu 8", "Gösterilmeyecek ama hatalı bilgi.", [E(message, quote)]); // beyond the six that are shown
        var plans = Plans(3);
        plans[2] = Block("Gösterilmeyecek ama hatalı plan.", [E(message, quote)]);

        var extraPoint = Read(Answer(points: points));
        var extraPlan = Read(Answer(plans: plans));

        (extraPoint.Failure, extraPoint.Markdown).Should().Be((expected, (string?)null), "a faulty item is never dropped silently to call the rest verified");
        (extraPlan.Failure, extraPlan.Markdown).Should().Be((expected, (string?)null));
    }

    [Fact]
    public void Hidden_content_or_a_reference_in_an_item_that_would_not_be_shown_still_refuses_the_answer()
    {
        var leak = Points(8);
        leak[7] = Point("Konu 8", "Thorfinn sonunda affediyor diye konuşuldu.", MainEvidence);
        var reference = Points(8);
        reference[6] = Point("Konu 7", "Monfy m004 mesajında düzeldiğini söyledi.", MainEvidence);

        ReadHidden(Answer(points: leak)).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        ReadHidden(Answer(points: reference)).Failure.Should().Be(SummaryGroundedFailure.TechnicalLeak);
    }

    [Fact]
    public void A_correction_written_as_one_complete_point_survives_selection_as_a_whole()
    {
        // "did not work → then it worked" in ONE self-contained point, among more points than are shown.
        var points = Points(8);
        points[2] = Point("Oyun ayarları", CorrectionText, Correction);

        var markdown = Read(Answer(points: points)).Markdown!;

        markdown.Should().Contain("- **Oyun ayarları:** " + CorrectionText + "\n", "the earlier state and its correction stay together and unshortened");
        Bullets(markdown).Should().HaveCount(6).And.OnlyContain(b => b.EndsWith('.'), "whole items only: nothing is cut inside a sentence");
    }

    [Fact]
    public void Selection_keeps_or_leaves_whole_items_and_never_cuts_inside_a_spoiler()
    {
        var points = Points(8);
        points[5] = Point("Vinland Saga", "Finalde Thorfinn'in affettiği konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "Vinland Saga finali"); // the last shown one
        points[6] = Point("Dizi", "Gösterilmeyen gizli bilgi.", [E("m005", "sonunda affediyor")], "dizi finali"); // the first one left out

        var result = ReadHidden(Answer(points: points));

        var bullets = Bullets(result.Markdown!);
        bullets.Should().HaveCount(6);
        bullets[5].Should().Be("- **Vinland Saga:** **Spoiler (Vinland Saga finali):** ||Finalde Thorfinn'in affettiği konuşuldu.||");
        Regex.Count(result.Markdown!, @"\|\|").Should().Be(2, "the shown spoiler is complete; the one left out leaves no half mark");
        result.Markdown.Should().NotContain("Gösterilmeyen").And.NotContain("dizi finali");
        result.SpoilerClaimCount.Should().Be(1, "counts what is published");
        (result.SpoilerCandidates, result.RequiredSpoilers, result.ReservedSpoilerPoints).Should().Be((2, 1, 1), "one point keeps the coverage; the second one for the same source is an ordinary candidate");
    }

    private static readonly string[] FirstSix = ["- **Konu 1:** Bilgi 1.", "- **Konu 2:** Bilgi 2.", "- **Konu 3:** Bilgi 3.", "- **Konu 4:** Bilgi 4.", "- **Konu 5:** Bilgi 5.", "- **Konu 6:** Bilgi 6."];

    [Fact]
    public void Without_a_hidden_part_nothing_is_required_and_selection_is_the_plain_first_six()
    {
        var result = Read(Answer(points: Points(9), plans: Plans(3)));

        Bullets(result.Markdown!).Should().Equal(FirstSix.Concat(["- Plan 1.", "- Plan 2."]));
        (result.RequiredSpoilers, result.ReservedSpoilerPoints, result.SpoilerCandidates, result.SpoilerClaimCount).Should().Be((0, 0, 0, 0));
        Input.RequiredSpoilerSources.Should().BeEmpty();
    }

    [Fact]
    public void Required_spoiler_sources_come_from_the_record_map_and_never_include_context()
    {
        Hidden.RequiredSpoilerSources.Should().Equal("m005");
        Many.RequiredSpoilerSources.Should().Equal("m002", "m003", "m004", "m005");
        (Many.Records["m001"].HasSpoiler, Many.Records["m001"].ContextOnly).Should().Be((true, true), "a context record with a hidden part is told apart, and is not required");
        Many.Records["m006"].HasSpoiler.Should().BeFalse();
    }

    [Fact]
    public void A_required_spoiler_point_below_the_cut_keeps_a_shown_place_as_a_whole()
    {
        var points = Points(8);
        points[2] = Point("Oyun ayarları", CorrectionText, Correction); // a correction among the shown ones: must stay whole and in place
        points[6] = Point("Vinland Saga", "Finalde Thorfinn'in affettiği konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "Vinland Saga finali");

        var result = ReadHidden(Answer(points: points, plans: Plans(3)));

        Bullets(result.Markdown!).Should().Equal(
            "- **Konu 1:** Bilgi 1.", "- **Konu 2:** Bilgi 2.", "- **Oyun ayarları:** " + CorrectionText, "- **Konu 4:** Bilgi 4.", "- **Konu 5:** Bilgi 5.",
            "- **Vinland Saga:** **Spoiler (Vinland Saga finali):** ||Finalde Thorfinn'in affettiği konuşuldu.||",
            "- Plan 1.", "- Plan 2.");
        result.Markdown.Should().NotContain("Konu 6").And.NotContain("Bilgi 6", "the last ordinary point leaves as a whole — nothing of it remains");
        result.Markdown.Should().NotContain("Konu 8");
        Regex.Count(result.Markdown!, @"\|\|").Should().Be(2);
        (result.ShownPoints, result.ShownPlans, result.RequiredSpoilers, result.ReservedSpoilerPoints, result.SpoilerClaimCount).Should().Be((6, 2, 1, 1, 1));
    }

    [Fact]
    public void Of_two_points_quoting_the_same_required_source_only_the_first_is_reserved()
    {
        var points = Points(8);
        points[6] = Point("Dizi", "Yedinci sıradaki gizli bilgi.", [E("m005", "Thorfinn sonunda affediyor")], "dizi finali");
        points[7] = Point("Dizi", "Sekizinci sıradaki gizli bilgi.", [E("m005", "sonunda affediyor")], "dizi finali");

        var result = ReadHidden(Answer(points: points));

        var bullets = Bullets(result.Markdown!);
        bullets.Take(5).Should().Equal(FirstSix.Take(5));
        bullets[5].Should().Be("- **Dizi:** **Spoiler (dizi finali):** ||Yedinci sıradaki gizli bilgi.||");
        result.Markdown.Should().NotContain("Sekizinci", "a place is kept for coverage, not for every spoiler point");
        (result.ShownPoints, result.SpoilerCandidates, result.ReservedSpoilerPoints, result.SpoilerClaimCount).Should().Be((6, 2, 1, 1));
    }

    [Fact]
    public void A_required_source_that_no_spoiler_point_quotes_from_its_hidden_part_refuses_the_answer()
    {
        // No spoiler point at all: the spoiler topic was simply left out.
        var leftOut = ReadHidden(Answer(points: Points(6)));
        // An "s" label, or the word "spoiler", with a quote from the OPEN part of the required record is not coverage.
        var labelOnly = ReadHidden(Answer(points: [Point("Dizi", "Dizi hakkında spoiler paylaşıldı.", [E("m005", "bence çok güzel")], "dizi finali")]));
        var wordOnly = ReadHidden(Answer(points: [Point("Spoiler", "Spoiler konuşuldu.", MainEvidence)]));

        (leftOut.Failure, leftOut.Markdown).Should().Be((SummaryGroundedFailure.MissingRequiredSpoiler, (string?)null));
        labelOnly.Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler, "only a verified quote from the hidden part covers a source");
        wordOnly.Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler);
        // Without a required source the same labelled point is still just published hidden, as before.
        Read(Answer(points: [Point("Dizi", "Dizi hakkında spoiler paylaşıldı.", [E("m005", "bence çok güzel")], "dizi finali")])).Failure.Should().Be(SummaryGroundedFailure.None);
    }

    [Fact]
    public void A_point_without_the_spoiler_field_but_quoting_a_hidden_span_covers_the_source_and_gets_the_fallback_label()
    {
        var points = Points(8);
        points[7] = Point("Dizi", "Finalde affetme sahnesi olduğu konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")]); // no "s"

        var result = ReadHidden(Answer(points: points));

        Bullets(result.Markdown!)[5].Should().Be("- **Dizi:** **Spoiler (konu belirtilmemiş):** ||Finalde affetme sahnesi olduğu konuşuldu.||");
        result.Markdown.Should().NotContain("Konu 6").And.NotContain("Konu 7");
        (result.RequiredSpoilers, result.ReservedSpoilerPoints).Should().Be((1, 1));
    }

    [Fact]
    public void A_broken_quote_still_refuses_everything_even_when_the_spoiler_coverage_is_complete()
    {
        var points = Points(9);
        points[6] = Point("Dizi", "Finalde affetme sahnesi olduğu konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "dizi finali");
        points[8] = Point("Konu 9", "Gösterilmeyecek ama hatalı bilgi.", [E("m002", "Eski config koydum")]);

        var result = ReadHidden(Answer(points: points));

        (result.Failure, result.Markdown).Should().Be((SummaryGroundedFailure.QuoteNotFound, (string?)null));
    }

    [Fact]
    public void Coverage_does_not_prove_that_the_text_summarises_the_hidden_event()
    {
        // What the reader can check is where the quote comes from — not whether the text says what happened.
        var points = Points(7);
        points[6] = Point("Dizi", "Final hakkında spoiler paylaşıldı.", [E("m005", "Thorfinn sonunda affediyor")], "dizi finali");

        var result = ReadHidden(Answer(points: points));

        result.Failure.Should().Be(SummaryGroundedFailure.None, "an empty sentence with the right quote passes the structural check");
        result.Markdown.Should().Contain("**Spoiler (dizi finali):** ||Final hakkında spoiler paylaşıldı.||", "hidden and shown, but exactly as empty as the model wrote it");
    }

    [Fact]
    public void One_spoiler_point_may_cover_several_required_sources_and_all_reserved_points_are_shown()
    {
        var result = ReadMany(Open(1), DiziA, FilmB, OyunC);

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        Bullets(result.Markdown!).Should().Equal(
            "- **Konu 1:** Bilgi 1.",
            "- **Dizi A:** **Spoiler (Dizi A finali):** ||Kahramanın ihanet ettiği ve geminin battığı konuşuldu.||",
            "- **Film B:** **Spoiler (Film B finali):** ||Katilin uşak çıktığı söylendi.||",
            "- **Oyun C:** **Spoiler (Oyun C finali):** ||Son boss'un kahramanın kardeşi olduğu söylendi.||");
        (result.RequiredSpoilers, result.ReservedSpoilerPoints, result.SpoilerClaimCount).Should().Be((4, 3, 3));
    }

    [Fact]
    public void One_uncovered_source_among_several_refuses_the_answer()
    {
        ReadMany(Open(1), DiziA, FilmB).Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler, "m005 is not quoted from its hidden part");
        // The same record quoted from its OPEN part inside a labelled point does not help.
        ReadMany(Open(1), DiziA, FilmB, Spoiler("Oyun C", "Oyun C finali konuşuldu.", ("m005", "inanamadım")))
            .Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler);
    }

    [Fact]
    public void A_context_only_record_with_a_hidden_part_is_neither_required_nor_coverage()
    {
        // Quoting the context record's hidden part (together with a window record) protects the point, but covers nothing.
        var old = Point("Eski sezon", "Eski sezondaki bir olay hatırlatıldı.", [E("m001", "baba karakteri ölüyor"), E("m006", "tamam")], "eski sezon");

        var complete = ReadMany(DiziA, FilmB, OyunC, old);
        var missing = ReadMany(DiziA, FilmB, old);

        complete.Failure.Should().Be(SummaryGroundedFailure.None, "m001 is context: nobody has to cover it");
        complete.Markdown.Should().Contain("- **Eski sezon:** **Spoiler (eski sezon):** ||Eski sezondaki bir olay hatırlatıldı.||");
        (complete.RequiredSpoilers, complete.ReservedSpoilerPoints, complete.SpoilerCandidates).Should().Be((4, 3, 4));
        missing.Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler, "the context quote does not stand in for m005");
    }

    [Fact]
    public void Two_spoiler_points_below_six_ordinary_ones_are_both_kept_within_six()
    {
        var rest = Spoiler("Film ve oyun", "Katilin uşak, son boss'un da kardeş çıktığı söylendi.", ("m004", "katil aslında uşak"), ("m005", "kahramanın kardeşi"));

        var result = ReadMany(Open(1), Open(2), Open(3), Open(4), Open(5), Open(6), DiziA, rest);

        var bullets = Bullets(result.Markdown!);
        bullets.Should().HaveCount(6);
        bullets.Take(4).Should().Equal("- **Konu 1:** Bilgi 1.", "- **Konu 2:** Bilgi 2.", "- **Konu 3:** Bilgi 3.", "- **Konu 4:** Bilgi 4.");
        bullets[4].Should().StartWith("- **Dizi A:** **Spoiler (Dizi A finali):** ||");
        bullets[5].Should().StartWith("- **Film ve oyun:** **Spoiler (Film ve oyun finali):** ||").And.EndWith("||");
        (result.CandidatePoints, result.ShownPoints, result.ReservedSpoilerPoints).Should().Be((8, 6, 2));
    }

    [Fact]
    public void Three_spoiler_points_can_all_be_kept()
    {
        var result = ReadMany(Open(1), Open(2), Open(3), Open(4), Open(5), Open(6), DiziA, FilmB, OyunC);

        Bullets(result.Markdown!).Select(b => b[..12]).Should().Equal("- **Konu 1:*", "- **Konu 2:*", "- **Konu 3:*", "- **Dizi A:*", "- **Film B:*", "- **Oyun C:*");
        (result.ShownPoints, result.ReservedSpoilerPoints, result.SpoilerClaimCount).Should().Be((6, 3, 3));
        SummaryGroundedAnswer.MaxShownSpoilerPoints.Should().Be(3);
    }

    [Fact]
    public void Coverage_that_needs_more_spoiler_points_than_may_be_shown_is_refused_and_texts_are_never_merged()
    {
        var a1 = Spoiler("Dizi A", "Kahramanın ihanet ettiği söylendi.", ("m002", "kahraman ihanet ediyor"));
        var a2 = Spoiler("Dizi A", "Geminin battığı söylendi.", ("m003", "gemi son bölümde batıyor"));

        var four = ReadMany(Open(1), a1, a2, FilmB, OyunC);

        (four.Failure, four.Markdown).Should().Be((SummaryGroundedFailure.SpoilerPointLimit, (string?)null), "four separate spoiler points cannot all be shown, and code does not merge them");
    }

    [Fact]
    public void The_fewest_points_that_cover_everything_are_reserved_and_the_others_stay_ordinary_candidates()
    {
        var a1 = Spoiler("Dizi A", "Kahramanın ihanet ettiği söylendi.", ("m002", "kahraman ihanet ediyor"));
        var a2 = Spoiler("Dizi A", "Geminin battığı söylendi.", ("m003", "gemi son bölümde batıyor"));

        // a1 and a2 alone would need four points; with the combined DiziA point three are enough.
        var result = ReadMany(a1, a2, FilmB, OyunC, DiziA, Open(1), Open(2), Open(3), Open(4));

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        Bullets(result.Markdown!).Select(b => b[..12]).Should().Equal("- **Dizi A:*", "- **Dizi A:*", "- **Film B:*", "- **Oyun C:*", "- **Dizi A:*", "- **Konu 1:*");
        (result.ShownPoints, result.ReservedSpoilerPoints, result.SpoilerClaimCount).Should().Be((6, 3, 5));
    }

    [Fact]
    public void A_spoiler_point_that_keeps_the_coverage_does_not_give_way_to_an_identical_plan()
    {
        object[] quote = [E("m005", "Thorfinn sonunda affediyor")];

        var result = ReadHidden(Answer(points: [Point("Dizi", "Finali birlikte izleyecekler.", quote)], plans: [Block("Finali birlikte izleyecekler.", quote)]));

        Bullets(result.Markdown!).Should().Equal("- **Dizi:** **Spoiler (konu belirtilmemiş):** ||Finali birlikte izleyecekler.||");
        (result.ShownPoints, result.ShownPlans).Should().Be((1, 0));
    }

    [Theory]
    [InlineData(4, SummaryGroundedFailure.None)]
    [InlineData(5, SummaryGroundedFailure.None)]
    [InlineData(6, SummaryGroundedFailure.Limit)]
    public void Up_to_five_checked_quotes_per_text_are_accepted_as_a_safety_ceiling_not_a_target(int quotes, SummaryGroundedFailure expected)
    {
        object[] all = [E("m002", "ama çalışmadı"), E("m003", "yeniden başlattın mı?"), E("m004", "şimdi oldu"), E("m006", "oynayalım, tamam"), E("m002", "Eski config'i koydum"), E("m004", "Evet")];
        var evidence = all.Take(quotes).ToArray();

        var onPoint = Read(Answer(points: [Point("Konu", "Bilgi.", evidence)]));
        var onMain = Read(Answer(main: Block("Konu.", evidence)));

        (onPoint.Failure, onMain.Failure).Should().Be((expected, expected));
        if (expected == SummaryGroundedFailure.None)
            onPoint.EvidenceCount.Should().Be(quotes + 2, "every one of them was checked");
        SummaryGroundedAnswer.MaxEvidence.Should().Be(5);
    }

    [Fact]
    public void One_false_quote_among_four_still_refuses_the_answer()
    {
        object[] evidence = [E("m002", "ama çalışmadı"), E("m003", "yeniden başlattın mı?"), E("m004", "Hayır, hâlâ olmadı."), E("m006", "oynayalım, tamam")];

        Read(Answer(points: [Point("Konu", "Bilgi.", evidence)])).Failure.Should().Be(SummaryGroundedFailure.QuoteNotFound, "a higher ceiling does not loosen the check of each quote");
        Read(Answer(points: [Point("Konu", "Bilgi.", [.. evidence.Take(2), E("m099", "şimdi oldu"), evidence[3]])])).Failure.Should().Be(SummaryGroundedFailure.UnknownSource);
    }

    [Fact]
    public void Exact_copies_are_shown_once_and_a_point_that_is_exactly_a_plan_gives_way_to_the_plan()
    {
        var plan = Block("Yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım")]);
        var result = Read(Answer(
            points:
            [
                Point("Oyun ayarları", CorrectionText, Correction),
                Point("Oyun ayarları", CorrectionText, Correction.AsEnumerable().Reverse().ToArray()), // the same record, evidence in another order
                Point("Maç planı", "Yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım")]), // exactly the plan below
            ],
            plans: [plan, plan]));

        Bullets(result.Markdown!).Should().Equal("- **Oyun ayarları:** " + CorrectionText, "- Yarın 21.00'de oynanacak.");
        (result.CandidatePoints, result.CandidatePlans, result.ShownPoints, result.ShownPlans).Should().Be((3, 2, 1, 1));
    }

    [Fact]
    public void Similar_but_different_records_are_never_merged()
    {
        var result = Read(Answer(points:
        [
            Point("Config", "Monfy config'in çalışmadığını söyledi.", MainEvidence),
            Point("Config", "Monfy config'in çalışmadığını söyledi", MainEvidence), // one character differs
            Point("Config", "Monfy config'in çalışmadığını söyledi.", [E("m002", "çalışmadı")]), // same text, another quote
            Point("Oyun", "Toro oyunun yeniden başlatılıp başlatılmadığını sordu.", MainEvidence), // same source, another fact
            Point("Dizi", "Dizi konuşuldu.", [E("m005", "bence çok güzel")]),
            Point("Dizi", "Dizi konuşuldu.", [E("m005", "bence çok güzel")], "dizi"), // same text and evidence, but marked as a spoiler
        ]));

        result.ShownPoints.Should().Be(6, "sharing a source or looking alike does not make two records the same");
    }

    [Fact]
    public void When_every_point_is_also_a_plan_the_points_section_is_not_left_empty()
    {
        var result = Read(Answer(
            points: [Point("Maç planı", "Yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım")])],
            plans: [Block("Yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım")])]));

        Bullets(result.Markdown!).Should().Equal("- **Maç planı:** Yarın 21.00'de oynanacak.");
        result.Markdown.Should().NotContain("Planlar / Kararlar");
    }

    [Fact]
    public void Oversized_fields_and_too_many_quotes_are_refused()
    {
        Read(Answer(main: Block(new string('a', 600), MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(main: Block("Konu.", Enumerable.Repeat(MainEvidence[0], 6).ToArray()))).Failure.Should().Be(SummaryGroundedFailure.Limit, "more than the safety ceiling");
        Read(Answer(points: [Point(new string('k', 200), "Bilgi.", MainEvidence)])).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(points: [Point("Konu", new string('a', 600), MainEvidence)])).Failure.Should().Be(SummaryGroundedFailure.Limit);
    }

    [Fact]
    public void Support_from_context_only_records_is_refused()
    {
        var onlyContext = Answer(points: [Point("Config", "Hasom config'i sordu.", [E("m001", "değiştiren var mı?")])]);
        var withWindow = Answer(points: [Point("Config", "Hasom'un sorusuna Monfy yanıt verdi.", [E("m001", "değiştiren var mı?"), E("m002", "çalışmadı")])]);

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
    public void A_point_quoting_a_hidden_span_is_published_as_a_labelled_native_spoiler_next_to_the_open_point()
    {
        var raw = Answer(points:
        [
            Point("Vinland Saga", "Oykeli diziyi bitirdiğini ve çok güzel bulduğunu söyledi.", [E("m005", "bence çok güzel")]),
            Point("Vinland Saga", "Finalde Thorfinn'in affettiği konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "Vinland Saga finali"),
        ]);

        var result = ReadHidden(raw);

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        Bullets(result.Markdown!).Should().Equal(
            "- **Vinland Saga:** Oykeli diziyi bitirdiğini ve çok güzel bulduğunu söyledi.",
            "- **Vinland Saga:** **Spoiler (Vinland Saga finali):** ||Finalde Thorfinn'in affettiği konuşuldu.||");
        result.SpoilerClaimCount.Should().Be(1);
        Regex.Count(result.Markdown!, @"\|\|").Should().Be(2, "only the spoiler point is hidden; the open part of the same message is not");
    }

    [Fact]
    public void A_missing_or_null_spoiler_field_is_not_permission_to_publish_hidden_content_openly()
    {
        // No "s" at all (the contract's normal case for non-spoilers) — but the quote comes from a hidden span.
        var omitted = Answer(points: [Point("Dizi", "Finalde affetme sahnesi olduğu konuşuldu.", [E("m005", "<spoiler>Thorfinn sonunda affediyor</spoiler>")])]);
        omitted.Should().NotContain("\"s\"");
        ReadHidden(omitted).Markdown.Should().Contain("- **Dizi:** **Spoiler (konu belirtilmemiş):** ||Finalde affetme sahnesi olduğu konuşuldu.||");

        // An explicit null is treated the same way; a quote only partly inside the span protects the point too.
        var withNull = Point("Dizi", "Final konuşuldu.", [E("m005", "bitirdim Thorfinn sonunda")]);
        withNull["s"] = null;
        ReadHidden(Answer(points: [withNull])).Markdown.Should().Contain("**Spoiler (konu belirtilmemiş):** ||Final konuşuldu.||");

        // The same holds for a plan (the required source itself is covered by a spoiler point).
        ReadHidden(Answer(points: [withNull], plans: [Block("Finali birlikte izleyecekler.", [E("m005", "sonunda affediyor")])])).Markdown
            .Should().Contain("- **Spoiler (konu belirtilmemiş):** ||Finali birlikte izleyecekler.||");
    }

    [Fact]
    public void Hidden_content_in_an_open_text_is_refused()
    {
        // main / atmosphere relying on a hidden span.
        ReadHidden(Answer(main: Block("Dizi finali konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")]))).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        ReadHidden(Answer(atmosphere: Block("Duygusal.", [E("m005", "sonunda affediyor")]))).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // An open text repeating the hidden content verbatim, whatever its evidence.
        ReadHidden(Answer(main: Block("Thorfinn sonunda affediyor diye konuşuldu.", MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        ReadHidden(Answer(points: [Point("thorfinn sonunda affediyor", "Dizi konuşuldu.", [E("m005", "bence çok güzel")])])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // A spoiler label that gives the content away.
        ReadHidden(Answer(points: [Point("Dizi", "Final konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")], "Thorfinn sonunda affediyor")])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
    }

    [Fact]
    public void Visible_texts_are_defused_and_keep_one_line()
    {
        var raw = Answer(
            main: Block("## Başlık\n@everyone ||gizli|| <spoiler>etiket</spoiler> konuşuldu.", MainEvidence),
            points: [Point("**Konu:**", "- madde\nikinci satır", MainEvidence)]);

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
        object Topic(int i) => i % 2 == 0
            ? Point("Konu " + i, string.Join(" ", Enumerable.Repeat("gizli" + i, 60)), [E("m005", "Thorfinn sonunda affediyor")], "dizi finali")
            : Point("Konu " + i, string.Join(" ", Enumerable.Repeat("açıklama" + i, 45)), MainEvidence);
        var markdown = ReadHidden(Answer(points: Enumerable.Range(1, 6).Select(Topic).ToArray())).Markdown!;
        markdown.Length.Should().BeGreaterThan(2000);

        var parts = SummaryOutput.Split(markdown);

        parts.Should().HaveCountGreaterThan(1).And.OnlyContain(p => p.Length <= 2000 && Regex.Count(p, @"\|\|") % 2 == 0);
        parts[0].Should().StartWith("# Son Mesajların Özeti");
    }
}
