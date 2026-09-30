using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Interactions;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Application;
using ToroSquad.Modules.Birthday.Commands;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Commands;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Commands;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Commands;
using ToroSquad.Modules.Live.Application;
using ToroSquad.Modules.Live.Commands;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Commands;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Commands;
using ToroSquad.Tests.Support;
using ActorContext = ToroSquad.Core.Security.ActorContext;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The former top-level "*-admin" commands merged into ONE /tsq-admin root (one subcommand group per module). The inventory
/// and the "before" payloads come from the manifest committed before the merge (Fixtures/commands/manifest-before-tsq-admin.json),
/// never from class names. Dispatch runs through the real Discord.Net InteractionService the bot uses. Offline only: nothing
/// here talks to Discord.
/// </summary>
public sealed class TsqAdminCommandTests
{
    private static readonly GuildId Guild = new(42);

    /// <summary>
    /// The migration table: every executable path of every former "*-admin" root → its /tsq-admin path and the handler that
    /// ran it before (unchanged class and method). Sub-groups are flattened to "&lt;subgroup&gt;-&lt;operation&gt;".
    /// </summary>
    public static readonly (string Old, string New, Type Handler, string Method)[] Moves =
    [
        ("birthday-admin set", "tsq-admin birthday set", typeof(BirthdayTsqAdmin.BirthdayAdminCommands), "SetAsync"),
        ("birthday-admin show", "tsq-admin birthday show", typeof(BirthdayTsqAdmin.BirthdayAdminCommands), "ShowAsync"),
        ("birthday-admin configure", "tsq-admin birthday configure", typeof(BirthdayTsqAdmin.BirthdayAdminCommands), "ConfigureAsync"),
        ("birthday-admin status", "tsq-admin birthday status", typeof(BirthdayTsqAdmin.BirthdayAdminCommands), "StatusAsync"),
        ("birthday-admin doctor", "tsq-admin birthday doctor", typeof(BirthdayTsqAdmin.BirthdayAdminCommands), "DoctorAsync"),
        ("esports-admin configure", "tsq-admin esports configure", typeof(EsportsTsqAdmin.EsportsAdminCommands), "ConfigureAsync"),
        ("esports-admin panel", "tsq-admin esports panel", typeof(EsportsTsqAdmin.EsportsAdminCommands), "PanelAsync"),
        ("esports-admin preview", "tsq-admin esports preview", typeof(EsportsTsqAdmin.EsportsAdminCommands), "PreviewAsync"),
        ("esports-admin pause", "tsq-admin esports pause", typeof(EsportsTsqAdmin.EsportsAdminCommands), "PauseAsync"),
        ("esports-admin resume", "tsq-admin esports resume", typeof(EsportsTsqAdmin.EsportsAdminCommands), "ResumeAsync"),
        ("esports-admin doctor", "tsq-admin esports doctor", typeof(EsportsTsqAdmin.EsportsAdminCommands), "DoctorAsync"),
        ("esports-admin filters show", "tsq-admin esports filters-show", typeof(EsportsTsqAdmin.EsportsAdminCommands.FilterCommands), "ShowAsync"),
        ("esports-admin filters team", "tsq-admin esports filters-team", typeof(EsportsTsqAdmin.EsportsAdminCommands.FilterCommands), "TeamAsync"),
        ("esports-admin filters tournament", "tsq-admin esports filters-tournament", typeof(EsportsTsqAdmin.EsportsAdminCommands.FilterCommands), "TournamentAsync"),
        ("esports-admin filters tier", "tsq-admin esports filters-tier", typeof(EsportsTsqAdmin.EsportsAdminCommands.FilterCommands), "TierAsync"),
        ("esports-admin filters vrs", "tsq-admin esports filters-vrs", typeof(EsportsTsqAdmin.EsportsAdminCommands.FilterCommands), "VrsAsync"),
        ("esports-admin filters clear", "tsq-admin esports filters-clear", typeof(EsportsTsqAdmin.EsportsAdminCommands.FilterCommands), "ClearAsync"),
        ("esports-admin roles list", "tsq-admin esports roles-list", typeof(EsportsTsqAdmin.EsportsAdminCommands.RoleCommands), "ListAsync"),
        ("esports-admin roles map", "tsq-admin esports roles-map", typeof(EsportsTsqAdmin.EsportsAdminCommands.RoleCommands), "MapAsync"),
        ("esports-admin roles unmap", "tsq-admin esports roles-unmap", typeof(EsportsTsqAdmin.EsportsAdminCommands.RoleCommands), "UnmapAsync"),
        ("esports-admin roles selfservice", "tsq-admin esports roles-selfservice", typeof(EsportsTsqAdmin.EsportsAdminCommands.RoleCommands), "SelfServiceAsync"),
        ("f1-admin preview", "tsq-admin f1 preview", typeof(Formula1TsqAdmin.Formula1AdminCommands), "PreviewAsync"),
        ("f1-admin status", "tsq-admin f1 status", typeof(Formula1TsqAdmin.Formula1AdminCommands), "StatusAsync"),
        ("f1-admin doctor", "tsq-admin f1 doctor", typeof(Formula1TsqAdmin.Formula1AdminCommands), "DoctorAsync"),
        ("f1-admin pause", "tsq-admin f1 pause", typeof(Formula1TsqAdmin.Formula1AdminCommands), "PauseAsync"),
        ("f1-admin resume", "tsq-admin f1 resume", typeof(Formula1TsqAdmin.Formula1AdminCommands), "ResumeAsync"),
        ("f1-admin configure channel", "tsq-admin f1 configure-channel", typeof(Formula1TsqAdmin.Formula1AdminCommands.ConfigureCommands), "ChannelAsync"),
        ("f1-admin configure notifications", "tsq-admin f1 configure-notifications", typeof(Formula1TsqAdmin.Formula1AdminCommands.ConfigureCommands), "NotificationsAsync"),
        ("f1-admin configure role", "tsq-admin f1 configure-role", typeof(Formula1TsqAdmin.Formula1AdminCommands.ConfigureCommands), "RoleAsync"),
        ("f1-admin configure spoilers", "tsq-admin f1 configure-spoilers", typeof(Formula1TsqAdmin.Formula1AdminCommands.ConfigureCommands), "SpoilersAsync"),
        ("lfg-admin channel", "tsq-admin lfg channel", typeof(LfgTsqAdmin.LfgAdminCommands), "ChannelAsync"),
        ("lfg-admin status", "tsq-admin lfg status", typeof(LfgTsqAdmin.LfgAdminCommands), "StatusAsync"),
        ("live-admin doctor", "tsq-admin live doctor", typeof(LiveTsqAdmin.LiveAdminCommands), "DoctorAsync"),
        ("news-admin configure", "tsq-admin news configure", typeof(NewsTsqAdmin.NewsAdminCommands), "ConfigureAsync"),
        ("news-admin pause", "tsq-admin news pause", typeof(NewsTsqAdmin.NewsAdminCommands), "PauseAsync"),
        ("news-admin resume", "tsq-admin news resume", typeof(NewsTsqAdmin.NewsAdminCommands), "ResumeAsync"),
        ("news-admin preview", "tsq-admin news preview", typeof(NewsTsqAdmin.NewsAdminCommands), "PreviewAsync"),
        ("news-admin status", "tsq-admin news status", typeof(NewsTsqAdmin.NewsAdminCommands), "StatusAsync"),
        ("news-admin doctor", "tsq-admin news doctor", typeof(NewsTsqAdmin.NewsAdminCommands), "DoctorAsync"),
        ("volleyball-admin preview", "tsq-admin volleyball preview", typeof(VolleyballTsqAdmin.VolleyballAdminCommands), "PreviewAsync"),
        ("volleyball-admin status", "tsq-admin volleyball status", typeof(VolleyballTsqAdmin.VolleyballAdminCommands), "StatusAsync"),
        ("volleyball-admin doctor", "tsq-admin volleyball doctor", typeof(VolleyballTsqAdmin.VolleyballAdminCommands), "DoctorAsync"),
        ("volleyball-admin pause", "tsq-admin volleyball pause", typeof(VolleyballTsqAdmin.VolleyballAdminCommands), "PauseAsync"),
        ("volleyball-admin resume", "tsq-admin volleyball resume", typeof(VolleyballTsqAdmin.VolleyballAdminCommands), "ResumeAsync"),
        ("volleyball-admin configure channel", "tsq-admin volleyball configure-channel", typeof(VolleyballTsqAdmin.VolleyballAdminCommands.ConfigureCommands), "ChannelAsync"),
        ("volleyball-admin configure notifications", "tsq-admin volleyball configure-notifications", typeof(VolleyballTsqAdmin.VolleyballAdminCommands.ConfigureCommands), "NotificationsAsync"),
        ("volleyball-admin configure role", "tsq-admin volleyball configure-role", typeof(VolleyballTsqAdmin.VolleyballAdminCommands.ConfigureCommands), "RoleAsync"),
    ];

