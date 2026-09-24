using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;

namespace ToroSquad.Tests.Integration;

/// <summary>Criteria 12 (role self-service safety) and 14 (export/delete scope) on real SQLite.</summary>
public sealed class RolesAndPrivacyTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(444);
    private static readonly ChannelId Channel = new(4440);
    private static readonly RoleId SafeRole = new(4441);
    private static readonly RoleId ModRole = new(4442);
    private static readonly RoleId PrivateRole = new(4443);
    private static readonly RoleId HighRole = new(4444);
    private static readonly RoleId ManagedRole = new(4445);
    private static readonly TeamRef Alpha = new("liquipedia", "counterstrike/Alpha", "Alpha", null);
    private static readonly TeamRef Bravo = new("liquipedia", "counterstrike/Bravo", "Bravo", null);

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        await _host.SetUpEsportsGuildAsync(Guild, Channel,
            new RoleInfo(SafeRole, "Alpha fans", 3, GuildPermission.None, false, false, true),
            new RoleInfo(ModRole, "Helpers", 4, GuildPermission.ManageMessages, false, false, true),
            new RoleInfo(PrivateRole, "VIP", 5, GuildPermission.None, false, false, true),
            new RoleInfo(HighRole, "Senior", 20, GuildPermission.None, false, false, true),
            new RoleInfo(ManagedRole, "SomeBot", 6, GuildPermission.None, true, false, false));
        var snapshot = FakeGuildGatewaySnapshot();
        _host.Guilds.SetSnapshot(snapshot);
        // Make teams known (as if seen in provider data).
        await _host.InScopeAsync(sp => sp.GetRequiredService<NotificationPlanner>().PlanAsync(
            [FilterAndRankingTests.Match("SEED", Alpha, Bravo) with { ScheduledStartUtc = TestHost.T0.AddDays(3) }], TestHost.T0, false, CancellationToken.None));
    }

    private static GuildRoleSnapshot FakeGuildGatewaySnapshot()
    {
        var baseSnapshot = Discord.Guilds.FakeGuildGateway.DemoSnapshot(Guild,
            new RoleInfo(SafeRole, "Alpha fans", 3, GuildPermission.None, false, false, true),
            new RoleInfo(ModRole, "Helpers", 4, GuildPermission.ManageMessages, false, false, true),
            new RoleInfo(PrivateRole, "VIP", 5, GuildPermission.None, false, false, true),
            new RoleInfo(HighRole, "Senior", 20, GuildPermission.None, false, false, true),
            new RoleInfo(ManagedRole, "SomeBot", 6, GuildPermission.None, true, false, false));
        // VIP opens a private channel through an allow overwrite.
        return baseSnapshot with { Overwrites = [new ChannelRoleOverwrite(new ChannelId(9999), PrivateRole, GuildPermission.ViewChannel, GuildPermission.None)] };
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<long> MapAsync(RoleId role, string team, bool selfService)
    {
        return await _host.InScopeAsync(async sp =>
        {
            var svc = sp.GetRequiredService<RoleMappingService>();
            (await svc.MapAsync(TestHost.Admin(Guild), role, team, true, false, CancellationToken.None)).Succeeded.Should().BeTrue();
            var id = (await svc.ListAsync(Guild, CancellationToken.None)).Single(m => m.RoleId == role.Value && m.TeamKey == team).Id;
            if (selfService)
                (await svc.SetSelfServiceAsync(TestHost.Admin(Guild), id, true, CancellationToken.None)).Succeeded.Should().BeTrue();
            return id;
        });
    }

    [Theory]
    [InlineData(4442UL, RoleSafetyProblem.GrantsGuildPermissions)]
    [InlineData(4443UL, RoleSafetyProblem.GrantsChannelPermissions)]
    [InlineData(4444UL, RoleSafetyProblem.AboveBot)]
    public async Task Unsafe_roles_cannot_become_self_service(ulong roleId, RoleSafetyProblem expected)
    {
        var id = await MapAsync(new RoleId(roleId), "counterstrike/Alpha", selfService: false);
        var result = await _host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>()
            .SetSelfServiceAsync(new ActorContext(Guild, new UserId(1), GuildPermission.Administrator, [], true, 100), id, true, CancellationToken.None));
        result.Succeeded.Should().BeFalse();
        result.Args.Single().ToString().Should().Contain(expected.ToString());
    }

    [Fact]
    public async Task Managed_and_everyone_roles_cannot_even_be_mapped()
    {
        await _host.InScopeAsync(async sp =>
        {
            var svc = sp.GetRequiredService<RoleMappingService>();
            (await svc.MapAsync(TestHost.Admin(Guild), ManagedRole, null, true, false, CancellationToken.None)).MessageKey.Should().Be("esports.roles.managed_role");
            (await svc.MapAsync(TestHost.Admin(Guild), new RoleId(Guild.Value), null, true, false, CancellationToken.None)).MessageKey.Should().Be("esports.roles.invalid_role");
        });
    }

    [Fact]
    public async Task Approver_must_be_above_the_role_in_the_hierarchy()
    {
        var id = await MapAsync(SafeRole, "counterstrike/Alpha", selfService: false);
        var lowAdmin = new ActorContext(Guild, new UserId(9), GuildPermission.ManageGuild | GuildPermission.ManageRoles, [], false, 2);
        var result = await _host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>().SetSelfServiceAsync(lowAdmin, id, true, CancellationToken.None));
        result.MessageKey.Should().Be("error.role_hierarchy");
    }

    [Fact]
    public async Task Follow_grants_role_and_unfollow_removes_only_what_the_bot_granted()
    {
        await MapAsync(SafeRole, "counterstrike/Alpha", selfService: true);
        var member = TestHost.Member(Guild, 50);
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeTrue();

        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().UnfollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeFalse();
    }

    [Fact]
    public async Task Pre_existing_membership_is_never_removed()
    {
        await MapAsync(SafeRole, "counterstrike/Alpha", selfService: true);
        var member = TestHost.Member(Guild, 51, SafeRole);
        _host.Guilds.SetMemberRoles(Guild, member.UserId, SafeRole);
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.Operations.Should().NotContain(o => o.Op == "add" && o.User == member.UserId, "the member already had it");
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().UnfollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeTrue();
        _host.Guilds.Operations.Should().NotContain(o => o.Op == "remove" && o.User == member.UserId);
    }

    [Fact]
    public async Task Shared_role_is_kept_until_the_last_related_follow_ends()
    {
        await MapAsync(SafeRole, "counterstrike/Alpha", selfService: true);
        await MapAsync(SafeRole, "counterstrike/Bravo", selfService: true);
        var member = TestHost.Member(Guild, 52);
        await _host.InScopeAsync(async sp =>
        {
            var subs = sp.GetRequiredService<SubscriptionService>();
            await subs.FollowAsync(member, "counterstrike/Alpha", CancellationToken.None);
            await subs.FollowAsync(member, "counterstrike/Bravo", CancellationToken.None);
            await subs.UnfollowAsync(member, "counterstrike/Alpha", CancellationToken.None);
        });
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeTrue("Bravo still needs it");
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().UnfollowAsync(member, "counterstrike/Bravo", CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeFalse();
    }

    [Fact]
    public async Task Failed_role_grant_is_not_recorded_as_active_and_is_retried()
    {
        await MapAsync(SafeRole, "counterstrike/Alpha", selfService: true);
        var member = TestHost.Member(Guild, 53);
        _host.Guilds.ScriptedRoleOutcomes.Enqueue(RoleOperationOutcome.MissingPermissions);
        var outcome = await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        outcome.NoteKeys.Should().Contain("esports.roles.add_failed");
        var grant = await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<RoleGrantEntity>().AsNoTracking().SingleAsync(g => g.UserId == member.UserId.Value));
        grant.State.Should().Be(RoleGrantState.Failed);

        // Background retries never add roles blindly (member roles unknown there) ...
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().RetryPendingAsync(5, CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeFalse();
        // ... the member's next interaction (roles known) retries the grant.
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeTrue();
    }

    [Fact]
    public async Task Unfollow_after_a_failed_grant_keeps_a_role_an_admin_gave_manually()
    {
        await MapAsync(SafeRole, "counterstrike/Alpha", selfService: true);
        var member = TestHost.Member(Guild, 55);
        _host.Guilds.ScriptedRoleOutcomes.Enqueue(RoleOperationOutcome.MissingPermissions);
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.SetMemberRoles(Guild, member.UserId, SafeRole); // an admin gives the role by hand
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().UnfollowAsync(member with { RoleIds = [SafeRole] }, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeTrue();
        _host.Guilds.Operations.Should().NotContain(o => o.Op == "remove" && o.User == member.UserId);
    }

    [Fact]
    public async Task Role_approved_for_self_service_later_is_recorded_as_pre_existing_for_members_who_have_it()
    {
        var mapping = await MapAsync(SafeRole, "counterstrike/Alpha", selfService: false);
        var member = TestHost.Member(Guild, 56, SafeRole);
        _host.Guilds.SetMemberRoles(Guild, member.UserId, SafeRole);
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        await _host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>().SetSelfServiceAsync(TestHost.Admin(Guild), mapping, true, CancellationToken.None));
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().RetryPendingAsync(5, CancellationToken.None));
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().UnfollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeTrue("the member had it before; the bot never granted it");
    }

    [Fact]
    public async Task Admin_without_mention_everyone_cannot_map_a_non_mentionable_role_as_ping_target()
    {
        var snapshot = FakeGuildGatewaySnapshot();
        _host.Guilds.SetSnapshot(snapshot with
        {
            Roles = snapshot.Roles.Select(r => r.Id == SafeRole ? r with { IsMentionable = false } : r).ToList(),
        });
        var result = await _host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>()
            .MapAsync(TestHost.Admin(Guild), SafeRole, null, true, false, CancellationToken.None));
        result.MessageKey.Should().Be("esports.roles.mention_not_allowed");
        var withMention = new ActorContext(Guild, new UserId(1), GuildPermission.ManageGuild | GuildPermission.ManageRoles | GuildPermission.MentionEveryone, [], false, 50);
        (await _host.InScopeAsync(sp => sp.GetRequiredService<RoleMappingService>()
            .MapAsync(withMention, SafeRole, null, true, false, CancellationToken.None))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Role_that_became_unsafe_after_approval_is_not_granted()
    {
        await MapAsync(SafeRole, "counterstrike/Alpha", selfService: true);
        // Someone later gives the role a dangerous permission.
        var snapshot = FakeGuildGatewaySnapshot();
        _host.Guilds.SetSnapshot(snapshot with
        {
            Roles = snapshot.Roles.Select(r => r.Id == SafeRole ? r with { Permissions = GuildPermission.Administrator } : r).ToList(),
        });
        var member = TestHost.Member(Guild, 54);
        var outcome = await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(member, "counterstrike/Alpha", CancellationToken.None));
        outcome.NoteKeys.Should().Contain("esports.roles.unsafe_skipped");
        _host.Guilds.MemberHasRole(Guild, member.UserId, SafeRole).Should().BeFalse();
    }

    [Fact]
    public async Task Export_contains_only_the_callers_rows_in_this_guild()
    {
        var other = new GuildId(555);
        var me = TestHost.Member(Guild, 60);
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Set<TeamFollowEntity>().AddRange(
                new TeamFollowEntity { GuildId = Guild.Value, UserId = 60, TeamKey = "counterstrike/Alpha", TeamName = "Alpha" },
                new TeamFollowEntity { GuildId = Guild.Value, UserId = 61, TeamKey = "counterstrike/Alpha", TeamName = "Alpha" },
                new TeamFollowEntity { GuildId = other.Value, UserId = 60, TeamKey = "counterstrike/Bravo", TeamName = "Bravo" });
            await db.SaveChangesAsync();
        });

        var json = await _host.InScopeAsync(sp => sp.GetRequiredService<PrivacyService>().ExportAsync(me, CancellationToken.None));
        using var doc = JsonDocument.Parse(json);
        var follows = doc.RootElement.GetProperty("modules").GetProperty("esports").GetProperty("teamFollows");
        follows.GetArrayLength().Should().Be(1);
        follows[0].GetProperty("teamKey").GetString().Should().Be("counterstrike/Alpha");
        doc.RootElement.GetProperty("userId").GetString().Should().Be("60");
        json.Should().NotContain("\"61\"").And.NotContain("Bravo");
    }

    [Fact]
    public async Task Delete_requires_a_bound_confirmation_and_keeps_pre_existing_roles()
    {
        await MapAsync(SafeRole, "counterstrike/Alpha", selfService: true);
        var me = TestHost.Member(Guild, 70);
        var granted = TestHost.Member(Guild, 70);
        await _host.InScopeAsync(sp => sp.GetRequiredService<SubscriptionService>().FollowAsync(granted, "counterstrike/Alpha", CancellationToken.None));
        _host.Guilds.MemberHasRole(Guild, me.UserId, SafeRole).Should().BeTrue();

        var (items, confirmationId) = await _host.InScopeAsync(sp => sp.GetRequiredService<PrivacyService>().PreviewDeleteAsync(me, CancellationToken.None));
        items.Should().Contain(i => i.LabelKey == "privacy.item.follows" && i.Count == 1);

        // Another user, or the same user in another guild, cannot use this confirmation.
        (await _host.InScopeAsync(sp => sp.GetRequiredService<PrivacyService>().ConfirmDeleteAsync(TestHost.Member(Guild, 71), confirmationId, CancellationToken.None)))
            .Result.Succeeded.Should().BeFalse();
        (await _host.InScopeAsync(sp => sp.GetRequiredService<PrivacyService>().ConfirmDeleteAsync(TestHost.Member(new GuildId(999), 70), confirmationId, CancellationToken.None)))
            .Result.Succeeded.Should().BeFalse();

        var (result, _) = await _host.InScopeAsync(sp => sp.GetRequiredService<PrivacyService>().ConfirmDeleteAsync(me, confirmationId, CancellationToken.None));
        result.Succeeded.Should().BeTrue();
        _host.Guilds.MemberHasRole(Guild, me.UserId, SafeRole).Should().BeFalse("the bot-granted role is revoked");
        (await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<TeamFollowEntity>().CountAsync(f => f.UserId == 70))).Should().Be(0);

        // Single use.
        (await _host.InScopeAsync(sp => sp.GetRequiredService<PrivacyService>().ConfirmDeleteAsync(me, confirmationId, CancellationToken.None))).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_confirmation_expires()
    {
        var me = TestHost.Member(Guild, 72);
        var (_, confirmationId) = await _host.InScopeAsync(sp => sp.GetRequiredService<PrivacyService>().PreviewDeleteAsync(me, CancellationToken.None));
        _host.Clock.Advance(TimeSpan.FromMinutes(6));
        (await _host.InScopeAsync(sp => sp.GetRequiredService<PrivacyService>().ConfirmDeleteAsync(me, confirmationId, CancellationToken.None))).Result.MessageKey
            .Should().Be("privacy.confirmation_invalid");
    }

    [Fact]
    public async Task Departed_guild_data_is_purged_after_retention()
    {
        await MapAsync(SafeRole, "counterstrike/Alpha", selfService: false);
        await _host.InScopeAsync(sp => sp.GetRequiredService<IGuildPresenceTracker>().MarkLeftAsync(Guild, CancellationToken.None));
        var retention = new ToroSquad.Infrastructure.Hosting.RetentionService(
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new ToroSquad.Infrastructure.Hosting.BotOptions { GuildDataRetentionDays = 30 }),
            _host.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ToroSquad.Infrastructure.Hosting.RetentionService>.Instance);

        (await retention.RunOnceAsync(CancellationToken.None)).Should().Be(0, "retention period not over");
        _host.Clock.Advance(TimeSpan.FromDays(31));
        (await retention.RunOnceAsync(CancellationToken.None)).Should().Be(1);
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            (await db.Set<RoleMappingEntity>().CountAsync(m => m.GuildId == Guild.Value)).Should().Be(0);
            (await db.Set<EsportsGuildConfigEntity>().CountAsync(c => c.GuildId == Guild.Value)).Should().Be(0);
            (await db.GuildModuleStates.CountAsync(s => s.GuildId == Guild.Value)).Should().Be(0);
        });
    }
}
