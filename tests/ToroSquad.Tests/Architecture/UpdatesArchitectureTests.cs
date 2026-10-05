using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ToroSquad.Core.Localization;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Updates.Persistence;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Bot Updates boundaries: isolated from other feature modules, no Discord SDK in business code, and generic by
/// construction — domain, application, persistence and commands never see a provider's own types, and only the game's own
/// folder and the module registration name a game. Commands never reach the network, providers never reach Discord or
/// the outbox, only the planner stages, the renderer makes no request. One fixed public Steam endpoint, no key. No ping
/// opt-in, injected clock only, no post text stored or logged, matching localization catalogs.
/// </summary>
public sealed partial class UpdatesArchitectureTests
{
    private static readonly Assembly Updates = typeof(ToroSquad.Modules.Updates.UpdatesModule).Assembly;
    private static readonly Assembly Core = typeof(ToroSquad.Core.Modules.IToroModule).Assembly;
    private static readonly Assembly Infrastructure = typeof(ToroSquad.Infrastructure.Persistence.ToroDbContext).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, Infrastructure, DiscordLayer, Updates).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Updates_and_other_feature_modules_never_reference_each_other()
    {
        References(Updates).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Updates");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                     typeof(ToroSquad.Modules.News.NewsModule), typeof(ToroSquad.Modules.Lfg.LfgModule),
                     typeof(ToroSquad.Modules.Birthday.BirthdayModule), typeof(ToroSquad.Modules.Currency.CurrencyModule),
                     typeof(ToroSquad.Modules.Giveaway.GiveawayModule), typeof(ToroSquad.Modules.Predictions.PredictionsModule),
                     typeof(ToroSquad.Modules.Quote.QuoteModule), typeof(ToroSquad.Modules.Summary.SummaryModule),
                     typeof(ToroSquad.Modules.Randomizer.RandomizerModule), typeof(ToroSquad.Modules.Timezone.TimezoneModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Updates", other.Name);
        References(Core).Should().NotContain("ToroSquad.Modules.Updates");
        References(Infrastructure).Should().NotContain("ToroSquad.Modules.Updates");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Updates");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.Updates.Domain")]
    [InlineData("ToroSquad.Modules.Updates.Providers")]
    [InlineData("ToroSquad.Modules.Updates.Application")]
    [InlineData("ToroSquad.Modules.Updates.Persistence")]
    public void Updates_business_code_does_not_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns).Or().ResideInNamespaceMatching(ns + @"\..*")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void The_domain_is_pure() =>
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates\.Domain(\..*)?$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(
                @"^(Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure|ToroSquad\.Discord|System\.Net\.Http|ToroSquad\.Modules\.Updates\.(Providers|Application|Persistence|Commands))(\..*)?$")));

    /// <summary>
    /// Steam is one provider behind <c>IGameUpdateProvider</c>: nothing outside the Providers folder (and the registration in
    /// UpdatesModule) depends on a provider's own types, so another provider never has to touch the Steam code — or the planner.
    /// </summary>
    [Fact]
    public void Provider_specific_types_never_leak_out_of_the_providers_folder()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates\.(Domain|Application|Persistence|Commands)(\..*)?$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates\.Providers(\..*)?$")));
        foreach (var file in SourceFiles(Root()))
        {
            var relative = Path.GetRelativePath(Root(), file);
            if (relative.StartsWith("Providers", StringComparison.Ordinal) || relative == "UpdatesModule.cs")
                continue;
            SteamIdentifier().IsMatch(File.ReadAllText(file)).Should().BeFalse($"{relative} must not name a Steam type");
        }
    }

    /// <summary>A game is a definition: only its own folder and the registration know Counter-Strike 2 exists.</summary>
    [Fact]
    public void Only_the_games_folder_and_the_registration_name_a_game()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates\.(Providers|Application|Persistence|Commands)(\..*)?$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates\.Domain\.Games(\..*)?$")));
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Updates.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates\.Domain\.Games(\..*)?$")));
        foreach (var file in SourceFiles(Root()))
        {
            var relative = Path.GetRelativePath(Root(), file);
            if (relative.StartsWith(Path.Combine("Domain", "Games"), StringComparison.Ordinal) || relative == "UpdatesModule.cs")
                continue;
            GameIdentifier().IsMatch(File.ReadAllText(file)).Should().BeFalse($"{relative} must not reference a game's own types");
        }

        // No guild, channel or user id is written into the source: 17-20 digit literals do not exist in this module.
        foreach (var file in SourceFiles(Root()))
            Snowflake().IsMatch(File.ReadAllText(file)).Should().BeFalse(Path.GetFileName(file));
    }

    [Fact]
    public void Commands_never_reach_a_provider_the_planner_or_the_network() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Updates.Commands")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(IGameUpdateProvider|SteamNewsUpdateProvider|UpdatesPoller|UpdatesPlanner|HttpClient|IHttpClientFactory|INotificationOutbox)$")));

    [Fact]
    public void Providers_never_talk_to_discord_or_the_outbox_and_only_the_planner_stages()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates\.Providers(\..*)?$").Should().NotDependOnAny(
            Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|OutgoingMessage|NotificationRequest|IDeliveryPolicy|MentionPolicy|ToroDbContext)$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates(\..*)?$")
            .And().DoNotHaveNameMatching(@"^UpdatesPlanner$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^INotificationOutbox$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Updates(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^IMessageTransport$")));
    }

    [Fact]
    public void The_renderer_makes_no_request_and_knows_no_database() =>
        AssertNoViolations(Types().That().HaveNameMatching(@"^UpdateCardRenderer$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^(System\.Net\.Http|Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure)(\..*)?$")));

    /// <summary>
    /// One fixed address — the public GetNewsForApp method — requested from exactly one class. No key parameter, no
    /// Authorization header, no publisher endpoint, and no user-supplied URL ever reaches an HTTP call.
    /// </summary>
    [Fact]
    public void The_only_request_is_the_public_steam_news_method_without_a_key()
    {
        var provider = Path.Combine("Providers", "SteamNewsUpdateProvider.cs");
        foreach (var file in SourceFiles(Root()))
        {
            var relative = Path.GetRelativePath(Root(), file);
            var code = File.ReadAllText(file);
            if (relative != provider)
            {
                code.Should().NotContain("api.steampowered.com", relative);
                code.Should().NotContain("new HttpRequestMessage", relative).And.NotContain(".SendAsync(", relative).And.NotContain("CreateClient(", relative);
            }

            code.Should().NotContain("GetStringAsync", relative).And.NotContain("GetStreamAsync", relative).And.NotContain("GetByteArrayAsync", relative);
            code.Should().NotContain("https://partner.steam-api.com", relative).And.NotContain("Authorization", relative).And.NotContain("&key=", relative).And.NotContain("?key=", relative);
        }

        var client = File.ReadAllText(Path.Combine(Root(), provider));
        client.Should().Contain("public const string Endpoint = \"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/\";");
        HttpsLiteral().Matches(client).Select(m => m.Value).Distinct().Should().Equal("https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/");
        client.Should().Contain("new HttpRequestMessage(HttpMethod.Get, RequestUri(appId, o.ItemsPerRequest))");
        Regex.Matches(client, @"new HttpRequestMessage").Should().ContainSingle();

        var src = Path.Combine(CommandManifestTests.RepoRoot(), "src");
        foreach (var file in SourceFiles(src).Where(f => !f.StartsWith(Root(), StringComparison.Ordinal)))
            File.ReadAllText(file).Should().NotContain("api.steampowered.com", Path.GetRelativePath(src, file));
    }

    [Fact]
    public void The_post_text_is_neither_stored_nor_logged()
    {
        typeof(UpdatesItemEntity).GetProperties().Select(p => p.Name).Should()
            .NotContain(["Body", "Contents", "Content", "Text", "Labels", "Tags", "Author", "Raw", "Payload"]);
        Updates.GetTypes().Where(t => t.Namespace == "ToroSquad.Modules.Updates.Persistence").SelectMany(t => t.GetProperties()).Select(p => p.Name).Should()
            .NotContain(["Body", "Contents", "RawJson", "Response"]);
        foreach (var file in SourceFiles(Root()))
            LoggedText().IsMatch(File.ReadAllText(file)).Should().BeFalse($"{Path.GetFileName(file)} must not log post text");
    }

    [Fact]
    public void Updates_never_opts_in_to_a_ping()
    {
        foreach (var file in SourceFiles(Root()))
            PingOptIn().IsMatch(File.ReadAllText(file)).Should().BeFalse(Path.GetFileName(file));
    }

    [Fact]
    public void The_module_is_off_by_default_has_no_public_command_and_declares_its_admin_operations()
    {
        foreach (var type in Updates.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
            type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!.ModuleId.Should().Be("updates", type.Name);
        var module = new ToroSquad.Modules.Updates.UpdatesModule();
        module.Descriptor.Id.Value.Should().Be("updates");
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("tsq-admin updates");
        module.InteractionModuleTypes.Should().BeEmpty("the first version is automatic delivery plus admin operations");
        ToroSquad.Modules.Updates.Commands.UpdatesAdminOperations.Definition.Operations.Select(o => o.Id).Should()
            .Equal("configure", "games", "game-enable", "game-disable", "pause", "resume", "preview", "status", "doctor");
        ToroSquad.Modules.Updates.Commands.UpdatesAdminOperations.Definition.Operations.Should()
            .OnlyContain(o => o.Permission == ToroSquad.Core.Security.Authorize.ServerSettings, "every operation needs Manage Server");
    }

    [Fact]
    public void Updates_code_uses_the_injected_clock()
    {
        foreach (var file in SourceFiles(Root()))
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("DateTime.Now", Path.GetFileName(file)).And.NotContain("DateTime.UtcNow", Path.GetFileName(file))
                .And.NotContain("DateTimeOffset.UtcNow", Path.GetFileName(file)).And.NotContain("DateTimeOffset.Now", Path.GetFileName(file));
        }
    }

    [Fact]
    public void Updates_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));

        var code = string.Join("\n", SourceFiles(Root()).Select(File.ReadAllText));
        var used = KeyLiteral().Matches(code).Select(m => m.Groups[1].Value).Distinct().ToList();
        used.Should().NotBeEmpty();
        foreach (var key in used)
            tr.Should().ContainKey(key);
        foreach (var operation in ToroSquad.Modules.Updates.Commands.UpdatesAdminOperations.Definition.Operations)
            tr.Should().ContainKey("admin.updates." + operation.Id);

        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
            new LocalizationSource(Updates, "ToroSquad.Modules.Updates.Localization"),
        ]);
        catalog.Get("tr", "updates.card.title", "CS2").Should().Be("🛠️ CS2 Güncellemesi");
        catalog.Get("tr", "updates.card.line", "Counter-Strike 2").Should().Be("Yeni Counter-Strike 2 güncellemesi yayınlandı.");
        catalog.Get("tr", "updates.card.read_steam").Should().Be("Steam'de Güncelleme Notlarını Gör");
        catalog.Get("tr", "updates.card.footer", "Steam").Should().Be("Kaynak: Steam");
    }

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Updates");

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or "Migrations"));

    private static IEnumerable<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"""((?:updates|module\.updates|admin\.updates)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"https://[^""\s{]*")]
    private static partial Regex HttpsLiteral();

    [GeneratedRegex(@"\bSteam(News|Parse)[A-Za-z]*\b")]
    private static partial Regex SteamIdentifier();

    [GeneratedRegex(@"\bCs2(Game|UpdateClassifier)\b")]
    private static partial Regex GameIdentifier();

    [GeneratedRegex(@"(?<![0-9])[0-9]{17,20}(?![0-9])")]
    private static partial Regex Snowflake();

    [GeneratedRegex(@"\.Log(Trace|Debug|Information|Warning|Error|Critical)\([^;]*\b(Body|Contents|contents|PayloadJson)\b")]
    private static partial Regex LoggedText();

    [GeneratedRegex(@"EveryoneOnly|ExplicitUsers|Everyone\s*:\s*true|Everyone\s*=\s*true|RoleMention|new MentionPolicy\(")]
    private static partial Regex PingOptIn();
}
