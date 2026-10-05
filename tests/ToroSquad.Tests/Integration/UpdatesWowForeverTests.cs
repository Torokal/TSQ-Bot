using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// World of Warcraft: Forever end to end through the unchanged Updates pipeline (real SQLite, real outbox, the production
/// wiring) against a scripted Blizzard forum: first run without spam, a new Blizzard post in the Development Notes thread,
/// a separate Client Update thread, everything that must not become a card, one card for a post found both ways, edits,
/// long change lists, hostile text, failures (a failing forum fails the round; one unreadable followed or new thread does
/// not), catching up after hours away, and Counter-Strike 2 untouched next to it. Nothing here touches the network.
/// </summary>
public sealed class UpdatesWowForeverTests
{
    private const string Wow = "wow-forever";
    private static readonly GuildId Guild = UpdatesRig.Guild;
    private static readonly ChannelId Channel = UpdatesRig.Channel;
    private static readonly DateTimeOffset Start = UpdatesRig.Start;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string Notes = ForumHtmlSamples.BoldParagraphSections("Today we updated the beta.",
        ("Bug Fixes", ["Fixed one thing.", "Fixed another thing."]), ("Classes", ["Changed a class."]));

    private static async Task<UpdatesRig> RigAsync(Dictionary<string, string?>? extra = null, params string[] games)
    {
        var rig = await UpdatesRig.CreateAsync(extra: extra, configure: false);
        await rig.ConfigureGuildAsync(Guild, Channel, true, games.Length == 0 ? [Wow] : games);
        return rig;
    }

