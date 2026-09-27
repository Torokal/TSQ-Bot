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
/// TSQ Live boundaries: isolated from other feature modules, no Discord SDK in business code, commands never reach a
/// provider or the network, providers never reach Discord or the outbox, only the planner stages notifications — and the
/// @everyone opt-in exists in exactly one place (the live card renderer). Plus: injected clock only, no secrets in
/// configuration files, matching localization catalogs.
/// </summary>
public sealed partial class LiveArchitectureTests
{
    private static readonly Assembly Live = typeof(ToroSquad.Modules.Live.LiveModule).Assembly;
    private static readonly Assembly Core = typeof(ToroSquad.Core.Modules.IToroModule).Assembly;
    private static readonly Assembly Infrastructure = typeof(ToroSquad.Infrastructure.Persistence.ToroDbContext).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() =>
        new ArchLoader().LoadAssemblies(Core, Infrastructure, DiscordLayer, Live).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Live_and_other_feature_modules_never_reference_each_other()
    {
        References(Live).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Live");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Live");
        References(Core).Should().NotContain("ToroSquad.Modules.Live");
        References(Infrastructure).Should().NotContain("ToroSquad.Modules.Live");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Live");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Live.Domain")]
    [InlineData("ToroSquad.Modules.Live.Providers")]
    [InlineData("ToroSquad.Modules.Live.Application")]
    [InlineData("ToroSquad.Modules.Live.Persistence")]
    public void Live_business_code_does_not_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns).Or().ResideInNamespaceMatching(ns + @"\..*")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Live_domain_is_pure() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Live.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure|System\.Net\.Http|ToroSquad\.Modules\.Live\.(Providers|Application|Persistence|Commands))(\..*)?$")));

    [Fact]
    public void Commands_never_reach_a_provider_or_the_network() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Live.Commands")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(ToroSquad\.Modules\.Live\.Providers|System\.Net\.Http)(\..*)?$"))
            .AndShould().NotDependOnAny(Types().That().HaveNameMatching(@"^(LivePoller|LiveCoordinator|LiveAnnouncementPlanner)$")));

    [Fact]
    public void Providers_never_talk_to_discord_or_the_outbox_and_only_the_planner_stages()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Live\.Providers(\..*)?$").Should().NotDependOnAny(
            Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|OutgoingMessage|NotificationRequest|IDeliveryPolicy|MentionPolicy)$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Live(\..*)?$")
            .And().DoNotHaveNameMatching(@"^LiveAnnouncementPlanner$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^INotificationOutbox$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Live(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^IMessageTransport$")));
    }

    [Fact]
    public void The_everyone_mention_opt_in_exists_only_in_the_live_card_renderer()
    {
        var src = Path.Combine(CommandManifestTests.RepoRoot(), "src");
        var allowed = new[]
        {
            Path.Combine("ToroSquad.Core", "Messaging", "OutgoingMessage.cs"), // the definition
            Path.Combine("ToroSquad.Discord", "Transport", "DiscordConversions.cs"), // the wire mapping
            Path.Combine("ToroSquad.Modules.Live", "Application", "LiveCardRenderer.cs"), // the only producer
        };
        foreach (var file in SourceFiles(src))
        {
            var relative = Path.GetRelativePath(src, file);
            if (allowed.Contains(relative))
                continue;
            var code = File.ReadAllText(file);
            EveryoneOptIn().IsMatch(code).Should().BeFalse($"{relative} must not opt in to @everyone pings");
        }

        EveryoneOptIn().IsMatch(File.ReadAllText(Path.Combine(src, allowed[2]))).Should().BeTrue();
    }

    [Fact]
    public void Every_live_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Live.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
            type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!.ModuleId.Should().Be("live", type.Name);
        var module = new ToroSquad.Modules.Live.LiveModule();
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("live-admin");
    }

    [Fact]
    public void Live_code_uses_the_injected_clock()
    {
        foreach (var file in SourceFiles(Root()))
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("DateTime.Now", Path.GetFileName(file)).And.NotContain("DateTime.UtcNow", Path.GetFileName(file))
                .And.NotContain("DateTimeOffset.UtcNow", Path.GetFileName(file)).And.NotContain("DateTimeOffset.Now", Path.GetFileName(file));
        }
    }

    [Fact]
    public void Configuration_files_contain_no_live_credentials()
    {
        var bot = Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot");
        foreach (var file in Directory.GetFiles(bot, "appsettings*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("Live", out var live))
                continue;
            foreach (var platform in new[] { "Twitch", "Kick" })
            {
                if (!live.TryGetProperty(platform, out var section))
                    continue;
                section.TryGetProperty("ClientId", out _).Should().BeFalse($"{Path.GetFileName(file)}: Live:{platform}:ClientId belongs in environment variables");
                section.TryGetProperty("ClientSecret", out _).Should().BeFalse($"{Path.GetFileName(file)}: Live:{platform}:ClientSecret belongs in environment variables");
            }

            live.GetProperty("Enabled").GetBoolean().Should().BeFalse("TSQ Live is off until the operator configures it");
            live.GetProperty("AnnounceExistingLiveOnBootstrap").GetBoolean().Should().BeFalse();
        }

        ToroSquad.Infrastructure.InfrastructureServiceCollectionExtensions.SecretConfigurationKeys.Should()
            .Contain(["Live:Twitch:ClientId", "Live:Twitch:ClientSecret", "Live:Kick:ClientId", "Live:Kick:ClientSecret"]);
    }

    [Fact]
    public void Live_localization_catalogs_match_and_every_used_key_exists()
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

        // Loads together with every other module's catalog (no duplicate keys across modules).
        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Formula1.Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Volleyball.VolleyballModule).Assembly, "ToroSquad.Modules.Volleyball.Localization"),
            new LocalizationSource(Live, "ToroSquad.Modules.Live.Localization"),
        ]);
        catalog.Get("tr", "live.card.content_everyone", "LORDTORO").Should().Be("@everyone 🔴 **LORDTORO** yayında!");
    }

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Live");

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static IEnumerable<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"""((?:live|module\.live)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"EveryoneOnly|Everyone\s*:\s*true|Everyone\s*=\s*true|AllowedMentionTypes\.Everyone")]
    private static partial Regex EveryoneOptIn();
}
