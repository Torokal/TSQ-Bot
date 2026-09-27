using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Commands;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The /ekip form and the owner's edit on the real SQLite database, production wiring and the real outbox: the typed texts
/// map onto the existing create rules (validation, relative/custom start, guild time zone, notices, voice); the edit is
/// owner-only, all-or-nothing, decided on the stored state inside the write lock (size never below the Joined players,
/// start only while ahead, expiry never restarted), never repeats or revives a handled notice, and redraws the same card
/// without pinging anyone.
/// </summary>
public sealed class LfgFormAndEditTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(891);
    private static readonly GuildId OtherGuild = new(892);
    private static readonly ChannelId Channel = new(8901);
    private static readonly ChannelId Voice = new(8902);
    private static readonly ChannelId Voice2 = new(8903);
    private static readonly ChannelId TextNotVoice = new(8904);
    private static readonly ChannelId OtherChannel = new(8905);
    private const ulong Owner = 10;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T0 = TestHost.T0;

    private const GuildPermission BotVoiceBasics = GuildPermission.ViewChannel | GuildPermission.Connect;

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, F1TestHostExtensions.ChannelPermissions));
        _host.Guilds.SetChannel(Guild, OtherChannel, new BotChannelAccess(true, true, F1TestHostExtensions.ChannelPermissions));
        _host.Guilds.SetVoiceChannel(Guild, Voice, new VoiceChannelAccess(true, true, BotVoiceBasics));
        _host.Guilds.SetVoiceChannel(Guild, Voice2, new VoiceChannelAccess(true, true, BotVoiceBasics));
        _host.Guilds.SetVoiceChannel(Guild, TextNotVoice, new VoiceChannelAccess(true, false, BotVoiceBasics));
        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "lfg", true, Ct)).Succeeded.Should().BeTrue());
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static ActorContext User(ulong id, GuildId? guild = null) =>
        new(guild ?? Guild, new UserId(id), GuildPermission.ViewChannel | GuildPermission.SendMessages, [], false, 1);

    private static ActorContext Moderator(ulong id) =>
        new(Guild, new UserId(id), GuildPermission.ViewChannel | GuildPermission.ManageMessages, [], false, 5);

    private static LfgFormValues Form(string? game = "Deadlock", string? players = "6", string? details = null, string? start = null, string? duration = null) =>
        new(game, players, details, start, duration);

    private Task<T> Lfg<T>(Func<LfgService, Task<T>> action) => _host.InScopeAsync(sp => action(sp.GetRequiredService<LfgService>()));

    private Task<LfgResult> CreateAsync(LfgFormValues form, bool before = false, bool atStart = false, ChannelId? voice = null, ulong owner = Owner,
        ChannelId? channel = null) =>
        Lfg(s => s.CreateAsync(User(owner), channel ?? Channel, LfgForm.ToCreateInput(form, before, atStart, voice), Ct));

    /// <summary>Creates through the form mapping and posts the card like the save step does (a message the bot can edit).</summary>
    private async Task<LfgListingView> OpenAsync(LfgFormValues? form = null, bool before = false, bool atStart = false, ChannelId? voice = null,
        ulong owner = Owner)
    {
        var created = await CreateAsync(form ?? Form(), before, atStart, voice, owner);
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

    private Task<LfgListingView?> GetAsync(long id) => Lfg(s => s.GetAsync(id, Ct));

    private Task<LfgListingEntity> RowAsync(long id) => _host.InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<LfgListingEntity>().AsNoTracking().SingleAsync(x => x.Id == id));

    private Task<LfgEditOpening> OpenEditAsync(long id, ActorContext? actor = null) => Lfg(s => s.OpenEditAsync(actor ?? User(Owner), id, Ct));

    /// <summary>The owner's edit as the form sends it: the stored form with <paramref name="change"/> applied, settings kept unless given.</summary>
    private async Task<LfgResult> EditAsync(long id, Func<LfgFormValues, LfgFormValues>? change = null, bool? before = null, bool? atStart = null,
        Optional<ChannelId?> voice = default, ActorContext? actor = null)
    {
        var owner = await OpenEditAsync(id);
        var view = (await GetAsync(id))!;
        var prefill = owner.Prefill ?? LfgForm.Prefill(view, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"));
        var input = new LfgEditInput((change ?? (f => f))(prefill), before ?? view.NotifyBeforeStart, atStart ?? view.NotifyAtStart,
            voice.IsSet ? voice.Value : view.VoiceChannel);
        return await Lfg(s => s.EditAsync(actor ?? User(Owner), id, input, Ct));
    }

    private Task<LfgResult> JoinAsync(long id, ulong user) => Lfg(s => s.JoinAsync(User(user), id, Ct));

    private Task<int> ListingCountAsync() => _host.InScopeAsync(async sp => await sp.GetRequiredService<ToroDbContext>().Set<LfgListingEntity>().CountAsync());

    private Task<List<OutboxMessageEntity>> NoticesAsync() => _host.InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "lfg").OrderBy(o => o.Id).ToListAsync());

    private async Task TickAsync()
    {
        await _host.Services.GetRequiredService<LfgExpiryWorker>().RunOnceAsync(Ct);
        var processor = _host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    private List<FakeMessageTransport.FakeMessage> Delivered() => _host.Transport.Messages.Where(m => m.Message.Content is not null).ToList();

    // ---- create through the form ----

    [Fact]
    public async Task The_form_opens_a_listing_through_the_existing_create_rules()
    {
        var created = await CreateAsync(Form(details: "Casual, mikrofon gerekli"));

        created.Result.MessageKey.Should().Be("lfg.create.done");
        var listing = created.Listing!;
        listing.GameName.Should().Be("Deadlock");
        listing.MaxPlayers.Should().Be(6);
        listing.Details.Should().Be("Casual, mikrofon gerekli");
        listing.Players.Should().Equal(new UserId(Owner)); // the owner is the first Joined player
        listing.Status.Should().Be(LfgStatus.Open);
        listing.EventAt.Should().BeNull("an empty start is now");
        listing.ExpiresAt.Should().Be(T0.AddMinutes(120), "the configured default duration");
    }

    [Theory]
    [InlineData("x", "6", null, null, null, "lfg.create.game_too_short")]
    [InlineData("Deadlock", "abc", null, null, null, "lfg.create.players_range")]
    [InlineData("Deadlock", "1", null, null, null, "lfg.create.players_range")]
    [InlineData("Deadlock", "99", null, null, null, "lfg.create.players_range")]
    [InlineData("Deadlock", "6 kişi", null, null, null, "lfg.create.players_range")]
    [InlineData("Deadlock", "6", null, "yarın akşam", null, "lfg.create.date_format")]
    [InlineData("Deadlock", "6", null, "31.02.2026 21:00", null, "lfg.create.date_format")]
    [InlineData("Deadlock", "6", null, "30", null, "lfg.create.date_format")]
    [InlineData("Deadlock", "6", null, "01.01.2026 10:00", null, "lfg.create.date_not_future")]
    [InlineData("Deadlock", "6", null, "1,33 saat", null, "lfg.create.start_invalid")]
    [InlineData("Deadlock", "6", null, null, "5", "lfg.create.duration_invalid")]
    [InlineData("Deadlock", "6", null, null, "1,5", "lfg.create.duration_invalid")]
    [InlineData("Deadlock", "6", null, null, "iki", "lfg.create.duration_invalid")]
    public async Task Invalid_form_fields_are_refused_with_specific_messages_and_store_nothing(string game, string players, string? details, string? start,
        string? duration, string key)
    {
        var result = await CreateAsync(Form(game, players, details, start, duration));

        result.Result.Succeeded.Should().BeFalse();
        result.Result.MessageKey.Should().Be(key);
        (await ListingCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Too_long_details_are_refused()
    {
        (await CreateAsync(Form(details: new string('a', LfgRules.DetailsMaxLength + 1)))).Result.MessageKey.Should().Be("lfg.create.details_too_long");
        (await CreateAsync(Form(details: new string('a', LfgRules.DetailsMaxLength)))).Result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("şimdi", null)]
    [InlineData("30 dk", 30)]
    [InlineData("45dk", 45)]
    [InlineData("1 saat", 60)]
    [InlineData("1.5 saat", 90)]
    [InlineData("1,5 saat", 90)]
    [InlineData("2 saat", 120)]
    [InlineData("2 saat sonra", 120)]
    [InlineData("1 gün", 1440)]
    public async Task Relative_starts_go_through_the_same_EventAt_pipeline(string start, int? minutes)
    {
        var listing = (await CreateAsync(Form(start: start, duration: "2"))).Listing!;

        listing.EventAt.Should().Be(minutes is { } m ? T0.AddMinutes(m) : null);
        listing.ExpiresAt.Should().Be((listing.EventAt ?? T0).AddHours(2), "ExpiresAt = (EventAt ?? now) + duration");
    }

    [Theory]
    [InlineData("05.10.2026 21:30")]
    [InlineData("5.10.2026 21:30")]
    [InlineData("2026-10-05 21:30")]
    public async Task A_custom_date_is_read_in_the_guild_time_zone(string start)
    {
        var listing = (await CreateAsync(Form(start: start, duration: "3"))).Listing!;

        listing.EventAt.Should().Be(new DateTimeOffset(2026, 10, 5, 18, 30, 0, TimeSpan.Zero), "21:30 in Europe/Istanbul (UTC+3)");
        listing.ExpiresAt.Should().Be(new DateTimeOffset(2026, 10, 5, 21, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Notices_and_voice_come_from_the_settings_step()
    {
        var listing = await OpenAsync(Form(start: "2 saat"), before: true, atStart: true, voice: Voice);
        listing.NotifyBeforeStart.Should().BeTrue();
        listing.NotifyAtStart.Should().BeTrue();
        listing.VoiceChannel.Should().Be(Voice);

        (await CreateAsync(Form(start: ""), before: true, owner: 11)).Result.MessageKey.Should().Be("lfg.create.notice_needs_start");
        (await CreateAsync(Form(start: "2 saat"), voice: TextNotVoice, owner: 11)).Result.MessageKey.Should().Be("lfg.create.voice_invalid");
    }

    [Fact]
    public async Task Checking_a_submitted_form_stores_nothing_and_previews_the_listing()
    {
        var check = await Lfg(s => s.CheckCreateAsync(User(Owner), Channel, LfgForm.ToCreateInput(Form(start: "2 saat", duration: "3"), false, false, null), Ct));

        check.Result.Succeeded.Should().BeTrue();
        check.Preview!.GameName.Should().Be("Deadlock");
        check.Preview.Start.Delay.Should().Be(TimeSpan.FromHours(2));
        check.Preview.EventAt.Should().Be(T0.AddHours(2), "a relative start has a moment, so the settings step offers the notices");
        var draft = _host.Services.GetRequiredService<LfgFormDrafts>().Open(User(Owner), Channel, LfgFormKind.Create, null, LfgFormValues.Empty) with
        {
            Preview = check.Preview,
        };
        LfgFormUi.Settings(draft, T0, (key, _) => key).Components.Components.Cast<global::Discord.ActionRowComponent>().SelectMany(r => r.Components)
            .OfType<global::Discord.SelectMenuComponent>().Select(c => c.Type).Should().Equal(global::Discord.ComponentType.SelectMenu, global::Discord.ComponentType.ChannelSelect);
        check.Preview.Duration.Should().Be(TimeSpan.FromHours(3));
        (await ListingCountAsync()).Should().Be(0);
        (await Lfg(s => s.CheckCreateAsync(User(Owner), Channel, LfgForm.ToCreateInput(Form(players: "x"), false, false, null), Ct)))
            .Result.MessageKey.Should().Be("lfg.create.players_range");
    }

    [Fact]
    public async Task The_form_does_not_open_where_or_when_a_listing_could_not_be_saved()
    {
        (await Lfg(s => s.PrecheckCreateAsync(User(Owner), Channel, Ct))).Should().BeNull();
        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<LfgConfigService>().SetChannelAsync(TestHost.Admin(Guild), Channel.Value, Ct)).Succeeded.Should().BeTrue());
        (await Lfg(s => s.PrecheckCreateAsync(User(Owner), OtherChannel, Ct)))!.MessageKey.Should().Be("lfg.create.wrong_channel");

        await CreateAsync(Form());
        await CreateAsync(Form(game: "CS2"));
        (await Lfg(s => s.PrecheckCreateAsync(User(Owner), Channel, Ct)))!.MessageKey.Should().Be("lfg.create.limit");
        (await CreateAsync(Form(game: "Valheim"))).Result.MessageKey.Should().Be("lfg.create.limit", "saving checks the limit again");
    }

    // ---- who may edit ----

    [Fact]
    public async Task Only_the_owner_can_open_and_save_the_edit_form()
    {
        var listing = await OpenAsync(Form(details: "Casual", start: "05.10.2026 21:30", duration: "2"));

        var opened = await OpenEditAsync(listing.Id);
        opened.Result.Succeeded.Should().BeTrue();
        opened.Prefill.Should().Be(new LfgFormValues("Deadlock", "6", "Casual", "05.10.2026 21:30", "2"), "the stored listing, in the guild's time zone");

        (await OpenEditAsync(listing.Id, User(20))).Result.MessageKey.Should().Be("lfg.edit.forbidden");
        (await OpenEditAsync(listing.Id, Moderator(40))).Result.MessageKey.Should().Be("lfg.edit.forbidden", "moderators close listings, they do not rewrite them");
        (await OpenEditAsync(listing.Id, User(Owner, OtherGuild))).Result.MessageKey.Should().Be("lfg.not_found", "another guild's listing does not exist");

        (await EditAsync(listing.Id, f => f with { Game = "Stolen" }, actor: User(20))).Result.MessageKey.Should().Be("lfg.edit.forbidden");
        (await EditAsync(listing.Id, f => f with { Game = "Stolen" }, actor: Moderator(40))).Result.MessageKey.Should().Be("lfg.edit.forbidden");
        (await EditAsync(listing.Id, f => f with { Game = "Stolen" }, actor: User(Owner, OtherGuild))).Result.MessageKey.Should().Be("lfg.not_found");
        (await GetAsync(listing.Id))!.GameName.Should().Be("Deadlock");

        (await EditAsync(listing.Id, f => f with { Game = "Valheim" })).Result.MessageKey.Should().Be("lfg.edit.done");
        (await GetAsync(listing.Id))!.GameName.Should().Be("Valheim");
    }

    // ---- team size ----

    [Fact]
    public async Task The_team_size_never_goes_below_the_joined_players()
    {
        var listing = await OpenAsync(Form(players: "6"));
        await JoinAsync(listing.Id, 20);
        await JoinAsync(listing.Id, 21);
        await Lfg(s => s.MaybeAsync(User(30), listing.Id, Ct));
        await Lfg(s => s.MaybeAsync(User(31), listing.Id, Ct)); // 3 Joined, 2 Maybe

        (await EditAsync(listing.Id, f => f with { Players = "8" })).Result.Succeeded.Should().BeTrue();
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Open);
        (await EditAsync(listing.Id, f => f with { Players = "4" })).Result.Succeeded.Should().BeTrue("Maybe players never count");
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Open);

        (await EditAsync(listing.Id, f => f with { Players = "3" })).Result.MessageKey.Should().Be("lfg.edit.done");
        var full = (await GetAsync(listing.Id))!;
        full.Status.Should().Be(LfgStatus.Full, "as many slots as Joined players");
        full.MaybePlayers.Should().HaveCount(2);

        var refused = await EditAsync(listing.Id, f => f with { Players = "2", Game = "Valheim", Details = "yeni" });
        refused.Result.MessageKey.Should().Be("lfg.edit.players_below_joined");
        refused.Result.Args.Should().Equal(3);
        var unchanged = (await GetAsync(listing.Id))!;
        (unchanged.MaxPlayers, unchanged.GameName, unchanged.Details).Should().Be((3, "Deadlock", (string?)null), "a refused edit changes no field");

        (await EditAsync(listing.Id, f => f with { Players = "5" })).Result.Succeeded.Should().BeTrue();
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Open, "a full listing reopens when it gets more slots");
    }

    [Fact]
    public async Task Joins_racing_a_smaller_team_size_never_overbook()
    {
        var listing = await OpenAsync(Form(players: "6"));
        await JoinAsync(listing.Id, 20); // 2 Joined

        var edit = EditAsync(listing.Id, f => f with { Players = "3" });
        var joins = new ulong[] { 21, 22, 23 }.Select(u => JoinAsync(listing.Id, u)).ToList();
        await Task.WhenAll(joins.Cast<Task>().Append(edit));

        var after = (await GetAsync(listing.Id))!;
        after.Players.Count.Should().BeLessThanOrEqualTo(after.MaxPlayers, "decided on the stored state inside the write lock");
        if (after.MaxPlayers == 3)
            after.Players.Should().HaveCount(3).And.Contain(new UserId(Owner));
        after.Status.Should().Be(after.Players.Count >= after.MaxPlayers ? LfgStatus.Full : LfgStatus.Open);
        if (!(await edit).Result.Succeeded)
            (await edit).Result.MessageKey.Should().Be("lfg.edit.players_below_joined");
    }

    // ---- texts and the card ----

    [Fact]
    public async Task Game_and_details_edits_redraw_the_same_card_without_pings()
    {
        var listing = await OpenAsync(Form(details: "Casual"));
        await JoinAsync(listing.Id, 20);

        var result = await EditAsync(listing.Id, f => f with { Game = "Val‮heim", Details = "Yagluth @everyone <@&1> <@999>  ertesi   gün" });
        result.Result.MessageKey.Should().Be("lfg.edit.done");
        result.RefreshCard.Should().BeTrue();
        var stored = await RowAsync(listing.Id);
        stored.GameName.Should().Be("Valheim", "format characters are dropped like on create");
        stored.Details.Should().Be("Yagluth @everyone <@&1> <@999> ertesi gün");
        stored.CardStale.Should().BeTrue();

        await _host.InScopeAsync(sp => sp.GetRequiredService<LfgCardSync>().SyncAsync(listing.Id, Ct));

        _host.Transport.Messages.Should().ContainSingle("the card is edited in place, never re-posted");
        var edited = _host.Transport.Messages.Single().Edits.Should().ContainSingle().Subject;
        edited.Embed!.Title.Should().Be("🎮 Valheim");
        edited.Embed.Description.Should().Contain("<@10> · <@20>", "the players stay");
        DiscordText.RawMentionPattern().Matches(edited.Embed.Description!).Select(m => m.Value).Should().BeEquivalentTo(["<@10>", "<@10>", "<@20>"],
            "only the rendered player mentions; the typed ones are defused");
        edited.Mentions.PingsAnything.Should().BeFalse();
        (await RowAsync(listing.Id)).CardStale.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_the_same_form_twice_changes_nothing_the_second_time()
    {
        var listing = await OpenAsync();
        (await EditAsync(listing.Id, f => f with { Details = "Ranked" })).Result.MessageKey.Should().Be("lfg.edit.done");
        var version = (await GetAsync(listing.Id))!.Version;

        var again = await EditAsync(listing.Id, f => f with { Details = "Ranked" });

        again.Result.MessageKey.Should().Be("lfg.edit.unchanged");
        again.RefreshCard.Should().BeFalse();
        (await GetAsync(listing.Id))!.Version.Should().Be(version);
    }

    // ---- start and expiry ----

    [Fact]
    public async Task A_future_start_can_move_and_the_expiry_follows_it()
    {
        var listing = await OpenAsync(Form(start: "2 saat", duration: "2"));

        (await EditAsync(listing.Id, f => f with { Start = "05.10.2026 21:30" })).Result.MessageKey.Should().Be("lfg.edit.done");
        var moved = (await GetAsync(listing.Id))!;
        moved.EventAt.Should().Be(new DateTimeOffset(2026, 10, 5, 18, 30, 0, TimeSpan.Zero));
        moved.ExpiresAt.Should().Be(moved.EventAt!.Value.AddHours(2));

        (await EditAsync(listing.Id, f => f with { Start = "3 saat" })).Result.Succeeded.Should().BeTrue();
        (await GetAsync(listing.Id))!.EventAt.Should().Be(T0.AddHours(3));

        (await EditAsync(listing.Id, f => f with { Duration = "3" })).Result.Succeeded.Should().BeTrue();
        var longer = (await GetAsync(listing.Id))!;
        (longer.EventAt, longer.ExpiresAt).Should().Be((T0.AddHours(3), T0.AddHours(6)), "an untouched start stays exactly where it was");

        (await EditAsync(listing.Id, f => f with { Start = "01.01.2026 10:00" })).Result.MessageKey.Should().Be("lfg.create.date_not_future", "never into the past");
        (await EditAsync(listing.Id, f => f with { Start = "iki saat" })).Result.MessageKey.Should().Be("lfg.create.date_format");
        (await EditAsync(listing.Id, f => f with { Duration = "4" })).Result.MessageKey.Should().Be("lfg.create.duration_invalid");
        (await GetAsync(listing.Id))!.ExpiresAt.Should().Be(T0.AddHours(6));
    }

    [Fact]
    public async Task An_empty_start_moves_a_scheduled_listing_to_now()
    {
        var listing = await OpenAsync(Form(start: "2 saat", duration: "1"));

        (await EditAsync(listing.Id, f => f with { Start = "" })).Result.Succeeded.Should().BeTrue();

        var now = (await GetAsync(listing.Id))!;
        (now.EventAt, now.ExpiresAt).Should().Be(((DateTimeOffset?)T0, T0.AddHours(1)));
    }

    [Fact]
    public async Task The_start_is_locked_once_the_event_started_but_the_rest_stays_editable()
    {
        var listing = await OpenAsync(Form(start: "30 dk", duration: "2"));
        _host.Clock.Advance(TimeSpan.FromMinutes(31));

        (await EditAsync(listing.Id, f => f with { Start = "2 saat" })).Result.MessageKey.Should().Be("lfg.edit.start_locked");
        (await EditAsync(listing.Id, f => f with { Start = "05.10.2026 21:30", Game = "CS2" })).Result.MessageKey.Should().Be("lfg.edit.start_locked");
        (await GetAsync(listing.Id))!.GameName.Should().Be("Deadlock");

        (await EditAsync(listing.Id, f => f with { Game = "CS2", Players = "5" })).Result.MessageKey.Should().Be("lfg.edit.done");
        var edited = (await GetAsync(listing.Id))!;
        (edited.GameName, edited.MaxPlayers, edited.EventAt).Should().Be(("CS2", 5, (DateTimeOffset?)T0.AddMinutes(30)));
    }

    [Fact]
    public async Task A_listing_that_started_now_counts_its_duration_from_creation_and_never_restarts()
    {
        var listing = await OpenAsync(Form(duration: "2"));
        _host.Clock.Advance(TimeSpan.FromMinutes(90));

        (await EditAsync(listing.Id, f => f with { Start = "2 saat" })).Result.MessageKey.Should().Be("lfg.edit.start_locked");
        (await EditAsync(listing.Id, f => f with { Duration = "3" })).Result.Succeeded.Should().BeTrue();
        (await GetAsync(listing.Id))!.ExpiresAt.Should().Be(T0.AddHours(3), "CreatedAt + duration, not now + duration");

        (await EditAsync(listing.Id, f => f with { Duration = "1" })).Result.MessageKey.Should().Be("lfg.edit.expiry_passed");
        (await GetAsync(listing.Id))!.ExpiresAt.Should().Be(T0.AddHours(3));
    }

    [Fact]
    public async Task A_duration_that_is_not_a_choice_survives_an_untouched_edit()
    {
        await using var host = await TestHost.CreateAsync(new Dictionary<string, string?> { ["Lfg:DefaultExpirationMinutes"] = "90" });
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, F1TestHostExtensions.ChannelPermissions));
        await host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "lfg", true, Ct)).Succeeded.Should().BeTrue());
        var created = await host.InScopeAsync(sp => sp.GetRequiredService<LfgService>().CreateAsync(User(Owner), Channel, LfgForm.ToCreateInput(Form(), false, false, null), Ct));
        var opened = await host.InScopeAsync(sp => sp.GetRequiredService<LfgService>().OpenEditAsync(User(Owner), created.Listing!.Id, Ct));
        opened.Prefill!.Duration.Should().Be("90 dk");

        var edited = await host.InScopeAsync(sp => sp.GetRequiredService<LfgService>().EditAsync(User(Owner), created.Listing!.Id,
            new LfgEditInput(opened.Prefill with { Details = "Ranked" }, false, false, null), Ct));

        edited.Result.MessageKey.Should().Be("lfg.edit.done");
        edited.Listing!.ExpiresAt.Should().Be(T0.AddMinutes(90));
    }

    // ---- notices ----

    [Fact]
    public async Task A_pending_reminder_follows_the_new_start()
    {
        var listing = await OpenAsync(Form(start: "2 saat"), before: true);
        (await EditAsync(listing.Id, f => f with { Start = "3 saat" })).Result.Succeeded.Should().BeTrue();

        _host.Clock.Advance(TimeSpan.FromMinutes(91)); // the old reminder window
        await TickAsync();
        Delivered().Should().BeEmpty();

        _host.Clock.Advance(TimeSpan.FromMinutes(60)); // 2:31 — the new window
        await TickAsync();
        Delivered().Should().ContainSingle().Which.Message.Content.Should().Contain("<t:" + T0.AddHours(3).ToUnixTimeSeconds() + ":R>");
    }

    [Fact]
    public async Task A_sent_reminder_is_never_sent_again_for_a_new_start()
    {
        var listing = await OpenAsync(Form(start: "40 dk"), before: true);
        _host.Clock.Advance(TimeSpan.FromMinutes(11));
        await TickAsync();
        Delivered().Should().ContainSingle();

        (await EditAsync(listing.Id, f => f with { Start = "3 saat" })).Result.Succeeded.Should().BeTrue();
        (await RowAsync(listing.Id)).ReminderState.Should().Be(LfgNoticeState.Queued, "handled once, never reset");
        _host.Clock.Advance(TimeSpan.FromMinutes(160)); // inside the new reminder window
        await TickAsync();

        Delivered().Should().ContainSingle("no second reminder for the same listing");
    }

    [Fact]
    public async Task Switching_notices_on_after_their_moment_never_sends_them_late()
    {
        var listing = await OpenAsync(Form(start: "30 dk", duration: "2"));
        _host.Clock.Advance(TimeSpan.FromMinutes(32));

        (await EditAsync(listing.Id, before: true, atStart: true)).Result.MessageKey.Should().Be("lfg.edit.done");

        var row = await RowAsync(listing.Id);
        (row.ReminderState, row.StartNoticeState).Should().Be((LfgNoticeState.Skipped, LfgNoticeState.Skipped));
        await TickAsync();
        Delivered().Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_a_notice_off_before_it_is_queued_sends_nothing()
    {
        var listing = await OpenAsync(Form(start: "2 saat"), before: true, atStart: true);
        (await EditAsync(listing.Id, before: false, atStart: false)).Result.Succeeded.Should().BeTrue();

        _host.Clock.Advance(TimeSpan.FromMinutes(121));
        await TickAsync();

        Delivered().Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_off_a_queued_notice_cancels_it_and_switching_on_again_never_repeats_it()
    {
        var listing = await OpenAsync(Form(start: "40 dk"), before: true);
        _host.Clock.Advance(TimeSpan.FromMinutes(11));
        await _host.Services.GetRequiredService<LfgExpiryWorker>().RunOnceAsync(Ct); // queued in the outbox, not delivered yet
        (await NoticesAsync()).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Pending);

        (await EditAsync(listing.Id, before: false)).Result.Succeeded.Should().BeTrue();
        (await NoticesAsync()).Single().Status.Should().Be(OutboxStatus.Cancelled);
        await TickAsync();
        Delivered().Should().BeEmpty();

        (await EditAsync(listing.Id, before: true)).Result.Succeeded.Should().BeTrue();
        (await RowAsync(listing.Id)).ReminderState.Should().Be(LfgNoticeState.Queued);
        _host.Clock.Advance(TimeSpan.FromMinutes(5));
        await TickAsync();
        Delivered().Should().BeEmpty("a handled notice is never handled again");
        (await NoticesAsync()).Should().ContainSingle();
    }

    // ---- voice ----

    [Fact]
    public async Task The_voice_channel_can_be_added_changed_and_removed()
    {
        var listing = await OpenAsync(Form(players: "3"));
        await JoinAsync(listing.Id, 20);

        (await EditAsync(listing.Id, voice: Voice)).Result.MessageKey.Should().Be("lfg.edit.done");
        (await GetAsync(listing.Id))!.VoiceChannel.Should().Be(Voice);
        (await EditAsync(listing.Id, voice: Voice2)).Result.MessageKey.Should().Be("lfg.edit.done");
        (await GetAsync(listing.Id))!.VoiceChannel.Should().Be(Voice2);
        (await EditAsync(listing.Id, voice: new Optional<ChannelId?>(null))).Result.MessageKey.Should().Be("lfg.edit.done");

        var after = (await GetAsync(listing.Id))!;
        after.VoiceChannel.Should().BeNull();
        after.Players.Should().Equal(new UserId(Owner), new UserId(20));
        after.Status.Should().Be(LfgStatus.Open);
    }

    [Fact]
    public async Task The_voice_channel_is_checked_on_every_save()
    {
        var listing = await OpenAsync(voice: Voice);
        _host.Guilds.SetVoiceChannel(OtherGuild, new ChannelId(8999), new VoiceChannelAccess(true, true, BotVoiceBasics));

        (await EditAsync(listing.Id, voice: TextNotVoice)).Result.MessageKey.Should().Be("lfg.create.voice_invalid");
        (await EditAsync(listing.Id, voice: new ChannelId(8999))).Result.MessageKey.Should().Be("lfg.create.voice_invalid", "another guild's channel");

        _host.Guilds.RemoveVoiceChannel(Guild, Voice);
        (await EditAsync(listing.Id, f => f with { Details = "yeni" })).Result.MessageKey.Should().Be("lfg.create.voice_invalid", "the kept channel is gone");
        (await GetAsync(listing.Id))!.Details.Should().BeNull();
        (await EditAsync(listing.Id, f => f with { Details = "yeni" }, voice: new Optional<ChannelId?>(null))).Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_next_notice_uses_the_new_voice_channel()
    {
        var listing = await OpenAsync(Form(start: "40 dk"), before: true, voice: Voice);
        (await EditAsync(listing.Id, voice: Voice2)).Result.Succeeded.Should().BeTrue();

        _host.Clock.Advance(TimeSpan.FromMinutes(11));
        await TickAsync();

        Delivered().Should().ContainSingle().Which.Message.Content.Should().Contain("<#8903>").And.NotContain("<#8902>");
    }

    // ---- ended listings and races ----

    [Theory]
    [InlineData(LfgStatus.Closed)]
    [InlineData(LfgStatus.Expired)]
    [InlineData(LfgStatus.Orphaned)]
    public async Task Ended_listings_cannot_be_edited(LfgStatus status)
    {
        var listing = await OpenAsync(Form(duration: "1"));
        switch (status)
        {
            case LfgStatus.Closed:
                (await Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct))).Result.MessageKey.Should().Be("lfg.close.done");
                break;
            case LfgStatus.Expired:
                _host.Clock.Advance(TimeSpan.FromMinutes(61));
                break;
            default:
                await _host.InScopeAsync(async sp => await sp.GetRequiredService<ToroDbContext>().Set<LfgListingEntity>().Where(x => x.Id == listing.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, LfgStatus.Orphaned), Ct));
                break;
        }

        (await OpenEditAsync(listing.Id)).Result.MessageKey.Should().Be("lfg.edit.ended");
        var input = new LfgEditInput(Form(game: "Valheim", duration: "1"), false, false, null);
        (await Lfg(s => s.EditAsync(User(Owner), listing.Id, input, Ct))).Result.MessageKey.Should().Be("lfg.edit.ended");
        (await GetAsync(listing.Id))!.GameName.Should().Be("Deadlock");
    }

    [Fact]
    public async Task A_full_listing_can_be_edited()
    {
        var listing = await OpenAsync(Form(players: "2"));
        await JoinAsync(listing.Id, 20);
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Full);

        (await EditAsync(listing.Id, f => f with { Details = "Hazırız" })).Result.MessageKey.Should().Be("lfg.edit.done");

        var edited = (await GetAsync(listing.Id))!;
        (edited.Status, edited.Details).Should().Be((LfgStatus.Full, "Hazırız"));
    }

    [Fact]
    public async Task An_edit_racing_a_close_never_reopens_the_listing()
    {
        var listing = await OpenAsync();

        var edit = EditAsync(listing.Id, f => f with { Details = "yarış" });
        var close = Lfg(s => s.CloseAsync(User(Owner), listing.Id, Ct));
        await Task.WhenAll(edit, close);

        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Closed);
        (await edit).Result.MessageKey.Should().BeOneOf("lfg.edit.done", "lfg.edit.ended");
    }

    [Fact]
    public async Task A_stale_form_saved_after_the_expiry_is_refused()
    {
        var listing = await OpenAsync(Form(duration: "1"));
        var opened = await OpenEditAsync(listing.Id);
        _host.Clock.Advance(TimeSpan.FromMinutes(61));

        var result = await Lfg(s => s.EditAsync(User(Owner), listing.Id, new LfgEditInput(opened.Prefill! with { Duration = "3" }, false, false, null), Ct));

        result.Result.MessageKey.Should().Be("lfg.edit.ended", "the form's snapshot never decides; an expired listing cannot be extended");
        (await GetAsync(listing.Id))!.Status.Should().Be(LfgStatus.Expired);
    }

    /// <summary>A value that may be explicitly set to null (to tell "remove the voice channel" from "keep it").</summary>
    private readonly record struct Optional<T>(T Value)
    {
        public bool IsSet { get; } = true;

        public static implicit operator Optional<T>(T value) => new(value);
    }
}
