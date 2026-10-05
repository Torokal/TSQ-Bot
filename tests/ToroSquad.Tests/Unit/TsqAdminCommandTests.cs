using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Roles;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Commands.Manifest;
using ToroSquad.Discord.Guilds;
using ToroSquad.Discord.Interactions;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Application;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Commands;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Commands;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Commands;
using ToroSquad.Modules.Volleyball.Commands;
using ToroSquad.Tests.Support;
using ActorContext = ToroSquad.Core.Security.ActorContext;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// <c>/tsq-admin</c> is ONE flat slash command: <c>modul</c> + <c>islem</c> (string, autocomplete) + <c>kanal</c>/<c>uye</c>/<c>rol</c>/<c>tarih</c>,
/// no subcommand and no subcommand group. The 47 former "-admin" operations are routed by <see cref="AdminRouter"/> to their
/// modules and run here against the real services (SQLite, fake Discord) through a recording <see cref="IAdminResponder"/>.
/// The "before" payloads come from committed manifests (before the merge, and the grouped /tsq-admin now on main).
/// </summary>
public sealed class TsqAdminCommandTests
{
    private static readonly GuildId Guild = new(42);
    private static readonly ChannelId Channel = new(500);
    private const ulong Role = 77;

    /// <summary>
    /// Every former operation → its module and operation id, the shared options it reads (the former channel/member/date/role
    /// options) and the former options that now come from the operation's own form.
    /// </summary>
    public static readonly (string Old, string Module, string Op, AdminFields Accepts, string[] Form)[] Moves =
    [
        ("birthday-admin set", "birthday", "set", AdminFields.User | AdminFields.Date, []),
        ("birthday-admin show", "birthday", "show", AdminFields.User, []),
        ("birthday-admin configure", "birthday", "configure", AdminFields.Channel, []),
        ("birthday-admin status", "birthday", "status", AdminFields.None, []),
        ("birthday-admin doctor", "birthday", "doctor", AdminFields.None, []),
        ("esports-admin configure", "esports", "configure", AdminFields.Channel, ["reminders", "reminder_minutes", "results", "spoilers"]),
        ("esports-admin panel", "esports", "panel", AdminFields.None, []),
        ("esports-admin preview", "esports", "preview", AdminFields.None, []),
        ("esports-admin pause", "esports", "pause", AdminFields.None, []),
        ("esports-admin resume", "esports", "resume", AdminFields.None, []),
        ("esports-admin doctor", "esports", "doctor", AdminFields.None, []),
        ("esports-admin filters show", "esports", "filters-show", AdminFields.None, []),
        ("esports-admin filters team", "esports", "filters-team", AdminFields.None, ["action", "team"]),
        ("esports-admin filters tournament", "esports", "filters-tournament", AdminFields.None, ["action", "tournament"]),
        ("esports-admin filters tier", "esports", "filters-tier", AdminFields.None, ["action", "tier"]),
        ("esports-admin filters vrs", "esports", "filters-vrs", AdminFields.None, ["top"]),
        ("esports-admin filters clear", "esports", "filters-clear", AdminFields.None, []),
        ("esports-admin roles list", "esports", "roles-list", AdminFields.None, []),
        ("esports-admin roles map", "esports", "roles-map", AdminFields.Role, ["team", "ping_reminder", "ping_result"]),
        ("esports-admin roles unmap", "esports", "roles-unmap", AdminFields.None, ["mapping"]),
        ("esports-admin roles selfservice", "esports", "roles-selfservice", AdminFields.None, ["mapping", "enabled"]),
        ("f1-admin preview", "f1", "preview", AdminFields.None, ["card"]),
        ("f1-admin status", "f1", "status", AdminFields.None, []),
        ("f1-admin doctor", "f1", "doctor", AdminFields.None, []),
        ("f1-admin pause", "f1", "pause", AdminFields.None, []),
        ("f1-admin resume", "f1", "resume", AdminFields.None, []),
        ("f1-admin configure channel", "f1", "configure-channel", AdminFields.Channel, []),
        ("f1-admin configure notifications", "f1", "configure-notifications", AdminFields.None,
            ["practice_start", "practice_results", "sprint_start", "sprint_results", "race_start", "race_results", "standings", "qualifying_start", "qualifying_results",
             "sprint_qualifying_start", "sprint_qualifying_results", "weekend_schedule", "race_reminder", "disqualification", "safety_car", "red_flag"]),
        ("f1-admin configure role", "f1", "configure-role", AdminFields.Role, ["ping_starts", "ping_results", "clear"]),
        ("f1-admin configure spoilers", "f1", "configure-spoilers", AdminFields.None, ["enabled"]),
        ("lfg-admin channel", "lfg", "channel", AdminFields.Channel, []),
        ("lfg-admin status", "lfg", "status", AdminFields.None, []),
        ("live-admin doctor", "live", "doctor", AdminFields.None, []),
        ("news-admin configure", "news", "configure", AdminFields.Channel, []),
        ("news-admin pause", "news", "pause", AdminFields.None, []),
        ("news-admin resume", "news", "resume", AdminFields.None, []),
        ("news-admin preview", "news", "preview", AdminFields.None, []),
        ("news-admin status", "news", "status", AdminFields.None, []),
        ("news-admin doctor", "news", "doctor", AdminFields.None, []),
        ("volleyball-admin preview", "volleyball", "preview", AdminFields.None, ["card"]),
        ("volleyball-admin status", "volleyball", "status", AdminFields.None, []),
        ("volleyball-admin doctor", "volleyball", "doctor", AdminFields.None, []),
        ("volleyball-admin pause", "volleyball", "pause", AdminFields.None, []),
        ("volleyball-admin resume", "volleyball", "resume", AdminFields.None, []),
        ("volleyball-admin configure channel", "volleyball", "configure-channel", AdminFields.Channel, []),
        ("volleyball-admin configure notifications", "volleyball", "configure-notifications", AdminFields.None,
            ["match_reminder_15m", "match_started", "set_finished", "match_finished", "match_postponed_cancelled"]),
        ("volleyball-admin configure role", "volleyball", "configure-role", AdminFields.Role, ["ping_reminder", "ping_final", "clear"]),
    ];

    /// <summary>Admin modules that were added to <c>/tsq-admin</c> directly (never an old "-admin" root).</summary>
    private static readonly string[] AddedLater = ["updates"];

    // ================================================================== A. command shape

    [Fact]
    public async Task Tsq_admin_is_one_flat_chat_input_command_with_zero_subcommands_and_zero_groups()
    {
        var (manifest, admin) = await BuildAsync();
        CommandManifestValidator.Validate(manifest, admin).Should().BeEmpty();
        manifest.Commands.Where(c => c.Name == AdminCatalog.Name).Should().ContainSingle();
        var command = manifest.Find(AdminCatalog.Name)!;

        // The command TYPE is CHAT_INPUT (1); the forbidden things are OPTIONS of type SUB_COMMAND (1) / SUB_COMMAND_GROUP (2).
        command.Type.Should().Be(CommandKind.ChatInput);
        var options = Flatten(command.Options).ToList();
        options.Count(o => o.Type == OptionType.SubCommand).Should().Be(0);
        options.Count(o => o.Type == OptionType.SubCommandGroup).Should().Be(0);
        command.Options.Should().OnlyContain(o => o.Options.Count == 0, "every option is a plain parameter");

        command.Options.Select(o => (o.Name, o.Type, o.Required, o.Autocomplete)).Should().Equal(
            ("modul", OptionType.String, true, true),
            ("islem", OptionType.String, true, true),
            ("kanal", OptionType.Channel, false, false),
            ("uye", OptionType.User, false, false),
            ("rol", OptionType.Role, false, false),
            ("tarih", OptionType.String, false, false));
        command.Options.Single(o => o.Name == "kanal").ChannelTypes.Should().Equal(0, 5);
        command.Options.Should().OnlyContain(o => o.DescriptionLocalizations.ContainsKey("tr"));
        command.DescriptionLocalizations["tr"].Should().Be("TSQ Bot yönetimi: modül ve işlem seçin");
        command.DefaultMemberPermissions.Should().Be("32");
        CommandManifestValidator.CountCharacters(command).Should().BeLessThan(500);

        manifest.Commands.Where(c => c.Name.EndsWith("-admin", StringComparison.Ordinal)).Select(c => c.Name).Should().Equal(AdminCatalog.Name);
        (await BuildAsync()).Manifest.ToDocumentJson().Should().Be(manifest.ToDocumentJson(), "the same manifest on every run");
    }

