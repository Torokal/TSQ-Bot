using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ToroSquad.Discord.Interactions;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Letterboxd boundaries: isolated from other feature modules (no News or Updates reference: Letterboxd is not a game
/// and shares no project), no Discord SDK in business code, commands never reach the network, providers never reach
/// Discord or the outbox, only the planner stages. The only Letterboxd request is the member's RSS feed, built in one class
/// from the validated username; no HTML, film, list, API or image page is requested. No ping opt-in, injected clock only,
/// review text never logged, matching localization catalogs.
/// </summary>
public sealed partial class LetterboxdArchitectureTests
{
    private static readonly Assembly Letterboxd = typeof(ToroSquad.Modules.Letterboxd.LetterboxdModule).Assembly;
    private static readonly Assembly Core = typeof(ToroSquad.Core.Modules.IToroModule).Assembly;
    private static readonly Assembly Infrastructure = typeof(ToroSquad.Infrastructure.Persistence.ToroDbContext).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, Infrastructure, DiscordLayer, Letterboxd).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Letterboxd_and_other_feature_modules_never_reference_each_other()
    {
        References(Letterboxd).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Letterboxd");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.News.NewsModule), typeof(ToroSquad.Modules.Updates.UpdatesModule), typeof(ToroSquad.Modules.Agenda.AgendaModule),
                     typeof(ToroSquad.Modules.Esports.EsportsModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Letterboxd");
        References(Core).Should().NotContain("ToroSquad.Modules.Letterboxd");
        References(Infrastructure).Should().NotContain("ToroSquad.Modules.Letterboxd");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Letterboxd");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Letterboxd.Domain")]
    [InlineData("ToroSquad.Modules.Letterboxd.Providers")]
    [InlineData("ToroSquad.Modules.Letterboxd.Application")]
    [InlineData("ToroSquad.Modules.Letterboxd.Persistence")]
    public void Letterboxd_business_code_does_not_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns).Or().ResideInNamespaceMatching(ns + @"\..*")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Letterboxd_domain_is_pure() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Letterboxd.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(
                @"^(Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure|System\.Net\.Http|ToroSquad\.Modules\.Letterboxd\.(Providers|Application|Persistence|Commands))(\..*)?$")));

    [Fact]
    public void Commands_never_reach_a_provider_or_the_network() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Letterboxd.Commands")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(LetterboxdRssClient|LetterboxdPoller|LetterboxdPlanner|HttpClient|IHttpClientFactory)$")));

    [Fact]
    public void Providers_never_talk_to_discord_or_the_outbox_and_only_the_planner_stages()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Letterboxd\.Providers(\..*)?$").Should().NotDependOnAny(
            Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|OutgoingMessage|NotificationRequest|IDeliveryPolicy|MentionPolicy)$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Letterboxd(\..*)?$")
            .And().DoNotHaveNameMatching(@"^LetterboxdPlanner$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^INotificationOutbox$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Letterboxd(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^IMessageTransport$")));
    }

    /// <summary>
    /// The one Letterboxd request in the code base is the member's RSS feed, made by <c>LetterboxdRssClient</c> from the
    /// address built in <c>LetterboxdUrl.Feed</c>. No other code builds a request, and no code names the API, a /by/ sort
    /// path, a film list or an image fetch.
    /// </summary>
    [Fact]
    public void The_only_letterboxd_request_is_the_member_rss_feed()
    {
        var clientCode = File.ReadAllText(Path.Combine(Root(), "Providers", "LetterboxdRssClient.cs"));
        clientCode.Should().Contain("new HttpRequestMessage(HttpMethod.Get, new Uri(LetterboxdUrl.Feed(o.Username)))");
        File.ReadAllText(Path.Combine(Root(), "Domain", "LetterboxdEntry.cs")).Should().Contain("public static string Feed(string username) => Profile(username) + \"rss/\";");

        foreach (var file in SourceFiles(Root()))
        {
            var name = Path.GetFileName(file);
            var code = File.ReadAllText(file);
            if (name != "LetterboxdRssClient.cs")
                code.Should().NotContain("new HttpRequestMessage", name).And.NotContain(".SendAsync(", name).And.NotContain("CreateClient(", name);
            code.Should().NotContain("GetStringAsync", name).And.NotContain("GetStreamAsync", name).And.NotContain("GetByteArrayAsync", name);
            code.Should().NotContain("api.letterboxd.com", name).And.NotContain("/by/", name).And.NotContain("/films/", name).And.NotContain("/ratings/", name);
        }

        foreach (var file in SourceFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src")).Where(f => !f.Contains("ToroSquad.Modules.Letterboxd", StringComparison.Ordinal)))
            File.ReadAllText(file).Should().NotContain("letterboxd.com", Path.GetFileName(file));
    }

    [Fact]
    public void Letterboxd_never_opts_in_to_a_ping_and_never_logs_review_text()
    {
        foreach (var file in SourceFiles(Root()))
        {
            var code = File.ReadAllText(file);
            PingOptIn().IsMatch(code).Should().BeFalse(Path.GetFileName(file));
            foreach (Match log in LogCall().Matches(code))
                log.Value.Should().NotContain("Review", Path.GetFileName(file)).And.NotContain("Excerpt", Path.GetFileName(file));
        }
    }

    [Fact]
    public void Letterboxd_has_no_interaction_class_and_is_off_by_default()
    {
        Letterboxd.GetTypes().Should().NotContain(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t), "only /tsq-admin operations, no own command root");
        var module = new ToroSquad.Modules.Letterboxd.LetterboxdModule();
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("tsq-admin letterboxd");
        module.InteractionModuleTypes.Should().BeEmpty();
    }

    [Fact]
    public void Letterboxd_code_uses_the_injected_clock()
    {
        foreach (var file in SourceFiles(Root()))
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("DateTime.Now", Path.GetFileName(file)).And.NotContain("DateTime.UtcNow", Path.GetFileName(file))
                .And.NotContain("DateTimeOffset.UtcNow", Path.GetFileName(file)).And.NotContain("DateTimeOffset.Now", Path.GetFileName(file))
                .And.NotContain("Task.Delay(TimeSpan", Path.GetFileName(file));
        }
    }

    [Fact]
    public void Letterboxd_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));

        var code = string.Join("\n", SourceFiles(Root()).Select(File.ReadAllText));
        foreach (Match m in KeyLiteral().Matches(code))
            tr.Should().ContainKey(m.Groups[1].Value);
    }

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Letterboxd");

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or "Migrations"));

    private static IEnumerable<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"""((?:letterboxd|module\.letterboxd)\.(?!com)[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"logger\.Log\w+\([^;]*;", RegexOptions.Singleline)]
    private static partial Regex LogCall();

    [GeneratedRegex(@"EveryoneOnly|ExplicitUsers|Everyone\s*:\s*true|Everyone\s*=\s*true|RoleMention|new MentionPolicy\(")]
    private static partial Regex PingOptIn();
}
