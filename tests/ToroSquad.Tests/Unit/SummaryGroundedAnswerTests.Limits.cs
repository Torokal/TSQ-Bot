using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Diagnostics of a refused answer. A <see cref="SummaryGroundedFailure.Limit"/> names the safety bound that refused it — the
/// first one in the reader's fixed order — and the size that broke it; every other outcome carries no limit reason. The
/// numbers of a refused answer are what the reader really measured: what it did not reach is unknown (null), never 0. None
/// of this changes a bound or what is refused.
/// </summary>
public sealed partial class SummaryGroundedAnswerTests
{
    private static string Words(string word, int count) => string.Join(" ", Enumerable.Repeat(word, count));

    private static void ShouldBeLimit(SummaryGroundedResult result, SummaryGroundedLimitReason reason, int value)
    {
        (result.Failure, result.Markdown).Should().Be((SummaryGroundedFailure.Limit, (string?)null));
        (result.LimitReason, result.LimitValue).Should().Be((reason, value));
    }

    [Fact]
    public void The_bounds_are_unchanged()
    {
        (SummaryGroundedAnswer.MaxCandidatePoints, SummaryGroundedAnswer.MaxSpoilers, SummaryGroundedAnswer.MaxCandidatePlans, SummaryGroundedAnswer.MaxEvidence)
            .Should().Be((8, 4, 4, 5));
        (SummaryGroundedAnswer.MaxAnswerChars, SummaryGroundedAnswer.MaxTextChars, SummaryGroundedAnswer.MaxTopicChars, SummaryGroundedAnswer.MaxQuoteChars,
            SummaryGroundedAnswer.MaxRenderedChars).Should().Be((16000, 500, 80, 300, 3900));
    }

    [Fact]
    public void An_answer_that_is_too_long_as_a_whole_names_the_answer_bound()
    {
        var raw = Answer().Replace("\"v\":4", "\"v\":4,\"pad\":\"" + new string('x', SummaryGroundedAnswer.MaxAnswerChars) + "\"", StringComparison.Ordinal);

        var result = Read(raw);

        ShouldBeLimit(result, SummaryGroundedLimitReason.AnswerChars, raw.Length);
        result.AnswerChars.Should().Be(raw.Length);
        (result.CandidatePoints, result.CandidateSpoilers, result.CandidatePlans, result.EvidenceCount, result.RenderedChars)
            .Should().Be(((int?)null, (int?)null, (int?)null, (int?)null, (int?)null), "nothing was read: the counts are unknown, not zero");
    }

    [Fact]
    public void Too_many_points_name_the_points_bound()
    {
        var result = Read(Answer(points: Points(9)));

        ShouldBeLimit(result, SummaryGroundedLimitReason.CandidatePoints, 9);
        (result.CandidatePoints, result.CandidateSpoilers, result.CandidatePlans).Should().Be((9, 0, (int?)null), "the empty spoilers list is a real zero; plans were not written");
        result.EvidenceCount.Should().BeNull("the items were never read");
        ShouldBeLimit(Read(Answer(points: Points(40))), SummaryGroundedLimitReason.CandidatePoints, 40);
    }

    [Fact]
    public void Too_many_spoiler_items_name_the_spoilers_bound()
    {
        var result = ReadMany([DiziA, FilmB, OyunC, DiziA, FilmB]);

        ShouldBeLimit(result, SummaryGroundedLimitReason.CandidateSpoilers, 5);
        (result.CandidatePoints, result.CandidateSpoilers).Should().Be((1, 5));
    }

    [Fact]
    public void Too_many_plans_name_the_plans_bound()
    {
        var result = Read(Answer(plans: Plans(5)));

        ShouldBeLimit(result, SummaryGroundedLimitReason.CandidatePlans, 5);
        (result.CandidatePoints, result.CandidateSpoilers, result.CandidatePlans).Should().Be((1, 0, 5));
    }

    [Fact]
    public void A_sixth_quote_on_one_text_names_the_evidence_bound_wherever_the_text_is()
    {
        object[] six = [E("m002", "ama çalışmadı"), E("m003", "yeniden başlattın mı?"), E("m004", "şimdi oldu"), E("m006", "oynayalım, tamam"), E("m002", "Eski config'i koydum"), E("m004", "Evet")];

        ShouldBeLimit(Read(Answer(main: Block("Konu.", six))), SummaryGroundedLimitReason.EvidencePerText, 6);
        ShouldBeLimit(Read(Answer(points: [Point("Konu", "Bilgi.", six)])), SummaryGroundedLimitReason.EvidencePerText, 6);
        ShouldBeLimit(Read(Answer(spoilers: [Point("Konu", "Gizli bilgi.", six)])), SummaryGroundedLimitReason.EvidencePerText, 6);
        ShouldBeLimit(Read(Answer(plans: [Block("Plan.", six)])), SummaryGroundedLimitReason.EvidencePerText, 6);
    }