    /// <summary>Group → owning module id (the module whose [ToroModule] gates and whose services authorize the group).</summary>
    private static readonly Dictionary<string, string> GroupOwners = new()
    {
        ["birthday"] = "birthday",
        ["esports"] = "esports",
        ["f1"] = "formula1",
        ["lfg"] = "lfg",
        ["live"] = "live",
        ["news"] = "news",
        ["volleyball"] = "volleyball",
    };

    // ------------------------------------------------------------------ A. scope and completeness

    [Fact]
    public void Inventory_comes_from_the_real_pre_merge_payload_and_the_table_covers_every_executable_path()
    {
        var before = Before();
        var roots = before.Where(c => (int)c.Payload["type"]! == 1 && c.Name.EndsWith("-admin", StringComparison.Ordinal)).Select(c => c.Name).ToList();
        roots.Should().BeEquivalentTo("birthday-admin", "esports-admin", "f1-admin", "lfg-admin", "live-admin", "news-admin", "volleyball-admin");

        var paths = new List<string>();
        foreach (var root in roots)
        {
            foreach (var option in before.Single(c => c.Name == root).Payload["options"]!.AsArray().Select(o => o!))
            {
                if ((int)option["type"]! == 1)
                    paths.Add(root + " " + option["name"]);
                else
                    paths.AddRange(option["options"]!.AsArray().Select(s => $"{root} {option["name"]} {s!["name"]}"));
            }
        }

        paths.Should().HaveCount(47);
        Moves.Select(m => m.Old).Should().BeEquivalentTo(paths, "every former admin operation has exactly one new path");
        Moves.Select(m => m.New).Should().OnlyHaveUniqueItems();
        foreach (var move in Moves)
        {
            var (oldRoot, newGroup) = (move.Old.Split(' ')[0], move.New.Split(' ')[1]);
            newGroup.Should().Be(oldRoot[..^"-admin".Length], "the group is the former command's prefix (f1 stays f1)");
            var oldOperation = string.Join('-', move.Old.Split(' ')[1..]);
            move.New.Split(' ')[2].Should().Be(oldOperation, "direct operations keep their name; sub-group operations become <subgroup>-<operation>");
        }
    }

