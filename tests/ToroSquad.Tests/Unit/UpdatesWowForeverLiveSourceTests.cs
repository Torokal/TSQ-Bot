using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
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
/// World of Warcraft: Forever after release, against a scripted forum in the shape the real one had on 2026-11-10 (thread and
/// post fields, tracked-post markers, the running hotfix thread with its opening post and one reply per day, a parent
/// category that lists its subcategories, pinned threads). All post text is synthetic; the titles are the kinds of titles
/// Blizzard used. Nothing here touches the network.
/// </summary>
public sealed class UpdatesWowForeverLiveSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 6, 0, 0, TimeSpan.Zero);
    private static readonly GameUpdateDefinition Live = WowForeverGame.Create(new WowForeverSettings());
    private static readonly GameUpdateDefinition Beta = WowForeverGame.Create(WowForum.BetaSettings);
    private static readonly WowForeverUpdateClassifier Classifier = new();

    private static readonly DateTimeOffset OpenedAt = new(2026, 11, 6, 2, 34, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset Nov6PostedAt = new(2026, 11, 7, 1, 7, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset Nov9PostedAt = new(2026, 11, 10, 4, 23, 46, TimeSpan.Zero);

    private static readonly string Nov5 = ForumHtmlSamples.HotfixDay("November 5, 2026", ("Classes", ["Synthetic fix one.", "Synthetic fix two."]));
    private static readonly string Nov6 = ForumHtmlSamples.HotfixDay("November 6, 2026", ("Classes", ["Synthetic fix three.", "Synthetic fix four."]), ("Dungeons", ["Synthetic fix five."]));
    private static readonly string Nov9 = ForumHtmlSamples.HotfixDay("November 9, 2026", ("Classes", ["Synthetic fix six.", "Synthetic fix seven."]), ("Quests", ["Synthetic fix eight."]));

    public const string AppliedXp = "Earlier today we deployed a hotfix that increased the experience dungeon enemies give, so another run feels rewarding.";
    public const string AppliedPvp = "To even out the factions on PvP realms, we've adjusted the character creation system to look at the overall balance of each region.";
    public const string PlannedTuning = "We are still investigating these class issues and plan to release hotfixes on Tuesday to address them.";

    private static (BlizzardForumUpdateProvider Provider, WowForum Forum) Create(BlizzardForumOptions? options = null)
    {
        var forum = new WowForum();
        var clock = new FakeTimeProvider(Now);
        var http = new ProviderHttp(new SingleHandlerFactory(forum), Options.Create(new UpdatesOptions()), clock, NullLogger<ProviderHttp>.Instance);
        return (new BlizzardForumUpdateProvider(http, Options.Create(options ?? new BlizzardForumOptions()), clock, NullLogger<BlizzardForumUpdateProvider>.Instance), forum);
    }

    /// <summary>The hotfix thread as it stood on 2026-11-10: opening post with three days, two moderator notes, two daily replies.</summary>
    public static ForumThreadSpec HotfixThread(string title = "WoW: Forever Hotfixes - November 9, 2026", params ForumPostSpec[] extra) =>
        new(WowForum.HotfixThread, title, OpenedAt, ByBlizzard: true, Category: WowForum.BugReport, Posts:
        [
            new ForumPostSpec(1, ForumHtmlSamples.HotfixOpening(Nov9, Nov6, Nov5), OpenedAt),
            new ForumPostSpec(2, "", OpenedAt.AddSeconds(4), Tracked: false, PostType: 3),
            new ForumPostSpec(3, "", OpenedAt.AddSeconds(9), Tracked: false, PostType: 3),
            new ForumPostSpec(4, Nov6, Nov6PostedAt),
            new ForumPostSpec(5, Nov9, Nov9PostedAt),
            .. extra,
        ]);

    private static ForumThreadSpec Announcement(long id, string title, DateTimeOffset created, string text, int category = WowForum.GeneralDiscussion) =>
        new(id, title, created, ByBlizzard: true, Category: category, Cooked: ForumHtmlSamples.Paragraphs(text), Excerpt: text[..Math.Min(60, text.Length)]);

    private static GameUpdateCandidate Post(string title, string text, params string[] labels) =>
        new("blizzard", WowForeverGame.Key, "1:1", title, "https://us.forums.blizzard.com/en/wow/t/1/1", Now, labels.Length == 0 ? [UpdateLabels.FirstPost] : labels,
            ForumPostReader.Read(text).Text, ForumPostReader.Read(text).Highlights);

    // ---------- 1. the old beta profile misses the live posts; the live profile finds them ----------

    [Fact]
    public async Task The_beta_profile_misses_every_live_post_and_the_live_profile_finds_each_of_them_once()
    {
        var (provider, forum) = Create();
        forum.SetDevNotes("WoW Forever Beta Development Notes – Updated November 2", new ForumPostSpec(1, ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.4 Build 70500", "A fix.", "Another fix."), Now.AddDays(-40)));
        forum.Threads.Add(HotfixThread());
        forum.Threads.Add(Announcement(2408781, "PvP Faction Balance Updates - November 10", Now.AddMinutes(-40), AppliedPvp));

        var beta = await provider.FetchAsync(Beta, CancellationToken.None);
        beta.Items.Select(i => i.ExternalId).Should().Equal(["2360696:1"], "category 349 and the Development Notes thread know nothing of the release");

        forum.Requests.Clear();
        var live = await provider.FetchAsync(Live, CancellationToken.None);
        live.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        live.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2403040:1", "2403040:4", "2403040:5", "2408781:1"]);
        live.Items.Select(i => (i.ExternalId, Live.Classifier.Classify(i).Reason)).Should().BeEquivalentTo(
            [("2403040:1", "hotfix"), ("2403040:4", "hotfix"), ("2403040:5", "hotfix"), ("2408781:1", "applied_change")]);
        forum.RequestPaths.Should().Equal(
            "/en/wow/t/2403040.json",
            "/en/wow/latest.json?category=347&order=created",
            "/en/wow/latest.json?category=347&order=created&page=1",
            "/en/wow/latest.json?category=352&order=created",
            "/en/wow/t/2408781.json");
        forum.RequestPaths.Should().NotContain(p => p.Contains("349") || p.Contains("2360696"), "the beta's places are not read any more");
    }

    // ---------- 2. 352 and its parent 346 ----------

    [Fact]
    public async Task A_parent_category_never_stands_for_its_subcategories()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(HotfixThread());
        forum.Threads.Add(Announcement(2406595, "Dungeon Enemy XP Increased - November 8", Now.AddMinutes(-30), AppliedXp));
        var parent = WowForeverGame.Create(new WowForeverSettings { ForumCategoryIds = [WowForum.ForeverParent] });

        var result = await provider.FetchAsync(parent, CancellationToken.None);

        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        result.Items.Should().BeEmpty("the forum lists 347 and 352 under 346, but each thread belongs to its own category only");
        result.Detail.Should().StartWith("partial: no watched thread usable; ", "the hotfix thread lives in 352, which is not configured");
        result.Detail.Should().EndWith(", 2 in other categories ignored", "what the parent listed from its subcategories is said, not used");
        forum.RequestPaths.Should().NotContain("/en/wow/t/2406595.json");
    }

    // ---------- 3-6. classification ----------

    [Theory]
    [InlineData("Dungeon Enemy XP Increased - November 8", AppliedXp)]
    [InlineData("PvP Faction Balance Updates - November 10", AppliedPvp)]
    [InlineData("Mount Speed Change", "A hotfix has been deployed that raised the riding speed of starter mounts.")]
    [InlineData("Ravenholdt Questline", "We just reverted the change to the Ravenholdt questline; it works as before.")]
    public void Blizzard_saying_in_prose_that_a_change_was_applied_is_an_update(string title, string text) =>
        Classifier.Classify(Post(title, ForumHtmlSamples.Paragraphs(text))).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, "applied_change"));

    [Theory]
    [InlineData("Class Tuning Incoming - November 10", PlannedTuning)]
    [InlineData("Class Tuning - November 10", PlannedTuning)]
    [InlineData("Hunter Pets", "We will adjust their stats as needed. Our plan is to make class changes on Tuesday.")]
    [InlineData("Hunter Pets", "We have seen your reports about pets. We are going to make changes next week.")]
    public void A_change_that_is_only_planned_is_not_an_applied_update_even_when_its_date_has_come(string title, string text)
    {
        var verdict = Classifier.Classify(Post(title, ForumHtmlSamples.Paragraphs(text)));
        verdict.Classification.Should().Be(UpdateClassification.NotUpdate);
        verdict.Reason.Should().Be("planned_change");
    }

    [Theory]
    [InlineData("Hunter Pets", "Hotfix. Fixed. Update.")]
    [InlineData("Dungeon experience", "Thanks for all the feedback about dungeon experience, keep it coming.")]
    [InlineData("Dungeon experience", "We've updated the post above with more details.")]
    [InlineData("Dungeon experience", "We've identified a few issues with pets and are looking into them.")]
    public void A_lone_word_a_thank_you_or_a_remark_about_the_notes_is_not_an_update(string title, string text) =>
        Classifier.Classify(Post(title, ForumHtmlSamples.Paragraphs(text))).Should().Be(new UpdateClassificationResult(UpdateClassification.NotUpdate, "untyped_title"));

    [Fact]
    public void Quoted_player_text_known_issues_maintenance_podcasts_and_other_versions_are_never_updates()
    {
        var quoted = "<aside class=\"quote\"><blockquote><p>We deployed a hotfix that fixed my class, thanks!</p></blockquote></aside>\n<p>Glad to hear it.</p>";
        Classifier.Classify(Post("Dungeon experience", quoted)).Should().Be(new UpdateClassificationResult(UpdateClassification.NotUpdate, "untyped_title"),
            "quoted text is left out: only what Blizzard itself wrote counts");
        Classifier.Classify(Post("WoW Forever Known Issues - November 10", ForumHtmlSamples.Paragraphs(AppliedXp))).Reason.Should().Be("excluded_topic");
        Classifier.Classify(Post("Brief Realm Maintenance - November 9", ForumHtmlSamples.Paragraphs(AppliedXp))).Reason.Should().Be("excluded_topic");
        Classifier.Classify(Post("The WoW: Forever Podcast: Episode 4", ForumHtmlSamples.Paragraphs(AppliedXp))).Reason.Should().Be("excluded_topic");
        Classifier.Classify(Post("Season of Discovery Experience Change", ForumHtmlSamples.Paragraphs(AppliedXp))).Should()
            .Be(new UpdateClassificationResult(UpdateClassification.NotUpdate, "other_version_title"));
        Classifier.Classify(Post("Midnight Hotfixes - November 9", Nov9)).Should().Be(new UpdateClassificationResult(UpdateClassification.Ambiguous, "other_version_title"));
        Classifier.Classify(Post("Dungeon Enemy XP Increased", ForumHtmlSamples.Paragraphs(AppliedXp), UpdateLabels.Reply)).Reason
            .Should().Be("reply_outside_watched_thread", "a Blizzard reply in a thread that is not read post by post stays ambiguous");
    }

    [Fact]
    public async Task A_player_thread_that_says_a_hotfix_was_deployed_is_never_even_a_candidate()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(HotfixThread());
        forum.Threads.Add(new ForumThreadSpec(2409000, "Dungeon XP Increased!", Now.AddMinutes(-10), Category: WowForum.GeneralDiscussion,
            Cooked: ForumHtmlSamples.Paragraphs(AppliedXp)));
        var result = await provider.FetchAsync(Live, CancellationToken.None);
        result.Items.Should().NotContain(i => i.ExternalId.StartsWith("2409000", StringComparison.Ordinal));
        forum.RequestPaths.Should().NotContain("/en/wow/t/2409000.json");
    }

    // ---------- 8, 11. the running hotfix thread ----------

    [Fact]
    public async Task Each_hotfix_day_is_one_post_with_its_own_day_and_the_forums_own_publication_time()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(HotfixThread());
        var items = (await provider.FetchAsync(Live, CancellationToken.None)).Items.ToDictionary(i => i.ExternalId);

        items.Keys.Should().BeEquivalentTo(["2403040:1", "2403040:4", "2403040:5"], "the moderator notes (#2, #3) are not posts");
        (items["2403040:5"].Title, items["2403040:5"].PublishedAt).Should().Be(("WoW: Forever Hotfixes – November 9, 2026", Nov9PostedAt),
            "the day comes from the post, the time is when the forum says the reply was posted — never a made-up November 9, 00:00");
        (items["2403040:4"].Title, items["2403040:4"].PublishedAt).Should().Be(("WoW: Forever Hotfixes – November 6, 2026", Nov6PostedAt));
        (items["2403040:1"].Title, items["2403040:1"].PublishedAt).Should().Be(("WoW: Forever Hotfixes – November 5, 2026", OpenedAt),
            "the opening post stands for the day it was published with; its newer days are the replies'");
        items["2403040:1"].Highlights!.Sections.SelectMany(s => s.Items).Should().Equal(["Synthetic fix one.", "Synthetic fix two."],
            "the opening post's card shows its own day only, not the days it collected later");
        items["2403040:5"].Highlights!.ChangeCount.Should().Be(3);
        items.Values.Should().OnlyContain(i => i.Labels.Contains(UpdateLabels.WatchedThread) && Live.Classifier.Classify(i).Reason == "hotfix");
    }

    [Fact]
    public async Task A_new_day_added_to_the_opening_post_and_posted_as_a_reply_is_one_new_post_and_the_opening_post_does_not_change()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(HotfixThread());
        var before = (await provider.FetchAsync(Live, CancellationToken.None)).Items.ToDictionary(i => i.ExternalId);

        var nov10 = ForumHtmlSamples.HotfixDay("November 10, 2026", ("Classes", ["Synthetic fix nine.", "Synthetic fix ten."]));
        var renamed = HotfixThread("WoW: Forever Hotfixes - November 10, 2026", new ForumPostSpec(6, nov10, Now.AddMinutes(5)));
        forum.Replace(renamed with { Posts = [new ForumPostSpec(1, ForumHtmlSamples.HotfixOpening(nov10, Nov9, Nov6, Nov5), OpenedAt), .. renamed.AllPosts.Skip(1)] });
        var after = (await provider.FetchAsync(Live, CancellationToken.None)).Items.ToDictionary(i => i.ExternalId);

        after.Keys.Except(before.Keys).Should().Equal(["2403040:6"]);
        after["2403040:6"].Title.Should().Be("WoW: Forever Hotfixes – November 10, 2026");
        foreach (var id in before.Keys)
            after[id].ContentHash.Should().Be(before[id].ContentHash, $"{id}: a renamed thread and a longer opening post are no change of an earlier day");
    }

    [Fact]
    public async Task The_opening_post_keeps_its_own_day_or_is_left_out_but_never_becomes_another_day()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(HotfixThread());
        var first = (await provider.FetchAsync(Live, CancellationToken.None)).Items.Single(i => i.ExternalId == "2403040:1");
        first.Title.Should().Be("WoW: Forever Hotfixes – November 5, 2026");

        GameUpdateCandidate? Opening(string cooked, DateTimeOffset? created = null)
        {
            var thread = HotfixThread();
            forum.Replace(thread with { Posts = [thread.AllPosts[0] with { Cooked = cooked, Created = created ?? OpenedAt }, .. thread.AllPosts.Skip(1)] });
            var result = provider.FetchAsync(Live, CancellationToken.None).GetAwaiter().GetResult();
            result.Items.Where(i => i.ExternalId != "2403040:1").Select(i => i.ExternalId).Should().BeEquivalentTo(["2403040:4", "2403040:5"], "the replies stand as they are");
            return result.Items.SingleOrDefault(i => i.ExternalId == "2403040:1");
        }

        Opening(ForumHtmlSamples.HotfixOpening(Nov5, Nov6, Nov9))!.ContentHash.Should().Be(first.ContentHash, "another order of the same days changes nothing");
        Opening(ForumHtmlSamples.HotfixOpening(Nov9, Nov6)).Should().BeNull("November 5 removed: November 6 is the reply's day, not the opening post's");
        Opening(ForumHtmlSamples.HotfixOpening(Nov9, Nov6, Nov5.Replace("November 5, 2026", "Nov 5, 2026", StringComparison.Ordinal)))
            .Should().BeNull("a date that no longer reads: its text would fall under November 6, which is the reply's");
        Opening(ForumHtmlSamples.HotfixOpening(ForumHtmlSamples.HotfixDay("November 7, 2026", ("Classes", ["A fix.", "Another fix."]))))
            .Should().BeNull("a day after the post's own publication (Pacific) cannot be the day it was published with");
        var reworded = Opening(ForumHtmlSamples.HotfixOpening(Nov9, Nov6, Nov5.Replace("Synthetic fix two.", "Synthetic fix two, corrected.", StringComparison.Ordinal)))!;
        (reworded.Title, reworded.PublishedAt).Should().Be((first.Title, OpenedAt), "a correction of its own day stays that day, at the forum's publication time");
        reworded.ContentHash.Should().NotBe(first.ContentHash);

        Opening(ForumHtmlSamples.HotfixOpening(Nov9, Nov6));
        (await provider.FetchAsync(Live, CancellationToken.None)).Detail.Should().StartWith("partial: 1 hotfix opening post left out (own day not provable); ");
    }

    [Fact]
    public async Task A_day_only_the_opening_post_has_is_named_in_the_detail_and_not_posted()
    {
        var (provider, forum) = Create();
        var nov10 = ForumHtmlSamples.HotfixDay("November 10, 2026", ("Classes", ["Synthetic fix nine.", "Synthetic fix ten."]));
        forum.Threads.Add(HotfixThread() with
        {
            Posts = [new ForumPostSpec(1, ForumHtmlSamples.HotfixOpening(nov10, Nov9, Nov6, Nov5), OpenedAt), .. HotfixThread().AllPosts.Skip(1)],
        });
        var result = await provider.FetchAsync(Live, CancellationToken.None);

        result.Items.Select(i => i.ExternalId).Should().BeEquivalentTo(["2403040:1", "2403040:4", "2403040:5"], "no post is made up for a day without its own reply");
        result.Detail.Should().StartWith("partial: hotfix day only in the opening post: November 10, 2026; ", "the gap is shown, never reported as full coverage");
    }

    [Fact]
    public async Task A_remark_that_the_notes_were_updated_is_never_a_hotfix_card()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(HotfixThread(extra: new ForumPostSpec(6, ForumHtmlSamples.Paragraphs("We've updated the notes above with one more fix."), Now.AddMinutes(-5))));
        var remark = (await provider.FetchAsync(Live, CancellationToken.None)).Items.Single(i => i.ExternalId == "2403040:6");
        Live.Classifier.Classify(remark).Should().Be(new UpdateClassificationResult(UpdateClassification.Ambiguous, "no_change_list"));
    }

    // ---------- the post reader and titles ----------

    [Fact]
    public void Dated_sections_are_read_only_from_a_post_that_consists_of_them()
    {
        var days = ForumPostReader.DatedSections(ForumHtmlSamples.HotfixOpening(Nov9, Nov6, Nov5));
        days.Select(d => (d.Date, d.Label)).Should().Equal(
            (new DateOnly(2026, 11, 9), "November 9, 2026"), (new DateOnly(2026, 11, 6), "November 6, 2026"), (new DateOnly(2026, 11, 5), "November 5, 2026"));
        days[2].Content.Text.Should().StartWith("# November 5, 2026\n");
        days[2].Content.Highlights.ChangeCount.Should().Be(2);

        ForumPostReader.DatedSections(ForumHtmlSamples.Paragraphs("Phase two begins on", "November 12, 2026")).Should().BeEmpty("a date in prose is not a section");
        ForumPostReader.DatedSections("<p>We have news.</p><p><strong>November 12, 2026</strong></p><ul><li>One.</li></ul>").Should()
            .BeEmpty("an announcement that mentions a date is not cut apart");
        ForumPostReader.DatedSections(null).Should().BeEmpty();

        ForumPostReader.DateLine("Tuesday, November 10, 2026:")!.Value.Label.Should().Be("November 10, 2026");
        ForumPostReader.DateLine("February 30, 2026").Should().BeNull();
        ForumPostReader.DateLine("Nov 10, 2026").Should().BeNull();
        ForumPostReader.DateLine("November 10").Should().BeNull("a day without a year cannot be ordered");
    }

    [Theory]
    [InlineData("WoW: Forever Hotfixes - November 9, 2026", "WoW: Forever Hotfixes")]
    [InlineData("WoW: Forever Hotfixes – November 10", "WoW: Forever Hotfixes")]
    [InlineData("WoW Forever Beta Development Notes – Updated November 2", "WoW Forever Beta Development Notes")]
    [InlineData("WoW: Forever Hotfixes", "WoW: Forever Hotfixes")]
    [InlineData("November 9, 2026", "November 9, 2026")]
    public void A_watched_threads_title_keeps_no_date_blizzard_rewrites(string title, string stable) =>
        BlizzardForumUpdateProvider.StableTitle(title).Should().Be(stable);

    // ---------- 17. failures and bounds ----------

    [Fact]
    public async Task A_rate_limit_on_any_category_ends_the_round_and_a_malformed_or_oversized_list_fails_it()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(HotfixThread());
        forum.Override = r => r.RequestUri!.Query.Contains("category=352") ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(7)) } } : null;
        var limited = await provider.FetchAsync(Live, CancellationToken.None);
        (limited.Outcome, limited.RetryAfter).Should().Be((UpdateFetchOutcome.RateLimited, TimeSpan.FromMinutes(7)));

        forum.Override = r => r.RequestUri!.Query.Contains("category=347") ? WowForum.Json("{\"topic_list\": [") : null;
        (await provider.FetchAsync(Live, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.Malformed);

        forum.Override = r => r.RequestUri!.Query.Contains("category=347") ? WowForum.Json("{\"topic_list\":{\"topics\":[]},\"pad\":\"" + new string('x', 3 * 1024 * 1024) + "\"}") : null;
        (await provider.FetchAsync(Live, CancellationToken.None)).Outcome.Should().Be(UpdateFetchOutcome.TooLarge);
    }

    [Fact]
    public async Task A_blizzard_post_the_bounded_requests_do_not_reach_is_counted_not_hidden()
    {
        var (provider, forum) = Create();
        // A very long thread with Blizzard replies in four places far apart: the opening request and two more reach three.
        var posts = Enumerable.Range(1, 400).Select(n => new ForumPostSpec(n, ForumHtmlSamples.ClientUpdate("WoW Forever 1.61.0 Build 71000", "A fix.", "Another fix."), OpenedAt.AddMinutes(n), Tracked: n is 1 or 100 or 200 or 300)).ToArray();
        forum.Threads.Add(new ForumThreadSpec(WowForum.HotfixThread, "WoW: Forever Hotfixes", OpenedAt, ByBlizzard: true, Category: WowForum.BugReport, Posts: posts));
        var result = await provider.FetchAsync(Live, CancellationToken.None);
        result.Detail.Should().StartWith("partial: 1 Blizzard post not read; ");
        forum.ThreadRequests(WowForum.HotfixThread).Should().Be(BlizzardForumOptions.MaxRequestsPerThread);
    }

    [Fact]
    public async Task The_page_bound_is_reported_per_category_and_a_pinned_old_thread_never_ends_a_list_early()
    {
        var (provider, forum) = Create();
        forum.Threads.Add(HotfixThread());
        // General Discussion is busy: 120 threads in the last two hours; the Bug Report category is quiet but has an old pinned thread.
        for (var i = 0; i < 120; i++)
            forum.Threads.Add(new ForumThreadSpec(2410000 + i, "player thread " + i, Now.AddMinutes(-i), Category: WowForum.GeneralDiscussion));
        forum.Threads.Add(new ForumThreadSpec(2400001, "Please read before posting bugs", Now.AddDays(-5), ByBlizzard: true, Category: WowForum.BugReport, Pinned: true,
            Cooked: ForumHtmlSamples.Paragraphs("How to report a bug.")));
        forum.Threads.Add(Announcement(2409999, "Quest Credit Fix", Now.AddMinutes(-110), "Earlier today we fixed quest credit for groups.", WowForum.BugReport));
        for (var i = 0; i < 40; i++)
            forum.Threads.Add(new ForumThreadSpec(2411000 + i, "bug report " + i, Now.AddMinutes(-3 * i - 1), Category: WowForum.BugReport));

        var result = await provider.FetchAsync(Live, new UpdateFetchContext([], Now.AddHours(-3)), CancellationToken.None);

        result.Detail.Should().StartWith("partial: list page bound reached before the catch-up point (347); ", "only the busy category ran out of pages");
        result.Detail.Should().EndWith("; lists 347 1.5h, 352 6h+", "how far back each list reached is shown");
        result.Items.Should().Contain(i => i.ExternalId == "2409999:1", "the pinned thread on top did not end the Bug Report list before its second page");
        Live.Classifier.Classify(result.Items.Single(i => i.ExternalId == "2409999:1")).Reason.Should().Be("applied_change");
        forum.RequestPaths.Should().NotContain("/en/wow/t/2400001.json", "an old pinned thread is not downloaded again on every round");
        forum.RequestPaths.Count(p => p.Contains("category=352")).Should().Be(2, "the second page reaches the old hotfix thread, which ends the list");
    }

    // ---------- 18. the hard request bound, counted ----------

    [Fact]
    public async Task No_round_asks_the_forum_more_than_fifty_times_even_with_settings_that_bypass_validation()
    {
        // Settings no validation would accept, handed to the provider directly: the count while the round runs still holds.
        var (provider, forum) = Create(new BlizzardForumOptions { MaxFollowedThreads = 10, MaxListPages = 5, MaxThreadRequests = 20 });
        ForumPostSpec[] Long(DateTimeOffset from) => Enumerable.Range(1, 400).Select(n => new ForumPostSpec(n, Nov9, from.AddMinutes(n), Tracked: n is 1 or 100 or 200 or 300)).ToArray();
        int[] watched = [2403040, 2403041, 2403042, 2403043, 2403044];
        foreach (var id in watched)
            forum.Threads.Add(new ForumThreadSpec(id, "WoW: Forever Hotfixes", Now.AddDays(-3), ByBlizzard: true, Category: WowForum.BugReport, Posts: Long(Now.AddDays(-3))));
        for (var i = 0; i < 10; i++)
            forum.Threads.Add(new ForumThreadSpec(2404000 + i, "WoW: Forever Hotfixes " + i, Now.AddDays(-2), ByBlizzard: true, Category: WowForum.BugReport, Posts: Long(Now.AddDays(-2))));
        for (var i = 0; i < 300; i++)
            forum.Threads.Add(i % 10 == 0
                ? Announcement(2405000 + i, "Fix " + i, Now.AddMinutes(-i), "We fixed a synthetic thing.", i % 20 == 0 ? WowForum.GeneralDiscussion : WowForum.BugReport)
                : new ForumThreadSpec(2405000 + i, "player " + i, Now.AddMinutes(-i), Category: i % 2 == 0 ? WowForum.GeneralDiscussion : WowForum.BugReport));
        var game = WowForeverGame.Create(new WowForeverSettings { ForumCategoryIds = [347, 352, 346], WatchedTopicIds = watched });

        var result = await provider.FetchAsync(game, new UpdateFetchContext(Enumerable.Range(0, 10).Select(i => (2404000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList(),
            Now.AddHours(-24)), CancellationToken.None);

        forum.Requests.Should().HaveCount(BlizzardForumOptions.MaxRequestsPerRound, "every request of the round is counted, and the count stops at the bound");
        result.Outcome.Should().Be(UpdateFetchOutcome.Ok);
        result.Incomplete.Should().BeTrue("what the bound left unread is asked for again: the catch-up point stays");
        result.Detail.Should().StartWith("partial: ").And.Contain("request bound of 50 reached");

        // The shipped settings in the live profile: one watched thread, two categories.
        var (shipped, quiet) = Create();
        quiet.Threads.Add(HotfixThread());
        await shipped.FetchAsync(Live, CancellationToken.None);
        quiet.Requests.Should().HaveCount(3, "a quiet round: the hotfix thread and one list page per category");
        new BlizzardForumOptions().WorstCaseRequestsPerRound.Should().Be(45);
    }

    // ---------- configuration ----------

    [Fact]
    public void The_shipped_configuration_is_the_released_games_profile_and_binds_as_the_bot_and_the_cli_bind_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "appsettings.json"))
            .Build();
        var settings = configuration.GetSection(WowForeverSettings.Section).Get<WowForeverSettings>()!;
        settings.Validate().Should().BeEmpty();
        (settings.ProviderGameId, string.Join(",", settings.EffectiveWatchedTopicIds)).Should().Be(("347,352", "2403040"));
        configuration.GetSection(UpdatesRecoveryOptions.Section).Get<UpdatesRecoveryOptions>().Should().BeNull("no recovery is shipped");

        // An environment variable overrides one element of the shipped list; it is never appended to a code default.
        var overridden = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Updates:WowForever:WatchedTopicIds:0"] = "2500000" })
            .Build().GetSection(WowForeverSettings.Section).Get<WowForeverSettings>()!;
        overridden.EffectiveWatchedTopicIds.Should().Equal(2500000);

        // The order of a configured list is no move of the source.
        new WowForeverSettings { ForumCategoryIds = [347, 352] }.ProviderGameId.Should().Be(new WowForeverSettings { ForumCategoryIds = [352, 347] }.ProviderGameId);
        BlizzardForumScope.Categories("347,352").Should().Equal(347, 352);
        BlizzardForumScope.Categories("352,347").Should().Equal(347, 352);
        BlizzardForumScope.Categories("347,347").Should().BeNull();
        BlizzardForumScope.Categories("1,2,3,4").Should().BeNull();
        BlizzardForumScope.Categories("").Should().BeNull();
        BlizzardForumScope.Categories("349").Should().Equal(349);
    }

    [Fact]
    public void A_recovery_setting_is_validated_before_anything_runs()
    {
        new UpdatesRecoveryOptions().Validate().Should().BeEmpty();
        new UpdatesRecoveryOptions { Game = "wow-forever" }.Validate().Should().BeEmpty();
        new UpdatesRecoveryOptions { Game = "wow-forever", Items = ["2403040:5", "2406595:1"], MaxCards = 3, Apply = true }.Validate().Should().BeEmpty();
        new UpdatesRecoveryOptions { Apply = true }.Validate().Should().ContainSingle("apply without a game");
        new UpdatesRecoveryOptions { Items = ["2403040:5"] }.Validate().Should().ContainSingle();
        new UpdatesRecoveryOptions { Game = "WoW Forever" }.Validate().Should().ContainSingle();
        new UpdatesRecoveryOptions { Game = "wow-forever", MaxCards = 4 }.Validate().Should().ContainSingle();
        new UpdatesRecoveryOptions { Game = "wow-forever", Items = ["2403040:5", "2403040:5"], MaxCards = 3 }.Validate().Should().ContainSingle();
        new UpdatesRecoveryOptions { Game = "wow-forever", Items = ["../etc"] }.Validate().Should().ContainSingle();
        new UpdatesRecoveryOptions { Game = "wow-forever", Items = Enumerable.Range(1, 11).Select(i => "1:" + i).ToArray(), MaxCards = 3 }.Validate().Should().HaveCount(2);
        new UpdatesRecoveryOptions { Game = "wow-forever", Apply = true }.Validate().Should().ContainSingle("apply never guesses: it needs the exact post ids")
            .Which.Should().Contain("Apply needs");
        new UpdatesRecoveryOptions { Game = "wow-forever", Items = ["2403040:5", "2406595:1"] }.Validate().Should().ContainSingle("two names, MaxCards 1: refused, not cut")
            .Which.Should().Contain("MaxCards is 1");
        new UpdatesRecoveryOptions().Apply.Should().BeFalse("apply is never on by default");
    }

    [Fact]
    public void Applied_and_planned_statements_are_read_sentence_by_sentence()
    {
        AppliedChange.Read("We deployed a fix.\nWe will adjust pets next week.").Should().Be(new AppliedChange.Statement(true, true));
        AppliedChange.Read("- We increased the drop rate\n- Pets will be adjusted").Applied.Should().BeTrue("a list line is a sentence of its own");
        AppliedChange.Read("We will have deployed a fix by Tuesday.").Should().Be(new AppliedChange.Statement(false, true));
        AppliedChange.Read("The bug was fixed.").Applied.Should().BeTrue();
        AppliedChange.Read("We’ve adjusted it.").Applied.Should().BeTrue("typographic apostrophes are folded");
        AppliedChange.Read("Weekly quests were great.").Applied.Should().BeFalse();
        AppliedChange.Read(null).Should().Be(new AppliedChange.Statement(false, false));
        AppliedChange.Read(new StringBuilder().Insert(0, "Nothing here. ", 2000).Append("We fixed it.").ToString()).Applied
            .Should().BeFalse("only a bounded number of sentences is read");
    }
}
