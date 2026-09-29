using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Giveaway;
using ToroSquad.Modules.Giveaway.Application;
using ToroSquad.Modules.Giveaway.Commands;
using ToroSquad.Modules.Giveaway.Domain;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Giveaway boundaries and integration: isolated from the other feature modules, no Discord SDK outside Commands, one
/// random source (never System.Random), the injected clock, the card is the only direct send (edits by the card sync,
/// the winner ping only through the outbox), no entrant list in any log, admin-only /giveaway in the manifest, registered
/// and gated through /modules like every optional module (off by default), catalogs complete.
/// </summary>
public sealed partial class GiveawayArchitectureTests
{
    private static readonly Assembly Giveaway = typeof(GiveawayModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Giveaway).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Giveaway");

    private static IEnumerable<string> SourceFiles() => Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static string Code() => string.Join("\n", SourceFiles().Select(File.ReadAllText));

    private static string Source(params string[] path) => System.IO.File.ReadAllText(Path.Combine([Root(), .. path]));

    [Fact]
    public void Giveaway_is_isolated_and_has_no_packages_of_its_own()
    {
        References(Giveaway).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Giveaway");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.Lfg.LfgModule), typeof(ToroSquad.Modules.Quote.QuoteModule),
                     typeof(ToroSquad.Modules.Birthday.BirthdayModule), typeof(ToroSquad.Modules.Currency.CurrencyModule),
                     typeof(ToroSquad.Modules.Randomizer.RandomizerModule), typeof(ToroSquad.Modules.Timezone.TimezoneModule),
                     typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Giveaway");
        References(Core).Should().NotContain("ToroSquad.Modules.Giveaway");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Giveaway");
        Source("ToroSquad.Modules.Giveaway.csproj").Should().NotContain("<PackageReference");
        new GiveawayModule().ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
    }

    [Fact]
    public void Only_commands_use_the_discord_sdk() =>
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Giveaway\.(Domain|Application|Persistence)$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void The_draw_uses_one_unbiased_source_and_the_injected_clock()
    {
        var code = string.Join("\n", Code().Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        code.Should().NotMatchRegex(@"\bnew Random\(|Random\.Shared|System\.Random\b|OrderBy\(\s*\w+\s*=>\s*Guid\.NewGuid|EF\.Functions\.Random",
            "no biased or database shuffle; the one source is IGiveawayRandom");
        Regex.Matches(code, @"=> RandomNumberGenerator\.GetInt32\(").Should().ContainSingle("SecureGiveawayRandom is the only production source");
        code.Should().NotMatchRegex(@"DateTime(Offset)?\.(Now|UtcNow|Today)\b", "'now' is the host's TimeProvider");
        typeof(GiveawayWorker).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(TimeProvider));
    }

    [Fact]
    public void The_card_is_the_only_direct_send_and_the_winner_ping_goes_through_the_outbox()
    {
        var service = Source("Application", "GiveawayService.cs");
        Regex.Matches(service, @"transport\.(\w+)\(").Select(m => m.Groups[1].Value).Distinct().Should().BeEquivalentTo("SendAsync", "FindRecentAsync");
        Regex.Matches(service, @"transport\.SendAsync\(").Should().ContainSingle("only the new card; never a second one");
        Regex.Matches(Source("Application", "GiveawayCardSync.cs"), @"transport\.(\w+)\(").Select(m => m.Groups[1].Value).Distinct().Should().Equal("EditAsync");
        Regex.Matches(service, @"outbox\.StageAsync\(").Should().ContainSingle();

        var code = Code();
        code.Should().NotContain("MentionPolicy.EveryoneOnly").And.NotContain("RoleMention").And.NotContain("AllowedMentionTypes");
        Regex.Matches(code, @"ExplicitUsers\(").Should().ContainSingle("the winner announcement only");
        Source("Application", "GiveawayCards.cs").Should().Contain("new OutgoingMessage(null, embed, MentionPolicy.None)");

        // Every interaction answer goes through ToroInteractionModule (allowed mentions: none); the modal carries no pings.
        var commands = Source("Commands", "GiveawayCommands.cs");
        commands.Should().NotMatchRegex(@"\b(RespondAsync|FollowupAsync|ModifyOriginalResponseAsync|SendMessageAsync)\(");
    }

    [Fact]
    public void Logs_carry_ids_and_counts_never_the_entrants_or_winners()
    {
        foreach (Match call in Regex.Matches(Code(), @"\blogger\.Log\w+\((?<args>[^;]*)\);"))
        {
            var args = Regex.Replace(call.Groups["args"].Value, "\"[^\"]*\"", "\"\"");
            args = Regex.Replace(args, @"[\w.]+\.Count\b", "count");
            args.Should().NotMatchRegex(@"\b(users|entrants|winners|candidates|previous|read|draw|Users|Winners|Prize|prize|Description|CreatorName|creatorName|input|request)\b", call.Value);
        }

        Regex.Matches(Code(), @"giveaway_(created|finished|cancelled|rerolled)").Select(m => m.Value).Distinct()
            .Should().BeEquivalentTo("giveaway_created", "giveaway_finished", "giveaway_cancelled", "giveaway_rerolled");
    }

