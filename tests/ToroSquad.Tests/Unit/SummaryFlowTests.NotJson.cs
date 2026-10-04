using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// What the log says about a grounded answer that is not one JSON object: the category and its reason as names, the
/// answer's length as a number — never the answer, its first or last characters, a fence's language or the parser's
/// message. The run is unchanged: such an answer is not published, reviewed, repaired or asked for again.
/// </summary>
public sealed partial class SummaryFlowTests
{
    [Theory]
    [InlineData("İşte özet: " + GroundedSecret + "\n{0}", "Envelope")]
    [InlineData("{0}\nNot: " + GroundedSecret, "Envelope")]
    [InlineData("```gizlidil\n{0}\n```\nNot: " + GroundedSecret, "CodeFence")]
    [InlineData("{{\"v\":4,,\"main\":{{\"t\":\"" + GroundedSecret + "\"}}}}", "MalformedJson")]
    [InlineData("[\"" + GroundedSecret + "\"]", "RootNotObject")]
    public async Task A_draft_that_is_not_one_json_object_names_the_reason_in_the_log_without_any_content(string shape, string reason)
    {
        var draft = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, GroundedJson());
        var world = PipelineWorld(_ => Ok(draft), _ => Ok(GroundedJson()));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1, "no review, no repair request, no retry");
        responder.Public.Should().BeEmpty("the object inside the text is not dug out and published");
        var logs = world.AllLogs;
        logs.Should().Contain("stage=generator validation=NotJson limit_reason=None limit_value=none not_json_reason=" + reason + " ")
            .And.Contain("raw_answer_chars=" + draft.Length).And.Contain("candidate_point_count=unknown").And.Contain("evidence_count=unknown")
            .And.Contain("failed_stage=generator").And.Contain("failure=InvalidResponse");
        logs.Should().NotContain(GroundedSecret).And.NotContain("İşte özet").And.NotContain("gizlidil").And.NotContain("```").And.NotContain("Monfy")
            .And.NotContain("Oyun ayarı").And.NotContain("\"t\"").And.NotContain("JsonException").And.NotContain("LineNumber").And.NotContain("BytePosition");
    }

    [Fact]
    public async Task A_review_that_is_not_one_json_object_names_the_reason_for_the_reviewer_stage()
    {
        var world = PipelineWorld(_ => Ok(GroundedJson()), _ => Ok("Düzeltilmiş özet:\n" + GroundedJson()));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(2, "never a third request");
        responder.Public.Should().BeEmpty("an accepted draft is not published in place of a refused review");
        world.AllLogs.Should().Contain("stage=generator validation=None limit_reason=None limit_value=none not_json_reason=None ")
            .And.Contain("stage=reviewer validation=NotJson limit_reason=None limit_value=none not_json_reason=Envelope ").And.Contain("failed_stage=reviewer");
    }

    [Theory]
    [InlineData(8, "stop", "validation=Limit limit_reason=CandidatePoints limit_value=9 not_json_reason=None ")]
    [InlineData(0, "length", "validation=Truncated limit_reason=None limit_value=none not_json_reason=None ")]
    public async Task Other_refusals_log_no_not_json_reason(int extraPoints, string finish, string expected)
    {
        var world = PipelineWorld(_ => Ok(GroundedJson(extraPoints: extraPoints)) with { FinishReason = finish }, _ => Ok(GroundedJson()));

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.AiFailed);

        world.AllLogs.Should().Contain("stage=generator " + expected);
    }

    [Fact]
    public async Task The_supported_code_fence_is_still_published_and_logs_no_reason()
    {
        var world = PipelineWorld(_ => Ok("```json\n" + GroundedJson() + "\n```"), _ => Ok("```json\n" + GroundedJson() + "\n```"));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        string.Join("\n", responder.Public.Single()).Should().Contain("## Ana konu\nOyun ayarı sorunu konuşuldu.").And.NotContain("```").And.NotContain("{");
        world.AllLogs.Should().Contain("stage=reviewer validation=None limit_reason=None limit_value=none not_json_reason=None ");
    }
}
