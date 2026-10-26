using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Ai;
using ToroSquad.Modules.Summary;
using ToroSquad.Modules.Summary.Application;
using ToroSquad.Modules.Summary.Providers;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The shared AI layer (ToroSquad.Ai): it depends on no TSQ project, sends the only HTTP request, and keeps TSQ Özet's
/// requests byte for byte what they were before the transport was shared (same body, deadlines and failure categories).
/// </summary>
public sealed class OpenCodeGoTransportTests
{
    private static readonly Assembly Ai = typeof(IAiChatClient).Assembly;

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Code(string project) => string.Join(Environment.NewLine, Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj")).Select(File.ReadAllText));

    [Fact]
    public void The_shared_ai_layer_depends_on_no_tsq_project_no_database_and_no_discord_and_sends_the_only_request()
    {
        References(Ai).Should().NotContain(r => r.StartsWith("ToroSquad.", StringComparison.Ordinal));
        References(Ai).Should().NotContain(r => r.StartsWith("Discord", StringComparison.Ordinal) || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        References(Ai).Should().NotContain(r => Regex.IsMatch(r, "Polly|Resilience|OpenAI|Anthropic|SemanticKernel", RegexOptions.IgnoreCase));
        var code = Code("ToroSquad.Ai");
        Regex.Matches(code, @"\.SendAsync\(").Should().ContainSingle("one HTTP send: one request per call, no retry loop");
        code.Should().NotMatchRegex(@"\b(while|for|foreach|do)\b[^;{]*\{[^}]*SendAsync").And.NotContain("Log");
        Regex.Matches(code, @"SessionHeader, Guid\.NewGuid\(\)").Should().ContainSingle("x-opencode-session is a random GUID — never an id, name or text");
        References(typeof(SummaryModule).Assembly).Should().Contain("ToroSquad.Ai");
    }

    [Fact]
    public void Summary_requests_are_byte_for_byte_what_they_were_before_the_shared_transport()
    {
        var prompt = new SummaryPromptMessages("S", "U");
        var settings = new SummaryOptions();
        Encoding.UTF8.GetString(OpenCodeSummaryAiClient.RequestBody(prompt, settings)).Should().Be(
            """{"model":"deepseek-v4.1-flash","messages":[{"role":"system","content":"S"},{"role":"user","content":"U"}],"max_tokens":1200,"temperature":0.3,"top_p":0.9,"thinking":{"type":"disabled"},"stream":false}""");
        Encoding.UTF8.GetString(OpenCodeSummaryAiClient.RequestBody(prompt, new SummaryOptions { DisableThinking = false, ReasoningEffort = "high" })).Should().Be(
            """{"model":"deepseek-v4.1-flash","messages":[{"role":"system","content":"S"},{"role":"user","content":"U"}],"max_tokens":1200,"temperature":0.3,"top_p":0.9,"thinking":{"type":"enabled"},"reasoning_effort":"high","stream":false}""");
        Encoding.UTF8.GetString(OpenCodeSummaryAiClient.RequestBody(prompt with { Profile = settings.GroundedGenerator, MaxOutputTokens = 2000 }, settings)).Should().Be(
            """{"model":"glm-5.3-flash","messages":[{"role":"system","content":"S"},{"role":"user","content":"U"}],"max_tokens":2000,"temperature":0.3,"top_p":0.9,"reasoning_effort":"low","stream":false}""");
        Encoding.UTF8.GetString(OpenCodeSummaryAiClient.RequestBody(prompt with { Profile = settings.GroundedReviewer }, settings)).Should().Be(
            """{"model":"deepseek-v4.1-flash","messages":[{"role":"system","content":"S"},{"role":"user","content":"U"}],"max_tokens":1200,"temperature":0.3,"top_p":0.9,"thinking":{"type":"disabled"},"stream":false}""");
        OpenCodeSummaryAiClient.Request(prompt with { Profile = settings.GroundedGenerator }, settings).Timeout.Should().Be(settings.GroundedGeneratorTimeout);
        OpenCodeSummaryAiClient.Request(prompt, settings).Timeout.Should().Be(settings.RequestTimeout);
    }

    [Fact]
    public void Failure_categories_and_usage_map_one_to_one_between_the_shared_layer_and_the_summary_module()
    {
        Enum.GetValues<AiFailure>().Select(f => (f.ToString(), (int)f)).Should()
            .BeEquivalentTo(Enum.GetValues<SummaryAiFailure>().Select(f => (f.ToString(), (int)f)));
        var parsed = OpenCodeGo.Parse(Encoding.UTF8.GetBytes(
            """{"choices":[{"finish_reason":"length","message":{"content":"x"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":4},"completion_tokens_details":{"reasoning_tokens":2}}}"""),
            TimeSpan.Zero, 200);
        parsed.Usage.Should().Be(new AiUsage(10, 5, 2, 4));
        parsed.Truncated.Should().BeTrue();
        OpenCodeSummaryAiClient.FromShared(parsed).Usage.Should().Be(new SummaryAiUsage(10, 5, 2));
        OpenCodeSummaryAiClient.FromShared(AiChatResult.Failed(AiFailure.Timeout, TimeSpan.Zero)).Usage.Should().Be(SummaryAiUsage.None);
        JsonSerializer.Serialize(new OpenCodeGoApiKey("secret-value-123").ToString()).Should().NotContain("secret");
    }
}
