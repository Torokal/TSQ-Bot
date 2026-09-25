using System.Reflection;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Formula1;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Commands;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;
using CorePermission = ToroSquad.Core.Security.GuildPermission;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// /f1-admin authorization (Discord metadata AND server-side), configuration validation, ping-free previews, doctor
/// diagnostics, and the cache-only selection logic behind /f1.
/// </summary>
public sealed class F1AdminAndCommandTests
{
    [Fact]
    public async Task Every_admin_operation_is_refused_server_side_for_members_and_other_guilds()
    {
        var (host, _) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        await host.SetUpF1GuildAsync(Guild, Channel);
        var member = TestHost.Member(Guild);
        var otherGuildAdmin = TestHost.Admin(Guild) with { GuildId = new GuildId(999) };
        await host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<Formula1ConfigService>();
            var results = new List<OperationResult>
            {
                await config.SetChannelAsync(member, Channel.Value, CancellationToken.None),
                await config.SetNotificationsAsync(member, new F1NotificationChanges(RaceStart: false), CancellationToken.None),
                await config.SetRoleAsync(member, Role.Value, true, true, CancellationToken.None),
                await config.SetSpoilersAsync(member, true, CancellationToken.None),
                await config.PauseAsync(member, true, CancellationToken.None),
                (await sp.GetRequiredService<Formula1Doctor>().RunAsync(member, CancellationToken.None)).Auth,
                (await sp.GetRequiredService<Formula1PreviewService>().BuildAsync(member, "tr", F1PreviewKind.RaceStart, CancellationToken.None)).Auth,
            };
            results.Should().OnlyContain(r => !r.Succeeded && r.Error == OperationError.Forbidden);
            // An admin of ANOTHER guild only ever changes that guild: the guild comes from the interaction, never from input.
            (await config.PauseAsync(otherGuildAdmin, true, CancellationToken.None)).Succeeded.Should().BeTrue();

            var stored = await config.GetAsync(Guild, CancellationToken.None);
            stored!.NotifyRaceStart.Should().BeTrue();
            stored.Paused.Should().BeFalse();
            stored.SpoilerMode.Should().BeFalse();
        });
    }

    [Fact]
    public void Discord_metadata_hides_admin_commands_and_gates_public_ones()
    {
        var admin = typeof(Formula1AdminCommands);
        admin.GetCustomAttribute<DefaultMemberPermissionsAttribute>()!.Permissions.Should().Be(global::Discord.GuildPermission.ManageGuild);
        admin.GetCustomAttribute<ToroModuleAttribute>()!.Should().BeEquivalentTo(new { ModuleId = "formula1", AllowWhenDisabled = true });
        typeof(Formula1Commands).GetCustomAttribute<ToroModuleAttribute>()!.AllowWhenDisabled.Should().BeFalse("public commands honour the module gate");
        typeof(Formula1Commands.StandingsCommands).GetCustomAttribute<ToroModuleAttribute>()!.AllowWhenDisabled.Should().BeFalse();
        typeof(Formula1Commands).GetCustomAttribute<CommandContextTypeAttribute>()!.ContextTypes.Should().Equal(InteractionContextType.Guild);
        new Formula1Module().Descriptor.Should().BeEquivalentTo(new { EnabledByDefault = false, IsCore = false, AdminCommands = new[] { "f1-admin" } });
        new Formula1Module().Descriptor.Version.Should().Be(new Version(0, 1, 0));
    }

    [Fact]
    public async Task Public_commands_are_refused_while_the_module_is_disabled_and_admin_setup_still_works()
    {
        var (host, _) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        await host.InScopeAsync(async sp =>
        {
            var gated = new ToroModuleAttribute("formula1");
            var setup = new ToroModuleAttribute("formula1") { AllowWhenDisabled = true };
            var context = InterfaceFake.Create<IInteractionContext>(new()
            {
                ["Guild"] = InterfaceFake.Create<IGuild>(new() { ["Id"] = Guild.Value, ["OwnerId"] = 1UL }),
                ["User"] = InterfaceFake.Create<IGuildUser>(new() { ["Id"] = 9UL, ["GuildPermissions"] = new GuildPermissions(0), ["RoleIds"] = (IReadOnlyCollection<ulong>)Array.Empty<ulong>() }),
            });
            (await gated.CheckRequirementsAsync(context, null!, sp)).ErrorReason.Should().Be(ToroModuleAttribute.ModuleDisabledError);
            (await setup.CheckRequirementsAsync(context, null!, sp)).IsSuccess.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Role_configuration_never_accepts_everyone_or_foreign_roles_and_channel_must_exist_in_the_guild()
    {
        var (host, _) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        await host.SetUpF1GuildAsync(Guild, Channel, Role);
        var admin = TestHost.Admin(Guild);
        await host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<Formula1ConfigService>();
            (await config.SetRoleAsync(admin, Guild.Value, true, true, CancellationToken.None)).MessageKey.Should().Be("f1.config.role_everyone");
            (await config.SetRoleAsync(admin, 424242, true, true, CancellationToken.None)).MessageKey.Should().Be("f1.config.role_invalid");
            (await config.SetChannelAsync(admin, 123456, CancellationToken.None)).MessageKey.Should().Be("f1.config.channel_invalid", "no silent fallback to another channel");
            (await config.GetAsync(Guild, CancellationToken.None))!.PingRoleId.Should().Be(Role.Value);
            (await config.SetRoleAsync(admin, null, null, null, CancellationToken.None)).MessageKey.Should().Be("f1.config.role_cleared");
        });
        Formula1NotificationPlanner.Pings(new() { PingRoleId = Guild.Value, PingOnStarts = true }, Guild, start: true).Should().Be(MentionPolicy.None,
            "even a stored @everyone id is never pinged");

        host.Guilds.SetChannel(Guild, new ChannelId(7777), new BotChannelAccess(true, true, CorePermission.ViewChannel | CorePermission.SendMessages));
        await host.InScopeAsync(async sp => (await sp.GetRequiredService<Formula1ConfigService>().SetChannelAsync(admin, 7777, CancellationToken.None))
            .MessageKey.Should().Be("f1.config.saved_missing_permissions"));
    }

    [Fact]
    public async Task Preview_is_ping_free_synthetic_and_labelled_and_reports_who_would_be_pinged()
    {
        var (host, _) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        await host.SetUpF1GuildAsync(Guild, Channel, Role);
        await host.InScopeAsync(async sp =>
        {
            var preview = sp.GetRequiredService<Formula1PreviewService>();
            var start = await preview.BuildAsync(TestHost.Admin(Guild), "tr", F1PreviewKind.RaceStart, CancellationToken.None);
            start.Message!.Mentions.Should().Be(MentionPolicy.None);
            start.Message.Content.Should().BeNull();
            start.Message.Embed!.Title.Should().StartWith("[TEST/DEMO]").And.Contain(F1DemoData.MeetingName);
            start.Message.Embed.Footer.Should().Contain("TEST/DEMO").And.NotContain("OpenF1");
            start.WouldPing.Should().Equal(Role);

            var result = await preview.BuildAsync(TestHost.Admin(Guild), "tr", F1PreviewKind.RaceResultStandings, CancellationToken.None);
            result.WouldPing.Should().BeEmpty("results do not ping by default");
            result.Message!.Embed!.Fields.Should().HaveCount(2);
        });
    }

    [Fact]
    public async Task Doctor_surfaces_actionable_provider_and_permission_problems_without_secrets()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(new(Live) { ["Formula1:OpenF1:Password"] = "super-secret-password" });
        await using var _ = host;
        fake.AddMeeting(2026, 18, (F1SessionType.Race, T0.AddDays(3)));
        fake.LifecycleConfigured = false;
        await host.SetUpF1GuildAsync(Guild, Channel);
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, CorePermission.ViewChannel | CorePermission.SendMessages));
        fake.StandingsFailure = F1ProviderOutcome.QuotaExceeded;
        await TickAsync(host);
        host.Services.GetRequiredService<Formula1Cache>().RecordResultsAttempt(F1ProviderOutcome.QuotaExceeded, "HTTP 429", host.Clock.GetUtcNow());

        var (auth, checks) = await host.InScopeAsync(sp => sp.GetRequiredService<Formula1Doctor>().RunAsync(TestHost.Admin(Guild), CancellationToken.None));
        auth.Succeeded.Should().BeTrue();
        checks.Should().Contain(c => c.LabelKey == "f1.doctor.lifecycle" && c.State == F1CheckState.Problem && c.DetailKey == "f1.doctor.lifecycle_not_configured");
        checks.Should().Contain(c => c.DetailKey == "f1.doctor.perm_missing" && c.Args[0].Equals("EmbedLinks") && c.State == F1CheckState.Problem);
        checks.Should().Contain(c => c.LabelKey == "f1.doctor.schedule" && c.State == F1CheckState.Ok);
        checks.Should().Contain(c => c.LabelKey == "f1.doctor.standings_drivers" && c.State == F1CheckState.Problem && c.DetailKey == "f1.doctor.feed_none");
        checks.Should().Contain(c => c.LabelKey == "f1.doctor.results" && c.State == F1CheckState.Warning && c.Args.Contains("QuotaExceeded"));
        checks.SelectMany(c => c.Args).Select(a => a.ToString()).Should().NotContain(a => a!.Contains("super-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_check_reports_cached_state_only_and_live_lifecycle_honestly()
    {
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(Live);
        await using var _ = host;
        fake.LifecycleConfigured = false;
        await host.SetUpF1GuildAsync(Guild, Channel);
        var calls = (fake.ScheduleCalls, fake.StandingsCalls, fake.LifecycleCalls, fake.ResultCalls);
        var report = await host.Services.GetServices<IModuleHealthCheck>().Single(h => h.Module.Value == "formula1").CheckAsync(CancellationToken.None);
        (fake.ScheduleCalls, fake.StandingsCalls, fake.LifecycleCalls, fake.ResultCalls).Should().Be(calls, "health checks never call providers");
        report.Entries.Should().Contain(e => e.Component == "f1.health.lifecycle" && e.State == HealthState.NotConfigured);
    }

    // ---------------------------------------------------------------- command selection logic (cache only)

    private static F1SessionView V(int round, F1SessionType type, DateTimeOffset start, F1SessionState state = F1SessionState.Scheduled) =>
        new(new F1Session(2026, round, type, start), "GP " + round, "Circuit", null, null, state, null, null, null, null, null, false);

    [Fact]
    public void Next_meeting_is_the_current_weekend_until_its_race_is_over_then_the_next_one()
    {
        var now = T0;
        var sessions = new List<F1SessionView>
        {
            V(17, F1SessionType.Race, now.AddDays(-7), F1SessionState.Finalised),
            V(18, F1SessionType.Practice1, now.AddDays(-1), F1SessionState.Finalised),
            V(18, F1SessionType.Race, now.AddHours(20)),
            V(19, F1SessionType.Race, now.AddDays(14)),
        };
        F1CommandViews.NextMeeting(sessions, now)![0].Session.Round.Should().Be(18, "the running weekend is the relevant one");
        F1CommandViews.NextMeeting(sessions, now.AddDays(2))![0].Session.Round.Should().Be(19);
        F1CommandViews.NextMeeting(sessions, now.AddDays(30)).Should().BeNull();
        F1CommandViews.Meeting(sessions, 2026, 18)!.Select(s => s.Session.Type).Should().Equal(F1SessionType.Practice1, F1SessionType.Race);
        F1CommandViews.Meeting(sessions, 2026, 99).Should().BeNull();
    }

    [Fact]
    public void Latest_result_is_chosen_from_the_cache_by_type()
    {
        var race = V(18, F1SessionType.Race, T0);
        var fp1 = V(18, F1SessionType.Practice1, T0.AddDays(-2));
        var results = new Dictionary<string, F1CachedResult>
        {
            [race.Session.Key] = new(new F1SessionResult(race.Session.Key, F1SessionType.Race, "openf1", []), T0, T0),
            [fp1.Session.Key] = new(new F1SessionResult(fp1.Session.Key, F1SessionType.Practice1, "openf1", []), T0, T0),
        };
        F1CommandViews.LatestResult([fp1, race], results, null)!.Value.View.Session.Type.Should().Be(F1SessionType.Race);
        F1CommandViews.LatestResult([fp1, race], results, F1SessionType.Practice1)!.Value.View.Should().Be(fp1);
        F1CommandViews.LatestResult([fp1, race], results, F1SessionType.Sprint).Should().BeNull();
    }
}
