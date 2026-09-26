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
/// Volleyball module boundaries: isolated from other feature modules, no Discord SDK in business code, commands never
/// reach the provider or the network, the provider never reaches Discord or the outbox, and only the planner stages
/// notifications. Plus: no hard-coded season years, injected clock only, matching localization catalogs.
/// </summary>
public sealed partial class VbArchitectureTests
{
    private static readonly Assembly Volleyball = typeof(ToroSquad.Modules.Volleyball.VolleyballModule).Assembly;
    private static readonly Assembly Core = typeof(ToroSquad.Core.Modules.IToroModule).Assembly;
    private static readonly Assembly Infrastructure = typeof(ToroSquad.Infrastructure.Persistence.ToroDbContext).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() =>
        new ArchLoader().LoadAssemblies(Core, Infrastructure, DiscordLayer, Volleyball).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Volleyball_and_other_feature_modules_never_reference_each_other()
    {
        References(Volleyball).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Volleyball");
        foreach (var other in new[] { typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module), typeof(ToroSquad.Modules.Example.ExampleModule) })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Volleyball");
        References(Core).Should().NotContain("ToroSquad.Modules.Volleyball");
        References(Infrastructure).Should().NotContain("ToroSquad.Modules.Volleyball");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Volleyball");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Volleyball.Domain")]
    [InlineData("ToroSquad.Modules.Volleyball.Providers")]
    [InlineData("ToroSquad.Modules.Volleyball.Application")]
    [InlineData("ToroSquad.Modules.Volleyball.Persistence")]
    public void Volleyball_business_code_does_not_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns).Or().ResideInNamespaceMatching(ns + @"\..*")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Volleyball_domain_is_pure() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Volleyball.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure|System\.Net\.Http|ToroSquad\.Modules\.Volleyball\.(Providers|Application|Persistence|Commands))(\..*)?$")));

    [Fact]
    public void Commands_never_reach_the_provider_or_the_network() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Volleyball.Commands")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(ToroSquad\.Modules\.Volleyball\.Providers\.(Fivb|Fixtures)|System\.Net\.Http)(\..*)?$"))
            .AndShould().NotDependOnAny(Types().That().HaveNameMatching(@"^IVolleyballDataProvider$|^VolleyballPoller$|^VolleyballWorkflow$|^FivbVis")));

    [Fact]
    public void The_provider_layer_never_talks_to_discord_or_stages_notifications()
    {
        var discordish = Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|OutgoingMessage|NotificationRequest|IDeliveryPolicy)$");
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Volleyball\.Providers(\..*)?$").Should().NotDependOnAny(discordish));
        AssertNoViolations(Types().That().HaveNameMatching(@"^(VolleyballWorkflow|VolleyballPoller)$").Should().NotDependOnAny(
            Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|NotificationRequest)$")));
    }

    [Fact]
    public void Only_the_planner_stages_notifications_and_nothing_in_the_module_sends_directly()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Volleyball(\..*)?$")
            .And().DoNotHaveNameMatching(@"^VolleyballNotificationPlanner$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^INotificationOutbox$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Volleyball(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^IMessageTransport$")));
    }

    [Fact]
    public void Every_volleyball_interaction_class_declares_its_module()
    {
        foreach (var type in Volleyball.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
            type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!.ModuleId.Should().Be("volleyball", type.Name);
    }

    [Fact]
    public void The_module_is_off_by_default_and_follows_no_configurable_team()
    {
        var module = new ToroSquad.Modules.Volleyball.VolleyballModule();
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("volleyball-admin");
        typeof(ToroSquad.Modules.Volleyball.Application.VolleyballOptions).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Team", StringComparison.OrdinalIgnoreCase) || n.Contains("Country", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_year_is_hard_coded_in_volleyball_code()
    {
        foreach (var file in SourceFiles(Root()))
        {
            var code = string.Join("\n", File.ReadAllLines(file).Select(l => l.Contains("//", StringComparison.Ordinal) ? l[..l.IndexOf("//", StringComparison.Ordinal)] : l)
                .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
            YearLiteral().IsMatch(code).Should().BeFalse($"{Path.GetFileName(file)} must derive seasons from provider data / the clock");
        }
    }

    [Fact]
    public void Volleyball_code_uses_the_injected_clock()
    {
        foreach (var file in SourceFiles(Root()))
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("DateTime.Now", Path.GetFileName(file)).And.NotContain("DateTime.UtcNow", Path.GetFileName(file))
                .And.NotContain("DateTimeOffset.UtcNow", Path.GetFileName(file)).And.NotContain("DateTimeOffset.Now", Path.GetFileName(file));
        }
    }

    [Fact]
    public void Volleyball_localization_catalogs_match_and_every_used_key_exists()
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
        {
            var key = m.Groups[1].Value;
            if (key.EndsWith('.'))
                continue;
            tr.Should().ContainKey(key);
        }

        foreach (var source in new[] { "fivb", "demo", "none" })
            tr.Should().ContainKey("vb.source." + source);
        foreach (var code3 in new[] { "TUR", "ITA", "BRA", "SRB", "POL", "USA", "CHN", "JPN", "NED" })
            tr.Should().ContainKey("vb.country." + code3);

        // Loads together with every other module's catalog (no duplicate keys across modules).
        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Formula1.Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"),
            new LocalizationSource(Volleyball, "ToroSquad.Modules.Volleyball.Localization"),
        ]);
        catalog.Get("tr", "vb.card.set_line", 3, "25", "19").Should().Be("3. Set: 25-19");
    }

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Volleyball");

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static IEnumerable<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"""((?:vb|module\.volleyball|about\.attr)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"\b20[2-9]\d\b")]
    private static partial Regex YearLiteral();
}
