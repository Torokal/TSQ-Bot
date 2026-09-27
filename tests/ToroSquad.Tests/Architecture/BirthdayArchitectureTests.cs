using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Bot;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Birthday;
using ToroSquad.Modules.Birthday.Persistence;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Birthday boundaries: isolated from the other feature modules, no Discord SDK outside Commands, the announcement goes
/// through the outbox and pings nobody, no year is stored, member-facing commands have no option naming another member,
/// registered and off by default like every optional module.
/// </summary>
public sealed partial class BirthdayArchitectureTests
{
    private static readonly Assembly Birthday = typeof(BirthdayModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Birthday).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Birthday_and_other_modules_never_reference_each_other()
    {
        References(Birthday).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Birthday");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.Lfg.LfgModule), typeof(ToroSquad.Modules.Quote.QuoteModule), typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Birthday");
        References(Core).Should().NotContain("ToroSquad.Modules.Birthday");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Birthday");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Birthday.Domain")]
    [InlineData("ToroSquad.Modules.Birthday.Application")]
    [InlineData("ToroSquad.Modules.Birthday.Persistence")]
    public void Only_commands_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns)
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void The_announcement_goes_through_the_outbox_and_pings_only_the_celebrants()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Birthday(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^IMessageTransport$")));
        var code = AllCode();
        // No @everyone/@here, no role pings, no broad allowed_mentions anywhere in the module.
        code.Should().NotContain("EveryoneOnly").And.NotContain("Everyone: true").And.NotContain("AllowedMentionTypes").And.NotContain("AllowedMentions")
            .And.NotContain("MentionEveryone").And.NotContain("new MentionPolicy(");
        // Exactly one user-ping opt-in, in the renderer, with the list the text was built from.
        Regex.Matches(code, @"ExplicitUsers\(").Should().ContainSingle();
        File.ReadAllText(Path.Combine(Root(), "Application", "BirthdayAnnouncementRenderer.cs"))
            .Should().Contain("new OutgoingMessage(text, null, MentionPolicy.ExplicitUsers(named))");
        // Every command reply stays private and ping-free (the base class sends with MentionPolicy.None).
        foreach (var file in new[] { "BirthdayCommands.cs", "BirthdayAdminCommands.cs" })
            File.ReadAllText(Path.Combine(Root(), "Commands", file)).Should().NotMatchRegex(@"\b(RespondAsync|FollowupAsync)\(", file);
    }

    [Fact]
    public void Setting_another_members_birthday_requires_administrator_not_manage_server()
    {
        ToroSquad.Modules.Birthday.Application.BirthdayService.SetForMemberPermission.Should().Be(ToroSquad.Core.Security.GuildPermission.Administrator);
        var service = File.ReadAllText(Path.Combine(Root(), "Application", "BirthdayService.cs"));
        service.Should().Contain("Authorize.Require(actor, actor.GuildId, SetForMemberPermission)");
        service.Should().NotContain("RoleIds").And.NotContain("\"Admin\"").And.NotContain("\"Owner\"");
    }

    [Fact]
    public void No_year_or_birth_date_is_stored()
    {
        var registration = typeof(BirthdayRegistrationEntity).GetProperties().Select(p => p.Name);
        registration.Should().BeEquivalentTo("Id", "GuildId", "UserId", "Day", "Month", "CreatedAt", "UpdatedAt");
    }

    [Fact]
    public void Member_commands_only_take_a_date_never_another_member()
    {
        var commands = File.ReadAllText(Path.Combine(Root(), "Commands", "BirthdayCommands.cs"));
        commands.Should().NotContain("IUser").And.NotContain("IGuildUser").And.NotContain("SocketUser");
        Regex.Matches(commands, @"\[Summary\(""").Should().ContainSingle("only /birthday set has an option: the date");
    }

    [Fact]
    public void Every_birthday_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Birthday.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("birthday", type.Name);
            attribute.AllowWhenDisabled.Should().Be(type.Name == "BirthdayAdminCommands", "only the admin setup works before activation");
        }

        var module = new BirthdayModule();
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("birthday-admin");
        module.ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
        DesignTimeDbContextFactory.AllContributors().Should().ContainSingle(c => c is BirthdayModelContributor);
    }

    [Fact]
    public async Task Module_is_registered_off_by_default_and_its_services_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        var guild = new ToroSquad.Core.GuildId(42);
        await host.InScopeAsync(async sp =>
        {
            sp.GetRequiredService<ModuleRegistry>().TryGet("birthday", out _).Should().BeTrue();
            (await sp.GetRequiredService<IModuleGate>().IsEnabledAsync(guild, BirthdayModule.ModuleIdTyped, CancellationToken.None)).Should().BeFalse();
            sp.GetRequiredService<ToroSquad.Modules.Birthday.Application.BirthdayReconciler>().Should().NotBeNull();
            sp.GetRequiredService<ToroSquad.Modules.Birthday.Application.BirthdayDoctor>().Should().NotBeNull();
            sp.GetServices<IModuleHealthCheck>().Should().Contain(h => h.Module == BirthdayModule.ModuleIdTyped);
        });
    }

    [Fact]
    public void Birthday_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders().Matches(tr[key]).Select(m => m.Value).Order().Should().Equal(Placeholders().Matches(en[key]).Select(m => m.Value).Order(), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));

        foreach (Match m in KeyLiteral().Matches(AllCode()))
            tr.Should().ContainKey(m.Groups[1].Value);
        tr["birthday.set.reminder"].Should().Be("🎂 Lütfen gerçek doğum gününüzü giriniz. Bizim için önemli.");
        tr["birthday.show.value"].Should().Be("🎂 Kayıtlı doğum günün: {0}");
        tr["birthday.set.saved"].Should().Be("🎂 Doğum günün {0} olarak kaydedildi.");

        // Loads together with every other module's catalog (no duplicate keys across modules).
        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Formula1.Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Volleyball.VolleyballModule).Assembly, "ToroSquad.Modules.Volleyball.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Live.LiveModule).Assembly, "ToroSquad.Modules.Live.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Lfg.LfgModule).Assembly, "ToroSquad.Modules.Lfg.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Quote.QuoteModule).Assembly, "ToroSquad.Modules.Quote.Localization"),
            new LocalizationSource(Birthday, "ToroSquad.Modules.Birthday.Localization"),
        ]);
        catalog.Get("tr", "birthday.date", 14, catalog.Get("tr", "birthday.month.3")).Should().Be("14 Mart");
        catalog.Get("en", "birthday.date", 14, catalog.Get("en", "birthday.month.3")).Should().Be("March 14");
    }

    private static string AllCode() => string.Join("\n", Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"))
        .Select(File.ReadAllText));

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Birthday");

    [GeneratedRegex(@"""((?:birthday|module\.birthday)\.[a-z0-9_.\-]+[a-z0-9_])""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholders();
}
