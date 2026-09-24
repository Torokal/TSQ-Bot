using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Discord.Interactions;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Tests.Support;
using ActorContext = ToroSquad.Core.Security.ActorContext;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Criterion 2 (no admin operation without permission — same service path serves slash commands, buttons, selects
/// and modals), criterion 3 (module gate) and criterion 5 (guild A cannot read/modify guild B).
/// </summary>
public sealed class AuthorizationAndIsolationTests : IAsyncLifetime
{
    private static readonly GuildId GuildA = new(1001);
    private static readonly GuildId GuildB = new(2002);

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        await _host.SetUpEsportsGuildAsync(GuildA, new ChannelId(10010), new RoleInfo(new RoleId(10011), "A-notify", 2, CorePermission.None, false, false, true));
        await _host.SetUpEsportsGuildAsync(GuildB, new ChannelId(20020), new RoleInfo(new RoleId(20021), "B-notify", 2, CorePermission.None, false, false, true));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    public static TheoryData<string> AdminOperations =>
    [
        "modules.enable", "modules.disable", "setup.update", "esports.configure", "esports.filter", "esports.vrs", "esports.clear",
        "esports.pause", "esports.resume", "roles.map", "roles.unmap", "roles.selfservice", "doctor", "preview",
    ];

    private static async Task<OperationResult> RunAdminOperationAsync(IServiceProvider sp, string op, ActorContext actor) => op switch
    {
        "modules.enable" => await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(actor, "esports", true, CancellationToken.None),
        "modules.disable" => await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(actor, "esports", false, CancellationToken.None),
        "setup.update" => await sp.GetRequiredService<GuildSettingsService>().UpdateAsync(actor, "en", null, CancellationToken.None),
        "esports.configure" => await sp.GetRequiredService<EsportsConfigService>().ConfigureAsync(actor, null, false, null, null, null, CancellationToken.None),
        "esports.filter" => await sp.GetRequiredService<EsportsConfigService>().SetFilterAsync(actor, FilterDimension.Tier, "1", null, true, CancellationToken.None),
        "esports.vrs" => await sp.GetRequiredService<EsportsConfigService>().SetVrsTopNAsync(actor, 10, CancellationToken.None),
        "esports.clear" => await sp.GetRequiredService<EsportsConfigService>().ClearFiltersAsync(actor, CancellationToken.None),
        "esports.pause" => await sp.GetRequiredService<EsportsConfigService>().PauseAsync(actor, true, CancellationToken.None),
        "esports.resume" => await sp.GetRequiredService<EsportsConfigService>().PauseAsync(actor, false, CancellationToken.None),
        "roles.map" => await sp.GetRequiredService<RoleMappingService>().MapAsync(actor, new RoleId(10011), null, true, false, CancellationToken.None),
        "roles.unmap" => await sp.GetRequiredService<RoleMappingService>().UnmapAsync(actor, 1, CancellationToken.None),
        "roles.selfservice" => await sp.GetRequiredService<RoleMappingService>().SetSelfServiceAsync(actor, 1, true, CancellationToken.None),
        "doctor" => (await sp.GetRequiredService<EsportsDoctor>().RunAsync(actor, CancellationToken.None)).Auth,
        "preview" => (await sp.GetRequiredService<EsportsPreviewService>().BuildAsync(actor, "tr", CancellationToken.None)).Auth,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    [Theory]
    [MemberData(nameof(AdminOperations))]
    public async Task Regular_members_are_refused_every_admin_operation(string op)
    {
        var result = await _host.InScopeAsync(sp => RunAdminOperationAsync(sp, op, TestHost.Member(GuildA)));
        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(OperationError.Forbidden);
    }

    [Fact]
    public void Authorization_rejects_an_actor_from_another_guild()
    {
        // Services never take a guild id from the caller; the central rule also refuses cross-guild access explicitly.
        var auth = ToroSquad.Core.Security.Authorize.Require(TestHost.Admin(GuildB), GuildA, ToroSquad.Core.Security.Authorize.ServerSettings);
        auth.Failure.Should().Be(ToroSquad.Core.Security.AuthorizationFailure.WrongGuild);
    }

    [Fact]
    public async Task Manage_roles_is_required_in_addition_to_manage_server_for_role_mappings()
    {
        var serverOnly = new ActorContext(GuildA, new UserId(5), CorePermission.ManageGuild, [], false, 50);
        var result = await _host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>().MapAsync(serverOnly, new RoleId(10011), null, true, false, CancellationToken.None));
        result.Error.Should().Be(OperationError.Forbidden);
    }

    [Fact]
    public async Task Guild_a_cannot_modify_or_read_guild_b_mappings_and_settings()
    {
        var mappingB = await _host.InScopeAsync(async sp =>
        {
            var svc = sp.GetRequiredService<RoleMappingService>();
            await svc.MapAsync(TestHost.Admin(GuildB), new RoleId(20021), null, true, false, CancellationToken.None);
            return (await svc.ListAsync(GuildB, CancellationToken.None)).Single().Id;
        });

        await _host.InScopeAsync(async sp =>
        {
            var svc = sp.GetRequiredService<RoleMappingService>();
            (await svc.UnmapAsync(TestHost.Admin(GuildA), mappingB, CancellationToken.None)).Error.Should().Be(OperationError.NotFound);
            (await svc.SetSelfServiceAsync(TestHost.Admin(GuildA), mappingB, true, CancellationToken.None)).Error.Should().Be(OperationError.NotFound);
            (await svc.PanelFollowKeyAsync(GuildA, mappingB, CancellationToken.None)).Should().BeNull();
            (await svc.ListAsync(GuildA, CancellationToken.None)).Should().BeEmpty();
            // Guild B's channel id is not a channel of guild A as far as the bot can see.
            (await sp.GetRequiredService<EsportsConfigService>().ConfigureAsync(TestHost.Admin(GuildA), 20020, null, null, null, null, CancellationToken.None))
                .MessageKey.Should().Be("esports.config.channel_invalid");
            await sp.GetRequiredService<EsportsConfigService>().PauseAsync(TestHost.Admin(GuildA), true, CancellationToken.None);
        });

        var b = await _host.InScopeAsync(sp => sp.GetRequiredService<EsportsConfigService>().GetAsync(GuildB, CancellationToken.None));
        b.Paused.Should().BeFalse();
        b.ChannelId.Should().Be(20020);
        (await _host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>().ListAsync(GuildB, CancellationToken.None))).Should().ContainSingle();
    }

    [Fact]
    public async Task Confirmations_and_settings_are_guild_scoped()
    {
        await _host.InScopeAsync(async sp =>
        {
            var confirmations = sp.GetRequiredService<IConfirmationStore>();
            var id = await confirmations.CreateAsync(GuildA, new UserId(7), "x", TimeSpan.FromMinutes(5), CancellationToken.None);
            (await confirmations.TryConsumeAsync(id, GuildB, new UserId(7), "x", CancellationToken.None)).Should().BeFalse();
            (await confirmations.TryConsumeAsync(id, GuildA, new UserId(7), "x", CancellationToken.None)).Should().BeTrue();

            await sp.GetRequiredService<GuildSettingsService>().UpdateAsync(TestHost.Admin(GuildA), "en", "UTC", CancellationToken.None);
            (await sp.GetRequiredService<IGuildSettingsStore>().GetAsync(GuildB, CancellationToken.None)).Language.Should().Be("tr");
        });
    }

    [Fact]
    public async Task Core_stays_on_when_esports_is_disabled()
    {
        await _host.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(GuildA), "esports", false, CancellationToken.None);
            var gate = sp.GetRequiredService<IModuleGate>();
            (await gate.IsEnabledAsync(GuildA, ModuleId.Core, CancellationToken.None)).Should().BeTrue();
            (await gate.IsEnabledAsync(GuildA, new ModuleId("esports"), CancellationToken.None)).Should().BeFalse();
            (await gate.IsEnabledAsync(GuildB, new ModuleId("esports"), CancellationToken.None)).Should().BeTrue("per-guild state");
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(GuildA), "core", false, CancellationToken.None))
                .MessageKey.Should().Be("modules.core_cannot_toggle");
            // Disabling keeps data.
            (await sp.GetRequiredService<ToroDbContext>().Set<EsportsGuildConfigEntity>().CountAsync(c => c.GuildId == GuildA.Value)).Should().Be(1);
        });
    }

    [Fact]
    public async Task Module_precondition_blocks_disabled_module_components_and_non_guild_context()
    {
        await _host.InScopeAsync(sp => sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(GuildA), "esports", false, CancellationToken.None));

        var gated = new ToroModuleAttribute("esports");
        var setupOnly = new ToroModuleAttribute("esports") { AllowWhenDisabled = true };
        var core = new ToroModuleAttribute("core");

        await _host.InScopeAsync(async sp =>
        {
            (await gated.CheckRequirementsAsync(Context(GuildA), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);
            (await gated.CheckRequirementsAsync(Context(GuildB), null!, sp)).IsSuccess.Should().BeTrue();
            (await setupOnly.CheckRequirementsAsync(Context(GuildA), null!, sp)).IsSuccess.Should().BeTrue();
            (await core.CheckRequirementsAsync(Context(GuildA), null!, sp)).IsSuccess.Should().BeTrue();
            (await core.CheckRequirementsAsync(Context(null), null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.GuildOnlyError, "DMs are refused server-side");
        });
    }

    [Fact]
    public void Actor_is_built_from_the_interaction_member_not_from_options()
    {
        var actor = ActorFactory.From(Context(GuildA, permissions: (ulong)CorePermission.ManageGuild, owner: 77, userId: 77))!;
        actor.GuildId.Should().Be(GuildA);
        actor.UserId.Should().Be(new UserId(77));
        actor.IsGuildOwner.Should().BeTrue();
        actor.Has(CorePermission.ManageRoles).Should().BeTrue("the guild owner can do everything");
        ActorFactory.From(Context(null)).Should().BeNull();
    }

    private static IInteractionContext Context(GuildId? guild, ulong permissions = 0, ulong owner = 1, ulong userId = 9)
    {
        var guildFake = guild is null ? null : InterfaceFake.Create<IGuild>(new() { ["Id"] = guild.Value.Value, ["OwnerId"] = owner });
        object user = guild is null
            ? InterfaceFake.Create<IUser>(new() { ["Id"] = userId })
            : InterfaceFake.Create<IGuildUser>(new()
            {
                ["Id"] = userId,
                ["GuildPermissions"] = new GuildPermissions(permissions),
                ["RoleIds"] = (IReadOnlyCollection<ulong>)Array.Empty<ulong>(),
            });
        return InterfaceFake.Create<IInteractionContext>(new() { ["Guild"] = guildFake, ["User"] = user });
    }
}
