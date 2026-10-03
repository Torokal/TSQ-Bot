using Microsoft.Extensions.Configuration;
using ToroSquad.Modules.Summary;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The grounded canary's configuration: the list is empty by default, matches exact ids only and never overrides a global
/// Grounded mode; the two grounded requests have their own deadlines while the legacy request keeps its own.
/// </summary>
public sealed class SummaryCanaryOptionsTests
{
    private const ulong Channel = 1300000000000000001;
    private const ulong Thread = 1300000000000000002;

    [Fact]
    public void Defaults_are_no_canary_legacy_everywhere_and_a_longer_deadline_for_the_generator_only()
    {
        var options = new SummaryOptions();

        options.GroundedCanaryChannelIds.Should().BeEmpty();
        options.GenerationMode.Should().Be(SummaryGenerationMode.Legacy);
        (options.RequestTimeoutSeconds, options.GroundedGeneratorTimeoutSeconds, options.GroundedReviewerTimeoutSeconds).Should().Be((25, 35, 25));
        options.LongestRequestTimeout.Should().Be(TimeSpan.FromSeconds(35), "the HTTP client's backstop must not cut the generator short");
        options.GroundedGenerator.Should().Be(new SummaryAiProfile("glm-5.3-flash", SummaryThinking.EffortOnly, "low", TimeSpan.FromSeconds(35)));
        options.GroundedReviewer.Should().Be(new SummaryAiProfile("deepseek-v4.1-flash", SummaryThinking.Disabled, null, TimeSpan.FromSeconds(25)));
        options.ResolveGenerationMode(Channel).Should().Be(new SummaryModeDecision(SummaryGenerationMode.Legacy, SummaryModeSource.Legacy));
        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Only_an_exact_id_is_a_canary()
    {
        var options = new SummaryOptions { GroundedCanaryChannelIds = [Channel] };

        options.ResolveGenerationMode(Channel).Should().Be(new SummaryModeDecision(SummaryGenerationMode.Grounded, SummaryModeSource.Canary));
        options.ResolveGenerationMode(Thread).Mode.Should().Be(SummaryGenerationMode.Legacy, "a thread has its own id; nothing is inherited");
        options.ResolveGenerationMode(Channel + 1).Mode.Should().Be(SummaryGenerationMode.Legacy);
        options.ResolveGenerationMode(0).Mode.Should().Be(SummaryGenerationMode.Legacy);
        new SummaryOptions { GroundedCanaryChannelIds = [Channel, Thread] }.ResolveGenerationMode(Thread).Source.Should().Be(SummaryModeSource.Canary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_global_grounded_mode_wins_and_the_list_cannot_switch_it_off(bool listed)
    {
        var options = new SummaryOptions { GenerationMode = SummaryGenerationMode.Grounded, GroundedCanaryChannelIds = listed ? [Channel] : [] };

        options.ResolveGenerationMode(Channel).Should().Be(new SummaryModeDecision(SummaryGenerationMode.Grounded, SummaryModeSource.Global));
        options.ResolveGenerationMode(Thread).Should().Be(new SummaryModeDecision(SummaryGenerationMode.Grounded, SummaryModeSource.Global));
    }

    [Fact]
    public void The_list_and_the_deadlines_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Summary:GroundedCanaryChannelIds:0"] = "1300000000000000001",
            ["Summary:GroundedCanaryChannelIds:1"] = "1300000000000000002",
            ["Summary:GroundedGeneratorTimeoutSeconds"] = "40",
            ["Summary:GroundedReviewerTimeoutSeconds"] = "20",
        }).Build();
        var options = new SummaryOptions();

        configuration.GetSection(SummaryOptions.Section).Bind(options);

        options.GroundedCanaryChannelIds.Should().Equal(Channel, Thread);
        (options.GroundedGeneratorTimeout, options.GroundedReviewerTimeout, options.RequestTimeout).Should().Be((TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(25)));
        options.GenerationMode.Should().Be(SummaryGenerationMode.Legacy, "listing canaries does not change the global mode");
        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Deadlines_and_canary_ids_are_validated()
    {
        new SummaryOptions { GroundedGeneratorTimeoutSeconds = 4 }.Validate().Should().ContainSingle().Which.Should().Contain("GroundedGeneratorTimeoutSeconds");
        new SummaryOptions { GroundedGeneratorTimeoutSeconds = 121 }.Validate().Should().ContainSingle().Which.Should().Contain("GroundedGeneratorTimeoutSeconds");
        new SummaryOptions { GroundedReviewerTimeoutSeconds = 0 }.Validate().Should().ContainSingle().Which.Should().Contain("GroundedReviewerTimeoutSeconds");
        new SummaryOptions { GroundedReviewerTimeoutSeconds = 500 }.Validate().Should().ContainSingle().Which.Should().Contain("GroundedReviewerTimeoutSeconds");
        new SummaryOptions { GroundedCanaryChannelIds = [5] }.Validate().Should().ContainSingle().Which.Should().Contain("GroundedCanaryChannelIds");
        new SummaryOptions { GroundedCanaryChannelIds = Enumerable.Range(1, SummaryOptions.MaxCanaryChannels + 1).Select(i => Channel + (ulong)i).ToArray() }
            .Validate().Should().ContainSingle().Which.Should().Contain("GroundedCanaryChannelIds", "a canary is a short explicit list");
        new SummaryOptions { GroundedCanaryChannelIds = [Channel, Thread], GroundedGeneratorTimeoutSeconds = 120, GroundedReviewerTimeoutSeconds = 5 }.Validate().Should().BeEmpty();
        // The legacy deadline is its own setting and keeps its default.
        new SummaryOptions { GroundedGeneratorTimeoutSeconds = 60 }.RequestTimeoutSeconds.Should().Be(25);
    }
}