    [Theory]
    [InlineData(501)] // one over the bound
    [InlineData(600)]
    [InlineData(1200)] // more than twice the bound: refused before the text is cleaned
    public void A_text_that_is_too_long_names_the_text_bound(int chars)
    {
        var text = new string('a', chars);

        ShouldBeLimit(Read(Answer(main: Block(text, MainEvidence))), SummaryGroundedLimitReason.TextChars, chars);
        ShouldBeLimit(Read(Answer(points: [Point("Konu", text, MainEvidence)])), SummaryGroundedLimitReason.TextChars, chars);
        ShouldBeLimit(ReadHidden(Answer(spoilers: [Point("Dizi", text, [E("m005", "Thorfinn sonunda affediyor")])])), SummaryGroundedLimitReason.TextChars, chars);
        ShouldBeLimit(Read(Answer(plans: [Block(text, MainEvidence)])), SummaryGroundedLimitReason.TextChars, chars);
        ShouldBeLimit(Read(Answer(atmosphere: Block(text, AtmosphereEvidence))), SummaryGroundedLimitReason.TextChars, chars);
    }

    [Theory]
    [InlineData(81)]
    [InlineData(200)]
    public void A_topic_that_is_too_long_names_the_topic_bound(int chars)
    {
        var topic = new string('k', chars);

        ShouldBeLimit(Read(Answer(points: [Point(topic, "Bilgi.", MainEvidence)])), SummaryGroundedLimitReason.TopicChars, chars);
        ShouldBeLimit(ReadHidden(Answer(spoilers: [Point(topic, "Gizli bilgi.", [E("m005", "Thorfinn sonunda affediyor")])])), SummaryGroundedLimitReason.TopicChars, chars);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(700)] // more than twice the bound: refused before the quote is cleaned
    public void A_quote_that_is_too_long_names_the_quote_bound_before_it_is_looked_up(int chars)
    {
        var quote = new string('q', chars);

        ShouldBeLimit(Read(Answer(main: Block("Konu.", [E("m002", quote)]))), SummaryGroundedLimitReason.QuoteChars, chars);
        ShouldBeLimit(Read(Answer(points: [Point("Konu", "Bilgi.", [E("m002", "ama çalışmadı"), E("m003", quote)])])), SummaryGroundedLimitReason.QuoteChars, chars);
    }

    [Fact]
    public void A_summary_that_is_too_long_once_rendered_names_the_rendered_bound_and_knows_every_count()
    {
        // Every field and list is inside its own bound; only the rendered whole is too long.
        var points = Enumerable.Range(1, 6).Select(i => (object)Point("Konu " + i, Words("açıklama" + i, 50), MainEvidence)).ToArray();
        var plans = Enumerable.Range(1, 2).Select(i => (object)Block(Words("kararlar" + i, 50), [E("m006", "oynayalım, tamam")])).ToArray();

        var result = Read(Answer(points: points, plans: plans));

        result.Failure.Should().Be(SummaryGroundedFailure.Limit);
        result.LimitReason.Should().Be(SummaryGroundedLimitReason.RenderedChars);
        result.RenderedChars.Should().BeGreaterThan(SummaryGroundedAnswer.MaxRenderedChars).And.Be(result.LimitValue);
        (result.CandidatePoints, result.CandidateSpoilers, result.CandidatePlans, result.EvidenceCount).Should().Be((6, 0, 2, 10), "the whole answer was read before it was rendered");
        (result.ShownPoints, result.ShownPlans, result.SpoilerClaimCount, result.Markdown).Should().Be((0, 0, 0, (string?)null), "nothing is published");
    }

