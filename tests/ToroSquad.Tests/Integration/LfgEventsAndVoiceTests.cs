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
using ToroSquad.Core.Notifications;
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
/// TSQ LFG V2 on the real SQLite database, production wiring and the real outbox: Maybe RSVP (never a slot, never pinged),
/// scheduled starts (EventAt vs ExpiresAt), the opt-in 30-minute and start notices (current Joined players only, at most
/// once across restarts, never late, never for ended listings or disabled modules), controlled user pings, the voice
/// channel option and button (move only who is already in voice, honest fallback otherwise), and the schema upgrade.
/// </summary>
public sealed class LfgEventsAndVoiceTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(881);
    private static readonly GuildId OtherGuild = new(882);
    private static readonly ChannelId Channel = new(8801);
    private static readonly ChannelId Voice = new(8802);
    private static readonly ChannelId TextNotVoice = new(8803);
    private const ulong Owner = 10;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private const GuildPermission BotVoiceBasics = GuildPermission.ViewChannel | GuildPermission.Connect;

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        await PrepareAsync(_host);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static async Task PrepareAsync(TestHost host)
    {
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, F1TestHostExtensions.ChannelPermissions));
        host.Guilds.SetVoiceChannel(Guild, Voice, new VoiceChannelAccess(true, true, BotVoiceBasics));
        host.Guilds.SetVoiceChannel(Guild, TextNotVoice, new VoiceChannelAccess(true, false, BotVoiceBasics));
        await SetModuleAsync(host, true);
    }

    private static Task SetModuleAsync(TestHost host, bool enabled) => host.InScopeAsync(async sp =>
        (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "lfg", enabled, Ct)).Succeeded.Should().BeTrue());

    private static ActorContext User(ulong id, GuildId? guild = null) =>
        new(guild ?? Guild, new UserId(id), GuildPermission.ViewChannel | GuildPermission.SendMessages, [], false, 1);

    private Task<T> Lfg<T>(Func<LfgService, Task<T>> action, TestHost? host = null) =>
        (host ?? _host).InScopeAsync(sp => action(sp.GetRequiredService<LfgService>()));

    private Task<LfgResult> JoinAsync(long id, ulong user) => Lfg(s => s.JoinAsync(User(user), id, Ct));

    private Task<LfgResult> MaybeAsync(long id, ulong user) => Lfg(s => s.MaybeAsync(User(user), id, Ct));

    private Task<LfgResult> LeaveAsync(long id, ulong user) => Lfg(s => s.LeaveAsync(User(user), id, Ct));

    private Task<LfgVoiceResult> VoiceAsync(long id, ulong user) => Lfg(s => s.VoiceAsync(User(user), id, Ct));

    private Task<LfgResult> CreateAsync(LfgCreateInput input, ulong owner = Owner) => Lfg(s => s.CreateAsync(User(owner), Channel, input, Ct));

    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    /// <summary>The date the owner types for a start <paramref name="minutes"/> from now (null / 0 = empty = now).</summary>
    private string? In(int? minutes) => minutes is > 0 ? LfgForm.FormatDate(_host.Clock.GetUtcNow().AddMinutes(minutes.Value), Istanbul) : null;

    private async Task<LfgListingView> OpenAsync(int players = 6, int? start = null, bool remind = false, bool atStart = false, ChannelId? voice = null,
        string game = "Deadlock", ulong owner = Owner)
    {
        var created = await CreateAsync(new LfgCreateInput(game, players, "Ranked, mikrofon gerekli", null, remind, atStart, voice, In(start)), owner);
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

    private Task<LfgListingView?> GetAsync(long id, TestHost? host = null) => Lfg(s => s.GetAsync(id, Ct), host);

    private Task<LfgListingEntity> RowAsync(long id, TestHost? host = null) => (host ?? _host).InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<LfgListingEntity>().AsNoTracking().SingleAsync(x => x.Id == id));

    private Task<List<OutboxMessageEntity>> NoticesAsync(TestHost? host = null) => (host ?? _host).InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "lfg").OrderBy(o => o.Id).ToListAsync());

    /// <summary>One LFG worker pass, then the outbox is drained into the fake Discord.</summary>
    private async Task TickAsync(TestHost? host = null)
    {
        var h = host ?? _host;
        await h.Services.GetRequiredService<LfgExpiryWorker>().RunOnceAsync(Ct);
        var processor = h.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    /// <summary>Messages the bot SENT (new messages, not the card posted by the test) that carry a notice.</summary>
    private List<FakeMessageTransport.FakeMessage> Delivered(TestHost? host = null) =>
        (host ?? _host).Transport.Messages.Where(m => m.Message.Content is not null).ToList();

    private static IEnumerable<ulong> Pinged(FakeMessageTransport.FakeMessage m) => m.Message.Mentions.Users?.Select(u => u.Value) ?? [];

    // ---- Maybe RSVP ----

    [Fact]
    public async Task Maybe_is_listed_separately_and_never_takes_a_slot()
    {
        var listing = await OpenAsync(players: 3);
        await JoinAsync(listing.Id, 20);

        var maybe = await MaybeAsync(listing.Id, 30);
        await MaybeAsync(listing.Id, 31);

        maybe.Result.MessageKey.Should().Be("lfg.maybe.done");
        var view = (await GetAsync(listing.Id))!;
        view.Players.Should().Equal(new UserId(Owner), new UserId(20));
        view.MaybePlayers.Should().Equal(new UserId(30), new UserId(31));
        view.Status.Should().Be(LfgStatus.Open, "2 joined of 3 — two Maybes do not make it full");
        var card = _host.Services.GetRequiredService<LfgCardRenderer>().Render(view, "tr");
        card.Embed!.Description.Should().Contain("👥 **2 / 3**").And.Contain("**Katılanlar**\n<@10> · <@20>").And.Contain("🤔 **Belki**\n<@30> · <@31>");
    }

    [Fact]
    public async Task Maybe_twice_owner_maybe_and_leaving_as_maybe()
    {
        var listing = await OpenAsync();
        await MaybeAsync(listing.Id, 30);

        (await MaybeAsync(listing.Id, 30)).Result.MessageKey.Should().Be("lfg.maybe.already");
        (await MaybeAsync(listing.Id, Owner)).Result.MessageKey.Should().Be("lfg.maybe.owner");
        (await GetAsync(listing.Id))!.Players.Should().Contain(new UserId(Owner), "the owner is always Joined");

        (await LeaveAsync(listing.Id, 30)).Result.MessageKey.Should().Be("lfg.leave.done");
        (await GetAsync(listing.Id))!.MaybePlayers.Should().BeEmpty();
    }

    [Fact]
    public async Task Maybe_to_joined_needs_a_free_slot_and_otherwise_stays_maybe()
    {
        var listing = await OpenAsync(players: 2);
        await MaybeAsync(listing.Id, 30);

        (await JoinAsync(listing.Id, 30)).Result.MessageKey.Should().Be("lfg.join.done");
        var full = (await GetAsync(listing.Id))!;
        full.Players.Should().Equal(new UserId(Owner), new UserId(30));
        full.Status.Should().Be(LfgStatus.Full);

        await MaybeAsync(listing.Id, 31);
        var refused = await JoinAsync(listing.Id, 31);
        refused.Result.MessageKey.Should().Be("lfg.join.full_stays_maybe");
        refused.Listing!.MaybePlayers.Should().Equal(new UserId(31));
        refused.Listing.Players.Should().HaveCount(2);
    }

    [Fact]
    public async Task Joined_to_maybe_frees_the_slot_and_reopens_a_full_listing_and_maybe_works_while_full()
    {
        var listing = await OpenAsync(players: 2);
        await JoinAsync(listing.Id, 20);
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Full);

        (await MaybeAsync(listing.Id, 40)).Result.MessageKey.Should().Be("lfg.maybe.done", "Belki stays available while full");
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Full);

        var switched = await MaybeAsync(listing.Id, 20);
        switched.Listing!.Status.Should().Be(LfgStatus.Open);
        switched.Listing.Players.Should().Equal(new UserId(Owner));
        switched.Listing.MaybePlayers.Should().BeEquivalentTo([new UserId(40), new UserId(20)]);
        _host.Services.GetRequiredService<LfgCardRenderer>().Render(switched.Listing, "tr").Buttons![0].Disabled.Should().BeFalse("Katıl is active again");
        (await LeaveAsync(listing.Id, 40)).Listing!.Status.Should().Be(LfgStatus.Open, "a Maybe leaving frees no slot");
    }

    [Fact]
    public async Task Racing_joins_and_maybes_keep_one_row_per_user_and_never_overbook()
    {
        for (var round = 0; round < 4; round++)
        {
            var listing = await OpenAsync(players: 3, owner: 50 + (ulong)round, game: "Round " + round);
            var users = Enumerable.Range(0, 10).Select(i => 1000 + ((ulong)round * 100) + (ulong)i).ToList();
            var tasks = users.SelectMany(u => new Func<Task<LfgResult>>[] { () => JoinAsync(listing.Id, u), () => MaybeAsync(listing.Id, u) })
                .OrderBy(_ => Guid.NewGuid()).Select(f => Task.Run(f)).ToList();
            await Task.WhenAll(tasks);

            var rows = await _host.InScopeAsync(async sp =>
                await sp.GetRequiredService<ToroDbContext>().Set<LfgParticipantEntity>().AsNoTracking().Where(p => p.ListingId == listing.Id).ToListAsync());
            rows.Select(r => r.UserId).Should().OnlyHaveUniqueItems();
            rows.Count(r => r.Response == LfgResponse.Joined).Should().BeLessThanOrEqualTo(3);
            var view = (await GetAsync(listing.Id))!;
            view.Status.Should().Be(view.Players.Count >= 3 ? LfgStatus.Full : LfgStatus.Open, "Full follows the Joined count only");
        }
    }

    // ---- scheduling ----

    [Fact]
    public async Task A_listing_starting_now_keeps_the_original_expiry()
    {
        var now = (await CreateAsync(new LfgCreateInput("CS2", 5, DurationMinutes: 60, StartAt: ""))).Listing!;

        now.EventAt.Should().BeNull();
        now.ExpiresAt.Should().Be(TestHost.T0.AddHours(1));
    }

    [Fact]
    public async Task A_scheduled_listing_expires_duration_after_the_start_and_never_before_it()
    {
        var listing = (await CreateAsync(new LfgCreateInput("Valheim", 6, DurationMinutes: 120, StartAt: In(180)))).Listing!;

        listing.EventAt.Should().Be(TestHost.T0.AddHours(3));
        listing.ExpiresAt.Should().Be(TestHost.T0.AddHours(5));
        var card = _host.Services.GetRequiredService<LfgCardRenderer>().Render(listing, "tr");
        var at = TestHost.T0.AddHours(3).ToUnixTimeSeconds();
        card.Embed!.Description.Should().Contain($"🗓️ **Başlangıç:** <t:{at}:F> • <t:{at}:R>");

        _host.Clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(5));
        await TickAsync();
        (await RowAsync(listing.Id)).Status.Should().Be(LfgStatus.Open, "the event just started; the listing keeps gathering");
        _host.Clock.Advance(TimeSpan.FromHours(2));
        await TickAsync();
        (await RowAsync(listing.Id)).Status.Should().Be(LfgStatus.Expired);
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData("", false, true)]
    public async Task Pings_need_a_later_start(string? start, bool remind, bool atStart)
    {
        var result = await CreateAsync(new LfgCreateInput("Deadlock", 5, StartAt: start, NotifyBeforeStart: remind, NotifyAtStart: atStart));

        result.Result.MessageKey.Should().Be("lfg.create.notice_needs_start");
    }

    // ---- custom start date (same EventAt pipeline) ----

    // TestHost.T0 = 24.09.2026 12:00 UTC = 15:00 in Istanbul (the guild default time zone).
    private static readonly DateTimeOffset Custom = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero); // "24.09.2026 17:00" Istanbul

    [Fact]
    public async Task A_custom_date_is_read_in_the_guild_time_zone_and_expires_duration_after_the_start()
    {
        var listing = (await CreateAsync(new LfgCreateInput("Deadlock", 6, DurationMinutes: 120, StartAt: "24.09.2026 17:00"))).Listing!;

        listing.EventAt.Should().Be(Custom);
        listing.ExpiresAt.Should().Be(Custom.AddHours(2));
        var at = Custom.ToUnixTimeSeconds();
        _host.Services.GetRequiredService<LfgCardRenderer>().Render(listing, "tr").Embed!.Description
            .Should().Contain($"🗓️ **Başlangıç:** <t:{at}:F> • <t:{at}:R>", "rendered like every scheduled start");
    }

    [Fact]
    public async Task Only_a_full_date_and_time_is_a_start_and_bad_dates_are_refused()
    {
        foreach (var relative in new[] { "2 saat", "30 dk", "1 gün", "2", "30" })
            (await CreateAsync(new LfgCreateInput("Deadlock", 6, StartAt: relative))).Result.MessageKey.Should().Be("lfg.create.date_format", relative);
        (await CreateAsync(new LfgCreateInput("Deadlock", 6, StartAt: "24.09.2026 14:00"))).Result.MessageKey.Should().Be("lfg.create.date_not_future");
        (await CreateAsync(new LfgCreateInput("Deadlock", 6, StartAt: "31.02.2026 21:00"))).Result.MessageKey.Should().Be("lfg.create.date_format");
        (await CreateAsync(new LfgCreateInput("Deadlock", 6, StartAt: "25.09.2027 12:00"))).Result.MessageKey.Should().Be("lfg.create.date_too_far");
        (await CreateAsync(new LfgCreateInput("Deadlock", 6, StartAt: "24.09.26 17:00"))).Listing!.EventAt.Should().Be(Custom, "a two-digit year");
        (await CreateAsync(new LfgCreateInput("Deadlock", 6, StartAt: " "), owner: 11)).Listing!.EventAt.Should().BeNull("blank = now");
    }

    [Fact]
    public async Task The_guild_time_zone_from_setup_decides_the_instant()
    {
        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<GuildSettingsService>().UpdateAsync(TestHost.Admin(Guild), null, "America/New_York", Ct)).Succeeded.Should().BeTrue());

        var listing = (await CreateAsync(new LfgCreateInput("Deadlock", 6, StartAt: "24.09.2026 17:00"))).Listing!;

        listing.EventAt.Should().Be(new DateTimeOffset(2026, 9, 24, 21, 0, 0, TimeSpan.Zero), "17:00 EDT = 21:00 UTC");
        var status = await _host.InScopeAsync(sp => sp.GetRequiredService<LfgConfigService>().StatusAsync(TestHost.Admin(Guild), Ct));
        status.Status!.TimeZoneId.Should().Be("America/New_York");
    }

    [Fact]
    public async Task A_custom_date_uses_the_same_reminder_start_notice_and_restart_rules()
    {
        var created = await CreateAsync(new LfgCreateInput("Deadlock", 6, StartAt: "24.09.2026 17:00", NotifyBeforeStart: true, NotifyAtStart: true, VoiceChannel: Voice));
        var id = created.Listing!.Id;
        await JoinAsync(id, 20);
        await MaybeAsync(id, 30);

        _host.Clock.Advance(Custom - TestHost.T0 - TimeSpan.FromMinutes(31)); // 31 minutes before the start
        await TickAsync();
        Delivered().Should().BeEmpty();

        _host.Clock.Advance(TimeSpan.FromMinutes(1)); // EventAt - 30 min
        await TickAsync();
        await TickAsync();
        var reminder = Delivered().Should().ContainSingle().Subject;
        Pinged(reminder).Should().BeEquivalentTo([Owner, 20UL]);
        reminder.Message.Buttons.Should().ContainSingle().Which.CustomId.Should().Be("tsq:lfg:voice:" + id);

        _host.Clock.Advance(TimeSpan.FromMinutes(30)); // EventAt
        await TickAsync();
        await TickAsync();
        Delivered().Should().HaveCount(2);
        Delivered().Last().Message.Content.Should().StartWith("🚀 **Deadlock** şimdi başlıyor!");
        var row = await RowAsync(id);
        (row.ReminderState, row.StartNoticeState, row.Status).Should().Be((LfgNoticeState.Queued, LfgNoticeState.Queued, LfgStatus.Open));
    }

    // ---- 30-minute reminder ----

    [Fact]
    public async Task Without_the_opt_in_no_notice_is_ever_created()
    {
        await OpenAsync(start: 60);
        for (var i = 0; i < 70; i += 10)
        {
            _host.Clock.Advance(TimeSpan.FromMinutes(10));
            await TickAsync();
        }

        (await NoticesAsync()).Should().BeEmpty();
        Delivered().Should().BeEmpty();
    }

    [Fact]
    public async Task The_reminder_pings_exactly_the_joined_players_at_that_moment_once()
    {
        var listing = await OpenAsync(start: 120, remind: true, voice: Voice);
        await JoinAsync(listing.Id, 20);
        await JoinAsync(listing.Id, 21);
        await MaybeAsync(listing.Id, 30);
        await LeaveAsync(listing.Id, 21); // left before the reminder

        _host.Clock.Advance(TimeSpan.FromMinutes(80));
        await TickAsync();
        Delivered().Should().BeEmpty("not due before 30 minutes ahead of the start");

        await JoinAsync(listing.Id, 22); // joined just before the reminder
        _host.Clock.Advance(TimeSpan.FromMinutes(10)); // 30 minutes before the start
        await TickAsync();
        await TickAsync();

        var reminder = Delivered().Should().ContainSingle().Subject;
        Pinged(reminder).Should().BeEquivalentTo([Owner, 20UL, 22UL], "Joined only: no Maybe, not the one who left");
        reminder.Pinged.Should().BeTrue();
        reminder.Message.Mentions.Everyone.Should().BeFalse();
        reminder.Message.Mentions.Roles.Should().BeEmpty();
        reminder.Message.Content.Should().Contain("<@10> <@20> <@22>").And.NotContain("<@30>").And.NotContain("<@21>")
            .And.Contain("🔊 Ses Odası: <#8802>");
        reminder.Message.Buttons.Should().ContainSingle().Which.CustomId.Should().Be("tsq:lfg:voice:" + listing.Id);
        var row = await RowAsync(listing.Id);
        row.ReminderState.Should().Be(LfgNoticeState.Queued);
        row.StartNoticeState.Should().Be(LfgNoticeState.Pending, "not requested: never handled");
    }

    [Fact]
    public async Task A_restart_never_pings_twice()
    {
        var listing = await OpenAsync(start: 60, remind: true, atStart: true);
        _host.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAsync();
        Delivered().Should().ContainSingle();

        await using var second = await TestHost.CreateAsync(new() { ["Bot:DataDirectory"] = _host.Directory }, _host.Clock.GetUtcNow(), services =>
        {
            services.AddSingleton(_host.Transport);
            services.AddSingleton<IMessageTransport>(_host.Transport);
        });
        second.Guilds.SetVoiceChannel(Guild, Voice, new VoiceChannelAccess(true, true, BotVoiceBasics));
        await TickAsync(second);
        await TickAsync(second);
        Delivered().Should().ContainSingle("the reminder is already handled");

        second.Clock.Advance(TimeSpan.FromMinutes(30)); // the start
        await TickAsync(second);
        await TickAsync(second);
        await TickAsync();
        Delivered().Should().HaveCount(2, "one reminder + one start notice, across both processes");
        (await RowAsync(listing.Id, second)).StartNoticeState.Should().Be(LfgNoticeState.Queued);
    }

    [Fact]
    public async Task A_reminder_that_became_due_while_the_bot_was_down_and_the_event_began_is_skipped_not_sent_late()
    {
        var listing = await OpenAsync(start: 60, remind: true);
        _host.Clock.Advance(TimeSpan.FromMinutes(61)); // the bot "wakes up" after the start

        await TickAsync();

        Delivered().Should().BeEmpty();
        (await RowAsync(listing.Id)).ReminderState.Should().Be(LfgNoticeState.Skipped);
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await TickAsync();
        (await NoticesAsync()).Should().BeEmpty("consumed: nothing is retried after a restart either");
    }

    [Fact]
    public async Task Ended_listings_and_a_disabled_module_produce_no_notice_and_nothing_floods_after_re_enabling()
    {
        var closed = await OpenAsync(start: 60, remind: true, atStart: true, game: "Closed");
        await Lfg(s => s.CloseAsync(User(Owner), closed.Id, Ct));
        var orphaned = await OpenAsync(start: 60, remind: true, atStart: true, game: "Orphaned", owner: 11);
        _host.Transport.DeleteMessage(orphaned.Message!.Value);
        await TickAsync(); // reconciliation orphans it
        (await RowAsync(orphaned.Id)).Status.Should().Be(LfgStatus.Orphaned);
        var disabled = await OpenAsync(start: 60, remind: true, atStart: true, game: "Disabled", owner: 12);

        await SetModuleAsync(_host, false);
        _host.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAsync();
        _host.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAsync();
        await SetModuleAsync(_host, true);
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await TickAsync();

        Delivered().Should().BeEmpty();
        (await NoticesAsync()).Should().BeEmpty();
        var skipped = await RowAsync(disabled.Id);
        skipped.ReminderState.Should().Be(LfgNoticeState.Skipped);
        skipped.StartNoticeState.Should().Be(LfgNoticeState.Skipped);
    }

    [Fact]
    public async Task A_listing_that_expired_while_the_bot_was_down_sends_nothing()
    {
        var listing = await OpenAsync(start: 60, remind: true, atStart: true);
        _host.Clock.Advance(TimeSpan.FromHours(5)); // start + default 2 h expiry long passed

        await TickAsync();

        (await RowAsync(listing.Id)).Status.Should().Be(LfgStatus.Expired);
        (await NoticesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_cancels_a_notice_still_waiting_in_the_outbox()
    {
        var listing = await OpenAsync(start: 30, remind: true);
        await _host.Services.GetRequiredService<LfgExpiryWorker>().RunOnceAsync(Ct); // queued, not delivered yet
        (await NoticesAsync()).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Pending);

        await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));
        await TickAsync();

        (await NoticesAsync()).Single().Status.Should().Be(OutboxStatus.Cancelled);
        Delivered().Should().BeEmpty();
    }

    // ---- start notice ----

    [Fact]
    public async Task The_start_notice_reads_the_joined_players_again_at_the_start()
    {
        var listing = await OpenAsync(start: 60, remind: true, atStart: true);
        await JoinAsync(listing.Id, 20);
        await JoinAsync(listing.Id, 21);
        _host.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAsync();
        Pinged(Delivered().Single()).Should().BeEquivalentTo([Owner, 20UL, 21UL]);

        await LeaveAsync(listing.Id, 21);
        await JoinAsync(listing.Id, 22);
        await MaybeAsync(listing.Id, 20); // now only a Maybe
        _host.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAsync();

        var start = Delivered().Last();
        start.Message.Content.Should().StartWith("🚀 **Deadlock** şimdi başlıyor!");
        Pinged(start).Should().BeEquivalentTo([Owner, 22UL]);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(90, false)] // still active (expires 2 h after the start) but far past the grace
    public async Task The_start_notice_has_a_short_grace_after_a_late_restart(int minutesLate, bool sent)
    {
        var listing = await OpenAsync(start: 60, atStart: true, voice: Voice);
        _host.Clock.Advance(TimeSpan.FromMinutes(60 + minutesLate));

        await TickAsync();

        Delivered().Should().HaveCount(sent ? 1 : 0);
        (await RowAsync(listing.Id)).StartNoticeState.Should().Be(sent ? LfgNoticeState.Queued : LfgNoticeState.Skipped);
        if (sent)
            Delivered().Single().Message.Buttons.Should().ContainSingle().Which.Label.Should().Be("🔊 Ses Odasına Katıl");
    }

    // ---- mention security ----

    [Fact]
    public async Task Notices_defuse_mentions_in_the_creators_text_and_ping_only_listed_ids()
    {
        var created = await CreateAsync(new LfgCreateInput("@everyone <@&1> @here <@999>", 5, "@everyone", StartAt: In(30), NotifyBeforeStart: true));
        created.Result.Succeeded.Should().BeTrue();
        await Lfg(s => s.MaybeAsync(User(30), created.Listing!.Id, Ct));

        await TickAsync();

        var notice = Delivered().Should().ContainSingle().Subject.Message;
        notice.Mentions.Should().BeEquivalentTo(MentionPolicy.ExplicitUsers([new UserId(Owner)]));
        DiscordText.RawMentionPattern().Matches(notice.Content!).Select(m => m.Value).Should().Equal("<@10>");
        var wire = DiscordConversions.ToAllowedMentions(notice.Mentions);
        wire.AllowedTypes.Should().Be(global::Discord.AllowedMentionTypes.None);
        wire.UserIds.Should().Equal(Owner);
        wire.RoleIds.Should().BeNullOrEmpty();
        notice.Embed.Should().BeNull();
    }

    [Fact]
    public async Task A_resend_after_an_uncertain_delivery_never_pings_the_users_again()
    {
        await OpenAsync(start: 30, remind: true);
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout after the request was written"));
        await TickAsync(); // attempt -> delivery unknown
        Delivered().Should().BeEmpty();

        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await TickAsync(); // reconciliation: not in the channel -> one resend

        var resent = Delivered().Should().ContainSingle().Subject;
        resent.Pinged.Should().BeFalse("a missing ping is acceptable, a second one is not");
        resent.Message.Content.Should().Contain("<@10>");
    }

    [Fact]
    public void User_pings_stay_out_of_stored_payloads_unless_listed()
    {
        PayloadSerializer.Serialize(new OutgoingMessage("x", null, MentionPolicy.None)).Should().NotContain("users");
        PayloadSerializer.Serialize(new OutgoingMessage("x", null, MentionPolicy.EveryoneOnly)).Should().NotContain("users");
        var listed = PayloadSerializer.Serialize(new OutgoingMessage("x", null, MentionPolicy.ExplicitUsers([new UserId(5), new UserId(5)])));
        PayloadSerializer.Deserialize(listed).Mentions.Users.Should().Equal(new UserId(5));
        MentionPolicy.ExplicitUsers([]).Should().BeSameAs(MentionPolicy.None);
        FluentActions.Invoking(() => MentionPolicy.ExplicitUsers(Enumerable.Range(1, 101).Select(i => new UserId((ulong)i))))
            .Should().Throw<ArgumentException>("Discord accepts at most 100 user ids");
        DiscordConversions.ToAllowedMentions(MentionPolicy.None).UserIds.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task No_card_create_or_edit_pings_even_with_maybe_voice_and_a_schedule()
    {
        var listing = await OpenAsync(players: 2, start: 60, remind: true, voice: Voice);
        var renderer = _host.Services.GetRequiredService<LfgCardRenderer>();
        var cards = new List<OutgoingMessage> { renderer.Render(listing, "tr") };
        foreach (var step in new Func<Task<LfgResult>>[]
                 {
                     () => JoinAsync(listing.Id, 20), () => MaybeAsync(listing.Id, 30), () => MaybeAsync(listing.Id, 20), () => LeaveAsync(listing.Id, 30),
                 })
            cards.Add(renderer.Render((await step()).Listing!, "tr"));
        await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));
        await TickAsync(); // close edit

        cards.AddRange(_host.Transport.Messages.Where(m => m.Id == listing.Message).SelectMany(m => m.Edits.Prepend(m.Message)));
        cards.Should().HaveCount(5 + 2);
        cards.Should().OnlyContain(c => c.Content == null && !c.Mentions.PingsAnything);
        cards.Take(5).Should().OnlyContain(c => c.Buttons!.Count == 6, "Katıl · Belki · Ayrıl · Ses Odası | Düzenle · Kapat");
    }

    // ---- voice ----

    [Fact]
    public async Task The_voice_channel_must_be_a_voice_channel_of_this_guild()
    {
        (await CreateAsync(new LfgCreateInput("CS2", 5, VoiceChannel: TextNotVoice))).Result.MessageKey.Should().Be("lfg.create.voice_invalid");
        (await CreateAsync(new LfgCreateInput("CS2", 5, VoiceChannel: new ChannelId(424242)))).Result.MessageKey.Should().Be("lfg.create.voice_invalid");
        _host.Guilds.SetVoiceChannel(OtherGuild, new ChannelId(8899), new VoiceChannelAccess(true, true, BotVoiceBasics));
        (await CreateAsync(new LfgCreateInput("CS2", 5, VoiceChannel: new ChannelId(8899)))).Result.MessageKey.Should().Be("lfg.create.voice_invalid", "another guild's channel");

        var ok = await CreateAsync(new LfgCreateInput("CS2", 5, VoiceChannel: Voice));
        ok.Listing!.VoiceChannel.Should().Be(Voice);
        var card = _host.Services.GetRequiredService<LfgCardRenderer>().Render(ok.Listing, "tr");
        card.Embed!.Description.Should().Contain("🔊 Ses Odası: <#8802>");
        card.Buttons!.Select(b => b.CustomId).Should().Equal(
            "tsq:lfg:join:" + ok.Listing.Id, "tsq:lfg:maybe:" + ok.Listing.Id, "tsq:lfg:leave:" + ok.Listing.Id, "tsq:lfg:voice:" + ok.Listing.Id,
            "tsq:lfg:edit:" + ok.Listing.Id, "tsq:lfg:close:" + ok.Listing.Id);
    }

    [Fact]
    public async Task Only_joined_players_get_the_voice_action()
    {
        var listing = await OpenAsync(voice: Voice);
        await MaybeAsync(listing.Id, 30);

        (await VoiceAsync(listing.Id, 30)).Result.MessageKey.Should().Be("lfg.voice.join_first");
        (await VoiceAsync(listing.Id, 31)).Result.MessageKey.Should().Be("lfg.voice.join_first");
        (await Lfg(s => s.VoiceAsync(User(Owner, OtherGuild), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.not_found");
        (await VoiceAsync((await OpenAsync(owner: 11)).Id, 11)).Result.MessageKey.Should().Be("lfg.voice.none");
        _host.Guilds.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_move_members_the_bot_never_tries_to_move_and_offers_the_channel_link()
    {
        var listing = await OpenAsync(voice: Voice);

        var result = await VoiceAsync(listing.Id, Owner);

        result.Result.MessageKey.Should().Be("lfg.voice.open");
        result.OpenChannelUrl.Should().Be("https://discord.com/channels/881/8802");
        _host.Guilds.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task A_member_already_in_voice_is_moved_and_one_who_is_not_gets_the_link()
    {
        _host.Guilds.SetVoiceChannel(Guild, Voice, new VoiceChannelAccess(true, true, VoiceChannelAccess.RequiredToMove));
        var listing = await OpenAsync(voice: Voice);
        await JoinAsync(listing.Id, 20);

        _host.Guilds.ScriptedMoves.Enqueue(VoiceMoveOutcome.Moved);
        var moved = await VoiceAsync(listing.Id, 20);
        moved.Result.MessageKey.Should().Be("lfg.voice.moved");
        moved.OpenChannelUrl.Should().BeNull();
        _host.Guilds.Moves.Should().ContainSingle().Which.Should().Be((Guild, new UserId(20), Voice));

        _host.Guilds.ScriptedMoves.Enqueue(VoiceMoveOutcome.NotConnected);
        var notConnected = await VoiceAsync(listing.Id, 20);
        notConnected.Result.MessageKey.Should().Be("lfg.voice.open_not_connected", "Discord cannot connect a member who is not in voice");
        notConnected.OpenChannelUrl.Should().NotBeNull();

        _host.Guilds.ScriptedMoves.Enqueue(VoiceMoveOutcome.MemberCannotConnect);
        (await VoiceAsync(listing.Id, 20)).Result.MessageKey.Should().Be("lfg.voice.no_access");
    }

    [Fact]
    public async Task A_deleted_voice_channel_only_drops_the_voice_feature()
    {
        var listing = await OpenAsync(voice: Voice);
        _host.Guilds.RemoveVoiceChannel(Guild, Voice);

        (await VoiceAsync(listing.Id, Owner)).Result.MessageKey.Should().Be("lfg.voice.gone");

        var row = await RowAsync(listing.Id);
        row.Status.Should().Be(LfgStatus.Open);
        row.VoiceChannelId.Should().BeNull();
        row.CardStale.Should().BeTrue();
        await TickAsync();
        var redrawn = _host.Transport.Messages.Single(m => m.Id == listing.Message).Edits.Should().ContainSingle().Subject;
        redrawn.Embed!.Description.Should().NotContain("Ses Odası");
        redrawn.Buttons!.Should().HaveCount(5);
        (await VoiceAsync(listing.Id, Owner)).Result.MessageKey.Should().Be("lfg.voice.none");
    }

    [Fact]
    public async Task Without_a_voice_channel_notices_have_no_voice_line_or_button()
    {
        await OpenAsync(start: 30, remind: true);

        await TickAsync();

        var notice = Delivered().Should().ContainSingle().Subject.Message;
        notice.Buttons.Should().BeNull();
        notice.Content.Should().NotContain("Ses Odası");
    }

    // ---- privacy ----

    [Fact]
    public async Task Privacy_covers_maybe_answers_and_notices_that_pinged_the_user()
    {
        var listing = await OpenAsync(players: 2, start: 30, remind: true);
        await JoinAsync(listing.Id, 20);
        await TickAsync(); // the reminder pinged 10 and 20
        var other = await OpenAsync(players: 2, owner: 11, game: "Other");
        await JoinAsync(other.Id, 21);
        await MaybeAsync(other.Id, 20); // a Maybe on a full listing

        await _host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg");
            var export = await data.ExportAsync(Guild, new UserId(20), Ct);
            export["joinedListings"]!.AsArray().Select(j => j!["response"]!.GetValue<string>()).Should().BeEquivalentTo(["Joined", "Maybe"]);
            export["eventNoticesPingingYou"]!.GetValue<int>().Should().Be(1);
            await data.DeleteAsync(Guild, new UserId(20), Ct);
        });

        (await GetAsync(other.Id))!.Status.Should().Be(LfgStatus.Full, "a deleted Maybe never held a slot");
        (await GetAsync(other.Id))!.MaybePlayers.Should().BeEmpty();
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Open, "a deleted Joined player frees the slot");
        (await NoticesAsync()).Should().BeEmpty("the notice that listed the user is gone");
    }

    [Fact]
    public async Task Finished_notices_are_pruned_after_a_day()
    {
        await OpenAsync(start: 30, remind: true);
        await TickAsync();
        (await NoticesAsync()).Should().ContainSingle();

        _host.Clock.Advance(LfgNoticePlanner.Retention + TimeSpan.FromMinutes(6));
        await TickAsync();

        (await NoticesAsync()).Should().BeEmpty();
    }

    // ---- release review fixes ----

    [Fact]
    public async Task A_moderator_who_closed_a_listing_is_covered_by_privacy()
    {
        var listing = await OpenAsync();
        var moderator = new ActorContext(Guild, new UserId(40), GuildPermission.ManageMessages, [], false, 5);
        await Lfg(s => s.CloseAsync(moderator, listing.Id, Ct));

        await _host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg");
            (await data.ExportAsync(Guild, new UserId(40), Ct))["listingsYouClosedAsModerator"]!.GetValue<int>().Should().Be(1);
            (await data.PreviewDeletionAsync(Guild, new UserId(40), Ct)).Should().ContainSingle(i => i.LabelKey == "lfg.privacy.closed");
            await data.DeleteAsync(Guild, new UserId(40), Ct);
        });

        var row = await RowAsync(listing.Id);
        row.ClosedByUserId.Should().BeNull();
        row.Status.Should().Be(LfgStatus.Closed, "the listing (the owner's data) stays; only who closed it is forgotten");
    }

    [Fact]
    public async Task A_voice_channel_with_a_user_limit_is_never_joined_through_the_bots_move()
    {
        _host.Guilds.SetVoiceChannel(Guild, Voice, new VoiceChannelAccess(true, true, VoiceChannelAccess.RequiredToMove));
        var listing = await OpenAsync(voice: Voice);
        _host.Guilds.ScriptedMoves.Enqueue(VoiceMoveOutcome.LimitedChannel);

        var result = await VoiceAsync(listing.Id, Owner);

        result.Result.MessageKey.Should().Be("lfg.voice.open", "the member joins themselves, so Discord enforces the limit");
        result.OpenChannelUrl.Should().Be("https://discord.com/channels/881/8802");
    }

    [Fact]
    public async Task A_refusal_never_carries_another_guilds_listing()
    {
        var listing = await OpenAsync();

        foreach (var action in new Func<LfgService, Task<LfgResult>>[]
                 {
                     s => s.JoinAsync(User(20, OtherGuild), listing.Id, Ct), s => s.MaybeAsync(User(20, OtherGuild), listing.Id, Ct),
                     s => s.LeaveAsync(User(20, OtherGuild), listing.Id, Ct), s => s.CheckCloseAsync(User(20, OtherGuild), listing.Id, Ct),
                     s => s.CloseAsync(new ActorContext(OtherGuild, new UserId(20), GuildPermission.Administrator, [], true, 9), listing.Id, Ct),
                 })
        {
            var result = await Lfg(action);
            result.Result.MessageKey.Should().Be("lfg.not_found");
            result.Listing.Should().BeNull();
        }

        (await RowAsync(listing.Id)).Status.Should().Be(LfgStatus.Open);
    }

    [Fact]
    public async Task Closing_also_cancels_a_notice_whose_delivery_is_uncertain()
    {
        var listing = await OpenAsync(start: 30, remind: true);
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        await TickAsync();
        (await NoticesAsync()).Single().Status.Should().Be(OutboxStatus.DeliveryUnknown);

        await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await TickAsync(); // reconciliation: not in the channel -> past its (moved) deadline, no resend

        (await NoticesAsync()).Single().Status.Should().Be(OutboxStatus.Expired);
        Delivered().Should().BeEmpty("no resend for a closed listing");
    }

    [Fact]
    public async Task Closing_while_a_notice_is_being_sent_never_retries_it()
    {
        var listing = await OpenAsync(start: 30, remind: true);
        // The close lands while the request is under way (row claimed InFlight); Discord then answers with a retryable error.
        _host.Transport.ScriptSend(() =>
        {
            Task.Run(() => Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct))).GetAwaiter().GetResult().Result.Succeeded.Should().BeTrue();
            return new SendOutcome.Transient("503 service unavailable");
        });
        await TickAsync();
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Closed);

        _host.Clock.Advance(TimeSpan.FromMinutes(10));
        await TickAsync();
        await TickAsync();

        (await NoticesAsync()).Single().Status.Should().Be(OutboxStatus.Expired);
        Delivered().Should().BeEmpty("a closed listing's notice is not retried");
        (await NoticesAsync()).Single().Attempts.Should().Be(1, "one request, the one the close could not recall");
    }

    [Fact]
    public async Task Privacy_delete_stops_an_uncertain_notice_of_a_deleted_listing()
    {
        await OpenAsync(start: 30, remind: true);
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        await TickAsync();
        (await NoticesAsync()).Single().Status.Should().Be(OutboxStatus.DeliveryUnknown);

        await _host.InScopeAsync(async sp =>
            await sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg").DeleteAsync(Guild, new UserId(Owner), Ct));
        _host.Clock.Advance(TimeSpan.FromMinutes(1));
        await TickAsync(); // reconciliation: not in the channel -> the listing is gone, no resend

        (await NoticesAsync()).Single().Status.Should().Be(OutboxStatus.Expired);
        Delivered().Should().BeEmpty("a deleted listing never pings afterwards");
    }

    [Fact]
    public async Task A_notice_stuck_in_an_uncertain_delivery_is_pruned_and_covered_by_privacy()
    {
        await OpenAsync(start: 30, remind: true);
        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        await TickAsync();
        // Reconciliation gave up (e.g. no Read Message History): no next attempt, it will never be delivered.
        await _host.InScopeAsync(async sp => await sp.GetRequiredService<ToroDbContext>().Outbox.Where(o => o.ModuleId == "lfg")
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, (DateTimeOffset?)null), Ct));

        await _host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "lfg");
            (await data.ExportAsync(Guild, new UserId(Owner), Ct))["eventNoticesPingingYou"]!.GetValue<int>().Should().Be(1);
        });
        _host.Clock.Advance(LfgNoticePlanner.Retention + TimeSpan.FromMinutes(6));
        await TickAsync();

        (await NoticesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task One_card_that_throws_does_not_stop_the_other_cards_or_the_pass()
    {
        var first = await OpenAsync(game: "First");
        var second = await OpenAsync(game: "Second");
        await Lfg(s => s.CloseAsync(User(Owner), first.Id, Ct));
        await Lfg(s => s.CloseAsync(User(Owner), second.Id, Ct));
        _host.Transport.ScriptEdit(() => throw new InvalidOperationException("unexpected SDK failure"));

        await TickAsync();

        var broken = await RowAsync(first.Id);
        broken.CardStale.Should().BeTrue();
        broken.CardSyncAttempts.Should().Be(1, "counted as an attempt, still bounded");
        (await RowAsync(second.Id)).CardStale.Should().BeFalse("the next card was still updated");
    }

    [Fact]
    public async Task Deleted_card_checks_reach_every_active_card_in_turn()
    {
        var listings = new List<LfgListingView>();
        for (var i = 0; i < 55; i++)
            listings.Add(await OpenAsync(owner: 5000 + (ulong)i, game: "Game " + i));
        var newest = listings[^1];
        _host.Transport.DeleteMessage(newest.Message!.Value);

        await TickAsync(); // first 50 cards
        (await RowAsync(newest.Id)).Status.Should().Be(LfgStatus.Open);
        _host.Clock.Advance(LfgExpiryWorker.VerifyInterval);
        await TickAsync(); // the rest

        (await RowAsync(newest.Id)).Status.Should().Be(LfgStatus.Orphaned);
        _host.Transport.PresenceCalls.Should().Be(55, "one read per active card, no repeats");
    }

    // ---- schema upgrade ----

    [Fact]
    public async Task Listings_created_before_rsvp_and_scheduling_keep_their_meaning_after_the_migration()
    {
        var path = Path.Combine(_host.Directory, "upgrade.db");
        var options = new DbContextOptionsBuilder<ToroDbContext>()
            .UseSqlite(DatabaseMaintenance.ConnectionString(path), o => o.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
            .Options;
        await using (var db = new ToroDbContext(options, DesignTimeDbContextFactory.AllContributors()))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260927054240_LfgModule", TestContext.Current.CancellationToken); // the first LFG schema
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO lfg_listing (Id, GuildId, ChannelId, OwnerUserId, GameName, MaxPlayers, Status, CreatedAt, ExpiresAt, CardStale, CardSyncAttempts, Version) " +
                "VALUES (1, 881, 8801, 10, 'Deadlock', 2, 1, 0, 1, 0, 0, 0);" +
                "INSERT INTO lfg_participant (ListingId, UserId, JoinedAt) VALUES (1, 10, 0), (1, 20, 0);", TestContext.Current.CancellationToken);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken); // -> latest
            (await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();

            var listing = await db.Set<LfgListingEntity>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            listing.EventAt.Should().BeNull();
            listing.VoiceChannelId.Should().BeNull();
            listing.NotifyBeforeStart.Should().BeFalse();
            listing.NotifyAtStart.Should().BeFalse();
            listing.ReminderState.Should().Be(LfgNoticeState.Pending);
            var players = await db.Set<LfgParticipantEntity>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
            players.Should().OnlyContain(p => p.Response == LfgResponse.Joined);
            var view = LfgService.ToView(listing, players);
            view.Players.Should().HaveCount(2);
            view.Status.Should().Be(LfgStatus.Full, "the existing full listing stays full");
        }

        using var connection = new SqliteConnection(DatabaseMaintenance.ConnectionString(path));
        SqliteConnection.ClearPool(connection);
    }
}
