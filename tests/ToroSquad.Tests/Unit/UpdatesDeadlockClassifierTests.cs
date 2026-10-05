using System.Text;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Domain.Games;
using ToroSquad.Modules.Updates.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Deadlock on Steam: which official announcements are game updates, how Valve's patch-notes layout is read, and what a
/// card may show. The titles are the kinds of titles the official feed carries; every text body is synthetic, in the
/// shapes observed there. A wrong card is worse than a missed one: everything inconclusive is Ambiguous, never Update.
/// </summary>
public sealed class UpdatesDeadlockClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeadlockUpdateClassifier Classifier = new();

    private const string OldGods =
        "[img]{STEAM_CLAN_IMAGE}/1/synthetic.png[/img]\n\nThe ritual takes form... Synthetic patrons, six new heroes, HUD updates and more.\n\n" +
        "View the [url=https://www.playdeadlock.com/oldgods]Old Gods, New Blood update page[/url].";

    private static UpdateClassificationResult Classify(string title, string body, bool tagged = false) =>
        Classifier.Classify(new GameUpdateCandidate("steam", DeadlockGame.Key, "1", title, SteamNews.CardUrl("1"), null, tagged ? DeadlockNews.PatchTag : [], body));

    // ---------- updates ----------

    public static TheoryData<string, string, bool, string> Updates => new()
    {
        { "Minor Update - 09-16-2026", DeadlockNews.MinorNotes, true, "patch_notes" },
        { "Minor Update - 07-01-2026", "[p]- Hero A: one[/p][p]- Hero A: two[/p][p]- Hero A: three[/p]", true, "patch_notes" },
        { "10-24-2024 Update", "Today's update adds a synthetic mode. For the full patch notes, visit the forums.", true, "patch_notes" },
        { "Gameplay Update - 04-30-2026", "[p][b][ General ][/b][/p][p][/p][p]- One[/p][p]- Two[/p][p]- Three[/p]", false, "titled_update" },
        { "Gameplay Update - 03-06-2026", "[p][b][u]\\[ General ][/u][/b][/p][p][/p][p]- One[/p][p]- Two[/p][p]- Three[/p]", false, "titled_update" },
        { "12-06-2024 Update", DeadlockNews.PlainNotes(DeadlockNews.SmallPatch), false, "titled_update" },
        { "Shop Rework Update", "[img]{STEAM_CLAN_IMAGE}/1/synthetic.png[/img]\n[ Shop Redesign ]\n- One\n- Two\n- Three", false, "titled_update" },
        { "Matchmaking Update", "This update includes a synthetic revamp of matchmaking.\n\n[b]STANDARD MODE[/b]\nA synthetic paragraph.", false, "titled_update" },
        { "Winter Visual Update", "Season's Greetings! Today's winter-themed visual update will be our last patch for the year.", false, "titled_update" },
        { "City Never Sleeps", DeadlockNews.Named("cityneversleeps"), false, "named_update" },
        { "Old Gods, New Blood", OldGods, false, "named_update" },
        { "A Synthetic Season", "Today's update brings a synthetic season. Read more: https://www.playdeadlock.com/season-two", false, "named_update" },
        // A real update's letter also talks about what is still to come: that does not make it an announcement.
        { "City Never Sleeps", DeadlockNews.Named("cityneversleeps") + "\n\nOther things are more speculative: we are not sure if they will be released or not.", false, "named_update" },
        { "Six New Heroes", DeadlockNews.SystemsRollout, false, "substantial_update" },
        { "Holliday, Vyper, Calico, and The Magnificent Sinclair", DeadlockNews.RosterDrop, false, "declared_update" },
    };

    [Theory]
    [MemberData(nameof(Updates))]
    public void Real_kinds_of_updates_are_updates_whatever_their_title_says(string title, string body, bool tagged, string reason) =>
        Classify(title, body, tagged).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, reason));

    [Fact]
    public void A_named_major_update_needs_neither_the_word_update_in_its_title_nor_the_tag_nor_a_change_list()
    {
        var verdict = Classify("City Never Sleeps", DeadlockNews.Named("cityneversleeps"));
        verdict.Classification.Should().Be(UpdateClassification.Update);
        UpdateText.ContainsWord(UpdateText.Normalize("City Never Sleeps"), "update").Should().BeFalse();
        DeadlockNotesReader.Read(DeadlockNews.Named("cityneversleeps")).ChangeCount.Should().Be(0);
    }

    // ---------- not updates ----------

    public static TheoryData<string, string> HeroReveals => new()
    {
        { "Listen up, Crumbums! Your King is here.", DeadlockNews.HeroReveal },
        { "Introducing The Dazzling Celeste",
            "[h3]Celeste[/h3]\n[img]{STEAM_CLAN_IMAGE}/1/synthetic.png[/img]\n\nA synthetic star performs.\n\nJoin us on Thursday for the conclusion of our " +
            "[url=https://www.playdeadlock.com/oldgods]Old Gods, New Blood[/url] update." },
        { "Rem Enters The City That Never Sleeps",
            "The people have spoken, and the first new addition from the Old Gods, New Blood update is...\n\n[h3]Rem[/h3]\nA synthetic stowaway. " +
            "[url=https://www.playdeadlock.com/oldgods]See the page[/url]" },
        { "Six Synthetic Faces", "Six new heroes have been spotted on the streets. We will be staggering their release, unlocking one every other day." },
        { "City Never Sleeps: Meet The Synthetic Hero", DeadlockNews.HeroReveal },
        // Pure spotlights in the feed's shapes: the hero's name as a heading, lore, "available to play now", a link to the update's page.
        { "Apollo - A Cut Above", DeadlockNews.Spotlight("Apollo") },
        { "Listen up, Crumbums! Your King is here.", DeadlockNews.Spotlight("The Rat King") },
        { "Introducing The Dazzling Celeste", DeadlockNews.Spotlight("Celeste") },
        // Several heroes introduced at once, each under a heading, but nothing says the game changed.
        { "Meet The Synthetic Four",
            "[h3]HERO A[/h3]\nA synthetic gunslinger.\n[h3]HERO B[/h3]\nA synthetic assassin.\n[h3]HERO C[/h3]\nA synthetic magician.\n[h3]HERO D[/h3]\nA synthetic sphinx." },
        // Named like an update, but no page of the site, no declaration and no change: marketing.
        { "Shadows Over Manhattan", "[img]{STEAM_CLAN_IMAGE}/1/synthetic.png[/img]\n\nSomething stirs beneath the streets. The city holds its breath. Watch the trailer." },
    };

    [Theory]
    [MemberData(nameof(HeroReveals))]
    public void Hero_reveals_and_other_announcements_are_not_updates(string title, string body) =>
        Classify(title, body).Should().Be(new UpdateClassificationResult(UpdateClassification.NotUpdate, "no_update_signal"));

    public static TheoryData<string, string, bool, string> Inconclusive => new()
    {
        // A hero post with a list of vote counts: a list, but nothing says it is a patch.
        { "Apollo - A Cut Above", "[h3]Apollo[/h3]\nA synthetic fencer.\n\n[b]Voting Stats:[/b]\n[list]\n[*] 100 votes\n[*] 200 votes\n[*] 300 votes\n[/list]", false, "list_only" },
        // "this update" without saying what it does, and nothing else.
        { "A Synthetic Hero Arrives", "A synthetic paragraph about the hero. We hope you enjoy this update.", false, "declared_without_changes" },
        // One sentence that states a change — or several without any section — is a hint, not an update.
        { "A Synthetic Hero Arrives", DeadlockNews.Spotlight("Hero") + " The map has been updated for the occasion.", false, "change_statements_only" },
        { "A Letter To Players", "The map has been updated. The shop has received a new look. Stamina is now shared. A synthetic closing line.", false, "change_statements_only" },
        // Named after its page and speaking of an update — but it is only announced.
        { "Shadows Over Manhattan", DeadlockNews.Named("shadows-over-manhattan", "A synthetic teaser: our next update is coming soon."), false, "named_but_upcoming" },
        { "An Update On Our Plans", "A synthetic paragraph about the future of the game.", false, "update_title_only" },
        { "Gameplay Update - 01-01-2027", "[p]- One[/p][p]- Two[/p]", false, "update_title_only" },
        { "A Synthetic Notice", "A synthetic paragraph.", true, "tagged_without_changes" },
        { "   ", DeadlockNews.MinorNotes, true, "no_title" },
    };

    [Theory]
    [MemberData(nameof(Inconclusive))]
    public void A_single_signal_is_ambiguous_and_never_an_update(string title, string body, bool tagged, string reason) =>
        Classify(title, body, tagged).Should().Be(new UpdateClassificationResult(UpdateClassification.Ambiguous, reason));

    [Theory]
    [InlineData("https://evil.example/cityneversleeps")]
    [InlineData("https://www.playdeadlock.com.evil.example/cityneversleeps")]
    [InlineData("http://www.playdeadlock.com/cityneversleeps")]
    [InlineData("https://user@www.playdeadlock.com/cityneversleeps")]
    [InlineData("https://www.playdeadlock.com:8443/cityneversleeps")]
    [InlineData("https://forums.playdeadlock.com/cityneversleeps")]
    public void Only_a_page_of_the_games_own_site_makes_a_post_a_named_update(string address)
    {
        var body = $"A synthetic summary of an update.\n\n[url={address}]{address}[/url]";
        DeadlockNotesReader.Read(body).Pages.Should().BeEmpty();
        Classify("City Never Sleeps", body).Classification.Should().NotBe(UpdateClassification.Update);
    }

    [Fact]
    public void The_word_update_inside_another_word_or_only_in_the_text_is_not_a_signal()
    {
        Classify("Updated Hero Art", "A synthetic paragraph.").Classification.Should().Be(UpdateClassification.NotUpdate);
        Classify("A Synthetic Hero", "This hero arrives with the big update we released last week.").Classification.Should().Be(UpdateClassification.NotUpdate,
            "\"the big update\" is not the post speaking of itself");
    }

    [Fact]
    public void The_classifier_is_pure_and_survives_broken_or_cut_off_text()
    {
        var cut = DeadlockNews.MinorNotes[..(DeadlockNews.MinorNotes.Length / 2)] + "[p][b]\\[ Her";
        Classify("Minor Update - 09-16-2026", cut, tagged: true).Classification.Should().Be(UpdateClassification.Update);
        foreach (var broken in new[] { "", "[", "[[[[[[", "\\", "[p", "[url=", "[img]never closed", "[*]", new string('[', 5000), "]]]]", "[/p][/p]\n\n\n" })
        {
            var act = () => Classify("Minor Update", broken);
            act.Should().NotThrow();
            Classify("Minor Update", broken).Should().Be(Classify("Minor Update", broken));
        }
    }

    // ---------- reading the notes ----------

    [Fact]
    public void Both_layouts_give_the_same_sections_and_lines()
    {
        var newer = DeadlockNotesReader.Read(DeadlockNews.Notes(DeadlockNews.SmallPatch));
        var older = DeadlockNotesReader.Read(DeadlockNews.PlainNotes(DeadlockNews.SmallPatch));
        newer.ChangeCount.Should().Be(4);
        newer.Sections.Select(s => s.Heading).Should().Equal("General", "Heroes");
        newer.Sections[0].Items.Should().Equal("Synthetic change one", "Synthetic change two");
        newer.Sections[1].Items[0].Should().Be("Hero A: Synthetic ability damage increased from 10 to 12");
        older.Sections.Should().BeEquivalentTo(newer.Sections, o => o.WithStrictOrdering());
        older.ChangeCount.Should().Be(newer.ChangeCount);
    }

    [Fact]
    public void Lines_before_the_first_section_list_items_images_and_links_are_read_as_what_they_are()
    {
        var notes = DeadlockNotesReader.Read(
            "[img]{STEAM_CLAN_IMAGE}/1/secret-address.png[/img]\nToday's update introduces a synthetic map. See [url=https://www.playdeadlock.com/map-rework]the page[/url] " +
            "and https://www.playdeadlock.com/second-page, not [url=https://evil.example/x]this[/url].\n- A loose change\n[ Map Rework ]\n[list]\n[*] First item\n[*] Second [b]bold[/b] item\n[/list]");
        notes.Sections.Select(s => (s.Heading, s.Items.Count)).Should().Equal((null, 1), ("Map Rework", 2));
        notes.Sections[1].Items.Should().Equal("First item", "Second bold item");
        notes.ChangeCount.Should().Be(3);
        notes.Pages.Should().Equal("map-rework", "second-page");
        notes.Summary.Should().StartWith("Today's update introduces a synthetic map.");
        notes.Words.Should().Contain(["today's", "update"]).And.NotContain(w => w.Contains("secret-address"), "an image address is not text");
    }

    [Fact]
    public void A_section_keeps_its_first_lines_while_every_line_is_counted_and_the_input_is_bounded()
    {
        var many = DeadlockNews.Notes(("Heroes", Enumerable.Range(1, 500).Select(n => "Hero: synthetic change " + n).ToArray()));
        var notes = DeadlockNotesReader.Read(many);
        (notes.ChangeCount, notes.Sections.Single().Items.Count).Should().Be((500, UpdateHighlights.MaxItemsPerSection));

        var endless = new StringBuilder();
        while (endless.Length < DeadlockNotesReader.MaxInputLength + 50_000)
            endless.Append("- a synthetic line\n");
        var bounded = DeadlockNotesReader.Read(endless.ToString());
        bounded.ChangeCount.Should().BeLessThanOrEqualTo(DeadlockNotesReader.MaxInputLength / "- a synthetic line\n".Length + 1, "nothing beyond the bound is read");

        var sections = string.Concat(Enumerable.Range(1, 300).Select(n => $"[ Section {n} ]\n- one\n"));
        DeadlockNotesReader.Read(sections).Sections.Count.Should().Be(DeadlockNotesReader.MaxSections);
        DeadlockNotesReader.Read(new string('x', 10_000)).Words[0].Length.Should().Be(DeadlockNotesReader.MaxLineLength);
    }

    [Fact]
    public void Headings_are_counted_and_sentences_are_kept_apart()
    {
        var notes = DeadlockNotesReader.Read(DeadlockNews.SystemsRollout);
        (notes.HeadingCount, notes.ChangeCount, notes.Sections.Count).Should().Be((4, 0, 0), "headings count with or without change lines under them");
        notes.Words.Should().ContainInOrder("replaces", "the", "existing").And.Contain(DeadlockNotesReader.SentenceEnd);
        DeadlockNotesReader.Read(DeadlockNews.MinorNotes).HeadingCount.Should().Be(2);

        // A declaration never reaches across the end of a sentence, and a number is not one.
        Classify("A Synthetic Hero", "Thank you for this. Update your drivers to 1.25 or later. Adds nothing.").Classification.Should().Be(UpdateClassification.NotUpdate);
        Classify("A Synthetic Hero", "The cooldown is 0.25s now. Has the hero been updated? No.").Classification.Should().Be(UpdateClassification.NotUpdate);
    }

    // ---------- the bounded copy of a long post ----------

    [Theory]
    [InlineData("City Never Sleeps", "named_update")]
    [InlineData("Six New Heroes", "substantial_update")]
    [InlineData("Old Gods, New Blood", "named_update")]
    public void A_named_update_is_decided_by_the_beginning_of_the_post_whatever_follows(string title, string reason)
    {
        var start = title switch
        {
            "City Never Sleeps" => DeadlockNews.Named("cityneversleeps"),
            "Six New Heroes" => DeadlockNews.SystemsRollout,
            _ => OldGods,
        };
        var letter = string.Concat(Enumerable.Range(1, 600).Select(n => $"\n\nA synthetic paragraph number {n} of a very long developer letter about the future of the game."));
        var whole = start + letter;
        whole.Length.Should().BeGreaterThan(GameUpdateCandidate.BodyMax * 2);
        var post = SteamNewsParser.Parse(SteamNews.Bytes(SteamNews.Json([DeadlockNews.Post(7, title, Now.AddHours(-1), whole)], DeadlockNews.AppId)), DeadlockGame.Definition, Now)
            .Items.Should().ContainSingle().Subject;
        post.Body.Length.Should().Be(GameUpdateCandidate.BodyMax);
        Classifier.Classify(post).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, reason));
        Classifier.Classify(post).Should().Be(Classify(title, start), "the same verdict as for the short post");
    }

    [Fact]
    public void Signals_that_only_stand_beyond_the_bounded_copy_are_not_seen_and_the_verdict_stays_the_same_every_time()
    {
        var filler = string.Concat(Enumerable.Range(1, 400).Select(n => $"A synthetic paragraph number {n} with nothing to say about the game.\n\n"));
        filler.Length.Should().BeGreaterThan(GameUpdateCandidate.BodyMax);
        var late = filler + DeadlockNews.Named("cityneversleeps");
        var post = SteamNewsParser.Parse(SteamNews.Bytes(SteamNews.Json([DeadlockNews.Post(8, "City Never Sleeps", Now.AddHours(-1), late)], DeadlockNews.AppId)), DeadlockGame.Definition, Now)
            .Items.Single();
        var verdict = Classifier.Classify(post);
        verdict.Classification.Should().NotBe(UpdateClassification.Update, "the classifier only reads the bounded beginning of a post: never a guess about the rest");
        Classifier.Classify(post).Should().Be(verdict);
        // A cut in the middle of a tag or a word changes nothing about that.
        foreach (var cut in new[] { 1, 7, 33, 120 })
            Classifier.Classify(post with { Body = post.Body[..^cut] }).Should().Be(verdict);
    }

    // ---------- the card excerpt ----------

    [Fact]
    public void The_excerpt_is_the_first_lines_per_section_or_the_first_sentence_of_an_announcement()
    {
        var highlighter = new DeadlockHighlighter();
        var patch = highlighter.Read("Minor Update", DeadlockNews.MinorNotes)!;
        (patch.Version, patch.Build, patch.ChangeCount).Should().Be((null, null, 4));
        patch.Sections.Select(s => s.Heading).Should().Equal("General", "Heroes");

        var named = highlighter.Read("City Never Sleeps", DeadlockNews.Named("cityneversleeps"))!;
        named.ChangeCount.Should().Be(0);
        named.Sections.Should().ContainSingle().Which.Should().BeEquivalentTo(new UpdateSection(null,
            ["A synthetic summary: a large visual update to the map, new heroes, HUD updates and more."]));

        highlighter.Read("An image", "[img]{STEAM_CLAN_IMAGE}/1/synthetic.png[/img]").Should().BeNull();
        highlighter.Read("Nothing", "").Should().BeNull();
    }

    // ---------- through the Steam parser ----------

    [Fact]
    public void A_deadlock_post_gets_its_excerpt_from_the_whole_text_and_a_counter_strike_post_gets_none()
    {
        var lines = Enumerable.Range(1, 1500).Select(n => "Hero: synthetic change number " + n).ToArray();
        var huge = DeadlockNews.Notes(("General", lines[..700]), ("Heroes", lines[700..]));
        huge.Length.Should().BeGreaterThan(GameUpdateCandidate.BodyMax);
        var deadlock = SteamNewsParser.Parse(SteamNews.Bytes(SteamNews.Json([DeadlockNews.Minor(5001, Now.AddHours(-1), huge)], DeadlockNews.AppId)), DeadlockGame.Definition, Now);
        var post = deadlock.Items.Should().ContainSingle().Subject;
        (post.Provider, post.GameKey, post.ExternalId, post.CanonicalUrl).Should().Be(("steam", "deadlock", "5001", SteamNews.CardUrl("5001")));
        post.Body.Length.Should().Be(GameUpdateCandidate.BodyMax, "the classifier's copy stays bounded");
        post.Highlights!.ChangeCount.Should().Be(1500, "the number of changes is the post's, not the bounded copy's");
        post.Highlights.Sections.Select(s => s.Heading).Should().Equal("General", "Heroes");
        Classifier.Classify(post).Should().Be(new UpdateClassificationResult(UpdateClassification.Update, "patch_notes"));

        var cs2 = SteamNewsParser.Parse(SteamNews.Bytes(SteamNews.Json([SteamNews.Update(9, Now.AddHours(-1))])), Cs2Game.Definition, Now);
        cs2.Items.Should().ContainSingle().Which.Highlights.Should().BeNull("Counter-Strike 2 names no reader: its card stays title and link");
    }

    [Fact]
    public void The_content_hash_covers_what_the_card_shows_and_is_unchanged_for_a_post_without_an_excerpt()
    {
        // A post without an excerpt (every Counter-Strike 2 post) hashes exactly as it always did.
        var cs2 = SteamNewsParser.Parse(SteamNews.Bytes(SteamNews.Json([SteamNews.Update(9, Now.AddHours(-1))])), Cs2Game.Definition, Now).Items.Single();
        var legacy = string.Join('\u001F', cs2.Title, cs2.CanonicalUrl, cs2.PublishedAt!.Value.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(',', cs2.Labels.Order(StringComparer.Ordinal)), cs2.Body);
        cs2.ContentHash.Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(legacy)))[..32]);

        // A change far beyond the bounded copy of the text changes the number of changes on the card — and the hash.
        GameUpdateCandidate Long(int lines) =>
            SteamNewsParser.Parse(SteamNews.Bytes(SteamNews.Json([DeadlockNews.Minor(5, Now.AddHours(-1),
                DeadlockNews.Notes(("General", Enumerable.Range(1, lines).Select(n => "Hero: synthetic change number " + n).ToArray())))], DeadlockNews.AppId)),
                DeadlockGame.Definition, Now).Items.Single();
        var (before, after) = (Long(1500), Long(1501));
        after.Body.Should().Be(before.Body, "the bounded copy is the same");
        (before.Highlights!.ChangeCount, after.Highlights!.ChangeCount).Should().Be((1500, 1501));
        after.ContentHash.Should().NotBe(before.ContentHash);
        Long(1500).ContentHash.Should().Be(before.ContentHash, "the same post gives the same hash");

        // The excerpt alone decides nothing else: same text and excerpt, same hash; another heading, another hash.
        var a = UpdateHighlights.Create("1.0", "100", 2, [new UpdateSection("Fixes", ["One", "Two"])]);
        var b = UpdateHighlights.Create("1.0", "100", 2, [new UpdateSection("Fixes", ["One", "Two"])]);
        var c = UpdateHighlights.Create("1.0", "100", 2, [new UpdateSection("Changes", ["One", "Two"])]);
        (a.Fingerprint == b.Fingerprint, a.Fingerprint == c.Fingerprint).Should().Be((true, false));
        (cs2 with { Highlights = a }).ContentHash.Should().Be((cs2 with { Highlights = b }).ContentHash).And.NotBe((cs2 with { Highlights = c }).ContentHash).And.NotBe(cs2.ContentHash);
    }

    [Fact]
    public void An_answer_for_another_app_is_never_a_deadlock_answer()
    {
        SteamNewsParser.Parse(SteamNews.Bytes(SteamNews.Json([SteamNews.Update(9, Now.AddHours(-1))])), DeadlockGame.Definition, Now).Outcome
            .Should().Be(SteamParseOutcome.UnexpectedSchema, "the answer is for AppID 730");
        var foreign = SteamNews.Json([DeadlockNews.Minor(1, Now.AddHours(-2)), SteamNews.Update(2, Now.AddHours(-1))], DeadlockNews.AppId);
        var parsed = SteamNewsParser.Parse(SteamNews.Bytes(foreign), DeadlockGame.Definition, Now);
        (parsed.Items.Select(i => i.ExternalId).Single(), parsed.Skipped).Should().Be(("1", 1), "a post that names another app is skipped");
    }

    [Fact]
    public void The_definition_is_the_official_steam_app_with_its_own_classifier_and_reader()
    {
        var game = DeadlockGame.Definition;
        (game.Key, game.DisplayName, game.ShortName, game.Provider, game.ProviderGameId).Should().Be(("deadlock", "Deadlock", "Deadlock", "steam", "1422450"));
        game.Classifier.Should().BeOfType<DeadlockUpdateClassifier>();
        game.Highlighter.Should().BeOfType<DeadlockHighlighter>();
        game.WatchedThreadIds.Should().BeEmpty();
        Cs2Game.Definition.Highlighter.Should().BeNull();
    }
}