    [Fact]
    public void The_reason_is_the_first_bound_in_the_reading_order()
    {
        var both = Read(Answer(points: Points(9), plans: Plans(5)));
        var mainFirst = Read(Answer(main: Block(new string('a', 600), MainEvidence), points: Points(9)));
        var topicBeforeText = Read(Answer(points: [Point(new string('k', 100), new string('a', 600), MainEvidence)]));
        var textBeforeQuotes = Read(Answer(points: [Point("Konu", new string('a', 600), [E("m002", new string('q', 400))])]));

        ShouldBeLimit(both, SummaryGroundedLimitReason.CandidatePoints, 9);
        ShouldBeLimit(mainFirst, SummaryGroundedLimitReason.TextChars, 600);
        (mainFirst.CandidatePoints, mainFirst.EvidenceCount).Should().Be((9, (int?)null), "the list size is known although its bound was not the first one met");
        ShouldBeLimit(topicBeforeText, SummaryGroundedLimitReason.TopicChars, 100);
        ShouldBeLimit(textBeforeQuotes, SummaryGroundedLimitReason.TextChars, 600);
    }

    [Fact]
    public void A_valid_answer_has_no_limit_reason_and_real_numbers()
    {
        var raw = Answer(plans: Plans(1));

        var result = Read(raw);

        (result.Failure, result.LimitReason, result.LimitValue).Should().Be((SummaryGroundedFailure.None, SummaryGroundedLimitReason.None, (int?)null));
        (result.AnswerChars, result.RenderedChars).Should().Be((raw.Length, result.Markdown!.Length));
        (result.CandidatePoints, result.CandidateSpoilers, result.CandidatePlans, result.EvidenceCount).Should().Be((1, 0, 1, 6));
        Read(Answer()).CandidatePlans.Should().Be(0, "an answer that was read to the end without a plans list has no plans");
    }

    [Fact]
    public void Other_refusals_carry_no_limit_reason()
    {
        var contract = Read(Answer(version: 3));
        var quoteNotFound = Read(Answer(points: [Point("Konu", "Bilgi.", [E("m004", "Hayır, hâlâ olmadı.")])]));
        var missingSpoiler = ReadHidden(Answer());
        var truncated = Read(Answer(), finish: "length");
        var notJson = Read("Özet: konuşuldu.");
        var unknownSource = Read(Answer(main: Block("Konu.", [E("m099", "şimdi oldu")])));

        (contract.Failure, quoteNotFound.Failure, missingSpoiler.Failure, truncated.Failure, notJson.Failure, unknownSource.Failure).Should().Be(
            (SummaryGroundedFailure.Contract, SummaryGroundedFailure.QuoteNotFound, SummaryGroundedFailure.MissingRequiredSpoiler,
                SummaryGroundedFailure.Truncated, SummaryGroundedFailure.NotJson, SummaryGroundedFailure.UnknownSource));
        new[] { contract, quoteNotFound, missingSpoiler, truncated, notJson, unknownSource }
            .Should().OnlyContain(r => r.LimitReason == SummaryGroundedLimitReason.None && r.LimitValue == null && r.Markdown == null);
    }

    [Fact]
    public void A_refused_answer_reports_what_was_measured_and_leaves_the_rest_unknown()
    {
        var truncated = Read(Answer(), finish: "length");
        var notJson = Read("Özet: konuşuldu.");
        var early = Read(Answer(main: Block("Konu.", [E("m004", "Hayır, hâlâ olmadı.")]))); // refused at the first text
        var late = ReadHidden(Answer(plans: Plans(1))); // read to the end, then refused for the missing spoiler

        (truncated.AnswerChars, truncated.CandidatePoints, truncated.EvidenceCount).Should().Be((Answer().Length, (int?)null, (int?)null), "a cut-off answer is not opened");
        (notJson.AnswerChars, notJson.CandidatePoints, notJson.CandidateSpoilers, notJson.CandidatePlans).Should().Be(("Özet: konuşuldu.".Length, (int?)null, (int?)null, (int?)null));
        (early.CandidatePoints, early.CandidateSpoilers, early.CandidatePlans, early.EvidenceCount, early.RenderedChars)
            .Should().Be((1, 0, (int?)null, (int?)null, (int?)null), "a partial quote count would look like a total");
        (late.Failure, late.CandidatePoints, late.CandidateSpoilers, late.CandidatePlans, late.EvidenceCount, late.RenderedChars)
            .Should().Be((SummaryGroundedFailure.MissingRequiredSpoiler, 1, 0, 1, 6, (int?)null));
        SummaryGroundedAnswer.Read(null, "stop", Input).AnswerChars.Should().BeNull("no answer text at all");
    }
}
