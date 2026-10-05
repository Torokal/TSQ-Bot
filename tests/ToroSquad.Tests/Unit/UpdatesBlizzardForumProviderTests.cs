using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Domain.Games;
using ToroSquad.Modules.Updates.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The Blizzard forum provider against a scripted forum: which addresses are requested (and which never), how Blizzard's
/// posts are found in the watched thread and among new threads, what is left out (player threads, other categories, hidden
/// posts), that every way in gives one post once, which failures fail the round as their own kind (the list, a watched
/// thread, a rate limit anywhere) and which only leave one thread out (a followed or a new thread), how far back the list
/// is read after an outage, and that every request bound is hard. Nothing here touches the network; all thread and post
/// content is synthetic.
/// </summary>
public sealed class UpdatesBlizzardForumProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameUpdateDefinition Game = WowForeverGame.Create(new WowForeverSettings());
    private static readonly string Notes = ForumHtmlSamples.BoldParagraphSections("Today we updated the beta.", ("Bug Fixes", ["Fixed one thing.", "Fixed another thing."]));

    private static (BlizzardForumUpdateProvider Provider, WowForum Forum) Create(BlizzardForumOptions? options = null, UpdatesOptions? module = null)
    {
        var forum = new WowForum();
        var settings = Options.Create(module ?? new UpdatesOptions());
        var clock = new FakeTimeProvider(Now);
        var http = new ProviderHttp(new SingleHandlerFactory(forum), settings, clock, NullLogger<ProviderHttp>.Instance);
        return (new BlizzardForumUpdateProvider(http, Options.Create(options ?? new BlizzardForumOptions()), clock, NullLogger<BlizzardForumUpdateProvider>.Instance), forum);
    }

    private static void SeedDevNotes(WowForum forum, string title = "WoW Forever Beta Development Notes – Updated October 1") =>
        forum.SetDevNotes(title,
            new ForumPostSpec(1, Notes, Now.AddDays(-14)),
            new ForumPostSpec(2, "", Now.AddDays(-14), Tracked: false, PostType: 3),
            new ForumPostSpec(3, "", Now.AddDays(-14), Tracked: false, PostType: 3),
            new ForumPostSpec(4, Notes, Now.AddDays(-7)));

    private static ForumThreadSpec Player(long id, string title, TimeSpan age) => new(id, title, Now - age);

    private static ForumThreadSpec Blizzard(long id, string title, TimeSpan age, string? cooked = null, int category = WowForum.Category) =>
        new(id, title, Now - age, ByBlizzard: true, Category: category, Cooked: cooked ?? Notes, Excerpt: "A short synthetic excerpt.");

    // ---------- requests ----------

    [Fact]
    public async Task A_quiet_round_is_two_requests_the_watched_thread_and_the_category_list_without_any_key()
    {
        var (provider, forum) = Create();
        SeedDevNotes(forum);
        forum.Threads.Add(Player(900, "Hunter damage is terrible", TimeSpan.FromMinutes(5)));
        forum.Threads.Add(Player(901, "An older player thread", TimeSpan.FromHours(7)));
        var result = await provider.FetchAsync(Game, CancellationToken.None);

        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        forum.Requests.Select(r => r.RequestUri!.AbsoluteUri).Should().Equal(
            "https://us.forums.blizzard.com/en/wow/t/2360696.json",
            "https://us.forums.blizzard.com/en/wow/latest.json?category=349&order=created");
        forum.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && r.Headers.Authorization == null && !r.Headers.Contains("Cookie") &&
                                                 !r.RequestUri!.Query.Contains("key", StringComparison.OrdinalIgnoreCase));
        forum.Requests.Should().OnlyContain(r => r.Headers.UserAgent.ToString().Contains("TSQBot") && r.Headers.Accept.ToString() == "application/json");
        forum.RequestPaths.Should().NotContain(p => p.Contains("/groups/") || p.Contains("/g/") || p.Contains("/search") || p.Contains("/posts/"),
            "the group feed and search live under paths the forum's robots.txt disallows");
        provider.MinimumPollInterval.Should().Be(TimeSpan.FromMinutes(15));
        (provider.Provider, provider.DisplayName, provider.ReadLinkKey).Should().Be(("blizzard", "Blizzard", "updates.card.read_blizzard"));
    }

    [Fact]
    public async Task A_game_without_a_numeric_category_is_never_requested()
    {
        var (provider, forum) = Create();
        foreach (var id in new[] { "349&x=1", "abc", "", "0", "-5", "349 " })
            (await provider.FetchAsync(Game with { ProviderGameId = id }, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.UnexpectedSchema, id);
        forum.Requests.Should().BeEmpty();

        var odd = Game with { WatchedThreadIds = ["abc", "0", "-1", "2360696", "2360696", "12; drop"] };
        SeedDevNotes(forum);
        await provider.FetchAsync(odd, CancellationToken.None);
        forum.RequestPaths.Where(p => p.Contains("/t/")).Should().Equal("/en/wow/t/2360696.json");
    }

    // ---------- the watched thread ----------

    [Fact]
    public async Task Every_blizzard_post_of_the_watched_thread_is_a_post_with_a_stable_title_and_its_own_link()
    {
        var (provider, forum) = Create();
        SeedDevNotes(forum);
        var result = await provider.FetchAsync(Game, CancellationToken.None);

        result.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:4"], "the two moderator action notes are not posts");
        var first = result.Items.Single(i => i.ExternalId == "2360696:1");
        var fourth = result.Items.Single(i => i.ExternalId == "2360696:4");
        first.Labels.Should().BeEquivalentTo([UpdateLabels.FirstPost, UpdateLabels.WatchedThread]);
        fourth.Labels.Should().BeEquivalentTo([UpdateLabels.Reply, UpdateLabels.WatchedThread]);
        fourth.Title.Should().Be("WoW Forever Beta Development Notes", "the '– Updated <date>' Blizzard rewrites with every build is not part of a post's title");
        fourth.CanonicalUrl.Should().Be("https://us.forums.blizzard.com/en/wow/t/2360696/4");
        fourth.PublishedAt.Should().Be(Now.AddDays(-7));
        (fourth.Provider, fourth.GameKey).Should().Be(("blizzard", "wow-forever"));
        fourth.Highlights!.ChangeCount.Should().Be(2);
        fourth.Body.Should().Contain("- Fixed one thing.");
        Game.Classifier.Classify(fourth).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, "development_notes"));
    }

    [Fact]
    public async Task Renaming_the_watched_thread_changes_no_earlier_post()
    {
        var (provider, forum) = Create();
        SeedDevNotes(forum);
        var before = (await provider.FetchAsync(Game, CancellationToken.None)).Items.ToDictionary(i => i.ExternalId, i => i.ContentHash);

        forum.SetDevNotes("WoW Forever Beta Development Notes – Updated October 8",
            [.. forum.Thread(WowForum.DevNotesThread).AllPosts, new ForumPostSpec(5, Notes.Replace("one thing", "a third thing"), Now.AddMinutes(-10))]);
        var after = (await provider.FetchAsync(Game, CancellationToken.None)).Items.ToDictionary(i => i.ExternalId, i => i.ContentHash);
        after.Keys.Should().BeEquivalentTo(["2360696:1", "2360696:4", "2360696:5"]);
        after["2360696:1"].Should().Be(before["2360696:1"]);
        after["2360696:4"].Should().Be(before["2360696:4"]);

        foreach (var title in new[] { "Notes - Updated Oct 8", "Notes — updated today", "Notes – Updated" })
            BlizzardForumUpdateProvider.StableTitle(title).Should().Be("Notes");
        BlizzardForumUpdateProvider.StableTitle("Updated Notes for October").Should().Be("Updated Notes for October");
        BlizzardForumUpdateProvider.StableTitle(" - Updated").Should().Be(" - Updated", "nothing would be left");
    }

    [Fact]
    public async Task Player_replies_hidden_and_deleted_posts_of_the_watched_thread_are_not_posts()
    {
        var (provider, forum) = Create();
        forum.SetDevNotes("WoW Forever Beta Development Notes",
            new ForumPostSpec(1, Notes, Now.AddDays(-3)),
            new ForumPostSpec(2, "<p>thanks blizz</p>", Now.AddDays(-3), Tracked: false),
            new ForumPostSpec(3, Notes, Now.AddDays(-2), Hidden: true),
            new ForumPostSpec(4, Notes, Now.AddDays(-1), Deleted: true),
            new ForumPostSpec(5, "", Now.AddHours(-5)),
            new ForumPostSpec(6, Notes, Now.AddHours(-1)));
        (await provider.FetchAsync(Game, CancellationToken.None)).Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:6"]);
    }

    [Fact]
    public async Task The_newest_blizzard_post_of_a_long_watched_thread_is_read_with_one_more_request()
    {
        var (provider, forum) = Create();
        var posts = Enumerable.Range(1, 45).Select(n => new ForumPostSpec(n, Notes, Now.AddDays(-50 + n), Tracked: n is 1 or 44)).ToArray();
        forum.SetDevNotes("WoW Forever Beta Development Notes", posts);
        var result = await provider.FetchAsync(Game, CancellationToken.None);

        forum.RequestPaths.Where(p => p.Contains("/t/")).Should().Equal("/en/wow/t/2360696.json", "/en/wow/t/2360696/44.json");
        result.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:44"]);
    }

    [Fact]
    public async Task A_watched_thread_that_is_gone_or_moved_is_skipped_and_the_rest_of_the_round_still_counts()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(Blizzard(700, "Beta Client Update - October 8", TimeSpan.FromMinutes(30), ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70300", "A fix.")));
        var gone = await provider.FetchAsync(Game, CancellationToken.None);
        gone.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        gone.SkippedItems.Should().Be(1);
        gone.Items.Select(i => i.ExternalId).Should().Equal("700:1");

        forum.Replace(new ForumThreadSpec(WowForum.DevNotesThread, "WoW Forever Beta Development Notes", Now.AddDays(-3), ByBlizzard: true, Category: 171, Cooked: Notes));
        var moved = await provider.FetchAsync(Game, CancellationToken.None);
        moved.SkippedItems.Should().Be(1);
        moved.Items.Select(i => i.ExternalId).Should().Equal(["700:1"], "a thread outside the game's category is out of scope");
    }

    // ---------- new threads ----------

    [Fact]
    public async Task A_new_blizzard_thread_with_an_update_title_is_read_in_full_and_everything_else_is_not_downloaded()
    {
        var (provider, forum) = Create();
        SeedDevNotes(forum);
        forum.Threads.Add(Blizzard(700, "Beta Client Update - October 8", TimeSpan.FromMinutes(30), ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70300", "A fix.", "Another fix.")));
        forum.Threads.Add(Blizzard(701, "WoW Forever Beta Known Issues - October 8", TimeSpan.FromMinutes(40)));
        forum.Threads.Add(Blizzard(702, "Beta Update Maintenance - October 8", TimeSpan.FromMinutes(50)));
        forum.Threads.Add(Blizzard(703, "Legacy Points for Beta Testing", TimeSpan.FromMinutes(55)));
        forum.Threads.Add(Player(704, "Client Update Patch Notes Development Notes Hotfix", TimeSpan.FromMinutes(20)));
        forum.Threads.Add(Blizzard(705, "World of Warcraft: Midnight Hotfixes - October 8", TimeSpan.FromMinutes(10), category: 171));
        var result = await provider.FetchAsync(Game, CancellationToken.None);

        forum.RequestPaths.Where(p => p.Contains("/t/")).Should().Equal(["/en/wow/t/2360696.json", "/en/wow/t/700.json"], "only the thread whose title can be an update is opened");
        result.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:4", "700:1", "701:1", "702:1", "703:1"],
            "Blizzard's other threads are kept as seen; the player thread and the other category are not posts at all");

        var update = result.Items.Single(i => i.ExternalId == "700:1");
        update.Title.Should().Be("Beta Client Update - October 8");
        update.Labels.Should().Equal(UpdateLabels.FirstPost);
        (update.Highlights!.Version, update.Highlights.Build, update.Highlights.ChangeCount).Should().Be(("1.60.2", "70300", 2));
        update.CanonicalUrl.Should().Be("https://us.forums.blizzard.com/en/wow/t/700/1");
        Game.Classifier.Classify(update).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, "client_update"));

        var knownIssues = result.Items.Single(i => i.ExternalId == "701:1");
        knownIssues.Labels.Should().BeEquivalentTo([UpdateLabels.FirstPost, UpdateLabels.ExcerptOnly]);
        knownIssues.Highlights.Should().BeNull();
        knownIssues.Body.Should().Be("A short synthetic excerpt.");
        result.Items.Where(i => i.ExternalId is "701:1" or "702:1" or "703:1").Should().OnlyContain(i => Game.Classifier.Classify(i).Classification == UpdateClassification.NotUpdate);
        result.Detail.Should().Be("6 threads listed, 4 opened by Blizzard, 1 read in full, 1 watched");
    }

    [Fact]
    public async Task A_blizzard_reply_in_a_player_thread_does_not_make_the_thread_a_blizzard_thread()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(new ForumThreadSpec(800, "Patch Notes are wrong about hunters", Now.AddMinutes(-30), Posts:
        [
            new ForumPostSpec(1, "<p>player opinion</p>", Now.AddMinutes(-30), Tracked: false),
            new ForumPostSpec(2, Notes, Now.AddMinutes(-10)),
        ]));
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None);
        result.Outcome.Should().Be(UpdateFetchOutcome.Ok, "a valid list without any Blizzard thread is a valid (empty) answer");
        result.Items.Should().BeEmpty();
        forum.ThreadRequests(800).Should().Be(0);
    }

    [Fact]
    public async Task The_same_post_found_both_ways_is_one_post()
    {
        var (provider, forum) = Create();
        // The watched thread is also among the newest threads of the category.
        forum.SetDevNotes("WoW Forever Beta Development Notes – Updated October 8", new ForumPostSpec(1, Notes, Now.AddHours(-2)), new ForumPostSpec(2, Notes, Now.AddMinutes(-20)));
        var result = await provider.FetchAsync(Game, CancellationToken.None);
        result.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:2"]);
        result.Items.Should().OnlyContain(i => i.Labels.Contains(UpdateLabels.WatchedThread) && i.Highlights != null);
        forum.ThreadRequests(WowForum.DevNotesThread).Should().Be(1, "it is read once, as a watched thread");
    }

    [Fact]
    public async Task A_thread_that_changed_between_the_list_and_the_read_is_skipped()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(Blizzard(700, "Beta Client Update - October 8", TimeSpan.FromMinutes(30)));
        forum.Threads.Add(Blizzard(710, "WoW Forever Patch Notes", TimeSpan.FromMinutes(25)));
        forum.Threads.Add(Blizzard(720, "WoW Forever Hotfixes", TimeSpan.FromMinutes(15)));
        forum.Override = request => request.RequestUri!.AbsolutePath switch
        {
            "/en/wow/t/700.json" => WowForum.NotFound(), // deleted meanwhile
            "/en/wow/t/710.json" => WowForum.Json(WowForum.ThreadJson(forum.Thread(710) with { Category = 171 })), // moved meanwhile
            "/en/wow/t/720.json" => WowForum.Json(WowForum.ThreadJson(new ForumThreadSpec(720, "WoW Forever Hotfixes", Now, ByBlizzard: false, Cooked: Notes))), // not Blizzard's after all
            _ => null,
        };
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None);
        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        result.Items.Should().BeEmpty();
        result.SkippedItems.Should().Be(3);
    }

    [Fact]
    public async Task Older_list_pages_are_only_read_while_they_are_inside_the_lookback_and_within_the_page_bound()
    {
        var (provider, forum) = Create(new BlizzardForumOptions { DiscoveryLookbackHours = 6, MaxListPages = 3 });
        for (var i = 0; i < 75; i++)
            forum.Threads.Add(Player(1000 + i, "player thread " + i, TimeSpan.FromMinutes(2 * i + 1))); // 75 threads in 2.5 hours
        forum.Threads.Add(Blizzard(700, "Beta Client Update - October 8", TimeSpan.FromMinutes(121))); // on the third page
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None);
        forum.RequestPaths.Where(p => p.Contains("latest.json")).Should().Equal(
            "/en/wow/latest.json?category=349&order=created", "/en/wow/latest.json?category=349&order=created&page=1", "/en/wow/latest.json?category=349&order=created&page=2");
        result.Items.Select(i => i.ExternalId).Should().Equal("700:1");

        var (quiet, quietForum) = Create();
        for (var i = 0; i < 40; i++)
            quietForum.Threads.Add(Player(1000 + i, "player thread " + i, TimeSpan.FromMinutes(20 * i + 1))); // the first page already reaches back 9 hours
        await quiet.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None);
        quietForum.RequestPaths.Should().Equal("/en/wow/latest.json?category=349&order=created");
    }

    [Fact]
    public async Task At_most_the_configured_number_of_threads_is_opened_per_round_and_the_rest_waits()
    {
        var (provider, forum) = Create(new BlizzardForumOptions { MaxThreadRequests = 2 });
        for (var i = 0; i < 5; i++)
            forum.Threads.Add(Blizzard(700 + i, "Beta Client Update - build " + i, TimeSpan.FromMinutes(10 + i)));
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None);
        result.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["700:1", "701:1"], "newest first; a post is never judged by its excerpt instead");
        forum.RequestPaths.Count(p => p.Contains("/t/")).Should().Be(2);
        result.Detail.Should().EndWith("3 left for the next round");
    }

    // ---------- followed threads ----------

    [Fact]
    public async Task A_followed_thread_gives_the_same_first_post_and_blizzards_later_posts_but_no_player_post()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(new ForumThreadSpec(700, "Beta Client Update - October 2", Now.AddHours(-2), ByBlizzard: true, Posts:
        [
            new ForumPostSpec(1, Notes, Now.AddHours(-2)),
            new ForumPostSpec(2, Notes, Now.AddHours(-1), Tracked: false), // a player quoting the notes
            new ForumPostSpec(3, ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70310", "A follow-up fix."), Now.AddMinutes(-10)),
        ]));
        forum.Threads.Add(Player(900, "an older player thread", TimeSpan.FromHours(7)));
        var game = Game with { WatchedThreadIds = [] };
        var asNewThread = (await provider.FetchAsync(game, CancellationToken.None)).Items.Should().ContainSingle().Subject;
        forum.Requests.Clear();

        var result = await provider.FetchAsync(game, new UpdateFetchContext(["700"]), CancellationToken.None);
        forum.RequestPaths.Should().Equal(["/en/wow/t/700.json", "/en/wow/latest.json?category=349&order=created"], "a followed thread is read once, not again as a new thread");
        result.Items.Select(i => i.ExternalId).Should().Equal("700:1", "700:3");
        var first = result.Items[0];
        (first.ContentHash, first.Title).Should().Be((asNewThread.ContentHash, asNewThread.Title), "following a thread never changes its first post");
        first.Labels.Should().Equal(UpdateLabels.FirstPost);
        var later = result.Items[1];
        later.Labels.Should().BeEquivalentTo([UpdateLabels.Reply, UpdateLabels.WatchedThread]);
        (later.Title, later.CanonicalUrl, later.PublishedAt).Should().Be(("Beta Client Update - October 2", "https://us.forums.blizzard.com/en/wow/t/700/3", Now.AddMinutes(-10)));
        Game.Classifier.Classify(later).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, "client_update"));
        result.Detail.Should().Be("2 threads listed, 0 opened by Blizzard, 0 read in full, 0 watched, 1 followed");
    }

    [Fact]
    public async Task Only_threads_blizzard_opened_are_followed_and_their_number_is_bounded()
    {
        var (provider, forum) = Create();
        SeedDevNotes(forum);
        forum.Threads.Add(new ForumThreadSpec(800, "Patch Notes are wrong about hunters", Now.AddDays(-2), Posts:
            [new ForumPostSpec(1, "<p>player opinion</p>", Now.AddDays(-2), Tracked: false), new ForumPostSpec(2, Notes, Now.AddDays(-1))]));
        for (var i = 0; i < 6; i++)
            forum.Threads.Add(Blizzard(700 + i, "Beta Client Update - build " + i, TimeSpan.FromDays(1) + TimeSpan.FromHours(i)));
        var context = new UpdateFetchContext(["2360696", "800", "abc", "-4", "999999", "700", "700", "701", "702", "703", "704", "705"]);
        var result = await provider.FetchAsync(Game, context, CancellationToken.None);

        // The threads read post by post come before the list: the watched thread once, then at most five followed threads in
        // the order given — unreadable ones count, they cost a request too. (703-705 are still among the listed threads of
        // this small forum and are opened as new threads afterwards, by the list's own bound.)
        forum.RequestPaths.TakeWhile(p => !p.Contains("latest.json")).Should().Equal(
            "/en/wow/t/2360696.json", "/en/wow/t/800.json", "/en/wow/t/999999.json", "/en/wow/t/700.json", "/en/wow/t/701.json", "/en/wow/t/702.json");
        result.Detail.Should().Contain("1 watched, 5 followed");
        result.SkippedItems.Should().Be(2, "a player's thread is never followed, and a thread that is gone is skipped");
        result.Items.Should().NotContain(i => i.ExternalId.StartsWith("800:", StringComparison.Ordinal));
        result.Items.Select(i => i.ExternalId).Should().Contain(["2360696:1", "2360696:4", "700:1", "701:1", "702:1"]);
    }

    [Fact]
    public async Task A_long_followed_thread_is_read_to_its_newest_blizzard_post()
    {
        var (provider, forum) = Create();
        var posts = Enumerable.Range(1, 60).Select(n => new ForumPostSpec(n, Notes, Now.AddDays(-1).AddMinutes(n), Tracked: n is 1 or 57)).ToArray();
        forum.Threads.Add(new ForumThreadSpec(700, "Beta Client Update - October 2", Now.AddDays(-1), ByBlizzard: true, Posts: posts));
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, new UpdateFetchContext(["700"]), CancellationToken.None);
        forum.RequestPaths.Where(p => p.Contains("/t/")).Should().Equal("/en/wow/t/700.json", "/en/wow/t/700/57.json");
        result.Items.Select(i => i.ExternalId).Should().Equal("700:1", "700:57");
        result.Detail.Should().NotContain("partial");
    }

    // ---------- one thread's failure is not the round's ----------

    /// <summary>The forum with a followed thread 700 (the one that will not answer), a followed thread 701 and a new update thread 710.</summary>
    private static (BlizzardForumUpdateProvider Provider, WowForum Forum, UpdateFetchContext Context) FollowedAndNew(UpdatesOptions? module = null)
    {
        var (provider, forum) = Create(module: module);
        SeedDevNotes(forum);
        forum.Threads.Add(Blizzard(700, "Beta Client Update - October 2", TimeSpan.FromDays(3)));
        forum.Threads.Add(new ForumThreadSpec(701, "WoW Forever Hotfixes - October 5", Now.AddDays(-2), ByBlizzard: true, Posts:
            [new ForumPostSpec(1, Notes, Now.AddDays(-2)), new ForumPostSpec(2, Notes, Now.AddHours(-3))]));
        forum.Threads.Add(Blizzard(710, "Beta Client Update - October 8", TimeSpan.FromMinutes(20), ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.3 Build 70400", "A fix.", "Another fix.")));
        for (var i = 0; i < 31; i++)
            forum.Threads.Add(Player(1000 + i, "player thread " + i, TimeSpan.FromMinutes(30 + 15 * i))); // the followed threads are off the first page
        return (provider, forum, new UpdateFetchContext(["700", "701"]));
    }

    public static TheoryData<string> ThreadFailures => new() { "500", "503", "timeout", "transport", "not json", "not a thread", "too large", "redirect" };

    private static HttpResponseMessage Failure(string kind) => kind switch
    {
        "500" => WowForum.Json("{}", HttpStatusCode.InternalServerError),
        "503" => WowForum.Json("{}", HttpStatusCode.ServiceUnavailable),
        "timeout" => throw new TaskCanceledException("simulated timeout"),
        "transport" => throw new HttpRequestException("simulated"),
        "not json" => WowForum.Json("{\"post_stream\":{\"posts\":["),
        "not a thread" => WowForum.Json("{\"unexpected\":true}"),
        "too large" => WowForum.Json("{\"padding\":\"" + new string('x', 70 * 1024) + "\"}"),
        "redirect" => WowForum.Json("{}", HttpStatusCode.MovedPermanently),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(ThreadFailures))]
    public async Task A_followed_thread_that_cannot_be_read_is_left_out_and_everything_else_is_still_found(string kind)
    {
        var (provider, forum, context) = FollowedAndNew(new UpdatesOptions { MaxResponseBytes = 64 * 1024 });
        forum.Override = request => request.RequestUri!.AbsolutePath == "/en/wow/t/700.json" ? Failure(kind) : null;
        var result = await provider.FetchAsync(Game, context, CancellationToken.None);

        result.Outcome.Should().Be(UpdateFetchOutcome.Ok, "an old followed thread never decides the round");
        result.Incomplete.Should().BeFalse("a followed thread only adds to what a round finds: the round is complete without it");
        forum.RequestPaths.Should().Equal(
            "/en/wow/t/2360696.json", "/en/wow/t/700.json", "/en/wow/t/701.json", "/en/wow/latest.json?category=349&order=created", "/en/wow/t/710.json");
        result.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:4", "701:1", "701:2", "710:1"],
            "the watched thread, the other followed thread and the new update thread of the category are all read");
        Game.Classifier.Classify(result.Items.Single(i => i.ExternalId == "710:1")).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, "client_update"));
        result.SkippedItems.Should().Be(1);
        result.Detail.Should().Be("partial: 1 followed thread unreadable; 30 threads listed, 1 opened by Blizzard, 1 read in full, 1 watched, 2 followed");
    }

    [Fact]
    public async Task A_followed_thread_that_is_gone_is_skipped_without_calling_the_round_partial()
    {
        var (provider, forum, context) = FollowedAndNew();
        forum.Override = request => request.RequestUri!.AbsolutePath == "/en/wow/t/700.json" ? WowForum.NotFound() : null;
        var result = await provider.FetchAsync(Game, context, CancellationToken.None);
        (result.Outcome, result.SkippedItems, result.Incomplete).Should().Be((UpdateFetchOutcome.Ok, 1, false));
        result.Detail.Should().StartWith("30 threads listed");
        result.Items.Select(i => i.ExternalId).Should().Contain(["701:2", "710:1"]);
    }

    [Fact]
    public async Task A_rate_limit_on_a_followed_thread_ends_the_round_and_nothing_more_is_requested()
    {
        var (provider, forum, context) = FollowedAndNew();
        forum.Override = request =>
        {
            if (request.RequestUri!.AbsolutePath != "/en/wow/t/700.json")
                return null;
            var limited = WowForum.Json("{}", HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(40));
            return limited;
        };
        var result = await provider.FetchAsync(Game, context, CancellationToken.None);

        (result.Outcome, result.Succeeded, result.HttpStatus).Should().Be((UpdateFetchOutcome.RateLimited, false, 429));
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(40), "the forum's own Retry-After is kept");
        result.Items.Should().BeEmpty("the forum asked for a pause: nothing is concluded from the part that was read");
        forum.RequestPaths.Should().Equal(["/en/wow/t/2360696.json", "/en/wow/t/700.json"], "the same host is not asked again in this round");
    }

    [Theory]
    [MemberData(nameof(ThreadFailures))]
    public async Task A_new_thread_that_cannot_be_read_is_left_out_the_others_are_read_and_the_round_stays_incomplete(string kind)
    {
        var (provider, forum) = Create(module: new UpdatesOptions { MaxResponseBytes = 64 * 1024 });
        SeedDevNotes(forum);
        forum.Threads.Add(Blizzard(710, "Beta Client Update - October 8", TimeSpan.FromMinutes(30), ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.3 Build 70400", "A fix.", "Another fix.")));
        forum.Threads.Add(Blizzard(711, "WoW Forever Hotfixes - October 8", TimeSpan.FromMinutes(20)));
        forum.Threads.Add(Blizzard(712, "WoW Forever Patch Notes - October 8", TimeSpan.FromMinutes(10)));
        forum.Threads.Add(Player(900, "an older player thread", TimeSpan.FromHours(7)));
        forum.Override = request => request.RequestUri!.AbsolutePath == "/en/wow/t/711.json" ? Failure(kind) : null;
        var result = await provider.FetchAsync(Game, CancellationToken.None);

        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        result.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2360696:1", "2360696:4", "710:1", "712:1"], "one unreadable thread does not take the others with it");
        result.Incomplete.Should().BeTrue("a new thread is still to be read: the module must not move its catch-up point past it");
        result.SkippedItems.Should().Be(1);
        result.Detail.Should().Be("partial: 1 new thread unreadable; 5 threads listed, 3 opened by Blizzard, 3 read in full, 1 watched");

        forum.Override = null;
        var next = await provider.FetchAsync(Game, CancellationToken.None);
        (next.Incomplete, next.SkippedItems).Should().Be((false, 0));
        next.Items.Select(i => i.ExternalId).Should().Contain("711:1");
        next.Detail.Should().NotContain("partial");
    }

    [Fact]
    public async Task Threads_left_for_the_next_round_keep_the_round_incomplete_only_while_they_are_inside_the_lookback()
    {
        var (provider, forum) = Create(new BlizzardForumOptions { MaxThreadRequests = 2 });
        for (var i = 0; i < 3; i++)
            forum.Threads.Add(Blizzard(700 + i, "Beta Client Update - build " + i, TimeSpan.FromMinutes(10 + i)));
        (await provider.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None)).Incomplete.Should().BeTrue("one recent thread waits for the next round");

        var (quiet, quietForum) = Create(new BlizzardForumOptions { MaxThreadRequests = 2 });
        for (var i = 0; i < 3; i++)
            quietForum.Threads.Add(Blizzard(700 + i, "Beta Client Update - build " + i, TimeSpan.FromDays(2 + i))); // old threads of a quiet category
        var result = await quiet.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None);
        result.Detail.Should().EndWith("1 left for the next round");
        result.Incomplete.Should().BeFalse("a thread older than the lookback is not something a catch-up point protects");
    }

    // ---------- Blizzard posts between the first posts and the newest one ----------

    private static ForumThreadSpec OpenThread(long id, params int[] blizzardPosts) =>
        new(id, "Beta Client Update - October 2", Now.AddDays(-1), ByBlizzard: true, Posts: Enumerable.Range(1, blizzardPosts.Max())
            .Select(n => new ForumPostSpec(n, blizzardPosts.Contains(n) ? ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build " + (70300 + n), "A fix.", "Another fix.") : "<p>player reply</p>",
                Now.AddDays(-1).AddMinutes(n), Tracked: blizzardPosts.Contains(n))).ToArray());

    [Fact]
    public async Task Two_blizzard_replies_far_apart_in_an_open_thread_are_both_found_in_one_round()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(OpenThread(700, 1, 30, 60)); // Blizzard opens, players reply, Blizzard reply A (#30), players again, Blizzard reply B (#60, the latest)
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, new UpdateFetchContext(["700"]), CancellationToken.None);

        forum.RequestPaths.Where(p => p.Contains("/t/")).Should().Equal(["/en/wow/t/700.json", "/en/wow/t/700/60.json", "/en/wow/t/700/30.json"],
            "the thread, then exactly the two places the forum's markers name — the newest first, nothing searched by guessing");
        result.Items.Select(i => i.ExternalId).Should().Equal("700:1", "700:30", "700:60");
        result.Items.Should().OnlyContain(i => Game.Classifier.Classify(i).Classification == UpdateClassification.Update);
        (result.SkippedItems, result.Incomplete).Should().Be((0, false));
        result.Detail.Should().NotContain("partial");
    }

    [Fact]
    public async Task Blizzard_replies_close_together_cost_one_request_and_no_place_is_asked_twice()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(OpenThread(700, 1, 52, 60));
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, new UpdateFetchContext(["700"]), CancellationToken.None);
        forum.RequestPaths.Where(p => p.Contains("/t/")).Should().Equal(["/en/wow/t/700.json", "/en/wow/t/700/52.json"], "one answer from the lower post onward brings both");
        result.Items.Select(i => i.ExternalId).Should().Equal("700:1", "700:52", "700:60");

        var (early, earlyForum) = Create();
        earlyForum.Threads.Add(OpenThread(700, 1, 12, 19));
        await early.FetchAsync(Game with { WatchedThreadIds = [] }, new UpdateFetchContext(["700"]), CancellationToken.None);
        earlyForum.ThreadRequests(700).Should().Be(1, "posts that came with the thread are not asked for again");
    }

    [Fact]
    public async Task A_thread_never_costs_more_than_three_requests_and_what_is_then_missing_is_counted_not_guessed()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(OpenThread(700, 1, 30, 60, 90, 120));
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, new UpdateFetchContext(["700"]), CancellationToken.None);

        forum.RequestPaths.Where(p => p.Contains("/t/")).Should().Equal(["/en/wow/t/700.json", "/en/wow/t/700/120.json", "/en/wow/t/700/90.json"], "the newest posts first, within the bound");
        result.Items.Select(i => i.ExternalId).Should().Equal("700:1", "700:90", "700:120");
        result.SkippedItems.Should().Be(2);
        result.Detail.Should().StartWith("partial: 2 Blizzard posts not read; ");
        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
    }

    // ---------- after an outage ----------

    /// <summary>A category with one thread every 15 minutes for 30 hours: a list page holds 7.5 hours.</summary>
    private static void SteadyCategory(WowForum forum)
    {
        for (var i = 0; i < 120; i++)
            forum.Threads.Add(Player(1000 + i, "player thread " + i, TimeSpan.FromMinutes(1 + 15 * i)));
    }

    private static readonly string ClientUpdate = ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.3 Build 70400", "A fix.", "Another fix.");

    [Fact]
    public async Task The_list_is_read_back_to_the_catch_up_point_after_an_outage_and_no_further_in_a_normal_round()
    {
        var game = Game with { WatchedThreadIds = [] };
        async Task<(UpdateFetchResult Result, int Pages)> RoundAsync(TimeSpan? sinceLastCompleteRound, TimeSpan threadAge)
        {
            var (provider, forum) = Create();
            SteadyCategory(forum);
            forum.Threads.Add(Blizzard(700, "Beta Client Update - October 8", threadAge, ClientUpdate));
            var context = sinceLastCompleteRound is { } gap ? new UpdateFetchContext([], Now - gap) : UpdateFetchContext.None;
            var result = await provider.FetchAsync(game, context, CancellationToken.None);
            return (result, forum.RequestPaths.Count(p => p.Contains("latest.json")));
        }

        // A normal round: the usual six hours — one page here, whatever the catch-up point says.
        var normal = await RoundAsync(TimeSpan.FromMinutes(15), TimeSpan.FromHours(8));
        (normal.Pages, normal.Result.Items.Count).Should().Be((1, 0), "a thread eight hours old is beyond a normal round");
        (await RoundAsync(TimeSpan.FromHours(2), TimeSpan.FromHours(8))).Pages.Should().Be(1, "a last round two hours ago is inside the usual lookback");
        (await RoundAsync(null, TimeSpan.FromHours(8))).Pages.Should().Be(1, "without a catch-up point the usual lookback applies");

        // After eight and twenty hours without a complete round, the thread opened meanwhile is found.
        var eight = await RoundAsync(TimeSpan.FromHours(9), TimeSpan.FromHours(8));
        (eight.Pages, eight.Result.Items.Select(i => i.ExternalId).SingleOrDefault()).Should().Be((2, "700:1"));
        var twenty = await RoundAsync(TimeSpan.FromHours(20), TimeSpan.FromHours(19));
        (twenty.Pages, twenty.Result.Items.Select(i => i.ExternalId).SingleOrDefault()).Should().Be((3, "700:1"));
        twenty.Result.Detail.Should().NotContain("partial", "the third page reached back past the catch-up point");
        (eight.Result.Incomplete, twenty.Result.Incomplete).Should().Be((false, false));
    }

    [Fact]
    public async Task The_page_bound_stays_hard_when_the_catch_up_point_lies_beyond_it_and_the_round_says_so()
    {
        var game = Game with { WatchedThreadIds = [] };
        var (provider, forum) = Create();
        SteadyCategory(forum);
        forum.Threads.Add(Blizzard(700, "Beta Client Update - October 8", TimeSpan.FromHours(23), ClientUpdate)); // on the fourth page
        var result = await provider.FetchAsync(game, new UpdateFetchContext([], Now.AddHours(-24)), CancellationToken.None);

        forum.RequestPaths.Should().HaveCount(3, "three pages are the bound, however far back the catch-up point lies");
        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        result.Items.Should().BeEmpty();
        result.Detail.Should().Be("partial: list page bound reached before the catch-up point; 90 threads listed, 0 opened by Blizzard, 0 read in full, 0 watched");
        result.Incomplete.Should().BeFalse("no later round can read more pages: holding the catch-up point would gain nothing");

        // A category so busy that three pages do not even hold the usual lookback is not partial in a normal round.
        var (busy, busyForum) = Create();
        for (var i = 0; i < 100; i++)
            busyForum.Threads.Add(Player(1000 + i, "player thread " + i, TimeSpan.FromMinutes(1 + i)));
        var normal = await busy.FetchAsync(game, new UpdateFetchContext([], Now.AddMinutes(-15)), CancellationToken.None);
        busyForum.RequestPaths.Should().HaveCount(3);
        normal.Detail.Should().Be("90 threads listed, 0 opened by Blizzard, 0 read in full, 0 watched", "everything since the last round was seen");
    }

    // ---------- request bounds ----------

    [Fact]
    public async Task The_worst_case_round_with_the_shipped_settings_is_twenty_seven_requests()
    {
        var (provider, forum) = Create();
        // One watched thread with Blizzard posts in three places: 3 requests.
        ForumPostSpec[] Long(DateTimeOffset from) => Enumerable.Range(1, 60).Select(n => new ForumPostSpec(n, Notes, from.AddMinutes(n), Tracked: n is 1 or 30 or 60)).ToArray();
        forum.SetDevNotes("WoW Forever Beta Development Notes", Long(Now.AddDays(-30)));
        // Five followed threads, each as long: 15 requests. (A sixth is never read.)
        for (var i = 0; i < 6; i++)
            forum.Threads.Add(new ForumThreadSpec(600 + i, "Beta Client Update - old " + i, Now.AddDays(-2), ByBlizzard: true, Posts: Long(Now.AddDays(-2))));
        // A very busy category after an outage: three list pages (3 requests) with eight new Blizzard update threads, six of which are opened (6 requests).
        for (var i = 0; i < 100; i++)
            forum.Threads.Add(i < 8 ? Blizzard(700 + i, "Beta Client Update - new " + i, TimeSpan.FromMinutes(i + 1)) : Player(1000 + i, "player thread " + i, TimeSpan.FromMinutes(i + 1)));
        var result = await provider.FetchAsync(Game, new UpdateFetchContext(["600", "601", "602", "603", "604", "605"], Now.AddHours(-24)), CancellationToken.None);

        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        forum.Requests.Should().HaveCount(27, "3 (watched) + 15 (followed) + 3 (list pages) + 6 (new threads) — a catch-up round costs no more than that");
        result.Detail.Should().Be(
            "partial: list page bound reached before the catch-up point; 90 threads listed, 8 opened by Blizzard, 6 read in full, 1 watched, 5 followed, 2 left for the next round");
        new BlizzardForumOptions().WorstCaseRequestsPerRound.Should().Be(39, "the same sum with the five watched threads a game may name: 3 x (5 + 5) + 3 + 6");
    }

    [Fact]
    public async Task More_watched_threads_than_a_game_may_name_are_never_read()
    {
        var (provider, forum) = Create();
        var game = Game with { WatchedThreadIds = Enumerable.Range(1, 9).Select(n => (800 + n).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList() };
        await provider.FetchAsync(game, CancellationToken.None);
        forum.RequestPaths.Count(p => p.Contains("/t/")).Should().Be(BlizzardForumOptions.MaxWatchedThreads);
    }

    [Fact]
    public void Thread_following_is_a_provider_setting_and_a_post_id_names_its_thread()
    {
        var (provider, _) = Create();
        provider.ThreadFollow.Should().Be(new ThreadFollowRule(TimeSpan.FromDays(7), 5));
        Create(new BlizzardForumOptions { FollowThreadDays = 0 }).Provider.ThreadFollow.Should().BeNull("0 days: threads are not followed");
        Create(new BlizzardForumOptions { MaxFollowedThreads = 0 }).Provider.ThreadFollow.Should().BeNull();
        provider.ThreadIdOf("2358655:4").Should().Be("2358655");
        foreach (var id in new[] { "abc:1", "2358655", ":4", "", "-5:1", "0:1", "12 :3" })
            provider.ThreadIdOf(id).Should().BeNull(id);
        new BlizzardForumOptions { FollowThreadDays = 31 }.Validate().Should().ContainSingle();
        new BlizzardForumOptions { MaxFollowedThreads = 11 }.Validate().Should().ContainSingle();
        new BlizzardForumOptions { FollowThreadDays = 0, MaxFollowedThreads = 0 }.Validate().Should().BeEmpty();
    }

    [Fact]
    public async Task A_post_dated_in_the_future_has_no_publication_time()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(new ForumThreadSpec(700, "Beta Client Update - October 8", Now.AddMinutes(-5), ByBlizzard: true,
            Posts: [new ForumPostSpec(1, Notes, Now.AddDays(2))]));
        (await provider.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None)).Items.Single().PublishedAt.Should().BeNull();
    }

    // ---------- failures ----------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFetchOutcome.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UpdateFetchOutcome.ServerError)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateFetchOutcome.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, UpdateFetchOutcome.HttpError)]
    [InlineData(HttpStatusCode.MovedPermanently, UpdateFetchOutcome.HttpError)]
    public async Task A_failing_category_list_fails_the_round_as_its_own_kind(HttpStatusCode status, UpdateFetchOutcome expected)
    {
        var (provider, forum) = Create();
        SeedDevNotes(forum);
        forum.Override = request => request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal) ? WowForum.Json("{}", status) : null;
        var result = await provider.FetchAsync(Game, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.Succeeded.Should().BeFalse();
        result.Items.Should().BeEmpty("nothing is concluded from half an answer, not even the watched thread's posts");
    }

    [Fact]
    public async Task A_server_error_on_the_watched_thread_fails_the_round_and_a_rate_limit_on_a_new_thread_ends_it()
    {
        var (provider, forum) = Create();
        SeedDevNotes(forum);
        forum.Threads.Add(Blizzard(700, "Beta Client Update - October 8", TimeSpan.FromMinutes(30)));
        forum.Override = request => request.RequestUri!.AbsolutePath == "/en/wow/t/2360696.json" ? WowForum.Json("{}", HttpStatusCode.BadGateway) : null;
        (await provider.FetchAsync(Game, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.ServerError);

        forum.Override = request => request.RequestUri!.AbsolutePath == "/en/wow/t/700.json" ? WowForum.Json("{}", HttpStatusCode.TooManyRequests) : null;
        var limited = await provider.FetchAsync(Game, CancellationToken.None);
        limited.Outcome.Should().Be(UpdateFetchOutcome.RateLimited);
        limited.RetryAfter.Should().Be(TimeSpan.FromMinutes(30));
        limited.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_timeout_a_transport_error_and_a_shutdown_are_told_apart()
    {
        var (provider, forum) = Create();
        forum.Override = _ => throw new TaskCanceledException("simulated timeout");
        (await provider.FetchAsync(Game, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.Timeout);
        forum.Override = _ => throw new HttpRequestException("simulated");
        (await provider.FetchAsync(Game, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.TransportError);

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        await provider.Invoking(p => p.FetchAsync(Game, stopping.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("{\"topic_list\":{\"topics\":[", UpdateFetchOutcome.Malformed)]
    [InlineData("<html>Just a moment...</html>", UpdateFetchOutcome.Malformed)]
    [InlineData("[]", UpdateFetchOutcome.UnexpectedSchema)]
    [InlineData("{}", UpdateFetchOutcome.UnexpectedSchema)]
    [InlineData("{\"topic_list\":[]}", UpdateFetchOutcome.UnexpectedSchema)]
    [InlineData("{\"topic_list\":{\"topics\":{}}}", UpdateFetchOutcome.UnexpectedSchema)]
    public async Task A_category_list_that_is_not_the_expected_json_fails_the_round(string body, UpdateFetchOutcome expected)
    {
        var (provider, forum) = Create();
        SeedDevNotes(forum);
        forum.Override = request => request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal) ? WowForum.Json(body) : null;
        var result = await provider.FetchAsync(Game, CancellationToken.None);
        result.Outcome.Should().Be(expected);
        result.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"id\":2360696,\"title\":\"x\",\"category_id\":349}")]
    [InlineData("{\"id\":2360696,\"title\":\"x\",\"category_id\":349,\"post_stream\":{\"posts\":5}}")]
    [InlineData("{\"id\":\"2360696\",\"title\":\"x\",\"category_id\":349,\"post_stream\":{\"posts\":[]}}")]
    [InlineData("{\"post_stream\":{\"posts\":[]}}")]
    [InlineData("not json at all")]
    public async Task A_thread_answer_that_is_not_a_thread_fails_the_round(string body)
    {
        var (provider, forum) = Create();
        forum.Threads.Add(Player(900, "a player thread", TimeSpan.FromMinutes(5)));
        forum.Override = request => request.RequestUri!.AbsolutePath == "/en/wow/t/2360696.json" ? WowForum.Json(body) : null;
        var result = await provider.FetchAsync(Game, CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().BeOneOf(UpdateFetchOutcome.Malformed, UpdateFetchOutcome.UnexpectedSchema);
    }

    [Fact]
    public async Task An_empty_category_list_is_empty_not_a_baseline_and_broken_entries_are_skipped_one_by_one()
    {
        var (provider, forum) = Create();
        var empty = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None);
        empty.Outcome.Should().Be(UpdateFetchOutcome.Empty);

        forum.Override = request => request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)
            ? WowForum.Json("""
                {"topic_list":{"unknown_field":1,"topics":[
                  5, "text", {"id":1}, {"id":2,"title":"no category"}, {"id":"3","title":"id is text","category_id":349},
                  {"id":4,"title":"A private message","category_id":349,"archetype":"private_message","first_tracked_post":{"post_number":1}},
                  {"id":5,"title":"An unlisted thread","category_id":349,"visible":false,"first_tracked_post":{"post_number":1}},
                  {"id":6,"title":"WoW Forever Beta Known Issues","category_id":349,"created_at":"2026-10-08T11:00:00.000Z","first_tracked_post":{"group":"x","post_number":1},"future_field":{"a":[1,2]}},
                  {"id":7,"title":"Marker without a number","category_id":349,"created_at":"2026-10-08T11:00:00.000Z","first_tracked_post":{"group":"x"}},
                  {"id":8,"title":"Blue reply further down","category_id":349,"created_at":"not a date","first_tracked_post":{"post_number":3}}
                ]}}
                """)
            : null;
        var result = await provider.FetchAsync(Game with { WatchedThreadIds = [] }, CancellationToken.None);
        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        result.SkippedItems.Should().Be(7, "five broken entries, the private message and the unlisted thread");
        result.Items.Select(i => i.ExternalId).Should().Equal(["6:1"], "only a thread whose FIRST post is Blizzard's is a Blizzard thread");
    }

    // ---------- link policy ----------

    [Theory]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/2360696/4", true)]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/700/1", true)]
    [InlineData("http://us.forums.blizzard.com/en/wow/t/700/1", false)]
    [InlineData("https://us.forums.blizzard.com:8443/en/wow/t/700/1", false)]
    [InlineData("https://user@us.forums.blizzard.com/en/wow/t/700/1", false)]
    [InlineData("https://eu.forums.blizzard.com/en/wow/t/700/1", false)]
    [InlineData("https://us.forums.blizzard.com.evil.example/en/wow/t/700/1", false)]
    [InlineData("https://evil.example/en/wow/t/700/1", false)]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/some-slug/700/1", false)]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/700", false)]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/700/1/", false)]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/700/1?u=x", false)]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/700/1#x", false)]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/0700/1", false)]
    [InlineData("https://us.forums.blizzard.com/en/wow/t/700/../../../login", false)]
    [InlineData("https://us.forums.blizzard.com/en/d3/t/700/1", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_forums_own_post_link_is_a_link(string? url, bool expected)
    {
        BlizzardForumUrl.IsPostLink(url).Should().Be(expected);
        var (provider, _) = Create();
        provider.IsCanonicalUrl(url ?? "").Should().Be(expected);
    }

    [Fact]
    public void Provider_settings_are_validated()
    {
        new BlizzardForumOptions().Validate().Should().BeEmpty();
        new BlizzardForumOptions { PollIntervalMinutes = 1 }.Validate().Should().ContainSingle();
        new BlizzardForumOptions { DiscoveryLookbackHours = 0 }.Validate().Should().ContainSingle();
        new BlizzardForumOptions { MaxListPages = 9 }.Validate().Should().ContainSingle();
        new BlizzardForumOptions { MaxThreadRequests = 0 }.Validate().Should().ContainSingle();
        new BlizzardForumOptions { FollowThreadDays = 31 }.Validate().Should().ContainSingle();
        new BlizzardForumOptions { MaxFollowedThreads = 11 }.Validate().Should().ContainSingle();
    }

    [Fact]
    public void No_valid_combination_of_settings_allows_more_than_fifty_requests_in_a_round()
    {
        BlizzardForumOptions.MaxRequestsPerRound.Should().Be(50);
        // Every setting at its own maximum would be 3 x (5 + 10) + 5 + 20 = 70: refused as a whole.
        var all = new BlizzardForumOptions { MaxFollowedThreads = 10, MaxListPages = 5, MaxThreadRequests = 20 };
        all.WorstCaseRequestsPerRound.Should().Be(70);
        all.Validate().Should().ContainSingle().Which.Should().Contain("at most 50");
        new BlizzardForumOptions { MaxFollowedThreads = 10 }.Validate().Should().ContainSingle("54 requests");
        new BlizzardForumOptions { MaxFollowedThreads = 10, MaxListPages = 1, MaxThreadRequests = 4 }.Validate().Should().BeEmpty("exactly 50");
        new BlizzardForumOptions { MaxThreadRequests = 17 }.Validate().Should().BeEmpty("exactly 50");
        new BlizzardForumOptions { MaxThreadRequests = 18 }.Validate().Should().ContainSingle();

        // Whatever passes validation stays at or under the bound.
        for (var followed = 0; followed <= 10; followed++)
            for (var pages = 1; pages <= 5; pages++)
                for (var opened = 1; opened <= 20; opened++)
                {
                    var options = new BlizzardForumOptions { MaxFollowedThreads = followed, MaxListPages = pages, MaxThreadRequests = opened };
                    if (options.Validate().Count == 0)
                        options.WorstCaseRequestsPerRound.Should().BeLessThanOrEqualTo(BlizzardForumOptions.MaxRequestsPerRound);
                }
    }
}
