using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ LFG against the real SQLite database and the production service wiring: create → join → leave → full → reopen →
/// close → expire, restart recovery, deleted card messages, privacy, and the races that must never overbook a listing.
/// </summary>
public sealed class LfgLifecycleTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(777);
    private static readonly GuildId OtherGuild = new(778);
    private static readonly ChannelId Channel = new(7001);
    private static readonly ChannelId OtherChannel = new(7002);
    private const ulong Owner = 10;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        await EnableAsync(_host);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static async Task EnableAsync(TestHost host)
    {
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, F1TestHostExtensions.ChannelPermissions));
        host.Guilds.SetChannel(Guild, OtherChannel, new BotChannelAccess(true, true, F1TestHostExtensions.ChannelPermissions));
        await host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "lfg", true, Ct)).Succeeded.Should().BeTrue());
    }

    private static ActorContext User(ulong id, GuildId? guild = null) =>
        new(guild ?? Guild, new UserId(id), GuildPermission.ViewChannel | GuildPermission.SendMessages, [], false, 1);

    private static ActorContext Moderator(ulong id) =>
        new(Guild, new UserId(id), GuildPermission.ViewChannel | GuildPermission.ManageMessages, [], false, 5);

    private Task<T> Lfg<T>(Func<LfgService, Task<T>> action, TestHost? host = null) =>
        (host ?? _host).InScopeAsync(sp => action(sp.GetRequiredService<LfgService>()));

    private Task<LfgResult> JoinAsync(long id, ulong user, TestHost? host = null) => Lfg(s => s.JoinAsync(User(user), id, Ct), host);

    private Task<LfgResult> LeaveAsync(long id, ulong user) => Lfg(s => s.LeaveAsync(User(user), id, Ct));

    private Task<LfgListingView?> GetAsync(long id, TestHost? host = null) => Lfg(s => s.GetAsync(id, Ct), host);

    private Task<LfgResult> CreateAsync(ulong owner = Owner, int players = 6, string game = "Deadlock", string? details = "Casual oynayacağız", int? minutes = null,
        ChannelId? channel = null) =>
        Lfg(s => s.CreateAsync(User(owner), channel ?? Channel, game, players, details, minutes, Ct));

    /// <summary>Opens a listing and posts its card the way the /ekip response does (a message the bot can later edit).</summary>
    private async Task<LfgListingView> OpenAsync(ulong owner = Owner, int players = 6, string game = "Deadlock", int? minutes = null)
    {
        var created = await CreateAsync(owner, players, game, minutes: minutes);
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

    private async Task FillAsync(long id, int players, ulong firstUser = 100)
    {
        for (var i = 0; i < players; i++)
            (await JoinAsync(id, firstUser + (ulong)i)).Result.MessageKey.Should().Be("lfg.join.done");
    }

    private Task<LfgListingEntity> RowAsync(long id, TestHost? host = null) => (host ?? _host).InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<LfgListingEntity>().AsNoTracking().SingleAsync(x => x.Id == id));

    private Task<int> ParticipantRowsAsync(long id) => _host.InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<LfgParticipantEntity>().CountAsync(p => p.ListingId == id));

    private Task WorkerPassAsync(TestHost? host = null) => (host ?? _host).Services.GetRequiredService<LfgExpiryWorker>().RunOnceAsync(Ct);

    private FakeMessageTransport.FakeMessage Card(LfgListingView listing, TestHost? host = null) =>
        (host ?? _host).Transport.Messages.Single(m => m.Id == listing.Message);

    // ---- create ----

    [Fact]
    public async Task Create_opens_a_listing_with_the_owner_as_first_player_and_the_default_two_hour_expiry()
    {
        var result = await CreateAsync();

        result.Result.Succeeded.Should().BeTrue();
        var listing = result.Listing!;
        listing.Status.Should().Be(LfgStatus.Open);
        listing.Owner.Should().Be(new UserId(Owner));
        listing.Players.Should().Equal(new UserId(Owner));
        listing.GameName.Should().Be("Deadlock");
        listing.Details.Should().Be("Casual oynayacağız");
        listing.CreatedAt.Should().Be(TestHost.T0);
        listing.ExpiresAt.Should().Be(TestHost.T0.AddHours(2));
        listing.Message.Should().BeNull("the card id is recorded once Discord confirms the /ekip response");
        (await ParticipantRowsAsync(listing.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Create_uses_the_chosen_duration()
    {
        (await CreateAsync(minutes: 60)).Listing!.ExpiresAt.Should().Be(TestHost.T0.AddHours(1));
        (await CreateAsync(owner: 11, minutes: 180)).Listing!.ExpiresAt.Should().Be(TestHost.T0.AddHours(3));
    }

    [Theory]
    [InlineData("X", 6, null, null, "lfg.create.game_too_short")]
    [InlineData("  ", 6, null, null, "lfg.create.game_too_short")]
    [InlineData("A game name that is definitely far too long for any card!", 6, null, null, "lfg.create.game_too_long")]
    [InlineData("Deadlock", 1, null, null, "lfg.create.players_range")]
    [InlineData("Deadlock", 21, null, null, "lfg.create.players_range")]
    [InlineData("Deadlock", 5000, null, null, "lfg.create.players_range")]
    [InlineData("Deadlock", 6, 45, null, "lfg.create.duration_invalid")]
    [InlineData("Deadlock", 6, null, 201, "lfg.create.details_too_long")]
    public async Task Invalid_input_is_refused_without_creating_anything(string game, int players, int? minutes, int? detailsLength, string key)
    {
        var result = await CreateAsync(game: game, players: players, minutes: minutes, details: detailsLength is { } n ? new string('x', n) : null);

        result.Result.Succeeded.Should().BeFalse();
        result.Result.MessageKey.Should().Be(key);
        result.Result.TraceCode.Should().BeNull("a user input mistake is not an incident");
        (await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<LfgListingEntity>().CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_user_can_have_at_most_two_active_listings_per_guild()
    {
        (await CreateAsync(game: "CS2")).Result.Succeeded.Should().BeTrue();
        var second = (await CreateAsync(game: "Valheim")).Listing!;

        var third = await CreateAsync(game: "WoW");
        third.Result.Succeeded.Should().BeFalse();
        third.Result.MessageKey.Should().Be("lfg.create.limit");
        third.Result.Args.Should().Equal(2);

        (await CreateAsync(owner: 11)).Result.Succeeded.Should().BeTrue("the limit is per user");
        (await Lfg(s => s.CreateAsync(User(Owner, OtherGuild), Channel, "WoW", 5, null, null, Ct))).Result.Succeeded.Should().BeTrue("and per guild");

        (await Lfg(s => s.CloseAsync(User(Owner), second.Id, Ct))).Result.Succeeded.Should().BeTrue();
        (await CreateAsync(game: "WoW")).Result.Succeeded.Should().BeTrue("a closed listing frees its slot");
    }

    [Fact]
    public async Task Expired_listings_do_not_count_against_the_limit_even_before_the_worker_ran()
    {
        await CreateAsync(minutes: 60);
        await CreateAsync(minutes: 60);
        _host.Clock.Advance(TimeSpan.FromMinutes(61));

        (await CreateAsync()).Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_configured_listing_channel_restricts_ekip_and_can_be_cleared()
    {
        await _host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<LfgConfigService>();
            (await config.SetChannelAsync(User(12), Channel.Value, Ct)).Succeeded.Should().BeFalse("Manage Server is required");
            (await config.SetChannelAsync(TestHost.Admin(Guild), 9999, Ct)).MessageKey.Should().Be("lfg.config.channel_invalid");
            (await config.SetChannelAsync(TestHost.Admin(Guild), Channel.Value, Ct)).MessageKey.Should().Be("lfg.config.channel_saved");
        });

        var elsewhere = await CreateAsync(channel: OtherChannel);
        elsewhere.Result.MessageKey.Should().Be("lfg.create.wrong_channel");
        elsewhere.Result.Args.Should().Equal("<#7001>");
        (await CreateAsync()).Result.Succeeded.Should().BeTrue();

        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<LfgConfigService>().SetChannelAsync(TestHost.Admin(Guild), null, Ct)).MessageKey.Should().Be("lfg.config.channel_cleared"));
        (await CreateAsync(owner: 11, channel: OtherChannel)).Result.Succeeded.Should().BeTrue();
    }

    // ---- join ----

    [Fact]
    public async Task Join_adds_the_player_and_redraws_the_card()
    {
        var listing = await OpenAsync();

        var result = await JoinAsync(listing.Id, 20);

        result.Result.MessageKey.Should().Be("lfg.join.done");
        result.RefreshCard.Should().BeTrue();
        result.Listing!.Players.Should().Equal(new UserId(Owner), new UserId(20));
        result.Listing.Status.Should().Be(LfgStatus.Open);
        result.Listing.Version.Should().BeGreaterThan(listing.Version, "a card drawn from an older state is detectable");
    }

    [Fact]
    public async Task Joining_twice_or_joining_your_own_listing_changes_nothing()
    {
        var listing = await OpenAsync();
        await JoinAsync(listing.Id, 20);

        var again = await JoinAsync(listing.Id, 20);
        again.Result.Succeeded.Should().BeFalse();
        again.Result.MessageKey.Should().Be("lfg.join.already");
        again.RefreshCard.Should().BeFalse();
        (await JoinAsync(listing.Id, Owner)).Result.MessageKey.Should().Be("lfg.join.already");
        (await ParticipantRowsAsync(listing.Id)).Should().Be(2);
    }

    [Fact]
    public async Task The_last_slot_makes_the_listing_full_and_further_joins_go_to_the_waitlist()
    {
        var listing = await OpenAsync(players: 3);
        await JoinAsync(listing.Id, 20);

        var last = await JoinAsync(listing.Id, 21);
        last.Result.MessageKey.Should().Be("lfg.join.done");
        last.Listing!.Status.Should().Be(LfgStatus.Full);
        last.Listing.Players.Should().HaveCount(3);

        var late = await JoinAsync(listing.Id, 22);
        late.Result.MessageKey.Should().Be("lfg.join.waitlisted");
        late.Result.Args.Should().Equal(1);
        late.RefreshCard.Should().BeTrue("the card shows the waitlist");
        (late.Listing!.Players.Count, late.Listing.Status).Should().Be((3, LfgStatus.Full));
        late.Listing.WaitlistedPlayers.Should().Equal(new UserId(22));
    }

    [Fact]
    public async Task Joining_an_expired_listing_is_refused_and_expires_it_right_away()
    {
        var listing = await OpenAsync(minutes: 60);
        _host.Clock.Advance(TimeSpan.FromMinutes(60));

        var result = await JoinAsync(listing.Id, 20);

        result.Result.MessageKey.Should().Be("lfg.expired");
        result.RefreshCard.Should().BeTrue();
        result.Listing!.Status.Should().Be(LfgStatus.Expired);
        result.Listing.Players.Should().Equal(new UserId(Owner));
        (await RowAsync(listing.Id)).CardStale.Should().BeFalse("the clicked card is redrawn by the interaction itself");
    }

    [Fact]
    public async Task Joining_a_closed_listing_is_refused()
    {
        var listing = await OpenAsync();
        await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));

        var result = await JoinAsync(listing.Id, 20);

        result.Result.MessageKey.Should().Be("lfg.closed");
        (await ParticipantRowsAsync(listing.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_listing_of_another_guild_is_invisible()
    {
        var listing = await OpenAsync();

        var result = await Lfg(s => s.JoinAsync(User(20, OtherGuild), listing.Id, Ct));

        result.Result.MessageKey.Should().Be("lfg.not_found");
        (await ParticipantRowsAsync(listing.Id)).Should().Be(1);
        (await Lfg(s => s.JoinAsync(User(20), 987654, Ct))).Result.MessageKey.Should().Be("lfg.not_found");
    }

    // ---- leave ----

    [Fact]
    public async Task Leave_removes_the_player()
    {
        var listing = await OpenAsync();
        await JoinAsync(listing.Id, 20);

        var result = await LeaveAsync(listing.Id, 20);

        result.Result.MessageKey.Should().Be("lfg.leave.done");
        result.Listing!.Players.Should().Equal(new UserId(Owner));
    }

    [Fact]
    public async Task Leave_by_a_non_member_or_by_the_owner_is_refused()
    {
        var listing = await OpenAsync();

        (await LeaveAsync(listing.Id, 20)).Result.MessageKey.Should().Be("lfg.leave.not_member");
        var owner = await LeaveAsync(listing.Id, Owner);
        owner.Result.MessageKey.Should().Be("lfg.leave.owner");
        owner.Listing!.Players.Should().Equal(new UserId(Owner));
    }

    [Fact]
    public async Task Leaving_a_full_listing_reopens_it_and_the_card_offers_join_again()
    {
        var listing = await OpenAsync(players: 2);
        (await JoinAsync(listing.Id, 20)).Listing!.Status.Should().Be(LfgStatus.Full);

        var result = await LeaveAsync(listing.Id, 20);

        result.Listing!.Status.Should().Be(LfgStatus.Open);
        var card = _host.Services.GetRequiredService<LfgCardRenderer>().Render(result.Listing, "tr");
        card.Buttons![0].Disabled.Should().BeFalse();
        (await JoinAsync(listing.Id, 21)).Result.MessageKey.Should().Be("lfg.join.done");
    }

    [Fact]
    public async Task Leaving_after_expiry_is_refused()
    {
        var listing = await OpenAsync();
        await JoinAsync(listing.Id, 20);
        _host.Clock.Advance(TimeSpan.FromHours(3));

        (await LeaveAsync(listing.Id, 20)).Result.MessageKey.Should().Be("lfg.expired");
        (await ParticipantRowsAsync(listing.Id)).Should().Be(2);
    }

    // ---- close ----

    [Fact]
    public async Task The_owner_closes_the_listing_and_the_card_is_redrawn_closed_without_deleting_the_message()
    {
        var listing = await OpenAsync();
        await JoinAsync(listing.Id, 20);

        (await Lfg(s => s.CheckCloseAsync(User(Owner), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.question");
        var result = await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));

        result.Result.MessageKey.Should().Be("lfg.close.done");
        var row = await RowAsync(listing.Id);
        row.Status.Should().Be(LfgStatus.Closed);
        row.ClosedAt.Should().Be(TestHost.T0);
        row.ClosedByUserId.Should().Be(Owner);
        row.CardStale.Should().BeTrue();

        (await _host.InScopeAsync(sp => sp.GetRequiredService<LfgCardSync>().SyncAsync(listing.Id, Ct))).Should().Be(LfgCardSyncOutcome.Updated);
        var edited = Card(listing).Edits.Should().ContainSingle().Subject;
        edited.Embed!.Description.Should().Contain("🔒 **İlan kapatıldı**").And.Contain("<@10> · <@20>");
        edited.Buttons!.Should().OnlyContain(b => b.Disabled);
        edited.Mentions.PingsAnything.Should().BeFalse();
        (await RowAsync(listing.Id)).CardStale.Should().BeFalse();
    }

    [Fact]
    public async Task Another_member_cannot_close_the_listing()
    {
        var listing = await OpenAsync();
        await JoinAsync(listing.Id, 20);

        (await Lfg(s => s.CheckCloseAsync(User(20), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.forbidden");
        var result = await Lfg(s => s.CloseAsync(User(20), listing.Id, Ct));

        result.Result.Succeeded.Should().BeFalse();
        result.Result.MessageKey.Should().Be("lfg.close.forbidden");
        (await RowAsync(listing.Id)).Status.Should().Be(LfgStatus.Open);
    }

    [Fact]
    public async Task Moderators_and_administrators_can_close_any_listing()
    {
        var first = await OpenAsync();
        var second = await OpenAsync(game: "CS2");
        var administrator = new ActorContext(Guild, new UserId(31), GuildPermission.Administrator, [], false, 9);

        (await Lfg(s => s.CloseAsync(Moderator(30), first.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.done");
        (await Lfg(s => s.CloseAsync(administrator, second.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.done");
        (await RowAsync(first.Id)).ClosedByUserId.Should().Be(30);
        var third = await OpenAsync(owner: 12);
        (await Lfg(s => s.CloseAsync(TestHost.Admin(Guild, 32), third.Id, Ct))).Result.MessageKey
            .Should().Be("lfg.close.forbidden", "Manage Server alone is server configuration, not message moderation");
    }

    [Fact]
    public async Task Closing_twice_is_idempotent()
    {
        var listing = await OpenAsync();
        await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));
        var version = (await RowAsync(listing.Id)).Version;

        var again = await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));

        again.Result.Succeeded.Should().BeTrue();
        again.Result.MessageKey.Should().Be("lfg.close.already");
        (await RowAsync(listing.Id)).Version.Should().Be(version);
    }

    // ---- expiry worker ----

    [Fact]
    public async Task The_worker_expires_due_listings_redraws_their_cards_and_a_second_pass_changes_nothing()
    {
        var due = await OpenAsync(minutes: 60);
        var full = await OpenAsync(owner: 11, players: 2, minutes: 60);
        await JoinAsync(full.Id, 20);
        var later = await OpenAsync(owner: 12, minutes: 180);
        _host.Clock.Advance(TimeSpan.FromMinutes(61));

        await WorkerPassAsync();

        foreach (var id in new[] { due.Id, full.Id })
        {
            var row = await RowAsync(id);
            row.Status.Should().Be(LfgStatus.Expired);
            row.ClosedAt.Should().Be(TestHost.T0.AddHours(1));
            row.CardStale.Should().BeFalse();
        }

        var edit = Card(due).Edits.Should().ContainSingle().Subject;
        edit.Embed!.Description.Should().EndWith("⏰ Bu ekip ilanının süresi doldu.");
        edit.Buttons!.Should().OnlyContain(b => b.Disabled);
        Card(full).Edits.Should().ContainSingle();
        (await RowAsync(later.Id)).Status.Should().Be(LfgStatus.Open);
        Card(later).Edits.Should().BeEmpty();

        var edits = _host.Transport.EditCalls;
        await WorkerPassAsync();
        _host.Transport.EditCalls.Should().Be(edits, "nothing is re-edited");
        (await RowAsync(due.Id)).Status.Should().Be(LfgStatus.Expired);
    }

    [Fact]
    public async Task A_deleted_card_message_orphans_the_listing_instead_of_being_retried_forever()
    {
        var listing = await OpenAsync(minutes: 60);
        _host.Transport.DeleteMessage(listing.Message!.Value);
        _host.Clock.Advance(TimeSpan.FromMinutes(61));

        await WorkerPassAsync();

        var row = await RowAsync(listing.Id);
        row.Status.Should().Be(LfgStatus.Orphaned);
        row.CardStale.Should().BeFalse();
        var edits = _host.Transport.EditCalls;
        await WorkerPassAsync();
        _host.Transport.EditCalls.Should().Be(edits);
        (await CreateAsync(game: "Next")).Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Failed_card_edits_are_retried_and_then_given_up()
    {
        var retried = await OpenAsync(minutes: 60);
        _host.Clock.Advance(TimeSpan.FromMinutes(61));
        _host.Transport.ScriptEdit(() => new SendOutcome.Transient("502"));

        await WorkerPassAsync();
        (await RowAsync(retried.Id)).CardStale.Should().BeTrue();
        await WorkerPassAsync();
        (await RowAsync(retried.Id)).CardStale.Should().BeFalse();
        Card(retried).Edits.Should().ContainSingle();

        var hopeless = await OpenAsync(owner: 11, minutes: 60);
        _host.Clock.Advance(TimeSpan.FromMinutes(61));
        for (var i = 0; i < LfgCardSync.MaxAttempts; i++)
            _host.Transport.ScriptEdit(() => new SendOutcome.Permanent(PermanentFailureKind.MissingPermissions, "Missing Permissions"));
        for (var i = 0; i < LfgCardSync.MaxAttempts; i++)
            await WorkerPassAsync();

        var row = await RowAsync(hopeless.Id);
        row.CardStale.Should().BeFalse("bounded retries");
        row.Status.Should().Be(LfgStatus.Expired, "a permission problem is not a deleted message");
        var calls = _host.Transport.EditCalls;
        await WorkerPassAsync();
        _host.Transport.EditCalls.Should().Be(calls);
    }

    [Fact]
    public async Task A_listing_whose_card_id_is_unknown_still_expires_without_any_edit()
    {
        var created = (await CreateAsync(minutes: 60)).Listing!;
        _host.Clock.Advance(TimeSpan.FromMinutes(61));

        await WorkerPassAsync();

        var row = await RowAsync(created.Id);
        row.Status.Should().Be(LfgStatus.Expired);
        row.CardStale.Should().BeFalse();
        _host.Transport.EditCalls.Should().Be(0);
    }

    // ---- concurrency ----

    [Fact]
    public async Task Two_players_racing_for_the_last_slot_never_overbook_the_listing()
    {
        for (var round = 0; round < 5; round++)
        {
            var listing = await OpenAsync(owner: 50 + (ulong)round, players: 6, game: "Deadlock " + round);
            await FillAsync(listing.Id, 4, firstUser: 1000 + ((ulong)round * 100)); // 5 / 6

            var a = 2000 + ((ulong)round * 10);
            var results = await Task.WhenAll(Task.Run(() => JoinAsync(listing.Id, a)), Task.Run(() => JoinAsync(listing.Id, a + 1)));

            results.Select(r => r.Result.MessageKey).Should().BeEquivalentTo(["lfg.join.done", "lfg.join.waitlisted"]);
            var stored = (await GetAsync(listing.Id))!;
            stored.Players.Should().HaveCount(6);
            stored.WaitlistedPlayers.Should().ContainSingle("the one who lost the race waits first in line");
            stored.Status.Should().Be(LfgStatus.Full);
        }
    }

    [Fact]
    public async Task Many_simultaneous_joins_fill_exactly_the_free_slots()
    {
        var listing = await OpenAsync(players: 4);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => JoinAsync(listing.Id, 300 + (ulong)i))));

        results.Count(r => r.Result.MessageKey == "lfg.join.done").Should().Be(3);
        results.Where(r => r.Result.MessageKey == "lfg.join.waitlisted").Select(r => (int)r.Result.Args[0]!).Should().BeEquivalentTo(Enumerable.Range(1, 9),
            "nine different places, one each");
        (await ParticipantRowsAsync(listing.Id)).Should().Be(13);
        var stored = (await GetAsync(listing.Id))!;
        (stored.Players.Count, stored.WaitlistedPlayers.Count).Should().Be((4, 9));
        (await RowAsync(listing.Id)).Status.Should().Be(LfgStatus.Full);
    }

    [Fact]
    public async Task A_double_click_never_creates_a_duplicate_player()
    {
        var listing = await OpenAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => JoinAsync(listing.Id, 20))));

        results.Count(r => r.Result.MessageKey == "lfg.join.done").Should().Be(1);
        results.Count(r => r.Result.MessageKey == "lfg.join.already").Should().Be(3);
        (await ParticipantRowsAsync(listing.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Simultaneous_creates_respect_the_active_listing_limit()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(() => CreateAsync(game: "Game " + i))));

        results.Count(r => r.Result.Succeeded).Should().Be(2);
        results.Where(r => !r.Result.Succeeded).Should().OnlyContain(r => r.Result.MessageKey == "lfg.create.limit");
    }

    [Fact]
    public async Task The_database_itself_rejects_a_duplicate_participant()
    {
        var listing = await OpenAsync();

        var insert = () => _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Set<LfgParticipantEntity>().Add(new LfgParticipantEntity { ListingId = listing.Id, UserId = Owner, JoinedAt = TestHost.T0 });
            await db.SaveChangesAsync();
        });

        await insert.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UNIQUE constraint failed*");
    }

    // ---- restart ----

    [Fact]
    public async Task Listings_players_and_buttons_survive_a_restart_and_downtime_expiries_are_caught_up()
    {
        var active = await OpenAsync(minutes: 180);
        await JoinAsync(active.Id, 20);
        var expiring = await OpenAsync(owner: 11, minutes: 60);

        // "Restart": a new process on the same database and the same Discord channel, 90 minutes later.
        await using var second = await TestHost.CreateAsync(new() { ["Bot:DataDirectory"] = _host.Directory }, TestHost.T0.AddMinutes(90), services =>
        {
            services.AddSingleton(_host.Transport);
            services.AddSingleton<IMessageTransport>(_host.Transport);
        });

        var recovered = (await GetAsync(active.Id, second))!;
        recovered.Status.Should().Be(LfgStatus.Open);
        recovered.Players.Should().Equal(new UserId(Owner), new UserId(20));
        recovered.Message.Should().Be(active.Message);

        (await JoinAsync(active.Id, 21, second)).Result.MessageKey.Should().Be("lfg.join.done", "button custom ids carry only the listing id");

        await WorkerPassAsync(second);
        (await RowAsync(expiring.Id, second)).Status.Should().Be(LfgStatus.Expired);
        Card(expiring).Edits.Should().ContainSingle().Which.Embed!.Description.Should().Contain("süresi doldu");
        (await RowAsync(active.Id, second)).Status.Should().Be(LfgStatus.Open);
    }

    // ---- deleted cards: no message-delete events (Guilds intent only), so a throttled one-read reconciliation ----

    [Fact]
    public async Task A_card_deleted_in_discord_orphans_its_listing_at_the_next_reconciliation_and_frees_the_owner_slot()
    {
        var kept = await OpenAsync(game: "CS2");
        var deleted = await OpenAsync(game: "Valheim");
        (await CreateAsync(game: "WoW")).Result.MessageKey.Should().Be("lfg.create.limit");
        _host.Transport.DeleteMessage(deleted.Message!.Value);

        await WorkerPassAsync();

        var row = await RowAsync(deleted.Id);
        row.Status.Should().Be(LfgStatus.Orphaned);
        row.ClosedAt.Should().Be(TestHost.T0);
        row.CardStale.Should().BeFalse();
        (await RowAsync(kept.Id)).Status.Should().Be(LfgStatus.Open);
        _host.Transport.EditCalls.Should().Be(0, "a gone card is never edited");
        (await CreateAsync(game: "WoW")).Result.Succeeded.Should().BeTrue("an orphaned listing no longer counts as active");

        var reads = _host.Transport.PresenceCalls;
        await WorkerPassAsync();
        _host.Transport.PresenceCalls.Should().Be(reads, "active cards are read at most every five minutes");
        _host.Clock.Advance(LfgExpiryWorker.VerifyInterval);
        await WorkerPassAsync();
        _host.Transport.PresenceCalls.Should().Be(reads + 1, "only the remaining active card with a known message is read");
        (await RowAsync(deleted.Id)).Status.Should().Be(LfgStatus.Orphaned);
    }

    [Fact]
    public async Task When_the_bot_cannot_tell_whether_a_card_exists_nothing_is_orphaned()
    {
        var listing = await OpenAsync();
        _host.Transport.DeleteMessage(listing.Message!.Value);
        _host.Transport.ScriptedPresence = MessagePresence.Unknown; // no Read Message History, 5xx, gateway not ready …

        await WorkerPassAsync();

        (await RowAsync(listing.Id)).Status.Should().Be(LfgStatus.Open);
    }

    [Fact]
    public async Task At_the_limit_only_the_callers_own_cards_are_checked_at_once()
    {
        await OpenAsync(game: "CS2");
        var second = await OpenAsync(game: "Valheim");
        var other = await OpenAsync(owner: 11);
        _host.Transport.DeleteMessage(second.Message!.Value);
        _host.Transport.DeleteMessage(other.Message!.Value);

        var orphaned = await _host.InScopeAsync(sp => sp.GetRequiredService<LfgCardSync>().VerifyOwnerCardsAsync(Guild, new UserId(Owner), Ct));

        orphaned.Should().Be(1);
        _host.Transport.PresenceCalls.Should().Be(2, "one read per active card of the caller");
        (await RowAsync(second.Id)).Status.Should().Be(LfgStatus.Orphaned);
        (await RowAsync(other.Id)).Status.Should().Be(LfgStatus.Open, "other users' cards are left to the reconciliation");
        (await CreateAsync(game: "WoW")).Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Orphaning_is_idempotent_and_never_touches_ended_listings()
    {
        var active = await OpenAsync();
        var closed = await OpenAsync(game: "CS2");
        await Lfg(s => s.CloseAsync(User(Owner), closed.Id, Ct));
        await _host.InScopeAsync(sp => sp.GetRequiredService<LfgCardSync>().SyncAsync(closed.Id, Ct));
        _host.Transport.DeleteMessage(active.Message!.Value);
        _host.Transport.DeleteMessage(closed.Message!.Value);

        await _host.InScopeAsync(async sp =>
        {
            var cards = sp.GetRequiredService<LfgCardSync>();
            (await cards.VerifyAsync(active.Id, Ct)).Should().Be(LfgCardSyncOutcome.MessageMissing);
            (await cards.VerifyAsync(active.Id, Ct)).Should().Be(LfgCardSyncOutcome.NothingToDo);
            (await cards.VerifyAsync(closed.Id, Ct)).Should().Be(LfgCardSyncOutcome.NothingToDo);
        });

        (await RowAsync(closed.Id)).Status.Should().Be(LfgStatus.Closed);
        var version = (await RowAsync(active.Id)).Version;
        (await JoinAsync(active.Id, 20)).Result.MessageKey.Should().Be("lfg.closed");
        (await RowAsync(active.Id)).Version.Should().Be(version, "an orphaned listing can never be reopened");
    }

    // ---- background card edits ----

    [Fact]
    public async Task A_lost_permission_is_retried_a_bounded_number_of_times_and_never_changes_the_listing_state()
    {
        var listing = await OpenAsync();
        await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));
        for (var i = 0; i < LfgCardSync.MaxAttempts + 3; i++)
            _host.Transport.ScriptEdit(() => new SendOutcome.Permanent(PermanentFailureKind.MissingAccess, "Missing Access"));

        for (var i = 0; i < LfgCardSync.MaxAttempts + 3; i++)
            await WorkerPassAsync();

        var row = await RowAsync(listing.Id);
        row.Status.Should().Be(LfgStatus.Closed);
        row.CardStale.Should().BeFalse();
        _host.Transport.EditCalls.Should().Be(LfgCardSync.MaxAttempts);
    }

    [Fact]
    public async Task The_card_ids_are_recorded_once_and_only_for_the_listings_own_guild()
    {
        var created = (await CreateAsync()).Listing!;
        Task Attach(GuildId guild, ChannelId channel, ulong message) =>
            Lfg(async s =>
            {
                await s.AttachMessageAsync(created.Id, guild, channel, new MessageId(message), Ct);
                return 0;
            });

        await Attach(OtherGuild, OtherChannel, 1);
        (await GetAsync(created.Id))!.Message.Should().BeNull();
        await Attach(Guild, Channel, 2);
        await Attach(Guild, OtherChannel, 3);

        var stored = (await GetAsync(created.Id))!;
        stored.Message.Should().Be(new MessageId(2));
        stored.Channel.Should().Be(Channel, "the later edit targets exactly the /ekip response");
    }

    // ---- full card ----

    [Fact]
    public async Task A_full_listing_queues_joins_and_leave_and_close_still_work()
    {
        var renderer = _host.Services.GetRequiredService<LfgCardRenderer>();
        var listing = await OpenAsync(players: 3);
        await FillAsync(listing.Id, 2);
        var full = (await GetAsync(listing.Id))!;
        full.Status.Should().Be(LfgStatus.Full);
        renderer.Render(full, "tr").Buttons!.Should().OnlyContain(b => !b.Disabled, "full is not closed: the first button queues");

        (await JoinAsync(listing.Id, 30)).Result.MessageKey.Should().Be("lfg.join.waitlisted");
        (await Lfg(s => s.CheckCloseAsync(User(Owner), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.question");
        (await Lfg(s => s.CheckCloseAsync(Moderator(40), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.question");
        (await Lfg(s => s.CheckCloseAsync(User(100), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.forbidden");

        var left = await LeaveAsync(listing.Id, 101);
        (left.Listing!.Status, left.Listing.WaitlistedPlayers.Count).Should().Be((LfgStatus.Full, 0));
        left.Listing.Players.Should().Contain(new UserId(30), "the freed slot went to the first in line").And.HaveCount(3);
        (await LeaveAsync(listing.Id, 100)).Listing!.Status.Should().Be(LfgStatus.Open);
        renderer.Render((await GetAsync(listing.Id))!, "tr").Buttons!.Should().OnlyContain(b => !b.Disabled);
        (await JoinAsync(listing.Id, 31)).Result.MessageKey.Should().Be("lfg.join.done");
    }

    // ---- mentions ----

    [Fact]
    public async Task No_create_or_edit_of_a_card_can_ping_the_players_it_shows()
    {
        var renderer = _host.Services.GetRequiredService<LfgCardRenderer>();
        var cards = new List<OutgoingMessage>();
        var listing = await OpenAsync(players: 3); // the /ekip card
        cards.Add(renderer.Render(listing, "tr"));
        foreach (var step in new Func<Task<LfgResult>>[] { () => JoinAsync(listing.Id, 20), () => JoinAsync(listing.Id, 21), () => LeaveAsync(listing.Id, 21) })
            cards.Add(renderer.Render((await step()).Listing!, "tr")); // interactive redraws after join, full, leave
        await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));
        await _host.InScopeAsync(sp => sp.GetRequiredService<LfgCardSync>().SyncAsync(listing.Id, Ct)); // close edit
        var expiring = await OpenAsync(owner: 11, minutes: 60);
        await JoinAsync(expiring.Id, 22);
        _host.Clock.Advance(TimeSpan.FromMinutes(61));
        await WorkerPassAsync(); // expiry edit

        cards.AddRange(_host.Transport.Messages.SelectMany(m => m.Edits.Prepend(m.Message)));
        cards.Should().HaveCount(4 + 2 + 2);
        cards.Should().OnlyContain(c => c.Content == null && !c.Mentions.PingsAnything, "players are mentioned only inside the embed");
        cards.Should().OnlyContain(c => c.Embed!.Description!.Contains("<@", StringComparison.Ordinal));
        _host.Transport.Messages.Should().OnlyContain(m => !m.Pinged);
    }

    // ---- privacy / retention ----

    [Fact]
    public async Task Privacy_export_and_delete_cover_created_and_joined_listings()
    {
        var mine = await OpenAsync(owner: 20, players: 3, game: "Minecraft");
        await JoinAsync(mine.Id, 21);
        var theirs = await OpenAsync(players: 2);
        (await JoinAsync(theirs.Id, 20)).Listing!.Status.Should().Be(LfgStatus.Full);

        await _host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg");
            var export = await data.ExportAsync(Guild, new UserId(20), Ct);
            export["listings"]!.AsArray().Should().ContainSingle();
            export["listings"]![0]!["game"]!.GetValue<string>().Should().Be("Minecraft");
            export["joinedListings"]!.AsArray().Should().ContainSingle();
            (await data.PreviewDeletionAsync(Guild, new UserId(20), Ct)).Select(i => (i.LabelKey, i.Count))
                .Should().BeEquivalentTo([("lfg.privacy.listings", 1), ("lfg.privacy.joined", 1)]);

            (await data.DeleteAsync(Guild, new UserId(20), Ct)).RecordsDeleted.Should().BeGreaterThan(0);
        });

        (await GetAsync(mine.Id)).Should().BeNull();
        var reopened = (await GetAsync(theirs.Id))!;
        reopened.Players.Should().Equal(new UserId(Owner));
        reopened.Status.Should().Be(LfgStatus.Open);
        (await RowAsync(theirs.Id)).CardStale.Should().BeTrue("the card is redrawn without the deleted player");
        (await JoinAsync(mine.Id, 22)).Result.MessageKey.Should().Be("lfg.not_found");
    }

    [Fact]
    public async Task Guild_retention_purges_every_lfg_row_of_that_guild_only()
    {
        var listing = await OpenAsync();
        await JoinAsync(listing.Id, 20);
        await Lfg(s => s.CreateAsync(User(Owner, OtherGuild), Channel, "WoW", 5, null, null, Ct));
        await _host.InScopeAsync(async sp =>
            await sp.GetRequiredService<LfgConfigService>().SetChannelAsync(TestHost.Admin(Guild), Channel.Value, Ct));

        var purged = await _host.InScopeAsync(sp => sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg").PurgeGuildAsync(Guild, Ct));

        purged.Should().Be(4); // 2 players + 1 listing + 1 config
        (await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<LfgListingEntity>().CountAsync())).Should().Be(1);
    }
}
