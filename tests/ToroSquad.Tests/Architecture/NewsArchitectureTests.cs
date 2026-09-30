using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ToroSquad.Core.Localization;
using ToroSquad.Discord.Interactions;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ News boundaries: isolated from other feature modules, no Discord SDK in business code, commands never reach the
/// network, providers never reach Discord or the outbox, only the planner stages. The HLTV exception is narrow: the only
/// HLTV address any code requests is the official RSS feed, from exactly one class. No ping opt-in, injected clock only,
/// matching localization catalogs.
/// </summary>
public sealed partial class NewsArchitectureTests
{
    private static readonly Assembly News = typeof(ToroSquad.Modules.News.NewsModule).Assembly;
    private static readonly Assembly Core = typeof(ToroSquad.Core.Modules.IToroModule).Assembly;
    private static readonly Assembly Infrastructure = typeof(ToroSquad.Infrastructure.Persistence.ToroDbContext).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, Infrastructure, DiscordLayer, News).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void News_and_other_feature_modules_never_reference_each_other()
    {
        References(News).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.News");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module),
                     typeof(ToroSquad.Modules.Volleyball.VolleyballModule), typeof(ToroSquad.Modules.Live.LiveModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.News");
        References(Core).Should().NotContain("ToroSquad.Modules.News");
        References(Infrastructure).Should().NotContain("ToroSquad.Modules.News");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.News");
    }

    [Theory]
    [InlineData("ToroSquad.Modules.News.Domain")]
    [InlineData("ToroSquad.Modules.News.Providers")]
    [InlineData("ToroSquad.Modules.News.Application")]
    [InlineData("ToroSquad.Modules.News.Persistence")]
    public void News_business_code_does_not_use_the_discord_sdk(string ns) =>
        AssertNoViolations(Types().That().ResideInNamespace(ns).Or().ResideInNamespaceMatching(ns + @"\..*")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void News_domain_is_pure() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.News.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(
                @"^(Microsoft\.EntityFrameworkCore|ToroSquad\.Infrastructure|System\.Net\.Http|ToroSquad\.Modules\.News\.(Providers|Application|Persistence|Commands))(\..*)?$")));

    [Fact]
    public void Commands_never_reach_a_provider_or_the_network() =>
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.News.Commands")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^(HltvRssClient|LiquipediaRosterClient|NewsPoller|NewsPlanner|HttpClient|IHttpClientFactory)$")));

    [Fact]
    public void Providers_never_talk_to_discord_or_the_outbox_and_only_the_planner_stages()
    {
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.News\.Providers(\..*)?$").Should().NotDependOnAny(
            Types().That().HaveNameMatching(@"^(IMessageTransport|INotificationOutbox|OutgoingMessage|NotificationRequest|IDeliveryPolicy|MentionPolicy)$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.News(\..*)?$")
            .And().DoNotHaveNameMatching(@"^NewsPlanner$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^INotificationOutbox$")));
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.News(\..*)?$")
            .Should().NotDependOnAny(Types().That().HaveNameMatching(@"^IMessageTransport$")));
    }

    /// <summary>
    /// The one HLTV request in the code base is the official RSS feed, made by <c>HltvRssClient</c>. No other file names the
    /// feed, and no News code builds a request to any other HLTV address (article, team, match or image pages).
    /// </summary>
    [Fact]
    public void The_only_hltv_request_is_the_official_rss_feed()
    {
        var src = Path.Combine(CommandManifestTests.RepoRoot(), "src");
        var client = Path.Combine("ToroSquad.Modules.News", "Providers", "HltvRssClient.cs");
        foreach (var file in SourceFiles(src))
        {
            var relative = Path.GetRelativePath(src, file);
            var code = File.ReadAllText(file);
            if (relative != client)
                code.Should().NotContain("hltv.org/rss", relative);
        }

        var clientCode = File.ReadAllText(Path.Combine(src, client));
        clientCode.Should().Contain("public const string FeedUrl = \"https://www.hltv.org/rss/news\";");
        HltvUrlLiteral().Matches(clientCode).Select(m => m.Value).Distinct().Should().Equal("https://www.hltv.org/rss/news");
        clientCode.Should().Contain("new HttpRequestMessage(HttpMethod.Get, new Uri(FeedUrl))");

        // Only the two provider clients build requests; nothing uses the shortcut GET helpers.
        foreach (var file in SourceFiles(Root()))
        {
            var name = Path.GetFileName(file);
            var code = File.ReadAllText(file);
            if (name is not ("HltvRssClient.cs" or "LiquipediaRosterClient.cs"))
                code.Should().NotContain("new HttpRequestMessage", name).And.NotContain(".SendAsync(", name).And.NotContain("CreateClient(", name);
            code.Should().NotContain("GetStringAsync", name).And.NotContain("GetStreamAsync", name).And.NotContain("GetByteArrayAsync", name);
        }

        File.ReadAllText(Path.Combine(Root(), "Providers", "LiquipediaRosterClient.cs")).Should().Contain("\"https://liquipedia.net/counterstrike/api.php\"")
            .And.NotContain("hltv.org");
    }

    [Fact]
    public void News_never_opts_in_to_a_ping()
    {
        foreach (var file in SourceFiles(Root()))
            PingOptIn().IsMatch(File.ReadAllText(file)).Should().BeFalse(Path.GetFileName(file));
    }

    [Fact]
    public void Every_news_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in News.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
            type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!.ModuleId.Should().Be("news", type.Name);
        var module = new ToroSquad.Modules.News.NewsModule();
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().Equal("tsq-admin news");
    }

    [Fact]
    public void News_code_uses_the_injected_clock()
    {
        foreach (var file in SourceFiles(Root()))
        {
            var code = File.ReadAllText(file);
            code.Should().NotContain("DateTime.Now", Path.GetFileName(file)).And.NotContain("DateTime.UtcNow", Path.GetFileName(file))
                .And.NotContain("DateTimeOffset.UtcNow", Path.GetFileName(file)).And.NotContain("DateTimeOffset.Now", Path.GetFileName(file));
        }
    }

    [Fact]
    public void News_localization_catalogs_match_and_every_used_key_exists()
    {
        var dir = Path.Combine(Root(), "Localization");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "tr.json")))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "en.json")))!;
        tr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var key in tr.Keys)
            Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), key);
        tr.Values.Concat(en.Values).Should().NotContain(v => v.Contains("ToroSquad", StringComparison.OrdinalIgnoreCase));

        var code = string.Join("\n", SourceFiles(Root()).Select(File.ReadAllText));
        foreach (Match m in KeyLiteral().Matches(code))
            tr.Should().ContainKey(m.Groups[1].Value);

        var catalog = new LocalizationCatalog(
        [
            new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Esports.EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
            new LocalizationSource(typeof(ToroSquad.Modules.Live.LiveModule).Assembly, "ToroSquad.Modules.Live.Localization"),
            new LocalizationSource(News, "ToroSquad.Modules.News.Localization"),
        ]);
        catalog.Get("tr", "news.card.footer").Should().Be("Kaynak: HLTV");
        catalog.Get("tr", "news.card.read").Should().Be("HLTV’de oku");
    }

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.News");

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or "Migrations"));

    private static IEnumerable<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"""((?:news|module\.news)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"https://www\.hltv\.org[^""\s]*")]
    private static partial Regex HltvUrlLiteral();

    [GeneratedRegex(@"EveryoneOnly|ExplicitUsers|Everyone\s*:\s*true|Everyone\s*=\s*true|RoleMention|new MentionPolicy\(")]
    private static partial Regex PingOptIn();
}