    [Fact]
    public async Task No_grouped_tsq_admin_path_is_registered_for_dispatch_any_more()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        service.SlashCommands.Where(c => c.Name == AdminCatalog.Name).Should().ContainSingle().Which.Module.IsSlashGroup.Should().BeFalse();
        service.SlashCommands.Should().NotContain(c => ModuleChainHas(c.Module, AdminCatalog.Name));
        service.SearchSlashCommand(Slash("tsq-admin news status")).IsSuccess.Should().BeFalse("the grouped path is gone");
        service.SearchSlashCommand(Slash("news-admin status")).IsSuccess.Should().BeFalse("no alias of the old roots");
        service.SearchSlashCommand(Slash("tsq-admin")).IsSuccess.Should().BeTrue();
    }

    // ================================================================== B. completeness

    [Fact]
    public async Task Every_former_operation_has_exactly_one_module_operation_and_every_former_option_a_place()
    {
        var before = Doc("manifest-before-tsq-admin.json");
        var old = new Dictionary<string, JsonNode>();
        foreach (var root in before.Where(c => (int)c.Payload["type"]! == 1 && c.Name.EndsWith("-admin", StringComparison.Ordinal)))
        {
            foreach (var option in root.Payload["options"]!.AsArray().Select(o => o!))
            {
                if ((int)option["type"]! == 1)
                    old[root.Name + " " + option["name"]] = option;
                else
                    foreach (var sub in option["options"]!.AsArray())
                        old[$"{root.Name} {option["name"]} {sub!["name"]}"] = sub;
            }
        }

        old.Keys.Should().BeEquivalentTo(Moves.Select(m => m.Old)).And.HaveCount(47);

        await using var host = await TestHost.CreateAsync();
        var catalog = host.Services.GetRequiredService<AdminCatalog>();
        // Modules added after the merge never had an "-admin" root: they have no former operation (their own tests cover them).
        catalog.Modules.Where(m => !AddedLater.Contains(m.Id)).SelectMany(m => m.Operations.Select(o => m.Id + " " + o.Id)).Should()
            .BeEquivalentTo(Moves.Select(m => m.Module + " " + m.Op));
        catalog.Modules.Select(m => m.Id).Should().BeEquivalentTo(Moves.Select(m => m.Module).Distinct().Concat(AddedLater));
        catalog.Problems(host.Services.GetRequiredService<ToroSquad.Core.Modules.ModuleRegistry>()).Should().BeEmpty();

        foreach (var move in Moves)
        {
            var operation = catalog.Find(move.Module)!.Find(move.Op)!;
            operation.Accepts.Should().Be(move.Accepts, move.Old);
            var formerOptions = (old[move.Old]["options"]?.AsArray() ?? []).Select(o => (string)o!["name"]!).ToList();
            var shared = formerOptions.Where(o => SharedField(o) is { } f && move.Accepts.HasFlag(f)).ToList();
            formerOptions.Except(shared).Should().BeEquivalentTo(move.Form, $"{move.Old}: every former option is a shared option or part of the form");
        }
    }

    [Fact]
    public async Task Every_operation_that_needs_more_than_the_shared_options_opens_its_form_and_writes_nothing()
    {
        await using var host = await TestHost.CreateAsync();
        foreach (var move in Moves.Where(m => m.Form.Length > 0 || m.Accepts != AdminFields.None && m.Op != "set"))
        {
            // With no shared option filled, every such operation asks privately (a form or a picker) instead of guessing.
            if (move.Op is "set" or "show" or "status" or "doctor")
                continue;
            var respond = await RunAsync(host, Administrator(), move.Module, move.Op);
            (respond.Modals.Count + respond.Sent.Count(s => s.Components is not null && CustomIds(s.Components).Count > 0)).Should().Be(1, move.Old);
            respond.AllCustomIds().Should().OnlyContain(id => id.StartsWith(AdminCall.CustomIdPrefix, StringComparison.Ordinal), move.Old);
        }
    }

    // ================================================================== C. autocomplete and validation

    [Fact]
    public async Task Module_suggestions_list_only_modules_the_caller_can_administer_and_filter_by_name_or_id()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        (await SuggestAsync(host, service, "modul", "", null, CorePermission.ManageGuild)).Select(r => r.Value).Should()
            .Equal("birthday", "esports", "f1", "lfg", "live", "news", "updates", "volleyball");
        (await SuggestAsync(host, service, "modul", "güncelleme", null, CorePermission.ManageGuild)).Should().ContainSingle().Which.Name.Should().Be("Oyun güncellemeleri — updates");
        var news = await SuggestAsync(host, service, "modul", "haber", null, CorePermission.ManageGuild);
        news.Should().ContainSingle().Which.Name.Should().Be("Haberler — news");
        (await SuggestAsync(host, service, "modul", "NEWS", null, CorePermission.ManageGuild)).Select(r => r.Value).Should().Equal("news");
        (await SuggestAsync(host, service, "modul", "", null, CorePermission.ViewChannel)).Should().BeEmpty("a member can administer nothing");
        (await SuggestAsync(host, service, "modul", "", null, CorePermission.ManageGuild, inGuild: false)).Should().BeEmpty("no DM suggestions");
    }

    [Fact]
    public async Task Operation_suggestions_follow_the_current_module_value_and_the_callers_permissions()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        (await SuggestAsync(host, service, "islem", "", "news", CorePermission.ManageGuild)).Select(r => r.Value).Should()
            .Equal("configure", "pause", "resume", "preview", "status", "doctor");
        (await SuggestAsync(host, service, "islem", "durum", "news", CorePermission.ManageGuild)).Select(r => r.Value).Should().Equal("status");
        (await SuggestAsync(host, service, "islem", "", null, CorePermission.ManageGuild)).Should().BeEmpty("no module chosen yet: no invented operation");
        (await SuggestAsync(host, service, "islem", "", "nope", CorePermission.ManageGuild)).Should().BeEmpty();

        // The module changed in the same form: the next request only knows the new value (no remembered state).
        (await SuggestAsync(host, service, "islem", "", "esports", CorePermission.ManageGuild)).Select(r => r.Value).Should()
            .Contain("filters-show").And.NotContain("roles-map", "roles need Manage Roles too").And.NotContain("status");
        (await SuggestAsync(host, service, "islem", "", "esports", CorePermission.ManageGuild | CorePermission.ManageRoles)).Select(r => r.Value).Should().Contain("roles-map");
        (await SuggestAsync(host, service, "islem", "", "birthday", CorePermission.ManageGuild)).Select(r => r.Value).Should()
            .Equal(["configure", "status", "doctor"], "set/show need Administrator");
        (await SuggestAsync(host, service, "islem", "", "birthday", CorePermission.Administrator)).Select(r => r.Value).Should().Contain(["set", "show"]);
        (await SuggestAsync(host, service, "islem", "", "birthday", CorePermission.ManageGuild)).Should().OnlyContain(r => r.Name.EndsWith(" — " + r.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_module_foreign_operation_and_unused_options_are_refused_before_anything_runs()
    {
        await using var host = await TestHost.CreateAsync();
        var admin = TestHost.Admin(Guild);
        (await RunAsync(host, admin, "nope", "status")).Text.Should().Contain("Bilinmeyen modül");
        (await RunAsync(host, admin, "news", "roles-map")).Text.Should().Contain("`roles-map` işlemi `news` modülünde yok", "no fallback to another module or a default");
        var withMember = await RunAsync(host, admin, "news", "status", new AdminArgs(null, new AdminMember(5, true), null, null), AdminFields.User);
        withMember.Text.Should().Contain("`uye`").And.Contain("kullanmaz");
        withMember.Embeds.Should().BeEmpty("the operation did not run");
        (await RunAsync(host, admin, "  NEWS ", "Status")).Embeds.Should().ContainSingle("ids are trimmed and case-insensitive");
    }

    [Fact]
    public async Task The_slash_pipeline_parses_the_flat_options_and_refuses_dms()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        await using var scope = host.Scope();
        var values = new Dictionary<string, object> { ["modul"] = "news", ["islem"] = "configure", ["kanal"] = TextChannel(Channel.Value) };
        var executed = default(SlashCommandInfo);
        service.SlashCommandExecuted += (info, _, _) =>
        {
            executed = info;
            return Task.CompletedTask;
        };
        var result = await service.ExecuteCommandAsync(Context(Slash("tsq-admin", values), CorePermission.ManageGuild), scope.ServiceProvider);
        executed!.Name.Should().Be(AdminCatalog.Name);
        ((ExecuteResult)result).Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().StartWith("Invalid context type",
            "precondition, the typed options and the module's DI all passed; only the offline context stops before the handler body");
        (await service.ExecuteCommandAsync(Context(Slash("tsq-admin", values), CorePermission.ManageGuild, inGuild: false), scope.ServiceProvider))
            .ErrorReason.Should().Be(ToroModuleAttribute.GuildOnlyError);
    }

    // ================================================================== D. running operations

    [Fact]
    public async Task News_status_answers_privately_and_configure_with_kanal_saves_the_channel()
    {
        await using var host = await TestHost.CreateAsync();
        SetChannel(host);
        var status = await RunAsync(host, TestHost.Admin(Guild), "news", "status");
        status.Embeds.Should().ContainSingle().Which.Title.Should().Be("TSQ Haber durumu");
        status.Sent.Should().OnlyContain(s => s.Ephemeral);

        var configure = await RunAsync(host, TestHost.Admin(Guild), "news", "configure", new AdminArgs(Channel.Value, null, null, null), AdminFields.Channel);
        configure.Sent.Should().ContainSingle().Which.Components.Should().BeNull("no extra step when the input is complete");
        (await NewsChannelAsync(host)).Should().Be(Channel.Value);
    }

    [Fact]
    public async Task News_configure_without_kanal_opens_a_channel_picker_and_saves_only_the_picked_valid_channel()
    {
        await using var host = await TestHost.CreateAsync();
        SetChannel(host);
        var open = await RunAsync(host, TestHost.Admin(Guild), "news", "configure");
        var draft = DraftId(open.AllCustomIds()[0]);
        (await NewsChannelAsync(host)).Should().BeNull("nothing is saved by opening the form");

        // A channel that Discord did not resolve for this guild is refused (a forged id or another guild's channel).
        var forged = await FormAsync(host, TestHost.Admin(Guild), draft, AdminForms.ChannelAction, Picked(Channel.Value, TextChannel(Channel.Value, guild: 99)));
        forged.Text.Should().Contain("metin veya duyuru kanalı");
        (await NewsChannelAsync(host)).Should().BeNull();

        var saved = await FormAsync(host, TestHost.Admin(Guild), draft, AdminForms.ChannelAction, Picked(Channel.Value, TextChannel(Channel.Value)));
        saved.Updates.Should().ContainSingle("the form message shows the result and loses its select");
        (await NewsChannelAsync(host)).Should().Be(Channel.Value);
        (await FormAsync(host, TestHost.Admin(Guild), draft, AdminForms.ChannelAction, Picked(Channel.Value, TextChannel(Channel.Value))))
            .Text.Should().Contain("artık geçerli değil", "a second click does not run it again");
    }

    [Fact]
    public async Task Updates_operations_run_through_the_router_and_games_are_picked_from_the_registered_ones()
    {
        await using var host = await TestHost.CreateAsync();
        SetChannel(host);
        var admin = TestHost.Admin(Guild);
        (await SuggestAsync(host, await ServiceAsync(host), "islem", "", "updates", CorePermission.ManageGuild)).Select(r => r.Value).Should()
            .Equal("configure", "games", "game-enable", "game-disable", "game-channel", "pause", "resume", "preview", "status", "doctor");

        var games = await RunAsync(host, admin, "updates", "games");
        games.Sent.Should().OnlyContain(s => s.Ephemeral);
        games.Embeds.Should().ContainSingle().Which.Description.Should().Contain("**Counter-Strike 2** · Steam · `cs2` — kapalı")
            .And.Contain("**World of Warcraft: Forever** · Blizzard · `wow-forever` — kapalı");

        var configure = await RunAsync(host, admin, "updates", "configure", new AdminArgs(Channel.Value, null, null, null), AdminFields.Channel);
        configure.Sent.Should().ContainSingle().Which.Components.Should().BeNull("no extra step when the input is complete");
        (await UpdatesStatusAsync(host))!.ChannelId.Should().Be(Channel.Value);

        var enable = await RunAsync(host, admin, "updates", "game-enable");
        enable.Sent.Should().OnlyContain(s => s.Ephemeral);
        enable.SelectValues().Should().Equal("cs2", "wow-forever");
        (await UpdatesStatusAsync(host))!.Games.Should().OnlyContain(g => !g.Enabled, "nothing is saved by opening the picker");
        (await FormAsync(host, admin, DraftId(enable.AllCustomIds()[0]), UpdatesAdminOperations.GameAction, Selected("wow-forever"))).Updates.Should().ContainSingle()
            .Which.Text.Should().Contain("World of Warcraft: Forever güncellemeleri açıldı");
        var enableCs2 = await RunAsync(host, admin, "updates", "game-enable");
        (await FormAsync(host, admin, DraftId(enableCs2.AllCustomIds()[0]), UpdatesAdminOperations.GameAction, Selected("cs2"))).Updates.Should().ContainSingle()
            .Which.Text.Should().Contain("Counter-Strike 2 güncellemeleri açıldı");
        (await UpdatesStatusAsync(host))!.Games.Should().OnlyContain(g => g.Enabled);
        (await RunAsync(host, admin, "updates", "games")).Embeds.Single().Description.Should().Contain("`cs2` — açık").And.Contain("`wow-forever` — açık");

        var preview = await RunAsync(host, admin, "updates", "preview");
        preview.Sent.Should().OnlyContain(s => s.Ephemeral);
        (await FormAsync(host, admin, DraftId(preview.AllCustomIds()[0]), UpdatesAdminOperations.GameAction, Selected("wow-forever"))).Updates.Should()
            .ContainSingle("the preview replaces the picker");
        (await RunAsync(host, admin, "updates", "status")).Embeds.Should().ContainSingle().Which.Title.Should().Be("TSQ Bot Updates durumu");
        (await RunAsync(host, admin, "updates", "doctor")).Embeds.Should().ContainSingle().Which.Title.Should().Be("TSQ Bot Updates tanı raporu");
        (await RunAsync(host, admin, "updates", "pause")).Text.Should().Contain("duraklatıldı");
        (await RunAsync(host, admin, "updates", "resume")).Text.Should().Contain("yeniden başladı");

        var disable = await RunAsync(host, admin, "updates", "game-disable");
        (await FormAsync(host, admin, DraftId(disable.AllCustomIds()[0]), UpdatesAdminOperations.GameAction, Selected("cs2"))).Updates.Should().ContainSingle()
            .Which.Text.Should().Contain("kapatıldı");
        (await UpdatesStatusAsync(host))!.Games.Select(g => (g.Game.Key, g.Enabled)).Should().Equal(("cs2", false), ("wow-forever", true));
        var member = await RunAsync(host, TestHost.Member(Guild), "updates", "game-enable");
        member.Text.Should().Contain("yetki");
        member.AllCustomIds().Should().BeEmpty("a member does not even get the picker");
        host.Transport.SendCalls.Should().Be(0, "no admin operation posts to a channel");
    }

    [Fact]
    public async Task Updates_configure_without_kanal_opens_a_channel_picker()
    {
        await using var host = await TestHost.CreateAsync();
        SetChannel(host);
        var open = await RunAsync(host, TestHost.Admin(Guild), "updates", "configure");
        var draft = DraftId(open.AllCustomIds()[0]);
        (await UpdatesStatusAsync(host))!.ChannelId.Should().BeNull("nothing is saved by opening the form");
        (await FormAsync(host, TestHost.Admin(Guild), draft, AdminForms.ChannelAction, Picked(Channel.Value, TextChannel(Channel.Value, guild: 99))))
            .Text.Should().Contain("metin veya duyuru kanalı");
        (await FormAsync(host, TestHost.Admin(Guild), draft, AdminForms.ChannelAction, Picked(Channel.Value, TextChannel(Channel.Value)))).Updates.Should().ContainSingle();
        (await UpdatesStatusAsync(host))!.ChannelId.Should().Be(Channel.Value);
    }

    [Fact]
    public async Task Updates_game_channel_gives_one_game_its_own_channel_and_only_after_the_game_is_picked()
    {
        const ulong Own = 7702;
        await using var host = await TestHost.CreateAsync();
        SetChannel(host);
        host.Guilds.SetChannel(Guild, new ChannelId(Own), new BotChannelAccess(true, true, CorePermission.ViewChannel | CorePermission.SendMessages | CorePermission.EmbedLinks));
        var admin = TestHost.Admin(Guild);
        async Task<ulong?> OwnChannelAsync(string game) => (await UpdatesStatusAsync(host))!.Games.Single(g => g.Game.Key == game).ChannelId;

        // Without the guild's Updates channel there is nothing a game could fall back to.
        var early = await RunAsync(host, admin, "updates", "game-channel", new AdminArgs(Own, null, null, null), AdminFields.Channel);
        (await FormAsync(host, admin, DraftId(early.AllCustomIds()[0]), UpdatesAdminOperations.GameAction, Selected("wow-forever"))).Updates.Should().ContainSingle()
            .Which.Text.Should().Contain("islem:configure");
        (await OwnChannelAsync("wow-forever")).Should().BeNull();
        await RunAsync(host, admin, "updates", "configure", new AdminArgs(Channel.Value, null, null, null), AdminFields.Channel);

        // With kanal: the game is picked next, and only then anything is saved.
        var withChannel = await RunAsync(host, admin, "updates", "game-channel", new AdminArgs(Own, null, null, null), AdminFields.Channel);
        withChannel.Sent.Should().OnlyContain(s => s.Ephemeral);
        withChannel.SelectValues().Should().Equal("cs2", "wow-forever");
        (await OwnChannelAsync("wow-forever")).Should().BeNull("nothing is saved by opening the picker");
        var draft = DraftId(withChannel.AllCustomIds()[0]);
        (await FormAsync(host, admin, draft, UpdatesAdminOperations.GameAction, Selected("dota2"))).Text.Should().Contain("desteklenmiyor", "a forged value is not a registered game");
        (await FormAsync(host, admin, draft, UpdatesAdminOperations.GameAction, Selected("wow-forever"))).Updates.Should().ContainSingle()
            .Which.Text.Should().Contain("World of Warcraft: Forever").And.Contain("<#7702>").And.Contain("islem:game-enable", "the game itself is still off");
        (await OwnChannelAsync("wow-forever")).Should().Be(Own);
        (await OwnChannelAsync("cs2")).Should().BeNull("the other game keeps the common channel");
        (await UpdatesStatusAsync(host))!.ChannelId.Should().Be(Channel.Value, "the guild's Updates channel is untouched");
        (await FormAsync(host, admin, draft, UpdatesAdminOperations.GameAction, Selected("cs2"))).Text.Should().Contain("artık geçerli değil", "a second click does not run it again");
        (await OwnChannelAsync("cs2")).Should().BeNull();
        (await RunAsync(host, admin, "updates", "games")).Embeds.Single().Description.Should().Contain("`wow-forever` — kapalı → <#7702>").And.NotContain("`cs2` — kapalı →");

        // Without kanal: a channel select first, then the game.
        var pick = await RunAsync(host, admin, "updates", "game-channel");
        var pickDraft = DraftId(pick.AllCustomIds()[0]);
        pick.AllCustomIds().Should().Contain(id => id.EndsWith(":" + AdminForms.ChannelAction, StringComparison.Ordinal))
            .And.Contain(id => id.EndsWith(":" + AdminForms.ClearAction, StringComparison.Ordinal));
        (await FormAsync(host, admin, pickDraft, UpdatesAdminOperations.GameAction, Selected("cs2"))).Text.Should().Contain("desteklenmiyor", "a game cannot be picked before the channel step");
        (await FormAsync(host, admin, pickDraft, AdminForms.ChannelAction, Picked(Own, TextChannel(Own, guild: 99)))).Text.Should().Contain("metin veya duyuru kanalı");
        var games = await FormAsync(host, admin, pickDraft, AdminForms.ChannelAction, Picked(Own, TextChannel(Own)));
        games.Updates.Should().ContainSingle("the same private message turns into the game select").Which.Text.Should().Contain("<#7702>");
        (await OwnChannelAsync("cs2")).Should().BeNull("still nothing saved");
        (await FormAsync(host, admin, pickDraft, UpdatesAdminOperations.GameAction, Selected("cs2"))).Updates.Should().ContainSingle().Which.Text.Should().Contain("Counter-Strike 2");
        (await OwnChannelAsync("cs2")).Should().Be(Own);

        // The explicit button sends a game back to the common channel.
        var back = await RunAsync(host, admin, "updates", "game-channel");
        var backDraft = DraftId(back.AllCustomIds()[0]);
        (await FormAsync(host, admin, backDraft, AdminForms.ClearAction, AdminInput.None)).Updates.Should().ContainSingle().Which.Text.Should().Contain("ortak");
        (await OwnChannelAsync("cs2")).Should().Be(Own, "nothing is saved until the game is picked");
        (await FormAsync(host, admin, backDraft, UpdatesAdminOperations.GameAction, Selected("cs2"))).Updates.Should().ContainSingle().Which.Text.Should().Contain("ortak güncelleme kanalına");
        (await OwnChannelAsync("cs2")).Should().BeNull();
        (await OwnChannelAsync("wow-forever")).Should().Be(Own);

        var member = await RunAsync(host, TestHost.Member(Guild), "updates", "game-channel", new AdminArgs(Own, null, null, null), AdminFields.Channel);
        member.Text.Should().Contain("yetki");
        member.AllCustomIds().Should().BeEmpty("a member does not even get the picker");
        host.Transport.SendCalls.Should().Be(0, "no admin operation posts to a channel");
    }

    [Fact]
    public async Task With_several_registered_games_the_game_operations_offer_exactly_the_registered_games()
    {
        await using var host = await TestHost.CreateAsync(replace: services =>
        {
            services.AddSingleton(FakeUpdateProvider.Game);
            services.AddSingleton<ToroSquad.Modules.Updates.Domain.IGameUpdateProvider>(new FakeUpdateProvider());
        });
        var admin = TestHost.Admin(Guild);
        foreach (var operation in new[] { "game-enable", "game-disable", "preview" })
            (await RunAsync(host, admin, "updates", operation)).SelectValues().Should().Equal(["cs2", "fakegame", "wow-forever"], operation);

        var open = await RunAsync(host, admin, "updates", "game-enable");
        var draft = DraftId(open.AllCustomIds()[0]);
        (await UpdatesStatusAsync(host))!.Games.Should().OnlyContain(g => !g.Enabled, "nothing is saved by opening the form");
        (await FormAsync(host, admin, draft, UpdatesAdminOperations.GameAction, Selected("dota2"))).Text.Should().Contain("desteklenmiyor", "a forged value is not a registered game");
        (await UpdatesStatusAsync(host))!.Games.Should().OnlyContain(g => !g.Enabled);

        (await FormAsync(host, admin, draft, UpdatesAdminOperations.GameAction, Selected("fakegame"))).Updates.Should().ContainSingle().Which.Text.Should().Contain("Fake Game güncellemeleri açıldı");
        (await UpdatesStatusAsync(host))!.Games.Select(g => (g.Game.Key, g.Enabled)).Should().Equal(("cs2", false), ("fakegame", true), ("wow-forever", false));
        (await FormAsync(host, admin, draft, UpdatesAdminOperations.GameAction, Selected("cs2"))).Text.Should().Contain("artık geçerli değil", "a second click does not run it again");
        (await UpdatesStatusAsync(host))!.Games.Single(g => g.Game.Key == "cs2").Enabled.Should().BeFalse();

        var previewDraft = DraftId((await RunAsync(host, admin, "updates", "preview")).AllCustomIds()[0]);
        (await FormAsync(host, admin, previewDraft, UpdatesAdminOperations.GameAction, Selected("cs2"))).Updates.Should().ContainSingle("the preview replaces the picker");
    }

    [Fact]
    public async Task Birthday_show_and_set_need_administrator_and_set_validates_the_date()
    {
        await using var host = await TestHost.CreateAsync();
        var member = new AdminMember(5, true);
        var manager = TestHost.Admin(Guild);
        (await RunAsync(host, manager, "birthday", "show", new AdminArgs(null, member, null, null), AdminFields.User)).Text.Should().Contain("yetki", "Manage Server is not enough");
        (await RunAsync(host, manager, "birthday", "set", new AdminArgs(null, member, null, "14.03"), AdminFields.User | AdminFields.Date)).Text.Should().Contain("yetki");
        (await BirthdayAsync(host, 5)).Should().BeNull();

        (await RunAsync(host, Administrator(), "birthday", "set", new AdminArgs(null, member, null, "32.13"), AdminFields.User | AdminFields.Date)).Text
            .Should().Contain(Localize(host, "birthday.set.invalid"));
        (await BirthdayAsync(host, 5)).Should().BeNull();
        (await RunAsync(host, Administrator(), "birthday", "set", new AdminArgs(null, member, null, "14.03"), AdminFields.User | AdminFields.Date)).Text
            .Should().Contain("<@5>").And.Contain("14 Mart");
        (await BirthdayAsync(host, 5)).Should().Be((14, 3));
        (await RunAsync(host, Administrator(), "birthday", "show", new AdminArgs(null, member, null, null), AdminFields.User)).Text.Should().Contain("14 Mart");
    }

    [Fact]
    public async Task Birthday_set_without_a_date_opens_the_date_form_and_saves_only_on_submit()
    {
        await using var host = await TestHost.CreateAsync();
        var open = await RunAsync(host, Administrator(), "birthday", "set", new AdminArgs(null, new AdminMember(6, true), null, null), AdminFields.User);
        open.Modals.Should().ContainSingle();
        var draft = DraftId(open.Modals[0].CustomId);
        (await BirthdayAsync(host, 6)).Should().BeNull();

        (await FormAsync(host, Administrator(), draft, "date", Modal(("date", ["01.02"])))).Text.Should().Contain("<@6>");
        (await BirthdayAsync(host, 6)).Should().Be((1, 2));
    }

    [Fact]
    public async Task Esports_tier_filter_is_added_from_its_select_and_role_mapping_is_created_from_its_form()
    {
        await using var host = await TestHost.CreateAsync();
        await host.SetUpEsportsGuildAsync(Guild, Channel, new RoleInfo(new RoleId(Role), "CS", 5, CorePermission.None, false, false, true));
        var admin = TestHost.Admin(Guild);

        var tiers = await RunAsync(host, admin, "esports", "filters-tier");
        var tierDraft = DraftId(tiers.AllCustomIds()[0]);
        await FormAsync(host, admin, tierDraft, "addpick", Selected("1"));
        (await host.InScopeAsync(sp => sp.GetRequiredService<EsportsConfigService>().GetAsync(Guild, CancellationToken.None))).Filters.Tiers.Should().Equal("1");

        var map = await RunAsync(host, admin, "esports", "roles-map", new AdminArgs(null, null, Role, null), AdminFields.Role);
        map.Modals.Should().ContainSingle();
        var mapped = await FormAsync(host, admin, DraftId(map.Modals[0].CustomId), "save",
            Modal((AdminForms.RoleField, [Role.ToString(System.Globalization.CultureInfo.InvariantCulture)]), ("query", [""]), (AdminForms.SwitchesField, ["ping_reminder"])));
        mapped.Text.Should().NotContain("geçerli değil");
        var rows = await host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>().ListAsync(Guild, CancellationToken.None));
        rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new { RoleId = Role, TeamKey = "", PingOnReminder = true, PingOnResult = false });
    }

    [Fact]
    public async Task Long_mapping_lists_are_paged_instead_of_cut()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            for (var i = 0; i < 30; i++)
                db.Set<RoleMappingEntity>().Add(new RoleMappingEntity { GuildId = Guild.Value, RoleId = 1000UL + (ulong)i, CreatedAt = TestHost.T0 });
            await db.SaveChangesAsync();
        });
        var admin = Administrator();
        var first = await RunAsync(host, admin, "esports", "roles-unmap");
        first.Text.Should().Contain("Sayfa 1/2");
        first.SelectValues().Should().HaveCount(25);
        var draft = DraftId(first.AllCustomIds()[0]);
        first.AllCustomIds().Should().Contain(AdminCall.CustomIdPrefix + draft + ":page-1");

        var second = await FormAsync(host, admin, draft, "page-1", AdminInput.None);
        second.Updates.Should().ContainSingle();
        second.SelectValues().Should().HaveCount(5).And.Contain("30");
    }

    [Fact]
    public async Task F1_notifications_form_writes_only_the_switches_changed_and_keeps_a_concurrent_change()
    {
        await using var host = await TestHost.CreateAsync();
        var open = await RunAsync(host, TestHost.Admin(Guild), "f1", "configure-notifications");
        var draft = DraftId(open.Modals.Single().CustomId);
        SelectOptions(open.Modals[0]).Where(o => o.IsDefault == true).Select(o => o.Value).Should().BeEquivalentTo(
            ["practice_start", "practice_results", "sprint_start", "sprint_results", "race_start", "race_results", "standings"], "the current (default) values are shown");

        // Another admin turns qualifying starts on while the form is open.
        await host.InScopeAsync(async sp => (await sp.GetRequiredService<Formula1ConfigService>().SetNotificationsAsync(TestHost.Admin(Guild, 9),
            new F1NotificationChanges(QualifyingStart: true), CancellationToken.None)).Succeeded.Should().BeTrue());

        // The form turns practice starts off and leaves everything else as it was shown.
        await FormAsync(host, TestHost.Admin(Guild), draft, "save", Modal((AdminForms.SwitchesField,
            ["practice_results", "sprint_start", "sprint_results", "race_start", "race_results", "standings"])));
        var stored = (await host.InScopeAsync(sp => sp.GetRequiredService<Formula1ConfigService>().GetAsync(Guild, CancellationToken.None)))!;
        stored.NotifyPracticeStart.Should().BeFalse();
        stored.NotifyQualifyingStart.Should().BeTrue("a switch the form did not change is not overwritten with the form's stale value");
        stored.NotifyRaceStart.Should().BeTrue();

        (await FormAsync(host, TestHost.Admin(Guild), draft, "save", Modal((AdminForms.SwitchesField, [])))).Text.Should().Contain("artık geçerli değil",
            "a double submit writes once");
        (await host.InScopeAsync(sp => sp.GetRequiredService<Formula1ConfigService>().GetAsync(Guild, CancellationToken.None)))!.NotifyRaceStart.Should().BeTrue();
    }

    [Fact]
    public async Task F1_role_form_keeps_the_role_when_left_empty_and_removes_it_only_when_asked()
    {
        await using var host = await TestHost.CreateAsync();
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild, new RoleInfo(new RoleId(Role), "F1", 5, CorePermission.None, false, false, true)));
        var admin = TestHost.Admin(Guild);
        await host.InScopeAsync(async sp => (await sp.GetRequiredService<Formula1ConfigService>().SetRoleAsync(admin, Role, true, false, CancellationToken.None))
            .Succeeded.Should().BeTrue());

        var keep = await RunAsync(host, admin, "f1", "configure-role");
        await FormAsync(host, admin, DraftId(keep.Modals.Single().CustomId), "save", Modal((AdminForms.SwitchesField, [])));
        var stored = (await host.InScopeAsync(sp => sp.GetRequiredService<Formula1ConfigService>().GetAsync(Guild, CancellationToken.None)))!;
        stored.PingRoleId.Should().Be(Role, "an empty role select keeps the role");
        stored.PingOnStarts.Should().BeFalse("the ping switch was unticked");

        var clear = await RunAsync(host, admin, "f1", "configure-role");
        await FormAsync(host, admin, DraftId(clear.Modals.Single().CustomId), "save", Modal((AdminForms.ClearField, [AdminForms.ClearField]), (AdminForms.SwitchesField, [])));
        (await host.InScopeAsync(sp => sp.GetRequiredService<Formula1ConfigService>().GetAsync(Guild, CancellationToken.None)))!.PingRoleId.Should().BeNull();
    }

    [Fact]
    public async Task Lfg_channel_restriction_is_removed_only_by_the_explicit_button()
    {
        await using var host = await TestHost.CreateAsync();
        SetChannel(host);
        var admin = TestHost.Admin(Guild);
        await RunAsync(host, admin, "lfg", "channel", new AdminArgs(Channel.Value, null, null, null), AdminFields.Channel);
        (await LfgChannelAsync(host)).Should().Be(Channel.Value);

        var open = await RunAsync(host, admin, "lfg", "channel");
        (await LfgChannelAsync(host)).Should().Be(Channel.Value, "an empty kanal does not remove the restriction by itself");
        await FormAsync(host, admin, DraftId(open.AllCustomIds()[0]), AdminForms.ClearAction, AdminInput.None);
        (await LfgChannelAsync(host)).Should().BeNull();
    }

    [Fact]
    public async Task Roles_list_needs_manage_server_and_every_mapping_change_needs_manage_roles_too()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Set<RoleMappingEntity>().Add(new RoleMappingEntity { GuildId = Guild.Value, RoleId = Role, CreatedAt = TestHost.T0 });
            await db.SaveChangesAsync();
        });
        var manageOnly = new ActorContext(Guild, new UserId(1), CorePermission.ManageGuild, [], false, 50);
        var roles = TestHost.Admin(Guild); // Manage Server + Manage Roles

        // Suggestions: Manage Server alone sees roles-list, not the three operations that change mappings.
        var suggested = (await SuggestAsync(host, service, "islem", "roles", "esports", CorePermission.ManageGuild)).Select(r => r.Value).ToList();
        suggested.Should().Equal("roles-list");
        (await SuggestAsync(host, service, "islem", "roles", "esports", CorePermission.ManageGuild | CorePermission.ManageRoles)).Select(r => r.Value).Should()
            .Equal("roles-list", "roles-map", "roles-unmap", "roles-selfservice");

        (await RunAsync(host, manageOnly, "esports", "roles-list")).Embeds.Should().ContainSingle("Manage Server is enough to read the mappings, as before");
        foreach (var change in new[] { "roles-map", "roles-unmap", "roles-selfservice" })
        {
            var refused = await RunAsync(host, manageOnly, "esports", change);
            refused.Text.Should().Contain("yetki", change);
            refused.Modals.Should().BeEmpty(change);
            refused.AllCustomIds().Should().BeEmpty($"{change}: not even the mapping picker opens without Manage Roles");
        }

        var member = await RunAsync(host, TestHost.Member(Guild), "esports", "roles-list");
        member.Embeds.Should().BeEmpty("a member reads no admin data");
        member.Text.Should().Contain("yetki");

        // The picker belongs to the change operation: losing Manage Roles after opening it stops the change.
        var picker = await RunAsync(host, roles, "esports", "roles-unmap");
        var draft = DraftId(picker.AllCustomIds()[0]);
        (await FormAsync(host, manageOnly, draft, "mapping", Selected("1"))).Text.Should().Contain("yetki");
        (await host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>().ListAsync(Guild, CancellationToken.None))).Should().ContainSingle();

        // Administrator and the guild owner keep full access.
        (await RunAsync(host, Administrator(), "esports", "roles-unmap")).AllCustomIds().Should().NotBeEmpty();
        var owner = new ActorContext(Guild, new UserId(1), CorePermission.None, [], true, 0);
        (await RunAsync(host, owner, "esports", "roles-map")).Modals.Should().ContainSingle();
    }

    /// <summary>
    /// The JSON Discord.Net 3.20.1 itself sends for each modal (its internal component → API model mapping and serializer, as
    /// in <c>RespondWithModalAsync</c>), measured against Discord's documented limits (components reference, 2026-09-29):
    /// 1-5 top-level components, each a Label (type 18, text ≤ 45) wrapping one input; Checkbox Group ≤ 10 options; String
    /// Select ≤ 25 options; custom ids 1-100 and unique; no <c>disabled</c> in a modal; an empty choice needs required=false and
    /// min_values=0.
    /// </summary>
    [Fact]
    public async Task Every_admin_modal_payload_respects_discords_modal_limits_and_allows_an_empty_choice()
    {
        await using var host = await TestHost.CreateAsync();
        var admin = Administrator();
        var modals = new List<(string Name, Modal Modal, string Shape)>();
        async Task AddAsync(string module, string op, AdminArgs? args = null, AdminFields provided = AdminFields.None, string shape = "")
        {
            var respond = await RunAsync(host, admin, module, op, args, provided);
            modals.Add((module + " " + op, respond.Modals.Single(), shape));
        }

        await AddAsync("f1", "configure-notifications", shape: "18>3[16]");
        await AddAsync("volleyball", "configure-notifications", shape: "18>3[5]");
        await AddAsync("f1", "configure-role", shape: "18>6 | 18>22[2] | 18>22[1]");
        await AddAsync("volleyball", "configure-role", shape: "18>6 | 18>22[2] | 18>22[1]");
        await AddAsync("esports", "configure", shape: "18>8 | 18>22[3] | 18>4");
        await AddAsync("esports", "roles-map", shape: "18>6 | 18>4 | 18>22[2]");
        await AddAsync("esports", "filters-vrs", shape: "18>4");
        await AddAsync("birthday", "set", new AdminArgs(null, new AdminMember(5, true), null, null), AdminFields.User, "18>4");
        var menu = await RunAsync(host, admin, "esports", "filters-team");
        var search = await FormAsync(host, admin, DraftId(menu.AllCustomIds()[0]), "add", AdminInput.None);
        modals.Add(("esports filters-team search", search.Modals.Single(), "18>4"));

        foreach (var (name, modal, shape) in modals)
        {
            modal.Title.Length.Should().BeInRange(1, 45, name);
            modal.CustomId.Length.Should().BeInRange(1, 100, name);
            var top = DiscordPayload(modal);
            top.Count.Should().BeInRange(1, 5, name);
            string.Join(" | ", top.Select(t => Shape(t!))).Should().Be(shape, name);
            payloadText(top).Should().NotContain("\"disabled\":true", name);

            var inputs = top.Select(t => t!["component"]!).ToList();
            inputs.Select(i => (string)i["custom_id"]!).Should().OnlyHaveUniqueItems(name).And.OnlyContain(id => id.Length >= 1 && id.Length <= 100);
            foreach (var label in top)
                ((string)label!["label"]!).Length.Should().BeInRange(1, 45, name);
            foreach (var group in inputs.Where(i => (int)i["type"]! == 22))
            {
                group["options"]!.AsArray().Count.Should().BeInRange(1, 10, name);
                ((bool)group["required"]!).Should().BeFalse($"{name}: nothing ticked is a valid answer");
                ((int)group["min_values"]!).Should().Be(0, name);
            }

            foreach (var select in inputs.Where(i => (int)i["type"]! == 3))
            {
                select["options"]!.AsArray().Count.Should().BeInRange(1, 25, name);
                ((bool)select["required"]!).Should().BeFalse($"{name}: every notification can be switched off");
                ((int)select["min_values"]!).Should().Be(0, name);
                ((int)select["max_values"]!).Should().Be(select["options"]!.AsArray().Count, name);
            }
        }

        // F1: the 16 switches are ONE string select (not a checkbox group, whose limit is 10), pre-selected as stored.
        var f1 = DiscordPayload(modals[0].Modal)[0]!["component"]!;
        f1["options"]!.AsArray().Where(o => (bool?)o!["default"] == true).Select(o => (string)o!["value"]!).Should().BeEquivalentTo(
            ["practice_start", "practice_results", "sprint_start", "sprint_results", "race_start", "race_results", "standings"]);

        static string payloadText(JsonArray top) => top.ToJsonString();
    }

    [Fact]
    public void A_submitted_modal_is_read_by_custom_id_and_an_empty_choice_is_not_a_missing_field()
    {
        IComponentInteractionData Field(string id, string? value, string[]? values) =>
            InterfaceFake.Create<IComponentInteractionData>(new() { ["CustomId"] = id, ["Value"] = value, ["Values"] = (IReadOnlyCollection<string>?)values });
        var data = InterfaceFake.Create<IModalInteractionData>(new()
        {
            ["Components"] = (IReadOnlyCollection<IComponentInteractionData>)
            [
                Field(AdminForms.SwitchesField, null, []),
                Field("query", "aurora", null),
                Field(AdminForms.RoleField, null, ["77"]),
            ],
        });
        var input = AdminInput.FromModal(data);
        input.Fields.Should().ContainKey(AdminForms.SwitchesField).WhoseValue.Should().BeEmpty("nothing ticked");
        input.Text("query").Should().Be("aurora");
        input.Id(AdminForms.RoleField).Should().Be(77);
        input.Fields.Should().NotContainKey(AdminForms.ClearField, "a field Discord did not send stays absent");
    }

    [Fact]
    public async Task F1_notifications_can_all_be_switched_off_and_an_incomplete_submission_writes_nothing()
    {
        await using var host = await TestHost.CreateAsync();
        var admin = TestHost.Admin(Guild);
        var open = await RunAsync(host, admin, "f1", "configure-notifications");
        var draft = DraftId(open.Modals.Single().CustomId);
        (await host.InScopeAsync(sp => sp.GetRequiredService<Formula1ConfigService>().GetAsync(Guild, CancellationToken.None))).Should().BeNull("opening the form writes nothing");

        (await FormAsync(host, admin, draft, "save", Modal(("unrelated", ["x"])))).Text.Should().Contain("eksik");
        (await host.InScopeAsync(sp => sp.GetRequiredService<Formula1ConfigService>().GetAsync(Guild, CancellationToken.None))).Should().BeNull(
            "a submission without the switches field is not 'everything off'");

        await FormAsync(host, admin, draft, "save", Modal((AdminForms.SwitchesField, [])));
        var stored = (await host.InScopeAsync(sp => sp.GetRequiredService<Formula1ConfigService>().GetAsync(Guild, CancellationToken.None)))!;
        new[]
        {
            stored.NotifyPracticeStart, stored.NotifyPracticeResults, stored.NotifySprintStart, stored.NotifySprintResults, stored.NotifyRaceStart,
            stored.NotifyRaceResults, stored.NotifyStandings, stored.NotifyQualifyingStart, stored.NotifyQualifyingResults, stored.NotifySprintQualifyingStart,
            stored.NotifySprintQualifyingResults, stored.NotifyWeekendSchedule, stored.NotifyRaceReminder, stored.NotifyDisqualification, stored.NotifySafetyCar,
            stored.NotifyRedFlag,
        }.Should().OnlyContain(on => !on, "an empty selection switches every notification off");
    }

    // ================================================================== E. form safety

    [Fact]
    public async Task A_form_answers_only_its_owner_in_its_guild_while_valid_and_permitted()
    {
        await using var host = await TestHost.CreateAsync();
        SetChannel(host);
        var owner = TestHost.Admin(Guild);
        var draft = DraftId((await RunAsync(host, owner, "news", "configure")).AllCustomIds()[0]);
        var pick = Picked(Channel.Value, TextChannel(Channel.Value));

        (await FormAsync(host, TestHost.Admin(Guild, 3), draft, AdminForms.ChannelAction, pick)).Text.Should().Contain("başka bir yöneticiye");
        (await FormAsync(host, owner with { GuildId = new GuildId(43) }, draft, AdminForms.ChannelAction, pick)).Text.Should().Contain("başka bir yöneticiye");
        (await FormAsync(host, TestHost.Member(Guild, 1), draft, AdminForms.ChannelAction, pick)).Text.Should().Contain("yetki", "a lost permission stops the save");
        (await FormAsync(host, owner, "0000000000000000", AdminForms.ChannelAction, pick)).Text.Should().Contain("artık geçerli değil", "an unknown (forged) draft");
        (await NewsChannelAsync(host)).Should().BeNull();

        (await FormAsync(host, owner, draft, AdminRouter.CancelAction, AdminInput.None)).Updates.Should().ContainSingle().Which.Text.Should().Contain("İptal edildi");
        (await FormAsync(host, owner, draft, AdminForms.ChannelAction, pick)).Text.Should().Contain("artık geçerli değil");
        (await NewsChannelAsync(host)).Should().BeNull("a cancelled form writes nothing");

        var late = DraftId((await RunAsync(host, owner, "news", "configure")).AllCustomIds()[0]);
        host.Clock.Advance(AdminDrafts.Lifetime + TimeSpan.FromSeconds(1));
        (await FormAsync(host, owner, late, AdminForms.ChannelAction, pick)).Text.Should().Contain("artık geçerli değil", "expired (or lost in a restart)");
        (await NewsChannelAsync(host)).Should().BeNull();
    }

    [Fact]
    public async Task Admin_forms_keep_their_own_custom_ids_and_existing_buttons_still_reach_their_handlers()
    {
        await using var host = await TestHost.CreateAsync();
        var service = await ServiceAsync(host);
        foreach (var (customId, handler) in new[]
                 {
                     (AdminCall.CustomIdPrefix + "0123456789abcdef:save", "TsqAdminCommands"),
                     (EsportsCommands.PanelPrefix + "5", nameof(EsportsCommands)),
                     (EsportsSetupFlow.ChannelId + "1", nameof(EsportsSetupComponents)),
                     ("tsq:f1:setup:enable:1", "Formula1SetupComponents"),
                     ("tsq:vb:setup:preview:1", "VolleyballSetupComponents"),
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

    // ================================================================== F. out-of-scope commands

    [Fact]
    public async Task Every_command_other_than_tsq_admin_is_byte_for_byte_the_same_as_before_and_on_main()
    {
        var after = Parse((await BuildAsync()).Manifest.ToDocumentJson());
        foreach (var file in new[] { "manifest-before-tsq-admin.json", "manifest-grouped-tsq-admin.json" })
        {
            var before = Doc(file).Where(c => !c.Name.EndsWith("-admin", StringComparison.Ordinal)).ToList();
            before.Should().HaveCount(24, file);
            before.Select(c => c.Name).Should().Contain(["giveaway", "ozetle", "setup", "modules", "help", "bot", "privacy", "quote", "Quote", "esports", "f1", "volleyball",
                "birthday", "ekip", "dolar", "euro", "altın", "çevir", "saat", "zarat", "randomsayi", "sec", "yazitura", "ongoru"]);
            foreach (var command in before)
            {
                var now = after.Single(c => c.Name == command.Name);
                now.Module.Should().Be(command.Module);
                JsonNode.DeepEquals(command.Payload, now.Payload).Should().BeTrue($"{file}: {command.Name} must not change");
            }
        }

        after.Should().HaveCount(25);
    }

    // ================================================================== G. sync

    [Fact]
    public async Task Sync_from_the_grouped_command_is_one_update_and_touches_nothing_else()
    {
        var (manifest, admin) = await BuildAsync();
        var grouped = Doc("manifest-grouped-tsq-admin.json");
        var legacy = Doc("manifest-before-tsq-admin.json").Where(c => c.Name.EndsWith("-admin", StringComparison.Ordinal)).ToList();
        var remote = grouped.Concat(legacy).Select((c, i) => new RemoteCommand((ulong)(1000 + i), c.Name, GuildJson(c.Payload), (CommandKind)(int)c.Payload["type"]!)).ToList();
        var managed = remote.ToDictionary(r => r.Name, r => r.Id);

        var plan = CommandSyncPlanner.Plan(manifest, remote, managed, Request(admin, prune: false));
        plan.IsBlocked.Should().BeFalse(string.Join(" | ", plan.BlockingErrors));
        plan.Items.Where(i => i.Action == SyncAction.Update).Select(i => i.Name).Should().Equal(AdminCatalog.Name);
        plan.Items.Where(i => i.Action == SyncAction.Update).Single().RemoteId.Should().Be(managed[AdminCatalog.Name], "the same command id is updated, not re-created");
        plan.Items.Should().NotContain(i => i.Action == SyncAction.Create || i.Action == SyncAction.DeleteManaged);
        plan.Items.Count(i => i.Action == SyncAction.Unchanged).Should().Be(24);
        plan.Items.Where(i => i.Action == SyncAction.KeepManagedNotInManifest).Select(i => i.Name).Should().BeEquivalentTo(legacy.Select(c => c.Name));

        var synced = manifest.Commands.Select((c, i) => new RemoteCommand((ulong)(2000 + i), c.Name, CommandManifest.CanonicalJson(c, false), c.Type)).ToList();
        CommandSyncPlanner.Plan(manifest, synced, synced.ToDictionary(r => r.Name, r => r.Id), Request(admin, prune: false)).HasChanges.Should().BeFalse();
    }

    [Fact]
    public void A_declared_but_unregistered_admin_module_or_an_undeclared_one_is_a_blocking_problem()
    {
        var registry = new ToroSquad.Core.Modules.ModuleRegistry([new ToroSquad.Discord.CoreBotModule(), new ToroSquad.Modules.News.NewsModule()]);
        new AdminCatalog([]).Problems(registry).Should().ContainSingle().Which.Should().Contain("registered no admin operations");
        new AdminCatalog([ToroSquad.Modules.News.Commands.NewsAdminOperations.Definition, VolleyballAdminOperations.Definition]).Problems(registry)
            .Should().ContainSingle().Which.Should().Contain("'volleyball' is registered");
        var act = () => new AdminCatalog([Formula1AdminOperations.Definition, Formula1AdminOperations.Definition]);
        act.Should().Throw<InvalidOperationException>();
    }

    // ================================================================== helpers

    private static AdminFields? SharedField(string formerOption) => formerOption switch
    {
        "channel" => AdminFields.Channel,
        "member" => AdminFields.User,
        "date" => AdminFields.Date,
        "role" or "ping_role" => AdminFields.Role,
        _ => null,
    };

    private static ActorContext Administrator() => new(Guild, new UserId(1), CorePermission.Administrator, [], false, 50);

    private static void SetChannel(TestHost host) =>
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, CorePermission.ViewChannel | CorePermission.SendMessages | CorePermission.EmbedLinks));

    private static Task<ulong?> NewsChannelAsync(TestHost host) =>
        host.InScopeAsync(async sp => (await sp.GetRequiredService<NewsConfigService>().StatusAsync(TestHost.Admin(Guild), CancellationToken.None)).Status?.ChannelId);

    private static Task<UpdatesStatus?> UpdatesStatusAsync(TestHost host) =>
        host.InScopeAsync(async sp => (await sp.GetRequiredService<UpdatesConfigService>().StatusAsync(TestHost.Admin(Guild), CancellationToken.None)).Status);

    private static Task<ulong?> LfgChannelAsync(TestHost host) =>
        host.InScopeAsync(async sp => (await sp.GetRequiredService<LfgConfigService>().StatusAsync(TestHost.Admin(Guild), CancellationToken.None)).Status?.ChannelId);

    private static Task<(int Day, int Month)?> BirthdayAsync(TestHost host, ulong member) =>
        host.InScopeAsync(async sp =>
        {
            var (_, date) = await sp.GetRequiredService<BirthdayService>().GetForMemberAsync(Administrator(), new UserId(member), true, CancellationToken.None);
            return date is { } d ? ((int, int)?)(d.Day, d.Month) : null;
        });

    private static string Localize(TestHost host, string key) => host.Services.GetRequiredService<ILocalizer>().Get("tr", key);

    private static async Task<RecordingResponder> RunAsync(TestHost host, ActorContext actor, string module, string operation, AdminArgs? args = null,
        AdminFields provided = AdminFields.None)
    {
        var respond = new RecordingResponder(host.Services.GetRequiredService<ILocalizer>());
        await using var scope = host.Scope();
        await host.Services.GetRequiredService<AdminRouter>().RunAsync(new AdminRequest(actor, "tr", module, operation, args ?? AdminArgs.None, provided), respond,
            scope.ServiceProvider);
        return respond;
    }

    private static async Task<RecordingResponder> FormAsync(TestHost host, ActorContext actor, string draft, string action, AdminInput input)
    {
        var respond = new RecordingResponder(host.Services.GetRequiredService<ILocalizer>());
        await using var scope = host.Scope();
        await host.Services.GetRequiredService<AdminRouter>().FormAsync(actor, "tr", draft, action, input, respond, scope.ServiceProvider);
        return respond;
    }

    private static string DraftId(string customId) => customId[AdminCall.CustomIdPrefix.Length..].Split(':')[0];

    private static AdminInput Picked(ulong id, IChannel channel) =>
        new([id.ToString(System.Globalization.CultureInfo.InvariantCulture)], new Dictionary<string, IReadOnlyList<string>>(), [], [channel]);

    private static AdminInput Selected(string value) => new([value], new Dictionary<string, IReadOnlyList<string>>(), [], []);

    private static AdminInput Modal(params (string Field, string[] Values)[] fields) =>
        new([], fields.ToDictionary(f => f.Field, f => (IReadOnlyList<string>)f.Values), [], []);

    private static ITextChannel TextChannel(ulong id, ulong? guild = null) =>
        InterfaceFake.Create<ITextChannel>(new() { ["Id"] = id, ["GuildId"] = guild ?? Guild.Value });

    /// <summary>
    /// The modal's top-level components exactly as Discord.Net 3.20.1 serializes them in RespondWithModalAsync
    /// (<c>modal.Component.Components.Select(x =&gt; x.ToModel())</c>, its internal API models and JSON contract resolver).
    /// </summary>
    private static JsonArray DiscordPayload(Modal modal)
    {
        var rest = typeof(global::Discord.Rest.DiscordRestClient).Assembly;
        var toModel = rest.GetTypes().Single(t => t.Name == "MessageComponentExtension")
            .GetMethod("ToModel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(IMessageComponent)])!;
        var resolver = (Newtonsoft.Json.Serialization.IContractResolver)Activator.CreateInstance(rest.GetType("Discord.Net.Converters.DiscordContractResolver")!, nonPublic: true)!;
        var serializer = new Newtonsoft.Json.JsonSerializer { ContractResolver = resolver };
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        serializer.Serialize(writer, modal.Component.Components.Select(c => toModel.Invoke(null, [c])).ToArray());
        return JsonNode.Parse(writer.ToString())!.AsArray();
    }

    /// <summary>"18>3[16]": a label (18) wrapping a string select (3) with 16 options.</summary>
    private static string Shape(JsonNode label)
    {
        var inner = label["component"]!;
        // Discord.Net sends an empty "options" array for auto-populated (role/channel) selects, as for the LFG voice select.
        var options = inner["options"] is JsonArray { Count: > 0 } list ? $"[{list.Count}]" : "";
        return $"{(int)label["type"]!}>{(int)inner["type"]!}{options}";
    }

    private static List<string> CustomIds(MessageComponent components) =>
        components.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components)
            .Select(c => c switch { ButtonComponent b => b.CustomId, SelectMenuComponent m => m.CustomId, _ => null }).OfType<string>().ToList();

    private static List<SelectMenuOption> SelectOptions(object root)
    {
        var found = new List<SelectMenuOption>();
        void Walk(object? node, int depth)
        {
            if (node is null || depth > 8 || node is string)
                return;
            if (node is SelectMenuComponent select)
            {
                found.AddRange(select.Options ?? []);
                return;
            }

            if (node is IEnumerable list)
            {
                foreach (var item in list)
                    Walk(item, depth + 1);
                return;
            }

            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name is "Component" or "Components"))
                Walk(property.GetValue(node), depth + 1);
        }

        Walk(root, 0);
        return found;
    }

    private static async Task<IReadOnlyList<AutocompleteResult>> SuggestAsync(TestHost host, InteractionService service, string field, string typed, string? module,
        CorePermission permissions, bool inGuild = true)
    {
        var options = new List<AutocompleteOption>();
        if (module is not null)
            options.Add(AutocompleteOption(ApplicationCommandOptionType.String, "modul", module, field == "modul"));
        var current = AutocompleteOption(ApplicationCommandOptionType.String, field, typed, true);
        if (field != "modul" || module is null)
            options.Add(current);
        var interaction = Recorder.Create<IAutocompleteInteraction>(out var recorder, new()
        {
            ["Data"] = InterfaceFake.Create<IAutocompleteInteractionData>(new()
            {
                ["CommandName"] = AdminCatalog.Name,
                ["Options"] = (IReadOnlyCollection<AutocompleteOption>)options,
                ["Current"] = current,
            }),
            ["Type"] = InteractionType.ApplicationCommandAutocomplete,
        });
        await using var scope = host.Scope();
        var result = await service.ExecuteCommandAsync(Context(interaction, permissions, inGuild), scope.ServiceProvider);
        result.IsSuccess.Should().BeTrue(result.ErrorReason);
        return recorder.Calls.Where(c => c.Method == "RespondAsync").Select(c => ((IEnumerable<AutocompleteResult>?)c.Args[0] ?? []).ToList()).Single();
    }

    private sealed record DocCommand(string Name, string Module, JsonObject Payload);

    private static List<DocCommand> Doc(string file) =>
        Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "commands", file)));

    private static List<DocCommand> Parse(string document) =>
        JsonNode.Parse(document)!["commands"]!.AsArray()
            .Select(c => new DocCommand((string)c!["payload"]!["name"]!, (string)c["module"]!, c["payload"]!.AsObject())).ToList();

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

    private static SyncRequest Request(IReadOnlySet<string> admin, bool prune) =>
        new(new SyncScope.Guild(42), 7, 7, new HashSet<ulong> { 42 }, false, prune, admin);

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string GuildJson(JsonNode payload)
    {
        var copy = payload.DeepClone().AsObject();
        copy.Remove("contexts");
        copy.Remove("integration_types");
        return copy.ToJsonString(Compact);
    }

    private static bool ModuleChainHas(ModuleInfo module, string group)
    {
        for (var m = module; m is not null; m = m.Parent)
        {
            if (m.IsSlashGroup && m.SlashGroupName == group)
                return true;
        }

        return false;
    }

    private static IEnumerable<ManifestOption> Flatten(IEnumerable<ManifestOption> options) =>
        options.SelectMany(o => new[] { o }.Concat(Flatten(o.Options)));

    private static ISlashCommandInteraction Slash(string path, IReadOnlyDictionary<string, object>? values = null)
    {
        var parts = path.Split(' ');
        IReadOnlyCollection<IApplicationCommandInteractionDataOption> options = (values ?? new Dictionary<string, object>()).Select(kv =>
            InterfaceFake.Create<IApplicationCommandInteractionDataOption>(new()
            {
                ["Name"] = kv.Key,
                ["Value"] = kv.Value,
                ["Options"] = (IReadOnlyCollection<IApplicationCommandInteractionDataOption>)[],
                ["Type"] = kv.Value is string ? ApplicationCommandOptionType.String : ApplicationCommandOptionType.Channel,
            })).ToList();
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

    /// <summary>Records what an admin operation answered (instead of Discord).</summary>
    private sealed class RecordingResponder(ILocalizer localizer) : IAdminResponder
    {
        public List<(string? Text, MessageEmbed? Embed, MessageComponent? Components, bool Ephemeral)> Sent { get; } = [];
        public List<(string? Text, MessageComponent? Components)> Updates { get; } = [];
        public List<Modal> Modals { get; } = [];

        public string Text => string.Join("\n", Sent.Select(s => s.Text).Concat(Updates.Select(u => u.Text)).Where(t => t is not null));
        public IReadOnlyList<MessageEmbed> Embeds => Sent.Where(s => s.Embed is not null).Select(s => s.Embed!).ToList();

        public List<string> AllCustomIds() =>
            Sent.Where(s => s.Components is not null).SelectMany(s => CustomIds(s.Components!))
                .Concat(Updates.Where(u => u.Components is not null).SelectMany(u => CustomIds(u.Components!))).ToList();

        public List<string> SelectValues() =>
            Sent.Select(s => s.Components).Concat(Updates.Select(u => u.Components)).Where(c => c is not null).SelectMany(c => SelectOptions(c!)).Select(o => o.Value).ToList();

        public Task DeferAsync() => Task.CompletedTask;

        public Task<string> DescribeAsync(OperationResult result) => Task.FromResult(localizer.Get("tr", result.MessageKey, result.Args.ToArray()));

        public Task SendAsync(string? text, MessageEmbed? embed = null, MessageComponent? components = null, bool ephemeral = true)
        {
            Sent.Add((text, embed, components, ephemeral));
            return Task.CompletedTask;
        }

        public Task UpdateAsync(string? text, MessageComponent? components, MessageEmbed? embed = null)
        {
            Updates.Add((text, components));
            return Task.CompletedTask;
        }

        public Task ModalAsync(Modal modal)
        {
            Modals.Add(modal);
            return Task.CompletedTask;
        }
    }

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
