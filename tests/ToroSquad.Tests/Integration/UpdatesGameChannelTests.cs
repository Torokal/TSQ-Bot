using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// A game's own channel end to end (real SQLite, real outbox, the production wiring, scripted sources): each game posts to
/// its own channel or to the guild's Updates channel, a channel change never posts anything again, corrections only edit
/// the card in the game's current channel, a queued card for a former channel is cancelled, and Counter-Strike 2 keeps
/// behaving exactly as before when no game has a channel of its own. Nothing here touches the network.
/// </summary>
public sealed class UpdatesGameChannelTests
{
    private const string Wow = "wow-forever";
    private const string Cs2 = "cs2";
    private static readonly GuildId Guild = UpdatesRig.Guild;
    private static readonly ChannelId Common = UpdatesRig.Channel;
    private static readonly ChannelId WowChannel = UpdatesRig.Channel2;
    private static readonly ChannelId Third = new(9703);
    private static readonly DateTimeOffset Start = UpdatesRig.Start;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string Notes = ForumHtmlSamples.BoldParagraphSections("Today we updated the beta.",
        ("Bug Fixes", ["Fixed one thing.", "Fixed another thing."]));

    /// <summary>Both games followed and baselined; the guild's Updates channel is <see cref="Common"/>.</summary>
    private static async Task<UpdatesRig> RigAsync()
    {
        var rig = await UpdatesRig.CreateAsync(configure: false);
        await rig.ConfigureGuildAsync(Guild, Common, true, Cs2, Wow);
        rig.AllowChannel(Guild, WowChannel);
        rig.AllowChannel(Guild, Third);
        rig.Forum.SetDevNotes("WoW Forever Beta Development Notes", new ForumPostSpec(1, Notes, Start.AddDays(-14)));
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3))]);
        (await rig.PollAsync()).Should().Be(2);
        return rig;
    }

    private static Task<OperationResult> SetAsync(UpdatesRig rig, string game, ChannelId? channel) =>
        rig.ConfigAsync(c => c.SetGameChannelAsync(TestHost.Admin(Guild), game, channel?.Value, Ct));

    private static void PublishWow(UpdatesRig rig, int post, DateTimeOffset at, string? cooked = null) =>
        rig.Forum.SetDevNotes("WoW Forever Beta Development Notes",
            [.. rig.Forum.Thread(WowForum.DevNotesThread).AllPosts.Where(p => p.Number != post), new ForumPostSpec(post, cooked ?? Notes, at)]);

    private static IEnumerable<(ChannelId Channel, string Title)> Sent(UpdatesRig rig) =>
        rig.Host.Transport.Messages.Select(m => (m.Channel, m.Message.Embed!.Title!));

    [Fact]
    public async Task Each_game_posts_to_its_own_channel_and_a_game_without_one_to_the_guilds_updates_channel()
    {
        await using var rig = await RigAsync();
        (await SetAsync(rig, Wow, WowChannel)).Succeeded.Should().BeTrue();

        var t = rig.Now.AddMinutes(1);
        PublishWow(rig, 2, t);
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3)), SteamNews.Update(102, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();

        Sent(rig).Should().BeEquivalentTo([(Common, "🛠️ CS2 Güncellemesi"), (WowChannel, "🛠️ WoW: Forever Güncellemesi")]);
        rig.Host.Transport.Messages.Should().OnlyContain(m => !m.Pinged);
        (await rig.OutboxAsync()).Select(o => (o.Kind, o.ChannelId)).Should().BeEquivalentTo([("update:cs2", Common.Value), ("update:wow-forever", WowChannel.Value)]);

        // A restart keeps the routing: it lives in the database.
        var restarted = ActivatorUtilities.CreateInstance<UpdatesPoller>(rig.Host.Services);
        PublishWow(rig, 3, rig.Now.AddMinutes(20));
        await rig.PollAsync(poller: restarted);
        await rig.DeliverAsync();
        Sent(rig).Where(s => s.Channel == WowChannel).Should().HaveCount(2);
        Sent(rig).Where(s => s.Channel == Common).Should().ContainSingle();
        restarted.Dispose();
    }

    [Fact]
    public async Task Changing_a_games_channel_posts_nothing_again_and_only_the_next_update_goes_to_the_new_channel()
    {
        await using var rig = await RigAsync();
        PublishWow(rig, 2, rig.Now.AddMinutes(1));
        await rig.PollAsync();
        await rig.DeliverAsync();
        Sent(rig).Should().Equal((Common, "🛠️ WoW: Forever Güncellemesi"));

        (await SetAsync(rig, Wow, WowChannel)).Succeeded.Should().BeTrue();
        await rig.PollAsync();
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1, "the card already posted is not posted again in the new channel");

        // A correction of the card in the former channel is not applied anywhere; the next update goes to the new channel.
        PublishWow(rig, 2, Start.AddMinutes(1), Notes.Replace("Fixed one thing.", "Fixed one thing, and its cause."));
        PublishWow(rig, 3, rig.Now.AddMinutes(16));
        await rig.PollAsync();
        await rig.DeliverAsync();
        Sent(rig).Should().Equal((Common, "🛠️ WoW: Forever Güncellemesi"), (WowChannel, "🛠️ WoW: Forever Güncellemesi"));
        rig.Host.Transport.EditCalls.Should().Be(0, "a card in a former channel is left as it is");

        // A correction of the card in the game's current channel edits that card.
        PublishWow(rig, 3, rig.Now.AddMinutes(1), Notes.Replace("Fixed another thing.", "Fixed another thing properly."));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.EditCalls.Should().Be(1);
        rig.Host.Transport.Messages.Single(m => m.Channel == WowChannel).Edits.Single().Embed!.Description.Should().Contain("Fixed another thing properly.");
        rig.Host.Transport.SendCalls.Should().Be(2);

        // Back to the common channel: again nothing is re-posted, and the next update goes there.
        (await SetAsync(rig, Wow, null)).Succeeded.Should().BeTrue();
        PublishWow(rig, 4, rig.Now.AddMinutes(16));
        await rig.PollAsync();
        await rig.DeliverAsync();
        Sent(rig).Select(s => s.Channel).Should().Equal(Common, WowChannel, Common);
    }

    [Fact]
    public async Task A_card_still_queued_for_the_former_channel_is_cancelled_not_sent_to_either_channel()
    {
        await using var rig = await RigAsync();
        PublishWow(rig, 2, rig.Now.AddMinutes(1));
        await rig.PollAsync(); // staged for the common channel, not delivered yet
        (await SetAsync(rig, Wow, WowChannel)).Succeeded.Should().BeTrue();
        await rig.DeliverAsync();

        rig.Host.Transport.SendCalls.Should().Be(0);
        (await rig.OutboxAsync()).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Cancelled);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0, "an update counts as delivered to the guild whatever channel it was meant for");
    }

    [Fact]
    public async Task The_guilds_updates_channel_can_change_without_moving_a_game_that_has_its_own()
    {
        await using var rig = await RigAsync();
        (await SetAsync(rig, Wow, WowChannel)).Succeeded.Should().BeTrue();
        (await rig.ConfigAsync(c => c.SetChannelAsync(TestHost.Admin(Guild), Third.Value, Ct))).Succeeded.Should().BeTrue();

        var t = rig.Now.AddMinutes(1);
        PublishWow(rig, 2, t);
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3)), SteamNews.Update(102, t)]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        Sent(rig).Should().BeEquivalentTo([(Third, "🛠️ CS2 Güncellemesi"), (WowChannel, "🛠️ WoW: Forever Güncellemesi")]);
    }

    [Fact]
    public async Task Setting_a_games_channel_is_validated_authorized_and_per_guild()
    {
        await using var rig = await UpdatesRig.CreateAsync(configure: false);
        rig.AllowChannel(Guild, Common);
        rig.AllowChannel(Guild, WowChannel);
        (await SetAsync(rig, Wow, WowChannel)).Error.Should().Be(OperationError.InvalidInput, "the guild's Updates channel comes first");
        await rig.ConfigureGuildAsync(Guild, Common, true, Cs2);

        (await rig.ConfigAsync(c => c.SetGameChannelAsync(TestHost.Member(Guild), Wow, WowChannel.Value, Ct))).Error.Should().Be(OperationError.Forbidden);
        (await SetAsync(rig, "dota2", WowChannel)).Error.Should().Be(OperationError.InvalidInput);
        (await SetAsync(rig, Wow, new ChannelId(424242))).Error.Should().Be(OperationError.InvalidInput, "unknown channel");

        // The channel can be chosen before the game is followed; following stays its own decision.
        var saved = await SetAsync(rig, Wow, WowChannel);
        (saved.Succeeded, saved.MessageKey).Should().Be((true, "updates.game.channel_saved_game_off"));
        var status = await rig.GameStatusAsync(Guild, Wow);
        (status.Enabled, status.ChannelId).Should().Be((false, WowChannel.Value));
        (await SetAsync(rig, Wow, WowChannel)).MessageKey.Should().Be("updates.game.channel_already");
        (await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), Wow, true, Ct))).Succeeded.Should().BeTrue();
        (await rig.GameStatusAsync(Guild, Wow)).ChannelId.Should().Be(WowChannel.Value, "turning the game on keeps its channel");
        (await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), Wow, false, Ct))).Succeeded.Should().BeTrue();
        (await rig.GameStatusAsync(Guild, Wow)).ChannelId.Should().Be(WowChannel.Value, "and so does turning it off");

        (await rig.GameStatusAsync(Guild, Cs2)).ChannelId.Should().BeNull();
        var other = await rig.ConfigAsync(c => c.StatusAsync(TestHost.Admin(UpdatesRig.OtherGuild), Ct));
        other.Status!.Games.Should().OnlyContain(g => g.ChannelId == null, "another guild's settings are never visible");

        (await SetAsync(rig, Wow, null)).MessageKey.Should().Be("updates.game.channel_common");
        (await SetAsync(rig, Wow, null)).MessageKey.Should().Be("updates.game.channel_already_common");
        (await rig.GameStatusAsync(Guild, Wow)).ChannelId.Should().BeNull();
    }

    [Fact]
    public async Task Doctor_checks_a_games_own_channel_and_a_delivery_problem_there_is_the_games()
    {
        await using var rig = await RigAsync();
        rig.Host.Guilds.SetChannel(Guild, WowChannel, new BotChannelAccess(true, true, GuildPermission.ViewChannel));
        var saved = await SetAsync(rig, Wow, WowChannel);
        (saved.Succeeded, saved.MessageKey).Should().Be((true, "updates.game.channel_saved_missing_permissions"));
        var checks = (await rig.ConfigAsync(c => c.DoctorAsync(TestHost.Admin(Guild), Ct))).Checks;
        checks.Should().ContainSingle(c => c.LabelKey == "updates.doctor.game_channel").Which.Should()
            .Match<UpdatesCheck>(c => c.State == UpdatesCheckState.Problem && c.DetailKey == "updates.doctor.game_channel_permissions");
        checks.Should().Contain(c => c.LabelKey == "updates.doctor.channel" && c.DetailKey == "updates.doctor.channel_ok", "the guild's own channel is fine");

        rig.AllowChannel(Guild, WowChannel);
        await rig.Host.InScopeAsync(async sp =>
        {
            var policy = sp.GetServices<IDeliveryPolicy>().Single(p => p.Module == new ModuleId("updates"));
            await policy.ReportChannelProblemAsync(Guild, WowChannel, PermanentFailureKind.MissingPermissions, Ct);
        });
        checks = (await rig.ConfigAsync(c => c.DoctorAsync(TestHost.Admin(Guild), Ct))).Checks;
        checks.Where(c => c.LabelKey == "updates.doctor.game_channel").Select(c => c.DetailKey).Should().Equal("updates.doctor.game_channel_ok", "updates.doctor.game_channel_problem");
        (await rig.GuildConfigAsync(Guild))!.ChannelProblem.Should().BeNull("the problem is not the common channel's");

        // Setting the channel again clears the recorded problem.
        (await SetAsync(rig, Wow, Third)).Succeeded.Should().BeTrue();
        (await rig.GameStatusAsync(Guild, Wow)).ChannelProblem.Should().BeNull();
    }

    [Fact]
    public async Task The_delivery_check_follows_the_games_channel()
    {
        await using var rig = await RigAsync();
        async Task<bool> AllowedAsync(string game, ChannelId channel) => await rig.Host.InScopeAsync(async sp =>
            await sp.GetServices<IDeliveryPolicy>().Single(p => p.Module == new ModuleId("updates")).CanDeliverAsync(Guild, channel, "update:" + game, Ct) is DeliveryDecision.Allow);

        ((await AllowedAsync(Wow, Common)), (await AllowedAsync(Wow, WowChannel))).Should().Be((true, false));
        await SetAsync(rig, Wow, WowChannel);
        ((await AllowedAsync(Wow, Common)), (await AllowedAsync(Wow, WowChannel))).Should().Be((false, true));
        ((await AllowedAsync(Cs2, Common)), (await AllowedAsync(Cs2, WowChannel))).Should().Be((true, false), "the other game is untouched");
        await SetAsync(rig, Wow, null);
        ((await AllowedAsync(Wow, Common)), (await AllowedAsync(Wow, WowChannel))).Should().Be((true, false));
    }
}
