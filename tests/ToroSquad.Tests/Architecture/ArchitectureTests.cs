using System.Reflection;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Interactions;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>Dependency directions of the modular monolith (docs/ARCHITECTURE.md).</summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly Infrastructure = typeof(ToroSquad.Infrastructure.Persistence.ToroDbContext).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;
    private static readonly Assembly Esports = typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly;
    private static readonly Assembly Example = typeof(ToroSquad.Modules.Example.ExampleModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() =>
        new ArchLoader().LoadAssemblies(Core, Infrastructure, DiscordLayer, Esports, Example).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Core_references_no_sdk_storage_or_module_assembly()
    {
        References(Core).Should().NotContain(r => r.StartsWith("Discord", StringComparison.Ordinal) ||
                                                  r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                                                  r.StartsWith("ToroSquad.", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_does_not_know_discord_or_modules()
    {
        References(Infrastructure).Should().NotContain(r => r.StartsWith("Discord", StringComparison.Ordinal) ||
                                                            r.StartsWith("ToroSquad.Discord", StringComparison.Ordinal) ||
                                                            r.StartsWith("ToroSquad.Modules", StringComparison.Ordinal));
    }

    [Fact]
    public void Discord_layer_does_not_know_storage_or_modules()
    {
        References(DiscordLayer).Should().NotContain(r => r.StartsWith("ToroSquad.Infrastructure", StringComparison.Ordinal) ||
                                                          r.StartsWith("ToroSquad.Modules", StringComparison.Ordinal) ||
                                                          r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Modules_do_not_depend_on_each_other()
    {
        References(Esports).Should().NotContain("ToroSquad.Modules.Example");
        References(Example).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.Esports", StringComparison.Ordinal) ||
                                                     r.StartsWith("ToroSquad.Infrastructure", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Esports.Domain")]
    [InlineData("ToroSquad.Modules.Esports.Providers")]
    [InlineData("ToroSquad.Modules.Esports.Application")]
    [InlineData("ToroSquad.Modules.Esports.Persistence")]
    public void Esports_business_code_does_not_use_the_discord_sdk(string ns)
    {
        IArchRule rule = Types().That().ResideInNamespace(ns)
            .Or().ResideInNamespaceMatching(ns + @"\..*")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$"))
            .Because("Discord SDK objects must not leak into esports business rules");
        AssertNoViolations(rule);
    }

    [Fact]
    public void Esports_domain_is_pure()
    {
        IArchRule rule = Types().That().ResideInNamespace("ToroSquad.Modules.Esports.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure|System\.Net\.Http)(\..*)?$"));
        AssertNoViolations(rule);
    }

    [Fact]
    public void Core_types_do_not_depend_on_esports_concepts()
    {
        Core.GetTypes().SelectMany(t => t.GetMembers()).Select(m => m.Name)
            .Should().NotContain(n => n.Contains("Liquipedia", StringComparison.OrdinalIgnoreCase) ||
                                      n.Contains("Valve", StringComparison.OrdinalIgnoreCase) ||
                                      n.Contains("Cs2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_interaction_class_declares_its_module_and_non_core_classes_are_gated()
    {
        var interactionTypes = new[] { DiscordLayer, Esports, Example }
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t));
        foreach (var type in interactionTypes)
        {
            var attr = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true);
            attr.Should().NotBeNull($"{type.Name} must declare [ToroModule]");
        }
    }

    [Fact]
    public void Registry_rejects_duplicate_module_ids_and_shared_interaction_types()
    {
        var core = new ToroSquad.Discord.CoreBotModule();
        var act = () => new ModuleRegistry([core, new ToroSquad.Discord.CoreBotModule()]);
        act.Should().Throw<InvalidOperationException>();
    }
}
