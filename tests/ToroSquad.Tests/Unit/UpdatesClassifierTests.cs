using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Domain.Games;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The Counter-Strike 2 update classifier on a synthetic corpus. The titles are the kinds of titles the official
/// announcement feed carries (patches, events, item releases, workshop posts); every text body is made up.
/// A wrong card is worse than a missed one, so everything inconclusive must come out as Ambiguous, never as Update.
/// </summary>
public sealed class UpdatesClassifierTests
{
    private static readonly Cs2UpdateClassifier Classifier = new();

    private static UpdateClassificationResult Classify(string title, string? body = null, params string[] labels) =>
        Classifier.Classify(new GameUpdateCandidate("steam", "cs2", "1", title, SteamNews.CardUrl("1"), null, labels, body ?? SteamNews.Prose));

    // ---------- positive ----------

    [Theory]
    [InlineData("Counter-Strike 2 Update")]
    [InlineData("counter-strike 2 update")]
    [InlineData("  Counter-Strike   2   Update  ")]
    [InlineData("Counter‑Strike 2 Update")] // non-breaking hyphen
    [InlineData("Counter–Strike 2 Update")] // en dash
    [InlineData("Ｃounter-Strike 2 Update")] // full-width letter (compatibility form)
    [InlineData("Counter-Strike 2 Pre-Release Update")]
    public void The_exact_patch_titles_are_an_update_on_their_own(string title)
    {
        var verdict = Classify(title);
        verdict.Classification.Should().Be(UpdateClassification.Update);
        verdict.Reason.Should().Be("strong_title");
    }

    [Fact]
    public void A_strong_title_needs_neither_the_tag_nor_readable_text()
    {
        Classify("Counter-Strike 2 Update", "").Classification.Should().Be(UpdateClassification.Update);
        Classify("Counter-Strike 2 Update", "[p]\\[ MI").Classification.Should().Be(UpdateClassification.Update, "a cut-off text does not matter here");
    }

    [Theory]
    [InlineData("Release Notes for 10/2/2026", "tagged_update_title")]
    [InlineData("Patch Notes — October", "tagged_update_title")]
    [InlineData("Animation Beta Update", "tagged_update_title")]
    public void An_update_like_title_with_the_patchnotes_tag_is_an_update(string title, string reason)
    {
        var verdict = Classify(title, SteamNews.Prose, "patchnotes");
        verdict.Classification.Should().Be(UpdateClassification.Update);
        verdict.Reason.Should().Be(reason);
    }

    [Fact]
    public void An_update_like_title_with_patch_notes_text_is_an_update_without_the_tag()
    {
        var verdict = Classify("Release Notes for 10/2/2026", SteamNews.PatchNotes);
        verdict.Classification.Should().Be(UpdateClassification.Update);
        verdict.Reason.Should().Be("update_title_with_patch_notes");
    }

    [Fact]
    public void The_tag_together_with_patch_notes_text_is_an_update_under_any_neutral_title()
    {
        var verdict = Classify("Season Six", SteamNews.PatchNotes, "PatchNotes", "mod_reviewed");
        verdict.Classification.Should().Be(UpdateClassification.Update);
        verdict.Reason.Should().Be("tagged_patch_notes");
    }

    // ---------- negative ----------

    [Theory]
    [InlineData("The Budapest Major Playoffs")]
    [InlineData("Champions of Shanghai")]
    [InlineData("The Grand Finals")]
    [InlineData("The Jackal Sticker Capsule")]
    [InlineData("Autumn Sale")]
    [InlineData("New Merch Drop")]
    [InlineData("2027 Service Medal")]
    public void Events_tournaments_and_item_releases_are_not_updates(string title)
    {
        var verdict = Classify(title);
        verdict.Classification.Should().Be(UpdateClassification.NotUpdate);
        verdict.Reason.Should().Be("event_or_marketing_title");
    }

    [Theory]
    [InlineData("Rush Hour")]
    [InlineData("Introducing a new mode")]
    [InlineData("Season's Greetings")]
    public void A_plain_announcement_is_not_an_update(string title)
    {
        var verdict = Classify(title);
        verdict.Classification.Should().Be(UpdateClassification.NotUpdate);
        verdict.Reason.Should().Be("no_update_signal");
    }

    [Theory]
    [InlineData("An update on the schedule")]
    [InlineData("Update: what we are working on")]
    [InlineData("Updates to matchmaking are coming")]
    public void The_word_update_inside_a_title_is_not_a_signal(string title)
    {
        var verdict = Classify(title, "We have an update for you. This update is great. Update now!");
        verdict.Classification.Should().Be(UpdateClassification.NotUpdate);
        verdict.Reason.Should().BeOneOf("update_word_only", "no_update_signal");
    }

    // ---------- ambiguous ----------

    [Fact]
    public void An_update_like_title_alone_is_ambiguous()
    {
        var verdict = Classify("Counter-Strike 2 Beta Update", "Short marketing prose.");
        verdict.Classification.Should().Be(UpdateClassification.Ambiguous);
        verdict.Reason.Should().Be("unsupported_update_title");
    }

    [Fact]
    public void The_tag_alone_is_ambiguous()
    {
        // Observed in the real feed: a workshop call for submissions carried the patchnotes tag.
        var verdict = Classify("Call for Submissions", "We are looking for new items.\n\n - Collection one\n - Collection two", "patchnotes", "workshop");
        verdict.Classification.Should().Be(UpdateClassification.Ambiguous);
        verdict.Reason.Should().Be("tagged_without_patch_notes");
    }

