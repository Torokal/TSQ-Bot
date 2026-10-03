using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The grounded answer's checks, selection and rendering, on contract v4: open information in "main", "points", "plans"
/// and "atmosphere"; hidden information only in the separate "spoilers" list (topic shown, text rendered as a native
/// spoiler). Source safety is strict and covers every item: an unknown source, a quote that is not in its record, a broken
/// or cut-off JSON, an earlier contract, oversized fields, context-only support, a hidden quote or hidden content in an
/// open text, or a technical reference refuse the whole answer — also when the faulty item would not have been shown.
/// Every window record with a hidden part must be quoted from that part inside "spoilers". The display target is
/// separate: a few more points or plans than are shown is not a failure. These checks prove that sources exist, quotes are
/// intact and hidden quotes stay hidden — not that the model understood them or that a spoiler text says what happened.
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

    /// <summary>A block {"t", "e"}: main, atmosphere or a plan.</summary>
    private static Dictionary<string, object?> Block(string text, object[]? evidence = null)
    {
        var block = new Dictionary<string, object?> { ["t"] = text };
        if (evidence is not null)
            block["e"] = evidence;
        return block;
    }

    /// <summary>A point or a spoiler item: the block's fields plus its own "topic".</summary>
    private static Dictionary<string, object?> Point(string topic, string text, object[]? evidence = null)
    {
        var point = Block(text, evidence);
        point["topic"] = topic;
        return point;
    }

    private static readonly object[] MainEvidence = [E("m002", "ama çalışmadı")];
    private static readonly object[] AtmosphereEvidence = [E("m003", "yeniden başlattın mı?")];
    private static readonly object[] Correction = [E("m002", "ama çalışmadı"), E("m003", "yeniden başlattın mı?"), E("m004", "şimdi oldu")];
    private const string CorrectionText = "Monfy eski config'in önce çalışmadığını, oyunu yeniden başlatınca düzeldiğini söyledi.";

    private static string Answer(object? main = null, object[]? points = null, object[]? spoilers = null, object[]? plans = null, object? atmosphere = null, int version = 4)
    {
        var answer = new Dictionary<string, object?>
        {
            ["v"] = version,
            ["main"] = main ?? Block("Oyun ayarı sorunu ve dizi sohbeti konuşuldu.", MainEvidence),
            ["points"] = points ?? [Point("Oyun ayarları", CorrectionText, Correction)],
            ["spoilers"] = spoilers ?? [],
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

    /// <summary>The spoiler item that covers <see cref="Hidden"/>'s one required source.</summary>
    private static readonly object Vinland = Point("Vinland Saga finali", "Finalde Thorfinn'in affettiği konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")]);

    private static readonly object[] OpenEvidence = [E("m006", "oynayalım, tamam")];

    /// <summary>An answer for <see cref="Many"/>: main, the one point and atmosphere rest on the one open record.</summary>
    private static SummaryGroundedResult ReadMany(object[] spoilers, object[]? points = null) => Read(
        Answer(main: Block("Dizi, film ve oyun finalleri konuşuldu.", OpenEvidence), points: points ?? [Point("Maç", "Yarın oynanması konuşuldu.", OpenEvidence)],
            spoilers: spoilers, atmosphere: Block("Heyecanlı bir sohbet.", [E("m006", "Yarın 21.00'de")])),
        input: Many);

    private static object Spoiler(string topic, string text, params (string Ref, string Quote)[] quotes) =>
        Point(topic, text, quotes.Select(q => E(q.Ref, q.Quote)).ToArray());

    private static readonly object DiziA = Spoiler("Dizi A finali", "Kahramanın ihanet ettiği ve geminin battığı konuşuldu.", ("m002", "kahraman ihanet ediyor"), ("m003", "gemi son bölümde batıyor"));
    private static readonly object FilmB = Spoiler("Film B", "Katilin uşak çıktığı söylendi.", ("m004", "katil aslında uşak çıkıyor"));
    private static readonly object OyunC = Spoiler("Oyun C", "Son boss'un kahramanın kardeşi olduğu söylendi.", ("m005", "son boss kahramanın kardeşi"));

    private static List<string> Bullets(string markdown) => Regex.Matches(markdown, "^- .+$", RegexOptions.Multiline).Select(m => m.Value).ToList();

    private static List<string> Headings(string markdown) => Regex.Matches(markdown, "^#{1,2} .+$", RegexOptions.Multiline).Select(m => m.Value).ToList();

    private static readonly string[] FirstSix = ["- **Konu 1:** Bilgi 1.", "- **Konu 2:** Bilgi 2.", "- **Konu 3:** Bilgi 3.", "- **Konu 4:** Bilgi 4.", "- **Konu 5:** Bilgi 5.", "- **Konu 6:** Bilgi 6."];

    // ----------------------------------------------------------------------------------------------------- shape and rendering

    [Fact]
    public void A_valid_answer_without_spoilers_is_rendered_as_the_usual_markdown_without_any_technical_field()
    {
        var result = Read(Answer(plans: [Block("Hasom'un önerisiyle yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım, tamam.")])]));

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        result.Markdown.Should().Be(
            "# Son Mesajların Özeti\n\n" +
            "## Ana konu\nOyun ayarı sorunu ve dizi sohbeti konuşuldu.\n\n" +
            "## Önemli noktalar\n- **Oyun ayarları:** Monfy eski config'in önce çalışmadığını, oyunu yeniden başlatınca düzeldiğini söyledi.\n\n" +
            "## Planlar / Kararlar\n- Hasom'un önerisiyle yarın 21.00'de oynanacak.\n\n" +
            "## Genel atmosfer\nYardımlaşmalı, sakin bir sohbet.");
        result.Markdown.Should().NotContain("m00").And.NotContain("{").And.NotContain("[").And.NotContain("\"").And.NotContain("Spoilerlar").And.NotContain("||");
        result.EvidenceCount.Should().Be(6, "one for main, three for the correction that needs them, one plan, one atmosphere");
        (result.CandidatePoints, result.CandidatePlans, result.ShownPoints, result.ShownPlans, result.SpoilerClaimCount, result.RequiredSpoilers).Should().Be((1, 1, 1, 1, 0, 0));
        Input.RequiredSpoilerSources.Should().BeEmpty();
    }

    [Fact]
    public void Spoilers_get_their_own_section_with_the_topic_outside_and_the_whole_text_inside_a_native_spoiler()
    {
        var result = ReadHidden(Answer(
            points: [Point("Vinland Saga", "Oykeli diziyi bitirdiğini ve çok güzel bulduğunu söyledi.", [E("m005", "bence çok güzel")])],
            spoilers: [Vinland],
            plans: [Block("Yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım")])]));

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        result.Markdown.Should().Be(
            "# Son Mesajların Özeti\n\n" +
            "## Ana konu\nOyun ayarı sorunu ve dizi sohbeti konuşuldu.\n\n" +
            "## Önemli noktalar\n- **Vinland Saga:** Oykeli diziyi bitirdiğini ve çok güzel bulduğunu söyledi.\n\n" +
            "## Spoilerlar\n- **Vinland Saga finali:** ||Finalde Thorfinn'in affettiği konuşuldu.||\n\n" +
            "## Planlar / Kararlar\n- Yarın 21.00'de oynanacak.\n\n" +
            "## Genel atmosfer\nYardımlaşmalı, sakin bir sohbet.");
        Regex.Count(result.Markdown!, @"\|\|").Should().Be(2, "only the spoiler text is hidden; the open part of the same message is a normal point");
        (result.SpoilerClaimCount, result.RequiredSpoilers).Should().Be((1, 1));
        SummaryGroundedAnswer.SpoilersHeading.Should().Be("## Spoilerlar");
    }

    [Fact]
    public void Without_plans_or_spoilers_those_sections_are_left_out()
    {
        Headings(Read(Answer()).Markdown!).Should().Equal("# Son Mesajların Özeti", "## Ana konu", "## Önemli noktalar", "## Genel atmosfer");
        Read(Answer(plans: [])).Failure.Should().Be(SummaryGroundedFailure.None, "an empty plan list is accepted too");
    }

    [Fact]
    public void An_answer_in_an_earlier_contract_is_not_mistaken_for_the_current_one_and_never_converted()
    {
        var v1 = JsonSerializer.Serialize(new
        {
            version = 1,
            main = new { text = "Konu.", evidence = new[] { new { message = "m002", quote = "ama çalışmadı" } } },
            points = new[] { new { topic = "Konu", claims = new[] { new { text = "Bilgi.", evidence = new[] { new { message = "m002", quote = "ama çalışmadı" } }, spoiler_topic = (string?)null } } } },
            plans = Array.Empty<object>(),
            atmosphere = new { text = "Sakin.", evidence = new[] { new { message = "m003", quote = "yeniden başlattın mı?" } } },
        });
        // v2: claims nested under each point. v3: flat points, spoilers as points with an "s" label, no "spoilers" list.
        object[] nested = [new { topic = "Oyun ayarları", claims = new[] { Block(CorrectionText, Correction) } }];
        var labelled = Point("Dizi", "Final konuşuldu.", [E("m005", "bence çok güzel")]);
        labelled["s"] = "dizi finali";
        var v3 = Answer(version: 3).Replace("\"spoilers\":[],", "", StringComparison.Ordinal);

        Read(v1).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(points: nested, version: 2)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(points: nested)).Failure.Should().Be(SummaryGroundedFailure.Contract, "nested claims under the new version number are not the contract");
        v3.Should().NotContain("spoilers");
        Read(v3).Failure.Should().Be(SummaryGroundedFailure.Contract, "a v3 answer is refused by its version");
        Read(v3.Replace("\"v\":3", "\"v\":4", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract, "the spoilers list is always there, even when empty");
        Read(Answer(points: [labelled])).Failure.Should().Be(SummaryGroundedFailure.Contract, "an \"s\" label belongs to the earlier contract: not converted silently");
        Read(Answer(version: 3)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer().Replace("\"v\":4", "\"version\":4", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: new { text = "Konu.", evidence = MainEvidence })).Failure.Should().Be(SummaryGroundedFailure.Contract);
        SummaryGroundedPrompt.ContractVersion.Should().Be(4);
    }

    [Fact]
    public void A_field_outside_the_contract_is_not_read_as_content()
    {
        // What a model once did: the spoiler written under a field of its own invention. It is not content, so nothing covers m005.
        var invented = Answer().Replace("\"spoilers\":[]", "\"spoilers\":[],\"points_spoiler\":[" + JsonSerializer.Serialize(Vinland) + "]", StringComparison.Ordinal);

        ReadHidden(invented).Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler);
        Read(invented).Failure.Should().Be(SummaryGroundedFailure.None, "without a required source the unknown field is simply ignored");
    }

    [Fact]
    public void Missing_fields_and_wrong_shapes_break_the_contract()
    {
        Read(Answer().Replace("\"atmosphere\"", "\"mood\"", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer().Replace("\"spoilers\":[]", "\"spoilers\":null", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer().Replace("\"spoilers\":[]", "\"spoilers\":{}", StringComparison.Ordinal)).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu."))).Failure.Should().Be(SummaryGroundedFailure.Contract, "main keeps its evidence: no unchecked text");
        Read(Answer(atmosphere: Block("Sakin."))).Failure.Should().Be(SummaryGroundedFailure.Contract, "atmosphere keeps its evidence too");
        Read(Answer(main: Block("Konu.", []))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("   ", MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(points: [])).Failure.Should().Be(SummaryGroundedFailure.Contract, "at least one point");
        Read(Answer(points: ["metin"])).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(points: [Block("Konusu olmayan bilgi.", MainEvidence)])).Failure.Should().Be(SummaryGroundedFailure.Contract, "a point carries its topic");
        Read(Answer(points: [Point("Konu", "Dayanaksız bilgi.")])).Failure.Should().Be(SummaryGroundedFailure.Contract, "a point carries its evidence");
        ReadHidden(Answer(spoilers: [Block("Konusu olmayan gizli bilgi.", [E("m005", "Thorfinn sonunda affediyor")])])).Failure.Should().Be(SummaryGroundedFailure.Contract, "a spoiler item carries its topic");
        ReadHidden(Answer(spoilers: [Point("Dizi", "Dayanaksız gizli bilgi.")])).Failure.Should().Be(SummaryGroundedFailure.Contract);
        ReadHidden(Answer(spoilers: ["metin"])).Failure.Should().Be(SummaryGroundedFailure.Contract);
        // Evidence must be [reference, quote] pairs — nothing positional beyond that, no objects.
        Read(Answer(main: Block("Konu.", [new[] { "m002" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", [new[] { "m002", "ama çalışmadı", "fazla" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", [new { message = "m002", quote = "ama çalışmadı" }]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
        Read(Answer(main: Block("Konu.", ["m002"]))).Failure.Should().Be(SummaryGroundedFailure.Contract);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Özet hazır değil.")]
    [InlineData("{\"v\":4,\"main\":{\"t\":\"Konu.\"")] // cut off
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

    // ----------------------------------------------------------------------------------------------------- sources and quotes

    [Fact]
    public void An_unknown_source_refuses_the_answer()
    {
        Read(Answer(main: Block("Konu.", [E("m099", "Evet, şimdi oldu.")]))).Failure.Should().Be(SummaryGroundedFailure.UnknownSource);
        Read(Answer(main: Block("Konu.", [E("41", "Eski config'i koydum")]))).Failure.Should().Be(SummaryGroundedFailure.UnknownSource, "a Discord id is not a reference");
        ReadHidden(Answer(spoilers: [Point("Dizi", "Final konuşuldu.", [E("m099", "Thorfinn sonunda affediyor")])])).Failure.Should().Be(SummaryGroundedFailure.UnknownSource);
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
        Read(Answer(spoilers: [Point("Konu", "Gizli bilgi.", [E(message, quote)])])).Failure.Should().Be(SummaryGroundedFailure.QuoteNotFound, "a spoiler item's quotes are checked the same way");
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
    [InlineData(4, SummaryGroundedFailure.None)]
    [InlineData(5, SummaryGroundedFailure.None)]
    [InlineData(6, SummaryGroundedFailure.Limit)]
    public void Up_to_five_checked_quotes_per_text_are_accepted_as_a_safety_ceiling_not_a_target(int quotes, SummaryGroundedFailure expected)
    {
        object[] all = [E("m002", "ama çalışmadı"), E("m003", "yeniden başlattın mı?"), E("m004", "şimdi oldu"), E("m006", "oynayalım, tamam"), E("m002", "Eski config'i koydum"), E("m004", "Evet")];
        var evidence = all.Take(quotes).ToArray();

        var onPoint = Read(Answer(points: [Point("Konu", "Bilgi.", evidence)]));
        var onMain = Read(Answer(main: Block("Konu.", evidence)));
        var onSpoiler = Read(Answer(spoilers: [Point("Konu", "Gizli bilgi.", evidence)]));

        (onPoint.Failure, onMain.Failure, onSpoiler.Failure).Should().Be((expected, expected, expected));
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
        ReadHidden(Answer(spoilers: [Point("Dizi", "m005 mesajındaki final konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")])])).Failure.Should().Be(SummaryGroundedFailure.TechnicalLeak, "a hidden text is visible once opened");
    }

    // ----------------------------------------------------------------------------------------------------- open and hidden

    [Fact]
    public void Open_texts_may_not_quote_a_hidden_span()
    {
        object[] hidden = [E("m005", "Thorfinn sonunda affediyor")];

        ReadHidden(Answer(main: Block("Dizi finali konuşuldu.", hidden), spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        ReadHidden(Answer(atmosphere: Block("Duygusal.", [E("m005", "sonunda affediyor")]), spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        ReadHidden(Answer(points: [Point("Dizi", "Finalde bir olay olduğu konuşuldu.", hidden)], spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText, "hidden evidence is valid only inside spoilers");
        ReadHidden(Answer(plans: [Block("Finali birlikte izleyecekler.", hidden)], spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // A quote only partly inside the span counts as hidden too.
        ReadHidden(Answer(points: [Point("Dizi", "Dizi bitmiş.", [E("m005", "bitirdim Thorfinn sonunda")])], spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
    }

    [Fact]
    public void Hidden_content_repeated_in_an_open_text_or_in_a_spoiler_topic_is_refused()
    {
        ReadHidden(Answer(main: Block("Thorfinn sonunda affediyor diye konuşuldu.", MainEvidence), spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        ReadHidden(Answer(points: [Point("thorfinn sonunda affediyor", "Dizi konuşuldu.", [E("m005", "bence çok güzel")])], spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        ReadHidden(Answer(plans: [Block("Thorfinn sonunda affediyor sahnesini izleyecekler.", [E("m006", "oynayalım, tamam")])], spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // The topic of a spoiler item is shown openly: it may not give the content away.
        ReadHidden(Answer(spoilers: [Point("Thorfinn sonunda affediyor", "Final konuşuldu.", [E("m005", "Thorfinn sonunda affediyor")])])).Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
        // Inside the hidden text itself the content is exactly where it belongs.
        ReadHidden(Answer(spoilers: [Point("Vinland Saga finali", "Thorfinn sonunda affediyor.", [E("m005", "Thorfinn sonunda affediyor")])])).Failure.Should().Be(SummaryGroundedFailure.None);
    }

    [Fact]
    public void The_open_part_of_a_message_may_support_a_point_and_its_hidden_part_a_spoiler_item()
    {
        var result = ReadHidden(Answer(points: [Point("Vinland Saga", "Oykeli diziyi çok güzel bulduğunu söyledi.", [E("m005", "bence çok güzel")])], spoilers: [Vinland]));

        result.Failure.Should().Be(SummaryGroundedFailure.None, "the same source on both sides, with different spans");
        Bullets(result.Markdown!).Should().Equal(
            "- **Vinland Saga:** Oykeli diziyi çok güzel bulduğunu söyledi.",
            "- **Vinland Saga finali:** ||Finalde Thorfinn'in affettiği konuşuldu.||");
    }

    // ----------------------------------------------------------------------------------------------------- required coverage

    [Fact]
    public void Required_spoiler_sources_come_from_the_record_map_and_never_include_context()
    {
        Hidden.RequiredSpoilerSources.Should().Equal("m005");
        Many.RequiredSpoilerSources.Should().Equal("m002", "m003", "m004", "m005");
        (Many.Records["m001"].HasSpoiler, Many.Records["m001"].ContextOnly).Should().Be((true, true), "a context record with a hidden part is told apart, and is not required");
        Many.Records["m006"].HasSpoiler.Should().BeFalse();
    }

    [Fact]
    public void With_a_required_source_an_empty_spoilers_list_refuses_the_answer()
    {
        var result = ReadHidden(Answer());

        (result.Failure, result.Markdown).Should().Be((SummaryGroundedFailure.MissingRequiredSpoiler, (string?)null));
        Read(Answer()).Failure.Should().Be(SummaryGroundedFailure.None, "without a hidden part an empty list is the normal case");
    }

    [Fact]
    public void Only_a_quote_from_the_hidden_part_inside_spoilers_covers_a_source()
    {
        // A spoiler item quoting only the OPEN part of the required record; the word "spoiler" changes nothing.
        ReadHidden(Answer(spoilers: [Point("Vinland Saga finali", "Dizi hakkında spoiler paylaşıldı.", [E("m005", "bence çok güzel")])]))
            .Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler);
        // The same source used by a normal point (open part) does not cover it either.
        ReadHidden(Answer(points: [Point("Spoiler", "Spoiler konuşuldu.", [E("m005", "Vinland Saga'yı bitirdim")])]))
            .Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler);
        // One verified hidden quote does.
        ReadHidden(Answer(spoilers: [Vinland])).Failure.Should().Be(SummaryGroundedFailure.None);
    }

    [Fact]
    public void One_spoiler_item_may_cover_one_source_or_several_of_the_same_production()
    {
        var all = Spoiler("Finaller", "Kahramanın ihaneti, batan gemi ve uşak katil konuşuldu.", ("m002", "kahraman ihanet ediyor"), ("m003", "gemi son bölümde batıyor"), ("m004", "katil aslında uşak"));

        var separate = ReadMany([DiziA, FilmB, OyunC]);
        var three = ReadMany([all, OyunC]);

        separate.Failure.Should().Be(SummaryGroundedFailure.None);
        Bullets(separate.Markdown!).Should().Equal(
            "- **Maç:** Yarın oynanması konuşuldu.",
            "- **Dizi A finali:** ||Kahramanın ihanet ettiği ve geminin battığı konuşuldu.||",
            "- **Film B:** ||Katilin uşak çıktığı söylendi.||",
            "- **Oyun C:** ||Son boss'un kahramanın kardeşi olduğu söylendi.||");
        (separate.RequiredSpoilers, separate.SpoilerClaimCount).Should().Be((4, 3));
        (three.Failure, three.SpoilerClaimCount).Should().Be((SummaryGroundedFailure.None, 2), "one item with three hidden quotes covers three sources");
    }

    [Fact]
    public void One_uncovered_source_among_several_refuses_the_answer()
    {
        ReadMany([DiziA, FilmB]).Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler, "m005 is not quoted from its hidden part");
        ReadMany([DiziA, FilmB, Spoiler("Oyun C", "Oyun C finali konuşuldu.", ("m005", "inanamadım"))]).Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler);
    }

    [Fact]
    public void A_context_only_record_with_a_hidden_part_is_neither_required_nor_coverage()
    {
        // Quoting the context record's hidden part (together with a window record) is allowed inside spoilers, but covers nothing.
        var old = Point("Eski sezon", "Eski sezondaki bir olay hatırlatıldı.", [E("m001", "baba karakteri ölüyor"), E("m006", "tamam")]);

        var complete = ReadMany([DiziA, FilmB, OyunC, old]);
        var missing = ReadMany([DiziA, FilmB, old]);

        complete.Failure.Should().Be(SummaryGroundedFailure.None, "m001 is context: nobody has to cover it");
        complete.Markdown.Should().Contain("- **Eski sezon:** ||Eski sezondaki bir olay hatırlatıldı.||");
        (complete.RequiredSpoilers, complete.SpoilerClaimCount).Should().Be((4, 4));
        missing.Failure.Should().Be(SummaryGroundedFailure.MissingRequiredSpoiler, "the context quote does not stand in for m005");
        // The context record's hidden part in an OPEN text is still a leak.
        ReadMany([DiziA, FilmB, OyunC], points: [Point("Eski sezon", "Eski sezon hatırlatıldı.", [E("m001", "baba karakteri ölüyor"), E("m006", "tamam")])])
            .Failure.Should().Be(SummaryGroundedFailure.SpoilerInOpenText);
    }

    [Fact]
    public void Coverage_does_not_prove_that_the_text_summarises_the_hidden_event()
    {
        // What the reader can check is where the quote comes from — not whether the text says what happened.
        var result = ReadHidden(Answer(spoilers: [Point("Vinland Saga finali", "Final hakkında spoiler paylaşıldı.", [E("m005", "Thorfinn sonunda affediyor")])]));

        result.Failure.Should().Be(SummaryGroundedFailure.None, "an empty sentence with the right quote passes the structural check");
        result.Markdown.Should().Contain("- **Vinland Saga finali:** ||Final hakkında spoiler paylaşıldı.||", "hidden and shown, but exactly as empty as the model wrote it");
    }

    // ----------------------------------------------------------------------------------------------------- limits and selection

    [Fact]
    public void Up_to_the_display_target_everything_is_shown()
    {
        var result = Read(Answer(points: Points(6), plans: Plans(2)));

        result.Failure.Should().Be(SummaryGroundedFailure.None);
        Bullets(result.Markdown!).Should().Equal(FirstSix.Concat(["- Plan 1.", "- Plan 2."]));
        (result.CandidatePoints, result.CandidatePlans, result.ShownPoints, result.ShownPlans).Should().Be((6, 2, 6, 2));
        (SummaryGroundedAnswer.ShownPoints, SummaryGroundedAnswer.ShownPlans).Should().Be((6, 2));
    }

    [Theory]
    [InlineData(7, 2)]
    [InlineData(8, 4)] // the safety bound itself
    public void A_few_more_valid_points_or_plans_than_are_shown_are_selected_in_the_models_order_not_refused(int points, int plans)
    {
        var result = Read(Answer(points: Points(points), plans: Plans(plans)));

        result.Failure.Should().Be(SummaryGroundedFailure.None, "the display target is not a safety rule");
        Bullets(result.Markdown!).Should().Equal(FirstSix.Concat(["- Plan 1.", "- Plan 2."]));
        result.Markdown.Should().NotContain("Konu 7").And.NotContain("Plan 3");
        (result.CandidatePoints, result.CandidatePlans, result.ShownPoints, result.ShownPlans).Should().Be((points, plans, 6, 2));
        result.EvidenceCount.Should().Be(2 + points + plans, "every candidate was checked, shown or not");
    }

    [Fact]
    public void Spoilers_and_points_do_not_compete_for_places()
    {
        var result = ReadMany([DiziA, FilmB, OyunC], points: Enumerable.Range(1, 8).Select(i => (object)Point("Konu " + i, "Bilgi " + i + ".", OpenEvidence)).ToArray());

        var bullets = Bullets(result.Markdown!);
        bullets.Take(6).Should().Equal(FirstSix, "the points keep their six places");
        bullets.Skip(6).Should().HaveCount(3).And.OnlyContain(b => b.EndsWith("||", StringComparison.Ordinal), "and every spoiler item is shown as well");
        Headings(result.Markdown!).Should().Equal("# Son Mesajların Özeti", "## Ana konu", "## Önemli noktalar", "## Spoilerlar", "## Genel atmosfer");
        (result.ShownPoints, result.SpoilerClaimCount).Should().Be((6, 3));
    }

    [Fact]
    public void More_items_than_the_safety_bound_are_refused()
    {
        Read(Answer(points: Points(9))).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(points: Points(40))).Failure.Should().Be(SummaryGroundedFailure.Limit, "no unbounded list");
        Read(Answer(plans: Plans(5))).Failure.Should().Be(SummaryGroundedFailure.Limit);
        ReadMany([DiziA, FilmB, OyunC, DiziA, FilmB]).Failure.Should().Be(SummaryGroundedFailure.Limit, "at most four spoiler items");
        Read(Answer() + new string(' ', 10) + "\n").Failure.Should().Be(SummaryGroundedFailure.None);
        Read(Answer(main: Block("Konu.", MainEvidence)).Replace("\"v\":4", "\"v\":4,\"pad\":\"" + new string('x', SummaryGroundedAnswer.MaxAnswerChars) + "\"", StringComparison.Ordinal))
            .Failure.Should().Be(SummaryGroundedFailure.Limit, "the whole answer is bounded too");
        (SummaryGroundedAnswer.MaxCandidatePoints, SummaryGroundedAnswer.MaxSpoilers, SummaryGroundedAnswer.MaxCandidatePlans, SummaryGroundedAnswer.MaxEvidence).Should().Be((8, 4, 4, 5));
    }

    [Fact]
    public void Oversized_fields_are_refused()
    {
        Read(Answer(main: Block(new string('a', 600), MainEvidence))).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(points: [Point(new string('k', 200), "Bilgi.", MainEvidence)])).Failure.Should().Be(SummaryGroundedFailure.Limit);
        Read(Answer(points: [Point("Konu", new string('a', 600), MainEvidence)])).Failure.Should().Be(SummaryGroundedFailure.Limit);
        ReadHidden(Answer(spoilers: [Point("Dizi", new string('a', 600), [E("m005", "Thorfinn sonunda affediyor")])])).Failure.Should().Be(SummaryGroundedFailure.Limit);
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
    public void Exact_copies_are_shown_once_and_a_point_that_is_exactly_a_plan_gives_way_to_the_plan()
    {
        var plan = Block("Yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım")]);
        var result = ReadHidden(Answer(
            points:
            [
                Point("Oyun ayarları", CorrectionText, Correction),
                Point("Oyun ayarları", CorrectionText, Correction.AsEnumerable().Reverse().ToArray()), // the same record, evidence in another order
                Point("Maç planı", "Yarın 21.00'de oynanacak.", [E("m006", "Yarın 21.00'de oynayalım")]), // exactly the plan below
            ],
            spoilers: [Vinland, Vinland],
            plans: [plan, plan]));

        Bullets(result.Markdown!).Should().Equal("- **Oyun ayarları:** " + CorrectionText, "- **Vinland Saga finali:** ||Finalde Thorfinn'in affettiği konuşuldu.||", "- Yarın 21.00'de oynanacak.");
        (result.CandidatePoints, result.CandidatePlans, result.ShownPoints, result.ShownPlans, result.SpoilerClaimCount).Should().Be((3, 2, 1, 1, 1));
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
        ]));

        result.ShownPoints.Should().Be(4, "sharing a source or looking alike does not make two records the same");
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
    public void Visible_texts_are_defused_and_keep_one_line()
    {
        var raw = Answer(
            main: Block("## Başlık\n@everyone ||gizli|| <spoiler>etiket</spoiler> konuşuldu.", MainEvidence),
            points: [Point("**Konu:**", "- madde\nikinci satır", MainEvidence)],
            spoilers: [Point("**Dizi:**", "||iç içe|| <spoiler>işaret</spoiler>\nikinci satır", MainEvidence)]);

        var markdown = Read(raw).Markdown!;

        markdown.Should().Contain("## Ana konu\nBaşlık @" + (char)0x200B + "everyone gizli etiket konuşuldu.");
        markdown.Should().Contain("- **Konu:** madde ikinci satır");
        markdown.Should().Contain("- **Dizi:** ||iç içe işaret ikinci satır||", "marks the model wrote inside a spoiler text cannot open or close the spoiler");
        Headings(markdown).Should().Equal("# Son Mesajların Özeti", "## Ana konu", "## Önemli noktalar", "## Spoilerlar", "## Genel atmosfer");
        Regex.Count(markdown, @"\|\|").Should().Be(2);
        markdown.Should().NotMatchRegex("@(everyone|here)");
    }

    [Fact]
    public void A_long_grounded_summary_is_split_without_breaking_a_spoiler()
    {
        object Long(int i) => Point("Konu " + i, string.Join(" ", Enumerable.Repeat("açıklama" + i, 45)), OpenEvidence);
        object LongSpoiler(string topic, string refId, string quote) => Point(topic, string.Join(" ", Enumerable.Repeat("gizli", 80)), [E(refId, quote)]);

        var markdown = ReadMany(
            [LongSpoiler("Dizi A", "m002", "kahraman ihanet ediyor"), LongSpoiler("Dizi A devamı", "m003", "gemi son bölümde batıyor"), LongSpoiler("Film B", "m004", "katil aslında uşak"), LongSpoiler("Oyun C", "m005", "son boss")],
            points: Enumerable.Range(1, 3).Select(Long).ToArray()).Markdown!;
        markdown.Length.Should().BeGreaterThan(2000);

        var parts = SummaryOutput.Split(markdown);

        parts.Should().HaveCountGreaterThan(1).And.OnlyContain(p => p.Length <= 2000 && Regex.Count(p, @"\|\|") % 2 == 0);
        parts[0].Should().StartWith("# Son Mesajların Özeti");
    }
}
