using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Giveaway.Application;
using ToroSquad.Modules.Giveaway.Domain;
using ToroSquad.Modules.Giveaway.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Giveaway against the real SQLite database and the production service wiring (fake Discord: the fake transport for the
/// card and announcements, <see cref="OfflineGiveawayReactions"/> for the 🎉 reactions, the fake guild gateway for members):
/// create → enter → draw → result card + winner ping, manual end / cancel / reroll, restart recovery, deleted cards,
/// Discord failures, permissions, privacy, and the races that must never draw a giveaway twice.
/// </summary>
public sealed class GiveawayLifecycleTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(777);
    private static readonly GuildId OtherGuild = new(778);
    private static readonly ChannelId Channel = new(7001);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly ActorContext Admin = TestHost.Admin(Guild, 10);
    private static readonly ActorContext Member = TestHost.Member(Guild, 2);

    private const GuildPermission ChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks |
                                                       GuildPermission.ReadMessageHistory | GuildPermission.AddReactions;

    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        await EnableAsync(_host);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static async Task EnableAsync(TestHost host)
    {
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, ChannelPermissions));
        await host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "giveaway", true, Ct)).Succeeded.Should().BeTrue());
    }

    private Task<T> Giveaways<T>(Func<GiveawayService, Task<T>> action, TestHost? host = null) =>
        (host ?? _host).InScopeAsync(sp => action(sp.GetRequiredService<GiveawayService>()));

    private OfflineGiveawayReactions Reactions(TestHost? host = null) => (host ?? _host).Services.GetRequiredService<OfflineGiveawayReactions>();

    private Task<GiveawayResult> CreateAsync(string? prize = "Discord Nitro", string? duration = "30m", string? winners = "1", string? description = null,
        ActorContext? actor = null, TestHost? host = null) =>
        Giveaways(s => s.CreateAsync(actor ?? Admin, Channel, "Kaan", new GiveawayRequest(prize, duration, winners, description), Ct), host);

    private async Task<GiveawayView> OpenAsync(string duration = "30m", string winners = "1", TestHost? host = null)
    {
        var created = await CreateAsync(duration: duration, winners: winners, host: host);
        created.Result.MessageKey.Should().Be("giveaway.create.done", created.Result.MessageKey);
        return (await GetAsync(created.GiveawayId!.Value, host))!;
    }

    private Task<GiveawayView?> GetAsync(long id, TestHost? host = null) => Giveaways(s => s.GetAsync(id, Ct), host);

    private static string Ref(GiveawayView giveaway) => "#" + giveaway.Id;

    /// <summary>Members of the server who react with 🎉.</summary>
    private void Enter(GiveawayView giveaway, IEnumerable<ulong> users, TestHost? host = null)
    {
        foreach (var user in users)
        {
            (host ?? _host).Guilds.AddMember(Guild, new UserId(user));
            Reactions(host).React(giveaway.Message!.Value, new UserId(user));
        }
    }

    private static IEnumerable<ulong> Range(int count, ulong first = 100) => Enumerable.Range(0, count).Select(i => first + (ulong)i);

    /// <summary>One worker pass after <paramref name="advance"/>, then the outbox is drained into the fake Discord.</summary>
    private async Task TickAsync(TimeSpan? advance = null, TestHost? host = null)
    {
        var h = host ?? _host;
        if (advance is { } by)
            h.Clock.Advance(by);
        await h.Services.GetRequiredService<GiveawayWorker>().RunOnceAsync(Ct);
        var processor = h.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    private Task<GiveawayEntity> RowAsync(long id, TestHost? host = null) => (host ?? _host).InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<GiveawayEntity>().AsNoTracking().SingleAsync(x => x.Id == id));

    private Task<List<GiveawayWinnerEntity>> WinnerRowsAsync(long id, TestHost? host = null) => (host ?? _host).InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<GiveawayWinnerEntity>().AsNoTracking().Where(w => w.GiveawayId == id)
            .OrderBy(w => w.Round).ThenBy(w => w.Place).ToListAsync());

    private Task<List<OutboxMessageEntity>> AnnouncementRowsAsync(TestHost? host = null) => (host ?? _host).InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "giveaway").OrderBy(o => o.Id).ToListAsync());

    private FakeMessageTransport.FakeMessage Card(GiveawayView giveaway, TestHost? host = null) =>
        (host ?? _host).Transport.Messages.Single(m => m.Id == giveaway.Message);

    private static MessageEmbed Shown(FakeMessageTransport.FakeMessage card) => (card.Edits.LastOrDefault() ?? card.Message).Embed!;

    /// <summary>Messages the bot SENT with text content: the winner announcements (the card is an embed).</summary>
    private List<FakeMessageTransport.FakeMessage> Announcements(TestHost? host = null) =>
        (host ?? _host).Transport.Messages.Where(m => m.Message.Content is not null).ToList();

    private static IEnumerable<ulong> Pinged(FakeMessageTransport.FakeMessage m) => m.Message.Mentions.Users?.Select(u => u.Value) ?? [];

    // ---- create ----

    [Fact]
    public async Task Create_posts_the_card_in_the_channel_adds_the_tada_and_stores_the_giveaway()
    {
        var created = await CreateAsync(winners: "2", description: "Kazanana Steam üzerinden gönderilecektir.");

        created.Result.Succeeded.Should().BeTrue();
        created.Result.MessageKey.Should().Be("giveaway.create.done");
        var giveaway = (await GetAsync(created.GiveawayId!.Value))!;
        giveaway.Status.Should().Be(GiveawayStatus.Active);
        giveaway.EndsAt.Should().Be(TestHost.T0.AddMinutes(30));
        giveaway.WinnerCount.Should().Be(2);
        giveaway.Creator.Should().Be(Admin.UserId);

        var card = _host.Transport.Messages.Should().ContainSingle().Subject;
        card.Channel.Should().Be(Channel);
        card.Id.Should().Be(giveaway.Message!.Value);
        card.Pinged.Should().BeFalse();
        card.Message.Embed!.Title.Should().Be("🎉 ÇEKİLİŞ");
        card.Message.Embed.Footer.Should().Be($"Başlatan: Kaan · Çekiliş #{giveaway.Id}", "the creator's own display name, never a fixed one");
        card.Message.Embed.Description.Should().Be("🎁 **ÖDÜL**\n## Discord Nitro");
        card.Message.Embed.Fields.Select(f => f.Name).Should().Equal("🏆 Kazanan Sayısı", "⏰ Bitiş", "🎟️ Katılım", "📝 Açıklama");
        card.Message.Embed.Fields[^1].Value.Should().Be("Kazanana Steam üzerinden gönderilecektir.");
        Reactions().BotReactions.Should().Equal(giveaway.Message.Value);
    }

    [Theory]
    [InlineData(null, "30m", "1", "giveaway.form.prize_required")]
    [InlineData("Nitro", "yarın akşam", "1", "giveaway.form.duration_invalid")]
    [InlineData("Nitro", "0m", "1", "giveaway.form.duration_too_short")]
    [InlineData("Nitro", "60d", "1", "giveaway.form.duration_too_long")]
    [InlineData("Nitro", "30m", "11", "giveaway.form.winners_invalid")]
    [InlineData("Nitro", "30m", "0", "giveaway.form.winners_invalid")]
    public async Task An_invalid_form_is_refused_and_nothing_is_posted_or_stored(string? prize, string duration, string winners, string key)
    {
        var result = await CreateAsync(prize, duration, winners);
        result.Result.Succeeded.Should().BeFalse();
        result.Result.MessageKey.Should().Be(key);
        _host.Transport.Messages.Should().BeEmpty();
        (await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<GiveawayEntity>().CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Members_without_manage_server_can_do_nothing_with_giveaways()
    {
        var giveaway = await OpenAsync();
        (await Giveaways(s => s.PrecheckCreateAsync(Member, Channel, Ct)))!.MessageKey.Should().Be("error.missing_permission");
        (await CreateAsync(actor: Member)).Result.MessageKey.Should().Be("error.missing_permission");
        (await Giveaways(s => s.EndAsync(Member, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("error.missing_permission");
        (await Giveaways(s => s.CancelAsync(Member, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("error.missing_permission");
        (await Giveaways(s => s.RerollAsync(Member, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("error.missing_permission");
        (await Giveaways(s => s.SuggestAsync(Member, false, null, Ct))).Should().BeEmpty();
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Active);

        // A moderator with Manage Messages only is not a server manager either (same model as every TSQ admin command).
        var moderator = new ActorContext(Guild, new UserId(3), GuildPermission.ManageMessages, [], false, 5);
        (await CreateAsync(actor: moderator)).Result.MessageKey.Should().Be("error.missing_permission");
        var administrator = new ActorContext(Guild, new UserId(4), GuildPermission.Administrator, [], false, 5);
        (await CreateAsync(actor: administrator)).Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_channel_the_bot_cannot_run_a_giveaway_in_is_refused_before_the_form_opens()
    {
        _host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, ChannelPermissions & ~GuildPermission.AddReactions & ~GuildPermission.ReadMessageHistory));
        var refusal = (await Giveaways(s => s.PrecheckCreateAsync(Admin, Channel, Ct)))!;
        refusal.MessageKey.Should().Be("giveaway.create.channel_permissions");
        refusal.Args.Single().ToString().Should().Contain("AddReactions").And.Contain("ReadMessageHistory");

        (await Giveaways(s => s.PrecheckCreateAsync(Admin, new ChannelId(9999), Ct)))!.MessageKey.Should().Be("giveaway.create.channel_type");
        (await CreateAsync()).Result.MessageKey.Should().Be("giveaway.create.channel_permissions", "the submit checks again");
        _host.Transport.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task At_most_twenty_giveaways_are_active_per_server()
    {
        for (var i = 0; i < GiveawayRules.MaxActivePerGuild; i++)
            (await CreateAsync()).Result.Succeeded.Should().BeTrue();
        (await Giveaways(s => s.PrecheckCreateAsync(Admin, Channel, Ct)))!.MessageKey.Should().Be("giveaway.create.limit");
        (await CreateAsync()).Result.MessageKey.Should().Be("giveaway.create.limit");
    }

    [Fact]
    public async Task A_card_discord_refused_or_did_not_confirm_leaves_no_giveaway_and_a_lost_response_is_found_again()
    {
        _host.Transport.ScriptSend(() => new SendOutcome.Permanent(PermanentFailureKind.MissingPermissions, "Missing Permissions"));
        (await CreateAsync()).Result.MessageKey.Should().Be("giveaway.create.post_failed");

        _host.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        (await CreateAsync()).Result.MessageKey.Should().Be("giveaway.create.post_uncertain");
        (await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<GiveawayEntity>().CountAsync())).Should().Be(0);

        // Discord accepted the card but the response was lost: the same card is found in the channel and used — never a second one.
        _host.Transport.ScriptAcceptedButTimedOut();
        var created = await CreateAsync();
        created.Result.MessageKey.Should().Be("giveaway.create.done");
        var giveaway = (await GetAsync(created.GiveawayId!.Value))!;
        _host.Transport.Messages.Should().ContainSingle().Which.Id.Should().Be(giveaway.Message!.Value);
    }

    private Task<int> GiveawayCountAsync(TestHost? host = null) =>
        (host ?? _host).InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<GiveawayEntity>().CountAsync());

    [Fact]
    public async Task Without_add_reactions_nothing_is_created_posted_or_stored()
    {
        _host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, ChannelPermissions & ~GuildPermission.AddReactions));

        var refusal = (await Giveaways(s => s.PrecheckCreateAsync(Admin, Channel, Ct)))!;
        refusal.MessageKey.Should().Be("giveaway.create.add_reactions", "checked before the form opens");
        var created = await CreateAsync();
        created.Result.Succeeded.Should().BeFalse();
        created.Result.MessageKey.Should().Be("giveaway.create.add_reactions", "and again on submit");
        created.GiveawayId.Should().BeNull();

        _host.Transport.Messages.Should().BeEmpty("no card is sent");
        _host.Transport.SendCalls.Should().Be(0);
        (await GiveawayCountAsync()).Should().Be(0, "no row is stored");
        Reactions().BotReactions.Should().BeEmpty();
        _host.Services.GetRequiredService<ToroSquad.Core.Localization.ILocalizer>().Get("tr", refusal.MessageKey)
            .Should().Be("❌ Botun bu kanalda **Tepki Ekle (Add Reactions)** iznine ihtiyacı var.");
        _host.Services.GetRequiredService<ToroSquad.Core.Localization.ILocalizer>().Get("en", refusal.MessageKey)
            .Should().Be("❌ The bot needs the **Add Reactions** permission in this channel.");

        // With Add Reactions (Administrator implies it) creation continues normally.
        _host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, ChannelPermissions));
        (await CreateAsync()).Result.MessageKey.Should().Be("giveaway.create.done");
        _host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, GuildPermission.Administrator));
        (await CreateAsync()).Result.MessageKey.Should().Be("giveaway.create.done");
        (await GiveawayCountAsync()).Should().Be(2);
        Reactions().BotReactions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_bot_reaction_discord_refuses_after_the_precheck_cancels_the_giveaway_at_once()
    {
        Reactions().ScriptedAdds.Enqueue(ReactionAddOutcome.Failed);
        var created = await CreateAsync();

        created.Result.Succeeded.Should().BeFalse();
        created.Result.MessageKey.Should().Be("giveaway.create.reaction_failed");
        var row = await RowAsync(created.GiveawayId!.Value);
        row.Status.Should().Be(GiveawayStatus.Cancelled, "a giveaway without the bot's 🎉 never runs");
        Shown(_host.Transport.Messages.Single()).Title.Should().Be("⚠️ ÇEKİLİŞ İPTAL EDİLDİ");
        await TickAsync(TimeSpan.FromHours(1));
        Reactions().Reads.Should().Be(0);
        Announcements().Should().BeEmpty();
    }

    // ---- module disabled: new giveaways stop, started ones finish ----

    [Fact]
    public async Task Disabling_the_module_stops_new_giveaways_but_a_started_one_completes_with_its_announcement()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(5));
        await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(Admin, "giveaway", false, Ct)).Succeeded.Should().BeTrue());
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Active, "disabling never cancels a started giveaway");

        (await Giveaways(s => s.PrecheckCreateAsync(Admin, Channel, Ct)))!.MessageKey.Should().Be("error.module_disabled");
        (await CreateAsync()).Result.MessageKey.Should().Be("error.module_disabled");
        (await GiveawayCountAsync()).Should().Be(1);
        _host.Transport.Messages.Should().ContainSingle("no new card");

        await TickAsync(TimeSpan.FromMinutes(30));
        await TickAsync(TimeSpan.FromMinutes(1));

        var row = await RowAsync(giveaway.Id);
        row.Status.Should().Be(GiveawayStatus.Finished);
        var winner = (await WinnerRowsAsync(giveaway.Id)).Should().ContainSingle().Subject;
        Shown(Card(giveaway)).Title.Should().Be("🎉 ÇEKİLİŞ SONUÇLANDI");
        var announcement = Announcements().Should().ContainSingle().Subject;
        Pinged(announcement).Should().Equal(winner.UserId);
        (await AnnouncementRowsAsync()).Should().ContainSingle().Which.Status.Should().Be(ToroSquad.Core.Notifications.OutboxStatus.Sent);
    }

    [Fact]
    public async Task While_disabled_the_create_command_is_refused_by_the_module_gate()
    {
        await _host.InScopeAsync(async sp =>
        {
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(Admin, "giveaway", false, Ct)).Succeeded.Should().BeTrue();
            var gated = new ToroSquad.Discord.Interactions.ToroModuleAttribute(ToroSquad.Modules.Giveaway.GiveawayModule.ModuleIdValue);
            var guild = InterfaceFake.Create<global::Discord.IGuild>(new() { ["Id"] = Guild.Value, ["OwnerId"] = 1UL });
            var user = InterfaceFake.Create<global::Discord.IGuildUser>(new()
            {
                ["Id"] = Admin.UserId.Value,
                ["GuildPermissions"] = new global::Discord.GuildPermissions((ulong)GuildPermission.ManageGuild),
                ["RoleIds"] = (IReadOnlyCollection<ulong>)Array.Empty<ulong>(),
            });
            var context = InterfaceFake.Create<global::Discord.IInteractionContext>(new() { ["Guild"] = guild, ["User"] = user });
            (await gated.CheckRequirementsAsync(context, null!, sp)).ErrorReason.Should().Be(ToroSquad.Discord.Interactions.ToroModuleAttribute.ModuleDisabledError);
        });
    }

    // ---- the draw ----

    [Fact]
    public async Task When_time_is_up_the_worker_draws_edits_the_same_card_and_pings_only_the_winner()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(10));

        await TickAsync(TimeSpan.FromMinutes(29));
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Active, "not due yet");
        Reactions().Reads.Should().Be(0);

        await TickAsync(TimeSpan.FromMinutes(1));
        var row = await RowAsync(giveaway.Id);
        row.Status.Should().Be(GiveawayStatus.Finished);
        row.EntrantCount.Should().Be(10);
        row.EndedByUserId.Should().BeNull("automatic");
        row.CardStale.Should().BeFalse();
        var winner = (await WinnerRowsAsync(giveaway.Id)).Should().ContainSingle().Subject;
        (winner.Round, winner.Place).Should().Be((0, 1));
        Range(10).Should().Contain(winner.UserId);

        _host.Transport.Messages.Count(m => m.Message.Embed is not null).Should().Be(1, "the result is the same message, edited");
        var shown = Shown(Card(giveaway));
        shown.Title.Should().Be("🎉 ÇEKİLİŞ SONUÇLANDI");
        shown.Fields.Should().Contain(f => f.Name == "🏆 Kazanan" && f.Value == $"🥇 <@{winner.UserId}>");
        shown.Fields.Should().Contain(f => f.Name == "👥 Katılımcı" && f.Value == "10");
        Card(giveaway).Edits.Should().OnlyContain(e => !e.Mentions.PingsAnything);

        var announcement = Announcements().Should().ContainSingle().Subject;
        Pinged(announcement).Should().Equal(winner.UserId);
        announcement.Message.Content.Should().Contain("**Discord Nitro**").And.Contain($"/{giveaway.Message!.Value}");

        await TickAsync(TimeSpan.FromMinutes(5));
        (await WinnerRowsAsync(giveaway.Id)).Should().ContainSingle("a finished giveaway is never drawn again");
        Announcements().Should().ContainSingle();
    }

    [Fact]
    public async Task Three_winners_of_ten_are_unique_entrants_bots_and_removed_reactions_never_count()
    {
        var giveaway = await OpenAsync(winners: "3");
        Enter(giveaway, Range(10));
        Reactions().React(giveaway.Message!.Value, new UserId(555), isBot: true);
        Reactions().React(giveaway.Message.Value, new UserId(556), isBot: true);
        Enter(giveaway, [200]);
        Reactions().Unreact(giveaway.Message.Value, new UserId(200));

        await TickAsync(TimeSpan.FromMinutes(30));

        var winners = (await WinnerRowsAsync(giveaway.Id)).Select(w => w.UserId).ToList();
        winners.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        winners.Should().OnlyContain(u => Range(10).Contains(u), "bots and a removed reaction are not entrants");
        (await RowAsync(giveaway.Id)).EntrantCount.Should().Be(10);
        Pinged(Announcements().Single()).Should().BeEquivalentTo(winners);
        Shown(Card(giveaway)).Fields.Single(f => f.Name == "🏆 Kazananlar").Value.Should().StartWith("🥇 <@").And.Contain("🥈").And.Contain("🥉");
    }

    [Fact]
    public async Task Fewer_entrants_than_winners_all_win_and_members_who_left_are_skipped()
    {
        var giveaway = await OpenAsync(winners: "5");
        Enter(giveaway, Range(4));
        _host.Guilds.RemoveMember(Guild, new UserId(100));
        _host.Guilds.RemoveMember(Guild, new UserId(101));

        await TickAsync(TimeSpan.FromMinutes(30));

        (await WinnerRowsAsync(giveaway.Id)).Select(w => w.UserId).Should().BeEquivalentTo(new ulong[] { 102, 103 });
        (await RowAsync(giveaway.Id)).EntrantCount.Should().Be(4, "they reacted; only winning needs membership");
    }

    [Fact]
    public async Task No_valid_entrant_ends_the_giveaway_without_a_winner_and_without_a_ping()
    {
        var giveaway = await OpenAsync();
        Reactions().React(giveaway.Message!.Value, new UserId(555), isBot: true);

        var ended = await Giveaways(s => s.EndAsync(Admin, Ref(giveaway), Ct));

        ended.Result.MessageKey.Should().Be("giveaway.end.no_entrants");
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Finished);
        (await WinnerRowsAsync(giveaway.Id)).Should().BeEmpty();
        Shown(Card(giveaway)).Fields[0].Value.Should().Be("Çekiliş sona erdi ancak geçerli katılımcı bulunamadı.");
        await TickAsync();
        Announcements().Should().BeEmpty();
        (await AnnouncementRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Discord_that_cannot_be_read_postpones_the_draw_with_backoff_instead_of_guessing()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(3));
        Reactions().ScriptedReads.Enqueue(new EntrantRead.Unavailable("503"));

        await TickAsync(TimeSpan.FromMinutes(30));
        var row = await RowAsync(giveaway.Id);
        row.Status.Should().Be(GiveawayStatus.Active);
        row.DrawAttempts.Should().Be(1);
        row.NextDrawAttemptAt.Should().Be(_host.Clock.GetUtcNow().AddMinutes(1));

        await TickAsync(TimeSpan.FromSeconds(30));
        Reactions().Reads.Should().Be(1, "waiting for the backoff");

        _host.Guilds.ScriptedMemberLookups.Enqueue(MemberLookupOutcome.Unavailable);
        await TickAsync(TimeSpan.FromSeconds(30));
        row = await RowAsync(giveaway.Id);
        row.Status.Should().Be(GiveawayStatus.Active, "a member lookup Discord could not answer postpones too");
        row.DrawAttempts.Should().Be(2);
        row.NextDrawAttemptAt.Should().Be(_host.Clock.GetUtcNow().AddMinutes(2));
        (await WinnerRowsAsync(giveaway.Id)).Should().BeEmpty();

        await TickAsync(TimeSpan.FromMinutes(2));
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Finished);
        (await WinnerRowsAsync(giveaway.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_deleted_card_or_channel_orphans_the_giveaway_without_a_draw()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(3));
        Reactions().Delete(giveaway.Message!.Value);

        await TickAsync(TimeSpan.FromMinutes(30));

        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Orphaned);
        (await WinnerRowsAsync(giveaway.Id)).Should().BeEmpty();
        Card(giveaway).Edits.Should().BeEmpty();
        Announcements().Should().BeEmpty();
        (await Giveaways(s => s.EndAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.state.orphaned");
    }

    [Fact]
    public async Task A_failed_card_edit_is_retried_by_the_worker_and_the_result_is_never_lost()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(2));
        // The edit right after the draw and the same pass's retry both fail; the next pass succeeds.
        _host.Transport.ScriptEdit(() => new SendOutcome.Transient("502"));
        _host.Transport.ScriptEdit(() => new SendOutcome.RateLimited(TimeSpan.FromSeconds(5)));

        await TickAsync(TimeSpan.FromMinutes(30));
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Finished);
        (await RowAsync(giveaway.Id)).CardStale.Should().BeTrue();
        (await RowAsync(giveaway.Id)).CardSyncAttempts.Should().Be(2);
        Card(giveaway).Edits.Should().BeEmpty();

        await TickAsync(TimeSpan.FromSeconds(30));
        (await RowAsync(giveaway.Id)).CardStale.Should().BeFalse();
        Shown(Card(giveaway)).Title.Should().Be("🎉 ÇEKİLİŞ SONUÇLANDI");
        Announcements().Should().ContainSingle();
    }

    // ---- manual end / cancel ----

    [Fact]
    public async Task End_draws_at_once_through_the_same_path_by_number_message_link_or_message_id()
    {
        var byNumber = await OpenAsync(duration: "2d");
        var byLink = await OpenAsync(duration: "2d");
        var byId = await OpenAsync(duration: "2d");
        foreach (var g in new[] { byNumber, byLink, byId })
            Enter(g, Range(3));

        (await Giveaways(s => s.EndAsync(Admin, Ref(byNumber), Ct))).Result.MessageKey.Should().Be("giveaway.end.done");
        (await Giveaways(s => s.EndAsync(Admin, $"https://discord.com/channels/{Guild.Value}/{Channel.Value}/{byLink.Message!.Value}", Ct))).Result.MessageKey
            .Should().Be("giveaway.end.done");
        (await Giveaways(s => s.EndAsync(Admin, byId.Message!.Value.ToString(), Ct))).Result.MessageKey
            .Should().Be("giveaway.end.done");

        foreach (var g in new[] { byNumber, byLink, byId })
        {
            var row = await RowAsync(g.Id);
            row.Status.Should().Be(GiveawayStatus.Finished);
            row.EndedByUserId.Should().Be(Admin.UserId.Value);
            (await WinnerRowsAsync(g.Id)).Should().ContainSingle();
        }

        (await Giveaways(s => s.EndAsync(Admin, Ref(byNumber), Ct))).Result.MessageKey.Should().Be("giveaway.state.finished");
        (await Giveaways(s => s.EndAsync(Admin, "#9999", Ct))).Result.MessageKey.Should().Be("giveaway.not_found");
        (await Giveaways(s => s.EndAsync(Admin, "yarın", Ct))).Result.MessageKey.Should().Be("giveaway.not_found");
    }

    [Fact]
    public async Task Another_servers_giveaway_is_not_found_by_number_or_link()
    {
        var giveaway = await OpenAsync();
        var foreignAdmin = TestHost.Admin(OtherGuild, 10);
        (await Giveaways(s => s.EndAsync(foreignAdmin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.not_found");
        (await Giveaways(s => s.CancelAsync(foreignAdmin, $"https://discord.com/channels/{OtherGuild.Value}/{Channel.Value}/{giveaway.Message!.Value}", Ct)))
            .Result.MessageKey.Should().Be("giveaway.not_found");
        (await Giveaways(s => s.EndAsync(Admin, $"https://discord.com/channels/{OtherGuild.Value}/{Channel.Value}/{giveaway.Message!.Value}", Ct)))
            .Result.MessageKey.Should().Be("giveaway.not_found", "a link to another server never resolves here");
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Active);
    }

    [Fact]
    public async Task Cancel_ends_without_a_draw_and_a_cancelled_giveaway_is_never_drawn_later()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(5));

        (await Giveaways(s => s.CancelAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.cancel.done");
        var row = await RowAsync(giveaway.Id);
        row.Status.Should().Be(GiveawayStatus.Cancelled);
        row.EndedByUserId.Should().Be(Admin.UserId.Value);
        await _host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "giveaway");
            (await data.PreviewDeletionAsync(Guild, Admin.UserId, Ct)).Select(i => i.LabelKey).Should().Contain("giveaway.privacy.ended");
        });
        var shown = Shown(Card(giveaway));
        shown.Title.Should().Be("⚠️ ÇEKİLİŞ İPTAL EDİLDİ");
        shown.Description.Should().Be("🎁 **ÖDÜL**\n## Discord Nitro", "the prize stays visible");
        shown.Fields.Should().Contain(new EmbedField("📌 Durum", "Bu çekiliş iptal edildi."));

        await TickAsync(TimeSpan.FromHours(1));
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Cancelled);
        Reactions().Reads.Should().Be(0, "the worker never even reads a cancelled giveaway");
        (await WinnerRowsAsync(giveaway.Id)).Should().BeEmpty();
        Announcements().Should().BeEmpty();

        (await Giveaways(s => s.EndAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.state.cancelled");
        (await Giveaways(s => s.CancelAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.state.cancelled");
        (await Giveaways(s => s.RerollAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.state.cancelled");
    }

    [Fact]
    public async Task A_finished_giveaway_cannot_be_cancelled_or_ended_again()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(2));
        await TickAsync(TimeSpan.FromMinutes(30));
        var winners = await WinnerRowsAsync(giveaway.Id);

        (await Giveaways(s => s.CancelAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.state.finished");
        (await Giveaways(s => s.EndAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.state.finished");
        await TickAsync(TimeSpan.FromMinutes(30));
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Finished);
        (await WinnerRowsAsync(giveaway.Id)).Should().BeEquivalentTo(winners);
        Announcements().Should().ContainSingle();
    }

    // ---- races: one draw, whoever gets there first ----

    /// <summary>Lets a test run another operation to completion while a draw is between reading Discord and committing.</summary>
    private sealed class InterleavingReactions(OfflineGiveawayReactions inner) : IGiveawayReactions
    {
        private Func<Task>? _duringNextRead;

        public void DuringNextRead(Func<Task> action) => _duringNextRead = action;

        public Task<ReactionAddOutcome> AddEntryReactionAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken) =>
            inner.AddEntryReactionAsync(channel, message, cancellationToken);

        public async Task<EntrantRead> ReadEntrantsAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken)
        {
            var read = await inner.ReadEntrantsAsync(channel, message, cancellationToken);
            if (Interlocked.Exchange(ref _duringNextRead, null) is { } during)
                await during();
            return read;
        }
    }

    private static Task<TestHost> InterleavingHostAsync() => TestHost.CreateAsync(replace: services =>
    {
        services.AddSingleton(sp => new InterleavingReactions(sp.GetRequiredService<OfflineGiveawayReactions>()));
        services.AddSingleton<IGiveawayReactions>(sp => sp.GetRequiredService<InterleavingReactions>());
    });

    [Fact]
    public async Task A_manual_end_that_lands_while_the_worker_is_drawing_wins_and_the_worker_discards_its_draw()
    {
        await using var host = await InterleavingHostAsync();
        await EnableAsync(host);
        var giveaway = await OpenAsync(host: host);
        Enter(giveaway, Range(10), host);
        GiveawayResult? manual = null;
        host.Services.GetRequiredService<InterleavingReactions>().DuringNextRead(async () =>
            manual = await Giveaways(s => s.EndAsync(Admin, Ref(giveaway), Ct), host));

        await TickAsync(TimeSpan.FromMinutes(30), host);

        manual!.Result.MessageKey.Should().Be("giveaway.end.done");
        var row = await RowAsync(giveaway.Id, host);
        row.Status.Should().Be(GiveawayStatus.Finished);
        row.EndedByUserId.Should().Be(Admin.UserId.Value, "the manual end committed first");
        (await WinnerRowsAsync(giveaway.Id, host)).Should().ContainSingle("the worker's own draw was thrown away");
        (await AnnouncementRowsAsync(host)).Should().ContainSingle();
        Announcements(host).Should().ContainSingle();
    }

    [Fact]
    public async Task A_cancel_that_lands_while_a_draw_is_running_wins_and_nothing_is_drawn()
    {
        await using var host = await InterleavingHostAsync();
        await EnableAsync(host);
        var giveaway = await OpenAsync(host: host);
        Enter(giveaway, Range(10), host);
        host.Services.GetRequiredService<InterleavingReactions>().DuringNextRead(async () =>
            (await Giveaways(s => s.CancelAsync(Admin, Ref(giveaway), Ct), host)).Result.MessageKey.Should().Be("giveaway.cancel.done"));

        var ended = await Giveaways(s => s.EndAsync(Admin, Ref(giveaway), Ct), host);

        ended.Result.MessageKey.Should().Be("giveaway.state.cancelled");
        (await RowAsync(giveaway.Id, host)).Status.Should().Be(GiveawayStatus.Cancelled);
        (await WinnerRowsAsync(giveaway.Id, host)).Should().BeEmpty();
        (await AnnouncementRowsAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task Simultaneous_worker_passes_and_manual_ends_draw_exactly_once()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(20));
        _host.Clock.Advance(TimeSpan.FromMinutes(30));
        var worker = _host.Services.GetRequiredService<GiveawayWorker>();

        var ends = Enumerable.Range(0, 3).Select(_ => Task.Run(() => Giveaways(s => s.EndAsync(Admin, Ref(giveaway), Ct)))).ToList();
        var passes = Enumerable.Range(0, 3).Select(_ => Task.Run(() => worker.RunOnceAsync(Ct))).ToList();
        await Task.WhenAll(passes);
        var results = await Task.WhenAll(ends);

        results.Count(r => r.Result.MessageKey == "giveaway.end.done").Should().BeLessThanOrEqualTo(1);
        results.Where(r => r.Result.MessageKey != "giveaway.end.done").Should().OnlyContain(r => r.Result.MessageKey == "giveaway.state.finished");
        (await RowAsync(giveaway.Id)).Status.Should().Be(GiveawayStatus.Finished);
        (await WinnerRowsAsync(giveaway.Id)).Should().ContainSingle();
        (await AnnouncementRowsAsync()).Should().ContainSingle();
    }

    // ---- restart ----

    [Fact]
    public async Task A_giveaway_survives_a_restart_and_one_that_ended_while_the_bot_was_down_is_drawn_on_the_first_pass()
    {
        var ended = await OpenAsync(duration: "10m");
        var running = await OpenAsync(duration: "1d");
        Enter(ended, Range(5));
        Enter(running, Range(5, 200));

        // "Restart": a new process on the same database, the same Discord channel and reactions, two hours later.
        var reactions = Reactions();
        await using var second = await TestHost.CreateAsync(new() { ["Bot:DataDirectory"] = _host.Directory }, TestHost.T0.AddHours(2), services =>
        {
            services.AddSingleton(_host.Transport);
            services.AddSingleton<IMessageTransport>(_host.Transport);
            services.AddSingleton(reactions);
            services.AddSingleton<IGiveawayReactions>(reactions);
        });
        second.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, ChannelPermissions));
        foreach (var user in Range(5).Concat(Range(5, 200)))
            second.Guilds.AddMember(Guild, new UserId(user));

        await TickAsync(host: second);

        (await RowAsync(ended.Id, second)).Status.Should().Be(GiveawayStatus.Finished);
        (await WinnerRowsAsync(ended.Id, second)).Single().UserId.Should().BeInRange(100, 104);
        Shown(Card(ended)).Title.Should().Be("🎉 ÇEKİLİŞ SONUÇLANDI");
        Announcements().Should().ContainSingle();
        (await RowAsync(running.Id, second)).Status.Should().Be(GiveawayStatus.Active);

        await TickAsync(TimeSpan.FromDays(1), second);
        (await RowAsync(running.Id, second)).Status.Should().Be(GiveawayStatus.Finished);
        (await WinnerRowsAsync(running.Id, second)).Single().UserId.Should().BeInRange(200, 204);
    }

    [Fact]
    public async Task The_layout_refresh_migration_redraws_existing_cards_once_by_edit_without_pings()
    {
        var active = await OpenAsync(duration: "1d");
        var drawn = await OpenAsync();
        var cancelled = await OpenAsync(duration: "1d");
        var orphaned = await OpenAsync(duration: "1d");
        Enter(drawn, Range(2));
        await TickAsync(TimeSpan.FromMinutes(30));
        (await Giveaways(s => s.CancelAsync(Admin, Ref(cancelled), Ct))).Result.Succeeded.Should().BeTrue();
        Reactions().Delete(orphaned.Message!.Value);
        (await Giveaways(s => s.EndAsync(Admin, Ref(orphaned), Ct))).Result.MessageKey.Should().Be("giveaway.end.message_missing");
        await TickAsync();
        var editsBefore = _host.Transport.Messages.ToDictionary(m => m.Id, m => m.Edits.Count);
        var messagesBefore = _host.Transport.Messages.Count;

        (await _host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ToroDbContext>().Database.GetAppliedMigrationsAsync()).ToList()))
            .Should().Contain(m => m.EndsWith("_GiveawayCardLayoutRefresh", StringComparison.Ordinal));
        await _host.InScopeAsync(async sp =>
            await sp.GetRequiredService<ToroDbContext>().Database.ExecuteSqlRawAsync(ToroSquad.Bot.Migrations.GiveawayCardLayoutRefresh.RedrawExistingCards));
        await TickAsync();
        await TickAsync(TimeSpan.FromSeconds(30));

        _host.Transport.Messages.Should().HaveCount(messagesBefore, "a redraw edits; it never posts");
        foreach (var g in new[] { active, drawn, cancelled })
        {
            Card(g).Edits.Should().HaveCount(editsBefore[g.Message!.Value] + 1, "exactly one redraw");
            Card(g).Edits[^1].Mentions.PingsAnything.Should().BeFalse();
            Shown(Card(g)).Description.Should().StartWith("🎁 **ÖDÜL**\n## ");
            (await RowAsync(g.Id)).CardStale.Should().BeFalse();
        }

        Shown(Card(active)).Title.Should().Be("🎉 ÇEKİLİŞ");
        Shown(Card(drawn)).Title.Should().Be("🎉 ÇEKİLİŞ SONUÇLANDI");
        Shown(Card(cancelled)).Title.Should().Be("⚠️ ÇEKİLİŞ İPTAL EDİLDİ");
        (await RowAsync(active.Id)).Status.Should().Be(GiveawayStatus.Active, "a redraw changes nothing but the card");
        (await RowAsync(orphaned.Id)).CardStale.Should().BeFalse("an orphaned card no longer exists");
        Announcements().Should().ContainSingle("no second winner ping");
    }

    [Fact]
    public async Task A_row_whose_card_was_never_confirmed_is_orphaned_after_the_grace_period()
    {
        var id = await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            var row = new GiveawayEntity
            {
                GuildId = Guild.Value,
                ChannelId = Channel.Value,
                CreatorUserId = 10,
                CreatorName = "Kaan",
                Prize = "Nitro",
                WinnerCount = 1,
                Status = GiveawayStatus.Active,
                CreatedAt = TestHost.T0,
                EndsAt = TestHost.T0.AddMinutes(5),
            };
            db.Set<GiveawayEntity>().Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });

        await TickAsync(TimeSpan.FromMinutes(6));
        (await RowAsync(id)).Status.Should().Be(GiveawayStatus.Active, "still within the grace period; the worker never draws a card it does not know");
        Reactions().Reads.Should().Be(0);

        await TickAsync(GiveawayService.UnpostedGrace);
        (await RowAsync(id)).Status.Should().Be(GiveawayStatus.Orphaned);
    }

    // ---- reroll ----

    [Fact]
    public async Task Reroll_draws_someone_who_has_not_won_yet_and_keeps_the_history()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, Range(3));
        await TickAsync(TimeSpan.FromMinutes(30));
        var first = (await WinnerRowsAsync(giveaway.Id)).Single().UserId;

        (await Giveaways(s => s.RerollAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.reroll.done");
        await TickAsync();
        var second = (await WinnerRowsAsync(giveaway.Id)).Single(w => w.Round == 1).UserId;
        second.Should().NotBe(first);
        (await RowAsync(giveaway.Id)).RerollCount.Should().Be(1);
        var shown = Shown(Card(giveaway));
        shown.Fields.Single(f => f.Name == "🏆 Kazanan").Value.Should().Be($"🥇 <@{second}>");
        shown.Footer.Should().EndWith("1. kez yeniden çekildi");
        Announcements().Should().HaveCount(2);
        Pinged(Announcements()[1]).Should().Equal(second);
        Announcements()[1].Message.Content.Should().StartWith("🔁");

        (await Giveaways(s => s.RerollAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.reroll.done");
        var third = (await WinnerRowsAsync(giveaway.Id)).Single(w => w.Round == 2).UserId;
        new[] { first, second, third }.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Range(3), "every earlier winner is left out");

        (await Giveaways(s => s.RerollAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.reroll.no_candidates");
        (await RowAsync(giveaway.Id)).RerollCount.Should().Be(2, "nothing changes when nobody else can win");
        (await WinnerRowsAsync(giveaway.Id)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Reroll_with_too_few_other_entrants_draws_all_of_them()
    {
        var giveaway = await OpenAsync(winners: "3");
        Enter(giveaway, Range(4));
        await TickAsync(TimeSpan.FromMinutes(30));
        var first = (await WinnerRowsAsync(giveaway.Id)).Select(w => w.UserId).ToList();

        (await Giveaways(s => s.RerollAsync(Admin, Ref(giveaway), Ct))).Result.MessageKey.Should().Be("giveaway.reroll.done_fewer");
        var rerolled = (await WinnerRowsAsync(giveaway.Id)).Where(w => w.Round == 1).Select(w => w.UserId).ToList();
        rerolled.Should().ContainSingle().Which.Should().BeOneOf(Range(4).Except(first));
    }

    [Fact]
    public async Task Reroll_needs_a_drawn_giveaway_and_readable_reactions()
    {
        var active = await OpenAsync();
        (await Giveaways(s => s.RerollAsync(Admin, Ref(active), Ct))).Result.MessageKey.Should().Be("giveaway.reroll.not_finished");

        Enter(active, Range(2));
        await TickAsync(TimeSpan.FromMinutes(30));
        Reactions().ScriptedReads.Enqueue(new EntrantRead.Unavailable("403"));
        (await Giveaways(s => s.RerollAsync(Admin, Ref(active), Ct))).Result.MessageKey.Should().Be("giveaway.reroll.unavailable");
        Reactions().Delete(active.Message!.Value);
        (await Giveaways(s => s.RerollAsync(Admin, Ref(active), Ct))).Result.MessageKey.Should().Be("giveaway.reroll.message_missing");
        (await RowAsync(active.Id)).Status.Should().Be(GiveawayStatus.Finished, "a stored result is never lost");
        (await RowAsync(active.Id)).RerollCount.Should().Be(0);
    }

    // ---- autocomplete, privacy ----

    [Fact]
    public async Task Autocomplete_lists_this_servers_active_or_drawn_giveaways()
    {
        var active = await OpenAsync(duration: "1d");
        var drawn = await OpenAsync();
        Enter(drawn, Range(1));
        await TickAsync(TimeSpan.FromMinutes(30));

        (await Giveaways(s => s.SuggestAsync(Admin, false, null, Ct))).Should().Equal(new GiveawaySuggestion($"#{active.Id} · Discord Nitro", active.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        (await Giveaways(s => s.SuggestAsync(Admin, true, "nitro", Ct))).Select(x => x.Value).Should().Equal(drawn.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await Giveaways(s => s.SuggestAsync(TestHost.Admin(OtherGuild), false, null, Ct))).Should().BeEmpty();
    }

    [Fact]
    public async Task Privacy_exports_and_deletes_a_members_wins_and_started_giveaways()
    {
        var giveaway = await OpenAsync();
        Enter(giveaway, [100]);
        await TickAsync(TimeSpan.FromMinutes(30));

        await _host.InScopeAsync(async sp =>
        {
            var data = sp.GetServices<IUserDataContributor>().Single(c => c.Module.Value == "giveaway");
            (await data.ExportAsync(Guild, new UserId(100), Ct))["won"]!.AsArray().Should().ContainSingle();
            (await data.ExportAsync(Guild, Admin.UserId, Ct))["started"]!.AsArray().Should().ContainSingle();
            (await data.PreviewDeletionAsync(Guild, new UserId(100), Ct)).Should().ContainSingle(i => i.LabelKey == "giveaway.privacy.won");
            (await data.DeleteAsync(Guild, new UserId(100), Ct)).RecordsDeleted.Should().Be(1);
            (await data.DeleteAsync(Guild, Admin.UserId, Ct)).RecordsDeleted.Should().Be(1);
            (await data.ExportAsync(Guild, Admin.UserId, Ct))["endedOrCancelled"]!.AsArray().Should().BeEmpty();
            (await data.ExportAsync(Guild, Admin.UserId, Ct))["started"]!.AsArray().Should().BeEmpty();
        });
        (await WinnerRowsAsync(giveaway.Id)).Should().BeEmpty();
        var row = await RowAsync(giveaway.Id);
        (row.CreatorUserId, row.CreatorName).Should().Be((0UL, ""));

        await TickAsync(GiveawayService.AnnouncementRetention + TimeSpan.FromMinutes(11));
        (await AnnouncementRowsAsync()).Should().BeEmpty("delivered winner pings are pruned after 24 hours");
    }
}
