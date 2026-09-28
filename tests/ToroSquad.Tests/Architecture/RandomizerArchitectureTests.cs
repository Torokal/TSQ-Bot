using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Randomizer;
using ToroSquad.Modules.Randomizer.Application;
using ToroSquad.Modules.Randomizer.Commands;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Randomizer boundaries and integration: a stateless module (no storage, no migration, no background work, no HTTP, no
/// AI, no configuration), isolated from the other feature modules, one secure random source and never System.Random, no
/// Discord SDK outside Commands, replies never ping; registered and gated through /modules like every optional module (off
/// by default), and /zarat, /randomsayi, /sec, /yazitura in the manifest as public guild commands.
/// </summary>
public sealed partial class RandomizerArchitectureTests
{
    private static readonly Assembly Randomizer = typeof(RandomizerModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Randomizer).Build());

    private static readonly GuildId Guild = new(42);

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string[] TypeReferences(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeReferences.Select(metadata.GetTypeReference)
            .Select(t => metadata.GetString(t.Namespace) + "." + metadata.GetString(t.Name)).ToArray();
    }

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Randomizer");

    private static IEnumerable<string> SourceFiles() => Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    [Fact]
    public void Randomizer_is_isolated_stateless_and_has_no_packages()
    {
        References(Randomizer).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Randomizer");
        References(Randomizer).Should().NotContain(r => r == "ToroSquad.Infrastructure" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
            "stateless: no tables, no outbox");
        References(Randomizer).Should().NotContain(r => Regex.IsMatch(r, "Http|OpenAI|Anthropic|SemanticKernel|Caching|Redis", RegexOptions.IgnoreCase),
            "no HTTP, no AI, no cache");
        Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "Migrations"))
            .Should().NotContain(f => Path.GetFileName(f).Contains("Random", StringComparison.OrdinalIgnoreCase), "no migration");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.Lfg.LfgModule), typeof(ToroSquad.Modules.Quote.QuoteModule),
                     typeof(ToroSquad.Modules.Birthday.BirthdayModule), typeof(ToroSquad.Modules.Currency.CurrencyModule),
                     typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Randomizer");
        References(Core).Should().NotContain("ToroSquad.Modules.Randomizer");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Randomizer");

        var csproj = File.ReadAllText(Path.Combine(Root(), "ToroSquad.Modules.Randomizer.csproj"));
        csproj.Should().NotContain("<PackageReference");
        new RandomizerModule().ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
    }

    [Fact]
    public void Randomness_comes_only_from_the_secure_source_never_system_random()
    {
        // Any use of System.Random (field, parameter, local, call, Random.Shared) needs a type reference to it in the metadata.
        TypeReferences(Randomizer).Should().NotContain("System.Random").And.Contain("System.Security.Cryptography.RandomNumberGenerator");
        foreach (var file in SourceFiles())
            File.ReadAllText(file).Should().NotMatchRegex(@"\bnew\s+(System\.)?Random\s*\(|\bRandom\.Shared\b|Guid\.NewGuid", Path.GetFileName(file));

        // RandomNumberGenerator is used in exactly one place: the production source.
        SourceFiles().Where(f => File.ReadAllText(f).Contains("RandomNumberGenerator.", StringComparison.Ordinal))
            .Select(Path.GetFileName).Should().Equal("IRandomSource.cs");
        typeof(SecureRandomSource).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Should().BeEmpty("no seed, no state, no memory of earlier results");
    }

    [Fact]
    public void Only_commands_use_the_discord_sdk() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Randomizer.Application")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Randomizer_sends_nothing_by_itself_and_runs_nothing_in_the_background()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Randomizer(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|NotificationRequest|IHostedService|BackgroundService|HttpClient|IHttpClientFactory)$")));
        Randomizer.GetTypes().Should().NotContain(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t));
    }

    [Fact]
    public void Replies_never_ping_and_results_are_never_logged()
    {
        var code = string.Join("\n", SourceFiles().Select(File.ReadAllText));
        code.Should().NotContain("MentionPolicy.EveryoneOnly").And.NotContain("ExplicitUsers").And.NotContain("RoleMention").And.NotContain("allowedMentions");
        // Every reply goes through ToroInteractionModule.SendAsync / SendEphemeralAsync (allowed mentions: none).
        var commands = File.ReadAllText(Path.Combine(Root(), "Commands", "RandomizerCommands.cs"));
        commands.Should().NotMatchRegex(@"\b(RespondAsync|FollowupAsync|ModifyOriginalResponseAsync|SendMessageAsync)\(");
        commands.Should().Contain("SendAsync(null, DiscordConversions.ToEmbed(card), null, ephemeral: false)");
        commands.Should().Contain("SendEphemeralAsync(reply.Refusal, null, null)");

        // Logs carry metadata only: no reply, card, option text or result is ever passed to a logger.
        foreach (Match call in Regex.Matches(code, @"\blogger\.Log\w+\((?<args>[^;]*)\);"))
            call.Groups["args"].Value.Should().NotMatchRegex(@"\b(reply|card|cards\.|Refusal|Description|options\)|dice\)|Dice\b|Choose|Flip|Between|Roll)");
        Regex.Matches(code, @"\blogger\.Log(\w+)\(").Select(m => m.Groups[1].Value).Should().OnlyContain(level => level == "Debug");
        Regex.Matches(code, @"\bLog(Information|Warning|Error|Critical|Trace)\(").Should().BeEmpty();
    }

    [Fact]
    public void No_user_name_is_hardcoded_the_cards_name_whoever_ran_the_command()
    {
        // "Toro" was only the example user of the specification: no fixed name in code or texts.
        var files = SourceFiles().Concat(Directory.GetFiles(Path.Combine(Root(), "Localization"), "*.json"));
        foreach (var file in files)
            File.ReadAllText(file).Should().NotMatchRegex(@"\bToro\b", Path.GetFileName(file));

        var commands = File.ReadAllText(Path.Combine(Root(), "Commands", "RandomizerCommands.cs"));
        Regex.Matches(commands, @"cards\.\w+\([^;]*DisplayName\(\)\)").Should().HaveCount(4, "all four commands pass the invoking member's name");
    }

    [Fact]
    public void Every_randomizer_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Randomizer.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("randomizer", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new RandomizerModule();
        module.Descriptor.Id.Value.Should().Be("randomizer");
        module.Descriptor.EnabledByDefault.Should().BeFalse("like every optional utility module (quote, birthday, currency)");
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().BeEmpty();
        module.InteractionModuleTypes.Should().Equal(typeof(RandomizerCommands));
    }

    [Fact]
    public async Task Module_is_listed_in_modules_off_by_default_gated_and_its_services_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        var gated = new ToroModuleAttribute(RandomizerModule.ModuleIdValue);
        await host.InScopeAsync(async sp =>
        {
            var localizer = sp.GetRequiredService<ILocalizer>();
            var modules = sp.GetRequiredService<ModuleManagementService>();
            var listed = (await modules.ListAsync(Guild, CancellationToken.None)).Single(m => m.Descriptor.Id == RandomizerModule.ModuleIdTyped);
            listed.Enabled.Should().BeFalse();
            localizer.Get("tr", listed.Descriptor.NameKey).Should().Be("TSQ Randomizer");
            localizer.Get("tr", listed.Descriptor.DescriptionKey).Should().Contain("/zarat").And.Contain("/randomsayi").And.Contain("/sec").And.Contain("/yazitura");

            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);

            (await modules.SetEnabledAsync(TestHost.Admin(Guild), "randomizer", true, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).IsSuccess.Should().BeTrue("a member without any permission may use it");
            (await gated.CheckRequirementsAsync(Context(new GuildId(43)), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError, "per guild");

            (await modules.SetEnabledAsync(TestHost.Admin(Guild), "randomizer", false, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);

            sp.GetRequiredService<IRandomSource>().Should().BeOfType<SecureRandomSource>();
            sp.GetRequiredService<RandomizerCards>().Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Manifest_has_the_four_public_guild_commands_with_turkish_descriptions_and_bounds()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();
        manifest.Commands.Where(c => c.OwnerModule == "randomizer").Select(c => c.Name).Should().BeEquivalentTo("zarat", "randomsayi", "sec", "yazitura");

        var expected = new Dictionary<string, string>
        {
            ["zarat"] = "Belirtilen zarları rastgele atar.",
            ["randomsayi"] = "Belirtilen aralıktan rastgele bir sayı seçer.",
            ["sec"] = "Verilen seçeneklerden rastgele birini seçer.",
            ["yazitura"] = "Yazı tura atar.",
        };
        foreach (var (name, tr) in expected)
        {
            var command = manifest.Find(name)!;
            command.DefaultMemberPermissions.Should().BeNull($"/{name} is for everyone");
            command.Contexts.Should().Equal(0);
            command.IntegrationTypes.Should().Equal(0);
            command.DescriptionLocalizations["tr"].Should().Be(tr);
        }

        var zar = manifest.Find("zarat")!.Options.Should().ContainSingle().Subject;
        (zar.Name, zar.Type, zar.Required, zar.MaxLength).Should().Be(("zar", OptionType.String, true, (int?)DiceNotation.MaxInputLength));

        var number = manifest.Find("randomsayi")!.Options;
        number.Select(o => (o.Name, o.Type, o.Required)).Should().Equal(("maksimum", OptionType.Integer, true), ("minimum", OptionType.Integer, false));
        number.Should().OnlyContain(o => o.MinValue == -NumberRanges.Limit && o.MaxValue == NumberRanges.Limit);

        var options = manifest.Find("sec")!.Options.Should().ContainSingle().Subject;
        (options.Name, options.Type, options.Required, options.MaxLength).Should().Be(("seçenekler", OptionType.String, true, (int?)ChoiceList.MaxInputLength));

        manifest.Find("yazitura")!.Options.Should().BeEmpty();
    }

    [Fact]
    public async Task Registration_payload_sends_turkish_for_every_description_and_keeps_english_as_the_default()
    {
        await using var host = await TestHost.CreateAsync();
        var manifest = await host.Services.GetRequiredService<InteractionHost>().InitializeAsync(host.Services);
        var expected = new Dictionary<string, (string English, string Turkish)>
        {
            ["zarat"] = ("Roll the given dice", "Belirtilen zarları rastgele atar."),
            ["zarat.zar"] = ("Number of dice and sides: 1-20, 2-6 or 2d6", "Zar biçimi. Örn: 1-20 veya 2d6"),
            ["randomsayi"] = ("Pick a random number from the given range", "Belirtilen aralıktan rastgele bir sayı seçer."),
            ["randomsayi.maksimum"] = ("Largest value (inclusive)", "Seçilebilecek en büyük sayı"),
            ["randomsayi.minimum"] = ("Smallest value (inclusive, default: 1)", "Seçilebilecek en küçük sayı (varsayılan: 1)"),
            ["sec"] = ("Pick one of the given options at random", "Verilen seçeneklerden rastgele birini seçer."),
            ["sec.seçenekler"] = ("Options separated by commas or |, e.g. CS2, Valheim | WoW", "Virgül veya | ile ayrılmış seçenekler. Örn: CS2, Valheim | WoW"),
            ["yazitura"] = ("Flip a coin", "Yazı tura atar."),
        };

        var actual = new Dictionary<string, (string English, string Turkish)>();
        foreach (var name in new[] { "zarat", "randomsayi", "sec", "yazitura" })
        {
            // What DiscordCommandRegistrar actually sends: the English default plus the "tr" localization.
            var properties = DiscordCommandRegistrar.ToProperties(manifest.Find(name)!);
            actual[name] = (properties.Description.Value, properties.DescriptionLocalizations["tr"]);
            foreach (var option in properties.Options.GetValueOrDefault() ?? [])
                actual[name + "." + option.Name] = (option.Description, option.DescriptionLocalizations["tr"]);
        }

        actual.Should().BeEquivalentTo(expected);
        foreach (var text in expected.Values)
            text.Turkish.Length.Should().BeLessThanOrEqualTo(100, "Discord's description limit");
    }

    [Fact]
    public async Task Option_names_pass_discord_net_registration_validation()
    {
        await using var host = await TestHost.CreateAsync();
        var manifest = await host.Services.GetRequiredService<InteractionHost>().InitializeAsync(host.Services);
        foreach (var name in new[] { "zarat", "randomsayi", "sec", "yazitura" })
            DiscordCommandRegistrar.ToProperties(manifest.Find(name)!).Name.Value.Should().Be(name);
        new SlashCommandBuilder().WithName("sec").WithDescription("x")
            .AddOption("seçenekler", ApplicationCommandOptionType.String, "x", isRequired: true).Build()
            .Options.Value.Single().Name.Should().Be("seçenekler", "ç is a lowercase letter: Discord accepts it like altın's ı");
    }

    [Fact]
    public void Randomizer_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));
        tr.Should().ContainKeys("cmd.zarat", "cmd.zarat.zar", "cmd.randomsayi", "cmd.randomsayi.maksimum", "cmd.randomsayi.minimum",
            "cmd.sec", "cmd.sec.seçenekler", "cmd.yazitura");

        var code = string.Join("\n", SourceFiles().Select(File.ReadAllText));
        foreach (Match m in KeyLiteral().Matches(code))
            tr.Should().ContainKey(m.Groups[1].Value);

        // The limits in the texts are the limits in the code.
        tr[DiceNotation.TooManyKey].Should().Contain(DiceNotation.MaxCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tr[ChoiceList.TooManyKey].Should().Contain(ChoiceList.MaxOptions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tr[ChoiceList.TooLongKey].Should().Contain(ChoiceList.MaxOptionLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tr[ChoiceList.InputTooLongKey].Should().Contain(ChoiceList.MaxInputLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tr[DiceNotation.TooManySidesKey].Should().Contain("10.000");
        tr[NumberRanges.OutOfRangeKey].Should().Contain("1.000.000.000");

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
            new LocalizationSource(typeof(ToroSquad.Modules.Currency.CurrencyModule).Assembly, "ToroSquad.Modules.Currency.Localization"),
            new LocalizationSource(Randomizer, "ToroSquad.Modules.Randomizer.Localization"),
        ]);
        catalog.Get("tr", "randomizer.coin.tura").Should().Be("TURA");
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

    [GeneratedRegex(@"""((?:randomizer|module\.randomizer)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();
}
