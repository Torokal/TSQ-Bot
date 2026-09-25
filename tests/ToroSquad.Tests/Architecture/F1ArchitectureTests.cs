using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ToroSquad.Core.Localization;
using ToroSquad.Discord.Interactions;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// Formula 1 module boundaries: isolated from other feature modules, no Discord SDK in business code, commands never reach
/// providers, providers/listener never reach Discord or the outbox, and only the planner stages notifications.
/// </summary>
public sealed partial class F1ArchitectureTests
{
    private static readonly Assembly Formula1 = typeof(ToroSquad.Modules.Formula1.Formula1Module).Assembly;
    private static readonly Assembly Esports = typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly;
    private static readonly Assembly Example = typeof(ToroSquad.Modules.Example.ExampleModule).Assembly;
    private static readonly Assembly Core = typeof(ToroSquad.Core.Modules.IToroModule).Assembly;
    private static readonly Assembly Infrastructure = typeof(ToroSquad.Infrastructure.Persistence.ToroDbContext).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() =>
        new ArchLoader().LoadAssemblies(Core, Infrastructure, DiscordLayer, Formula1).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Formula1_and_other_feature_modules_never_reference_each_other()
    {
        References(Formula1).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Formula1");
        References(Esports).Should().NotContain("ToroSquad.Modules.Formula1");
        References(Example).Should().NotContain("ToroSquad.Modules.Formula1");
        References(Core).Should().NotContain("ToroSquad.Modules.Formula1");
        References(Infrastructure).Should().NotContain("ToroSquad.Modules.Formula1");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Formula1");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Formula1.Domain")]
    [InlineData("ToroSquad.Modules.Formula1.Providers")]
    [InlineData("ToroSquad.Modules.Formula1.Application")]
    [InlineData("ToroSquad.Modules.Formula1.Persistence")]
    public void Formula1_business_code_does_not_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns).Or().ResideInNamespaceMatching(ns + @"\..*")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Formula1_domain_is_pure() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Formula1.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure|System\.Net\.Http|MQTTnet|ToroSquad\.Modules\.Formula1\.(Providers|Application|Persistence|Commands))(\..*)?$")));

    [Fact]
    public void Commands_never_reach_providers_or_the_network()
    {
        // Slash commands read the cache only: no provider interface, provider client, HTTP or MQTT type is even reachable.
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Formula1.Commands")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(ToroSquad\.Modules\.Formula1\.Providers\.(Jolpica|OpenF1|Fixtures)|System\.Net\.Http|MQTTnet)(\..*)?$"))
            .AndShould().NotDependOnAny(Types().That().HaveNameMatching(@"^IF1(Schedule|Lifecycle|Results|Standings)Provider$|^IF1LiveTransport$|^Formula1Poller$|^Formula1Workflow$")));
    }

    [Fact]
    public void Providers_and_the_live_listener_never_talk_to_discord_or_stage_notifications()
    {
        var discordish = Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|OutgoingMessage|NotificationRequest|IDeliveryPolicy)$");
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Formula1\.Providers(\..*)?$").Should().NotDependOnAny(discordish));
        AssertNoViolations(Types().That().HaveNameMatching(@"^(Formula1LiveListener|Formula1Workflow|Formula1Poller)$").Should().NotDependOnAny(
            Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|NotificationRequest)$")));
    }

    [Fact]
    public void Only_the_planner_stages_notifications_and_nothing_in_the_module_sends_directly()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Formula1(\..*)?$")
            .And().DoNotHaveNameMatching(@"^Formula1NotificationPlanner$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^INotificationOutbox$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Formula1(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^IMessageTransport$")));
    }

    [Fact]
    public void Every_formula1_interaction_class_declares_its_module()
    {
        foreach (var type in Formula1.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
            type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!.ModuleId.Should().Be("formula1", type.Name);
    }

    [Fact]
    public void No_year_is_hard_coded_in_formula1_code()
    {
        var root = Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Formula1");
        foreach (var file in SourceFiles(root))
        {
            // Comments may mention provider history (e.g. a format change); code may not contain a season number.
            var code = string.Join("\n", File.ReadAllLines(file).Select(l => l.Contains("//", StringComparison.Ordinal) ? l[..l.IndexOf("//", StringComparison.Ordinal)] : l));
            YearLiteral().IsMatch(code).Should().BeFalse($"{Path.GetFileName(file)} must derive seasons from provider data / the clock");
        }
    }

    [Fact]
    public void Formula1_code_uses_the_injected_clock()
    {
        var root = Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Formula1");
        foreach (var file in SourceFiles(root))
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("DateTime.Now", Path.GetFileName(file)).And.NotContain("DateTime.UtcNow", Path.GetFileName(file))
                .And.NotContain("DateTimeOffset.UtcNow", Path.GetFileName(file)).And.NotContain("DateTimeOffset.Now", Path.GetFileName(file));
        }
    }

    [Fact]
    public void Formula1_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Formula1", "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));

        // Every literal key used by the code resolves (dynamic prefixes are checked for each value they can take).
        var code = string.Join("\n", SourceFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Formula1")).Select(File.ReadAllText));
        foreach (Match m in KeyLiteral().Matches(code))
        {
            var key = m.Groups[1].Value;
            if (key.EndsWith('.'))
                continue;
            tr.Should().ContainKey(key);
        }

        foreach (var slug in new[] { "fp1", "fp2", "fp3", "sq", "sprint", "quali", "race", "unknown" })
            tr.Should().ContainKey("f1.session." + slug);
        foreach (var source in new[] { "jolpica", "openf1", "demo", "none" })
            tr.Should().ContainKey("f1.source." + source);

        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(Esports, "ToroSquad.Modules.Esports.Localization"),
            new LocalizationSource(Formula1, "ToroSquad.Modules.Formula1.Localization"),
        ]);
        catalog.Get("tr", "f1.standings.constructors_title", 2030).Should().Be("🏭 Takımlar Şampiyonası 2030");
    }

    /// <summary>Hand-written sources only (bin/obj hold generated files).</summary>
    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static IEnumerable<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"""((?:f1|module\.formula1|about\.attr)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"\b20[2-9]\d\b")]
    private static partial Regex YearLiteral();
}
