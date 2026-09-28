using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Application;
using ToroSquad.Modules.Birthday.Domain;
using ToroSquad.Modules.Birthday.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Birthday against the real SQLite database and the production service wiring: registration, the Istanbul day
/// boundary, the single daily announcement, the temporary role, restarts and downtime, duplicate passes, members who left,
/// role hierarchy problems, 29 February, privacy and guild isolation.
/// </summary>
public sealed class BirthdayTests
{
    private static readonly GuildId Guild = new(900);
    private static readonly GuildId OtherGuild = new(901);
    private static readonly ChannelId Channel = new(9001);
    private static readonly ChannelId OtherChannel = new(9002);
    private static readonly RoleId Role = new(BirthdayOptions.DefaultRoleId);
    private const ulong Alice = 11;
    private const ulong Bob = 22;
    private const ulong Cem = 33;
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>14 March 2027 00:00 in Istanbul (UTC+3) — still 13 March in UTC.</summary>
    private static readonly DateTimeOffset March14 = new(2027, 3, 13, 21, 0, 0, TimeSpan.Zero);

    private static async Task<TestHost> HostAsync(DateTimeOffset start, Dictionary<string, string?>? overrides = null, Action<IServiceCollection>? replace = null)
    {
        var host = await TestHost.CreateAsync(overrides, start, replace);
        await SetUpGuildAsync(host, Guild, Channel);
        return host;
    }

