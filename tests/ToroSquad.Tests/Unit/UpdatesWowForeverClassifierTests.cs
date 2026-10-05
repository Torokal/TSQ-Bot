using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Domain.Games;
using ToroSquad.Modules.Updates.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The World of Warcraft: Forever classifier on a synthetic corpus. The titles are the kinds of thread titles Blizzard
/// posts in the game's forum category (development notes, client updates, known issues, maintenance, restarts, podcasts,
/// replies in player threads); every post body is made up. The scope (Blizzard post, Forever category) is the provider's
/// job — here only the kind of post is decided, and anything inconclusive must be Ambiguous, never Update.
/// </summary>
public sealed class UpdatesWowForeverClassifierTests
{
    private static readonly WowForeverUpdateClassifier Classifier = new();

    private static readonly string Notes = ForumHtmlSamples.BoldParagraphSections("Today we updated the beta.", ("Bug Fixes", ["Fixed one thing.", "Fixed another thing."]));

    private static GameUpdateCandidate Post(string title, string? cooked, params string[] labels)
    {
        var content = cooked is null ? null : ForumPostReader.Read(cooked);
        return new GameUpdateCandidate("blizzard", WowForeverGame.Key, "1:1", title, "https://us.forums.blizzard.com/en/wow/t/1/1", null, labels, content?.Text ?? "", content?.Highlights);
    }

    private static UpdateClassificationResult FirstPost(string title, string? cooked) => Classifier.Classify(Post(title, cooked, UpdateLabels.FirstPost));

    // ---------- updates and their kinds ----------

    [Theory]
    [InlineData("WoW Forever Beta Development Notes", "development_notes")]
    [InlineData("WoW Forever Beta Development Notes – Updated October 8", "development_notes")]
    [InlineData("Beta Client Update - September 22", "client_update")]
    [InlineData("WoW Forever Patch Notes", "patch_notes")]
    [InlineData("WoW: Forever 1.61 Update Notes", "patch_notes")]
    [InlineData("WoW Forever Hotfixes - October 9", "hotfix")]
    [InlineData("Hotfix: auction house", "hotfix")]
    [InlineData("wow forever BETA development   NOTES", "development_notes")]
    public void A_typed_title_backed_by_a_change_list_is_an_update_of_that_kind(string title, string kind)
    {
        var verdict = FirstPost(title, Notes);
        verdict.Classification.Should().Be(UpdateClassification.Update);
        verdict.Reason.Should().Be(kind);
    }

