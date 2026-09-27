using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// A changed start time is not a card of its own (owner decision 2026-09-26). Real SQLite + planner + outbox + fake
/// transport: a delivered reminder is edited in place (new planned start as a native Discord timestamp plus "start time
/// updated (previously …)") without a new message or ping; a second change edits the same message again and shows the
/// last previous time; unchanged data and restarts edit nothing; without a reminder the new time is only match state
/// until the normal reminder is due; postponed keeps its own card.
/// </summary>
public sealed class RescheduleReminderTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(666);
    private static readonly ChannelId Channel = new(6660);
    private static readonly RoleId AlphaRole = new(6661);
    private const string LogoAlpha = "https://cdn-api.pandascore.co/images/team/image/1/alpha.png";
    private static readonly TeamRef Alpha = new("pandascore", "ps-team:1", "Alpha", "ALP", LogoAlpha);
    private static readonly TeamRef Bravo = new("pandascore", "ps-team:2", "Bravo", "BRV");

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        // Live data mode so the followed team's logo is on the card (demo cards never carry one); no provider is called.
        _host = await TestHost.CreateAsync(new() { ["Esports:Provider:Name"] = "PandaScore" },
            replace: s => s.AddSingleton(new EsportsDataMode(ProviderMode.Live)));
        await _host.SetUpEsportsGuildAsync(Guild, Channel, new RoleInfo(AlphaRole, "Alpha fans", 3, GuildPermission.None, false, false, true));
        await PlanAsync([M("BOOT", TestHost.T0.AddDays(5))]); // baseline run; also makes the teams known for role mapping
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Set<EsportsFilterEntity>().Add(new EsportsFilterEntity { GuildId = Guild.Value, Dimension = (int)FilterDimension.Team, Value = "ps-team:1" });
            await db.SaveChangesAsync();
            (await sp.GetRequiredService<RoleMappingService>().MapAsync(TestHost.Admin(Guild), AlphaRole, "ps-team:1", true, true, CancellationToken.None))
                .Succeeded.Should().BeTrue();
        });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static EsportsMatch M(string id, DateTimeOffset start, MatchStatus status = MatchStatus.Scheduled, bool rescheduled = false) => new(
        new MatchKey("pandascore", id),
        new TournamentRef("pandascore", "ps-tournament:1", "Demo Masters 2026", "1", null, null, null),
        start, true, 3, status, "test",
        new MatchOpponent(OpponentKind.Team, Alpha, null, OpponentResult.Scored),
        new MatchOpponent(OpponentKind.Team, Bravo, null, OpponentResult.Scored),
        null, false, false, [], "Playoffs", null, [],
        Rescheduled: rescheduled);

    private Task<PlanReport> PlanAsync(IReadOnlyList<EsportsMatch> matches) =>
        _host.InScopeAsync(sp => sp.GetRequiredService<NotificationPlanner>().PlanAsync(matches, _host.Clock.GetUtcNow(), false, CancellationToken.None));

    private Task DeliverAsync() => _host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

    private Task<List<OutboxMessageEntity>> OutboxAsync() =>
        _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    /// <summary>"Restart": every scope is new anyway; planning again with state only from the database is what a restart does.</summary>
    private async Task RestartAsync() => await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Database.CanConnectAsync());

    private static string Description(OutgoingMessage m) => m.Embed!.Description!;

    private static string Ts(DateTimeOffset t, char style) => $"<t:{t.ToUnixTimeSeconds()}:{style}>";

    [Fact]
    public async Task A_changed_start_time_edits_the_delivered_reminder_without_a_new_card_or_ping()
    {
        var start = _host.Clock.GetUtcNow().AddMinutes(20);
        await PlanAsync([M("R1", start)]);
        await DeliverAsync();
        var sent = _host.Transport.Messages.Should().ContainSingle().Subject;
        sent.Message.Mentions.Roles.Select(r => r.Value).Should().Equal(new[] { AlphaRole.Value }, "the first reminder pings the mapped role");
        Description(sent.Message).Should().Contain(Ts(start, 'R')).And.NotContain("güncellendi");
        sent.Message.Embed!.ThumbnailUrl.Should().Be(LogoAlpha);

        _host.Clock.Advance(TimeSpan.FromMinutes(2));
        var moved = M("R1", start.AddMinutes(45), rescheduled: true);
        await PlanAsync([moved]);
        await DeliverAsync();

        _host.Transport.SendCalls.Should().Be(1, "no second message of any kind");
        var message = _host.Transport.Messages.Should().ContainSingle().Subject;
        message.Id.Should().Be(sent.Id, "the same Discord message is edited");
        var edit = message.Edits.Should().ContainSingle().Subject;
        edit.Mentions.Roles.Should().BeEmpty("an edit never pings again (allowed_mentions is emptied)");
        edit.Content.Should().Be(sent.Message.Content, "the visible content, including the role text, stays as it was");
        Description(edit).Should().Contain(Ts(start.AddMinutes(45), 'R'), "the new planned start as a native Discord timestamp")
            .And.Contain("🕒 Başlangıç saati güncellendi").And.Contain(Ts(start, 'f'), "the previous official time")
            .And.NotContain("saati değişti");
        edit.Embed!.ThumbnailUrl.Should().Be(LogoAlpha, "the followed team's logo is unchanged");
        edit.Embed.Title.Should().Be(sent.Message.Embed.Title);
        edit.Embed.Url.Should().Be(sent.Message.Embed.Url, "Match Page behaviour is unchanged");
        edit.Embed.Fields.Select(f => f.Name).Should().Equal(sent.Message.Embed.Fields.Select(f => f.Name));

        var rows = await OutboxAsync();
        rows.Should().ContainSingle().Which.Kind.Should().Be(NotificationPlanner.KindReminder, "no rescheduled row exists");
    }

    [Fact]
    public async Task A_second_change_edits_the_same_message_again_and_shows_the_last_previous_time()
    {
        var start = _host.Clock.GetUtcNow().AddMinutes(20);
        await PlanAsync([M("R2", start)]);
        await DeliverAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await PlanAsync([M("R2", start.AddMinutes(30))]); // unflagged small move: also just an edit
        await DeliverAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = M("R2", start.AddMinutes(60), rescheduled: true);
        await PlanAsync([again]);
        await DeliverAsync();

        var message = _host.Transport.Messages.Should().ContainSingle().Subject;
        message.Edits.Should().HaveCount(2);
        Description(message.Edits[0]).Should().Contain(Ts(start.AddMinutes(30), 'R')).And.Contain(Ts(start, 'f'));
        Description(message.Edits[1]).Should().Contain(Ts(start.AddMinutes(60), 'R'))
            .And.Contain(Ts(start.AddMinutes(30), 'f'), "previous = the last known official time, not the original one")
            .And.NotContain(Ts(start, 'f'));
        message.Edits.Should().OnlyContain(e => e.Mentions.Roles.Count == 0);

        // The same provider data again, across polls and a restart: nothing to send, nothing to edit.
        await PlanAsync([again]);
        await RestartAsync();
        await PlanAsync([again]);
        await DeliverAsync();
        _host.Transport.SendCalls.Should().Be(1);
        _host.Transport.EditCalls.Should().Be(2, "unchanged data is not re-edited");
        var row = (await OutboxAsync()).Should().ContainSingle().Subject;
        row.Kind.Should().Be(NotificationPlanner.KindReminder);
        row.EditPending.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_reminder_a_changed_time_is_only_match_state_until_the_normal_reminder_is_due()
    {
        var start = _host.Clock.GetUtcNow().AddHours(4);
        await PlanAsync([M("R3", start)]);
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        var moved = M("R3", start.AddHours(2), rescheduled: true);
        await PlanAsync([moved]);
        await PlanAsync([moved]);
        await RestartAsync();
        await PlanAsync([moved]);
        await DeliverAsync();
        (await OutboxAsync()).Should().BeEmpty("no separate card, and the reminder is not due yet");
        _host.Transport.SendCalls.Should().Be(0);

        _host.Clock.Advance(start.AddHours(2).AddMinutes(-10) - _host.Clock.GetUtcNow());
        await PlanAsync([moved]);
        await PlanAsync([moved]);
        await RestartAsync();
        await PlanAsync([moved]);
        await DeliverAsync();

        (await OutboxAsync()).Should().ContainSingle().Which.Kind.Should().Be(NotificationPlanner.KindReminder);
        var sent = _host.Transport.Messages.Should().ContainSingle().Subject;
        Description(sent.Message).Should().Contain(Ts(start.AddHours(2), 'R'), "the reminder is for the new time");
        _host.Transport.EditCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_postponed_match_that_gets_a_new_date_edits_the_old_reminder_and_keeps_one_postponed_card()
    {
        var start = _host.Clock.GetUtcNow().AddMinutes(20);
        await PlanAsync([M("R4", start)]);
        await DeliverAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await PlanAsync([M("R4", start, MatchStatus.Postponed)]);
        await DeliverAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        var newDate = M("R4", start.AddDays(1), rescheduled: true);
        await PlanAsync([newDate]);
        await PlanAsync([newDate]);
        await RestartAsync();
        await PlanAsync([newDate]);
        await DeliverAsync();

        var rows = await OutboxAsync();
        rows.Select(r => r.Kind).Should().BeEquivalentTo([NotificationPlanner.KindReminder, NotificationPlanner.KindPostponed]);
        _host.Transport.SendCalls.Should().Be(2, "reminder + postponed, nothing else");
        var reminder = _host.Transport.Messages.Single(m => Description(m.Message).Contains("Planlanan başlangıç", StringComparison.Ordinal));
        var edit = reminder.Edits.Should().ContainSingle().Subject;
        Description(edit).Should().Contain(Ts(start.AddDays(1), 'R')).And.Contain(Ts(start, 'f'));
        edit.Mentions.Roles.Should().BeEmpty();
        _host.Transport.Messages.Single(m => Description(m.Message).Contains("ertelendi", StringComparison.Ordinal)).Edits.Should().BeEmpty();
    }
}