    /// <summary>The bot's highest role is at position 10; the birthday role sits below it (or above it: a hierarchy problem).</summary>
    private static async Task SetUpGuildAsync(TestHost host, GuildId guild, ChannelId channel, bool roleAboveBot = false, bool enable = true)
    {
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(guild,
            new RoleInfo(Role, "Doğum Günü Bireyi", roleAboveBot ? 12 : 5, GuildPermission.None, false, false, false)));
        host.Guilds.SetChannel(guild, channel, new BotChannelAccess(true, true, GuildPermission.ViewChannel | GuildPermission.SendMessages));
        await host.InScopeAsync(async sp =>
        {
            (await sp.GetRequiredService<BirthdayConfigService>().SetChannelAsync(TestHost.Admin(guild), channel.Value, Ct)).Succeeded.Should().BeTrue();
            if (enable)
                (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(guild), "birthday", true, Ct)).Succeeded.Should().BeTrue();
        });
    }

    private static ActorContext User(ulong id, GuildId? guild = null) => TestHost.Member(guild ?? Guild, id);

    private static Task<OperationResult> SetAsync(TestHost host, ulong user, string date, GuildId? guild = null) =>
        host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().SetAsync(User(user, guild), date, Ct));

    /// <summary>Registers the birthday and makes the user a guild member (optionally with roles).</summary>
    private static async Task RegisterAsync(TestHost host, ulong user, string date = "14.03", GuildId? guild = null, params RoleId[] roles)
    {
        (await SetAsync(host, user, date, guild)).Succeeded.Should().BeTrue();
        host.Guilds.SetMemberRoles(guild ?? Guild, new UserId(user), roles);
    }

    private static Task<BirthdayPassSummary> PassAsync(TestHost host, string reason = "periodic") =>
        host.Services.GetRequiredService<BirthdayReconciler>().RunAsync(reason, Ct);

    /// <summary>A reconciler with fresh in-memory state, as after a process restart (same database, same Discord).</summary>
    private static BirthdayReconciler FreshReconciler(TestHost host) => ActivatorUtilities.CreateInstance<BirthdayReconciler>(host.Services);

    private static async Task DeliverAsync(TestHost host)
    {
        var processor = host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    private static Task<List<BirthdayCelebrationEntity>> CelebrationsAsync(TestHost host, GuildId? guild = null) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<BirthdayCelebrationEntity>().AsNoTracking()
            .Where(c => c.GuildId == (guild ?? Guild).Value).OrderBy(c => c.Id).ToListAsync(Ct));

    private static Task<List<BirthdayAnnouncementEntity>> AnnouncementsAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<BirthdayAnnouncementEntity>().AsNoTracking().OrderBy(a => a.Id).ToListAsync(Ct));

    private static Task<List<OutboxMessageEntity>> OutboxAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "birthday").ToListAsync(Ct));

    private static bool HasRole(TestHost host, ulong user, GuildId? guild = null) => host.Guilds.MemberHasRole(guild ?? Guild, new UserId(user), Role);

    private static int RoleCalls(TestHost host, string op) => host.Guilds.Operations.Count(o => o.Op == op && o.Role == Role);

    // ---------- registration (/birthday set | show | remove) ----------

    [Fact]
    public async Task Set_show_update_and_remove_touch_only_the_callers_own_birthday()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        var service = (Func<IServiceProvider, BirthdayService>)(sp => sp.GetRequiredService<BirthdayService>());

        (await SetAsync(host, Alice, "14.03")).MessageKey.Should().Be("birthday.set.saved");
        (await SetAsync(host, Alice, "14/03")).MessageKey.Should().Be("birthday.set.unchanged");
        var updated = await SetAsync(host, Alice, "15-04");
        updated.MessageKey.Should().Be("birthday.set.updated");
        updated.Args.Should().Equal(15, 4);
        (await SetAsync(host, Alice, "31.02")).Error.Should().Be(OperationError.InvalidInput);

        (await host.InScopeAsync(sp => service(sp).GetAsync(User(Alice), Ct))).Should().Be(BirthdayDate.Create(15, 4), "the invalid date changed nothing");
        (await host.InScopeAsync(sp => service(sp).GetAsync(User(Bob), Ct))).Should().BeNull("show is only ever the caller's own");
        (await host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<BirthdayRegistrationEntity>().CountAsync(Ct))).Should().Be(1, "updated in place");

        (await host.InScopeAsync(sp => service(sp).RemoveAsync(User(Bob), Ct))).MessageKey.Should().Be("birthday.remove.none", "Bob cannot remove Alice's");
        (await host.InScopeAsync(sp => service(sp).RemoveAsync(User(Alice), Ct))).MessageKey.Should().Be("birthday.remove.done");
        (await host.InScopeAsync(sp => service(sp).GetAsync(User(Alice), Ct))).Should().BeNull();
        (await host.InScopeAsync(sp => service(sp).RemoveAsync(User(Alice), Ct))).MessageKey.Should().Be("birthday.remove.none");
    }

    [Fact]
    public async Task February_29_can_be_saved()
    {
        await using var host = await HostAsync(March14);
        (await SetAsync(host, Alice, "29.02")).Succeeded.Should().BeTrue();
    }

    // ---------- the day ----------

    [Fact]
    public async Task Nothing_happens_at_23_59_and_the_birthday_starts_right_after_midnight_in_Istanbul()
    {
        await using var host = await HostAsync(March14.AddMinutes(-1));
        await RegisterAsync(host, Alice);

        await PassAsync(host);
        (await CelebrationsAsync(host)).Should().BeEmpty("23:59 on 13 March in Istanbul — although it is 20:59 UTC");
        RoleCalls(host, "add").Should().Be(0);

        host.Clock.Advance(TimeSpan.FromSeconds(62));
        await PassAsync(host);
        (await CelebrationsAsync(host)).Should().ContainSingle(c => c.LocalDate == new DateOnly(2027, 3, 14) && c.Year == 2027);
    }

    [Fact]
    public async Task Birthday_gets_one_announcement_and_the_role_for_the_day_which_is_removed_when_the_next_day_starts()
    {
        await using var host = await HostAsync(March14.AddSeconds(30));
        await RegisterAsync(host, Alice);

        var summary = await PassAsync(host);
        summary.Detected.Should().Be(1);
        summary.AnnouncementsQueued.Should().Be(1);
        summary.RolesAssigned.Should().Be(1);
        HasRole(host, Alice).Should().BeTrue();

        await DeliverAsync(host);
        var sent = host.Transport.Messages.Should().ContainSingle().Subject;
        sent.Channel.Should().Be(Channel);
        sent.Message.Content.Should().Be("🎂 Bugün <@11> doğum gününü kutluyor!\nİyi ki doğdun! 🥳");
        sent.Message.Mentions.Users.Should().Equal(new UserId(Alice));
        sent.Message.Mentions.Everyone.Should().BeFalse();
        sent.Message.Mentions.Roles.Should().BeEmpty();
        sent.Pinged.Should().BeTrue("the birthday member gets a real notification");

        await PassAsync(host);
        (await AnnouncementsAsync(host)).Should().ContainSingle().Which.State.Should().Be(BirthdayAnnouncementState.Sent);

        // Still the birthday at 23:59 local: the role stays.
        host.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        await PassAsync(host);
        HasRole(host, Alice).Should().BeTrue();
        RoleCalls(host, "remove").Should().Be(0);

        // 15 March 00:00 Istanbul: gone.
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var next = await PassAsync(host);
        next.RolesRemoved.Should().Be(1);
        HasRole(host, Alice).Should().BeFalse();
        var celebration = (await CelebrationsAsync(host)).Single();
        celebration.RoleState.Should().Be(BirthdayRoleState.Removed);
        celebration.RoleRemovedAt.Should().NotBeNull();

        await PassAsync(host);
        await DeliverAsync(host);
        RoleCalls(host, "add").Should().Be(1, "never re-added");
        RoleCalls(host, "remove").Should().Be(1, "removed once; a second pass does nothing");
        host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Bot_started_at_08_00_still_celebrates_today()
    {
        await using var host = await HostAsync(March14.AddHours(8));
        await RegisterAsync(host, Alice);

        (await PassAsync(host, "startup")).Detected.Should().Be(1);
        HasRole(host, Alice).Should().BeTrue();
        await DeliverAsync(host);
        host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Several_birthdays_on_the_same_day_share_one_message()
    {
        await using var host = await HostAsync(March14.AddSeconds(5));
        await RegisterAsync(host, Alice);
        await RegisterAsync(host, Bob);
        await RegisterAsync(host, Cem);
        await RegisterAsync(host, 44, "15.03");

        await PassAsync(host);
        await DeliverAsync(host);
        host.Transport.Messages.Should().ContainSingle().Which.Message.Content.Should()
            .Be("🎂 Bugün <@11>, <@22> ve <@33> doğum günlerini kutluyor!\nİyi ki doğdunuz! 🥳");
        new[] { Alice, Bob, Cem }.Should().OnlyContain(u => HasRole(host, u, null));
        HasRole(host, 44).Should().BeFalse("tomorrow");
    }

    [Fact]
    public async Task February_29_is_celebrated_only_on_a_real_February_29()
    {
        // 28 Feb and 1 Mar 2027 (not a leap year), then 29 Feb 2028.
        await using var host = await HostAsync(new DateTimeOffset(2027, 2, 27, 21, 0, 30, TimeSpan.Zero));
        await RegisterAsync(host, Alice, "29.02");

        await PassAsync(host);
        host.Clock.Advance(TimeSpan.FromDays(1));
        await PassAsync(host);
        (await CelebrationsAsync(host)).Should().BeEmpty("never moved to 28 February or 1 March");
        RoleCalls(host, "add").Should().Be(0);

        host.Clock.SetUtcNow(new DateTimeOffset(2028, 2, 28, 21, 0, 30, TimeSpan.Zero));
        await PassAsync(host);
        (await CelebrationsAsync(host)).Should().ContainSingle(c => c.LocalDate == new DateOnly(2028, 2, 29));
        HasRole(host, Alice).Should().BeTrue();
    }

    // ---------- restarts, downtime, duplicates ----------

    [Fact]
    public async Task Duplicate_and_concurrent_passes_never_duplicate_the_announcement()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        await RegisterAsync(host, Bob);

        // Two schedulers at once (e.g. an overlapping deploy), then again, then after a "restart".
        await Task.WhenAll(FreshReconciler(host).RunAsync("periodic", Ct), FreshReconciler(host).RunAsync("periodic", Ct), PassAsync(host));
        await PassAsync(host);
        await FreshReconciler(host).RunAsync("startup", Ct);
        await DeliverAsync(host);
        await FreshReconciler(host).RunAsync("startup", Ct);
        await DeliverAsync(host);

        (await AnnouncementsAsync(host)).Should().ContainSingle();
        (await OutboxAsync(host)).Should().ContainSingle();
        var only = host.Transport.Messages.Should().ContainSingle().Subject;
        only.Message.Content.Should().Contain("<@11>").And.Contain("<@22>");
        only.Message.Mentions.Users.Should().BeEquivalentTo([new UserId(Alice), new UserId(Bob)], "each celebrant is pinged once, by one message");
        (await CelebrationsAsync(host)).Should().HaveCount(2, "unique guild + user + year").And.OnlyContain(c => c.RoleState == BirthdayRoleState.Active);
        HasRole(host, Alice).Should().BeTrue();
        HasRole(host, Bob).Should().BeTrue();
    }

    [Fact]
    public async Task Restart_during_the_birthday_restores_the_role_and_sends_nothing_twice()
    {
        await using var first = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(first, Alice);
        await RegisterAsync(first, Bob);
        // Alice's grant is interrupted (the process dies / Discord times out); Bob's goes through.
        first.Guilds.ScriptedRoleOutcomes.Enqueue(RoleOperationOutcome.Transient);
        await PassAsync(first);
        await DeliverAsync(first);
        HasRole(first, Alice).Should().BeFalse();
        HasRole(first, Bob).Should().BeTrue();
        first.Transport.Messages.Should().ContainSingle();

        // A new process on the same database and the same Discord, hours later on the same day.
        var guilds = first.Guilds;
        var transport = first.Transport;
        await using var second = await TestHost.CreateAsync(new() { ["Bot:DataDirectory"] = first.Directory }, March14.AddHours(9), s =>
        {
            s.AddSingleton(guilds);
            s.AddSingleton(transport);
            s.AddSingleton<IMessageTransport>(transport);
        });
        await PassAsync(second, "startup");
        await DeliverAsync(second);

        HasRole(second, Alice).Should().BeTrue("the interrupted grant is finished after the restart");
        HasRole(second, Bob).Should().BeTrue();
        transport.Messages.Should().ContainSingle("the day's announcement is never sent again");
        RoleCalls(second, "add").Should().Be(3, "Alice twice (interrupted + after restart), Bob once");

        // …and after the restart the next day still cleans up both.
        second.Clock.Advance(TimeSpan.FromHours(16));
        await PassAsync(second);
        HasRole(second, Alice).Should().BeFalse();
        HasRole(second, Bob).Should().BeFalse();
    }

    [Fact]
    public async Task Earlier_grant_that_reached_Discord_despite_an_error_is_recognised_not_repeated()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        host.Guilds.ScriptedRoleOutcomes.Enqueue(RoleOperationOutcome.Transient);
        await PassAsync(host);
        // Discord did apply it (timeout after the change): the member now has the role.
        host.Guilds.SetMemberRoles(Guild, new UserId(Alice), Role);

        await PassAsync(host);
        RoleCalls(host, "add").Should().Be(1, "the member's roles are checked before trying again");
        (await CelebrationsAsync(host)).Single().RoleState.Should().Be(BirthdayRoleState.Active, "it is ours: removed tomorrow");

        host.Clock.Advance(TimeSpan.FromDays(1));
        await PassAsync(host);
        HasRole(host, Alice).Should().BeFalse();
    }

    [Fact]
    public async Task Role_left_over_from_an_earlier_day_is_removed_after_downtime_and_old_days_are_not_announced()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        await PassAsync(host);
        HasRole(host, Alice).Should().BeTrue();

        // The bot is down from 00:01 on the birthday until three days later; the announcement never got out.
        host.Clock.Advance(TimeSpan.FromDays(3));
        var summary = await FreshReconciler(host).RunAsync("startup", Ct);
        await DeliverAsync(host);

        summary.RolesRemoved.Should().Be(1);
        HasRole(host, Alice).Should().BeFalse();
        host.Transport.Messages.Should().BeEmpty("a missed day is not announced later (the outbox row expired at the end of that day)");
        (await OutboxAsync(host)).Single().Status.Should().Be(OutboxStatus.Expired);
        await PassAsync(host);
        (await AnnouncementsAsync(host)).Single().State.Should().Be(BirthdayAnnouncementState.Failed);
    }

    [Fact]
    public async Task Role_the_member_already_had_is_never_taken_away()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice, "14.03", null, Role); // an admin gave it by hand earlier

        await PassAsync(host);
        await DeliverAsync(host);
        host.Transport.Messages.Should().ContainSingle("still celebrated");
        (await CelebrationsAsync(host)).Single().RoleState.Should().Be(BirthdayRoleState.NotManaged);
        RoleCalls(host, "add").Should().Be(0, "already there");

        host.Clock.Advance(TimeSpan.FromDays(1));
        await PassAsync(host);
        HasRole(host, Alice).Should().BeTrue("not given by the bot, so not removed by it");
        RoleCalls(host, "remove").Should().Be(0);
    }

    // ---------- members and failures ----------

    [Fact]
    public async Task Member_who_left_is_skipped_quietly_and_the_registration_is_kept()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        await RegisterAsync(host, Bob);
        host.Guilds.RemoveMember(Guild, new UserId(Bob));

        var summary = await PassAsync(host);
        summary.MissingMembers.Should().Be(1);
        summary.Errors.Should().Be(0);
        var lookups = host.Guilds.MemberLookups;
        await PassAsync(host);
        host.Guilds.MemberLookups.Should().Be(lookups, "asked once a day, not every pass");

        await DeliverAsync(host);
        host.Transport.Messages.Should().ContainSingle().Which.Message.Content.Should().Contain("<@11>").And.NotContain("<@22>");
        RoleCalls(host, "add").Should().Be(1);
        (await CelebrationsAsync(host)).Should().ContainSingle(c => c.UserId == Alice);
        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().GetAsync(User(Bob), Ct))).Should().NotBeNull("never deleted automatically");
    }

    [Fact]
    public async Task One_members_role_failure_does_not_hold_back_the_others()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        await RegisterAsync(host, Bob);
        await RegisterAsync(host, Cem);
        host.Guilds.RemoveMember(Guild, new UserId(Bob)); // left the server
        host.Guilds.ScriptedRoleOutcomes.Enqueue(RoleOperationOutcome.Success); // Alice
        host.Guilds.ScriptedRoleOutcomes.Enqueue(RoleOperationOutcome.MissingPermissions); // Cem: Discord refuses (50013)

        var summary = await PassAsync(host);
        summary.RolesAssigned.Should().Be(1);
        summary.RoleFailures.Should().Be(1);
        HasRole(host, Alice).Should().BeTrue();
        HasRole(host, Cem).Should().BeFalse();
        var cem = (await CelebrationsAsync(host)).Single(c => c.UserId == Cem);
        cem.RoleState.Should().Be(BirthdayRoleState.Pending, "retried on the next pass");
        cem.RoleError.Should().Be("MissingPermissions");

        await DeliverAsync(host);
        host.Transport.Messages.Should().ContainSingle().Which.Message.Content.Should().Be("🎂 Bugün <@11> ve <@33> doğum günlerini kutluyor!\nİyi ki doğdunuz! 🥳");

        await PassAsync(host);
        HasRole(host, Cem).Should().BeTrue("the retry succeeds");
    }

    [Fact]
    public async Task Role_hierarchy_failure_does_not_block_the_announcement_and_doctor_reports_it_failed()
    {
        await using var host = await TestHost.CreateAsync(start: March14.AddMinutes(1));
        await SetUpGuildAsync(host, Guild, Channel, roleAboveBot: true);
        await RegisterAsync(host, Alice);

        var summary = await PassAsync(host);
        summary.Errors.Should().Be(0, "no crash");
        RoleCalls(host, "add").Should().Be(0, "no Discord call that is bound to fail");
        var celebration = (await CelebrationsAsync(host)).Single();
        celebration.RoleState.Should().Be(BirthdayRoleState.Pending);
        celebration.RoleError.Should().Contain("AboveBot");

        await DeliverAsync(host);
        host.Transport.Messages.Should().ContainSingle("the announcement still goes out");

        var (_, checks) = await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayDoctor>().RunAsync(TestHost.Admin(Guild), Ct));
        checks.Should().ContainSingle(c => c.LabelKey == "birthday.doctor.hierarchy").Which.State.Should().Be(BirthdayCheckState.Problem);
        checks.Single(c => c.LabelKey == "birthday.doctor.hierarchy").DetailKey.Should().Be("birthday.doctor.hierarchy_failed");

        // Fixed by an admin later that day: the role is given on the next pass.
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild, new RoleInfo(Role, "Doğum Günü Bireyi", 5, GuildPermission.None, false, false, false)));
        await PassAsync(host);
        HasRole(host, Alice).Should().BeTrue();
    }

    [Fact]
    public async Task A_role_that_grants_permissions_is_never_handed_out()
    {
        await using var host = await TestHost.CreateAsync(start: March14.AddMinutes(1));
        await SetUpGuildAsync(host, Guild, Channel);
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild, new RoleInfo(Role, "Doğum Günü Bireyi", 5, GuildPermission.ManageMessages, false, false, false)));
        await RegisterAsync(host, Alice);

        await PassAsync(host);
        RoleCalls(host, "add").Should().Be(0, "anyone can register a birthday: the role must not be a way to gain rights");
        var (_, checks) = await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayDoctor>().RunAsync(TestHost.Admin(Guild), Ct));
        checks.Should().Contain(c => c.LabelKey == "birthday.doctor.role_permissions" && c.State == BirthdayCheckState.Problem);
    }

    [Fact]
    public async Task Late_registration_on_the_day_gets_the_role_but_no_second_message_and_changing_the_date_never_celebrates_twice_a_year()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        await PassAsync(host);
        await DeliverAsync(host);

        host.Clock.Advance(TimeSpan.FromHours(10));
        await RegisterAsync(host, Bob);
        await PassAsync(host);
        await DeliverAsync(host);
        HasRole(host, Bob).Should().BeTrue();
        host.Transport.Messages.Should().ContainSingle("one announcement per guild and day");

        // Alice moves her birthday to tomorrow: already celebrated this year, so nothing tomorrow.
        (await SetAsync(host, Alice, "15.03")).MessageKey.Should().Be("birthday.set.updated");
        host.Clock.Advance(TimeSpan.FromHours(14));
        await PassAsync(host);
        (await CelebrationsAsync(host)).Should().HaveCount(2);
        HasRole(host, Alice).Should().BeFalse("the role of 14 March is gone and 15 March is not celebrated again");
    }

    [Fact]
    public async Task Removing_the_registration_on_the_day_takes_back_todays_role()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        await PassAsync(host);
        HasRole(host, Alice).Should().BeTrue();

        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().RemoveAsync(User(Alice), Ct))).MessageKey.Should().Be("birthday.remove.done");
        await PassAsync(host);
        HasRole(host, Alice).Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_is_announced_or_granted_while_the_module_is_off_or_without_a_channel()
    {
        await using var host = await TestHost.CreateAsync(start: March14.AddMinutes(1));
        await SetUpGuildAsync(host, Guild, Channel, enable: false);
        await RegisterAsync(host, Alice);

        await PassAsync(host);
        await DeliverAsync(host);
        (await CelebrationsAsync(host)).Should().BeEmpty();
        RoleCalls(host, "add").Should().Be(0);
        host.Transport.Messages.Should().BeEmpty();

        // Enabled later that day, but the channel is not set in this guild: role yes, announcement no.
        await SetUpGuildAsync(host, OtherGuild, OtherChannel, enable: true);
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            await db.Set<BirthdayGuildConfigEntity>().Where(c => c.GuildId == OtherGuild.Value).ExecuteUpdateAsync(s => s.SetProperty(c => c.ChannelId, (ulong?)null), Ct);
        });
        await RegisterAsync(host, Bob, "14.03", OtherGuild);
        await PassAsync(host);
        await DeliverAsync(host);
        HasRole(host, Bob, OtherGuild).Should().BeTrue();
        host.Transport.Messages.Should().BeEmpty("the channel is never guessed");
    }

    [Fact]
    public async Task Dry_run_logs_the_announcement_instead_of_sending_it()
    {
        await using var host = await HostAsync(March14.AddMinutes(1), new() { ["Delivery:Mode"] = "DryRun" });
        await RegisterAsync(host, Alice);
        await PassAsync(host);
        await DeliverAsync(host);
        host.Transport.Messages.Should().BeEmpty();
        await PassAsync(host);
        (await AnnouncementsAsync(host)).Single().State.Should().Be(BirthdayAnnouncementState.Simulated);
    }

    [Fact]
    public async Task Multi_member_announcement_pings_only_that_days_celebrants()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        await RegisterAsync(host, Bob);
        await RegisterAsync(host, Cem);
        await RegisterAsync(host, 44, "15.03"); // tomorrow
        host.Guilds.RemoveMember(Guild, new UserId(Bob)); // left: neither named nor pinged

        await PassAsync(host);
        await DeliverAsync(host);
        var message = host.Transport.Messages.Should().ContainSingle().Subject.Message;
        message.Content.Should().Be("🎂 Bugün <@11> ve <@33> doğum günlerini kutluyor!\nİyi ki doğdunuz! 🥳");
        message.Mentions.Users.Should().Equal(new UserId(Alice), new UserId(Cem));
        message.Mentions.Everyone.Should().BeFalse();
        message.Mentions.Roles.Should().BeEmpty();
        var wire = ToroSquad.Discord.Transport.DiscordConversions.ToAllowedMentions(message.Mentions);
        wire.AllowedTypes.Should().Be(global::Discord.AllowedMentionTypes.None);
        wire.RoleIds.Should().BeNullOrEmpty();
        wire.UserIds.Should().Equal(Alice, Cem);
    }

    // ---------- /birthday-admin set ----------

    private static ActorContext Administrator(ulong user = 1) =>
        new(Guild, new UserId(user), GuildPermission.Administrator, [], false, 50);

    private static ActorContext Owner(ulong user = 2) =>
        new(Guild, new UserId(user), GuildPermission.ViewChannel | GuildPermission.SendMessages, [], IsGuildOwner: true, 1);

    private static Task<OperationResult> AdminSetAsync(TestHost host, ActorContext actor, ulong member, string date, bool eligible = true) =>
        host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().SetForMemberAsync(actor, new UserId(member), eligible, date, Ct));

    private static Task<List<BirthdayRegistrationEntity>> RegistrationsAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<BirthdayRegistrationEntity>().AsNoTracking().OrderBy(r => r.Id).ToListAsync(Ct));

    [Fact]
    public async Task Administrator_creates_and_updates_another_members_birthday_in_the_same_registration()
    {
        await using var host = await HostAsync(March14.AddDays(-30));

        var created = await AdminSetAsync(host, Administrator(), Alice, "14.03");
        created.MessageKey.Should().Be("birthday.admin_set.saved");
        created.Args.Should().Equal(14, 3);
        var updated = await AdminSetAsync(host, Administrator(), Alice, "15/04");
        updated.MessageKey.Should().Be("birthday.admin_set.updated");
        updated.Args.Should().Equal(15, 4);
        (await AdminSetAsync(host, Administrator(), Alice, "15-04")).MessageKey.Should().Be("birthday.admin_set.unchanged");

        // The member's own /birthday set and the admin's write the same unique (guild, user) row.
        (await SetAsync(host, Alice, "16.05")).MessageKey.Should().Be("birthday.set.updated");
        (await AdminSetAsync(host, Administrator(), Alice, "17.06")).MessageKey.Should().Be("birthday.admin_set.updated");
        var rows = await RegistrationsAsync(host);
        rows.Should().ContainSingle();
        rows[0].UserId.Should().Be(Alice);
        (rows[0].Day, rows[0].Month).Should().Be((17, 6));
        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().GetAsync(User(1), Ct))).Should().BeNull("the admin's own birthday is untouched");
    }

    [Fact]
    public async Task Guild_owner_may_set_a_members_birthday_without_the_administrator_bit()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        (await AdminSetAsync(host, Owner(), Alice, "14.03")).MessageKey.Should().Be("birthday.admin_set.saved");
        (await RegistrationsAsync(host)).Should().ContainSingle(r => r.UserId == Alice);
    }

    [Fact]
    public async Task Manage_server_without_administrator_and_normal_members_cannot_set_someone_elses_birthday()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        var manageServer = TestHost.Admin(Guild); // Manage Server + Manage Roles, no Administrator
        var everythingElse = new ActorContext(Guild, new UserId(3), (GuildPermission)ulong.MaxValue & ~GuildPermission.Administrator, [], false, 99);
        foreach (var actor in new[] { manageServer, User(Bob), everythingElse })
        {
            var result = await AdminSetAsync(host, actor, Alice, "14.03");
            result.Succeeded.Should().BeFalse();
            result.Error.Should().Be(OperationError.Forbidden);
            result.MessageKey.Should().Be("birthday.admin_set.forbidden");
            (await AdminSetAsync(host, actor, Alice, "31.02")).MessageKey.Should().Be("birthday.admin_set.forbidden", "authorization comes before anything else");
        }

        (await RegistrationsAsync(host)).Should().BeEmpty();
        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().SetAsync(User(Bob), "14.03", Ct))).Succeeded.Should().BeTrue();
        (await RegistrationsAsync(host)).Should().ContainSingle().Which.UserId.Should().Be(Bob, "/birthday set only ever writes the caller's own row");
        var catalog = ToroSquad.Tests.Unit.BirthdayDateTests.Localizer();
        catalog.Get("tr", "birthday.admin_set.forbidden").Should().Be("❌ Bu işlem için Yönetici (Administrator) yetkisine sahip olmalısınız.");
        catalog.Get("tr", "birthday.admin_set.saved", "<@11>", "14 Mart").Should().Be("🎂 <@11> kullanıcısının doğum günü 14 Mart olarak kaydedildi.");
        catalog.Get("tr", "birthday.admin_set.updated", "<@11>", "14 Mart").Should().Be("🎂 <@11> kullanıcısının doğum günü 14 Mart olarak güncellendi.");
    }

    [Fact]
    public async Task Admin_set_uses_the_same_date_rules_and_refuses_non_members()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        foreach (var bad in new[] { "31.02", "00.05", "32.01", "13.13", "14.03.1990" })
            (await AdminSetAsync(host, Administrator(), Alice, bad)).MessageKey.Should().Be("birthday.set.invalid", bad);
        (await AdminSetAsync(host, Administrator(), Alice, "29.02")).MessageKey.Should().Be("birthday.admin_set.saved");
        (await AdminSetAsync(host, Administrator(), Bob, "14.03", eligible: false)).MessageKey.Should().Be("birthday.admin_set.not_member");
        (await RegistrationsAsync(host)).Should().ContainSingle(r => r.UserId == Alice && r.Day == 29 && r.Month == 2);
    }

    [Fact]
    public async Task Admin_set_for_today_follows_the_normal_reconciliation_rules()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        host.Guilds.SetMemberRoles(Guild, new UserId(Alice));
        host.Guilds.SetMemberRoles(Guild, new UserId(Bob));

        // Before the day's announcement: included in it and given the role.
        (await AdminSetAsync(host, Administrator(), Alice, "14.03")).Succeeded.Should().BeTrue();
        await PassAsync(host);
        await DeliverAsync(host);
        HasRole(host, Alice).Should().BeTrue();
        host.Transport.Messages.Should().ContainSingle().Which.Message.Mentions.Users.Should().Equal(new UserId(Alice));

        // After it: the role, but no second announcement.
        (await AdminSetAsync(host, Administrator(), Bob, "14.03")).Succeeded.Should().BeTrue();
        await PassAsync(host);
        await DeliverAsync(host);
        HasRole(host, Bob).Should().BeTrue();
        host.Transport.Messages.Should().ContainSingle();

        // An admin moving Alice's birthday to tomorrow does not celebrate her twice this year.
        (await AdminSetAsync(host, Administrator(), Alice, "15.03")).MessageKey.Should().Be("birthday.admin_set.updated");
        host.Clock.Advance(TimeSpan.FromDays(1));
        await PassAsync(host);
        await DeliverAsync(host);
        HasRole(host, Alice).Should().BeFalse();
        host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Admin_set_is_audited_with_admin_target_day_month_and_kind_and_self_set_is_distinguishable()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        var logs = new CapturingLogger();
        await host.InScopeAsync(async sp =>
        {
            var service = new BirthdayService(sp.GetRequiredService<ToroDbContext>(), host.Clock, logs);
            await service.SetForMemberAsync(Administrator(1), new UserId(Alice), true, "14.03", Ct);
            await service.SetForMemberAsync(Administrator(1), new UserId(Alice), true, "15.03", Ct);
            await service.SetAsync(User(Bob), "20.07", Ct);
        });

        logs.Entries.Should().HaveCount(3);
        logs.Entries[0].Message.Should().StartWith("birthday_registered").And.Contain("source=admin");
        logs.Entries[0].Values.Should().Contain(new KeyValuePair<string, object?>("Guild", Guild))
            .And.Contain(new KeyValuePair<string, object?>("Admin", new UserId(1)))
            .And.Contain(new KeyValuePair<string, object?>("User", new UserId(Alice)))
            .And.Contain(new KeyValuePair<string, object?>("Day", 14))
            .And.Contain(new KeyValuePair<string, object?>("Month", 3));
        logs.Entries[1].Message.Should().StartWith("birthday_updated").And.Contain("source=admin").And.Contain("day=15");
        logs.Entries[2].Message.Should().Be("birthday_registered guild=900 user=22 source=self", "self-service logs no date");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<BirthdayService>
    {
        public List<(string Message, IReadOnlyList<KeyValuePair<string, object?>> Values)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }

    // ---------- /birthday-admin show ----------

    private static Task<(OperationResult Result, BirthdayDate? Date)> AdminShowAsync(TestHost host, ActorContext actor, ulong member, bool eligible = true) =>
        host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().GetForMemberAsync(actor, new UserId(member), eligible, Ct));

    [Fact]
    public async Task Administrator_and_guild_owner_see_a_members_saved_birthday_or_that_there_is_none()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        await RegisterAsync(host, Alice, "14.03");

        foreach (var actor in new[] { Administrator(), Owner() })
        {
            var (found, date) = await AdminShowAsync(host, actor, Alice);
            found.Succeeded.Should().BeTrue();
            found.MessageKey.Should().Be("birthday.admin_show.value");
            date.Should().Be(BirthdayDate.Create(14, 3));

            var (none, nothing) = await AdminShowAsync(host, actor, Bob);
            none.Succeeded.Should().BeTrue();
            none.MessageKey.Should().Be("birthday.admin_show.none");
            nothing.Should().BeNull();
        }

        var catalog = ToroSquad.Tests.Unit.BirthdayDateTests.Localizer();
        catalog.Get("tr", "birthday.admin_show.value", "<@11>", "14 Mart").Should().Be("🎂 <@11> kullanıcısının kayıtlı doğum günü: 14 Mart");
        catalog.Get("tr", "birthday.admin_show.none", "<@22>").Should().Be("🎂 <@22> kullanıcısının kayıtlı bir doğum günü yok.");
        (await RegistrationsAsync(host)).Should().ContainSingle("reading changes nothing");
    }

    [Fact]
    public async Task Without_administrator_the_lookup_is_refused_before_the_database_and_reveals_nothing()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        await RegisterAsync(host, Alice, "14.03"); // Bob has none
        var manageServer = TestHost.Admin(Guild); // Manage Server + Manage Roles, no Administrator
        var everythingElse = new ActorContext(Guild, new UserId(3), (GuildPermission)ulong.MaxValue & ~GuildPermission.Administrator, [], false, 99);
        var logs = new CapturingLogger();

        await host.InScopeAsync(async sp =>
        {
            var service = new BirthdayService(sp.GetRequiredService<ToroDbContext>(), host.Clock, logs);
            foreach (var actor in new[] { manageServer, User(Cem), everythingElse })
            {
                var withRecord = await service.GetForMemberAsync(actor, new UserId(Alice), true, Ct);
                var withoutRecord = await service.GetForMemberAsync(actor, new UserId(Bob), true, Ct);
                var notMember = await service.GetForMemberAsync(actor, new UserId(Bob), false, Ct);
                foreach (var (result, date) in new[] { withRecord, withoutRecord, notMember })
                {
                    result.Succeeded.Should().BeFalse();
                    result.Error.Should().Be(OperationError.Forbidden);
                    result.MessageKey.Should().Be("birthday.admin_set.forbidden", "the same Administrator message as /birthday-admin set");
                    result.Args.Should().BeEmpty();
                    date.Should().BeNull();
                }
            }
        });

        logs.Entries.Should().BeEmpty("a refused caller never reaches the lookup, so nothing is audited as viewed");
    }

    [Fact]
    public async Task Admin_lookup_reads_only_this_guild_and_refuses_bots_and_non_members()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        await SetUpGuildAsync(host, OtherGuild, OtherChannel);
        await RegisterAsync(host, Alice, "20.07", OtherGuild);
        await RegisterAsync(host, Bob, "14.03");
        await RegisterAsync(host, Bob, "01.01", OtherGuild);

        (await AdminShowAsync(host, Administrator(), Alice)).Result.MessageKey.Should().Be("birthday.admin_show.none", "Alice's record belongs to the other guild");
        (await AdminShowAsync(host, Administrator(), Bob)).Date.Should().Be(BirthdayDate.Create(14, 3), "this guild's row, never the other guild's");

        var (bot, date) = await AdminShowAsync(host, Administrator(), Bob, eligible: false);
        bot.Error.Should().Be(OperationError.InvalidInput);
        bot.MessageKey.Should().Be("birthday.admin_show.not_member");
        date.Should().BeNull();
    }

    [Fact]
    public async Task Member_show_stays_self_only()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        await RegisterAsync(host, Alice, "14.03");

        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().GetAsync(User(Bob), Ct))).Should().BeNull("Bob only ever sees his own");
        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().GetAsync(User(Alice), Ct))).Should().Be(BirthdayDate.Create(14, 3));
        typeof(BirthdayService).GetMethod(nameof(BirthdayService.GetAsync))!.GetParameters().Select(p => p.ParameterType)
            .Should().Equal([typeof(ActorContext), typeof(CancellationToken)], "no target parameter: the caller is the target");
    }

    [Fact]
    public async Task Admin_lookup_is_audited_with_admin_and_target_but_never_the_date()
    {
        await using var host = await HostAsync(March14.AddDays(-30));
        await RegisterAsync(host, Alice, "14.03");
        var logs = new CapturingLogger();
        await host.InScopeAsync(async sp =>
        {
            var service = new BirthdayService(sp.GetRequiredService<ToroDbContext>(), host.Clock, logs);
            await service.GetForMemberAsync(Administrator(1), new UserId(Alice), true, Ct);
            await service.GetForMemberAsync(Owner(2), new UserId(Bob), true, Ct);
        });

        logs.Entries.Should().HaveCount(2);
        logs.Entries[0].Message.Should().Be("birthday_admin_viewed guild=900 admin=1 target=11 found=True");
        logs.Entries[0].Values.Should().Contain(new KeyValuePair<string, object?>("Guild", Guild))
            .And.Contain(new KeyValuePair<string, object?>("Admin", new UserId(1)))
            .And.Contain(new KeyValuePair<string, object?>("TargetUser", new UserId(Alice)))
            .And.Contain(new KeyValuePair<string, object?>("Found", true));
        logs.Entries[1].Message.Should().Be("birthday_admin_viewed guild=900 admin=2 target=22 found=False");
        logs.Entries.SelectMany(e => e.Values).Select(v => v.Key).Should().NotContain(["Day", "Month", "Date"]);
        logs.Entries.Should().NotContain(e => e.Message.Contains("14.03", StringComparison.Ordinal) || e.Message.Contains("Mart", StringComparison.Ordinal)
                                               || e.Message.Contains("day=", StringComparison.Ordinal) || e.Message.Contains("month=", StringComparison.Ordinal));
    }

    // ---------- isolation and privacy ----------

    [Fact]
    public async Task Guilds_are_isolated()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await SetUpGuildAsync(host, OtherGuild, OtherChannel);
        await RegisterAsync(host, Alice, "14.03");
        await RegisterAsync(host, Alice, "20.07", OtherGuild);
        await RegisterAsync(host, Bob, "14.03", OtherGuild);

        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().GetAsync(User(Alice, OtherGuild), Ct))).Should().Be(BirthdayDate.Create(20, 7));
        await PassAsync(host);
        await DeliverAsync(host);

        host.Transport.Messages.Should().HaveCount(2);
        host.Transport.Messages.Single(m => m.Channel == Channel).Message.Content.Should().Contain("<@11>").And.NotContain("<@22>");
        host.Transport.Messages.Single(m => m.Channel == OtherChannel).Message.Content.Should().Contain("<@22>").And.NotContain("<@11>");
        HasRole(host, Alice).Should().BeTrue();
        HasRole(host, Alice, OtherGuild).Should().BeFalse("her birthday there is in July");

        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().RemoveAsync(User(Alice, OtherGuild), Ct))).MessageKey.Should().Be("birthday.remove.done");
        (await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayService>().GetAsync(User(Alice), Ct))).Should().NotBeNull("the other guild is untouched");

        var (auth, status) = await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayConfigService>().StatusAsync(TestHost.Admin(OtherGuild), Ct));
        auth.Succeeded.Should().BeTrue();
        status!.Registered.Should().Be(1);
        status.ChannelId.Should().Be(OtherChannel.Value);
    }

    [Fact]
    public async Task Admin_commands_require_manage_server_and_the_channel_must_belong_to_the_guild()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<BirthdayConfigService>();
            (await config.SetChannelAsync(User(Alice), Channel.Value, Ct)).Error.Should().Be(OperationError.Forbidden);
            (await config.StatusAsync(User(Alice), Ct)).Status.Should().BeNull();
            (await sp.GetRequiredService<BirthdayDoctor>().RunAsync(User(Alice), Ct)).Checks.Should().BeEmpty();
            (await config.SetChannelAsync(TestHost.Admin(Guild), OtherChannel.Value, Ct)).Error.Should().Be(OperationError.InvalidInput, "not a channel of this guild");
        });
    }

    [Fact]
    public async Task Doctor_passes_on_a_healthy_setup()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        host.Services.GetRequiredService<BirthdayHealth>().WorkerStarted(host.Clock.GetUtcNow());
        await PassAsync(host);

        var (auth, checks) = await host.InScopeAsync(sp => sp.GetRequiredService<BirthdayDoctor>().RunAsync(TestHost.Admin(Guild), Ct));
        auth.Succeeded.Should().BeTrue();
        checks.Where(c => c.State == BirthdayCheckState.Problem).Should().BeEmpty(string.Join(", ", checks.Select(c => c.LabelKey + ":" + c.DetailKey)));
        checks.Select(c => c.LabelKey).Should().Contain(["birthday.doctor.config", "birthday.doctor.channel", "birthday.doctor.perm_send", "birthday.doctor.role",
            "birthday.doctor.manage_roles", "birthday.doctor.hierarchy", "birthday.doctor.managed", "birthday.doctor.database", "birthday.doctor.scheduler"]);
    }

    [Fact]
    public async Task Privacy_export_and_delete_cover_the_birthday_and_take_back_the_role()
    {
        await using var host = await HostAsync(March14.AddMinutes(1));
        await RegisterAsync(host, Alice);
        await PassAsync(host);
        HasRole(host, Alice).Should().BeTrue();

        await host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "birthday");
            var export = (await data.ExportAsync(Guild, new UserId(Alice), Ct)).ToJsonString();
            export.Should().Contain("\"day\":14").And.Contain("\"month\":3").And.NotContain("1990");
            (await data.PreviewDeletionAsync(Guild, new UserId(Alice), Ct)).Should().HaveCount(2);
            (await data.DeleteAsync(Guild, new UserId(Alice), Ct)).RecordsDeleted.Should().Be(2);
        });
        HasRole(host, Alice).Should().BeFalse();
        (await CelebrationsAsync(host)).Should().BeEmpty();
    }
}
