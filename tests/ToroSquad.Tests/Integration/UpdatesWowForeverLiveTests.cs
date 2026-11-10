using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Persistence;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// World of Warcraft: Forever leaving beta, end to end (real SQLite, real outbox, production wiring, a scripted forum in the
/// shape the real one had on 2026-11-10): a guild that followed the game through the beta, with the game in its own channel,
/// is moved to the released game's source by a restart with the new settings. Nothing old is posted, the next hotfix day is
/// one card in the same channel — once, also after a restart or a failed first request — a correction edits it, a renamed
/// thread changes nothing; and the missed days can be recovered explicitly: previewed first (nothing written), then applied
/// once. All post text is synthetic. Nothing here touches the network.
/// </summary>
public sealed class UpdatesWowForeverLiveTests
{
    private const string Wow = "wow-forever";
    private static readonly GuildId Guild = UpdatesRig.Guild;
    private static readonly ChannelId Channel = UpdatesRig.Channel;
    private static readonly ChannelId WowChannel = UpdatesRig.Channel2;
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>The moment the bot restarts with the released game's settings.</summary>
    private static readonly DateTimeOffset Release = new(2026, 11, 10, 6, 0, 0, TimeSpan.Zero);

    private static readonly string BetaNotes = ForumHtmlSamples.BoldParagraphSections("Today we updated the beta.", ("Bug Fixes", ["Fixed one thing.", "Fixed another thing."]));
    private static readonly string Nov10 = ForumHtmlSamples.HotfixDay("November 10, 2026", ("Classes", ["Synthetic fix nine.", "Synthetic fix ten."]));

    /// <summary>
    /// A guild that followed the game since the beta: its updates channel, the game in its own channel, the beta's baseline
    /// and one beta card delivered there.
    /// </summary>
    private static async Task<UpdatesRig> BetaGuildAsync()
    {
        var rig = await UpdatesRig.CreateAsync(configure: false);
        await rig.ConfigureGuildAsync(Guild, Channel, true, Wow);
        rig.AllowChannel(Guild, WowChannel);
        (await rig.ConfigAsync(c => c.SetGameChannelAsync(TestHost.Admin(Guild), Wow, WowChannel.Value, Ct))).Succeeded.Should().BeTrue();
        rig.Forum.SetDevNotes("WoW Forever Beta Development Notes", new ForumPostSpec(1, BetaNotes, rig.Now.AddDays(-14)));
        await rig.PollAsync();
        rig.Forum.SetDevNotes("WoW Forever Beta Development Notes – Updated October 1",
            new ForumPostSpec(1, BetaNotes, rig.Now.AddDays(-14)), new ForumPostSpec(4, BetaNotes, rig.Now.AddMinutes(2)));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().ContainSingle().Which.Channel.Should().Be(WowChannel);
        return rig;
    }

    /// <summary>The released game's forum as it stood at the release restart (published times as the forum had them).</summary>
    private static void SeedRelease(WowForum forum)
    {
        forum.Threads.Add(UpdatesWowForeverLiveSourceTests.HotfixThread());
        forum.Threads.Add(new ForumThreadSpec(2406595, "Dungeon Enemy XP Increased - November 8", new DateTimeOffset(2026, 11, 8, 19, 47, 33, TimeSpan.Zero), ByBlizzard: true,
            Category: WowForum.GeneralDiscussion, Cooked: ForumHtmlSamples.Paragraphs(UpdatesWowForeverLiveSourceTests.AppliedXp)));
        forum.Threads.Add(new ForumThreadSpec(2408781, "PvP Faction Balance Updates - November 10", new DateTimeOffset(2026, 11, 10, 2, 5, 13, TimeSpan.Zero), ByBlizzard: true,
            Category: WowForum.GeneralDiscussion, Cooked: ForumHtmlSamples.Paragraphs(UpdatesWowForeverLiveSourceTests.AppliedPvp)));
        forum.Threads.Add(new ForumThreadSpec(2404092, "Class Tuning Incoming - November 10", new DateTimeOffset(2026, 11, 6, 21, 11, 39, TimeSpan.Zero), ByBlizzard: true,
            Category: WowForum.GeneralDiscussion, Cooked: ForumHtmlSamples.Paragraphs(UpdatesWowForeverLiveSourceTests.PlannedTuning)));
    }

