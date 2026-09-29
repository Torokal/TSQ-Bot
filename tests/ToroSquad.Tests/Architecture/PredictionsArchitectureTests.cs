using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Modules.Predictions;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Commands;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchModel = ArchUnitNET.Domain.Architecture;
using Assembly = System.Reflection.Assembly;

namespace ToroSquad.Tests.Architecture;

/// <summary>
/// TSQ Öngörü boundaries and integration: isolated from the other feature modules, no Discord SDK outside Commands, no
/// floating point in the economy, one random source (never System.Random) and the injected clock, the card is the only
/// direct send (edits by the card sync, the closing announcement only through the outbox), nothing ever pings, logs carry
/// ids and amounts only, /ongoru has no default member permissions (every subcommand authorizes itself), off by default,
/// catalogs complete, the forms fit Discord's modal limits, and the shared select extension keeps old payload hashes.
/// </summary>
public sealed partial class PredictionsArchitectureTests
{
    private static readonly Assembly Predictions = typeof(PredictionsModule).Assembly;
    private static readonly Assembly Core = typeof(IToroModule).Assembly;
    private static readonly Assembly DiscordLayer = typeof(ToroInteractionModule).Assembly;

    private static readonly Lazy<ArchModel> Arch = new(() => new ArchLoader().LoadAssemblies(Core, DiscordLayer, Predictions).Build());

    private static void AssertNoViolations(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch.Value).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.Should().BeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static string Root() => Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Modules.Predictions");

