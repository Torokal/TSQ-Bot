using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Diagnostics of an answer that is not one JSON object. A <see cref="SummaryGroundedFailure.NotJson"/> says why: the
/// envelope (text around the object, or no closed object), a code fence in a shape that is not the supported one, an object
/// that does not parse, or valid JSON of another kind. Every other outcome carries no such reason. Nothing is recovered:
/// no object is searched for inside the text, no text is cut away, no JSON is repaired, and the one tolerated fence is the
/// same as before.
/// </summary>
public sealed partial class SummaryGroundedAnswerTests
{
    private static void ShouldBeNotJson(SummaryGroundedResult result, SummaryGroundedNotJsonReason reason)
    {
        (result.Failure, result.Markdown).Should().Be((SummaryGroundedFailure.NotJson, (string?)null));
        result.NotJsonReason.Should().Be(reason);
        (result.LimitReason, result.LimitValue).Should().Be((SummaryGroundedLimitReason.None, (int?)null));
        (result.CandidatePoints, result.CandidateSpoilers, result.CandidatePlans, result.EvidenceCount, result.RenderedChars)
            .Should().Be(((int?)null, (int?)null, (int?)null, (int?)null, (int?)null), "an answer that was never opened has unknown counts");
    }

    [Fact]
    public void A_normal_answer_has_no_not_json_reason()
    {
        var result = Read(Answer());

        (result.Failure, result.NotJsonReason).Should().Be((SummaryGroundedFailure.None, SummaryGroundedNotJsonReason.None));
    }

    [Fact]
    public void The_one_supported_enclosing_code_fence_is_still_accepted()
    {
        var fenced = Read("```json\n" + Answer() + "\n```");
        var bare = Read("  \n```\n" + Answer() + "\n```\n ");

        (fenced.Failure, fenced.NotJsonReason).Should().Be((SummaryGroundedFailure.None, SummaryGroundedNotJsonReason.None));
        (bare.Failure, bare.NotJsonReason).Should().Be((SummaryGroundedFailure.None, SummaryGroundedNotJsonReason.None));
        fenced.Markdown.Should().Be(Read(Answer()).Markdown);
    }

    [Fact]
    public void Text_before_or_after_the_object_is_an_envelope_refusal_and_the_object_is_not_dug_out()
    {
        ShouldBeNotJson(Read("İşte özet:\n" + Answer()), SummaryGroundedNotJsonReason.Envelope);
        ShouldBeNotJson(Read(Answer() + "\nUmarım yardımcı olur."), SummaryGroundedNotJsonReason.Envelope);
        ShouldBeNotJson(Read("İşte özet:\n" + Answer() + "\nUmarım yardımcı olur."), SummaryGroundedNotJsonReason.Envelope);
        ShouldBeNotJson(Read("Özet hazır değil."), SummaryGroundedNotJsonReason.Envelope);
        ShouldBeNotJson(Read("İşte özet:\n```json\n" + Answer() + "\n```"), SummaryGroundedNotJsonReason.Envelope);
        ShouldBeNotJson(Read("```json\nİşte özet:\n" + Answer() + "\n```"), SummaryGroundedNotJsonReason.Envelope);
    }

    [Fact]
    public void An_object_that_is_not_closed_is_an_envelope_refusal()
    {
        ShouldBeNotJson(Read("{\"v\":4,\"main\":{\"t\":\"Konu.\""), SummaryGroundedNotJsonReason.Envelope);
        ShouldBeNotJson(Read(Answer()[..^2]), SummaryGroundedNotJsonReason.Envelope);
    }

    [Fact]
    public void A_code_fence_in_another_shape_is_named_as_such_and_not_tolerated()
    {
        ShouldBeNotJson(Read("```json\n" + Answer() + "\n```\nUmarım yardımcı olur."), SummaryGroundedNotJsonReason.CodeFence);
        ShouldBeNotJson(Read("```json\n" + Answer()), SummaryGroundedNotJsonReason.CodeFence);
        ShouldBeNotJson(Read("```" + Answer() + "```"), SummaryGroundedNotJsonReason.CodeFence);
    }

    [Fact]
    public void An_object_that_does_not_parse_is_malformed_json()
    {
        var valid = Answer();

        ShouldBeNotJson(Read(valid.Replace("\"v\":4,", "\"v\":4,,", StringComparison.Ordinal)), SummaryGroundedNotJsonReason.MalformedJson);
        ShouldBeNotJson(Read(valid.Replace("\"v\":4", "v:4", StringComparison.Ordinal)), SummaryGroundedNotJsonReason.MalformedJson);
        ShouldBeNotJson(Read(valid[..^1] + ",}"), SummaryGroundedNotJsonReason.MalformedJson);
        ShouldBeNotJson(Read(valid + valid), SummaryGroundedNotJsonReason.MalformedJson);
        ShouldBeNotJson(Read("```json\n" + valid[..^1] + ",}\n```"), SummaryGroundedNotJsonReason.MalformedJson);
        // Brace to brace on the outside, so it is the parser that refuses these: an object whose last brace is missing still
        // ends with the brace of its last block, and two fenced objects are one fence around something that is not one object.
        ShouldBeNotJson(Read(valid[..^1]), SummaryGroundedNotJsonReason.MalformedJson);
        ShouldBeNotJson(Read("```json\n" + valid + "\n```\n```json\n" + valid + "\n```"), SummaryGroundedNotJsonReason.MalformedJson);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("[{\"v\":4}]")]
    [InlineData("\"Özet hazır.\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("```json\n[1,2,3]\n```")]
    public void Valid_json_of_another_kind_is_root_not_object(string raw)
    {
        ShouldBeNotJson(Read(raw), SummaryGroundedNotJsonReason.RootNotObject);
    }

    [Fact]
    public void Nothing_at_all_is_an_envelope_refusal_without_a_category_of_its_own()
    {
        // The provider client reports an empty answer itself (EmptyOutput); the reader has no made-up "empty" reason for it.
        ShouldBeNotJson(Read(""), SummaryGroundedNotJsonReason.Envelope);
        ShouldBeNotJson(Read("  \n "), SummaryGroundedNotJsonReason.Envelope);
        ShouldBeNotJson(SummaryGroundedAnswer.Read(null, "stop", Input), SummaryGroundedNotJsonReason.Envelope);
    }

    [Fact]
    public void Other_refusals_carry_no_not_json_reason()
    {
        var limit = Read(Answer(points: Points(9)));
        var contract = Read(Answer(version: 3));
        var truncated = Read(Answer(), finish: "length");
        var truncatedProse = Read("Özet: konuşuldu.", finish: "length");
        var quoteNotFound = Read(Answer(main: Block("Konu.", [E("m004", "Hayır, hâlâ olmadı.")])));
        var missingSpoiler = ReadHidden(Answer());

        (limit.Failure, contract.Failure, truncated.Failure, truncatedProse.Failure, quoteNotFound.Failure, missingSpoiler.Failure).Should().Be(
            (SummaryGroundedFailure.Limit, SummaryGroundedFailure.Contract, SummaryGroundedFailure.Truncated, SummaryGroundedFailure.Truncated,
                SummaryGroundedFailure.QuoteNotFound, SummaryGroundedFailure.MissingRequiredSpoiler));
        new[] { limit, contract, truncated, truncatedProse, quoteNotFound, missingSpoiler }
            .Should().OnlyContain(r => r.NotJsonReason == SummaryGroundedNotJsonReason.None);
    }
}
