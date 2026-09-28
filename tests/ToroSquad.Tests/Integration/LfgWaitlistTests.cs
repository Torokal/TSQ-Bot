using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Bot;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The RSVP waitlist on the real SQLite database and the production wiring: a full team queues new players first come
/// first served (a durable stored place, never a timestamp or an id), every freed slot goes to the first in line in the
/// same write (leave, maybe, the owner's larger size, /privacy delete), the races, notices, voice, privacy, terminal
/// listings and the schema upgrade. After every scenario: Joined ≤ MaxPlayers and a waitlist only while every slot is taken.
/// </summary>
public sealed class LfgWaitlistTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(991);
    private static readonly ChannelId Channel = new(9901);
    private static readonly ChannelId Voice = new(9902);
    private const ulong Owner = 10;
    private static readonly CancellationToken Ct = CancellationToken.None;

    // The example team: Oykeli is one of the players, Arif, Shotgun and Hasom wait.
    private const ulong Oykeli = 101;
    private const ulong Arif = 201;
    private const ulong Shotgun = 202;
    private const ulong Hasom = 203;

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, F1TestHostExtensions.ChannelPermissions));
        _host.Guilds.SetVoiceChannel(Guild, Voice, new VoiceChannelAccess(true, true, GuildPermission.ViewChannel | GuildPermission.Connect));
        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "lfg", true, Ct)).Succeeded.Should().BeTrue());
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static ActorContext User(ulong id) => new(Guild, new UserId(id), GuildPermission.ViewChannel | GuildPermission.SendMessages, [], false, 1);

    private Task<T> Lfg<T>(Func<LfgService, Task<T>> action, TestHost? host = null) =>
        (host ?? _host).InScopeAsync(sp => action(sp.GetRequiredService<LfgService>()));

    private Task<LfgResult> JoinAsync(long id, ulong user) => Lfg(s => s.JoinAsync(User(user), id, Ct));

    private Task<LfgResult> MaybeAsync(long id, ulong user) => Lfg(s => s.MaybeAsync(User(user), id, Ct));

    private Task<LfgResult> LeaveAsync(long id, ulong user) => Lfg(s => s.LeaveAsync(User(user), id, Ct));

    private Task<LfgListingView?> GetAsync(long id, TestHost? host = null) => Lfg(s => s.GetAsync(id, Ct), host);

    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    /// <summary>Opens a listing and posts its card like the /ekip form does.</summary>
    private async Task<LfgListingView> OpenAsync(int players, ulong owner = Owner, int? startInMinutes = null, bool notices = false, ChannelId? voice = null)
    {
        var start = startInMinutes is { } m ? LfgForm.FormatDate(_host.Clock.GetUtcNow().AddMinutes(m), Istanbul) : null;
        var created = await Lfg(s => s.CreateAsync(User(owner), Channel, new LfgCreateInput("Deadlock", players, null, null, notices, notices, voice, start), Ct));
        created.Result.Succeeded.Should().BeTrue(created.Result.MessageKey);
        var card = _host.Services.GetRequiredService<LfgCardRenderer>().Render(created.Listing!, "tr");
        var posted = (SendOutcome.Sent)await _host.Transport.SendAsync(Channel, card, Ct);
        await Lfg(async s =>
        {
            await s.AttachMessageAsync(created.Listing!.Id, Guild, Channel, posted.MessageId, Ct);
            return 0;
        });
        return (await GetAsync(created.Listing!.Id))!;
    }

    private async Task FillAsync(long id, int count, ulong firstUser)
    {
        for (var i = 0; i < count; i++)
            (await JoinAsync(id, firstUser + (ulong)i)).Result.MessageKey.Should().Be("lfg.join.done");
    }

    private async Task QueueAsync(long id, params ulong[] users)
    {
        foreach (var user in users)
            (await JoinAsync(id, user)).Result.MessageKey.Should().Be("lfg.join.waitlisted");
    }

    /// <summary>The 10 / 10 team of the example with Arif, Shotgun and Hasom waiting.</summary>
    private async Task<long> FullTeamWithThreeWaitingAsync(ulong owner = Owner, ulong firstPlayer = Oykeli, ulong firstWaiting = Arif)
    {
        var listing = await OpenAsync(10, owner);
        await FillAsync(listing.Id, 9, firstPlayer);
        await QueueAsync(listing.Id, firstWaiting, firstWaiting + 1, firstWaiting + 2);
        return listing.Id;
    }

    /// <summary>The owner's edit form with only the team size changed (every other field untouched).</summary>
    private async Task<LfgResult> EditPlayersAsync(long id, int players)
    {
        var opening = await Lfg(s => s.OpenEditAsync(User(Owner), id, Ct));
        var listing = opening.Listing!;
        var input = new LfgEditInput(opening.Prefill! with { Players = players.ToString(CultureInfo.InvariantCulture) }, listing.NotifyBeforeStart, listing.NotifyAtStart,
            opening.Voice, opening.Prefill, new LfgFormSettings(listing.NotifyBeforeStart, listing.NotifyAtStart, opening.Voice, listing.VoiceChannel));
        return await Lfg(s => s.EditAsync(User(Owner), id, input, Ct));
    }

    private Task<(LfgListingEntity Listing, List<LfgParticipantEntity> Rows)> StoredAsync(long id, TestHost? host = null) => (host ?? _host).InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<ToroDbContext>();
        return (await db.Set<LfgListingEntity>().AsNoTracking().SingleAsync(x => x.Id == id),
            await db.Set<LfgParticipantEntity>().AsNoTracking().Where(p => p.ListingId == id).ToListAsync());
    });

    /// <summary>The roster invariant, read from the database (not from the service's own view).</summary>
    private async Task InvariantAsync(long id)
    {
        var (listing, rows) = await StoredAsync(id);
        var joined = rows.Count(p => p.Response == LfgResponse.Joined);
        var waiting = rows.Where(p => p.Response == LfgResponse.Waitlisted).ToList();
        joined.Should().BeLessThanOrEqualTo(listing.MaxPlayers, "a listing is never overbooked");
        (waiting.Count == 0 || joined == listing.MaxPlayers).Should().BeTrue($"a waitlist exists only while every slot is taken ({joined} / {listing.MaxPlayers}, {waiting.Count} waiting)");
        waiting.Should().OnlyContain(p => p.WaitlistOrder != null);
        rows.Where(p => p.Response != LfgResponse.Waitlisted).Should().OnlyContain(p => p.WaitlistOrder == null, "a place only while waiting");
        waiting.Select(p => p.WaitlistOrder).Should().OnlyHaveUniqueItems("two people never share a place");
        rows.Select(p => p.UserId).Should().OnlyHaveUniqueItems();
        rows.Single(p => p.UserId == listing.OwnerUserId).Response.Should().Be(LfgResponse.Joined, "the owner is always Joined");
        if (listing.Status is LfgStatus.Open or LfgStatus.Full)
            listing.Status.Should().Be(joined >= listing.MaxPlayers ? LfgStatus.Full : LfgStatus.Open);
    }

    private static List<UserId> Users(params ulong[] ids) => ids.Select(id => new UserId(id)).ToList();

    // ---- joining a full team ----

    [Fact]
    public async Task A_full_team_puts_the_11th_and_12th_player_in_line_and_an_open_one_takes_them_straight_in()
    {
        var listing = await OpenAsync(10);
        await FillAsync(listing.Id, 8, Oykeli);
        (await JoinAsync(listing.Id, 150)).Result.MessageKey.Should().Be("lfg.join.done", "the 10th slot is still free");

        var eleventh = await JoinAsync(listing.Id, Arif);
        var twelfth = await JoinAsync(listing.Id, Shotgun);

        (eleventh.Result.MessageKey, eleventh.Result.Args[0], twelfth.Result.MessageKey, twelfth.Result.Args[0])
            .Should().Be(("lfg.join.waitlisted", (object)1, "lfg.join.waitlisted", (object)2));
        eleventh.Result.Succeeded.Should().BeTrue("joining the waitlist is not a refusal");
        var view = twelfth.Listing!;
        (view.Players.Count, view.Status).Should().Be((10, LfgStatus.Full));
        view.WaitlistedPlayers.Should().Equal(Users(Arif, Shotgun));
        view.Players.Should().NotContain(new UserId(Arif));
        await InvariantAsync(listing.Id);
    }

    [Fact]
    public async Task Pressing_join_again_while_waiting_keeps_one_row_and_answers_the_current_place()
    {
        var id = await FullTeamWithThreeWaitingAsync();
        var before = (await StoredAsync(id)).Rows.Single(p => p.UserId == Shotgun).WaitlistOrder;

        var again = await JoinAsync(id, Shotgun);

        (again.Result.MessageKey, again.Result.Args[0], again.RefreshCard).Should().Be(("lfg.join.already_waitlisted", (object)2, false));
        var rows = (await StoredAsync(id)).Rows;
        rows.Should().HaveCount(13);
        rows.Single(p => p.UserId == Shotgun).WaitlistOrder.Should().Be(before, "the place is assigned once");

        await LeaveAsync(id, Arif);
        (await JoinAsync(id, Shotgun)).Result.Args.Should().Equal([1], "the position is computed from the live queue, not stored");
        await InvariantAsync(id);
    }

    [Fact]
    public async Task A_maybe_who_presses_join_on_a_full_team_goes_to_the_end_of_the_line()
    {
        var id = await FullTeamWithThreeWaitingAsync();
        await MaybeAsync(id, 300);

        var committed = await JoinAsync(id, 300);

        (committed.Result.MessageKey, committed.Result.Args[0]).Should().Be(("lfg.join.waitlisted", (object)4));
        committed.Listing!.MaybePlayers.Should().BeEmpty();
        committed.Listing.WaitlistedPlayers.Should().Equal(Users(Arif, Shotgun, Hasom, 300));
        await InvariantAsync(id);
    }

    // ---- automatic promotion ----

    [Fact]
    public async Task A_player_who_leaves_is_replaced_by_the_first_in_line_in_the_same_write()
    {
        var id = await FullTeamWithThreeWaitingAsync();

        var left = await LeaveAsync(id, Oykeli);

        left.Result.MessageKey.Should().Be("lfg.leave.done");
        var view = left.Listing!;
        (view.Players.Count, view.Status).Should().Be((10, LfgStatus.Full), "the card is drawn from the final state: never a free slot while someone waits");
        view.Players.Should().Contain(new UserId(Arif)).And.NotContain(new UserId(Oykeli));
        view.WaitlistedPlayers.Should().Equal(Users(Shotgun, Hasom));
        var card = _host.Services.GetRequiredService<LfgCardRenderer>().Render(view, "tr");
        card.Embed!.Description.Should().Contain("👥 **10 / 10**").And.Contain("🎟️ **Bekleme Listesi (2)**\n`1.` <@202> · `2.` <@203>");
        var (_, rows) = await StoredAsync(id);
        rows.Single(p => p.UserId == Arif).WaitlistOrder.Should().BeNull();
        await InvariantAsync(id);
    }

    [Fact]
    public async Task A_player_who_switches_to_maybe_frees_the_slot_for_the_first_in_line()
    {
        var id = await FullTeamWithThreeWaitingAsync();

        var maybe = await MaybeAsync(id, 103);

        maybe.Result.MessageKey.Should().Be("lfg.maybe.done");
        var view = maybe.Listing!;
        (view.Players.Count, view.Status).Should().Be((10, LfgStatus.Full));
        view.Players.Should().Contain(new UserId(Arif));
        view.MaybePlayers.Should().Equal(Users(103));
        view.WaitlistedPlayers.Should().Equal(Users(Shotgun, Hasom));
        await InvariantAsync(id);
    }

    [Fact]
    public async Task Leaving_or_choosing_maybe_from_the_middle_of_the_line_keeps_everyone_elses_order()
    {
        var id = await FullTeamWithThreeWaitingAsync();

        (await MaybeAsync(id, Shotgun)).Listing!.WaitlistedPlayers.Should().Equal(Users(Arif, Hasom));
        (await StoredAsync(id)).Rows.Single(p => p.UserId == Shotgun).WaitlistOrder.Should().BeNull();
        (await JoinAsync(id, Hasom)).Result.Args.Should().Equal([2], "positions close up without rewriting anyone's place");

        (await JoinAsync(id, Shotgun)).Result.Args.Should().Equal([3], "coming back means the end of the line");
        var left = await LeaveAsync(id, Hasom);
        left.Result.MessageKey.Should().Be("lfg.leave.done_waitlist");
        left.Listing!.WaitlistedPlayers.Should().Equal(Users(Arif, Shotgun));
        (left.Listing.Players.Count, left.Listing.Status).Should().Be((10, LfgStatus.Full), "leaving the line frees no slot");
        await InvariantAsync(id);
    }

    [Theory]
    [InlineData(11, 11, new ulong[] { Shotgun, Hasom }, LfgStatus.Full)]
    [InlineData(12, 12, new ulong[] { Hasom }, LfgStatus.Full)]
    [InlineData(15, 13, new ulong[0], LfgStatus.Open)]
    public async Task A_larger_team_takes_the_waitlist_in_order_when_the_owner_saves(int size, int joined, ulong[] stillWaiting, LfgStatus status)
    {
        var id = await FullTeamWithThreeWaitingAsync();

        var edited = await EditPlayersAsync(id, size);

        edited.Result.MessageKey.Should().Be("lfg.edit.done");
        var view = edited.Listing!;
        (view.MaxPlayers, view.Players.Count, view.Status).Should().Be((size, joined, status));
        view.WaitlistedPlayers.Should().Equal(Users(stillWaiting));
        view.Players.Should().Contain(Users(new[] { Arif, Shotgun, Hasom }.Except(stillWaiting).ToArray()));
        (await StoredAsync(id)).Listing.CardStale.Should().BeTrue("the card is redrawn with the promoted players");
        await InvariantAsync(id);
    }

    [Fact]
    public async Task Three_new_slots_take_the_first_three_of_five_waiting_and_the_other_two_keep_their_places()
    {
        var listing = await OpenAsync(10);
        await FillAsync(listing.Id, 9, Oykeli);
        await QueueAsync(listing.Id, 201, 202, 203, 204, 205);
        var stored = (await StoredAsync(listing.Id)).Rows.Where(p => p.UserId is 204 or 205).ToDictionary(p => p.UserId, p => p.WaitlistOrder);

        var edited = await EditPlayersAsync(listing.Id, 13);

        var view = edited.Listing!;
        (view.Players.Count, view.Status).Should().Be((13, LfgStatus.Full));
        view.Players.Should().Contain(Users(201, 202, 203));
        view.WaitlistedPlayers.Should().Equal(Users(204, 205));
        var after = (await StoredAsync(listing.Id)).Rows.Where(p => p.UserId is 204 or 205).ToDictionary(p => p.UserId, p => p.WaitlistOrder);
        after.Should().Equal(stored, "the remaining places are not rewritten");
        (await JoinAsync(listing.Id, 204)).Result.Args.Should().Equal([1], "old #4 is now shown as #1");
        (await JoinAsync(listing.Id, 205)).Result.Args.Should().Equal([2]);
        _host.Services.GetRequiredService<LfgCardRenderer>().Render(view, "tr").Embed!.Description.Should().Contain("🎟️ **Bekleme Listesi (2)**\n`1.` <@204> · `2.` <@205>");
        await InvariantAsync(listing.Id);
    }

    [Fact]
    public async Task The_team_size_cannot_drop_below_the_joined_players_and_the_waitlist_does_not_count()
    {
        var listing = await OpenAsync(10);
        await FillAsync(listing.Id, 9, Oykeli);
        await QueueAsync(listing.Id, 201, 202, 203, 204, 205);

        (await EditPlayersAsync(listing.Id, 10)).Result.Succeeded.Should().BeTrue("10 joined, 5 waiting: 10 is valid");
        var refused = await EditPlayersAsync(listing.Id, 9);

        (refused.Result.MessageKey, refused.Result.Args[0]).Should().Be(("lfg.edit.players_below_joined", (object)10));
        (await GetAsync(listing.Id))!.WaitlistedPlayers.Should().HaveCount(5, "nobody is demoted or dropped");
        await InvariantAsync(listing.Id);
    }

    // ---- FIFO ----

    [Fact]
    public async Task The_line_is_the_stored_order_of_arrival_not_a_timestamp_or_an_id_and_it_survives_a_restart()
    {
        var listing = await OpenAsync(2);
        await FillAsync(listing.Id, 1, 50); // 2 / 2
        // The clock does not move: all four arrive at the same instant, with DESCENDING ids.
        await QueueAsync(listing.Id, 900, 800, 700, 600);

        var (_, rows) = await StoredAsync(listing.Id);
        rows.Where(p => p.Response == LfgResponse.Waitlisted).Select(p => p.JoinedAt).Distinct().Should().ContainSingle("same timestamp for everyone");
        rows.Where(p => p.Response == LfgResponse.Waitlisted).OrderBy(p => p.WaitlistOrder).Select(p => (p.UserId, p.WaitlistOrder))
            .Should().Equal((900UL, (long?)1), (800UL, (long?)2), (700UL, (long?)3), (600UL, (long?)4));

        await using var restarted = await TestHost.CreateAsync(new() { ["Bot:DataDirectory"] = _host.Directory });
        (await GetAsync(listing.Id, restarted))!.WaitlistedPlayers.Should().Equal(Users(900, 800, 700, 600));
        (await Lfg(s => s.LeaveAsync(User(50), listing.Id, Ct), restarted)).Listing!.Players.Should().Contain(new UserId(900),
            "the first to arrive moves up, not the lowest id");
    }

    // ---- races ----

    [Fact]
    public async Task Twenty_simultaneous_joins_on_a_full_team_get_twenty_different_places()
    {
        var listing = await OpenAsync(4);
        await FillAsync(listing.Id, 3, 100);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => JoinAsync(listing.Id, 500 + (ulong)i))));

        results.Should().OnlyContain(r => r.Result.MessageKey == "lfg.join.waitlisted");
        var places = results.Select(r => (int)r.Result.Args[0]!).ToList();
        places.Should().BeEquivalentTo(Enumerable.Range(1, 20));
        // Each answer is that player's real place in the stored line.
        var line = (await GetAsync(listing.Id))!.WaitlistedPlayers;
        for (var i = 0; i < 20; i++)
            line[places[i] - 1].Should().Be(new UserId(500 + (ulong)i));
        await InvariantAsync(listing.Id);
    }

    [Fact]
    public async Task Two_simultaneous_leaves_promote_the_first_two_in_line()
    {
        for (var round = 0; round < 4; round++)
        {
            var id = await FullTeamWithThreeWaitingAsync(owner: 20 + (ulong)round, firstPlayer: 1000 + ((ulong)round * 100), firstWaiting: 2000 + ((ulong)round * 100));
            var first = 2000 + ((ulong)round * 100);

            await Task.WhenAll(Task.Run(() => LeaveAsync(id, 1000 + ((ulong)round * 100))), Task.Run(() => LeaveAsync(id, 1001 + ((ulong)round * 100))));

            var view = (await GetAsync(id))!;
            (view.Players.Count, view.Status).Should().Be((10, LfgStatus.Full));
            view.Players.Should().Contain(Users(first, first + 1));
            view.WaitlistedPlayers.Should().Equal(Users(first + 2));
            await InvariantAsync(id);
        }
    }

    [Fact]
    public async Task A_newcomer_racing_a_leave_never_passes_the_one_already_waiting()
    {
        for (var round = 0; round < 6; round++)
        {
            var owner = 30 + (ulong)round;
            var listing = await OpenAsync(4, owner);
            var baseUser = 3000 + ((ulong)round * 100);
            await FillAsync(listing.Id, 3, baseUser); // 4 / 4
            await QueueAsync(listing.Id, baseUser + 50); // waiting first

            await Task.WhenAll(Task.Run(() => LeaveAsync(listing.Id, baseUser)), Task.Run(() => JoinAsync(listing.Id, baseUser + 60)));

            var view = (await GetAsync(listing.Id))!;
            view.Players.Should().Contain(new UserId(baseUser + 50), "whatever the order of the two writes, the earlier waiter gets the slot");
            view.WaitlistedPlayers.Should().Equal(Users(baseUser + 60));
            await InvariantAsync(listing.Id);
        }
    }

    [Fact]
    public async Task A_join_racing_the_owners_larger_size_keeps_the_line_and_never_overbooks()
    {
        var id = await FullTeamWithThreeWaitingAsync();

        await Task.WhenAll(Task.Run(() => EditPlayersAsync(id, 12)), Task.Run(() => JoinAsync(id, 400)));

        var view = (await GetAsync(id))!;
        (view.MaxPlayers, view.Players.Count, view.Status).Should().Be((12, 12, LfgStatus.Full));
        view.Players.Should().Contain(Users(Arif, Shotgun));
        view.WaitlistedPlayers.Should().Equal(Users(Hasom, 400), "the newcomer is behind everyone who waited before");
        await InvariantAsync(id);
    }

    [Fact]
    public async Task A_double_click_on_a_full_team_takes_one_place_once()
    {
        var listing = await OpenAsync(2);
        await FillAsync(listing.Id, 1, 50);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => JoinAsync(listing.Id, 60))));

        results.Count(r => r.Result.MessageKey == "lfg.join.waitlisted").Should().Be(1);
        results.Count(r => r.Result.MessageKey == "lfg.join.already_waitlisted").Should().Be(3);
        results.Should().OnlyContain(r => (int)r.Result.Args[0]! == 1);
        (await StoredAsync(listing.Id)).Rows.Single(p => p.UserId == 60).WaitlistOrder.Should().Be(1);
        await InvariantAsync(listing.Id);
    }

    [Fact]
    public async Task A_long_random_mix_of_answers_and_size_changes_never_breaks_the_invariant()
    {
        var random = new Random(20260928);
        var listing = await OpenAsync(4);
        for (var step = 0; step < 150; step++)
        {
            var user = 700 + (ulong)random.Next(12);
            switch (random.Next(10))
            {
                case < 4:
                    await JoinAsync(listing.Id, user);
                    break;
                case < 6:
                    await MaybeAsync(listing.Id, user);
                    break;
                case < 9:
                    await LeaveAsync(listing.Id, user);
                    break;
                default:
                    await EditPlayersAsync(listing.Id, random.Next(2, 9)); // refused when below the joined players
                    break;
            }

            await InvariantAsync(listing.Id);
        }
    }

    // ---- notices and voice ----

    [Fact]
    public async Task Only_joined_players_are_pinged_and_a_promoted_player_is_pinged_from_then_on_without_a_repeat()
    {
        var listing = await OpenAsync(3, startInMinutes: 120, notices: true);
        await FillAsync(listing.Id, 2, 20); // owner, 20, 21
        await QueueAsync(listing.Id, 30, 31);
        await MaybeAsync(listing.Id, 40);

        await LeaveAsync(listing.Id, 20); // before the reminder: 30 moves up
        _host.Clock.Advance(TimeSpan.FromMinutes(90)); // EventAt - 30 min
        await TickAsync();
        await TickAsync();
        var reminder = Delivered().Should().ContainSingle().Subject;
        Pinged(reminder).Should().BeEquivalentTo([Owner, 21UL, 30UL], "31 waits and 40 is a Maybe: neither is pinged");

        await LeaveAsync(listing.Id, 21); // after the reminder: 31 moves up, gets no late reminder
        _host.Clock.Advance(TimeSpan.FromMinutes(30)); // EventAt
        await TickAsync();
        await TickAsync();
        await TickAsync();
        Delivered().Should().HaveCount(2, "one reminder and one start notice, nothing repeated");
        Pinged(Delivered()[1]).Should().BeEquivalentTo([Owner, 30UL, 31UL]);
        await InvariantAsync(listing.Id);
    }

    [Fact]
    public async Task A_waiting_player_cannot_use_the_voice_button_until_promoted()
    {
        var listing = await OpenAsync(2, voice: Voice);
        await FillAsync(listing.Id, 1, 20);
        await QueueAsync(listing.Id, 30);

        (await Lfg(s => s.VoiceAsync(User(30), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.voice.waitlisted");
        await LeaveAsync(listing.Id, 20);
        var promoted = await Lfg(s => s.VoiceAsync(User(30), listing.Id, Ct));
        (promoted.Result.Succeeded, promoted.Result.MessageKey).Should().Be((true, "lfg.voice.open"));
    }

    private async Task TickAsync()
    {
        await _host.Services.GetRequiredService<LfgExpiryWorker>().RunOnceAsync(Ct);
        var processor = _host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    private List<FakeMessageTransport.FakeMessage> Delivered() => _host.Transport.Messages.Where(m => m.Message.Content is not null).ToList();

    private static IEnumerable<ulong> Pinged(FakeMessageTransport.FakeMessage m) => m.Message.Mentions.Users?.Select(u => u.Value) ?? [];

    // ---- privacy and terminal listings ----

    [Fact]
    public async Task Privacy_exports_the_waitlist_and_deleting_a_player_keeps_the_invariant()
    {
        var listing = await OpenAsync(2);
        await FillAsync(listing.Id, 1, 20); // full
        await QueueAsync(listing.Id, 30, 31);
        await MaybeAsync(listing.Id, 40);

        await _host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg");
            var export = await data.ExportAsync(Guild, new UserId(30), Ct);
            export["joinedListings"]![0]!["response"]!.GetValue<string>().Should().Be("Waitlisted");
            await data.DeleteAsync(Guild, new UserId(30), Ct); // waiting: leaves the line
        });
        var afterWaiting = (await GetAsync(listing.Id))!;
        (afterWaiting.Players.Count, afterWaiting.Status).Should().Be((2, LfgStatus.Full));
        afterWaiting.WaitlistedPlayers.Should().Equal(Users(31));
        await InvariantAsync(listing.Id);

        await _host.InScopeAsync(sp => sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg").DeleteAsync(Guild, new UserId(20), Ct));
        var afterJoined = (await GetAsync(listing.Id))!;
        afterJoined.Players.Should().Equal(Users(Owner, 31), "the freed slot went to the first in line");
        (afterJoined.Status, afterJoined.WaitlistedPlayers.Count).Should().Be((LfgStatus.Full, 0));
        (await StoredAsync(listing.Id)).Listing.CardStale.Should().BeTrue();
        await InvariantAsync(listing.Id);

        await _host.InScopeAsync(sp => sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg").DeleteAsync(Guild, new UserId(40), Ct));
        (await GetAsync(listing.Id))!.MaybePlayers.Should().BeEmpty();
        await InvariantAsync(listing.Id);
    }

    [Fact]
    public async Task A_closed_listing_never_promotes_and_refuses_every_answer()
    {
        var listing = await OpenAsync(2);
        await FillAsync(listing.Id, 1, 20);
        await QueueAsync(listing.Id, 30);
        (await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.done");

        (await LeaveAsync(listing.Id, 20)).Result.MessageKey.Should().Be("lfg.closed");
        (await JoinAsync(listing.Id, 31)).Result.MessageKey.Should().Be("lfg.closed");
        (await MaybeAsync(listing.Id, 30)).Result.MessageKey.Should().Be("lfg.closed");
        await _host.InScopeAsync(sp => sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg").DeleteAsync(Guild, new UserId(20), Ct));

        var view = (await GetAsync(listing.Id))!;
        view.Status.Should().Be(LfgStatus.Closed);
        view.Players.Should().Equal(Users(Owner), "the deleted player is gone");
        view.WaitlistedPlayers.Should().Equal(Users(30), "but nobody is promoted in a closed listing");
    }

    // ---- schema upgrade ----

    [Fact]
    public async Task Existing_players_keep_their_answers_and_statuses_after_the_waitlist_migration()
    {
        var path = Path.Combine(_host.Directory, "upgrade.db");
        var options = new DbContextOptionsBuilder<ToroDbContext>()
            .UseSqlite(DatabaseMaintenance.ConnectionString(path), o => o.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
            .Options;
        await using (var db = new ToroDbContext(options, DesignTimeDbContextFactory.AllContributors()))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260927222930_BirthdayModule", Ct); // the production schema before the waitlist
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO lfg_listing (Id, GuildId, ChannelId, OwnerUserId, GameName, MaxPlayers, Status, CreatedAt, ExpiresAt, CardStale, CardSyncAttempts, Version, " +
                "NotifyBeforeStart, NotifyAtStart, ReminderState, StartNoticeState) VALUES " +
                "(1, 991, 9901, 10, 'Deadlock', 2, 1, 0, 1, 0, 0, 3, 0, 0, 0, 0), (2, 991, 9901, 11, 'Valheim', 5, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0);" +
                "INSERT INTO lfg_participant (ListingId, UserId, Response, JoinedAt) VALUES (1, 10, 0, 0), (1, 20, 0, 0), (1, 30, 1, 0), (2, 11, 0, 0), (2, 40, 1, 0);", Ct);
            await db.Database.MigrateAsync(Ct); // -> latest
            (await db.Database.GetPendingMigrationsAsync(Ct)).Should().BeEmpty();

            var rows = await db.Set<LfgParticipantEntity>().AsNoTracking().OrderBy(p => p.ListingId).ThenBy(p => p.UserId).ToListAsync(Ct);
            rows.Select(p => (p.ListingId, p.UserId, p.Response, p.WaitlistOrder)).Should().Equal(
                (1L, 10UL, LfgResponse.Joined, (long?)null), (1L, 20UL, LfgResponse.Joined, (long?)null), (1L, 30UL, LfgResponse.Maybe, (long?)null),
                (2L, 11UL, LfgResponse.Joined, (long?)null), (2L, 40UL, LfgResponse.Maybe, (long?)null));
            var listings = await db.Set<LfgListingEntity>().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Status, x.Version }).ToListAsync(Ct);
            listings.Select(x => (x.Status, x.Version)).Should().Equal((LfgStatus.Full, 3L), (LfgStatus.Open, 1L));
            var check = db.Database.GetDbConnection();
            await check.OpenAsync(Ct);
            await using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check;";
                (await cmd.ExecuteScalarAsync(Ct)).Should().Be("ok");
            }

            await check.CloseAsync();
        }

        using var connection = new SqliteConnection(DatabaseMaintenance.ConnectionString(path));
        SqliteConnection.ClearPool(connection);
    }
}
