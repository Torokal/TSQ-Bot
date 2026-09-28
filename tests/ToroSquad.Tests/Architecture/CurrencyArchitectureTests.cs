using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Currency;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Commands;
using ToroSquad.Modules.Currency.Providers;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Döviz &amp; Altın boundaries and integration: an isolated, stateless module (no tables, no migration, no background
/// work, no outbox, no secrets, no AI), no Discord SDK outside Commands, registered and gated through /modules like every
/// optional module (off by default), and /dolar, /euro, /altın in the manifest — /altın with its Turkish ı.
/// </summary>
public sealed partial class CurrencyArchitectureTests
{
    private static readonly Assembly Currency = typeof(CurrencyModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Currency).Build());

    private static readonly GuildId Guild = new(42);

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Currency");

    private static IEnumerable<string> SourceFiles() => Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    [Fact]
    public void Currency_is_isolated_and_has_no_storage_no_ai_and_no_new_packages()
    {
        References(Currency).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Currency");
        References(Currency).Should().NotContain(r => r == "ToroSquad.Infrastructure" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
            "prices are never stored: no tables, no migration");
        References(Currency).Should().NotContain(r => Regex.IsMatch(r, "OpenAI|Anthropic|SemanticKernel|Polly|Caching", RegexOptions.IgnoreCase),
            "deterministic, BCL only: no AI, no resilience or cache packages");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.Lfg.LfgModule), typeof(ToroSquad.Modules.Quote.QuoteModule),
                     typeof(ToroSquad.Modules.Birthday.BirthdayModule), typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Currency");
        References(Core).Should().NotContain("ToroSquad.Modules.Currency");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Currency");

        var csproj = File.ReadAllText(Path.Combine(Root(), "ToroSquad.Modules.Currency.csproj"));
        Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
            .Should().BeEquivalentTo("Microsoft.Extensions.Http", "Microsoft.Extensions.Options.ConfigurationExtensions");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Currency.Domain")]
    [InlineData("ToroSquad.Modules.Currency.Providers")]
    [InlineData("ToroSquad.Modules.Currency.Application")]
    public void Only_commands_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns)
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Commands_never_see_provider_payloads() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Currency.Commands")
            .Should().NotDependOnAny(Types().That().ResideInNamespace("ToroSquad.Modules.Currency.Providers")));

    [Fact]
    public void Nothing_runs_in_the_background_and_nothing_is_sent_outside_the_interaction()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Currency(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|NotificationRequest|IHostedService|BackgroundService|PeriodicTimer)$")));
        Currency.GetTypes().Should().NotContain(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t));
        typeof(CurrencyModule).GetMethod("AddBackgroundJobs").Should().BeNull("no polling: prices are fetched when a command asks");
    }

    [Fact]
    public void Http_goes_through_the_factory_to_fixed_endpoints_only()
    {
        foreach (var file in SourceFiles())
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("new HttpClient(", Path.GetFileName(file));
            code.Should().NotMatchRegex(@"\b(float|double)\b", Path.GetFileName(file) + ": money is decimal");
        }

        // The only request targets: three configured base URLs + fixed relative paths. Nothing a user types reaches a URL.
        new[] { MarketDataset.AltinkaynakCurrency, MarketDataset.AltinkaynakGold, MarketDataset.Tcmb, MarketDataset.Truncgil }
            .Select(d => MarketDataClient.Endpoint(d).Path).Should().Equal("Currency", "Gold", "today.xml", "today.json");
        typeof(CurrencyCommands).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().OnlyContain(m => m.GetParameters().Length == 0, "the commands take no arguments");
    }

    [Fact]
    public void Configuration_has_no_secrets_and_production_defaults_validate()
    {
        var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "appsettings.json")));
        var section = settings.RootElement.GetProperty("Currency");
        section.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "AltinkaynakBaseUrl", "TcmbBaseUrl", "TruncgilBaseUrl", "TimeoutSeconds", "FreshSeconds", "FallbackFreshSeconds", "StaleMaxMinutes");
        section.GetRawText().Should().NotContainAny("Key", "Token", "Secret", "Password");

        var module = new CurrencyModule();
        module.ValidateConfiguration(new ConfigurationBuilder().Build()).Should().BeEmpty();
        var fromFile = new ConfigurationBuilder().AddJsonFile(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "appsettings.json")).Build();
        module.ValidateConfiguration(fromFile).Should().BeEmpty();
        var typo = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Currency:TcmbBaseUrl"] = "htps://www.tcmb.gov.tr/kurlar/" }).Build();
        module.ValidateConfiguration(typo).Should().ContainSingle().Which.Should().Contain("Currency:TcmbBaseUrl").And.Contain("htps://");
    }

    [Fact]
    public void Every_currency_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Currency.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("currency", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new CurrencyModule();
        module.Descriptor.Id.Value.Should().Be("currency");
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().BeEmpty();
        module.InteractionModuleTypes.Should().Equal(typeof(CurrencyCommands));
    }

    [Fact]
    public async Task Module_is_listed_in_modules_off_by_default_gated_and_its_services_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        var gated = new ToroModuleAttribute(CurrencyModule.ModuleIdValue);
        await host.InScopeAsync(async sp =>
        {
            var localizer = sp.GetRequiredService<ILocalizer>();
            var modules = sp.GetRequiredService<ModuleManagementService>();
            var listed = (await modules.ListAsync(Guild, CancellationToken.None)).Single(m => m.Descriptor.Id == CurrencyModule.ModuleIdTyped);
            listed.Enabled.Should().BeFalse();
            localizer.Get("tr", listed.Descriptor.NameKey).Should().Be("Döviz & Altın");
            localizer.Get("tr", listed.Descriptor.DescriptionKey).Should().Be("Güncel dolar, euro ve gram altın alış/satış fiyatları.");

            // Disabled: the command precondition refuses, exactly like every other optional module.
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);

            (await modules.SetEnabledAsync(TestHost.Admin(Guild), "currency", true, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await sp.GetRequiredService<IModuleGate>().IsEnabledAsync(Guild, CurrencyModule.ModuleIdTyped, CancellationToken.None)).Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).IsSuccess.Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(new GuildId(43)), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError, "per guild");

            (await modules.SetEnabledAsync(TestHost.Admin(Guild), "currency", false, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);

            sp.GetRequiredService<IMarketDataSource>().Should().BeOfType<MarketDataClient>();
            sp.GetRequiredService<MarketQuoteService>().LastStatus.Should().BeNull("building the host fetched nothing");
            sp.GetRequiredService<CurrencyCardRenderer>().Should().NotBeNull();
            var health = sp.GetServices<IModuleHealthCheck>().Single(h => h.Module == CurrencyModule.ModuleIdTyped);
            (await health.CheckAsync(CancellationToken.None)).Entries.Should().ContainSingle()
                .Which.Should().Be(new HealthEntry(CurrencyHealthCheck.Component, HealthState.Healthy, "currency.health.idle"));
        });
    }

    [Fact]
    public async Task Health_reports_the_last_answer_without_calling_a_provider()
    {
        var (service, source, _) = CurrencyTestKit.OverFake();
        var health = new CurrencyHealthCheck(service);
        await health.CheckAsync(CancellationToken.None);
        source.TotalCalls.Should().Be(0);

        source.Fail(ToroSquad.Modules.Currency.Providers.MarketDataset.AltinkaynakCurrency);
        await service.GetQuoteAsync(ToroSquad.Modules.Currency.Domain.MarketInstrument.Usd, CancellationToken.None);
        var calls = source.TotalCalls;
        var entry = (await health.CheckAsync(CancellationToken.None)).Entries.Single();
        entry.State.Should().Be(HealthState.Degraded);
        entry.DetailKey.Should().Be("currency.health.fallback");
        CurrencyTestKit.Localizer().Get("tr", entry.DetailKey, [.. entry.Args!]).Should().StartWith("Son sorgu yedek kaynaktan (TCMB — Gösterge Kuru)");
        source.TotalCalls.Should().Be(calls);
    }

    [Fact]
    public async Task Manifest_has_dolar_euro_and_altin_as_public_guild_commands_without_options()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();

        var expected = new Dictionary<string, string>
        {
            ["dolar"] = "Güncel dolar alış ve satış kuru (TL)",
            ["euro"] = "Güncel euro alış ve satış kuru (TL)",
            ["altın"] = "Güncel gram altın alış ve satış fiyatı (TL)",
        };
        foreach (var (name, tr) in expected)
        {
            var command = manifest.Find(name)!;
            command.Should().NotBeNull(name);
            command.OwnerModule.Should().Be("currency");
            command.Options.Should().BeEmpty();
            command.DefaultMemberPermissions.Should().BeNull("for everyone");
            command.Contexts.Should().Equal(0);
            command.DescriptionLocalizations["tr"].Should().Be(tr);
        }

        manifest.Find("altin").Should().BeNull("the name keeps its Turkish ı; it is not silently transliterated");
        manifest.Commands.Where(c => c.OwnerModule == "currency").Select(c => c.Name).Should().BeEquivalentTo("dolar", "euro", "altın");
    }

    [Fact]
    public async Task Altin_passes_discord_net_registration_validation_and_the_manifest_rules()
    {
        // Discord: names match ^[-_\p{L}\p{N}\p{sc=Deva}\p{sc=Thai}]{1,32}$ using the lowercase variant of each letter; the
        // dotless ı (U+0131) is itself a lowercase letter. Discord.Net 3.20 checks the same before sending the request.
        "altın".Should().Contain("ı");
        char.IsLower('ı').Should().BeTrue();

        await using var host = await TestHost.CreateAsync();
        var manifest = await host.Services.GetRequiredService<InteractionHost>().InitializeAsync(host.Services);
        var properties = DiscordCommandRegistrar.ToProperties(manifest.Find("altın")!);
        properties.Name.Value.Should().Be("altın");

        new SlashCommandBuilder().WithName("altın").WithDescription("x").Build().Name.Value.Should().Be("altın");
        var uppercase = () => new SlashCommandBuilder().WithName("Altın").WithDescription("x").Build();
        uppercase.Should().Throw<FormatException>("Discord.Net enforces the lowercase rule: the check above is real");

        var tr = new Dictionary<string, string> { ["tr"] = "açıklama" };
        ManifestCommand Named(string name) => new(name, "desc", tr, [], null, [0], [0], false, "currency");
        CommandManifestValidator.Validate(new CommandManifest([Named("altın"), Named("dolar"), Named("euro")], []), new HashSet<string>()).Should().BeEmpty();
        foreach (var bad in new[] { "Altın", "altın!", "alt ın", "ALTIN", "", new string('a', 33) })
            CommandManifestValidator.Validate(new CommandManifest([Named(bad)], []), new HashSet<string>())
                .Should().Contain(e => e.Contains("invalid name", StringComparison.Ordinal), bad);
    }

    [Fact]
    public void Currency_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));
        tr.Should().ContainKeys("cmd.dolar", "cmd.euro", "cmd.altın");

        var code = string.Join("\n", SourceFiles().Select(File.ReadAllText));
        foreach (Match m in KeyLiteral().Matches(code))
            tr.Should().ContainKey(m.Groups[1].Value);
        tr["module.currency.name"].Should().Be("Döviz & Altın");
        tr["currency.source.tcmb"].Should().Be("TCMB — Gösterge Kuru");

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
            new LocalizationSource(typeof(ToroSquad.Modules.Birthday.BirthdayModule).Assembly, "ToroSquad.Modules.Birthday.Localization"),
            new LocalizationSource(Currency, "ToroSquad.Modules.Currency.Localization"),
        ]);
        catalog.Get("en", "currency.instrument.gram_gold").Should().Be("Gram Gold");
    }

    private static IInteractionContext Context(GuildId guild)
    {
        var guildFake = InterfaceFake.Create<IGuild>(new() { ["Id"] = guild.Value, ["OwnerId"] = 1UL });
        var user = InterfaceFake.Create<IGuildUser>(new()
        {
            ["Id"] = 9UL,
            ["GuildPermissions"] = new GuildPermissions(0),
            ["RoleIds"] = (IReadOnlyCollection<ulong>)Array.Empty<ulong>(),
        });
        return InterfaceFake.Create<IInteractionContext>(new() { ["Guild"] = guildFake, ["User"] = user });
    }

    [GeneratedRegex(@"""((?:currency|module\.currency)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();
}
