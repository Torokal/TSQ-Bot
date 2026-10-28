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
using ToroSquad.Modules.Pfp;
using ToroSquad.Modules.Pfp.Application;
using ToroSquad.Modules.Pfp.Commands;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Profil Görseli boundaries and integration: a stateless module (no storage, no migration, no background work, no
/// configuration, no package, no image processing, no AI, no own HTTP), isolated from the other feature modules, no Discord
/// SDK outside Commands, no wrong picture sources (the SDK's display-avatar fallback, the accent colour, the guild's own
/// banner), replies never ping; registered and gated through /modules like every optional module (off by default), and
/// /pfp and /banner in the manifest as public guild commands with a native USER option and three fixed choices.
/// </summary>
public sealed partial class PfpArchitectureTests
{
    private static readonly Assembly Pfp = typeof(PfpModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Pfp).Build());

    private static readonly GuildId Guild = new(42);

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Pfp");

    private static IEnumerable<string> SourceFiles() => Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static string Code() => string.Join("\n", SourceFiles().Select(File.ReadAllText));

    /// <summary>The code without comments (which explain, by name, what the module deliberately does not use).</summary>
    private static string CodeWithoutComments() => Regex.Replace(Code(), @"(?<!:)//.*$", "", RegexOptions.Multiline);

    [Fact]
    public void Pfp_is_isolated_stateless_and_has_no_packages()
    {
        References(Pfp).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Pfp");
        References(Pfp).Should().NotContain(r => r == "ToroSquad.Infrastructure" || r == "ToroSquad.Ai" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
            "no tables, no outbox, no database, no AI");
        References(Pfp).Should().NotContain(r => Regex.IsMatch(r, @"SixLabors|ImageSharp|Drawing|Microsoft\.Extensions\.Http|Caching|Redis|OpenAI|Anthropic", RegexOptions.IgnoreCase),
            "no image processing, no own HTTP client, no cache, no AI (System.Net.Http only for HttpRequestException)");
        Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "Migrations"))
            .Should().NotContain(f => Path.GetFileName(f).Contains("Pfp", StringComparison.OrdinalIgnoreCase), "no migration");
        References(Core).Should().NotContain("ToroSquad.Modules.Pfp");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Pfp");
        File.ReadAllText(Path.Combine(Root(), "ToroSquad.Modules.Pfp.csproj")).Should().NotContain("<PackageReference");
        new PfpModule().ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
        Pfp.GetTypes().Should().NotContain(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t));
    }

    [Fact]
    public void Application_never_uses_the_discord_sdk()
    {
        AssertNoViolations(Types().That().ResideInNamespace("ToroSquad.Modules.Pfp.Application")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));
    }

    [Fact]
    public void Pictures_come_only_from_the_members_own_profile_and_nothing_is_stored_or_downloaded()
    {
        var code = CodeWithoutComments();
        // GetDisplayAvatarUrl falls back from the server avatar to the global one (wrong for "Genel"); ImageFormat.Auto makes
        // animated pictures GIF; the accent colour and the guild's own banner are not the member's picture.
        code.Should().NotContain("GetDisplayAvatarUrl").And.NotContain("DisplayAvatarId").And.NotContain("ImageFormat")
            .And.NotContain("AccentColor").And.NotContain("BannerColor").And.NotContain("Guild.BannerId").And.NotContain("BannerUrl")
            .And.NotContain("GetBannerUrl").And.NotContain("GetAvatarUrl").And.NotContain("GetGuildAvatarUrl").And.NotContain("GetGuildBannerUrl");
        // No downloads, uploads, files or members lists.
        code.Should().NotContain("HttpClient").And.NotContain("FileAttachment").And.NotContain("SendFileAsync").And.NotContain("File.")
            .And.NotContain("GetUsersAsync").And.NotContain("DownloadUsersAsync").And.NotContain("CacheMode");
        // The member read is Get Guild + Get Guild Member, the global banner Get User; all REST, nothing else.
        var adapter = File.ReadAllText(Path.Combine(Root(), "Commands", "DiscordPfpProfileSource.cs"));
        Regex.Matches(adapter, @"rest\.(\w+)\(").Select(m => m.Groups[1].Value).Should().Equal("GetGuildAsync", "GetUserAsync");
        Regex.Matches(adapter, @"guild\.(\w+)\(").Select(m => m.Groups[1].Value).Should().Equal("GetUserAsync");
        adapter.Should().Contain("user.BannerId").And.NotContain("member.BannerId");
    }

    [Fact]
    public void Replies_go_through_the_base_class_never_ping_and_logs_carry_no_text_or_tokens()
    {
        var code = CodeWithoutComments();
        code.Should().NotContain("MentionPolicy.EveryoneOnly").And.NotContain("ExplicitUsers").And.NotContain("allowedMentions").And.NotContain("AllowedMentions");
        var commands = File.ReadAllText(Path.Combine(Root(), "Commands", "PfpCommands.cs"));
        commands.Should().NotMatchRegex(@"\b(RespondAsync|FollowupAsync|ModifyOriginalResponseAsync|SendMessageAsync)\(");
        commands.Should().Contain("DeferAsync(ephemeral: false)").And.Contain("SendEphemeralAsync(text, null, null)")
            .And.Contain("SendAsync(null, ToEmbed(card), ToComponents(card), ephemeral: false)").And.Contain("SendAsync(reply.Text, null, null, ephemeral: false)");
        commands.Should().NotContain("WithThumbnailUrl").And.Contain("WithImageUrl(card.ImageUrl)").And.Contain("url: card.ImageUrl");

        foreach (Match call in Regex.Matches(code, @"\blogger\.Log\w+\((?<args>[^;]*)\);"))
        {
            var args = Regex.Replace(call.Groups["args"].Value, "\"[^\"]*\"", "\"\"");
            args.Should().NotMatchRegex(@"\b(ex|token|Token|Message|Url|url|DisplayName|card|reply)\b(?!\.GetType)", "metadata only, never exception text, URLs, names or tokens");
        }
    }

    [Fact]
    public void Every_pfp_interaction_class_declares_its_module_and_the_module_is_off_by_default()
    {
        foreach (var type in Pfp.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("pfp", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
        }

        var module = new PfpModule();
        module.Descriptor.Id.Value.Should().Be("pfp");
        module.Descriptor.EnabledByDefault.Should().BeFalse("like every optional utility module");
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().BeEmpty();
        module.InteractionModuleTypes.Should().Equal(typeof(PfpCommands));
    }

    [Fact]
    public async Task Module_is_listed_in_modules_off_by_default_gated_and_its_services_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        var gated = new ToroModuleAttribute(PfpModule.ModuleIdValue);
        await host.InScopeAsync(async sp =>
        {
            var localizer = sp.GetRequiredService<ILocalizer>();
            var modules = sp.GetRequiredService<ModuleManagementService>();
            var listed = (await modules.ListAsync(Guild, CancellationToken.None)).Single(m => m.Descriptor.Id == PfpModule.ModuleIdTyped);
            listed.Enabled.Should().BeFalse();
            localizer.Get("tr", listed.Descriptor.NameKey).Should().Be("TSQ Profil Görseli");
            localizer.Get("tr", listed.Descriptor.DescriptionKey).Should().Contain("/pfp").And.Contain("/banner");

            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);
            (await modules.SetEnabledAsync(TestHost.Admin(Guild), "pfp", true, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await gated.CheckRequirementsAsync(Context(Guild), null!, sp)).IsSuccess.Should().BeTrue("a member without any permission may use it");
            (await gated.CheckRequirementsAsync(Context(new GuildId(43)), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError, "per guild");
            (await gated.CheckRequirementsAsync(InterfaceFake.Create<IInteractionContext>(new()), null!, sp)).ErrorReason
                .Should().Be(ToroModuleAttribute.GuildOnlyError, "a DM never runs it");

            sp.GetRequiredService<PfpCommandFlow>().Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Manifest_has_pfp_and_banner_as_public_guild_commands_with_a_native_user_picker_and_three_types()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();
        manifest.Commands.Where(c => c.OwnerModule == "pfp").Select(c => c.Name).Should().BeEquivalentTo("pfp", "banner");

        foreach (var (name, english, turkish) in new[]
                 {
                     ("pfp", "Show a member's profile picture large", "Bir üyenin profil fotoğrafını büyük gösterir."),
                     ("banner", "Show a member's profile banner large", "Bir üyenin profil banner’ını büyük gösterir."),
                 })
        {
            var command = manifest.Find(name)!;
            command.DefaultMemberPermissions.Should().BeNull($"/{name} is for everyone");
            command.Contexts.Should().Equal(0);
            command.IntegrationTypes.Should().Equal(0);
            command.Options.Select(o => o.Name).Should().Equal("uye", "tur");

            var uye = command.Options[0];
            (uye.Type, (int)uye.Type, uye.Required, uye.Autocomplete).Should().Be((OptionType.User, 6, false, false));
            uye.Choices.Should().BeEmpty("Discord's own member picker, no autocomplete or typed names");
            var tur = command.Options[1];
            (tur.Type, tur.Required, tur.Autocomplete).Should().Be((OptionType.String, false, false));
            tur.Choices.Select(c => (c.Name, c.Value, c.NameLocalizations["tr"])).Should().Equal(
                ("Otomatik", "otomatik", "Otomatik"), ("Genel", "genel", "Genel"), ("Sunucu", "sunucu", "Sunucu"));

            var properties = DiscordCommandRegistrar.ToProperties(command);
            (properties.Description.Value, properties.DescriptionLocalizations["tr"]).Should().Be((english, turkish));
            properties.Options.Value.Should().OnlyContain(o => o.IsRequired != true && o.DescriptionLocalizations["tr"].Length <= 100);
            properties.Options.Value[0].Type.Should().Be(ApplicationCommandOptionType.User);
        }
    }

    [Fact]
    public void Pfp_localization_catalogs_match_and_every_used_key_exists()
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
        foreach (var source in Enum.GetValues<PfpSource>())
            tr.Should().ContainKey(PfpCards.SourceKey(source));
        tr["pfp.open_full"].Should().Be("Tam boy aç");

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
            new LocalizationSource(Pfp, "ToroSquad.Modules.Pfp.Localization"),
        ]);
        catalog.Get("tr", "pfp.source.default_avatar").Should().Be("Varsayılan profil fotoğrafı");
        catalog.Get("en", "pfp.open_full").Should().Be("Open full size");
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

    [GeneratedRegex(@"""((?:pfp|module\.pfp)\.[a-z0-9_.\-]+)""")]
    private static partial Regex KeyLiteral();
}