    [Fact]
    public async Task Every_moved_operation_keeps_its_description_options_types_order_choices_and_translations()
    {
        var before = Before();
        var after = await AfterAsync();
        var root = after.Single(c => c.Name == TsqAdminRoot.Name).Payload;
        foreach (var move in Moves)
        {
            var old = move.Old.Split(' ');
            var oldNode = before.Single(c => c.Name == old[0]).Payload["options"]!.AsArray().Single(o => (string)o!["name"]! == old[1])!;
            if (old.Length == 3)
                oldNode = oldNode["options"]!.AsArray().Single(o => (string)o!["name"]! == old[2])!;
            var parts = move.New.Split(' ');
            var newNode = root["options"]!.AsArray().Single(g => (string)g!["name"]! == parts[1])!["options"]!.AsArray().Single(o => (string)o!["name"]! == parts[2])!;

            var expected = oldNode.DeepClone().AsObject();
            var actual = newNode.DeepClone().AsObject();
            expected.Remove("name");
            actual.Remove("name");
            JsonNode.DeepEquals(expected, actual).Should().BeTrue($"/{move.New} must be /{move.Old} unchanged (description, tr, options, choices, bounds): {actual.ToJsonString()}");
        }
    }

    [Fact]
    public async Task There_is_exactly_one_admin_root_with_one_group_per_module_and_no_moved_root_left()
    {
        var after = await AfterAsync();
        after.Where(c => c.Name.EndsWith("-admin", StringComparison.Ordinal)).Select(c => c.Name).Should().Equal(TsqAdminRoot.Name);
        var root = after.Single(c => c.Name == TsqAdminRoot.Name);
        root.Module.Should().Be("core", "the shared root is core's");
        root.Payload["default_member_permissions"]!.GetValue<string>().Should().Be("32", "every former admin root used Manage Server");
        var groups = root.Payload["options"]!.AsArray().Select(g => g!).ToList();
        groups.Should().OnlyContain(g => (int)g["type"]! == 2);
        groups.Select(g => (string)g["name"]!).Should().Equal("birthday", "esports", "f1", "lfg", "live", "news", "volleyball");
        groups.Should().NotContain(g => ((string)g["name"]!).StartsWith("tsq", StringComparison.Ordinal), "the root is never wrapped into itself");
        groups.SelectMany(g => g["options"]!.AsArray()).Should().OnlyContain(s => (int)s!["type"]! == 1, "no third level");
        root.GroupModules.Should().BeEquivalentTo(GroupOwners);
    }

    [Fact]
    public async Task Merged_payload_respects_discords_documented_limits()
    {
        var (manifest, admin) = await BuildAsync();
        CommandManifestValidator.Validate(manifest, admin).Should().BeEmpty();
        var root = manifest.Find(TsqAdminRoot.Name)!;
        root.Options.Count.Should().BeLessThanOrEqualTo(CommandManifestValidator.MaxOptions);
        root.Options.Should().OnlyContain(g => g.Options.Count <= CommandManifestValidator.MaxOptions);
        root.Options.SelectMany(g => g.Options).Should().OnlyContain(s => s.Options.Count <= CommandManifestValidator.MaxOptions);
        root.Options.Single(g => g.Name == "esports").Options.Should().HaveCount(16, "the largest group");
        root.Options.Single(g => g.Name == "f1").Options.Single(s => s.Name == "configure-notifications").Options.Should().HaveCount(16, "the widest subcommand");

        var characters = CommandManifestValidator.CountCharacters(root);
        characters.Should().BeLessThanOrEqualTo(CommandManifestValidator.MaxCommandCharacters, "Discord's 8000-character budget per command");
        characters.Should().Be(5787, "measured budget of the merged payload (update deliberately when admin texts change)");
    }

    [Fact]
    public void Character_budget_counts_the_longest_localization_not_json_bytes()
    {
        var option = new ManifestOption(OptionType.String, "ab", "12345", new Dictionary<string, string> { ["tr"] = "1234567890" }, false,
            [new ManifestChoice("x", "yy", new Dictionary<string, string> { ["tr"] = "xxxx" })], [], false, null, null, null, null, []);
        var command = new ManifestCommand("cmd", "d", new Dictionary<string, string> { ["tr"] = "dd" }, [option], null, [0], [0], false, "core");
        // name 3 + max(1,2) + option name 2 + max(5,10) + choice max(1,4) + value 2
        CommandManifestValidator.CountCharacters(command).Should().Be(3 + 2 + 2 + 10 + 4 + 2);
    }

    [Fact]
    public async Task The_manifest_is_the_same_on_every_run_and_does_not_depend_on_module_registration_order()
    {
        var (first, _) = await BuildAsync();
        var (second, _) = await BuildAsync();
        second.ToDocumentJson().Should().Be(first.ToDocumentJson());

        await using var host = await TestHost.CreateAsync();
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        using var reversed = new InteractionService(new DiscordRestClient(), InteractionHost.CreateConfig());
        await using (var scope = host.Scope())
        {
            foreach (var type in registry.All.Reverse().SelectMany(m => m.InteractionModuleTypes.Reverse()))
                await reversed.AddModuleAsync(type, scope.ServiceProvider);
        }

        var manifest = CommandManifestBuilder.Build(reversed, registry, host.Services.GetRequiredService<ILocalizer>(), []);
        CommandManifest.CanonicalJson(manifest.Find(TsqAdminRoot.Name)!, true).Should().Be(CommandManifest.CanonicalJson(first.Find(TsqAdminRoot.Name)!, true));
        manifest.Hash.Should().Be(first.Hash);
    }