    [Fact]
    public void No_user_name_is_hardcoded_the_card_names_whoever_started_it()
    {
        foreach (var file in SourceFiles().Concat(Directory.GetFiles(Path.Combine(Root(), "Localization"), "*.json")))
            System.IO.File.ReadAllText(file).Should().NotMatchRegex(@"\bToro\b", Path.GetFileName(file));
        Source("Commands", "GiveawayCommands.cs").Should().MatchRegex(@"giveaways\.CreateAsync\(Actor, Here, DisplayName\(\), request");
    }

    [Fact]
    public void Every_giveaway_interaction_class_declares_its_module_and_the_module_is_an_admin_module_off_by_default()
    {
        foreach (var type in Giveaway.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("giveaway", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new GiveawayModule();
        module.Descriptor.Id.Value.Should().Be("giveaway");
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("giveaway");
        module.Descriptor.RequiredBotChannelPermissions.Should().Be(GiveawayRules.RequiredChannelPermissions);
        module.InteractionModuleTypes.Should().Equal(typeof(GiveawayCommands));
        GiveawayService.ManagePermission.Should().Be(ToroSquad.Core.Security.Authorize.ServerSettings);
    }

    [Fact]
    public async Task Module_is_listed_off_by_default_gated_and_its_services_resolve_with_offline_reactions()
    {
        await using var host = await TestHost.CreateAsync();
        var guild = new GuildId(42);
        await host.InScopeAsync(async sp =>
        {
            var modules = sp.GetRequiredService<ModuleManagementService>();
            var listed = (await modules.ListAsync(guild, CancellationToken.None)).Single(m => m.Descriptor.Id == GiveawayModule.ModuleIdTyped);
            listed.Enabled.Should().BeFalse();
            sp.GetRequiredService<ILocalizer>().Get("tr", listed.Descriptor.NameKey).Should().Be("TSQ Çekiliş");
            sp.GetRequiredService<GiveawayService>().Should().NotBeNull();
            sp.GetRequiredService<IGiveawayReactions>().Should().BeOfType<OfflineGiveawayReactions>("the fake transport never reaches Discord");
            sp.GetRequiredService<IGiveawayRandom>().Should().BeOfType<SecureGiveawayRandom>();
        });
        host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().NotContain(s => s is GiveawayWorker, "tests and CLI verbs run no worker");
    }

    [Fact]
    public async Task Manifest_has_giveaway_as_an_admin_command_with_four_subcommands_and_turkish_descriptions()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();
        manifest.Commands.Where(c => c.OwnerModule == "giveaway").Select(c => c.Name).Should().Equal("giveaway");

        var giveaway = manifest.Find("giveaway")!;
        giveaway.DefaultMemberPermissions.Should().Be("32", "Manage Server, as every TSQ admin command");
        giveaway.Contexts.Should().Equal(0);
        giveaway.Options.Select(o => o.Name).Should().Equal("create", "end", "cancel", "reroll");
        giveaway.Options[0].Options.Should().BeEmpty("create opens a form");
        foreach (var sub in giveaway.Options.Skip(1))
        {
            var target = sub.Options.Should().ContainSingle().Subject;
            (target.Name, target.Required, target.Autocomplete, target.MaxLength).Should().Be(("giveaway", true, true, (int?)GiveawayTarget.MaxInputLength));
        }

        var properties = DiscordCommandRegistrar.ToProperties(giveaway);
        properties.DescriptionLocalizations["tr"].Should().Be("Çekiliş başlat ve yönet (yöneticiler)");
        properties.Options.Value.Should().OnlyContain(o => o.Description.Length <= 100 && o.DescriptionLocalizations["tr"].Length <= 100);
    }

    [Fact]
    public void The_form_fits_discords_modal_limits_in_both_languages()
    {
        var catalog = Catalog();
        foreach (var language in new[] { "tr", "en" })
        {
            var modal = GiveawayFormUi.Modal(key => catalog.Get(language, key));
            modal.Title.Length.Should().BeLessThanOrEqualTo(GiveawayFormUi.MaxTitleLength);
            modal.CustomId.Should().Be(GiveawayFormUi.ModalId);
            foreach (var key in new[] { "giveaway.form.prize", "giveaway.form.duration", "giveaway.form.winners", "giveaway.form.description" })
                catalog.Get(language, key).Length.Should().BeLessThanOrEqualTo(GiveawayFormUi.MaxTitleLength, key);
            foreach (var key in new[] { "giveaway.form.prize_hint", "giveaway.form.duration_hint", "giveaway.form.winners_hint", "giveaway.form.description_hint" })
                catalog.Get(language, key).Length.Should().BeLessThanOrEqualTo(GiveawayFormUi.MaxHintLength, key);
        }
    }

    [Fact]
    public void Giveaway_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));

        foreach (Match m in KeyLiteral().Matches(Code()))
            tr.Should().ContainKey(m.Groups[1].Value);

        var catalog = Catalog();
        catalog.Get("tr", "giveaway.card.no_entrants").Should().Be("Çekiliş sona erdi ancak geçerli katılımcı bulunamadı.");
        catalog.Get("tr", "giveaway.card.cancelled").Should().Be("Bu çekiliş iptal edildi.");
    }

    /// <summary>Every module's catalog together (no duplicate keys across modules).</summary>
    private static LocalizationCatalog Catalog() => new(
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
        new LocalizationSource(typeof(ToroSquad.Modules.Timezone.TimezoneModule).Assembly, "ToroSquad.Modules.Timezone.Localization"),
        new LocalizationSource(Giveaway, "ToroSquad.Modules.Giveaway.Localization"),
    ]);

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"""((?:giveaway|module\.giveaway)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();
}