    /// <summary>The forum as a first run finds it: the Development Notes thread with two earlier builds and an earlier client update.</summary>
    private static void SeedHistory(WowForum forum)
    {
        forum.SetDevNotes("WoW Forever Beta Development Notes – Updated September 24",
            new ForumPostSpec(1, Notes, Start.AddDays(-14)), new ForumPostSpec(4, Notes, Start.AddDays(-7)));
        forum.Threads.Add(new ForumThreadSpec(600, "Beta Client Update - September 22", Start.AddHours(-3), ByBlizzard: true,
            Cooked: ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.1 Build 69977", "An old fix.", "Another old fix.")));
        forum.Threads.Add(new ForumThreadSpec(601, "Hunter damage is terrible", Start.AddHours(-1)));
    }

    private static async Task<UpdatesRig> BaselinedAsync(Dictionary<string, string?>? extra = null)
    {
        var rig = await RigAsync(extra);
        SeedHistory(rig.Forum);
        (await rig.PollAsync()).Should().Be(1);
        (await rig.StateAsync(Wow))!.BaselineAt.Should().NotBeNull();
        return rig;
    }

    private static void AddDevNotesPost(UpdatesRig rig, int number, string cooked, DateTimeOffset created, string title = "WoW Forever Beta Development Notes – Updated October 1") =>
        rig.Forum.SetDevNotes(title, [.. rig.Forum.Thread(WowForum.DevNotesThread).AllPosts.Where(p => p.Number != number), new ForumPostSpec(number, cooked, created)]);

    private static ForumThreadSpec BlizzardThread(long id, string title, DateTimeOffset created, string? cooked = null, int category = WowForum.Category) =>
        new(id, title, created, ByBlizzard: true, Category: category, Cooked: cooked ?? Notes);

    // ---------- first run, new posts ----------

    [Fact]
    public async Task The_first_run_posts_nothing_and_a_new_blizzard_post_in_the_development_notes_thread_is_one_card()
    {
        await using var rig = await RigAsync();
        SeedHistory(rig.Forum);
        (await rig.PollAsync()).Should().Be(1);

        (await rig.OutboxAsync()).Should().BeEmpty("the builds that already exist are never dumped into the channel");
        var seeded = await rig.ItemsAsync();
        seeded.Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:4", "600:1"], "the player thread is not a post at all");
        seeded.Should().OnlyContain(i => i.Baseline && i.Provider == "blizzard" && i.GameKey == Wow);
        var state = await rig.StateAsync(Wow);
        (state!.Provider, state.ProviderGameId, state.BaselineAt).Should().Be(("blizzard", "349", Start));
        state.NextPollAt.Should().Be(Start.AddMinutes(15), "the forum is asked less often than the module's own 5 minutes");

        var published = rig.Now.AddMinutes(2);
        AddDevNotesPost(rig, 5, Notes, published);
        await rig.PollAsync();
        await rig.DeliverAsync();

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        message.Channel.Should().Be(Channel);
        message.Pinged.Should().BeFalse();
        message.Message.Content.Should().BeNull();
        var embed = message.Message.Embed!;
        embed.Title.Should().Be("🛠️ WoW: Forever Güncellemesi");
        embed.Description.Should().Be(
            "**WoW Forever Beta Development Notes**\n\n**Bug Fixes**\n• Fixed one thing.\n• Fixed another thing.\n\n**Classes**\n• Changed a class.\n\n" +
            "Yeni World of Warcraft: Forever güncellemesi yayınlandı.\n\n" +
            "[Blizzard Forumunda Güncelleme Notlarını Gör](https://us.forums.blizzard.com/en/wow/t/2360696/5)");
        embed.Url.Should().Be("https://us.forums.blizzard.com/en/wow/t/2360696/5");
        embed.Footer.Should().Be("Kaynak: Blizzard");
        embed.Timestamp.Should().Be(published);
        embed.ThumbnailUrl.Should().BeNull();
        var row = (await rig.OutboxAsync()).Should().ContainSingle().Subject;
        (row.SourceKey, row.Kind).Should().Be(("blizzard:349:2360696:5", "update:wow-forever"));
        var item = (await rig.ItemsAsync()).Single(i => i.ExternalId == "2360696:5");
        ((UpdateClassification)item.Classification, item.ClassificationReason, item.Baseline).Should().Be((UpdateClassification.Update, "development_notes", false));

        await rig.PollAsync();
        await rig.PollAsync();
        var restarted = ActivatorUtilities.CreateInstance<UpdatesPoller>(rig.Host.Services);
        await rig.PollAsync(TimeSpan.FromHours(2), restarted);
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().ContainSingle("a second poll and a restart never post the same build again");
        (await rig.OutboxAsync()).Should().ContainSingle();
        restarted.Dispose();
    }

    [Fact]
    public async Task A_separate_client_update_thread_is_a_card_with_version_and_build()
    {
        await using var rig = await BaselinedAsync();
        var published = rig.Now.AddMinutes(3);
        rig.Forum.Threads.Add(BlizzardThread(700, "Beta Client Update - October 2", published, ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70300", "A fix.", "Another fix.")));
        await rig.PollAsync();
        await rig.DeliverAsync();

        var embed = rig.Host.Transport.Messages.Should().ContainSingle().Subject.Message.Embed!;
        embed.Description.Should().Be(
            "**Beta Client Update \\- October 2**\n1.60.2 · Build 70300\n\n• A fix.\n• Another fix.\n\n" +
            "Yeni World of Warcraft: Forever güncellemesi yayınlandı.\n\n" +
            "[Blizzard Forumunda Güncelleme Notlarını Gör](https://us.forums.blizzard.com/en/wow/t/700/1)");
        embed.Timestamp.Should().Be(published);
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "700:1").ClassificationReason.Should().Be("client_update");
    }

    [Fact]
    public async Task Development_notes_without_a_build_number_are_posted_and_a_later_build_line_edits_the_same_card()
    {
        await using var rig = await BaselinedAsync();
        var published = rig.Now.AddMinutes(2);
        AddDevNotesPost(rig, 5, Notes, published);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().NotContain("Build");

        AddDevNotesPost(rig, 5, "<p>WoW Forever 1.60.2 Build 70300</p>" + Notes, published);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1, "an edited post never becomes a second card");
        rig.Host.Transport.EditCalls.Should().Be(1);
        var message = rig.Host.Transport.Messages.Single();
        message.Edits.Should().ContainSingle().Which.Embed!.Description.Should().Contain("\n1.60.2 · Build 70300\n");
        message.Pinged.Should().BeFalse();
    }

    // ---------- what never becomes a card ----------

    [Fact]
    public async Task Known_issues_maintenance_marketing_player_threads_and_other_wow_versions_never_become_cards()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now.AddMinutes(1);
        rig.Forum.Threads.AddRange(
        [
            BlizzardThread(701, "WoW Forever Beta Known Issues - October 2", t),
            BlizzardThread(702, "Beta Update Maintenance - October 2", t),
            BlizzardThread(703, "Beta Realm Restarts Incoming - Oct. 2", t),
            BlizzardThread(704, "The WoW: Forever Podcast: Episode 3", t),
            BlizzardThread(705, "World of Warcraft: Forever Class Deep Dives — Mage", t),
            BlizzardThread(706, "Warrior Updates in Today's Beta Build", t),
            BlizzardThread(707, "Beta is Up - Development Notes Posted", t, "<p>The beta is back up. The notes are in the pinned thread.</p>"),
            new ForumThreadSpec(708, "WoW Forever Patch Notes (unofficial, by a player)", t, Cooked: Notes),
            new ForumThreadSpec(709, "Hotfix request: hunter pets", t, Posts: [new ForumPostSpec(1, "<p>player</p>", t, Tracked: false), new ForumPostSpec(2, Notes, t)]),
            BlizzardThread(710, "World of Warcraft: Midnight Hotfixes - October 2", t, category: 171),
            BlizzardThread(711, "Mists of Pandaria Classic Hotfixes - October 2", t, category: 335),
            BlizzardThread(712, "Classic Era Development Notes", t), // misfiled in the Forever category
        ]);
        await rig.PollAsync();
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.Host.Transport.SendCalls.Should().Be(0);
        (await rig.OutboxAsync()).Should().BeEmpty();
        var items = (await rig.ItemsAsync()).Where(i => !i.Baseline).ToDictionary(i => i.ExternalId, i => ((UpdateClassification)i.Classification, i.ClassificationReason));
        items.Keys.Should().BeEquivalentTo(["701:1", "702:1", "703:1", "704:1", "705:1", "706:1", "707:1", "712:1"],
            "player threads, a Blizzard reply in a player thread and other categories are not even stored");
        foreach (var id in new[] { "701:1", "702:1", "703:1", "704:1", "705:1" })
            items[id].Should().Be((UpdateClassification.NotUpdate, "excluded_topic"));
        items["706:1"].Should().Be((UpdateClassification.NotUpdate, "untyped_title"));
        items["707:1"].Should().Be((UpdateClassification.Ambiguous, "no_change_list"));
        items["712:1"].Should().Be((UpdateClassification.Ambiguous, "other_version_title"));
        rig.Forum.RequestPaths.Where(p => p.Contains("/t/7")).Distinct().Should().BeEquivalentTo(["/en/wow/t/707.json", "/en/wow/t/712.json"],
            "only threads whose title could be an update are ever opened");
    }

    // ---------- verified update threads are followed ----------

    /// <summary>So many newer player threads that an older thread is on none of the list pages a round reads.</summary>
    private static void PushOutOfTheNewestThreads(UpdatesRig rig)
    {
        for (var i = 0; i < 95; i++)
            rig.Forum.Threads.Add(new ForumThreadSpec(3000 + rig.Forum.Threads.Count, "player thread", rig.Now.AddMinutes(-i)));
    }

    private static IReadOnlyList<string> ThreadRequestsSince(UpdatesRig rig, int requestsBefore) =>
        rig.Forum.RequestPaths.Skip(requestsBefore).Where(p => p.Contains("/t/", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task A_later_blizzard_reply_in_a_verified_client_update_thread_is_found_after_the_thread_left_the_newest_threads()
    {
        await using var rig = await BaselinedAsync();
        var opened = rig.Now.AddMinutes(1);
        var first = ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70300", "A fix.", "Another fix.");
        rig.Forum.Threads.Add(BlizzardThread(720, "Beta Client Update - October 2", opened, first));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle("the separate Client Update thread is discovered").Which.Should().Contain("/t/720/1)");

        // Hours later the thread is far down the category, players have replied — and Blizzard adds a second build to it.
        rig.Host.Clock.Advance(TimeSpan.FromHours(8));
        PushOutOfTheNewestThreads(rig);
        var replied = rig.Now.AddMinutes(2);
        var posts = new List<ForumPostSpec> { new(1, first, opened) };
        posts.AddRange(Enumerable.Range(2, 24).Select(n => new ForumPostSpec(n, "<p>player reply " + n + "</p>", opened.AddMinutes(n), Tracked: false)));
        posts.Add(new ForumPostSpec(26, ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70310", "A follow-up fix.", "One more fix."), replied));
        posts.Add(new ForumPostSpec(27, "<ul><li>Player list one</li><li>Player list two</li></ul><p>Build 99999</p>", replied.AddMinutes(1), Tracked: false));
        rig.Forum.Replace(new ForumThreadSpec(720, "Beta Client Update - October 2", opened, ByBlizzard: true, Posts: posts));
        var before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.Forum.RequestPaths.Skip(before).Count(p => p.Contains("latest.json", StringComparison.Ordinal)).Should().Be(3, "the newest threads were read to the page bound");
        ThreadRequestsSince(rig, before).Should().Contain(["/en/wow/t/720.json", "/en/wow/t/720/26.json"],
            "the thread is not among the newest threads any more: it is read because an update was verified in it");
        rig.Host.Transport.Messages.Should().HaveCount(2);
        var reply = rig.Host.Transport.Messages[1].Message.Embed!;
        reply.Description.Should().StartWith("**Beta Client Update \\- October 2**\n1.60.2 · Build 70310\n\n• A follow\\-up fix.\n• One more fix.")
            .And.EndWith("(https://us.forums.blizzard.com/en/wow/t/720/26)");
        reply.Timestamp.Should().Be(replied);
        rig.Host.Transport.Messages[1].Pinged.Should().BeFalse();
        var stored = (await rig.ItemsAsync()).Where(i => i.ExternalId.StartsWith("720:", StringComparison.Ordinal)).ToList();
        stored.Select(i => i.ExternalId).Should().BeEquivalentTo(["720:1", "720:26"], "player replies — also one that looks like patch notes — are not posts at all");
        stored.Should().OnlyContain(i => i.Classification == (int)UpdateClassification.Update && i.ClassificationReason == "client_update");
        stored.Single(i => i.ExternalId == "720:1").ContentChangedAt.Should().BeNull("following a thread does not change its first post");
        (await rig.OutboxAsync()).Select(o => o.SourceKey).Should().BeEquivalentTo(["blizzard:349:720:1", "blizzard:349:720:26"]);
        (await rig.StateAsync(Wow))!.LastDetail.Should().EndWith("1 watched, 2 followed", "this thread and the client update thread of the first run");

        await rig.PollAsync();
        await rig.PollAsync();
        var restarted = ActivatorUtilities.CreateInstance<UpdatesPoller>(rig.Host.Services);
        await rig.PollAsync(TimeSpan.FromHours(1), restarted);
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(2, "the same reply is never posted a second time");
        rig.Host.Transport.EditCalls.Should().Be(0);
        restarted.Dispose();
    }

    [Fact]
    public async Task An_edit_of_a_followed_thread_is_still_seen_and_a_blizzard_remark_there_is_not_an_update()
    {
        await using var rig = await BaselinedAsync();
        var opened = rig.Now.AddMinutes(1);
        rig.Forum.Threads.Add(BlizzardThread(721, "WoW Forever Hotfixes - October 2", opened, Notes));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Clock.Advance(TimeSpan.FromHours(30));
        PushOutOfTheNewestThreads(rig);

        // More than a day later Blizzard corrects the first post and adds a one-line remark.
        rig.Forum.Replace(new ForumThreadSpec(721, "WoW Forever Hotfixes - October 2", opened, ByBlizzard: true, Posts:
        [
            new ForumPostSpec(1, Notes.Replace("Fixed one thing.", "Fixed one thing, and its cause."), opened),
            new ForumPostSpec(2, "<p>Thanks for the reports, everyone.</p>", rig.Now.AddMinutes(1)),
        ]));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1, "a remark is not a card");
        rig.Host.Transport.EditCalls.Should().Be(1, "the correction of the first post edits its card");
        rig.Host.Transport.Messages.Single().Edits.Single().Embed!.Description.Should().Contain("• Fixed one thing, and its cause.");
        var remark = (await rig.ItemsAsync()).Single(i => i.ExternalId == "721:2");
        ((UpdateClassification)remark.Classification, remark.ClassificationReason).Should().Be((UpdateClassification.Ambiguous, "no_change_list"));
    }

    [Fact]
    public async Task Followed_threads_are_few_the_most_recent_ones_and_retire_after_the_follow_window()
    {
        await using var rig = await BaselinedAsync(new() { ["Updates:BlizzardForum:MaxFollowedThreads"] = "2", ["Updates:BlizzardForum:FollowThreadDays"] = "3" });
        for (var i = 0; i < 4; i++)
            rig.Forum.Threads.Add(BlizzardThread(730 + i, "Beta Client Update - build " + i, rig.Now.AddMinutes(1 + i)));
        await rig.PollAsync();
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(4);

        rig.Host.Clock.Advance(TimeSpan.FromHours(8));
        PushOutOfTheNewestThreads(rig);
        var before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        ThreadRequestsSince(rig, before).Should().BeEquivalentTo(["/en/wow/t/2360696.json", "/en/wow/t/733.json", "/en/wow/t/732.json"],
            "the watched thread, and of five verified update threads only the two most recent");
        await rig.Host.InScopeAsync(async sp =>
        {
            var planner = sp.GetRequiredService<UpdatesPlanner>();
            var catalog = sp.GetRequiredService<GameUpdateCatalog>();
            (await planner.FetchContextAsync(catalog.Find(Wow)!, Ct)).FollowedThreadIds.Should().Equal("733", "732");
            (await planner.FetchContextAsync(catalog.Find("cs2")!, Ct)).Should().BeSameAs(UpdateFetchContext.None, "a provider without threads follows nothing");
        });

        rig.Host.Clock.Advance(TimeSpan.FromDays(3));
        before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        ThreadRequestsSince(rig, before).Should().Equal(["/en/wow/t/2360696.json"], "three days after their last verified update the threads are no longer read");
        (await rig.StateAsync(Wow))!.LastDetail.Should().EndWith("1 watched");
        rig.Host.Transport.SendCalls.Should().Be(4);
    }

    // ---------- one post, one card ----------

    [Fact]
    public async Task A_post_found_as_a_new_thread_and_as_a_watched_thread_is_one_card()
    {
        await using var rig = await RigAsync();
        rig.Forum.Threads.Add(new ForumThreadSpec(601, "Hunter damage is terrible", Start.AddHours(-1)));
        await rig.PollAsync();
        (await rig.StateAsync(Wow))!.BaselineAt.Should().NotBeNull("a valid list without any Blizzard post is a valid first answer");
        (await rig.ItemsAsync()).Should().BeEmpty();

        // Blizzard opens the Development Notes thread: it is both a new thread of the category and the watched thread.
        var t = rig.Now.AddMinutes(1);
        rig.Forum.SetDevNotes("WoW Forever Beta Development Notes", new ForumPostSpec(1, Notes, t));
        await rig.PollAsync();
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().ContainSingle();
        (await rig.ItemsAsync()).Should().ContainSingle().Which.ExternalId.Should().Be("2360696:1");
        (await rig.OutboxAsync()).Should().ContainSingle();
    }

    // ---------- edits ----------

    [Fact]
    public async Task An_unchanged_or_only_re_rendered_post_is_not_edited_and_a_real_edit_updates_the_same_message()
    {
        await using var rig = await BaselinedAsync();
        var published = rig.Now.AddMinutes(2);
        AddDevNotesPost(rig, 5, "<p><strong>Bug Fixes</strong></p><ul><li>Fixed one thing.</li><li>Fixed another thing.</li></ul>", published);
        await rig.PollAsync();
        await rig.DeliverAsync();
        var hash = (await rig.ItemsAsync()).Single(i => i.ExternalId == "2360696:5").ContentHash;

        await rig.PollAsync();
        // The forum re-renders the same text with other markup, and Blizzard renames the thread for the next build.
        AddDevNotesPost(rig, 5, "<p ><strong class=\"x\">Bug  Fixes</strong></p>\n<ul>\n<li>Fixed one thing.</li>\n<li>Fixed another\nthing.</li>\n</ul><!-- rebaked -->", published,
            "WoW Forever Beta Development Notes – Updated October 9");
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.EditCalls.Should().Be(0);
        var item = (await rig.ItemsAsync()).Single(i => i.ExternalId == "2360696:5");
        (item.ContentHash, item.ContentChangedAt).Should().Be((hash, null));

        // A change far down the post: the hash changes, the card excerpt does not — still no edit.
        var manyLines = Enumerable.Range(1, 30).Select(i => "Line " + i + ".").ToArray();
        AddDevNotesPost(rig, 6, ForumHtmlSamples.BoldParagraphSections(null, ("Bug Fixes", manyLines)), rig.Now.AddMinutes(1));
        await rig.PollAsync();
        await rig.DeliverAsync();
        manyLines[29] = "Line 30, corrected.";
        AddDevNotesPost(rig, 6, ForumHtmlSamples.BoldParagraphSections(null, ("Bug Fixes", manyLines)), rig.Forum.Thread(WowForum.DevNotesThread).AllPosts.Single(p => p.Number == 6).Created);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.EditCalls.Should().Be(0);
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "2360696:6").ContentChangedAt.Should().NotBeNull();

        // A change the card shows: the same message is edited, silently.
        AddDevNotesPost(rig, 5, "<p><strong>Bug Fixes</strong></p><ul><li>Fixed one thing, properly this time.</li><li>Fixed another thing.</li></ul>", published);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(2, "two builds, two cards — never a third");
        rig.Host.Transport.EditCalls.Should().Be(1);
        var edited = rig.Host.Transport.Messages.Single(m => m.Message.Embed!.Url!.EndsWith("/5", StringComparison.Ordinal));
        edited.Edits.Should().ContainSingle().Which.Embed!.Description.Should().Contain("• Fixed one thing, properly this time.");
        edited.Pinged.Should().BeFalse();
    }

    // ---------- content safety ----------

    [Fact]
    public async Task A_very_long_change_list_becomes_a_short_excerpt_with_the_link_and_the_number_of_changes_left()
    {
        await using var rig = await BaselinedAsync();
        var sections = Enumerable.Range(1, 30).Select(s => ("Section " + s, Enumerable.Range(1, 25).Select(i => $"Change {s}.{i} " + new string('x', 300)).ToArray())).ToArray();
        AddDevNotesPost(rig, 5, ForumHtmlSamples.NestedListSections("Today we updated the beta.", sections), rig.Now.AddMinutes(2));
        await rig.PollAsync();
        await rig.DeliverAsync();

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject.Message;
        DiscordLimits.Validate(message).Should().BeEmpty();
        var text = message.Embed!.Description!;
        text.Length.Should().BeLessThan(1700, "the card is an excerpt, never the post");
        text.Should().Contain("**Section 1**").And.Contain("**Section 2**").And.NotContain("**Section 4**");
        text.Split('\n').Count(l => l.StartsWith("• ", StringComparison.Ordinal)).Should().BeInRange(3, UpdateCardRenderer.MaxSections * UpdateCardRenderer.MaxItemsPerSection);
        text.Split('\n').Where(l => l.StartsWith("• ", StringComparison.Ordinal)).Should().OnlyContain(l => l.Length <= UpdateCardRenderer.MaxItemLength + 2 && l.EndsWith('…'));
        text.Should().MatchRegex(@"_… ve 7\d\d değişiklik daha_").And.EndWith("[Blizzard Forumunda Güncelleme Notlarını Gör](https://us.forums.blizzard.com/en/wow/t/2360696/5)");

        var stored = (await rig.ItemsAsync()).Single(i => i.ExternalId == "2360696:5").Highlights!;
        stored.Length.Should().BeLessThanOrEqualTo(UpdateHighlightsJson.MaxLength, "what is kept is the excerpt, not the post");
        stored.Should().NotContain("Section 9");
        (await rig.OutboxAsync()).Single().PayloadJson.Should().NotContain("Section 9");
    }

    [Fact]
    public async Task Hostile_text_in_a_post_cannot_mention_format_or_link()
    {
        await using var rig = await BaselinedAsync();
        var hostile = ForumHtmlSamples.BoldParagraphSections(null,
            ("@everyone **Fixes** <@&5>", ["@here [free gold](https://evil.example) `code` ||spoiler|| <@123>", "Visit https://evil.example now", "> quote # heading - list"]));
        rig.Forum.Threads.Add(BlizzardThread(700, "Beta Client Update - @everyone [x](https://evil.example)", rig.Now.AddMinutes(1), "<p>WoW Forever 1.60.2 Build 70300</p>" + hostile));
        await rig.PollAsync();
        await rig.DeliverAsync();

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        message.Pinged.Should().BeFalse();
        message.Message.Mentions.PingsAnything.Should().BeFalse();
        var text = message.Message.Embed!.Description!;
        DiscordText.RawMentionPattern().IsMatch(text).Should().BeFalse("no mention syntax survives");
        text.Should().NotContain("https://evil.example", "a bare address would become a link").And.NotContain("](https://evil").And.NotContain("||spoiler||").And.NotContain("`code`");
        System.Text.RegularExpressions.Regex.Matches(text, @"\]\(https://").Should().ContainSingle("the only link is the Blizzard one");
        text.Should().NotContain("<script").And.NotContain("<p>");
    }

    // ---------- failures ----------

    [Fact]
    public async Task A_failing_forum_establishes_nothing_loses_nothing_and_never_looks_like_no_updates()
    {
        await using var rig = await RigAsync();
        SeedHistory(rig.Forum);
        rig.Forum.Override = _ => WowForum.Json("{}", HttpStatusCode.InternalServerError);
        await rig.PollAsync();
        var state = await rig.StateAsync(Wow);
        (state!.BaselineAt, state.ConsecutiveFailures, UpdatesConfigService.OutcomeName(state)).Should().Be((null, 1, "ServerError"));
        state.NextPollAt.Should().Be(rig.Now.AddMinutes(15));

        rig.Forum.Override = r => r.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal) ? WowForum.Json("{\"topic_list\":{\"topics\":[") : null;
        await rig.PollAsync();
        state = await rig.StateAsync(Wow);
        (state!.BaselineAt, state.ConsecutiveFailures, UpdatesConfigService.OutcomeName(state)).Should().Be((null, 2, "Malformed"));
        (await rig.ItemsAsync()).Should().BeEmpty("half an answer (the watched thread was readable) stores nothing");
        state.NextPollAt.Should().Be(rig.Now.AddMinutes(30), "exponential backoff from the forum's own interval");

        rig.Forum.Override = _ => throw new TaskCanceledException("simulated timeout");
        await rig.PollAsync();
        UpdatesConfigService.OutcomeName((await rig.StateAsync(Wow))!).Should().Be("Timeout");

        rig.Forum.Override = null;
        await rig.PollAsync();
        state = await rig.StateAsync(Wow);
        (state!.ConsecutiveFailures, state.BaselineAt).Should().Be((0, rig.Now));
        rig.Host.Transport.SendCalls.Should().Be(0);

        // An outage after the baseline: the build published meanwhile is posted once the forum answers again.
        var during = rig.Now.AddMinutes(5);
        rig.Forum.Override = _ => WowForum.Json("{}", HttpStatusCode.BadGateway);
        AddDevNotesPost(rig, 5, Notes, during);
        await rig.PollAsync();
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0);
        (await rig.ItemsAsync()).Should().NotContain(i => i.ExternalId == "2360696:5");

        rig.Forum.Override = null;
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain("/t/2360696/5)");
    }

    private static readonly string ClientNotes = ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70300", "A fix.", "Another fix.");

    private static int ListRequestsSince(UpdatesRig rig, int requestsBefore) =>
        rig.Forum.RequestPaths.Skip(requestsBefore).Count(p => p.Contains("latest.json", StringComparison.Ordinal));

    private static Task<ModuleHealthReport> HealthAsync(UpdatesRig rig) =>
        rig.Host.Services.GetServices<IModuleHealthCheck>().Single(h => h.Module == new ModuleId("updates")).CheckAsync(Ct);

    [Fact]
    public async Task One_unreadable_thread_among_several_does_not_stop_the_others_and_is_posted_once_it_can_be_read()
    {
        await using var rig = await BaselinedAsync();
        var complete = (await rig.StateAsync(Wow))!.LastSuccessAt;
        var t = rig.Now.AddMinutes(1);
        rig.Forum.Threads.Add(BlizzardThread(700, "Beta Client Update - October 2", t, ClientNotes));
        rig.Forum.Threads.Add(BlizzardThread(701, "WoW Forever Hotfixes - October 2", t.AddMinutes(1)));
        rig.Forum.Override = r => r.RequestUri!.AbsolutePath == "/en/wow/t/701.json" ? WowForum.Json("{\"unexpected\":true}") : null;
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.CardTexts.Should().ContainSingle("the readable update is not held back by the unreadable one").Which.Should().Contain("/t/700/1)");
        var state = await rig.StateAsync(Wow);
        (UpdatesConfigService.OutcomeName(state!), state!.ConsecutiveFailures, state.LastSkippedCount).Should().Be(("SuccessPartial", 0, 1));
        state.LastDetail.Should().StartWith("partial: 1 new thread unreadable; ");
        state.LastSuccessAt.Should().Be(complete, "a new thread is still to be read: the catch-up point does not move past it");
        state.NextPollAt.Should().Be(rig.Now.AddMinutes(15), "the round itself did not fail: no backoff");
        (await rig.ItemsAsync()).Should().NotContain(i => i.ExternalId == "701:1", "an unreadable thread is not judged by its title");

        rig.Forum.Override = null;
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().HaveCount(2).And.Contain(c => c.Contains("/t/701/1)"));
        state = await rig.StateAsync(Wow);
        state!.LastSuccessAt.Should().Be(rig.Now, "everything was read: the catch-up point moves again");
        state.LastDetail.Should().NotContain("partial");
        UpdatesConfigService.OutcomeName(state).Should().Be("SuccessItems");
        rig.Host.Transport.SendCalls.Should().Be(2);
    }

    // ---------- a followed thread never decides the round ----------

    [Fact]
    public async Task A_followed_thread_that_never_answers_does_not_stop_discovery_for_as_long_as_it_is_followed()
    {
        await using var rig = await BaselinedAsync();
        var baseline = (await rig.StateAsync(Wow))!.BaselineAt;
        rig.Forum.Threads.Add(BlizzardThread(720, "Beta Client Update - October 2", rig.Now.AddMinutes(1), ClientNotes));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain("/t/720/1)");
        var followedPost = (await rig.ItemsAsync()).Single(i => i.ExternalId == "720:1");

        // From now on the thread answers with a server error, every time, for a week.
        rig.Forum.Override = r => r.RequestUri!.AbsolutePath.StartsWith("/en/wow/t/720", StringComparison.Ordinal) ? WowForum.Json("{}", HttpStatusCode.InternalServerError) : null;
        var cards = 1;
        for (var day = 1; day <= 6; day++)
        {
            rig.Host.Clock.Advance(TimeSpan.FromHours(24));
            if (day % 2 == 0)
            {
                // A real update is published in the category meanwhile.
                rig.Forum.Threads.Add(BlizzardThread(800 + day, "WoW Forever Hotfixes - day " + day, rig.Now.AddMinutes(-20)));
                cards++;
            }

            var before = rig.Forum.Requests.Count;
            await rig.PollAsync();
            await rig.DeliverAsync();

            rig.Forum.RequestPaths.Skip(before).Should().Contain("/en/wow/t/720.json", "the thread is still followed and asked for");
            rig.Host.Transport.SendCalls.Should().Be(cards, "day {0}: the new update of the category is found although a followed thread fails", day);
            var state = await rig.StateAsync(Wow);
            (UpdatesConfigService.OutcomeName(state!), state!.ConsecutiveFailures, state.LastSuccessAt).Should().Be(("SuccessPartial", 0, rig.Now),
                "a followed thread is supplemental: the round is a success and the catch-up point moves — and status names it partial");
            state.LastDetail.Should().StartWith("partial: 1 followed thread unreadable; ", "the detail says what could not be read");
            state.LastSkippedCount.Should().BeGreaterThanOrEqualTo(1);
            state.NextPollAt.Should().Be(rig.Now.AddMinutes(15), "no backoff");
            (await HealthAsync(rig)).Overall.Should().Be(HealthState.Healthy);
        }

        // Nothing the module knows was touched by the failing thread.
        var end = await rig.StateAsync(Wow);
        (end!.BaselineAt, end.PrunedThroughPublishedAt, end.ProviderGameId).Should().Be((baseline, null, "349"));
        var still = (await rig.ItemsAsync()).Single(i => i.ExternalId == "720:1");
        (still.ContentHash, still.ContentChangedAt, still.Classification, still.Baseline).Should().Be((followedPost.ContentHash, null, followedPost.Classification, false));
        rig.Host.Transport.EditCalls.Should().Be(0);
        (await rig.OutboxAsync()).Select(o => o.SourceKey).Should().OnlyHaveUniqueItems().And.HaveCount(cards);

        // A week after its last verified update the thread is no longer followed; once it is also off the newest threads
        // (the first round after a day away still reads that far back) it is not asked for at all and nothing is partial.
        rig.Host.Clock.Advance(TimeSpan.FromHours(24));
        PlayerThreadsEvery15Minutes(rig, rig.Now, rig.Now.AddHours(-8));
        await rig.PollAsync();
        var requests = rig.Forum.Requests.Count;
        await rig.PollAsync();
        rig.Forum.RequestPaths.Skip(requests).Should().NotContain(p => p.Contains("/t/720", StringComparison.Ordinal));
        var retired = await rig.StateAsync(Wow);
        retired!.LastDetail.Should().NotContain("partial");
        UpdatesConfigService.OutcomeName(retired).Should().Be("SuccessNoNewItems");
    }

    [Fact]
    public async Task A_rate_limit_on_a_followed_thread_fails_the_whole_round_and_the_catch_up_point_stays()
    {
        await using var rig = await BaselinedAsync();
        rig.Forum.Threads.Add(BlizzardThread(720, "Beta Client Update - October 2", rig.Now.AddMinutes(1), ClientNotes));
        await rig.PollAsync();
        await rig.DeliverAsync();
        var complete = (await rig.StateAsync(Wow))!.LastSuccessAt;

        rig.Forum.Override = r =>
        {
            if (r.RequestUri!.AbsolutePath != "/en/wow/t/720.json")
                return null;
            var limited = WowForum.Json("{}", HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(40));
            return limited;
        };
        rig.Forum.Threads.Add(BlizzardThread(730, "WoW Forever Hotfixes - October 2", rig.Now.AddMinutes(16)));
        var before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        await rig.DeliverAsync();

        var state = await rig.StateAsync(Wow);
        (UpdatesConfigService.OutcomeName(state!), state!.ConsecutiveFailures, state.LastHttpStatus).Should().Be(("RateLimited", 1, 429));
        state.LastSuccessAt.Should().Be(complete, "a failed round never moves the catch-up point");
        state.NextPollAt.Should().Be(rig.Now.AddMinutes(40), "the forum's Retry-After is honoured");
        ListRequestsSince(rig, before).Should().Be(0, "the forum asked for a pause: the list is not requested in this round");
        (await rig.ItemsAsync()).Should().NotContain(i => i.ExternalId == "730:1");
        rig.Host.Transport.SendCalls.Should().Be(1);
        (await HealthAsync(rig)).Overall.Should().Be(HealthState.Degraded);

        rig.Forum.Override = null;
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().HaveCount(2).And.Contain(c => c.Contains("/t/730/1)"), "nothing was lost by the pause");
        (await rig.StateAsync(Wow))!.LastSuccessAt.Should().Be(rig.Now);
    }

    // ---------- catching up after hours without a complete round ----------

    /// <summary>One player thread every 15 minutes back from <paramref name="newest"/> to just after <paramref name="after"/> (a list page holds 7.5 hours).</summary>
    private static int PlayerThreadsEvery15Minutes(UpdatesRig rig, DateTimeOffset newest, DateTimeOffset after)
    {
        var added = 0;
        for (var at = newest; at > after; at = at.AddMinutes(-15), added++)
            rig.Forum.Threads.Add(new ForumThreadSpec(40_000 + rig.Forum.Threads.Count, "player thread", at));
        return added;
    }

    [Theory]
    [InlineData(8, 2, false)]
    [InlineData(20, 3, false)]
    [InlineData(10, 2, true)]
    public async Task An_update_thread_opened_while_the_bot_was_away_for_hours_is_still_found(int hoursAway, int listPages, bool restarted)
    {
        await using var rig = await BaselinedAsync();
        var opened = rig.Now.AddMinutes(30);
        rig.Forum.Threads.Add(BlizzardThread(740, "Beta Client Update - October 2", opened, ClientNotes));
        rig.Host.Clock.Advance(TimeSpan.FromHours(hoursAway));
        PlayerThreadsEvery15Minutes(rig, rig.Now, opened).Should().BeGreaterThanOrEqualTo(30, "the update thread is off the first list page, which alone reaches back more than six hours");

        // The catch-up point is the last complete round as it was stored — a restarted process reads the same one.
        var poller = restarted ? ActivatorUtilities.CreateInstance<UpdatesPoller>(rig.Host.Services) : null;
        var before = rig.Forum.Requests.Count;
        await rig.PollAsync(poller: poller);
        await rig.DeliverAsync();

        ListRequestsSince(rig, before).Should().Be(listPages, "the list is read back to the last complete round, not only over the usual six hours");
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain("/t/740/1)");
        var state = await rig.StateAsync(Wow);
        (state!.LastSuccessAt, state.ConsecutiveFailures).Should().Be((rig.Now, 0));
        state.LastDetail.Should().NotContain("partial");

        // Caught up: the next round is a normal one again.
        before = rig.Forum.Requests.Count;
        await rig.PollAsync(poller: poller);
        await rig.DeliverAsync();
        ListRequestsSince(rig, before).Should().Be(1);
        rig.Host.Transport.SendCalls.Should().Be(1);
        poller?.Dispose();
    }

    [Fact]
    public async Task A_thread_that_cannot_be_read_while_catching_up_is_not_lost_when_the_next_round_is_a_normal_one()
    {
        await using var rig = await BaselinedAsync();
        var complete = (await rig.StateAsync(Wow))!.LastSuccessAt;
        var opened = rig.Now.AddMinutes(30);
        rig.Forum.Threads.Add(BlizzardThread(740, "Beta Client Update - October 2", opened, ClientNotes));
        rig.Host.Clock.Advance(TimeSpan.FromHours(20));
        PlayerThreadsEvery15Minutes(rig, rig.Now, opened);

        // The first round back reaches the thread on the third list page — and just then the thread does not answer.
        rig.Forum.Override = r => r.RequestUri!.AbsolutePath == "/en/wow/t/740.json" ? throw new TaskCanceledException("simulated timeout") : null;
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(0);
        var state = await rig.StateAsync(Wow);
        (UpdatesConfigService.OutcomeName(state!), state!.ConsecutiveFailures, state.LastSuccessAt).Should().Be(("SuccessPartial", 0, complete),
            "the round is applied, but its catch-up point stays where the last complete round left it");

        // A quarter of an hour later: without the kept catch-up point this round would read one page and never see the thread again.
        rig.Forum.Override = null;
        var before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        await rig.DeliverAsync();
        ListRequestsSince(rig, before).Should().Be(3);
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain("/t/740/1)");
        (await rig.StateAsync(Wow))!.LastSuccessAt.Should().Be(rig.Now);

        before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        ListRequestsSince(rig, before).Should().Be(1, "caught up: a normal round again");
    }

    [Fact]
    public async Task After_more_than_a_day_away_the_look_back_stops_at_the_catch_up_window_and_at_the_page_bound()
    {
        await using var rig = await BaselinedAsync();
        var opened = rig.Now.AddMinutes(30);
        rig.Forum.Threads.Add(BlizzardThread(740, "Beta Client Update - October 2", opened, ClientNotes));
        rig.Host.Clock.Advance(TimeSpan.FromHours(30));
        PlayerThreadsEvery15Minutes(rig, rig.Now, opened);
        await rig.Host.InScopeAsync(async sp =>
        {
            var context = await sp.GetRequiredService<UpdatesPlanner>().FetchContextAsync(sp.GetRequiredService<GameUpdateCatalog>().Find(Wow)!, Ct);
            context.Since.Should().Be(rig.Now.AddHours(-24), "never further back than Updates:CatchUpHours — nothing older could be posted anyway");
        });

        var before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        await rig.DeliverAsync();
        ListRequestsSince(rig, before).Should().Be(3, "Updates:BlizzardForum:MaxListPages stays the bound");
        rig.Host.Transport.SendCalls.Should().Be(0, "an update published more than a day ago is beyond catch-up");
        var state = await rig.StateAsync(Wow);
        state!.LastDetail.Should().StartWith("partial: list page bound reached before the catch-up point; ", "what the bound left unseen is said, not hidden");
        (state.LastSuccessAt, state.ConsecutiveFailures).Should().Be((rig.Now, 0), "more pages will never be read: the round is as complete as it can be");

        before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        ListRequestsSince(rig, before).Should().Be(1, "the next round is a normal one");
        (await rig.StateAsync(Wow))!.LastDetail.Should().NotContain("partial");
    }

    [Fact]
    public async Task Failed_rounds_do_not_move_the_catch_up_point_and_the_update_published_meanwhile_is_found_afterwards()
    {
        await using var rig = await BaselinedAsync();
        var before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        ListRequestsSince(rig, before).Should().Be(1, "a normal round asks for one list page, as it always did");
        var complete = (await rig.StateAsync(Wow))!.LastSuccessAt;
        complete.Should().Be(rig.Now);

        // The forum answers nothing for hours (the watched thread and the list fail: the round fails and backs off).
        var opened = rig.Now.AddMinutes(10);
        rig.Forum.Threads.Add(BlizzardThread(740, "Beta Client Update - October 2", opened, ClientNotes));
        rig.Forum.Override = _ => WowForum.Json("{}", HttpStatusCode.BadGateway);
        for (var failed = 1; failed <= 5; failed++)
        {
            await rig.PollAsync();
            var state = await rig.StateAsync(Wow);
            (state!.ConsecutiveFailures, state.LastSuccessAt).Should().Be((failed, complete), "a failed round is not a successful one");
        }

        var due = (await rig.StateAsync(Wow))!.NextPollAt!.Value;
        (due - complete!.Value).Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(8));
        PlayerThreadsEvery15Minutes(rig, due, opened).Should().BeGreaterThanOrEqualTo(30);
        rig.Forum.Override = null;
        before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        await rig.DeliverAsync();

        ListRequestsSince(rig, before).Should().BeGreaterThan(1, "the list is read back to the last round that succeeded");
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain("/t/740/1)");
        (await rig.StateAsync(Wow))!.LastSuccessAt.Should().Be(rig.Now);
    }