    // ------------------------------------------------------------------ B. out-of-scope commands

    [Fact]
    public async Task Every_command_that_was_not_an_admin_root_is_byte_for_byte_unchanged()
    {
        var before = Before();
        var after = await AfterAsync();
        var untouched = before.Where(c => !Moves.Any(m => m.Old.StartsWith(c.Name + " ", StringComparison.Ordinal))).ToList();
        untouched.Select(c => c.Name).Should().Contain(["giveaway", "ozetle", "setup", "modules", "help", "bot", "privacy", "Quote", "quote", "esports", "f1", "volleyball", "birthday",
            "ekip", "dolar", "euro", "altın", "çevir", "saat", "zarat", "randomsayi", "sec", "yazitura", "ongoru"]);
        untouched.Should().HaveCount(24);
        foreach (var command in untouched)
        {
            var now = after.SingleOrDefault(c => c.Name == command.Name);
            now.Should().NotBeNull($"{command.Name} must still exist");
            now!.Module.Should().Be(command.Module, command.Name);
            JsonNode.DeepEquals(command.Payload, now.Payload).Should().BeTrue($"{command.Name}: type, options, choices, permissions, contexts and translations must not change");
        }

        // Explicit regression targets: admin-looking commands that are NOT "-admin" roots stay where they are.
        after.Single(c => c.Name == "giveaway").Payload["default_member_permissions"]!.GetValue<string>().Should().Be("32");
        after.Single(c => c.Name == "Quote").Payload["type"]!.GetValue<int>().Should().Be(3, "Apps → Quote stays a message command");
        after.Select(c => c.Name).Should().HaveCount(before.Count - 7 + 1);
    }

    // ------------------------------------------------------------------ C. real dispatch

    [Fact]
    public async Task Every_new_path_dispatches_to_the_same_handler_method_and_old_paths_no_longer_resolve()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        foreach (var move in Moves)
        {
            var found = service.SearchSlashCommand(Slash(move.New));
            found.IsSuccess.Should().BeTrue($"/{move.New} must be registered for dispatch: {found.ErrorReason}");
            found.Command.MethodName.Should().Be(move.Method, move.New);
            ModuleChain(found.Command.Module).Should().Equal(TypeChain(move.Handler), $"/{move.New} runs in {move.Handler.Name}");

            service.SearchSlashCommand(Slash(move.Old)).IsSuccess.Should().BeFalse($"/{move.Old} is gone (no public alias)");
        }