    private static IEnumerable<string> SourceFiles(params string[] folder) => Directory.GetFiles(Path.Combine([Root(), .. folder]), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static string Code(params string[] folder) => string.Join("\n", SourceFiles(folder).Select(File.ReadAllText));

    private static string Source(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path]));

    private static string WithoutComments(string code) =>
        string.Join("\n", code.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    [Fact]
    public void Predictions_is_isolated_and_has_no_packages_of_its_own()
    {
        References(Predictions).Should().NotContain(r => r.StartsWith("ToroSquad.Modules.", StringComparison.Ordinal) && r != "ToroSquad.Modules.Predictions");
        foreach (var other in new[]
                 {
                     typeof(ToroSquad.Modules.Esports.EsportsModule), typeof(ToroSquad.Modules.Formula1.Formula1Module), typeof(ToroSquad.Modules.Volleyball.VolleyballModule),
                     typeof(ToroSquad.Modules.Live.LiveModule), typeof(ToroSquad.Modules.Lfg.LfgModule), typeof(ToroSquad.Modules.Quote.QuoteModule),
                     typeof(ToroSquad.Modules.Birthday.BirthdayModule), typeof(ToroSquad.Modules.Currency.CurrencyModule), typeof(ToroSquad.Modules.Randomizer.RandomizerModule),
                     typeof(ToroSquad.Modules.Timezone.TimezoneModule), typeof(ToroSquad.Modules.Giveaway.GiveawayModule), typeof(ToroSquad.Modules.Summary.SummaryModule),
                     typeof(ToroSquad.Modules.Example.ExampleModule),
                 })
            References(other.Assembly).Should().NotContain("ToroSquad.Modules.Predictions");
        References(Core).Should().NotContain("ToroSquad.Modules.Predictions");
        References(DiscordLayer).Should().NotContain("ToroSquad.Modules.Predictions");
        Source("ToroSquad.Modules.Predictions.csproj").Should().NotContain("<PackageReference");
        new PredictionsModule().ValidateConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Should().BeEmpty();
    }

    [Fact]
    public void Only_commands_use_the_discord_sdk() =>
        AssertNoViolations(Types().That().ResideInNamespaceMatching(@"^ToroSquad\.Modules\.Predictions\.(Domain|Application|Persistence)$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Discord(\..*)?$")));

    [Fact]
    public void The_economy_uses_integers_one_random_source_and_the_injected_clock()
    {
        var business = WithoutComments(Code("Domain") + Code("Application") + Code("Persistence"));
        business.Should().NotMatchRegex(@"\b(double|float|Math\.Round|MidpointRounding)\b", "coins are long units, odds ×100 integers");
        var code = WithoutComments(Code());
        code.Should().NotMatchRegex(@"\bnew Random\(|Random\.Shared|System\.Random\b|EF\.Functions\.Random", "the one source is IPredictionRandom");
        Regex.Matches(code, @"RandomNumberGenerator\.GetInt32\(").Should().ContainSingle("SecurePredictionRandom is the only production source");
        code.Should().NotMatchRegex(@"DateTime(Offset)?\.(Now|UtcNow|Today)\b|TimeZoneInfo\.Local\b", "'now' is the host's TimeProvider and the zone is Europe/Istanbul");
        typeof(PredictionWorker).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(TimeProvider));
    }

    [Fact]
    public void Economic_changes_run_in_write_transactions_and_discord_is_called_outside_them()
    {
        var service = Source("Application", "PredictionService.cs");
        var economy = Source("Application", "PredictionEconomy.cs");
        foreach (var file in new[] { service, economy })
        {
            // Inside every PredictionWrites.RunAsync lambda: no transport, gateway or card-sync call.
            var blocks = Regex.Matches(file, @"PredictionWrites\.RunAsync.*?\}, ct\);", RegexOptions.Singleline);
            blocks.Should().NotBeEmpty();
            foreach (Match block in blocks)
                block.Value.Should().NotMatchRegex(@"transport\.|guilds\.|cardSync\.", "no Discord call inside a transaction");
        }

        Regex.Count(service + economy, @"PredictionWrites\.RunAsync").Should().BeGreaterThanOrEqualTo(8);
        Source("Application", "PredictionWrites.cs").Should().Contain("BeginTransactionAsync");
    }

    [Fact]
    public void The_card_is_the_only_direct_send_and_the_announcement_goes_through_the_outbox()
    {
        var service = Source("Application", "PredictionService.cs");
        Regex.Matches(service, @"transport\.SendAsync\(").Should().ContainSingle("only the new card; never a second one");
        Regex.Matches(service, @"transport\.(\w+)\(").Select(m => m.Groups[1].Value).Distinct().Should().BeEquivalentTo("SendAsync", "FindRecentAsync", "GetPresenceAsync");
        Regex.Matches(Source("Application", "PredictionCardSync.cs"), @"transport\.(\w+)\(").Select(m => m.Groups[1].Value).Distinct().Should().Equal("EditAsync");
        Regex.Matches(Code("Application"), @"outbox\.StageAsync\(").Should().ContainSingle("the closing announcement only");
        Code("Commands").Should().NotMatchRegex(@"\bSendMessageAsync\(|IMessageChannel");
    }

    [Fact]
    public void Nothing_in_the_module_ever_pings()
    {
        var code = Code();
        code.Should().NotContain("ExplicitUsers(").And.NotContain("MentionPolicy.EveryoneOnly").And.NotContain("AllowedMentionTypes").And.NotContain("UserIds");
        Regex.Matches(code, @"new OutgoingMessage\([^;]*MentionPolicy\.(\w+)").Select(m => m.Groups[1].Value).Distinct().Should().Equal("None");
        var components = Source("Commands", "PredictionComponents.cs");
        Regex.Count(components, @"AllowedMentions = NoPings").Should().Be(Regex.Count(components, @"(UpdateAsync|ModifyOriginalResponseAsync)\("));
        Source("Commands", "PredictionCommands.cs").Should().NotMatchRegex(@"\b(RespondAsync|FollowupAsync|ModifyOriginalResponseAsync)\(");
    }

    [Fact]
    public void Logs_carry_ids_counts_and_amounts_never_titles_reasons_or_names()
    {
        foreach (Match call in Regex.Matches(Code(), @"\blogger\.Log\w+\((?<args>[^;]*)\);"))
        {
            var args = Regex.Replace(call.Groups["args"].Value, "\"[^\"]*\"", "\"\"");
            args.Should().NotMatchRegex(@"\b(Title|title|Reason|reason|Rules|rules|CreatorName|creatorName|Label|label|values|Values|modal|amountText|token)\b", call.Value);
        }

        Regex.Matches(Code(), @"(prediction|tournament)_(published|entry|locked|settled|cancelled|daily|closed)").Select(m => m.Value).Distinct()
            .Should().BeEquivalentTo("prediction_published", "prediction_entry", "prediction_locked", "prediction_settled", "prediction_cancelled", "prediction_daily",
                "tournament_closed");
    }

    [Fact]
    public void Every_interaction_class_is_gated_and_the_group_carries_no_member_permissions()
    {
        foreach (var type in Predictions.GetTypes().Where(t => !t.IsAbstract && typeof(ToroInteractionModule).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<ToroModuleAttribute>(inherit: true)!;
            attribute.ModuleId.Should().Be("predictions", type.Name);
            attribute.AllowWhenDisabled.Should().BeFalse();
            type.GetCustomAttribute<global::Discord.Interactions.DefaultMemberPermissionsAttribute>().Should().BeNull(type.Name + ": members need cuzdan/gunluk/liderlik");
        }

        var module = new PredictionsModule();
        module.Descriptor.Id.Value.Should().Be("predictions");
        module.Descriptor.EnabledByDefault.Should().BeFalse();
        module.Descriptor.IsCore.Should().BeFalse();
        module.Descriptor.AdminCommands.Should().BeEmpty();
        module.Descriptor.RequiredBotChannelPermissions.Should().Be(PredictionRules.RequiredChannelPermissions);
        PredictionRules.RequiredChannelPermissions.HasFlag(ToroSquad.Core.Security.GuildPermission.AddReactions).Should().BeFalse();
        PredictionRules.RequiredChannelPermissions.HasFlag(ToroSquad.Core.Security.GuildPermission.Administrator).Should().BeFalse();

        // Bots never create, enter or claim; every handler that could spend or create checks it first.
        var components = Source("Commands", "PredictionComponents.cs");
        foreach (var handler in new[] { "SubmitFormAsync", "PublishAsync", "PickAsync", "SubmitStakeAsync", "ConfirmEntryAsync" })
            Regex.IsMatch(components, handler + @"\([^)]*\)\s*\{\s*if \(await RefuseBotAsync\(\)\)").Should().BeTrue(handler);
    }

    [Fact]
    public async Task Module_is_listed_off_by_default_and_its_services_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InScopeAsync(async sp =>
        {
            var listed = (await sp.GetRequiredService<ModuleManagementService>().ListAsync(new GuildId(42), CancellationToken.None))
                .Single(m => m.Descriptor.Id == PredictionsModule.ModuleIdTyped);
            listed.Enabled.Should().BeFalse();
            sp.GetRequiredService<ILocalizer>().Get("tr", listed.Descriptor.NameKey).Should().Be("TSQ Öngörü");
            sp.GetRequiredService<PredictionService>().Should().NotBeNull();
            sp.GetRequiredService<PredictionEconomy>().Should().NotBeNull();
            sp.GetRequiredService<IPredictionRandom>().Should().BeOfType<SecurePredictionRandom>();
            sp.GetServices<ToroSquad.Core.Privacy.IUserDataContributor>().Should().Contain(c => c.Module == PredictionsModule.ModuleIdTyped);
        });
        host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().NotContain(s => s is PredictionWorker, "tests and CLI verbs run no worker");
    }

    [Fact]
    public async Task Manifest_has_one_ongoru_group_with_member_and_manager_subcommands_and_turkish_descriptions()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        var manifest = await interactions.InitializeAsync(host.Services);
        CommandManifestValidator.Validate(manifest, interactions.AdminCommandNames).Should().BeEmpty();
        manifest.Commands.Where(c => c.OwnerModule == "predictions").Select(c => c.Name).Should().Equal("ongoru");

        var ongoru = manifest.Find("ongoru")!;
        ongoru.DefaultMemberPermissions.Should().BeNull("hiding the group would hide the member commands; every subcommand authorizes itself");
        ongoru.Options.Select(o => o.Name).Should().Equal("yarat", "kilitle", "sonuclandir", "iptal", "cuzdan", "gunluk", "tahminlerim", "liderlik", "turnuva");
        ongoru.Options.Single(o => o.Name == "turnuva").Options.Select(o => o.Name).Should().Equal("durum", "bitir");
        foreach (var name in new[] { "kilitle", "sonuclandir", "iptal" })
        {
            var target = ongoru.Options.Single(o => o.Name == name).Options[0];
            (target.Name, target.Required, target.Autocomplete, target.MaxLength).Should().Be(("ongoru", true, true, (int?)PredictionTarget.MaxInputLength));
        }

        var reason = ongoru.Options.Single(o => o.Name == "iptal").Options[1];
        (reason.Name, reason.Required, reason.MinLength, reason.MaxLength).Should().Be(("gerekce", true, (int?)3, (int?)300));
        ongoru.DescriptionLocalizations["tr"].Should().Be("TSQ Öngörü: sabit oranlı topluluk öngörüleri (sanal TSQ Coin)");
    }

    [Fact]
    public void The_forms_fit_discords_modal_limits_in_both_languages()
    {
        var catalog = Catalog();
        foreach (var language in new[] { "tr", "en" })
        {
            string L(string key) => catalog.Get(language, key);
            var create = PredictionFormUi.CreateModal("draft", new PredictionFormValues("t", "o", "l", "r"), L);
            create.Title.Length.Should().BeLessThanOrEqualTo(PredictionFormUi.MaxTitleLength);
            create.CustomId.Should().Be(PredictionMessages.FormModalPrefix + "draft");
            var stake = PredictionFormUi.StakeModal(new EntryFormInfo(12, 34, new string('x', 80), 110, 100_000), "1000", L);
            stake.CustomId.Should().Be("tsq:pred:stake:12:34");
            foreach (var key in new[] { "predictions.form.question", "predictions.form.outcomes", "predictions.form.lock", "predictions.form.rules", "predictions.stake.amount", "predictions.stake.title", "predictions.form.title" })
                L(key).Length.Should().BeLessThanOrEqualTo(PredictionFormUi.MaxTitleLength, key);
            foreach (var key in new[] { "predictions.form.question_hint", "predictions.form.outcomes_hint", "predictions.form.lock_hint", "predictions.form.rules_hint",
                         "predictions.form.question_placeholder", "predictions.form.outcomes_placeholder", "predictions.form.lock_placeholder", "predictions.form.rules_placeholder" })
                L(key).Length.Should().BeLessThanOrEqualTo(PredictionFormUi.MaxHintLength, key);
        }
    }

    [Fact]
    public void Custom_ids_fit_discords_100_characters_and_carry_no_authority()
    {
        var token = new string('A', 22); // 128 random bits, base64url
        foreach (var prefix in new[]
                 {
                     PredictionMessages.FormModalPrefix, PredictionMessages.PublishPrefix, PredictionMessages.EditPrefix, PredictionMessages.DiscardPrefix,
                     PredictionMessages.EntryConfirmPrefix, PredictionMessages.SettlePickPrefix, PredictionMessages.SettleConfirmPrefix, PredictionMessages.CancelConfirmPrefix,
                     PredictionMessages.EndConfirmPrefix, PredictionMessages.DismissPrefix,
                 })
            (prefix + token).Length.Should().BeLessThanOrEqualTo(100);
        (PredictionMessages.StakeModalPrefix + long.MaxValue + ":" + long.MaxValue).Length.Should().BeLessThanOrEqualTo(100);
        Code().Should().NotMatchRegex(@"CustomId\s*\+\s*.*(Amount|Odds|Balance|Permission)", "amounts, odds and rights never travel in a custom id");
    }

    [Fact]
    public void The_shared_select_extension_keeps_existing_payload_hashes()
    {
        var message = new OutgoingMessage("x", new MessageEmbed("t", "d", null, [], null, null, 1), MentionPolicy.None,
            [new MessageButton("b", "id", null)]);
        PayloadSerializer.Serialize(message).Should().NotContain("select", "an absent select is not written");
        PayloadSerializer.Serialize(message with { Select = new MessageSelectMenu("s", null, [new MessageSelectOption("a", "1")]) }).Should().Contain("\"select\"");
        var roundTrip = PayloadSerializer.Deserialize(PayloadSerializer.Serialize(message with { Select = new MessageSelectMenu("s", "p", [new MessageSelectOption("a", "1", "d")], true) }));
        roundTrip.Select.Should().BeEquivalentTo(new MessageSelectMenu("s", "p", [new MessageSelectOption("a", "1", "d")], true));
    }

    [Fact]
    public void Localization_catalogs_match_and_every_used_key_exists()
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
        foreach (var prefix in new[] { "predictions.form.error.lock_", "predictions.form.error.odds_", "predictions.entry.amount_", "predictions.status." })
            tr.Keys.Should().Contain(k => k.StartsWith(prefix, StringComparison.Ordinal), prefix);

        var catalog = Catalog();
        catalog.Get("tr", "predictions.wrong_channel", "<#689814679056547857>").Should().Be("Bu komutu yalnızca <#689814679056547857> kanalında kullanabilirsiniz.");
        catalog.Get("tr", "predictions.missing_role", "<@&1233057768408350741>").Should().Be("Öngörü yaratabilmek için <@&1233057768408350741> rolüne sahip olmalısınız.");
        catalog.Get("tr", "predictions.tournament.unresolved").Should().Be("Turnuvayı bitirmeden önce sonuçlanmamış öngörüleri sonuçlandırmalı veya iptal etmelisiniz.");
    }

    private static LocalizationCatalog Catalog() => (LocalizationCatalog)PredictionDomainTests.Localizer();

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"""((?:predictions|module\.predictions)\.[a-z0-9_.\-]*[a-z0-9])""")]
    private static partial Regex KeyLiteral();
}
