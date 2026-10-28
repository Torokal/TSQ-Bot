using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Agenda;
using ToroSquad.Modules.Agenda.Application;
using ToroSquad.Modules.Agenda.Commands;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Ajanda boundaries: a stateless view module (no storage, migration, background job, configuration, package or own
/// HTTP) that references no feature module — each source module implements the Core <see cref="IAgendaSource"/> contract in
/// its own project, read-only (no provider call, no outbox staging, no write) and result-free (no score, set, map, winner,
/// classification, standings or race-control field is read). Off by default, gated, two flat public guild commands.
/// </summary>
public sealed partial class AgendaArchitectureTests
{
    private static readonly Assembly Agenda = typeof(AgendaModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Agenda).Build());

    private static readonly GuildId Guild = new(42);

    private static readonly string[] Adapters =
    [
        Path.Combine("ToroSquad.Modules.Formula1", "Application", "Formula1AgendaSource.cs"),
        Path.Combine("ToroSquad.Modules.Esports", "Application", "EsportsAgendaSource.cs"),
        Path.Combine("ToroSquad.Modules.Volleyball", "Application", "VolleyballAgendaSource.cs"),
        Path.Combine("ToroSquad.Modules.Lfg", "Application", "LfgAgendaSource.cs"),
        Path.Combine("ToroSquad.Modules.Predictions", "Application", "PredictionsAgendaSource.cs"),
    ];

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Src() => Path.Combine(CommandManifestTests.RepoRoot(), "src");

    private static string Root() => Path.Combine(Src(), "ToroSquad.Modules.Agenda");

    private static string Code() => string.Join("\n", Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"))
        .Select(File.ReadAllText));

    private static string WithoutComments(string code) => Regex.Replace(code, @"(?<!:)//.*$", "", RegexOptions.Multiline);

    [Fact]
    public void Agenda_is_isolated_stateless_and_references_no_feature_module()
    {
        References(Agenda).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Agenda");
        References(Agenda).Should().NotContain(r => r == "ToroSquad.Infrastructure" || r == "ToroSquad.Ai" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
            "no tables, no outbox, no database, no AI");
        File.ReadAllText(Path.Combine(Root(), "ToroSquad.Modules.Agenda.csproj")).Should().NotContain("<PackageReference");
        Directory.GetFiles(Path.Combine(Src(), "ToroSquad.Bot", "Migrations"))
            .Should().NotContain(f => Path.GetFileName(f).Contains("Agenda", StringComparison.OrdinalIgnoreCase), "no migration");
        Agenda.GetTypes().Should().NotContain(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t), "no polling, no scheduled post");
        new AgendaModule().ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
        foreach (var other in new[] { Core, DiscordLayer, typeof(ToroSquad.Modules.Formula1.Formula1Module).Assembly, typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly,
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule).Assembly, typeof(ToroSquad.Modules.Lfg.LfgModule).Assembly,
                     typeof(ToroSquad.Modules.Predictions.PredictionsModule).Assembly })
            References(other).Should().NotContain("ToroSquad.Modules.Agenda");
    }

    [Fact]
    public void Application_never_uses_the_discord_sdk_and_time_comes_from_the_injected_clock()
    {
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Agenda.Application")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));
        foreach (var code in Adapters.Select(a => File.ReadAllText(Path.Combine(Src(), a))).Append(Code()))
            WithoutComments(code).Should().NotMatchRegex(@"DateTime(Offset)?\.(Now|UtcNow|Today)");
    }

    [Fact]
    public void Source_adapters_are_read_only_and_never_read_results()
    {
        foreach (var adapter in Adapters)
        {
            var code = WithoutComments(File.ReadAllText(Path.Combine(Src(), adapter)));
            code.Should().NotMatchRegex(@"\b(GetMatchesAsync|GetEventsAsync|GetLiveStateAsync|GetScheduleAsync|GetResultsAsync|HttpClient|INotificationOutbox|StageAsync)\b",
                $"{adapter}: no provider call, no notification");
            code.Should().NotMatchRegex(@"\b(SaveChanges\w*|ExecuteUpdate\w*|ExecuteDelete\w*)\b|\bdb\.[^;]*\.(Add|AddRange|Update|UpdateRange|Remove|RemoveRange|Attach)\(",
                $"{adapter}: nothing is written");
            code.Should().NotMatchRegex(@"\.(Score|WinnerIndex|IsDraw|IsForfeit|Maps|HomeSets|AwaySets|Sets|CurrentSet\w*|Results|Standings\w*|Details|VoiceChannelId|OwnerUserId)\b|RaceControl|F1Incident",
                $"{adapter}: programme only — no results, race control, members or listing details");
            code.Should().NotMatchRegex(@"\b(WinningOutcome\w*|WinnerCount|Payout\w*|Refund\w*|EntryCount|StakeTotal\w*|CreatorName|CreatorUserId|CancelReason|Rules|PredictionEntryEntity|PredictionOutcomeEntity|PredictionWalletEntity|PredictionLedger\w*|PredictionStanding\w*)\b",
                $"{adapter}: predictions as programme only — no outcome, entries, coins, rules or creator");
            code.Should().Contain(": IAgendaSource");
        }
    }

    [Fact]
    public void Every_agenda_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Agenda.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("agenda", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new AgendaModule();
        module.Descriptor.Id.Value.Should().Be("agenda");
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().BeEmpty("no /ajanda-admin, no /tsq-admin operations: /modules enable|disable is enough");
        module.InteractionModuleTypes.Should().Equal(typeof(AgendaCommands));
    }

    [Fact]
    public async Task Each_source_module_registers_its_own_source_and_the_module_is_gated()
    {
        await using var host = await TestHost.CreateAsync();
        var gated = new ToroModuleAttribute(AgendaModule.ModuleIdValue);
        await host.InScopeAsync(async sp =>
        {
            var sources = sp.GetServices<IAgendaSource>().ToList();
            sources.Select(s => (s.Kind, s.Module.Value)).Should().BeEquivalentTo(new[]
            {
                (AgendaKind.Formula1, "formula1"), (AgendaKind.Esports, "esports"), (AgendaKind.Volleyball, "volleyball"), (AgendaKind.Lfg, "lfg"),
                (AgendaKind.Predictions, "predictions"),
            });
            sp.GetRequiredService<AgendaFlow>().Should().NotBeNull();

            var modules = sp.GetRequiredService<ModuleManagementService>();
            var listed = (await modules.ListAsync(Guild, CancellationToken.None)).Single(m => m.Descriptor.Id == AgendaModule.ModuleIdTyped);
            listed.Enabled.Should().BeFalse();
            sp.GetRequiredService<ILocalizer>().Get("tr", listed.Descriptor.NameKey).Should().Be("TSQ Ajanda");
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);
            (await modules.SetEnabledAsync(TestHost.Admin(Guild), "agenda", true, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).IsSuccess.Should().BeTrue("a member without any permission may use it");
            (await gated.CheckRequirementsAsync(InterfaceFake.Create<IInteractionContext>(new()), null!, sp)).ErrorReason
                .Should().Be(ToroModuleAttribute.GuildOnlyError, "a DM never runs it");
        });
    }

    [Fact]
    public async Task Manifest_has_two_flat_public_guild_commands_with_fixed_choices()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();
        manifest.Commands.Where(c => c.OwnerModule == "agenda").Select(c => c.Name).Should().BeEquivalentTo("ajanda", "gundem");

        foreach (var name in new[] { "ajanda", "gundem" })
        {
            var command = manifest.Find(name)!;
            command.DefaultMemberPermissions.Should().BeNull($"/{name} is for everyone");
            command.Contexts.Should().Equal(0);
            command.IntegrationTypes.Should().Equal(0);
            command.Options.Should().OnlyContain(o => o.Type == OptionType.String && !o.Required && !o.Autocomplete, "no subcommands or groups");
        }

        manifest.Find("gundem")!.Options.Should().BeEmpty();
        var ajanda = manifest.Find("ajanda")!;
        ajanda.Options.Select(o => o.Name).Should().Equal("spor", "donem");
        ajanda.Options[0].Choices.Select(c => (c.Value, c.NameLocalizations["tr"])).Should().Equal(
            ("tum", "Tüm sporlar"), ("f1", "Formula 1"), ("cs2", "CS2"), ("voleybol", "Voleybol"));
        ajanda.Options[1].Choices.Select(c => (c.Value, c.NameLocalizations["tr"])).Should().Equal(
            ("bugun", "Bugün"), ("yarin", "Yarın"), ("7-gun", "Önümüzdeki 7 gün"), ("14-gun", "Önümüzdeki 14 gün"));
        foreach (var c in ajanda.Options.SelectMany(o => o.Choices))
            AgendaOptions.Sport(c.Value).ToString().Should().NotBeNull();
        AgendaOptions.Period("bugun").Should().Be(AgendaPeriod.Today);
    }

    [Fact]
    public void Agenda_localization_catalogs_match_and_every_used_key_exists()
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
        foreach (var sport in Enum.GetValues<AgendaSport>())
            tr.Should().ContainKey("agenda.sport." + sport.ToString().ToLowerInvariant());
        foreach (var period in Enum.GetValues<AgendaPeriod>())
            tr.Should().ContainKey("agenda.period." + period.ToString().ToLowerInvariant());
        foreach (var kind in Enum.GetValues<AgendaKind>())
            tr.Should().ContainKey("agenda.kind." + AgendaRenderer.KindKey(kind));

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
            new LocalizationSource(typeof(ToroSquad.Modules.Timezone.TimezoneModule).Assembly, "ToroSquad.Modules.Timezone.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Giveaway.GiveawayModule).Assembly, "ToroSquad.Modules.Giveaway.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Summary.SummaryModule).Assembly, "ToroSquad.Modules.Summary.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Sor.SorModule).Assembly, "ToroSquad.Modules.Sor.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Predictions.PredictionsModule).Assembly, "ToroSquad.Modules.Predictions.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.News.NewsModule).Assembly, "ToroSquad.Modules.News.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Updates.UpdatesModule).Assembly, "ToroSquad.Modules.Updates.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Pfp.PfpModule).Assembly, "ToroSquad.Modules.Pfp.Localization"),
            new LocalizationSource(Agenda, "ToroSquad.Modules.Agenda.Localization"),
        ]);
        catalog.Get("tr", "agenda.lfg.full", 6, 6).Should().Be("6/6 · Dolu / bekleme listesi");
        catalog.Get("en", "agenda.footer", 1, 2, "UTC").Should().Be("Page 1/2 · Days: UTC · Times in your local time");
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

    /// <summary>Full key literals only (a literal ending in '.' is a prefix completed by an enum value, checked above).</summary>
    [GeneratedRegex(@"""((?:agenda|module\.agenda)\.[a-z0-9_.\-]*[a-z0-9_\-])""")]
    private static partial Regex KeyLiteral();
}