    // ---------- follow places, poll guard ----------

    [Fact]
    public async Task The_watched_thread_takes_none_of_the_followed_places()
    {
        await using var rig = await BaselinedAsync();
        // A new build in the watched Development Notes thread (the usual case) and five separate update threads, all recent.
        AddDevNotesPost(rig, 5, Notes, rig.Now.AddMinutes(9));
        for (var i = 0; i < 5; i++)
            rig.Forum.Threads.Add(BlizzardThread(750 + i, "Beta Client Update - build " + i, rig.Now.AddMinutes(1 + i), ClientNotes));
        await rig.PollAsync();
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(6);

        await rig.Host.InScopeAsync(async sp =>
        {
            var context = await sp.GetRequiredService<UpdatesPlanner>().FetchContextAsync(sp.GetRequiredService<GameUpdateCatalog>().Find(Wow)!, Ct);
            context.FollowedThreadIds.Should().Equal(["754", "753", "752", "751", "750"], "five followed threads NEXT TO the watched one, whose own new build is the most recent update of all");
        });
        rig.Host.Clock.Advance(TimeSpan.FromHours(8));
        PushOutOfTheNewestThreads(rig);
        var before = rig.Forum.Requests.Count;
        await rig.PollAsync();
        ThreadRequestsSince(rig, before).Should().BeEquivalentTo(
            ["/en/wow/t/2360696.json", "/en/wow/t/754.json", "/en/wow/t/753.json", "/en/wow/t/752.json", "/en/wow/t/751.json", "/en/wow/t/750.json"]);
        (await rig.StateAsync(Wow))!.LastDetail.Should().EndWith("1 watched, 5 followed");
    }

