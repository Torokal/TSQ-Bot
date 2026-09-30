using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Quote;
using ToroSquad.Modules.Quote.Application;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Quote boundaries: a stateless utility module — isolated from the other feature modules, no storage, no background
/// work, no outbox or transport, no Discord SDK outside Commands, no GDI+; fonts embedded; answers never ping; registered,
/// gated and off by default like every optional module.
/// </summary>
public sealed partial class QuoteArchitectureTests
{
    private static readonly Assembly Quote = typeof(QuoteModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Quote).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Quote_and_other_modules_never_reference_each_other_and_quote_has_no_storage()
    {
        References(Quote).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Quote");
        References(Quote).Should().NotContain(r => r == "ToroSquad.Infrastructure" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
            "stateless: no tables, no migration");
        References(Quote).Should().NotContain("System.Drawing.Common", "GDI+ is Windows-only; the bot runs on Linux");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.Lfg.LfgModule), typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Quote");
        References(Core).Should().NotContain("ToroSquad.Modules.Quote");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Quote");
    }

    [Fact]
    public void Quote_application_does_not_use_the_discord_sdk() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Quote.Application")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void Quote_sends_nothing_by_itself_and_runs_nothing_in_the_background()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Quote(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|NotificationRequest|IHostedService|BackgroundService)$")));
        Quote.GetTypes().Should().NotContain(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t));
    }

    [Fact]
    public void The_only_reply_paths_never_ping()
    {
        var commands = File.ReadAllText(Path.Combine(Root(), "Commands", "QuoteCommands.cs"));
        commands.Should().Contain("NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None)");
        foreach (Match call in Regex.Matches(commands, @"\b(RespondAsync|FollowupAsync|FollowupWithFileAsync|ModifyOriginalResponseAsync)\("))
        {
            var end = call.Index + call.Length;
            for (var depth = 1; depth > 0; end++)
                depth += commands[end] switch { '(' => 1, ')' => -1, _ => 0 };
            commands[call.Index..end].Should().Contain("NoPings", $"{call.Value} at offset {call.Index}");
        }

        // The single public message is the card itself: a file, no text content.
        Regex.Matches(commands, @"ephemeral:\s*false").Should().ContainSingle();
        commands.Should().Contain("FollowupWithFileAsync(png, FileName, ephemeral: false, allowedMentions: NoPings)");
    }

    [Fact]
    public void Quote_adds_no_gateway_intent_message_event_or_message_cache()
    {
        // Messages are read by id over REST when /quote asks; nothing listens to MESSAGE_CREATE.
        var config = GatewayBotService.CreateSocketConfig();
        config.GatewayIntents.Should().Be(global::Discord.GatewayIntents.Guilds);
        config.MessageCacheSize.Should().Be(0);
        foreach (var file in Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Discord"), "*.cs", SearchOption.AllDirectories))
                     .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj")))
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("MessageReceived", Path.GetFileName(file)).And.NotContain("GatewayIntents.GuildMessages", Path.GetFileName(file))
                .And.NotContain("GatewayIntents.MessageContent", Path.GetFileName(file));
        }

        // Two entry points and no more: /quote and the MESSAGE command Apps → Quote (no user command, no other context command).
        var commands = File.ReadAllText(Path.Combine(Root(), "Commands", "QuoteCommands.cs"));
        Regex.Matches(commands, @"\[(SlashCommand|MessageCommand|UserCommand)\(").Select(m => m.Groups[1].Value).Should().Equal("SlashCommand", "MessageCommand");
    }

    [Fact]
    public void Fonts_are_embedded_and_licensed()
    {
        var resources = Quote.GetManifestResourceNames();
        foreach (var file in QuoteFonts.Files)
        {
            resources.Should().Contain(QuoteFonts.ResourcePrefix + file);
            File.Exists(Path.Combine(Root(), "Assets", "Fonts", file)).Should().BeTrue(file);
        }

        foreach (var license in new[] { "OFL-NotoSans.txt", "OFL-NotoEmoji.txt" })
            File.ReadAllText(Path.Combine(Root(), "Assets", "Fonts", license)).Should().Contain("SIL Open Font License, Version 1.1");
        var notices = File.ReadAllText(Path.Combine(CommandManifestTests.RepoRoot(), "THIRD_PARTY_NOTICES.md"));
        notices.Should().Contain("Noto Sans").And.Contain("Noto Emoji").And.Contain("SixLabors.ImageSharp");

        // Loading never looks at the host's fonts.
        var fonts = QuoteFonts.Load();
        fonts.Sans.Name.Should().Be("Noto Sans");
        fonts.Emoji.Name.Should().Be("Noto Emoji");
        File.ReadAllText(Path.Combine(Root(), "Application", "QuoteFonts.cs")).Should().NotContain("SystemFonts");
    }

    [Fact]
    public void Every_quote_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Quote.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("quote", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new QuoteModule();
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().BeEmpty();
        module.ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
    }

    [Fact]
    public async Task Module_is_registered_off_by_default_and_its_services_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        var guild = new ToroSquad.Core.GuildId(42);
        await host.InScopeAsync(async sp =>
        {
            sp.GetRequiredService<ModuleRegistry>().TryGet("quote", out _).Should().BeTrue();
            (await sp.GetRequiredService<IModuleGate>().IsEnabledAsync(guild, QuoteModule.ModuleIdTyped, CancellationToken.None)).Should().BeFalse();
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(guild), "quote", true, CancellationToken.None))
                .Succeeded.Should().BeTrue();
            (await sp.GetRequiredService<IModuleGate>().IsEnabledAsync(guild, QuoteModule.ModuleIdTyped, CancellationToken.None)).Should().BeTrue();

            sp.GetRequiredService<QuoteMessageResolver>().Should().NotBeNull();
            sp.GetRequiredService<QuoteAvatarClient>().Should().NotBeNull();
            sp.GetRequiredService<QuoteImageRenderer>().Render(new QuoteRenderModel("DI", "Ad", "ad", null)).Png.Should().NotBeEmpty();
        });
    }

    [Fact]
    public void Quote_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));

        var code = string.Join("\n", Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Select(File.ReadAllText));
        foreach (Match m in KeyLiteral().Matches(code))
            tr.Should().ContainKey(m.Groups[1].Value);
        tr["quote.not_found"].Should().Be("Mesaj bulunamadı veya bu mesaja erişim iznin yok.");
        tr["quote.no_text"].Should().Be("Bu mesajda alıntılanabilecek bir metin yok.");

        // Loads together with every other module's catalog (no duplicate keys across modules).
        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Formula1.Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Volleyball.VolleyballModule).Assembly, "ToroSquad.Modules.Volleyball.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Live.LiveModule).Assembly, "ToroSquad.Modules.Live.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Lfg.LfgModule).Assembly, "ToroSquad.Modules.Lfg.Localization"),
            new LocalizationSource(Quote, "ToroSquad.Modules.Quote.Localization"),
        ]);
        catalog.Get("en", "quote.not_found").Should().Be("Message not found, or you don't have access to it.");
    }

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Quote");

    [GeneratedRegex(@"""((?:quote|module\.quote)\.(?!png"")[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();
}