    [Fact]
    public void Patch_notes_text_alone_is_ambiguous()
    {
        var verdict = Classify("Something new", SteamNews.PatchNotes);
        verdict.Classification.Should().Be(UpdateClassification.Ambiguous);
        verdict.Reason.Should().Be("patch_notes_shape_only");
    }

    [Theory]
    [InlineData("CS2 Workshop Update", false, false)] // observed real title: not a patch
    [InlineData("Major Update", true, true)]
    [InlineData("Sticker Capsule Release Notes", true, false)]
    [InlineData("Viewer Pass Update", true, false)]
    [InlineData("Autumn Case Update", true, true)]
    [InlineData("New Collection and Charms Update", true, false)]
    [InlineData("Music Kits Update", true, false)]
    public void An_event_or_item_title_with_update_signals_is_ambiguous_never_an_update(string title, bool tagged, bool patchNotes)
    {
        var verdict = Classify(title, patchNotes ? SteamNews.PatchNotes : SteamNews.Prose, tagged ? ["patchnotes"] : []);
        verdict.Classification.Should().Be(UpdateClassification.Ambiguous);
        verdict.Reason.Should().Be("conflicting_signals");
    }

    [Fact]
    public void A_cut_off_text_cannot_supply_the_second_signal()
    {
        // The section header survived the cut, the list did not: no patch-notes shape, so the title stays unsupported.
        Classify("Release Notes for 10/2/2026", "[p]\\[ MAPS ][/p][li").Classification.Should().Be(UpdateClassification.Ambiguous);
        Classify("Release Notes for 10/2/2026", "").Classification.Should().Be(UpdateClassification.Ambiguous);
    }

    [Fact]
    public void An_empty_title_is_ambiguous()
    {
        Classify("   ", SteamNews.PatchNotes, "patchnotes").Should().Be(new UpdateClassificationResult(UpdateClassification.Ambiguous, "no_title"));
    }

    // ---------- the patch-notes shape ----------

    [Theory]
    [InlineData("[p]\\[ MISC ][/p][list][*][p]x[/p][/*][/list]", true)]
    [InlineData("[ GAMEPLAY ]\n- one change\n- another", true)]
    [InlineData("[ MAPS & MODES ]\n• one change", true)]
    [InlineData("[p]\\[ MISC ][/p][p]no list at all[/p]", false)]
    [InlineData("[list][*][p]a list without a section[/p][/*][/list]", false)]
    [InlineData("[b]Bold[/b] and [url=https://example.org]a link[/url]\n- item", false)] // markup tags are not sections
    [InlineData("See [this] and [that]\n- item", false)]
    [InlineData("[MISC]\n- item", false)] // Valve pads the name with spaces
    [InlineData("[ misc ]\n- item", false)]
    [InlineData("", false)]
    public void The_patch_notes_shape_needs_a_capital_section_header_and_a_list(string body, bool expected) =>
        Cs2UpdateClassifier.HasPatchNotesShape(body).Should().Be(expected);

    [Fact]
    public void Markup_in_the_text_never_changes_a_decision_by_itself()
    {
        Classify("Rush Hour", "<b>UPDATE</b> [video webm=\"https://example.org/a.webm\"][/video] [img]https://example.org/a.png[/img] update update")
            .Classification.Should().Be(UpdateClassification.NotUpdate);
    }

    [Fact]
    public void The_classifier_is_deterministic_and_the_reason_is_a_short_code()
    {
        string[] titles = ["Counter-Strike 2 Update", "Rush Hour", "CS2 Workshop Update", "Release Notes for 1/1/2027", "The Major", ""];
        foreach (var title in titles)
        {
            var first = Classify(title, SteamNews.PatchNotes, "patchnotes");
            Classify(title, SteamNews.PatchNotes, "patchnotes").Should().Be(first);
            first.Reason.Should().MatchRegex("^[a-z_]{3,48}$");
        }
    }

    [Fact]
    public void A_post_never_prints_its_text()
    {
        var post = new GameUpdateCandidate("steam", "cs2", "42", "Counter-Strike 2 Update", SteamNews.CardUrl("42"), null, ["patchnotes"], "secret body text");
        post.ToString().Should().Be("steam:cs2:42");
        $"{post}".Should().NotContain("secret");
    }

    // ---------- shared text helpers ----------

    [Theory]
    [InlineData("Counter-Strike 2 Update", "update", true)]
    [InlineData("Counter-Strike 2 Updates", "update", false)]
    [InlineData("Pre-update notes", "update", true)]
    [InlineData("The Majority", "major", false)]
    [InlineData("the major!", "major", true)]
    public void Whole_word_matching(string text, string word, bool expected) =>
        UpdateText.ContainsWord(UpdateText.Normalize(text), word).Should().Be(expected);

    [Fact]
    public void The_registered_game_is_cs2_on_steam_app_730()
    {
        var game = Cs2Game.Definition;
        game.Key.Should().Be("cs2");
        game.DisplayName.Should().Be("Counter-Strike 2");
        game.ShortName.Should().Be("CS2");
        game.Provider.Should().Be("steam");
        game.ProviderGameId.Should().Be("730");
        game.Classifier.Should().BeOfType<Cs2UpdateClassifier>();
        GameUpdateDefinition.IsValidKey(game.Key).Should().BeTrue();
        GameUpdateDefinition.IsValidKey("CS2").Should().BeFalse();
        GameUpdateDefinition.IsValidKey("a:b").Should().BeFalse("the key is part of the outbox kind");
        GameUpdateDefinition.IsValidKey(new string('a', 17)).Should().BeFalse();
    }
}
