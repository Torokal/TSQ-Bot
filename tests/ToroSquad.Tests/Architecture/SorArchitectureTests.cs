using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Ai;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Sor;
using ToroSquad.Modules.Sor.Application;
using ToroSquad.Modules.Sor.Commands;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Sor's boundaries (the shared AI layer has its own tests in OpenCodeGoTransportTests): Sor
/// never references TSQ Özet (or any other module); only Sor's Commands use the Discord SDK; one generation per question
/// in the service; logs carry no text; replies never ping; the module is off by default and gated; its commands, admin
/// operations, localization, privacy contributor and tables are registered.
/// </summary>
public sealed partial class SorArchitectureTests
{
    private static readonly Assembly Sor = typeof(SorModule).Assembly;
    private static readonly Assembly Ai = typeof(IAiChatClient).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Ai, Sor).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Root(string project) => Path.Combine(CommandManifestTests.RepoRoot(), "src", project);

    private static IEnumerable<string> SourceFiles(string project) => Directory.GetFiles(Root(project), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static string Code(string project) => string.Join("\n", SourceFiles(project).Select(File.ReadAllText));

    [Fact]
    public void Sor_is_isolated_from_other_modules_and_only_its_commands_use_the_discord_sdk()
    {
        References(Sor).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Sor",
            "never TSQ Özet or any other feature module");
        foreach (var other in new[] { typeof(ToroSquad.Modules.Summary.SummaryModule), typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Birthday.BirthdayModule) })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Sor");
        References(Core).Should().NotContain("ToroSquad.Ai");
        References(Sor).Should().Contain("ToroSquad.Ai", "the provider is reached through the shared layer only");
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Sor\.(Domain|Application|Persistence)$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Sor\.(Domain|Application|Persistence|Commands)$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(HttpClient|HttpRequestMessage)$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Sor(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|IMemoryCache)$")));

        // No member list, presence or new intent: the gateway stays Guilds-only.
        GatewayBotService.CreateSocketConfig().GatewayIntents.Should().Be(GatewayIntents.Guilds);
        var code = Code("ToroSquad.Modules.Sor");
        code.Should().NotContain("GatewayIntents.").And.NotContain("DownloadUsersAsync").And.NotContain("GetUsersAsync").And.NotContain("MessageReceived");
    }

    [Fact]
    public void One_question_is_one_generation_without_retry_fallback_or_extra_inference()
    {
        var service = File.ReadAllText(Path.Combine(Root("ToroSquad.Modules.Sor"), "Application", "SorService.cs"));
        Regex.Matches(service, @"\bai\.CompleteAsync\(").Should().ContainSingle();
        service.Should().NotMatchRegex(@"\b(while|for|foreach|do)\b[^;{]*\{[^}]*CompleteAsync", "a request is never inside a loop");
        Regex.Matches(Code("ToroSquad.Modules.Sor"), @"\.CompleteAsync\(").Should().ContainSingle("no extraction, summary, review or repair call anywhere");
        Code("ToroSquad.Modules.Sor").Should().NotMatchRegex("(?i)fallback ?model|FallbackModel|RetryCount|MaxRetries|response_format|json_schema");
        typeof(SorOptions).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Fallback", StringComparison.Ordinal) || n.Contains("Key", StringComparison.Ordinal));
    }

    [Fact]
    public void Replies_never_ping_and_logs_never_carry_text()
    {
        foreach (var file in new[] { "SorCommands.cs", "SorMemoryCommands.cs" })
        {
            var commands = File.ReadAllText(Path.Combine(Root("ToroSquad.Modules.Sor"), "Commands", file));
            commands.Should().NotMatchRegex(@"\bRespondAsync\(", "replies go through ToroInteractionModule or set NoPings");
            Regex.Count(commands, @"\bFollowupAsync\(").Should().Be(Regex.Count(commands, @"allowedMentions: NoPings"), file);
            Regex.Count(commands, @"m\.AllowedMentions = NoPings;").Should().BeGreaterThanOrEqualTo(Regex.Count(commands, @"ModifyOriginalResponseAsync\("), file);
        }

        var code = Code("ToroSquad.Modules.Sor");
        code.Should().NotContain("MentionPolicy.EveryoneOnly").And.NotContain("ExplicitUsers");
        foreach (Match call in Regex.Matches(code, @"\b(logger\.Log\w+)\((?<args>[^;]*)\);"))
            Regex.Replace(call.Groups["args"].Value, @"""[^""]*""", "\"\"").Should().NotMatchRegex(@"\b(question|Question|answer|Answer|Text|prompt|Messages|Content|AskerName|DisplayName|note)\b", call.Value);
    }

    [Fact]
    public void Every_sor_interaction_class_is_gated_and_the_module_is_off_by_default()
    {
        foreach (var type in Sor.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("sor", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new SorModule();
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("tsq-admin sor");
        module.InteractionModuleTypes.Should().Equal(typeof(SorCommands), typeof(SorMemoryCommands));
        module.ValidateConfiguration(new ConfigurationBuilder().Build()).Should().BeEmpty();
        new SorModule().ValidateConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sor:Model"] = "a b",
            [OpenCodeGo.ApiKeyVariable] = "sk-should-never-appear-0123456789",
        }).Build()).Should().ContainSingle().Which.Should().NotContain("sk-should-never-appear");
    }

    [Fact]
    public async Task Services_resolve_with_the_shared_key_and_client_and_privacy_includes_sor()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InScopeAsync(async sp =>
        {
            sp.GetRequiredKeyedService<IAiChatClient>(SorModule.ModuleIdValue).Should().BeOfType<OpenCodeGoChatClient>();
            sp.GetServices<OpenCodeGoApiKey>().Should().ContainSingle("one key for every OpenCode Go feature");
            sp.GetRequiredService<SorService>().Should().NotBeNull();
            sp.GetServices<IUserDataContributor>().Should().ContainSingle(c => c.Module == SorModule.ModuleIdTyped);
            sp.GetServices<IModuleHealthCheck>().Should().ContainSingle(h => h.Module == SorModule.ModuleIdTyped);
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(SorModule.HttpClientName);
            client.BaseAddress.Should().Be(new Uri(OpenCodeGo.DefaultBaseUrl));
            client.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("SorModule");
            var modules = sp.GetRequiredService<ModuleManagementService>();
            (await modules.ListAsync(SorKit.Guild, CancellationToken.None)).Single(m => m.Descriptor.Id == SorModule.ModuleIdTyped).Enabled.Should().BeFalse();
        });
    }

    [Fact]
    public async Task Manifest_has_sor_and_hafizam_as_public_guild_commands_with_turkish_descriptions()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();
        manifest.Commands.Where(c => c.OwnerModule == "sor").Select(c => c.Name).Should().BeEquivalentTo("sor", "hafizam");

        var sor = manifest.Find("sor")!;
        sor.DefaultMemberPermissions.Should().BeNull();
        sor.Contexts.Should().Equal(0);
        sor.Options.Select(o => o.Name).Should().Equal("soru", "gizli", "yeni", "kisi");
        sor.Options[0].Required.Should().BeTrue();
        sor.Options.Skip(1).Should().OnlyContain(o => !o.Required);
        sor.DescriptionLocalizations["tr"].Should().Be("TSQ Sor'a istediğini sor.");
        manifest.Find("hafizam")!.Options.Should().BeEmpty();
    }

    [Fact]
    public void Localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root("ToroSquad.Modules.Sor"), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Regex.Matches(tr[key], @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal).Should()
                .Equal(Regex.Matches(en[key], @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));
        tr["sor.first_use"].Should().Be("Sor, bu sunucuda yaptığın sohbetleri kısa süre tutar ve uygun tercihlerini hatırlayabilir. Hafızam bölümünden görebilir, kapatabilir veya silebilirsin.");

        foreach (Match m in KeyLiteral().Matches(Code("ToroSquad.Modules.Sor")))
            tr.Should().ContainKey(m.Groups[1].Value);
        foreach (var operation in SorAdminOperations.Definition.Operations)
            tr.Should().ContainKey("admin.sor." + operation.Id);

        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Summary.SummaryModule).Assembly, "ToroSquad.Modules.Summary.Localization"),
            new LocalizationSource(Sor, "ToroSquad.Modules.Sor.Localization"),
        ]);
        catalog.Get("tr", "sor.cooldown", 5).Should().Contain("5");
    }

    [Fact]
    public void Sor_keeps_its_own_tables_and_never_reads_other_modules_data()
    {
        var code = Code("ToroSquad.Modules.Sor");
        Regex.Matches(code, @"Set<(\w+)>\(").Select(m => m.Groups[1].Value).Distinct().Should().OnlyContain(n => n.StartsWith("Sor", StringComparison.Ordinal));
        code.Should().NotContain("db.Outbox").And.NotContain("ToroSquad.Modules.Summary");
        Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "Migrations"), "*_SorModule.cs").Should().ContainSingle();
    }

    [GeneratedRegex(@"""((?:sor|module\.sor|admin\.sor)\.[a-z0-9_.\-]*[a-z0-9_\-])""")] // a "prefix." built at run time is not a key
    private static partial Regex KeyLiteral();
}