    /// <summary>The beta guild, restarted at <see cref="Release"/> with the released game's settings (the code's defaults).</summary>
    private static async Task<(UpdatesRig Beta, UpdatesRig Live)> ReleasedAsync(Dictionary<string, string?>? extra = null)
    {
        var beta = await BetaGuildAsync();
        beta.Host.Clock.SetUtcNow(Release);
        var live = await RestartAsync(beta, extra, wowBeta: false);
        return (beta, live);
    }

    /// <summary>A restart on the same database (same profile unless said otherwise), the released forum seeded, the game's channel visible.</summary>
    private static async Task<UpdatesRig> RestartAsync(UpdatesRig from, Dictionary<string, string?>? extra = null, bool? wowBeta = null)
    {
        var rig = await UpdatesRig.CreateAsync(extra: extra, shareWith: from, wowBeta: wowBeta);
        rig.AllowChannel(Guild, WowChannel);
        SeedRelease(rig.Forum);
        return rig;
    }

    /// <summary>Blizzard posts a new hotfix day: the reply, the same day on top of the opening post, and the thread renamed.</summary>
    private static void PostNewDay(WowForum forum, DateTimeOffset posted, string? reply = null)
    {
        var thread = UpdatesWowForeverLiveSourceTests.HotfixThread("WoW: Forever Hotfixes - November 10, 2026", new ForumPostSpec(6, reply ?? Nov10, posted));
        var opening = thread.AllPosts[0] with { Cooked = thread.AllPosts[0].Cooked.Replace("<hr>", Nov10 + "<hr>", StringComparison.Ordinal) };
        forum.Replace(thread with { Posts = [opening, .. thread.AllPosts.Skip(1)] });
    }

