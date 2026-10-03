using ToroSquad.Core;
using ToroSquad.Modules.Summary;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The grounded canary: with the global mode on Legacy, only the exact channel or thread ids listed in
/// <see cref="SummaryOptions.GroundedCanaryChannelIds"/> use the grounded pipeline. An empty list (the default) is no canary;
/// a listed parent does not cover its threads; a global Grounded mode ignores the list. The mode is decided once per run.
/// Everything else — role gate, 100-message rule, cooldowns, the inference limits of each mode — is unchanged.
/// </summary>
public sealed partial class SummaryFlowTests
{
    /// <summary>Global mode Legacy; the given channels are canaries; the fake provider answers each mode in its own format.</summary>
    private static World CanaryWorld(params ChannelId[] canary)
    {
        var world = new World();
        world.Options.GroundedCanaryChannelIds = canary.Select(c => c.Value).ToArray();
        foreach (var channel in new[] { Here, Other, Thread })
            world.Discord.Channels[channel.Value] = ConfigTalk();
        world.Ai.Respond = prompt => Task.FromResult(prompt.Profile is null ? Ok(Answer) : Ok(GroundedJson()));
        return world;
    }

    private static bool IsLegacy(SummaryPromptMessages prompt) => prompt.System == SummaryPrompt.System && prompt.Profile is null;

    [Fact]
    public async Task Without_a_canary_list_every_channel_stays_legacy_with_one_inference()
    {
        var world = CanaryWorld();

        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);

