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
using ToroSquad.Modules.Summary;
using ToroSquad.Modules.Summary.Application;
using ToroSquad.Modules.Summary.Commands;
using ToroSquad.Modules.Summary.Providers;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Özet boundaries and integration: a stateless module (no tables, no migration, no background work, no message listener
/// or cache, gateway intents unchanged), isolated from the other feature modules, the Discord SDK only in Commands, HTTP only
/// in the one provider client, no retry/resilience library and no second model anywhere; replies never ping; logs never carry
/// text; the key is a redacted secret outside appsettings; registered and gated through /modules like every optional module
/// (off by default), and /ozetle in the manifest as a public guild command with its Turkish description.
/// </summary>
public sealed partial class SummaryArchitectureTests
{
    private static readonly Assembly Summary = typeof(SummaryModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Summary).Build());

    private static readonly GuildId Guild = new(42);

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Summary");

    private static IEnumerable<string> SourceFiles() => Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static string AllCode() => string.Join("\n", SourceFiles().Select(File.ReadAllText));

    [Fact]
    public void Summary_is_isolated_stateless_and_uses_no_retry_or_ai_framework()
    {
        References(Summary).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Summary");
        References(Summary).Should().NotContain(r => r == "ToroSquad.Infrastructure" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
            "no tables, no outbox, no database");
        References(Summary).Should().NotContain(r => Regex.IsMatch(r, "Polly|Resilience|OpenAI|Anthropic|SemanticKernel|Caching|Redis|Embedding|Vector", RegexOptions.IgnoreCase),
            "no retry library, no AI SDK, no cache, no embeddings");
        Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "Migrations"))
            .Should().NotContain(f => Path.GetFileName(f).Contains("Summary", StringComparison.OrdinalIgnoreCase), "no migration");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.Lfg.LfgModule), typeof(ToroSquad.Modules.Quote.QuoteModule),
                     typeof(ToroSquad.Modules.Birthday.BirthdayModule), typeof(ToroSquad.Modules.Currency.CurrencyModule),
                     typeof(ToroSquad.Modules.Randomizer.RandomizerModule), typeof(ToroSquad.Modules.Timezone.TimezoneModule),
                     typeof(ToroSquad.Modules.Giveaway.GiveawayModule), typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Summary");
        References(Core).Should().NotContain("ToroSquad.Modules.Summary");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Summary");

        var csproj = File.ReadAllText(Path.Combine(Root(), "ToroSquad.Modules.Summary.csproj"));
        Regex.Matches(csproj, @"<PackageReference Include=""([^""]+)""").Select(m => m.Groups[1].Value)
            .Should().BeEquivalentTo("Microsoft.Extensions.Http", "Microsoft.Extensions.Options.ConfigurationExtensions");
        AllCode().Should().NotContain("AddStandardResilienceHandler").And.NotContain("AddPolicyHandler").And.NotContain("DbContext");
    }

    [Fact]
    public void Only_commands_use_the_discord_sdk_and_only_the_provider_uses_http()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Summary\.(Application|Providers)$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Summary\.(Application|Commands)$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(HttpClient|IHttpClientFactory|HttpRequestMessage)$")));
    }

    [Fact]
    public void Nothing_runs_in_the_background_listens_to_messages_or_stores_them()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Summary(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|NotificationRequest|IHostedService|BackgroundService|IMemoryCache)$")));
        Summary.GetTypes().Should().NotContain(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t));

        // Messages are read over REST when /ozetle runs; the gateway stays Guilds-only, without a message cache.
        var config = GatewayBotService.CreateSocketConfig();
        config.GatewayIntents.Should().Be(GatewayIntents.Guilds);
        config.MessageCacheSize.Should().Be(0);
        AllCode().Should().NotContain("MessageReceived").And.NotContain("GatewayIntents.");
    }

    [Fact]
    public void One_summary_is_one_inference_with_one_model_and_no_fallback()
    {
        var code = AllCode();
        Regex.Matches(code, @"\bai\.SummarizeAsync\(").Should().ContainSingle("SummaryService calls the model in exactly one place");
        Regex.Matches(File.ReadAllText(Path.Combine(Root(), "Providers", "OpenCodeSummaryAiClient.cs")), @"\.SendAsync\(").Should().ContainSingle();
        code.Should().NotMatchRegex("(?i)glm|fallback ?model\\s*=|FallbackModel", "no second model");
        Summary.GetTypes().Where(t => !t.IsInterface && typeof(ISummaryAiClient).IsAssignableFrom(t)).Should().Equal(typeof(OpenCodeSummaryAiClient));
        typeof(SummaryOptions).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Fallback", StringComparison.Ordinal) || n.Contains("Retry", StringComparison.Ordinal));
    }

    [Fact]
    public void Replies_never_ping_and_logs_never_carry_text()
    {
        var commands = File.ReadAllText(Path.Combine(Root(), "Commands", "SummaryCommands.cs"));
        commands.Should().NotMatchRegex(@"\b(RespondAsync|FollowupAsync|SendMessageAsync)\(", "every reply goes through ToroInteractionModule (no allowed mentions)");
        commands.Should().Contain("SendAsync(part, null, null, ephemeral: false)");
        Regex.Count(commands, @"ModifyOriginalResponseAsync\(").Should().Be(Regex.Count(commands, @"m\.AllowedMentions = NoPings;"));
        commands.Should().Contain("DiscordConversions.ToAllowedMentions(MentionPolicy.None)");
        AllCode().Should().NotContain("MentionPolicy.EveryoneOnly").And.NotContain("ExplicitUsers").And.NotContain("Everyone: true");

        // Logs carry ids, counts, usage and outcomes: never a transcript, prompt, answer or message text.
        var code = AllCode();
        foreach (Match call in Regex.Matches(code, @"\b(logger\.Log\w+|Log[A-Z]\w+)\((?<args>[^;]*)\);"))
            call.Groups["args"].Value.Should().NotMatchRegex(@"\b(transcript\.Text|Text\b|Content|prompt|summary\b|parts\b(?!\.Count)|Messages|AuthorName|Names)", call.Value);
    }

    [Fact]
    public void The_api_key_is_a_redacted_secret_that_never_lives_in_appsettings()
    {
        ToroSquad.Infrastructure.InfrastructureServiceCollectionExtensions.SecretConfigurationKeys.Should().Contain(SummaryOptions.ApiKeyVariable);
        ToroSquad.Infrastructure.InfrastructureServiceCollectionExtensions.SecretEnvironmentVariables.Should().Contain(SummaryOptions.ApiKeyVariable);
        foreach (var file in Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot"), "appsettings*.json"))
            File.ReadAllText(file).Should().NotContain("OPENCODE", Path.GetFileName(file)).And.NotContain("ApiKey\"", Path.GetFileName(file));
        typeof(SummaryOptions).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Key", StringComparison.Ordinal));
        new ToroSquad.Infrastructure.Hosting.SecretRedactor(["sk-test-0123456789abcdef"]).Redact("key sk-test-0123456789abcdef").Should().NotContain("sk-test");
    }

    [Fact]
    public void Every_summary_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Summary.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("summary", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new SummaryModule();
        module.Descriptor.Id.Value.Should().Be("summary");
        module.Descriptor.EnabledByDefault.Should().BeFalse("like every optional module");
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().BeEmpty();
        module.Descriptor.RequiredBotChannelPermissions.Should().Be(SummaryService.RequiredToRead);
        module.InteractionModuleTypes.Should().Equal(typeof(SummaryCommands));
        module.ValidateConfiguration(new ConfigurationBuilder().Build()).Should().BeEmpty("the defaults are valid");
    }

    [Fact]
    public void Generation_mode_defaults_to_legacy_binds_from_configuration_and_is_validated()
    {
        new SummaryOptions().GenerationMode.Should().Be(SummaryGenerationMode.Legacy);
        var grounded = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Summary:GenerationMode"] = "Grounded" })
            .Build().GetSection(SummaryOptions.Section).Get<SummaryOptions>()!;
        grounded.GenerationMode.Should().Be(SummaryGenerationMode.Grounded);
        grounded.Validate().Should().BeEmpty();
        (grounded.MaxOutputTokens, grounded.RequestTimeoutSeconds, grounded.Temperature, grounded.TopP, grounded.DisableThinking)
            .Should().Be((1200, 25, 0.3, 0.9, true), "switching the mode changes no other AI setting");

        new SummaryOptions { GenerationMode = (SummaryGenerationMode)7 }.Validate().Should().ContainSingle(e => e.Contains("GenerationMode", StringComparison.Ordinal));
        new SummaryOptions { GroundedMaxOutputTokens = 100 }.Validate().Should().ContainSingle(e => e.Contains("GroundedMaxOutputTokens", StringComparison.Ordinal));
    }

    [Fact]
    public void Allowed_roles_live_in_one_place_and_bind_from_configuration()
    {
        foreach (var id in SummaryOptions.DefaultAllowedRoleIds)
        {
            var literal = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SourceFiles().Where(f => File.ReadAllText(f).Contains(literal, StringComparison.Ordinal)).Select(Path.GetFileName)
                .Should().Equal("SummaryOptions.cs");
        }

        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Summary:AllowedRoleIds:0"] = "123456789012345678",
            ["Summary:AllowedRoleIds:1"] = "223456789012345678",
        }).Build().GetSection(SummaryOptions.Section).Get<SummaryOptions>()!;
        configured.EffectiveAllowedRoleIds.Should().Equal(123456789012345678UL, 223456789012345678UL);
        (new ConfigurationBuilder().Build().GetSection(SummaryOptions.Section).Get<SummaryOptions>() ?? new SummaryOptions())
            .EffectiveAllowedRoleIds.Should().Equal(SummaryOptions.DefaultAllowedRoleIds);
        new SummaryOptions { AllowedRoleIds = [5] }.Validate().Should().ContainSingle(e => e.Contains("AllowedRoleIds", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_configuration_is_reported_without_secret_values()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Summary:BaseUrl"] = "http://opencode.ai/zen/go/v1",
            ["Summary:Model"] = "deepseek v4",
            ["Summary:MaxMessages"] = "1000",
            ["Summary:ReasoningEffort"] = "none",
            ["Summary:TopP"] = "0",
            ["OPENCODE_GO_API_KEY"] = "sk-should-never-appear-0123456789",
        }).Build();

        var problems = new SummaryModule().ValidateConfiguration(configuration);

        problems.Should().HaveCount(5);
        problems.Should().NotContain(p => p.Contains("sk-should-never-appear", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Module_is_listed_in_modules_off_by_default_gated_and_its_services_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        var gated = new ToroModuleAttribute(SummaryModule.ModuleIdValue);
        await host.InScopeAsync(async sp =>
        {
            var localizer = sp.GetRequiredService<ILocalizer>();
            var modules = sp.GetRequiredService<ModuleManagementService>();
            var listed = (await modules.ListAsync(Guild, CancellationToken.None)).Single(m => m.Descriptor.Id == SummaryModule.ModuleIdTyped);
            listed.Enabled.Should().BeFalse();
            localizer.Get("tr", listed.Descriptor.NameKey).Should().Be("TSQ Özet");
            localizer.Get("tr", listed.Descriptor.DescriptionKey).Should().Contain("/ozetle");

            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);
            (await modules.SetEnabledAsync(TestHost.Admin(Guild), "summary", true, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).IsSuccess.Should().BeTrue("a member without any permission may use it");
            (await gated.CheckRequirementsAsync(Context(new GuildId(43)), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError, "per guild");

            sp.GetRequiredService<ISummaryAiClient>().Should().BeOfType<OpenCodeSummaryAiClient>();
            sp.GetRequiredService<SummaryService>().Should().NotBeNull();
            sp.GetServices<IModuleHealthCheck>().Should().ContainSingle(h => h.Module == SummaryModule.ModuleIdTyped);
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenCodeSummaryAiClient.HttpClientName);
            client.BaseAddress.Should().Be(new Uri("https://opencode.ai/zen/go/v1/"));
            client.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("SummaryModule");
        });
    }

    [Fact]
    public async Task Manifest_has_ozetle_as_a_public_guild_command_with_its_turkish_description()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();
        manifest.Commands.Where(c => c.OwnerModule == "summary").Select(c => c.Name).Should().Equal("ozetle");

        var command = manifest.Find("ozetle")!;
        command.DefaultMemberPermissions.Should().BeNull("/ozetle is for everyone");
        command.Contexts.Should().Equal(0);
        command.IntegrationTypes.Should().Equal(0);
        command.Options.Should().BeEmpty();
        command.DescriptionLocalizations["tr"].Should().Be("Bu kanaldaki son mesajları özetler.");
        var properties = DiscordCommandRegistrar.ToProperties(command);
        (properties.Name.Value, properties.Description.Value).Should().Be(("ozetle", "Summarize the latest messages in this channel"));
    }

    [Fact]
    public void Summary_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));
        tr["summary.channel_busy"].Should().Be("Bu kanal için zaten bir özet hazırlanıyor.");

        foreach (Match m in KeyLiteral().Matches(AllCode()))
            tr.Should().ContainKey(m.Groups[1].Value);

        // Loads together with the core catalog (no duplicate keys).
        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(Summary, "ToroSquad.Modules.Summary.Localization"),
        ]);
        catalog.Get("tr", "summary.not_enough", 5).Should().Contain("5");
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

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

    [GeneratedRegex(@"""((?:summary|module\.summary)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();
}