    private static Task<List<UpdatesDeliveryEntity>> DeliveriesAsync(UpdatesRig rig) =>
        rig.Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<UpdatesDeliveryEntity>().AsNoTracking().OrderBy(d => d.ExternalId).ToListAsync(Ct));

    private static Task<UpdatesSubscriptionEntity> SubscriptionAsync(UpdatesRig rig) =>
        rig.Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<UpdatesSubscriptionEntity>().AsNoTracking().SingleAsync(s => s.GameKey == Wow, Ct));

    // ---------- 12, 15, 7, 8: the release move ----------

    [Fact]
    public async Task The_release_move_keeps_the_guilds_channel_and_history_posts_nothing_old_and_the_next_hotfix_day_is_one_card()
    {
        var (beta, live) = await ReleasedAsync();
        await using var _ = beta;
        await using var __ = live;
        var before = (await beta.StateAsync(Wow))!;

        // The restart with the new settings IS the move: recorded before anything is requested.
        var moved = (await live.StateAsync(Wow))!;
        (moved.ProviderGameId, moved.BaselineAt, moved.PrunedThroughPublishedAt, moved.NextPollAt).Should().Be(("347,352", before.BaselineAt, Release, null));
        (await SubscriptionAsync(live)).ChannelId.Should().Be(WowChannel.Value, "the game's own channel is the guild's setting and stays");

        await live.PollAsync();
        await live.DeliverAsync();
        live.Host.Transport.SendCalls.Should().Be(0, "nothing published before the move is posted, however recent");
        var items = (await live.ItemsAsync()).ToDictionary(i => i.ExternalId);
        foreach (var id in new[] { "2403040:1", "2403040:4", "2403040:5", "2408781:1" })
            items[id].Baseline.Should().BeTrue(id);
        items["2403040:5"].Title.Should().Be("WoW: Forever Hotfixes – November 9, 2026");

        // Blizzard posts the next day: as a reply, and on top of the opening post, and renames the thread.
        var posted = Release.AddMinutes(30);
        PostNewDay(live.Forum, posted);
        await live.PollAsync(TimeSpan.FromMinutes(30));
        await live.DeliverAsync();

        var card = live.Host.Transport.Messages.Should().ContainSingle("one card for the new day — not a second one for the opening post that also carries it").Subject;
        card.Channel.Should().Be(WowChannel, "the game's own channel, exactly as before the move");
        card.Pinged.Should().BeFalse();
        card.Message.Embed!.Description.Should().StartWith("**WoW: Forever Hotfixes – November 10, 2026**\n\n**Classes**\n• Synthetic fix nine.\n• Synthetic fix ten.");
        card.Message.Embed.Url.Should().Be("https://us.forums.blizzard.com/en/wow/t/2403040/6");
        card.Message.Embed.Timestamp.Should().Be(posted, "the card's time is when the reply was posted");
        (await live.OutboxAsync()).Select(o => o.SourceKey).Should().Equal(["blizzard:wow-forever:2360696:4", "blizzard:wow-forever:2403040:6"],
            "the beta card from before the move and the new day's card — nothing else");

        // Again, and after a restart with the same settings: no second card, no second move.
        await live.PollAsync(TimeSpan.FromMinutes(20));
        live.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var restarted = await RestartAsync(live);
        restarted.WowBeta.Should().BeFalse();
        PostNewDay(restarted.Forum, posted);
        (await restarted.StateAsync(Wow))!.PrunedThroughPublishedAt.Should().Be(Release, "a restart with the same settings is not another move");
        await restarted.PollAsync();
        await restarted.DeliverAsync();
        restarted.Host.Transport.SendCalls.Should().Be(0);
        (await DeliveriesAsync(restarted)).Select(d => (d.ExternalId, d.ChannelId)).Should().Equal(
            ("2360696:4", WowChannel.Value), ("2403040:6", WowChannel.Value));
        restarted.Forum.RequestPaths.Should().NotContain(p => p.Contains("category=349"));
    }

    // ---------- 9, 10: renamed thread, corrected reply ----------

    [Fact]
    public async Task A_renamed_thread_changes_no_card_and_a_corrected_reply_edits_its_own_card()
    {
        var (beta, live) = await ReleasedAsync();
        await using var _ = beta;
        await using var __ = live;
        await live.PollAsync();
        var posted = Release.AddMinutes(30);
        PostNewDay(live.Forum, posted);
        await live.PollAsync(TimeSpan.FromMinutes(30));
        await live.DeliverAsync();
        live.Host.Transport.SendCalls.Should().Be(1);

        // The thread takes the next day's date in its title, nothing else changes.
        var renamed = live.Forum.Thread(WowForum.HotfixThread) with { Title = "WoW: Forever Hotfixes - November 11, 2026" };
        live.Forum.Replace(renamed);
        await live.PollAsync(TimeSpan.FromMinutes(20));
        await live.DeliverAsync();
        live.Host.Transport.EditCalls.Should().Be(0, "the card keeps its own day: a thread title's date is not part of it");
        (await live.ItemsAsync()).Single(i => i.ExternalId == "2403040:6").Title.Should().Be("WoW: Forever Hotfixes – November 10, 2026");

        // Blizzard corrects the reply of that day.
        var corrected = ForumHtmlSamples.HotfixDay("November 10, 2026", ("Classes", ["Synthetic fix nine.", "Synthetic fix ten, corrected."]));
        live.Forum.Replace(renamed with { Posts = [.. renamed.AllPosts.Where(p => p.Number != 6), new ForumPostSpec(6, corrected, posted)] });
        await live.PollAsync(TimeSpan.FromMinutes(20));
        await live.DeliverAsync();

        live.Host.Transport.SendCalls.Should().Be(1, "a correction never posts again");
        live.Host.Transport.EditCalls.Should().Be(1);
        var message = live.Host.Transport.Messages.Single();
        message.Edits.Single().Embed!.Description.Should().Contain("• Synthetic fix ten, corrected.").And.StartWith("**WoW: Forever Hotfixes – November 10, 2026**");
        message.Edits.Single().Embed!.Timestamp.Should().Be(posted);
    }

    // ---------- 13: the first request after the move fails, then a restart ----------

    [Fact]
    public async Task A_failed_first_request_and_a_restart_after_the_move_lose_nothing_published_after_it()
    {
        var (beta, live) = await ReleasedAsync();
        await using var _ = beta;
        await using var __ = live;
        live.Forum.Override = _ => WowForum.Json("{}", HttpStatusCode.ServiceUnavailable);
        await live.PollAsync();
        (await live.StateAsync(Wow))!.ConsecutiveFailures.Should().Be(1);

        // Blizzard posts a day while the bot cannot read the forum; then the bot restarts.
        var posted = Release.AddMinutes(10);
        live.Host.Clock.Advance(TimeSpan.FromMinutes(40));
        await using var restarted = await RestartAsync(live);
        PostNewDay(restarted.Forum, posted);
        (await restarted.StateAsync(Wow))!.PrunedThroughPublishedAt.Should().Be(Release, "the line stays where the move drew it");

        await restarted.PollAsync();
        await restarted.DeliverAsync();
        var card = restarted.Host.Transport.Messages.Should().ContainSingle("published after the move: a real update, although the first answer came later").Subject;
        (card.Channel, card.Message.Embed!.Url).Should().Be((WowChannel, "https://us.forums.blizzard.com/en/wow/t/2403040/6"));
        (await restarted.ItemsAsync()).Single(i => i.ExternalId == "2403040:5").Baseline.Should().BeTrue("the day before the move stays history");
    }

    // ---------- 16: the order of a configured list ----------

    [Fact]
    public async Task Reordering_the_configured_categories_or_watched_threads_is_no_move_of_the_source()
    {
        var (beta, live) = await ReleasedAsync();
        await using var _ = beta;
        await using var __ = live;
        await live.PollAsync();
        var settled = (await live.StateAsync(Wow))!;
        live.Host.Clock.Advance(TimeSpan.FromMinutes(5));

        await using var reordered = await RestartAsync(live, new()
        {
            ["Updates:WowForever:ForumCategoryIds:0"] = "352",
            ["Updates:WowForever:ForumCategoryIds:1"] = "347",
            ["Updates:WowForever:WatchedTopicIds:0"] = "2403040",
        });
        var state = (await reordered.StateAsync(Wow))!;
        (state.ProviderGameId, state.PrunedThroughPublishedAt, state.NextPollAt, state.BaselineAt).Should()
            .Be(("347,352", Release, settled.NextPollAt, settled.BaselineAt), "the same categories in another order are the same source");
        (await reordered.ItemsAsync()).Should().HaveCount((await live.ItemsAsync()).Count, "nothing is reset or dropped");
    }

    // ---------- 14, 15: recovery ----------

    private static Dictionary<string, string?> Recovery(bool apply, int maxCards = 1, params string[] items)
    {
        var config = new Dictionary<string, string?>
        {
            ["Updates:Recovery:Game"] = Wow,
            ["Updates:Recovery:Apply"] = apply ? "true" : "false",
            ["Updates:Recovery:MaxCards"] = maxCards.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < items.Length; i++)
            config["Updates:Recovery:Items:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = items[i];
        return config;
    }

    private static async Task<int> OutboxRowsAsync(UpdatesRig rig) => (await rig.OutboxAsync()).Count;

    [Fact]
    public async Task A_proposal_is_only_previewed_and_apply_posts_exactly_the_named_post_once()
    {
        var (beta, live) = await ReleasedAsync();
        await using var _ = beta;
        await using var __ = live;
        await live.PollAsync();
        live.Host.Clock.Advance(TimeSpan.FromMinutes(20));

        // Preview: the proposal is the newest day of the hotfix thread, to the game's own channel. Nothing is written.
        await using var preview = await RestartAsync(live, Recovery(apply: false));
        var (rowsBefore, deliveriesBefore) = (await OutboxRowsAsync(preview), (await DeliveriesAsync(preview)).Count);
        await preview.PollAsync();
        await preview.DeliverAsync();
        (await OutboxRowsAsync(preview), (await DeliveriesAsync(preview)).Count).Should().Be((rowsBefore, deliveriesBefore), "a preview writes nothing");
        preview.Host.Transport.SendCalls.Should().Be(0);

        var plan = (await preview.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct))).Status!.Recovery!;
        (plan.Apply, plan.Proposed, plan.DryRun).Should().Be((false, true, false));
        var guild = plan.Guilds.Should().ContainSingle().Subject;
        (guild.GuildId, guild.ChannelId, guild.DeliveredCount, guild.Cards).Should().Be((Guild.Value, WowChannel.Value, 1, 1));
        guild.Items.Should().Equal(new RecoveryItem("2403040:5", "WoW: Forever Hotfixes – November 9, 2026", new DateTimeOffset(2026, 11, 10, 4, 23, 46, TimeSpan.Zero), RecoveryItemState.Ready));
        var (_, checks) = await preview.ConfigAsync(c => c.DoctorAsync(TestHost.Admin(Guild), Ct));
        checks.Should().Contain(c => c.LabelKey == "updates.doctor.recovery" && c.DetailKey == "updates.doctor.recovery_proposal" && c.State == UpdatesCheckState.Info);
        checks.Should().Contain(c => c.LabelKey == "updates.doctor.scope" && c.Args.Contains("347,352") && c.Args.Contains("2403040"));

        // Apply without a named list is refused at startup; should it ever get this far, nothing is guessed and nothing posted.
        new UpdatesRecoveryOptions { Game = Wow, Apply = true }.Validate().Should().ContainSingle().Which.Should().Contain("Apply needs");
        preview.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var guessed = await RestartAsync(preview, Recovery(apply: true));
        await guessed.PollAsync();
        await guessed.DeliverAsync();
        guessed.Host.Transport.SendCalls.Should().Be(0, "a proposal is never applied");

        // Apply with the one named post: one card, the same card a normal round would have made, to the same channel.
        guessed.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var apply = await RestartAsync(guessed, Recovery(apply: true, 1, "2403040:5"));
        await apply.PollAsync();
        await apply.DeliverAsync();
        var card = apply.Host.Transport.Messages.Should().ContainSingle().Subject;
        card.Channel.Should().Be(WowChannel);
        card.Message.Embed!.Description.Should().StartWith("**WoW: Forever Hotfixes – November 9, 2026**");
        card.Message.Embed.Timestamp.Should().Be(new DateTimeOffset(2026, 11, 10, 4, 23, 46, TimeSpan.Zero));
        (await apply.OutboxAsync()).Select(o => (o.SourceKey, o.Kind)).Should().Contain(("blizzard:wow-forever:2403040:5", "update:wow-forever"));

        // Blizzard posts the next day: the normal pipeline posts it once; the recovery still means 2403040:5 only.
        var posted = apply.Now.AddMinutes(5);
        PostNewDay(apply.Forum, posted);
        await apply.PollAsync(TimeSpan.FromMinutes(20));
        await apply.DeliverAsync();
        apply.Host.Transport.Messages.Select(m => m.Message.Embed!.Url).Should().Equal(
            "https://us.forums.blizzard.com/en/wow/t/2403040/5", "https://us.forums.blizzard.com/en/wow/t/2403040/6");
        var still = (await apply.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct))).Status!.Recovery!.Guilds.Single();
        still.Items.Select(i => (i.ExternalId, i.State)).Should().Equal([("2403040:5", RecoveryItemState.Delivered)], "the selection is the named list, nothing else");

        // A restart (another day, the setting still on) produces nothing again.
        apply.Host.Clock.Advance(TimeSpan.FromDays(1));
        await using var again = await RestartAsync(apply, Recovery(apply: true, 1, "2403040:5"));
        PostNewDay(again.Forum, posted);
        await again.PollAsync();
        await again.DeliverAsync();
        again.Host.Transport.SendCalls.Should().Be(0);
        (await DeliveriesAsync(again)).Select(d => d.ExternalId).Should().Equal(["2360696:4", "2403040:5", "2403040:6"]);
        (await again.OutboxAsync()).Select(o => o.SourceKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_recovered_post_staged_but_not_yet_delivered_and_a_post_the_normal_round_also_plans_are_each_one_card()
    {
        var (beta, live) = await ReleasedAsync();
        await using var _ = beta;
        await using var __ = live;
        await live.PollAsync();

        // The next day appears after the move; the recovery names it too. Normal planning and recovery see it in the same round.
        var posted = live.Now.AddMinutes(5);
        PostNewDay(live.Forum, posted);
        live.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var both = await RestartAsync(live, Recovery(apply: true, 2, "2403040:6", "2403040:4"));
        PostNewDay(both.Forum, posted);
        await both.PollAsync();
        var rows = await both.OutboxAsync();
        rows.Select(o => o.SourceKey).Should().BeEquivalentTo(["blizzard:wow-forever:2360696:4", "blizzard:wow-forever:2403040:6", "blizzard:wow-forever:2403040:4"],
            "one outbox row per post — the post both ways planned is staged once");

        // Not delivered yet (the outbox still holds the cards) when the bot restarts: nothing is staged again.
        both.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var restarted = await RestartAsync(both, Recovery(apply: true, 2, "2403040:6", "2403040:4"));
        PostNewDay(restarted.Forum, posted);
        await restarted.PollAsync();
        (await restarted.OutboxAsync()).Should().HaveCount(rows.Count);
        await restarted.DeliverAsync();
        restarted.Host.Transport.Messages.Select(m => (m.Channel, m.Message.Embed!.Url)).Should().BeEquivalentTo(
            [(WowChannel, "https://us.forums.blizzard.com/en/wow/t/2403040/6"), (WowChannel, "https://us.forums.blizzard.com/en/wow/t/2403040/4")]);
    }

    [Fact]
    public async Task Named_posts_are_planned_one_by_one_and_the_limit_is_the_list()
    {
        var (beta, live) = await ReleasedAsync();
        await using var _ = beta;
        await using var __ = live;
        // A busy General Discussion: the Dungeon XP announcement is on none of the list pages a round reads.
        for (var i = 0; i < 100; i++)
            live.Forum.Threads.Add(new ForumThreadSpec(2412000 + i, "player thread " + i, Release.AddMinutes(-i), Category: WowForum.GeneralDiscussion));
        await live.PollAsync();
        (await live.ItemsAsync()).Should().NotContain(i => i.ExternalId == "2406595:1");
        live.Host.Clock.Advance(TimeSpan.FromMinutes(20));

        // More names than MaxCards are refused, never cut or shifted.
        new UpdatesRecoveryOptions { Game = Wow, Items = ["2406595:1", "2408781:1", "2404092:1"], MaxCards = 2 }.Validate().Should().ContainSingle()
            .Which.Should().Contain("MaxCards is 2");

        await using var preview = await RestartAsync(live, Recovery(apply: false, 3, "2406595:1", "2404092:1", "2360696:1"));
        for (var i = 0; i < 100; i++)
            preview.Forum.Threads.Add(new ForumThreadSpec(2412000 + i, "player thread " + i, Release.AddMinutes(-i), Category: WowForum.GeneralDiscussion));
        var first = (await preview.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct))).Status!.Recovery!.Guilds.Single();
        first.Items.Single(i => i.ExternalId == "2406595:1").State.Should().Be(RecoveryItemState.NotSeen);

        // The thread of a named post the source has not shown is read like a followed thread on the next round.
        await preview.PollAsync();
        preview.Forum.RequestPaths.Should().Contain("/en/wow/t/2406595.json");
        var plan = (await preview.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct))).Status!.Recovery!;
        plan.Proposed.Should().BeFalse();
        plan.Guilds.Single().Items.Select(i => (i.ExternalId, i.State)).Should().Equal(
            ("2406595:1", RecoveryItemState.Ready),
            ("2404092:1", RecoveryItemState.NotAnUpdate),
            ("2360696:1", RecoveryItemState.BeforeWindow));
        preview.Host.Transport.SendCalls.Should().Be(0);

        preview.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var apply = await RestartAsync(preview, Recovery(apply: true, 2, "2406595:1", "2408781:1"));
        await apply.PollAsync();
        await apply.DeliverAsync();
        apply.Host.Transport.Messages.Select(m => (m.Channel, m.Message.Embed!.Url)).Should().Equal(
            (WowChannel, "https://us.forums.blizzard.com/en/wow/t/2406595/1"), (WowChannel, "https://us.forums.blizzard.com/en/wow/t/2408781/1"));
        apply.Host.Transport.Messages.Should().OnlyContain(m => !m.Pinged);

        // The setting stays on for more rounds and another restart: nothing beyond the list, nothing twice.
        await apply.PollAsync(TimeSpan.FromMinutes(20));
        await apply.PollAsync(TimeSpan.FromMinutes(20));
        await apply.DeliverAsync();
        apply.Host.Transport.SendCalls.Should().Be(2);
        apply.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var again = await RestartAsync(apply, Recovery(apply: true, 2, "2406595:1", "2408781:1"));
        await again.PollAsync();
        await again.DeliverAsync();
        again.Host.Transport.SendCalls.Should().Be(0, "the list is the recovery's limit in all, not per round or per restart");
        (await DeliveriesAsync(again)).Select(d => d.ExternalId).Should().Equal(["2360696:4", "2406595:1", "2408781:1"]);
    }

    [Fact]
    public async Task A_recovery_never_touches_another_game_or_a_guild_that_does_not_follow_the_game()
    {
        var (beta, live) = await ReleasedAsync(new() { ["Updates:Recovery:Game"] = "cs2", ["Updates:Recovery:Apply"] = "true", ["Updates:Recovery:Items:0"] = "2403040:5" });
        await using var _ = beta;
        await using var __ = live;
        await live.PollAsync();
        await live.DeliverAsync();
        live.Host.Transport.SendCalls.Should().Be(0, "a recovery for another game leaves World of Warcraft: Forever alone");

        live.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var off = await RestartAsync(live, Recovery(apply: true, 1, "2403040:5"));
        await off.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), Wow, false, Ct));
        (await off.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct))).Status!.Recovery!.Guilds.Should().BeEmpty();
        await off.PollAsync();
        await off.DeliverAsync();
        off.Host.Transport.SendCalls.Should().Be(0);

        // Paused: the guild's pause holds for recovery too.
        off.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var paused = await RestartAsync(off, Recovery(apply: true, 1, "2403040:5"));
        await paused.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), Wow, true, Ct));
        await paused.ConfigAsync(c => c.SetPausedAsync(TestHost.Admin(Guild), true, Ct));
        (await paused.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct))).Status!.Recovery!.Guilds.Should().BeEmpty();
        await paused.PollAsync();
        await paused.DeliverAsync();
        paused.Host.Transport.SendCalls.Should().Be(0);
    }

    // ---------- the opening post's own day ----------

    [Fact]
    public async Task The_opening_posts_card_keeps_its_own_day_when_blizzard_removes_reorders_or_rewords_the_days()
    {
        var (beta, live) = await ReleasedAsync();
        await using var _ = beta;
        await using var __ = live;
        await live.PollAsync();
        live.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var apply = await RestartAsync(live, Recovery(apply: true, 1, "2403040:1"));
        await apply.PollAsync();
        await apply.DeliverAsync();
        var card = apply.Host.Transport.Messages.Should().ContainSingle().Subject;
        card.Message.Embed!.Description.Should().StartWith("**WoW: Forever Hotfixes – November 5, 2026**");
        var row = (await apply.OutboxAsync()).Single(o => o.SourceKey == "blizzard:wow-forever:2403040:1");

        ForumThreadSpec WithOpening(string cooked)
        {
            var thread = UpdatesWowForeverLiveSourceTests.HotfixThread();
            return thread with { Posts = [thread.AllPosts[0] with { Cooked = cooked }, .. thread.AllPosts.Skip(1)] };
        }

        var nov5 = ForumHtmlSamples.HotfixDay("November 5, 2026", ("Classes", ["Synthetic fix one.", "Synthetic fix two."]));
        var nov6 = ForumHtmlSamples.HotfixDay("November 6, 2026", ("Classes", ["Synthetic fix three.", "Synthetic fix four."]), ("Dungeons", ["Synthetic fix five."]));
        var nov9 = ForumHtmlSamples.HotfixDay("November 9, 2026", ("Classes", ["Synthetic fix six.", "Synthetic fix seven."]), ("Quests", ["Synthetic fix eight."]));

        // November 5 removed from the opening post: it is left out, its card is not rewritten as November 6.
        apply.Forum.Replace(WithOpening(ForumHtmlSamples.HotfixOpening(nov9, nov6)));
        await apply.PollAsync(TimeSpan.FromMinutes(20));
        await apply.DeliverAsync();
        (apply.Host.Transport.SendCalls, apply.Host.Transport.EditCalls).Should().Be((1, 0));
        (await apply.StateAsync(Wow))!.LastDetail.Should().StartWith("partial: 1 hotfix opening post left out (own day not provable); ");

        // The days in another order, and a date that no longer reads: no new day is taken for the card.
        apply.Forum.Replace(WithOpening(ForumHtmlSamples.HotfixOpening(nov5, nov6, nov9)));
        await apply.PollAsync(TimeSpan.FromMinutes(20));
        apply.Forum.Replace(WithOpening(ForumHtmlSamples.HotfixOpening(nov9, nov6, nov5.Replace("November 5, 2026", "Nov 5, 2026", StringComparison.Ordinal))));
        await apply.PollAsync(TimeSpan.FromMinutes(20));
        await apply.DeliverAsync();
        (apply.Host.Transport.SendCalls, apply.Host.Transport.EditCalls).Should().Be((1, 0), "reordered: same text; unreadable date: left out");

        // November 5 itself is corrected: the same card is edited, still November 5, with its own publication time.
        apply.Forum.Replace(WithOpening(ForumHtmlSamples.HotfixOpening(nov9, nov6, nov5.Replace("Synthetic fix two.", "Synthetic fix two, corrected.", StringComparison.Ordinal))));
        await apply.PollAsync(TimeSpan.FromMinutes(20));
        await apply.DeliverAsync();
        (apply.Host.Transport.SendCalls, apply.Host.Transport.EditCalls).Should().Be((1, 1));
        var edit = apply.Host.Transport.Messages.Single().Edits.Single().Embed!;
        edit.Description.Should().StartWith("**WoW: Forever Hotfixes – November 5, 2026**").And.Contain("Synthetic fix two, corrected.");
        edit.Timestamp.Should().Be(new DateTimeOffset(2026, 11, 6, 2, 34, 10, TimeSpan.Zero));
        (await apply.OutboxAsync()).Where(o => o.SourceKey == "blizzard:wow-forever:2403040:1").Should().ContainSingle()
            .Which.LogicalKey.Should().Be(row.LogicalKey, "recovery, the edit and the reconciliation all use one logical key");
    }
}