        new SummaryOptions().GroundedCanaryChannelIds.Should().BeEmpty("the default is no canary");
        world.Ai.Calls.Should().Be(1);
        world.Ai.Prompts.Should().OnlyContain(p => IsLegacy(p));
        world.AllLogs.Should().Contain("generation_mode=Legacy mode_source=Legacy").And.NotContain("grounded pipeline");
    }

    [Fact]
    public async Task A_listed_channel_uses_the_grounded_pipeline_while_the_global_mode_is_legacy()
    {
        var world = CanaryWorld(Here);
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        world.Options.GenerationMode.Should().Be(SummaryGenerationMode.Legacy);
        world.Ai.Calls.Should().Be(2, "a canary run is the grounded pipeline: a draft and its review, never more");
        world.Ai.Prompts.First().System.Should().Be(SummaryGroundedPrompt.System);
        world.Ai.Prompts.Last().System.Should().Be(SummaryGroundedReviewPrompt.System);
        string.Join("\n", responder.Public.Single()).Should().Contain("- **Oyun ayarları:**");
        world.AllLogs.Should().Contain("generation_mode=Grounded mode_source=Canary").And.Contain("inference_count=2").And.Contain("failed_stage=none");
    }

    [Fact]
    public async Task Another_channel_is_not_affected_by_the_canary()
    {
        var world = CanaryWorld(Here);

        (await world.RunAsync(new FakeResponder(), channel: Other)).Should().Be(SummaryOutcome.Posted);

        world.Ai.Calls.Should().Be(1);
        world.Ai.Prompts.Should().OnlyContain(p => IsLegacy(p));
        world.AllLogs.Should().Contain("generation_mode=Legacy mode_source=Legacy");
    }

    [Fact]
    public async Task A_listed_parent_channel_does_not_make_its_thread_grounded()
    {
        var world = CanaryWorld(Here);

        (await world.RunAsync(new FakeResponder(), channel: Thread)).Should().Be(SummaryOutcome.Posted);

        world.Ai.Calls.Should().Be(1, "the match is the interaction's own id: no inheritance from a parent, a category or the guild");
        world.Ai.Prompts.Should().OnlyContain(p => IsLegacy(p));
    }

    [Fact]
    public async Task A_thread_is_a_canary_only_by_its_own_id_and_then_its_parent_is_not()
    {
        var world = CanaryWorld(Thread);

        (await world.RunAsync(new FakeResponder(), channel: Thread)).Should().Be(SummaryOutcome.Posted);
        world.Ai.Calls.Should().Be(2);
        world.Ai.Prompts.Should().OnlyContain(p => !IsLegacy(p));

        (await world.RunAsync(new FakeResponder(), channel: Here, member: Member2)).Should().Be(SummaryOutcome.Posted);
        world.Ai.Calls.Should().Be(3, "the parent channel is not listed: one legacy inference");
        IsLegacy(world.Ai.Prompts.Last()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_global_grounded_mode_is_grounded_everywhere_whatever_the_canary_list_says(bool otherChannelListed)
    {
        var world = otherChannelListed ? CanaryWorld(Here) : CanaryWorld();
        world.Options.GenerationMode = SummaryGenerationMode.Grounded;

        (await world.RunAsync(new FakeResponder(), channel: Other)).Should().Be(SummaryOutcome.Posted);

        world.Ai.Calls.Should().Be(2);
        world.AllLogs.Should().Contain("generation_mode=Grounded mode_source=Global", "the list never switches a globally grounded channel back to legacy");
    }

    [Fact]
    public async Task The_mode_is_decided_once_at_the_start_of_a_canary_run()
    {
        // The channel is removed from the list while the generator request is running: the run stays grounded and is reviewed.
        var world = CanaryWorld(Here);
        world.Ai.Respond = prompt =>
        {
            world.Options.GroundedCanaryChannelIds = [];
            return Task.FromResult(Ok(GroundedJson()));
        };
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        world.Ai.Calls.Should().Be(2);
        string.Join("\n", responder.Public.Single()).Should().Contain("## Ana konu\nOyun ayarı sorunu konuşuldu.").And.NotContain("{");
    }

    [Fact]
    public async Task A_legacy_run_does_not_become_grounded_when_its_channel_is_listed_meanwhile()
    {
        var world = CanaryWorld();
        world.Ai.Respond = prompt =>
        {
            world.Options.GroundedCanaryChannelIds = [Here.Value];
            return Task.FromResult(Ok(Answer));
        };
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.Posted);

        world.Ai.Calls.Should().Be(1, "one run never mixes two modes");
        responder.Public.Single().Should().Equal(Answer);
    }

    [Fact]
    public async Task Each_request_carries_its_own_deadline()
    {
        var world = CanaryWorld(Here);

        await world.RunAsync(new FakeResponder());
        await world.RunAsync(new FakeResponder(), channel: Other, member: Member2);

        var prompts = world.Ai.Prompts.ToList();
        prompts.Should().HaveCount(3);
        prompts[0].Profile!.RequestTimeout.Should().Be(TimeSpan.FromSeconds(35), "the grounded generator gets more room");
        prompts[1].Profile!.RequestTimeout.Should().Be(TimeSpan.FromSeconds(25), "the grounded reviewer keeps 25 s");
        prompts[2].Profile.Should().BeNull("the legacy request has no profile: the client applies Summary:RequestTimeoutSeconds");
        world.Options.RequestTimeoutSeconds.Should().Be(25);
    }

    [Fact]
    public async Task A_generator_timeout_in_a_canary_channel_ends_the_run_without_a_review()
    {
        var world = CanaryWorld(Here);
        world.Ai.Respond = _ => Task.FromResult(SummaryAiResult.Failed(SummaryAiFailure.Timeout, TimeSpan.FromSeconds(35)));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(1, "no review, no retry, no legacy request for the canary channel");
        responder.Public.Should().BeEmpty();
        world.AllLogs.Should().Contain("failure=Timeout").And.Contain("failed_stage=generator").And.Contain("generator_latency_ms=35000").And.Contain("reviewer_model=not_called");
    }

    [Fact]
    public async Task A_reviewer_timeout_in_a_canary_channel_is_not_followed_by_a_third_request()
    {
        var world = CanaryWorld(Here);
        world.Ai.Respond = prompt => Task.FromResult(IsReview(prompt) ? SummaryAiResult.Failed(SummaryAiFailure.Timeout, TimeSpan.FromSeconds(25)) : Ok(GroundedJson()));
        var responder = new FakeResponder();

        (await world.RunAsync(responder)).Should().Be(SummaryOutcome.AiFailed);

        world.Ai.Calls.Should().Be(2);
        responder.Public.Should().BeEmpty();
        world.AllLogs.Should().Contain("failure=Timeout").And.Contain("failed_stage=reviewer").And.Contain("reviewer_latency_ms=25000");
    }

    [Fact]
    public async Task A_canary_channel_keeps_the_role_gate_the_100_message_rule_and_the_cooldowns()
    {
        var world = CanaryWorld(Here);

        (await world.RunAsync(new FakeResponder(), roles: [])).Should().Be(SummaryOutcome.RoleMissing);
        world.Discord.Channels[Here.Value] = AfterSummary(99);
        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.NotEnoughNewMessages);
        world.Ai.Calls.Should().Be(0, "the gates come before any request, in a canary channel as everywhere");

        world.Discord.Channels[Here.Value] = ConfigTalk();
        (await world.RunAsync(new FakeResponder())).Should().Be(SummaryOutcome.Posted);
        (await world.RunAsync(new FakeResponder(), member: Member2)).Should().Be(SummaryOutcome.Throttled, "a posted summary starts the channel cooldown");
        world.Ai.Calls.Should().Be(2);
    }
}
