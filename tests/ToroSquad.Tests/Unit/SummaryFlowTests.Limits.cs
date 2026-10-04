using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// What the log says about a refused grounded answer: the validation category, for a Limit the bound that refused it and
/// the size that broke it, and the reader's numbers — "unknown" for what it did not reach, never a made-up 0. Names and
/// numbers only: no answer text, topic, quote, message text or prompt. The run itself is unchanged: a refused answer is not
/// published, reviewed, repaired or retried.
/// </summary>
public sealed partial class SummaryFlowTests
{
    private static readonly string LongText = new('a', 600);

    [Fact]
    public async Task A_draft_refused_for_a_safety_bound_names_the_bound_in_the_log_without_any_content()
    {
        var draft = GroundedJson(extraPoints: 8); // nine points: one more than the bound
        var world = PipelineWorld(_ => Ok(draft), _ => Ok(GroundedJson()));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1, "a refused draft is not reviewed, repaired or retried");
        responder.Public.Should().BeEmpty();
        var logs = world.AllLogs;
        logs.Should().Contain("stage=generator validation=Limit limit_reason=CandidatePoints limit_value=9")
            .And.Contain("raw_answer_chars=" + draft.Length).And.Contain("candidate_point_count=9").And.Contain("candidate_spoiler_count=0")
            .And.Contain("candidate_plan_count=unknown").And.Contain("evidence_count=unknown").And.Contain("rendered_chars=unknown")
            .And.Contain("shown_point_count=0").And.Contain("failed_stage=generator").And.Contain("failure=InvalidResponse");
        logs.Should().NotContain(GroundedSecret).And.NotContain("config").And.NotContain("Monfy").And.NotContain("şimdi oldu").And.NotContain("Oyun ayarı")
            .And.NotContain("Ek konu").And.NotContain("Ek bilgi").And.NotContain("\"t\"");
    }

    [Fact]
    public async Task A_review_refused_for_a_safety_bound_names_the_bound_for_the_reviewer_stage()
    {
        var world = PipelineWorld(_ => Ok(GroundedJson()), _ => Ok(GroundedJson(pointText: LongText)));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(2, "never a third request");
        responder.Public.Should().BeEmpty();
        var logs = world.AllLogs;
        logs.Should().Contain("stage=generator validation=None limit_reason=None limit_value=none")
            .And.Contain("stage=reviewer validation=Limit limit_reason=TextChars limit_value=600").And.Contain("failed_stage=reviewer");
        logs.Should().NotContain("aaaaaaaa", "the size is logged, the text is not");
    }

    [Fact]
    public async Task An_accepted_answer_logs_no_limit_reason_and_its_measured_sizes()
    {
        var world = PipelineWorld(_ => Ok(GroundedJson()), _ => Ok(GroundedJson()));

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);

        world.AllLogs.Should().Contain("stage=reviewer validation=None limit_reason=None limit_value=none")
            .And.Contain("raw_answer_chars=" + GroundedJson().Length).And.Contain("evidence_count=4").And.Contain("candidate_point_count=1")
            .And.Contain("candidate_spoiler_count=0").And.Contain("candidate_plan_count=0")
            .And.MatchRegex(@"stage=reviewer validation=None [^\n]*rendered_chars=[1-9]\d*").And.NotContain("=unknown");
    }

    [Theory]
    [InlineData("stop", "QuoteNotFound", "candidate_point_count=1")]
    [InlineData("length", "Truncated", "candidate_point_count=unknown")]
    public async Task A_refusal_that_is_not_a_bound_logs_no_limit_reason_and_no_made_up_counts(string finish, string category, string points)
    {
        var world = PipelineWorld(_ => Ok(GroundedJson("m009", "Hayır, hâlâ olmadı.")) with { FinishReason = finish }, _ => Ok(GroundedJson()));

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1);
        world.AllLogs.Should().Contain("stage=generator validation=" + category + " limit_reason=None limit_value=none")
            .And.Contain(points).And.Contain("evidence_count=unknown").And.Contain("rendered_chars=unknown")
            .And.NotContain("evidence_count=0", "a count the reader never finished is not a zero").And.NotContain("candidate_point_count=0");
    }
}