        service.SlashCommands.Count(c => ModuleChain(c.Module)[0].EndsWith("TsqAdmin", StringComparison.Ordinal)).Should().Be(Moves.Length, "nothing else lives under /tsq-admin");
    }

    public static TheoryData<string, string[]> PipelineCases => new()
    {
        { "tsq-admin news doctor", [] },
        { "tsq-admin birthday show", ["member"] },
        { "tsq-admin esports roles-map", ["role", "team", "ping_reminder"] },
        { "tsq-admin esports filters-team", ["action", "team"] },
        { "tsq-admin f1 configure-channel", ["channel"] },
        { "tsq-admin volleyball configure-notifications", ["match_started"] },
        { "tsq-admin lfg status", [] },
        { "tsq-admin live doctor", [] },
    };

    /// <summary>
    /// Runs the real InteractionService pipeline (map lookup → preconditions → typed option parsing → DI construction of the
    /// module). Discord.Net then needs a live SocketInteractionContext to bind the module, which an offline test cannot create;
    /// reaching exactly that point proves everything before the handler body, and the handler body itself did not change.
    /// </summary>
    [Theory]
    [MemberData(nameof(PipelineCases))]
    public async Task The_interaction_pipeline_reaches_the_handler_with_typed_options(string path, string[] options)
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        var move = Moves.Single(m => m.New == path);
        SlashCommandInfo? executed = null;
        service.SlashCommandExecuted += (info, _, _) =>
        {
            executed = info;
            return Task.CompletedTask;
        };

        var values = options.ToDictionary(o => o, OptionValue);
        await using var scope = host.Scope();
        var context = Context(Slash(path, values), CorePermission.ManageGuild);
        var result = await service.ExecuteCommandAsync(context, scope.ServiceProvider);

        executed.Should().NotBeNull();
        executed!.MethodName.Should().Be(move.Method);
        result.Should().BeOfType<ExecuteResult>();
        ((ExecuteResult)result).Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().StartWith("Invalid context type", "preconditions passed, options parsed and the module was built; only the offline context stops it");

        // The typed options bind to the handler's parameters exactly as Discord sends them.
        foreach (var (name, value) in values)
        {
            var parameter = executed.Parameters.Single(p => p.Name == name);
            var read = await parameter.TypeConverter.ReadAsync(context, Option(name, value), scope.ServiceProvider);
            read.IsSuccess.Should().BeTrue($"{path} {name}: {read.ErrorReason}");
            read.Value.Should().Be(Convert(value, parameter.ParameterType), $"{path} {name}");
        }
    }

    [Fact]
    public async Task Autocomplete_on_the_new_path_answers_admins_and_gives_members_and_dms_nothing()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Set<RoleMappingEntity>().Add(new RoleMappingEntity { GuildId = Guild.Value, RoleId = 77, CreatedAt = TestHost.T0 });
            await db.SaveChangesAsync();
        });

        async Task<IReadOnlyList<AutocompleteResult>> AskAsync(CorePermission permissions, bool inGuild = true)
        {
            var interaction = Recorder.Create<IAutocompleteInteraction>(out var recorder, new()
            {
                ["Data"] = InterfaceFake.Create<IAutocompleteInteractionData>(new()
                {
                    ["CommandName"] = TsqAdminRoot.Name,
                    ["Options"] = (IReadOnlyCollection<AutocompleteOption>)
                    [
                        AutocompleteOption(ApplicationCommandOptionType.SubCommandGroup, "esports", null, false),
                        AutocompleteOption(ApplicationCommandOptionType.SubCommand, "roles-unmap", null, false),
                        AutocompleteOption(ApplicationCommandOptionType.Integer, "mapping", "", true),
                    ],
                    ["Current"] = AutocompleteOption(ApplicationCommandOptionType.Integer, "mapping", "", true),
                }),
                ["Type"] = InteractionType.ApplicationCommandAutocomplete,
            });
            await using var scope = host.Scope();
            var result = await service.ExecuteCommandAsync(Context(interaction, permissions, inGuild), scope.ServiceProvider);
            result.IsSuccess.Should().BeTrue(result.ErrorReason);
            return recorder.Calls.Where(c => c.Method == "RespondAsync").Select(c => ((IEnumerable<AutocompleteResult>?)c.Args[0] ?? []).ToList()).Single();
        }

        var forAdmin = await AskAsync(CorePermission.ManageGuild);
        forAdmin.Should().ContainSingle().Which.Value.Should().Be(1L);
        forAdmin[0].Name.Should().StartWith("#1 @").And.Contain("77", "the mapping as the admin knew it (text is neutralized against mentions)");
        (await AskAsync(CorePermission.ViewChannel | CorePermission.SendMessages)).Should().BeEmpty("a member who cannot run the command sees no server settings");
        (await AskAsync(CorePermission.ManageGuild, inGuild: false)).Should().BeEmpty("no guild, no suggestions");
    }

    // ------------------------------------------------------------------ D. authorization and module gates

    [Fact]
    public async Task Each_operation_is_gated_only_by_its_own_module_and_the_root_adds_no_precondition()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        await using var scope = host.Scope();
        foreach (var move in Moves)
        {
            var command = service.SearchSlashCommand(Slash(move.New)).Command;
            var owner = GroupOwners[move.New.Split(' ')[1]];
            var gates = command.Module.Preconditions.Concat(command.Preconditions).ToList();
            gates.Should().NotBeEmpty().And.AllBeOfType<ToroModuleAttribute>();
            gates.Cast<ToroModuleAttribute>().Should().OnlyContain(g => g.ModuleId == owner && g.AllowWhenDisabled,
                $"/{move.New}: like /{move.Old}, only its own module's setup gate (usable while the module is off)");

            // Every module is still disabled in this fresh database: admin setup keeps working, as before the merge.
            (await command.CheckPreconditionsAsync(Context(Slash(move.New), CorePermission.ManageGuild), scope.ServiceProvider)).IsSuccess.Should().BeTrue(move.New);
            (await command.CheckPreconditionsAsync(Context(Slash(move.New), CorePermission.ManageGuild, inGuild: false), scope.ServiceProvider))
                .ErrorReason.Should().Be(ToroModuleAttribute.GuildOnlyError, $"/{move.New} refuses DMs server-side");
        }

        var roots = service.Modules.Where(m => m.Parent is null && m.SlashGroupName == TsqAdminRoot.Name).ToList();
        roots.Should().HaveCount(7);
        roots.Should().OnlyContain(r => r.Preconditions.Count == 0 && r.SlashCommands.Count == 0, "the core root neither gates nor bypasses a module");
        typeof(TsqAdminRoot).GetCustomAttribute<DefaultMemberPermissionsAttribute>()!.Permissions.Should().Be(global::Discord.GuildPermission.ManageGuild);
        foreach (var type in Moves.Select(m => m.Handler).Distinct())
            type.GetCustomAttribute<DefaultMemberPermissionsAttribute>(inherit: false).Should().BeNull($"{type.Name}: Discord has no per-group permission; the root carries it");
    }

    [Fact]
    public async Task Server_side_authorization_is_unchanged_member_refused_manage_server_allowed_birthday_member_data_needs_administrator()
    {
        await using var host = await TestHost.CreateAsync();
        var member = TestHost.Member(Guild);
        var manager = new ActorContext(Guild, new UserId(1), CorePermission.ManageGuild, [], false, 50);
        var administrator = new ActorContext(Guild, new UserId(1), CorePermission.Administrator, [], false, 50);
        await host.InScopeAsync(async sp =>
        {
            async Task<OperationResult[]> AllAsync(ActorContext actor) =>
            [
                (await sp.GetRequiredService<NewsConfigService>().DoctorAsync(actor, CancellationToken.None)).Auth,
                (await sp.GetRequiredService<EsportsDoctor>().RunAsync(actor, CancellationToken.None)).Auth,
                (await sp.GetRequiredService<Formula1Doctor>().RunAsync(actor, CancellationToken.None)).Auth,
                (await sp.GetRequiredService<VolleyballDoctor>().RunAsync(actor, CancellationToken.None)).Auth,
                (await sp.GetRequiredService<LiveDoctor>().RunAsync(actor, CancellationToken.None)).Auth,
                (await sp.GetRequiredService<LfgConfigService>().StatusAsync(actor, CancellationToken.None)).Auth,
                (await sp.GetRequiredService<BirthdayDoctor>().RunAsync(actor, CancellationToken.None)).Auth,
            ];

            (await AllAsync(member)).Should().OnlyContain(r => !r.Succeeded && r.Error == OperationError.Forbidden, "a member is refused by every module");
            (await AllAsync(manager)).Should().OnlyContain(r => r.Succeeded, "Manage Server is still enough for the module settings");

            var birthdays = sp.GetRequiredService<BirthdayService>();
            (await birthdays.GetForMemberAsync(manager, new UserId(5), true, CancellationToken.None)).Result.Error.Should().Be(OperationError.Forbidden,
                "/tsq-admin birthday show still needs Administrator, not just Manage Server");
            (await birthdays.GetForMemberAsync(administrator, new UserId(5), true, CancellationToken.None)).Result.Succeeded.Should().BeTrue();
        });
    }

    // ------------------------------------------------------------------ E. components, modals and custom ids

    [Fact]
    public async Task Buttons_and_modals_keep_their_custom_ids_and_none_is_prefixed_by_the_new_groups()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        var handlers = service.ComponentCommands.Cast<ICommandInfo>().Concat(service.ModalCommands).ToList();
        handlers.Should().NotBeEmpty();
        handlers.Should().OnlyContain(h => h.IgnoreGroupNames || ModuleChain(h.Module).All(n => !n.EndsWith("TsqAdmin", StringComparison.Ordinal)));
        handlers.Should().NotContain(h => ModuleChain(h.Module)[0].EndsWith("TsqAdmin", StringComparison.Ordinal), "admin groups own no buttons or modals");

        // Buttons already posted in Discord (the /tsq-admin esports panel posts the same ones) still reach the same handlers.
        foreach (var (customId, handler) in new[]
                 {
                     (EsportsCommands.PanelPrefix + "5", nameof(EsportsCommands)),
                     (EsportsSetupFlow.ChannelId + "1", nameof(EsportsSetupComponents)),
                     (Formula1SetupFlow.EnableId + "1", "Formula1SetupComponents"),
                     (VolleyballSetupFlow.PreviewId + "1", "VolleyballSetupComponents"),
                 })
        {
            var found = service.SearchComponentCommand(InterfaceFake.Create<IComponentInteraction>(new()
            {
                ["Data"] = InterfaceFake.Create<IComponentInteractionData>(new() { ["CustomId"] = customId }),
            }));
            found.IsSuccess.Should().BeTrue(customId);
            found.Command.Module.Name.Should().Be(handler, customId);
        }
    }

    // ------------------------------------------------------------------ F. sync safety

    [Fact]
    public async Task Sync_plan_from_the_old_commands_creates_one_root_keeps_everything_else_and_prunes_only_the_moved_roots()
    {
        var (manifest, admin) = await BuildAsync();

        // The converter below mirrors what Discord returns for a guild command; prove it on the current commands first.
        var current = JsonNode.Parse(manifest.ToDocumentJson())!["commands"]!.AsArray();
        foreach (var command in manifest.Commands)
            GuildJson(current.Single(c => (string)c!["payload"]!["name"]! == command.Name)!["payload"]!).Should().Be(CommandManifest.CanonicalJson(command, false));

        var before = Before();
        var remote = before.Select((c, i) => new RemoteCommand((ulong)(1000 + i), c.Name, GuildJson(c.Payload), (CommandKind)(int)c.Payload["type"]!)).ToList();
        var managed = remote.ToDictionary(r => r.Name, r => r.Id);
        var moved = new[] { "birthday-admin", "esports-admin", "f1-admin", "lfg-admin", "live-admin", "news-admin", "volleyball-admin" };

        var plan = CommandSyncPlanner.Plan(manifest, remote, managed, Request(admin, prune: false));
        plan.IsBlocked.Should().BeFalse(string.Join(" | ", plan.BlockingErrors));
        plan.Items.Where(i => i.Action == SyncAction.Create).Select(i => i.Name).Should().Equal(TsqAdminRoot.Name);
        plan.Items.Where(i => i.Action == SyncAction.Update).Should().BeEmpty("no command outside the merge changes");
        plan.Items.Where(i => i.Action == SyncAction.Unchanged).Should().HaveCount(24).And.Contain(i => i.Name == "Quote" && i.Kind == CommandKind.Message);
        plan.Items.Where(i => i.Action == SyncAction.KeepManagedNotInManifest).Select(i => i.Name).Should().BeEquivalentTo(moved, "without --prune nothing is deleted");

        var pruned = CommandSyncPlanner.Plan(manifest, remote, managed, Request(admin, prune: true));
        pruned.Items.Where(i => i.Action == SyncAction.DeleteManaged).Select(i => i.Name).Should().BeEquivalentTo(moved, "only the moved roots are removed");
        pruned.Items.Where(i => i.Action == SyncAction.Unchanged).Should().HaveCount(24);

        // After the sync Discord holds exactly the manifest: a second plan has nothing to do.
        var synced = manifest.Commands.Select((c, i) => new RemoteCommand((ulong)(2000 + i), c.Name, CommandManifest.CanonicalJson(c, false), c.Type)).ToList();
        var again = CommandSyncPlanner.Plan(manifest, synced, synced.ToDictionary(r => r.Name, r => r.Id), Request(admin, prune: true));
        again.HasChanges.Should().BeFalse();
        again.Items.Should().OnlyContain(i => i.Action == SyncAction.Unchanged);
    }

    [Fact]
    public async Task A_missing_or_undeclared_admin_group_blocks_the_sync_so_nothing_is_deleted()
    {
        var (manifest, admin) = await BuildAsync();
        var root = manifest.Find(TsqAdminRoot.Name)!;
        var remote = manifest.Commands.Select((c, i) => new RemoteCommand((ulong)(1 + i), c.Name, CommandManifest.CanonicalJson(c, false), c.Type)).ToList();
        var managed = remote.ToDictionary(r => r.Name, r => r.Id);

        var withoutNews = Replace(manifest, root with { Options = root.Options.Where(o => o.Name != "news").ToList() });
        var plan = CommandSyncPlanner.Plan(withoutNews, remote, managed, Request(admin, prune: true));
        plan.IsBlocked.Should().BeTrue();
        plan.BlockingErrors.Should().Contain("expected admin command '/tsq-admin news' is missing");
        plan.Items.Should().BeEmpty("a partial root never reaches Discord");

        var rogue = root.Options[0] with { Name = "rogue" };
        var undeclared = Replace(manifest, root with { Options = [.. root.Options, rogue] });
        CommandManifestValidator.Validate(undeclared, admin).Should().Contain("/tsq-admin rogue: not declared by any module's AdminCommands");
    }

    [Fact]
    public async Task The_builder_refuses_group_collisions_root_level_commands_undeclared_groups_and_a_third_level()
    {
        await using var host = await TestHost.CreateAsync();
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var localizer = host.Services.GetRequiredService<ILocalizer>();
        await using var scope = host.Scope();

        async Task<IReadOnlyList<string>> ErrorsAsync(params Type[] extra)
        {
            using var service = new InteractionService(new DiscordRestClient(), InteractionHost.CreateConfig());
            await service.AddModuleAsync<NewsTsqAdmin>(scope.ServiceProvider);
            foreach (var type in extra)
                await service.AddModuleAsync(type, scope.ServiceProvider);
            return CommandManifestBuilder.Build(service, registry, localizer, []).LoadErrors;
        }

        (await ErrorsAsync()).Should().BeEmpty();
        (await ErrorsAsync(typeof(SecondNewsGroup))).Should().Contain(e => e.Contains("group names must be unique", StringComparison.Ordinal));
        (await ErrorsAsync(typeof(RootLevelCommand))).Should().Contain(e => e.Contains("adds commands to the root itself", StringComparison.Ordinal));
        (await ErrorsAsync(typeof(UndeclaredGroup))).Should().Contain(e => e.Contains("does not declare 'tsq-admin rogue'", StringComparison.Ordinal));

        // Discord.Net 3.20 loads /tsq-admin deep deeper x without complaint; Discord would reject it, so the builder must.
        (await ErrorsAsync(typeof(ThreeLevels))).Should().Contain(e => e.Contains("Discord allows no group inside a group", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ test-only contributors (never registered in the bot)

    public sealed class SecondNewsGroup : TsqAdminRoot
    {
        [ToroModule("news", AllowWhenDisabled = true)]
        [Group("news", "Duplicate group")]
        public sealed class Commands(InteractionServices services) : ToroInteractionModule(services)
        {
            [SlashCommand("extra", "Extra")]
            public Task ExtraAsync() => Task.CompletedTask;
        }
    }

    public sealed class RootLevelCommand : TsqAdminRoot
    {
        [SlashCommand("oops", "Directly on the root")]
        public Task OopsAsync() => Task.CompletedTask;
    }

    public sealed class UndeclaredGroup : TsqAdminRoot
    {
        [ToroModule("news", AllowWhenDisabled = true)]
        [Group("rogue", "Not declared")]
        public sealed class Commands(InteractionServices services) : ToroInteractionModule(services)
        {
            [SlashCommand("x", "X")]
            public Task XAsync() => Task.CompletedTask;
        }
    }

    public sealed class ThreeLevels : TsqAdminRoot
    {
        [ToroModule("news", AllowWhenDisabled = true)]
        [Group("deep", "Deep")]
        public sealed class Outer(InteractionServices services) : ToroInteractionModule(services)
        {
            [Group("deeper", "Deeper")]
            public sealed class Inner(InteractionServices services) : ToroInteractionModule(services)
            {
                [SlashCommand("x", "X")]
                public Task XAsync() => Task.CompletedTask;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record DocCommand(string Name, string Module, JsonObject Payload, IReadOnlyDictionary<string, string>? GroupModules);

    private static List<DocCommand> Before() =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "commands", "manifest-before-tsq-admin.json")));

    private static async Task<List<DocCommand>> AfterAsync() => Parse((await BuildAsync()).Manifest.ToDocumentJson());

    private static List<DocCommand> Parse(string document) =>
        JsonNode.Parse(document)!["commands"]!.AsArray().Select(c => new DocCommand(
            (string)c!["payload"]!["name"]!,
            (string)c["module"]!,
            c["payload"]!.AsObject(),
            c["group_modules"]?.AsObject().ToDictionary(kv => kv.Key, kv => (string)kv.Value!))).ToList();

    private static async Task<(CommandManifest Manifest, IReadOnlySet<string> Admin)> BuildAsync()
    {
        await using var host = await TestHost.CreateAsync();
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        return (await interactions.InitializeAsync(host.Services), interactions.AdminCommandNames);
    }

    private static async Task<InteractionService> ServiceAsync(TestHost host)
    {
        var interactions = host.Services.GetRequiredService<InteractionHost>();
        (await interactions.InitializeAsync(host.Services)).LoadErrors.Should().BeEmpty();
        return interactions.Service;
    }

    private static CommandManifest Replace(CommandManifest manifest, ManifestCommand command) =>
        new(manifest.Commands.Select(c => c.Name == command.Name ? command : c).ToList(), manifest.LoadErrors);

    private static SyncRequest Request(IReadOnlySet<string> admin, bool prune) =>
        new(new SyncScope.Guild(42), 7, 7, new HashSet<ulong> { 42 }, false, prune, admin);

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A document payload as Discord returns it for a guild command (contexts/integration_types are global-only).</summary>
    private static string GuildJson(JsonNode payload)
    {
        var copy = payload.DeepClone().AsObject();
        copy.Remove("contexts");
        copy.Remove("integration_types");
        return copy.ToJsonString(Compact);
    }

    private static List<string> ModuleChain(ModuleInfo module)
    {
        var names = new List<string>();
        for (var m = module; m is not null; m = m.Parent)
            names.Insert(0, m.Name);
        return names;
    }

    private static List<string> TypeChain(Type type)
    {
        var names = new List<string>();
        for (var t = type; t is not null; t = t.DeclaringType)
            names.Insert(0, t.Name);
        return names;
    }

    /// <summary>A slash interaction exactly as Discord nests it: command → group → subcommand → options.</summary>
    private static ISlashCommandInteraction Slash(string path, IReadOnlyDictionary<string, object>? values = null)
    {
        var parts = path.Split(' ');
        IReadOnlyCollection<IApplicationCommandInteractionDataOption> options = (values ?? new Dictionary<string, object>()).Select(kv => Option(kv.Key, kv.Value)).ToList();
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            options =
            [
                InterfaceFake.Create<IApplicationCommandInteractionDataOption>(new()
                {
                    ["Name"] = parts[i],
                    ["Type"] = i == parts.Length - 1 ? ApplicationCommandOptionType.SubCommand : ApplicationCommandOptionType.SubCommandGroup,
                    ["Options"] = options,
                }),
            ];
        }

        var data = InterfaceFake.Create<IApplicationCommandInteractionData>(new() { ["Name"] = parts[0], ["Options"] = options });
        return InterfaceFake.Create<ISlashCommandInteraction>(new() { ["Data"] = data, ["Type"] = InteractionType.ApplicationCommand });
    }

    private static IApplicationCommandInteractionDataOption Option(string name, object value) =>
        InterfaceFake.Create<IApplicationCommandInteractionDataOption>(new() { ["Name"] = name, ["Value"] = value, ["Options"] = (IReadOnlyCollection<IApplicationCommandInteractionDataOption>)[] });

    private static object OptionValue(string name) => name switch
    {
        "member" => InterfaceFake.Create<IGuildUser>(new() { ["Id"] = 5UL, ["GuildId"] = Guild.Value }),
        "role" => InterfaceFake.Create<IRole>(new() { ["Id"] = 77UL }),
        "channel" => InterfaceFake.Create<ITextChannel>(new() { ["Id"] = 500UL }),
        "team" => "aurora",
        "action" => "add",
        _ => true,
    };

    private static object Convert(object value, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return target.IsInstanceOfType(value) ? value : System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IInteractionContext Context(IDiscordInteraction interaction, CorePermission permissions, bool inGuild = true)
    {
        var user = InterfaceFake.Create<IGuildUser>(new()
        {
            ["Id"] = 9UL,
            ["GuildId"] = Guild.Value,
            ["GuildPermissions"] = new GuildPermissions((ulong)permissions),
            ["RoleIds"] = (IReadOnlyCollection<ulong>)Array.Empty<ulong>(),
        });
        var guild = inGuild ? InterfaceFake.Create<IGuild>(new() { ["Id"] = Guild.Value, ["OwnerId"] = 1UL }) : null;
        return InterfaceFake.Create<IInteractionContext>(new() { ["Guild"] = guild, ["User"] = user, ["Interaction"] = interaction });
    }

    private static AutocompleteOption AutocompleteOption(ApplicationCommandOptionType type, string name, object? value, bool focused) =>
        (AutocompleteOption)Activator.CreateInstance(typeof(AutocompleteOption), BindingFlags.Instance | BindingFlags.NonPublic, null, [type, name, value, focused], null)!;

    /// <summary>Like <see cref="InterfaceFake"/>, but records method calls and completes Task-returning ones.</summary>
    public class Recorder : DispatchProxy
    {
        private Dictionary<string, object?> _values = [];

        public List<(string Method, object?[] Args)> Calls { get; } = [];

        public static T Create<T>(out Recorder recorder, Dictionary<string, object?> values)
            where T : class
        {
            var proxy = Create<T, Recorder>();
            recorder = (Recorder)(object)proxy;
            recorder._values = values;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;
            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal) && _values.TryGetValue(targetMethod.Name[4..], out var value))
                return value;
            Calls.Add((targetMethod.Name, args ?? []));
            var type = targetMethod.ReturnType;
            if (type == typeof(Task))
                return Task.CompletedTask;
            if (type == typeof(void))
                return null;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
