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
/// TSQ LFG boundaries: isolated from other feature modules, no Discord SDK in business code, a pure domain, one generic
/// listing model (no game-specific type or column), the bot only ever EDITS its own cards (never sends through the
/// transport, never stages outbox notifications), injected clock only, matching localization catalogs.
/// </summary>
public sealed partial class LfgArchitectureTests
{
    private static readonly Assembly Lfg = typeof(ToroSquad.Modules.Lfg.LfgModule).Assembly;
    private static readonly Assembly Core = typeof(ToroSquad.Core.Modules.IToroModule).Assembly;
    private static readonly Assembly Infrastructure = typeof(ToroSquad.Infrastructure.Persistence.ToroDbContext).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() =>
        new ArchLoader().LoadAssemblies(Core, Infrastructure, DiscordLayer, Lfg).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Lfg_and_other_feature_modules_never_reference_each_other()
    {
        References(Lfg).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Lfg");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Lfg");
        References(Core).Should().NotContain("ToroSquad.Modules.Lfg");
        References(Infrastructure).Should().NotContain("ToroSquad.Modules.Lfg");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Lfg");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Lfg.Domain")]
    [InlineData("ToroSquad.Modules.Lfg.Application")]
    [InlineData("ToroSquad.Modules.Lfg.Persistence")]
    public void Lfg_business_code_does_not_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns)
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Lfg_domain_is_pure() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Lfg.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure|System\.Net\.Http|ToroSquad\.Modules\.Lfg\.(Application|Persistence|Commands))(\..*)?$")));

    [Fact]
    public void Only_the_card_sync_touches_the_transport_and_it_only_edits()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Lfg(\..*)?$")
            .And().DoNotHaveNameMatching(@"^LfgCardSync$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^IMessageTransport$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Lfg(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(INotificationOutbox|NotificationRequest)$")));

        var sync = File.ReadAllText(Path.Combine(Root(), "Application", "LfgCardSync.cs"));
        sync.Should().Contain("transport.EditAsync(").And.NotContain("transport.SendAsync(");
    }

    [Fact]
    public void There_is_one_generic_listing_model_and_nothing_game_specific()
    {
        var names = Lfg.GetTypes().Where(t => !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .SelectMany(t => new[] { t.Name }.Concat(t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => t.Name + "." + p.Name)))
            .ToList();
        names.Should().NotContain(n => GameSpecific().IsMatch(n));

        var entities = new[] { typeof(ToroSquad.Modules.Lfg.Persistence.LfgListingEntity), typeof(ToroSquad.Modules.Lfg.Persistence.LfgParticipantEntity) };
        entities.SelectMany(e => e.GetProperties().Select(p => p.Name)).Should().BeEquivalentTo(
            "Id", "GuildId", "ChannelId", "MessageId", "OwnerUserId", "GameName", "Details", "MaxPlayers", "Status", "CreatedAt", "ExpiresAt", "ClosedAt",
            "ClosedByUserId", "CardStale", "CardSyncAttempts", "Version", "Participants", "ListingId", "UserId", "JoinedAt");
    }

    [Fact]
    public void Every_lfg_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Lfg.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
            type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!.ModuleId.Should().Be("lfg", type.Name);
        typeof(ToroSquad.Modules.Lfg.Commands.LfgCommands).GetCustomAttribute<ToroModuleAttribute>()!.AllowWhenDisabled.Should().BeFalse();
        var module = new ToroSquad.Modules.Lfg.LfgModule();
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("lfg-admin");
    }

    [Fact]
    public void Lfg_code_uses_the_injected_clock()
    {
        foreach (var file in SourceFiles(Root()))
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("DateTime.Now", Path.GetFileName(file)).And.NotContain("DateTime.UtcNow", Path.GetFileName(file))
                .And.NotContain("DateTimeOffset.UtcNow", Path.GetFileName(file)).And.NotContain("DateTimeOffset.Now", Path.GetFileName(file));
        }
    }

    [Fact]
    public void Lfg_configuration_defaults_are_the_documented_safety_limits()
    {
        var bot = Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(bot));
        var lfg = doc.RootElement.GetProperty("Lfg");
        lfg.GetProperty("DefaultExpirationMinutes").GetInt32().Should().Be(120);
        lfg.GetProperty("MaxActiveListingsPerUser").GetInt32().Should().Be(2);
        lfg.GetProperty("MaxPlayersPerListing").GetInt32().Should().Be(20);
        new ToroSquad.Modules.Lfg.LfgModule().ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
    }

    [Fact]
    public void Lfg_localization_catalogs_match_and_every_used_key_exists()
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
            new LocalizationSource(typeof(ToroSquad.Modules.Live.LiveModule).Assembly, "ToroSquad.Modules.Live.Localization"),
            new LocalizationSource(Lfg, "ToroSquad.Modules.Lfg.Localization"),
        ]);
        catalog.Get("tr", "lfg.create.limit", 2).Should().Be("Aynı anda en fazla 2 aktif ekip ilanı açabilirsin.");
        catalog.Get("tr", "lfg.leave.owner").Should().Be("İlan sahibi ekipten ayrılamaz. İstersen ilanı kapatabilirsin.");
    }

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Lfg");

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static IEnumerable<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"""((?:lfg|module\.lfg)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"(?i)(cs2|csgo|counter|warcraft|wow|valheim|deadlock|league|minecraft|premier|rank|rating|boss|dungeon|mythic|tank|healer|game(profile|specific|metadata))")]
    private static partial Regex GameSpecific();
}