    /// <summary>An outbox that fails once for one kind of card: the round that stages it cannot be stored.</summary>
    private sealed class FailingOutbox
    {
        public string? FailKind { get; set; }

        public INotificationOutbox Wrap(INotificationOutbox inner) => new Wrapper(this, inner);

        private sealed class Wrapper(FailingOutbox owner, INotificationOutbox inner) : INotificationOutbox
        {
            public Task<StageOutcome> StageAsync(NotificationRequest request, CancellationToken cancellationToken)
            {
                if (owner.FailKind is { } kind && request.Kind == kind)
                {
                    owner.FailKind = null;
                    throw new InvalidOperationException("simulated failure while storing the round");
                }

                return inner.StageAsync(request, cancellationToken);
            }
        }
    }

    [Fact]
    public async Task A_round_that_cannot_be_stored_is_not_repeated_before_the_games_own_interval()
    {
        var outbox = new FailingOutbox();
        await using var rig = await UpdatesRig.CreateAsync(configure: false,
            replace: s => s.AddScoped<INotificationOutbox>(sp => outbox.Wrap(ActivatorUtilities.CreateInstance<NotificationOutbox>(sp))));
        await rig.ConfigureGuildAsync(Guild, Channel, true, "cs2", Wow);
        SeedHistory(rig.Forum);
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3))]);
        (await rig.PollAsync()).Should().Be(2);
        var stored = await rig.StateAsync(Wow);

        // The Forever round finds a new build but cannot be stored: no next poll time is written for it.
        AddDevNotesPost(rig, 5, Notes, rig.Now.AddMinutes(10));
        outbox.FailKind = "update:wow-forever";
        (await rig.PollAsync()).Should().Be(1, "both games are due a quarter of an hour later; the Forever round is requested but does not complete");
        var failedAt = rig.Now;
        var state = await rig.StateAsync(Wow);
        (state!.LastAttemptAt, state.NextPollAt).Should().Be((stored!.LastAttemptAt, stored.NextPollAt), "nothing of the round was committed");
        rig.Host.Transport.SendCalls.Should().Be(0);

        // Five minutes later Counter-Strike 2 is asked again (its interval), the forum is not (fifteen minutes).
        var (steam, forum) = (rig.SteamRequests, rig.Forum.Requests.Count);
        rig.Host.Clock.Advance(TimeSpan.FromMinutes(5));
        (await rig.Poller.TickAsync(Ct)).Should().Be(1);
        (rig.SteamRequests - steam, rig.Forum.Requests.Count - forum).Should().Be((1, 0), "the in-memory guard uses the forum's own fifteen minutes, not the module's five");
        rig.Host.Clock.SetUtcNow(failedAt.AddMinutes(14));
        await rig.Poller.TickAsync(Ct);
        rig.Forum.Requests.Count.Should().Be(forum);

        rig.Host.Clock.SetUtcNow(failedAt.AddMinutes(15));
        await rig.Poller.TickAsync(Ct);
        rig.Forum.Requests.Count.Should().BeGreaterThan(forum, "after the forum's interval the round is repeated");
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain("/t/2360696/5)");
    }

    [Fact]
    public async Task The_interval_of_a_game_is_the_longer_of_the_modules_and_the_providers()
    {
        await using var rig = await RigAsync();
        rig.Poller.Interval(null).Should().Be(TimeSpan.FromMinutes(5), "Counter-Strike 2: Steam names no minimum");
        rig.Poller.Interval(TimeSpan.FromMinutes(15)).Should().Be(TimeSpan.FromMinutes(15), "World of Warcraft: Forever: the forum's own minimum");
        rig.Poller.Interval(TimeSpan.FromMinutes(2)).Should().Be(TimeSpan.FromMinutes(5));

        await using var slow = await RigAsync(new() { ["Updates:PollIntervalMinutes"] = "30" });
        slow.Poller.Interval(TimeSpan.FromMinutes(15)).Should().Be(TimeSpan.FromMinutes(30), "a longer module interval wins over the provider's minimum");
        SeedHistory(slow.Forum);
        await slow.PollAsync();
        (await slow.StateAsync(Wow))!.NextPollAt.Should().Be(slow.Now.AddMinutes(30), "the next poll time comes from the same interval");
    }

    // ---------- next to Counter-Strike 2 ----------

    [Fact]
    public async Task Counter_strike_2_is_unchanged_next_to_wow_forever_and_each_source_keeps_its_own_pace()
    {
        await using var rig = await RigAsync(null, "cs2", Wow);
        SeedHistory(rig.Forum);
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3))]);
        (await rig.PollAsync()).Should().Be(2, "one round per game");
        (await rig.StatesAsync()).Select(s => (s.Provider, s.GameKey, s.NextPollAt)).Should().Equal(("steam", "cs2", Start.AddMinutes(5)), ("blizzard", Wow, Start.AddMinutes(15)));

        var forumRequests = rig.Forum.Requests.Count;
        rig.Host.Clock.Advance(TimeSpan.FromMinutes(5));
        (await rig.Poller.TickAsync(Ct)).Should().Be(1, "only Steam is due after five minutes");
        rig.Forum.Requests.Count.Should().Be(forumRequests);

        var t = rig.Now.AddMinutes(1);
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3)), SteamNews.Update(102, t)]);
        AddDevNotesPost(rig, 5, Notes, t);
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.Host.Transport.Messages.Should().HaveCount(2);
        var cs2 = rig.Host.Transport.Messages.Single(m => m.Message.Embed!.Title == "🛠️ CS2 Güncellemesi").Message.Embed!;
        cs2.Description.Should().Be("**Counter\\-Strike 2 Update**\n\nYeni Counter-Strike 2 güncellemesi yayınlandı.\n\n" +
                                    "[Steam'de Güncelleme Notlarını Gör](https://store.steampowered.com/news/externalpost/steam_community_announcements/102)",
            "the Counter-Strike 2 card is byte for byte what it was");
        cs2.Footer.Should().Be("Kaynak: Steam");
        rig.Host.Transport.Messages.Should().ContainSingle(m => m.Message.Embed!.Title == "🛠️ WoW: Forever Güncellemesi" && m.Message.Embed.Footer == "Kaynak: Blizzard");
        (await rig.ItemsAsync()).Where(i => i.GameKey == "cs2").Should().OnlyContain(i => i.Highlights == null);
        (await rig.OutboxAsync()).Select(o => o.Kind).Should().BeEquivalentTo(["update:cs2", "update:wow-forever"]);

        // The forum failing (even by throwing) leaves Counter-Strike 2 working, and the other way round.
        rig.Forum.Override = _ => throw new InvalidOperationException("simulated crash");
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3)), SteamNews.Update(102, t), SteamNews.Update(103, rig.Now.AddMinutes(1))]);
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().HaveCount(3);
        UpdatesConfigService.OutcomeName((await rig.StateAsync(Wow))!).Should().Be("TransportError");
        (await rig.StateAsync("cs2"))!.ConsecutiveFailures.Should().Be(0);

        // Unfollowing one game stops only that game.
        rig.Forum.Override = null;
        await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), Wow, false, Ct));
        var (steamBefore, forumBefore) = (rig.SteamRequests, rig.Forum.Requests.Count);
        await rig.PollAsync();
        (rig.SteamRequests - steamBefore, rig.Forum.Requests.Count - forumBefore).Should().Be((1, 0));
    }

    // ---------- settings, state, preview ----------

    private static readonly Dictionary<string, string?> NewCategory = new() { ["Updates:WowForever:ForumCategoryId"] = "400", ["Updates:WowForever:WatchedTopicIds:0"] = "5000" };

    [Fact]
    public async Task A_plain_restart_changes_nothing_about_the_source()
    {
        await using var first = await BaselinedAsync();
        var before = await first.StateAsync(Wow);
        first.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await using var restarted = await UpdatesRig.CreateAsync(shareWith: first);
        SeedHistory(restarted.Forum);
        var state = await restarted.StateAsync(Wow);
        (state!.ProviderGameId, state.BaselineAt, state.PrunedThroughPublishedAt, state.NextPollAt).Should().Be(("349", before!.BaselineAt, null, before.NextPollAt));
        await restarted.PollAsync();
        await restarted.DeliverAsync();
        restarted.Host.Transport.SendCalls.Should().Be(0, "nothing that was there before the restart is posted after it");
        (await restarted.ItemsAsync()).Should().OnlyContain(i => i.Baseline);
    }

    [Fact]
    public async Task Moving_the_game_to_another_forum_category_posts_nothing_old_and_loses_nothing_new()
    {
        await using var beta = await BaselinedAsync();
        var baseline = (await beta.StateAsync(Wow))!.BaselineAt;
        beta.Host.Clock.Advance(TimeSpan.FromMinutes(20));

        // The restart with the new category IS the transition: it is recorded before anything is requested.
        await using var live = await UpdatesRig.CreateAsync(extra: NewCategory, shareWith: beta);
        var moved = live.Now;
        var state = await live.StateAsync(Wow);
        (state!.ProviderGameId, state.BaselineAt, state.PrunedThroughPublishedAt, state.NextPollAt, state.ConsecutiveFailures).Should()
            .Be(("400", baseline, moved, null, 0), "the old baseline is kept, the line between history and news is the move, and the new place is asked at once");
        (await live.ItemsAsync()).Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:4", "600:1"], "what is known about the old category is kept");

        // What already exists in the new category — also posts published after the guild started following the game.
        live.Forum.Threads.Add(new ForumThreadSpec(5000, "WoW Forever Development Notes", moved.AddDays(-9), ByBlizzard: true,
            Posts: [new ForumPostSpec(1, Notes, moved.AddDays(-9)), new ForumPostSpec(2, Notes, moved.AddMinutes(-12))]));
        live.Forum.Threads.Add(BlizzardThread(5001, "WoW Forever Hotfixes - Launch Day", moved.AddMinutes(-5), category: 400));
        live.Forum.Replace(live.Forum.Thread(5000) with { Category = 400 });

        // The forum cannot be read at first, and meanwhile Blizzard publishes a real update in the new category.
        live.Forum.Override = _ => WowForum.Json("{}", HttpStatusCode.BadGateway);
        await live.PollAsync();
        (await live.StateAsync(Wow))!.ConsecutiveFailures.Should().Be(1);
        var published = live.Now.AddMinutes(3);
        live.Forum.Threads.Add(BlizzardThread(5002, "WoW Forever Hotfixes - Day Two", published, category: 400));

        live.Forum.Override = null;
        await live.PollAsync();
        await live.DeliverAsync();
        await live.PollAsync();
        await live.DeliverAsync();

        live.CardTexts.Should().ContainSingle("the first real update after the move is posted, although it was published before the first answer from the new category")
            .Which.Should().Contain("/t/5002/1)");
        var items = (await live.ItemsAsync()).ToDictionary(i => i.ExternalId);
        items.Keys.Should().BeEquivalentTo(["2360696:1", "2360696:4", "600:1", "5000:1", "5000:2", "5001:1", "5002:1"]);
        (items["5000:1"].Baseline, items["5000:2"].Baseline, items["5001:1"].Baseline, items["5002:1"].Baseline).Should().Be((true, true, true, false),
            "everything published before the move is history, whatever the guild's own window says");
        live.Forum.RequestPaths.Should().Contain("/en/wow/latest.json?category=400&order=created").And.Contain("/en/wow/t/5000.json")
            .And.NotContain(p => p.Contains("category=349") || p.Contains("2360696"));

        // A restart with the same (new) settings is not another move.
        live.Host.Clock.Advance(TimeSpan.FromMinutes(30));
        await using var again = await UpdatesRig.CreateAsync(extra: NewCategory, shareWith: live);
        (await again.StateAsync(Wow))!.PrunedThroughPublishedAt.Should().Be(moved);
    }

    [Fact]
    public async Task A_game_that_moves_before_it_ever_answered_simply_takes_its_first_baseline_at_the_new_place()
    {
        await using var beta = await RigAsync();
        beta.Forum.Override = _ => WowForum.Json("{}", HttpStatusCode.BadGateway);
        await beta.PollAsync(); // followed, asked once, never answered: no baseline, no history
        (await beta.StateAsync(Wow))!.BaselineAt.Should().BeNull();
        beta.Host.Clock.Advance(TimeSpan.FromMinutes(20));

        await using var live = await UpdatesRig.CreateAsync(extra: NewCategory, shareWith: beta);
        var state = await live.StateAsync(Wow);
        (state!.ProviderGameId, state.BaselineAt, state.PrunedThroughPublishedAt).Should().Be(("400", null, null));
        live.Forum.Threads.Add(BlizzardThread(5001, "WoW Forever Hotfixes - Launch Day", live.Now.AddMinutes(-5), category: 400));
        await live.PollAsync();
        await live.DeliverAsync();
        live.Host.Transport.SendCalls.Should().Be(0, "a first answer is a baseline");
        (await live.StateAsync(Wow))!.BaselineAt.Should().Be(live.Now);
    }

    [Fact]
    public async Task The_excerpt_is_kept_with_the_post_shown_in_the_preview_and_dropped_with_the_title()
    {
        await using var rig = await BaselinedAsync(new() { ["Updates:TextRetentionDays"] = "7", ["Updates:DedupRetentionDays"] = "30" });
        var admin = TestHost.Admin(Guild);
        var (_, synthetic) = await rig.ConfigAsync(c => c.PreviewAsync(admin, "cs2", "tr", Ct));
        synthetic!.Synthetic.Should().BeTrue("nothing of Counter-Strike 2 is stored in this guild's bot yet");

        var (_, preview) = await rig.ConfigAsync(c => c.PreviewAsync(admin, Wow, "en", Ct));
        preview!.Synthetic.Should().BeFalse("the latest stored update is shown, even a baseline one — only to the admin");
        var embed = preview.Message.Embed!;
        embed.Title.Should().Be("🛠️ WoW: Forever Update");
        embed.Description.Should().Contain("**Beta Client Update \\- September 22**\n1.60.1 · Build 69977\n\n• An old fix.\n• Another old fix.")
            .And.Contain("A new World of Warcraft: Forever update has been released.")
            .And.Contain("[View the update notes on the Blizzard forum](https://us.forums.blizzard.com/en/wow/t/600/1)");
        embed.Footer.Should().Be("Source: Blizzard");

        var status = await rig.GameStatusAsync(Guild, Wow);
        (status.ProviderName, status.Enabled, status.Source!.LastItemCount).Should().Be(("Blizzard", true, 3));
        status.Source.LastDetail.Should().Be("3 threads listed, 1 opened by Blizzard, 1 read in full, 1 watched");
        var (_, checks) = await rig.ConfigAsync(c => c.DoctorAsync(admin, Ct));
        checks.Should().Contain(c => c.DetailKey == "updates.doctor.source_value" && c.State == UpdatesCheckState.Ok && c.Args.Contains("Blizzard"));
        checks.Should().NotContain(c => c.State == UpdatesCheckState.Problem);

        // The client update thread scrolls out of the newest threads; a week later its title and excerpt are dropped.
        (await rig.ItemsAsync()).Should().OnlyContain(i => i.Highlights != null && i.Title != null);
        rig.Forum.Threads.RemoveAll(t => t.Id == 600);
        await rig.PollAsync(TimeSpan.FromDays(8));
        var items = (await rig.ItemsAsync()).ToDictionary(i => i.ExternalId);
        (items["600:1"].Title, items["600:1"].Highlights).Should().Be((null, null));
        items["2360696:4"].Highlights.Should().NotBeNull("posts of the watched thread are still listed");
        rig.Host.Transport.SendCalls.Should().Be(0);
    }
}