    [Fact]
    public void A_build_line_backs_a_typed_title_even_with_a_single_change()
    {
        FirstPost("Beta Client Update - October 9", ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70300", "One fix.")).Should()
            .Be(new UpdateClassificationResult(UpdateClassification.Update, "client_update"));
        FirstPost("Beta Client Update - October 9", "<p>A new client build is available: WoW Forever 1.60.2 Build 70300.</p>").Classification.Should().Be(UpdateClassification.Update);
    }

    [Fact]
    public void Development_notes_without_a_build_number_are_still_an_update()
    {
        var post = Post("WoW Forever Beta Development Notes", Notes, UpdateLabels.Reply, UpdateLabels.WatchedThread);
        post.Highlights!.Build.Should().BeNull();
        Classifier.Classify(post).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, "development_notes"));
    }

    [Fact]
    public void A_new_blizzard_post_in_the_watched_thread_counts_as_much_as_the_first_post()
    {
        Classifier.Classify(Post("WoW Forever Beta Development Notes", Notes, UpdateLabels.FirstPost, UpdateLabels.WatchedThread)).Classification.Should().Be(UpdateClassification.Update);
        Classifier.Classify(Post("WoW Forever Beta Development Notes", Notes, UpdateLabels.Reply, UpdateLabels.WatchedThread)).Classification.Should().Be(UpdateClassification.Update);
    }

    // ---------- not an update ----------

    [Theory]
    [InlineData("WoW Forever Beta Known Issues - October 8")]
    [InlineData("Beta Update Maintenance - October 1")]
    [InlineData("Beta Realm Maintenance is Underway - September 24")]
    [InlineData("Beta Realm Restarts Incoming - Sept. 25")]
    [InlineData("Beta Service Issue - October 1")]
    [InlineData("The WoW: Forever Podcast: Episode 2 - Patch Notes Special")]
    [InlineData("World of Warcraft: Forever Class Deep Dives — Priest and Warrior")]
    [InlineData("Hunter Class Preview")]
    [InlineData("WoW Weekly: The Future of Azeroth")]
    [InlineData("Feedback: Development Notes for October 1")]
    [InlineData("Bug Report: client update broke my addons")]
    [InlineData("WoW Forever Beta - Account Actions For Name Violations")]
    public void Known_issues_maintenance_marketing_and_feedback_titles_are_not_updates_whatever_the_text_says(string title)
    {
        foreach (var cooked in new[] { Notes, ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.2 Build 70300", "One fix.", "Two fixes."), null })
            FirstPost(title, cooked).Should().Be(new UpdateClassificationResult(UpdateClassification.NotUpdate, "excluded_topic"), title);
    }

    [Theory]
    [InlineData("Hunter damage is terrible")]
    [InlineData("Gnomeregan, Who broke it?!")]
    [InlineData("Warrior Updates in Today's Beta Build")]
    [InlineData("Legacy Points for Beta Testing")]
    [InlineData("Auto-shoot Bug and Fix Incoming")]
    [InlineData("Dialog Bug for Australian Testers -- fixed")]
    [InlineData("Updated client notes")]
    public void A_title_that_names_no_update_kind_is_not_an_update_whatever_the_text_says(string title)
    {
        foreach (var cooked in new[] { Notes, null })
            FirstPost(title, cooked).Should().Be(new UpdateClassificationResult(UpdateClassification.NotUpdate, "untyped_title"), title);
    }

    [Fact]
    public void A_not_update_decision_never_depends_on_the_text_so_such_posts_need_not_be_downloaded()
    {
        string[] titles =
        [
            "WoW Forever Beta Development Notes", "Beta Client Update - October 9", "WoW Forever Beta Known Issues - October 8", "Hunter damage is terrible",
            "Beta is Up - Development Notes Posted", "Midnight Hotfixes - October 9", "Beta Update Maintenance - October 1", "",
        ];
        foreach (var title in titles)
        {
            var byExcerpt = Classifier.Classify(Post(title, null, UpdateLabels.FirstPost, UpdateLabels.ExcerptOnly)).Classification;
            var inFull = FirstPost(title, Notes).Classification;
            (byExcerpt == UpdateClassification.NotUpdate).Should().Be(inFull == UpdateClassification.NotUpdate, title);
            byExcerpt.Should().NotBe(UpdateClassification.Update, "an excerpt is never enough for an update");
        }
    }

    // ---------- ambiguous ----------

    [Fact]
    public void An_update_like_title_over_a_pointer_or_a_remark_is_ambiguous()
    {
        FirstPost("Beta is Up - Development Notes Posted", "<p>The beta is back up. You can find the notes <a href=\"https://example.org\">here</a>.</p>").Should()
            .Be(new UpdateClassificationResult(UpdateClassification.Ambiguous, "no_change_list"));
        FirstPost("Beta Client Update - October 9", "<p>Coming soon.</p><ul><li>One line is a remark, not a change list.</li></ul>").Reason.Should().Be("no_change_list");
        FirstPost("WoW Forever Patch Notes", "").Reason.Should().Be("no_change_list");
        FirstPost("WoW Forever Patch Notes", null).Reason.Should().Be("no_change_list");
    }

    [Fact]
    public void A_blizzard_reply_outside_a_watched_thread_is_ambiguous_even_with_patch_notes()
    {
        Classifier.Classify(Post("Beta Client Update - September 22", Notes, UpdateLabels.Reply)).Should()
            .Be(new UpdateClassificationResult(UpdateClassification.Ambiguous, "reply_outside_watched_thread"));
        Classifier.Classify(Post("Beta Client Update - September 22", Notes)).Reason.Should().Be("reply_outside_watched_thread", "a post without its place is treated as a reply");
    }

    [Fact]
    public void A_typed_title_known_only_by_its_excerpt_is_ambiguous_until_the_text_is_loaded()
    {
        Classifier.Classify(Post("Beta Client Update - October 9", null, UpdateLabels.FirstPost, UpdateLabels.ExcerptOnly)).Should()
            .Be(new UpdateClassificationResult(UpdateClassification.Ambiguous, "content_not_loaded"));
    }

    [Theory]
    [InlineData("World of Warcraft: Midnight Hotfixes - October 1")]
    [InlineData("Season of Discovery Patch Notes")]
    [InlineData("Mists of Pandaria Classic Development Notes")]
    [InlineData("Cataclysm Classic Hotfixes")]
    [InlineData("Classic Era and Hardcore Client Update")]
    [InlineData("Retail Patch Notes")]
    public void A_title_that_names_another_wow_version_is_never_an_update(string title)
    {
        var verdict = FirstPost(title, Notes);
        verdict.Classification.Should().Be(UpdateClassification.Ambiguous);
        verdict.Reason.Should().Be("other_version_title");
    }

    [Fact]
    public void An_empty_title_is_ambiguous_and_the_classifier_is_deterministic()
    {
        FirstPost("  ", Notes).Should().Be(new UpdateClassificationResult(UpdateClassification.Ambiguous, "no_title"));
        foreach (var title in new[] { "WoW Forever Beta Development Notes", "Rush Hour", "Beta Client Update - October 9", "Known Issues" })
        {
            var first = FirstPost(title, Notes);
            FirstPost(title, Notes).Should().Be(first);
            first.Reason.Should().MatchRegex("^[a-z_]{3,48}$");
        }
    }

    // ---------- the game ----------

    [Fact]
    public void The_game_is_defined_by_settings_not_by_code()
    {
        var game = WowForeverGame.Create(new WowForeverSettings());
        (game.Key, game.DisplayName, game.ShortName, game.Provider, game.ProviderGameId).Should().Be(("wow-forever", "World of Warcraft: Forever", "WoW: Forever", "blizzard", "349"));
        game.WatchedThreadIds.Should().Equal("2360696");
        game.Classifier.Should().BeOfType<WowForeverUpdateClassifier>();
        GameUpdateDefinition.IsValidKey(game.Key).Should().BeTrue();
        Cs2Game.Definition.WatchedThreadIds.Should().BeEmpty("Counter-Strike 2 is unchanged");

        var moved = WowForeverGame.Create(new WowForeverSettings { ForumCategoryId = 400, WatchedTopicIds = [11, 22] });
        (moved.ProviderGameId, string.Join(",", moved.WatchedThreadIds)).Should().Be(("400", "11,22"));
        WowForeverGame.Create(new WowForeverSettings { WatchedTopicIds = [] }).WatchedThreadIds.Should().BeEmpty();

        new WowForeverSettings().Validate().Should().BeEmpty();
        new WowForeverSettings { ForumCategoryId = 0 }.Validate().Should().ContainSingle();
        new WowForeverSettings { WatchedTopicIds = [5, 5] }.Validate().Should().ContainSingle();
        new WowForeverSettings { WatchedTopicIds = [1, 2, 3, 4, 5, 6] }.Validate().Should().ContainSingle();
        new WowForeverSettings { WatchedTopicIds = [-1] }.Validate().Should().ContainSingle();
    }
}
