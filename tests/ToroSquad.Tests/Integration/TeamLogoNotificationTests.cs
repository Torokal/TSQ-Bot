using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Team logos through the real planner and outbox (real SQLite, live data mode): the followed team's logo reaches the
/// stored payload, repeated polls and restarts still produce exactly one message, nobody is pinged because of it, and a
/// server without a team filter gets the same logo-less payload as before.
/// </summary>
public sealed class TeamLogoNotificationTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(555);
    private static readonly ChannelId Channel = new(5550);
    private static readonly RoleId BravoRole = new(5551);
    private const string LogoAlpha = "https://cdn-api.pandascore.co/images/team/image/1/alpha.png";
    private const string LogoBravo = "https://cdn-api.pandascore.co/images/team/image/2/bravo.png";
    private static readonly TeamRef Alpha = new("pandascore", "ps-team:1", "Alpha", "ALP", LogoAlpha);
    private static readonly TeamRef Bravo = new("pandascore", "ps-team:2", "Bravo", "BRV", LogoBravo);

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        // Live data mode (no provider call happens here: the planner is fed directly).
        _host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" },
            replace: s => s.AddSingleton(new EsportsDataMode(ProviderMode.Live)));
        await _host.SetUpEsportsGuildAsync(Guild, Channel, new RoleInfo(BravoRole, "Bravo fans", 3, GuildPermission.None, false, false, true));
        await PlanAsync([M("BOOT", MatchStatus.Scheduled, TestHost.T0.AddDays(5))]);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static EsportsMatch M(string id, MatchStatus status, DateTimeOffset start, int? winner = null) => new(
        new MatchKey("pandascore", id),
        new TournamentRef("pandascore", "ps-tournament:1", "Demo Masters 2026", "1", null, null, null),
        start, true, 3, status, "test",
        new MatchOpponent(OpponentKind.Team, Alpha, status == MatchStatus.Finished ? 1 : null, OpponentResult.Scored),
        new MatchOpponent(OpponentKind.Team, Bravo, status == MatchStatus.Finished ? 2 : null, OpponentResult.Scored),
        winner, false, false, [], "Playoffs", null, [],
        BeginAtUtc: status is MatchStatus.Live or MatchStatus.Finished ? start : null,
        EndAtUtc: status == MatchStatus.Finished ? start.AddHours(2) : null);

    private Task<PlanReport> PlanAsync(IReadOnlyList<EsportsMatch> matches) =>
        _host.InScopeAsync(sp => sp.GetRequiredService<NotificationPlanner>().PlanAsync(matches, _host.Clock.GetUtcNow(), false, CancellationToken.None));

    private Task<List<OutboxMessageEntity>> OutboxAsync(string kind) =>
        _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.Kind == kind).OrderBy(o => o.Id).ToListAsync());

    private Task FollowAsync(string teamKey) => _host.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<ToroDbContext>();
        db.Set<EsportsFilterEntity>().Add(new EsportsFilterEntity { GuildId = Guild.Value, Dimension = (int)FilterDimension.Team, Value = teamKey });
        await db.SaveChangesAsync();
    });

    private static JsonNode Payload(OutboxMessageEntity o) => JsonNode.Parse(o.PayloadJson)!;

    private async Task<OutboxMessageEntity> StartedOnceAsync(string id)
    {
        var start = _host.Clock.GetUtcNow().AddHours(3);
        await PlanAsync([M(id, MatchStatus.Scheduled, start)]);
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await PlanAsync([M(id, MatchStatus.Live, start)]);
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await PlanAsync([M(id, MatchStatus.Live, start)]);
        await PlanAsync([M(id, MatchStatus.Live, start)]);
        var started = await OutboxAsync(NotificationPlanner.KindStarted);
        started.Should().ContainSingle("the logo must not cause a duplicate across repeated polls");
        return started[0];
    }

    [Fact]
    public async Task Started_card_for_the_followed_team_carries_its_logo_once_and_pings_nobody()
    {
        await FollowAsync("ps-team:2");
        var row = await StartedOnceAsync("L1");
        var payload = Payload(row);
        payload["embed"]!["thumbnailUrl"]!.GetValue<string>().Should().Be(LogoBravo);
        payload["content"].Should().BeNull();
        payload["mentions"]!["roles"]!.AsArray().Should().BeEmpty("no role mapping, so the logo adds no ping");
        row.GuildId.Should().Be(Guild.Value);
    }

    [Fact]
    public async Task Without_a_team_filter_the_started_card_has_no_logo_and_the_same_payload_shape_as_before()
    {
        var row = await StartedOnceAsync("L2");
        row.PayloadJson.Should().NotContain("thumbnail");
    }

    [Fact]
    public async Task Result_card_shows_the_winners_logo()
    {
        await FollowAsync("ps-team:1");
        var start = _host.Clock.GetUtcNow().AddHours(-2);
        await PlanAsync([M("L3", MatchStatus.Live, start)]);
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await PlanAsync([M("L3", MatchStatus.Finished, start, winner: 1)]);
        await PlanAsync([M("L3", MatchStatus.Finished, start, winner: 1)]);

        var result = await OutboxAsync(NotificationPlanner.KindResult);
        result.Should().ContainSingle();
        Payload(result[0])["embed"]!["thumbnailUrl"]!.GetValue<string>().Should().Be(LogoBravo, "the winner, not the followed loser");
    }

    [Fact]
    public async Task Reminder_for_the_followed_team_carries_its_logo_keeps_the_mapped_ping_and_is_sent_once()
    {
        await FollowAsync("ps-team:2");
        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<RoleMappingService>().MapAsync(TestHost.Admin(Guild), BravoRole, "ps-team:2", true, true, CancellationToken.None))
            .Succeeded.Should().BeTrue());

        var start = _host.Clock.GetUtcNow().AddMinutes(10); // inside the 15-minute reminder lead
        await PlanAsync([M("R1", MatchStatus.Scheduled, start)]);
        _host.Clock.Advance(TimeSpan.FromMinutes(2));
        await PlanAsync([M("R1", MatchStatus.Scheduled, start)]);
        await PlanAsync([M("R1", MatchStatus.Scheduled, start)]);

        var reminders = await OutboxAsync(NotificationPlanner.KindReminder);
        reminders.Should().ContainSingle("repeated polls never create a second reminder");
        var payload = Payload(reminders[0]);
        payload["embed"]!["thumbnailUrl"]!.GetValue<string>().Should().Be(LogoBravo);
        payload["content"]!.GetValue<string>().Should().Be("<@&5551>", "the mapped reminder ping is unchanged by the logo");
        payload["mentions"]!["roles"]!.AsArray().Select(r => r!["value"]!.GetValue<ulong>()).Should().Equal(5551UL);
        payload["embed"]!["description"]!.GetValue<string>().Should().Contain("<t:" + start.ToUnixTimeSeconds() + ":R>");
    }
}
