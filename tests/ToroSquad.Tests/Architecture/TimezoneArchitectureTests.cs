using System.Reflection;
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
using ToroSquad.Modules.Timezone;
using ToroSquad.Modules.Timezone.Application;
using ToroSquad.Modules.Timezone.Commands;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Saat Dönüştürücü boundaries and integration: a stateless module (no storage, no migration, no background work, no
/// HTTP, no AI, no cache, no configuration, no package), isolated from the other feature modules, real zone data only (no
/// fixed offsets), no Discord SDK outside Commands, public card / private refusal, replies never ping; registered and gated
/// through /modules like every optional module (off by default), and /saat in the manifest as a public guild command.
/// </summary>
public sealed partial class TimezoneArchitectureTests
{
    private static readonly Assembly Timezone = typeof(TimezoneModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Timezone).Build());

    private static readonly GuildId Guild = new(42);

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Timezone");

    private static IEnumerable<string> SourceFiles() => Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static string Code() => string.Join("\n", SourceFiles().Select(File.ReadAllText));

    [Fact]
    public void Timezone_is_isolated_stateless_and_has_no_packages()
    {
        References(Timezone).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Timezone");
        References(Timezone).Should().NotContain(r => r == "ToroSquad.Infrastructure" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
            "stateless: no tables, no outbox");
        References(Timezone).Should().NotContain(r => Regex.IsMatch(r, "Http|OpenAI|Anthropic|SemanticKernel|Caching|Redis|NodaTime|TimeZoneConverter", RegexOptions.IgnoreCase),
            "no HTTP, no AI, no cache, no extra time zone library");
        Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "Migrations"))
            .Should().NotContain(f => Path.GetFileName(f).Contains("Timezone", StringComparison.OrdinalIgnoreCase), "no migration");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.Lfg.LfgModule), typeof(ToroSquad.Modules.Quote.QuoteModule),
                     typeof(ToroSquad.Modules.Birthday.BirthdayModule), typeof(ToroSquad.Modules.Currency.CurrencyModule),
                     typeof(ToroSquad.Modules.Randomizer.RandomizerModule), typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Timezone");
        References(Core).Should().NotContain("ToroSquad.Modules.Timezone");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Timezone");

        File.ReadAllText(Path.Combine(Root(), "ToroSquad.Modules.Timezone.csproj")).Should().NotContain("<PackageReference");
        new TimezoneModule().ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
    }

    [Fact]
    public void Conversion_uses_the_zone_database_never_a_fixed_offset_or_the_machine_clock()
    {
        var code = Code();
        code.Should().NotMatchRegex(@"FromHours\(|FromMinutes\(|CreateCustomTimeZone|TimeZoneInfo\.Local\b|\bUTC[+-]\d|\bGMT[+-]\d",
            "every offset comes from TimeZoneInfo and the IANA id");
        code.Should().NotMatchRegex(@"DateTime(Offset)?\.(Now|UtcNow|Today)\b", "'now' is the host's TimeProvider");
        File.ReadAllText(Path.Combine(Root(), "Commands", "TimezoneCommands.cs")).Should().Contain("Services.Clock.GetUtcNow()");
    }

    [Fact]
    public void Only_commands_use_the_discord_sdk() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Timezone.Application")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Timezone_sends_nothing_by_itself_and_runs_nothing_in_the_background()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Timezone(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|NotificationRequest|IHostedService|BackgroundService|HttpClient|IHttpClientFactory|IMemoryCache)$")));
        Timezone.GetTypes().Should().NotContain(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t));
    }

    [Fact]
    public void Card_is_public_refusal_is_private_nothing_pings_and_the_time_is_never_logged()
    {
        var code = Code();
        code.Should().NotContain("MentionPolicy.EveryoneOnly").And.NotContain("ExplicitUsers").And.NotContain("RoleMention").And.NotContain("allowedMentions");
        // Every reply goes through ToroInteractionModule.SendAsync / SendEphemeralAsync (allowed mentions: none).
        var commands = File.ReadAllText(Path.Combine(Root(), "Commands", "TimezoneCommands.cs"));
        commands.Should().NotMatchRegex(@"\b(RespondAsync|FollowupAsync|ModifyOriginalResponseAsync|SendMessageAsync|DeferAsync)\(");
        commands.Should().Contain("SendAsync(null, DiscordConversions.ToEmbed(card), null, ephemeral: false)");
        commands.Should().Contain("SendEphemeralAsync(reply.Refusal, null, null)");

        // Logs carry metadata only: never the entered time, the instant or the card.
        foreach (Match call in Regex.Matches(code, @"\blogger\.Log\w+\((?<args>[^;]*)\);"))
            Regex.Replace(call.Groups["args"].Value, "\"[^\"]*\"", "\"\"").Replace("time.Length", "", StringComparison.Ordinal) // arguments, not the message text
                .Should().NotMatchRegex(@"\b(time|input|instant|date|rows|fields|reply|card|displayName)\b");
        Regex.Matches(commands, @"\blogger\.Log(\w+)\(").Select(m => m.Groups[1].Value).Should().Equal("Debug");
        // The only non-Debug log: the host has no zone data (an operator problem, not a user action).
        Regex.Matches(code, @"\blogger\.Log(Information|Warning|Error|Critical|Trace)\(").Should().ContainSingle()
            .Which.Groups[1].Value.Should().Be("Error");
    }

    [Fact]
    public void No_user_name_is_hardcoded_the_card_names_whoever_ran_the_command()
    {
        foreach (var file in SourceFiles().Concat(Directory.GetFiles(Path.Combine(Root(), "Localization"), "*.json")))
            File.ReadAllText(file).Should().NotMatchRegex(@"\bToro\b", Path.GetFileName(file));
        File.ReadAllText(Path.Combine(Root(), "Commands", "TimezoneCommands.cs")).Should().MatchRegex(@"cards\.Convert\([^;]*DisplayName\(\)\)");
    }

    [Fact]
    public void Every_timezone_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Timezone.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("timezone", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new TimezoneModule();
        module.Descriptor.Id.Value.Should().Be("timezone");
        module.Descriptor.EnabledByDefault.Should().BeFalse("like every optional utility module");
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().BeEmpty();
        module.InteractionModuleTypes.Should().Equal(typeof(TimezoneCommands));
    }

    [Fact]
    public async Task Module_is_listed_in_modules_off_by_default_gated_and_its_services_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        var gated = new ToroModuleAttribute(TimezoneModule.ModuleIdValue);
        await host.InScopeAsync(async sp =>
        {
            var localizer = sp.GetRequiredService<ILocalizer>();
            var modules = sp.GetRequiredService<ModuleManagementService>();
            var listed = (await modules.ListAsync(Guild, CancellationToken.None)).Single(m => m.Descriptor.Id == TimezoneModule.ModuleIdTyped);
            listed.Enabled.Should().BeFalse();
            localizer.Get("tr", listed.Descriptor.NameKey).Should().Be("TSQ Saat Dönüştürücü");
            localizer.Get("tr", listed.Descriptor.DescriptionKey).Should().Contain("/saat");

            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);
            (await modules.SetEnabledAsync(TestHost.Admin(Guild), "timezone", true, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).IsSuccess.Should().BeTrue("a member without any permission may use it");
            (await gated.CheckRequirementsAsync(Context(new GuildId(43)), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError, "per guild");

            sp.GetRequiredService<TimezoneCards>().Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Manifest_has_saat_as_a_public_guild_command_with_turkish_and_english_descriptions()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();
        manifest.Commands.Where(c => c.OwnerModule == "timezone").Select(c => c.Name).Should().Equal("saat");

        var saat = manifest.Find("saat")!;
        saat.DefaultMemberPermissions.Should().BeNull("/saat is for everyone");
        saat.Contexts.Should().Equal(0);
        saat.IntegrationTypes.Should().Equal(0);
        var time = saat.Options.Should().ContainSingle().Subject;
        (time.Name, time.Type, time.Required, time.MinLength, time.MaxLength).Should().Be(("time", OptionType.String, true, (int?)1, (int?)ClockInput.MaxInputLength));

        // What DiscordCommandRegistrar actually sends: the English default plus the "tr" localization.
        var properties = DiscordCommandRegistrar.ToProperties(saat);
        properties.Name.Value.Should().Be("saat");
        (properties.Description.Value, properties.DescriptionLocalizations["tr"]).Should().Be(("Convert a time in Türkiye to other time zones", "Türkiye saatini farklı saat dilimlerine çevirir."));
        var option = properties.Options.Value.Single();
        (option.Description, option.DescriptionLocalizations["tr"]).Should().Be(("Time in Türkiye (today), e.g. 21:00 or 9.30", "Türkiye saati (bugün). Örn: 21:00 veya 9.30"));
    }

    [Fact]
    public void Timezone_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));

        foreach (Match m in KeyLiteral().Matches(Code()))
            tr.Should().ContainKey(m.Groups[1].Value);
        foreach (var zone in TimeZoneBoard.Zones)
            tr.Should().ContainKey(zone.NameKey);

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
            new LocalizationSource(typeof(ToroSquad.Modules.Randomizer.RandomizerModule).Assembly, "ToroSquad.Modules.Randomizer.Localization"),
            new LocalizationSource(Timezone, "ToroSquad.Modules.Timezone.Localization"),
        ]);
        catalog.Get("tr", "timezone.previous_day").Should().Be("Önceki gün");
        catalog.Get("en", "timezone.next_day").Should().Be("Next day");
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

    [GeneratedRegex(@"""((?:timezone|module\.timezone)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();
}
