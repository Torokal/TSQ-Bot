using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// From a forum post's rendered HTML to what the module uses: blocks, normalized text, sections, version and build, the
/// bounded card excerpt and its stored form. All post text here is made up; the layouts are the two Blizzard's update
/// posts use. Nothing may throw on broken markup, and the same post must always give the same result.
/// </summary>
public sealed class UpdatesForumContentTests
{
    // ---------- reading the HTML ----------

    [Fact]
    public void Headings_paragraphs_and_nested_lists_become_plain_blocks()
    {
        var blocks = ForumHtml.Read("<p>Intro <em>text</em>.</p><h2><a name=\"x\" class=\"anchor\" href=\"#x\"></a>Change Log</h2>" +
                                    "<p><strong>Bug Fixes</strong></p><ul><li>First fix.</li><li>Second <strong>fix</strong>.<ul><li>A detail.</li></ul></li></ul>");
        blocks.Should().HaveCount(4);
        blocks[0].Should().Be(new ForumParagraph("Intro text.", false));
        blocks[1].Should().Be(new ForumHeading("Change Log"));
        blocks[2].Should().Be(new ForumParagraph("Bug Fixes", true));
        var list = blocks[3].Should().BeOfType<ForumList>().Subject;
        list.Items.Select(i => (i.Text, i.Emphasized, i.Children.Count)).Should().Equal(("First fix.", false, 0), ("Second fix.", false, 1));
        list.Items[1].Children.Single().Text.Should().Be("A detail.");
    }

    [Fact]
    public void Entities_line_breaks_and_whitespace_are_normalized()
    {
        var blocks = ForumHtml.Read("<p>Tom &amp; Jerry&#39;s &lt;b&gt; &quot;build&quot;&nbsp;notes<br>\n   second\tline &#8211; done</p>");
        blocks.Should().ContainSingle().Which.Should().Be(new ForumParagraph("Tom & Jerry's <b> \"build\" notes second line – done", false));
    }

    [Fact]
    public void A_list_item_made_only_of_bold_text_is_emphasized_and_partly_bold_text_is_not()
    {
        var list = ForumHtml.Read("<ul><li><strong>Classes</strong><ul><li>x</li></ul></li><li><b>All bold</b> <strong>still</strong></li><li><strong>Name</strong>: detail</li></ul>")
            .Single().Should().BeOfType<ForumList>().Subject;
        list.Items.Select(i => (i.Text, i.Emphasized)).Should().Equal(("Classes", true), ("All bold still", true), ("Name: detail", false));
    }

    [Fact]
    public void Quotes_embeds_code_blocks_tables_and_scripts_are_left_out()
    {
        var blocks = ForumHtml.Read(
            "<aside class=\"quote\" data-username=\"player\"><blockquote><p>quoted player text <ul><li>quoted item</li></ul></p></blockquote></aside>" +
            "<p>Kept.</p><pre><code>code &lt;ul&gt;</code></pre><table><tr><td>cell</td></tr></table><script>alert('x')</script><style>p{}</style>" +
            "<blockquote><p>another quote</p></blockquote><ul><li>Kept item</li></ul><!-- a comment <ul><li>hidden</li></ul> --><p>End.</p>");
        blocks.Should().HaveCount(3);
        blocks[0].Should().Be(new ForumParagraph("Kept.", false));
        blocks[1].Should().BeOfType<ForumList>().Which.Items.Single().Text.Should().Be("Kept item");
        blocks[2].Should().Be(new ForumParagraph("End.", false));
    }

    [Fact]
    public void Images_links_and_unknown_tags_are_only_their_text_and_nothing_is_fetched()
    {
        var blocks = ForumHtml.Read("<div class=\"x\"><p>See <a href=\"https://evil.example/x?a=1&amp;b=2\" onclick=\"a>b\">the <span>link</span></a> " +
                                    "<img src=\"https://example.org/a.png\" alt=\"an image > here\"> now</p></div>");
        blocks.Should().ContainSingle().Which.Should().Be(new ForumParagraph("See the link now", false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   \n ")]
    [InlineData("<")]
    [InlineData("<<<<>>>>")]
    [InlineData("</ul></li></p>")]
    [InlineData("<ul><li>unclosed")]
    [InlineData("<p>text <strong>never closed")]
    [InlineData("<li>item without a list</li>")]
    [InlineData("<ul><ul><ul></ul>")]
    [InlineData("<a href=\"never ends")]
    [InlineData("<!-- never ends")]
    [InlineData("5 < 6 and 7 > 3")]
    [InlineData("<p>a</p></aside></blockquote><p>b</p>")]
    public void Broken_markup_never_throws(string? html)
    {
        var act = () => ForumPostReader.Read(html);
        act.Should().NotThrow();
        ForumPostReader.Read(html).Should().NotBeNull();
    }

    [Fact]
    public void Unclosed_items_and_stray_text_still_read_sensibly()
    {
        ForumHtml.Read("<ul><li>one<li>two<li>three</ul>").Single().Should().BeOfType<ForumList>().Which.Items.Select(i => i.Text).Should().Equal("one", "two", "three");
        ForumHtml.Read("5 < 6 and 7 > 3").Single().Should().Be(new ForumParagraph("5 < 6 and 7 > 3", false));
        ForumHtml.Read("<ul><li>open").Single().Should().BeOfType<ForumList>().Which.Items.Single().Text.Should().Be("open");
    }

    [Fact]
    public void The_reader_is_bounded_in_depth_items_text_and_input()
    {
        var deep = string.Concat(Enumerable.Repeat("<ul><li>level", 40)) + string.Concat(Enumerable.Repeat("</li></ul>", 40));
        int Depth(ForumListItem item) => 1 + (item.Children.Count == 0 ? 0 : item.Children.Max(Depth));
        Depth(ForumHtml.Read(deep).Single().Should().BeOfType<ForumList>().Subject.Items.Single()).Should().Be(ForumHtml.MaxListDepth);

        var many = "<ul>" + string.Concat(Enumerable.Range(0, ForumHtml.MaxListItems + 500).Select(i => "<li>i" + i + "</li>")) + "</ul>";
        ForumHtml.Read(many).Single().Should().BeOfType<ForumList>().Which.Items.Should().HaveCount(ForumHtml.MaxListItems);

        ForumHtml.Read("<p>" + new string('x', 50_000) + "</p>").Single().Should().BeOfType<ForumParagraph>().Which.Text.Should().HaveLength(ForumHtml.MaxTextLength);

        var huge = "<p>start</p>" + new string(' ', ForumHtml.MaxInputLength) + "<p>beyond the bound</p>";
        ForumHtml.Read(huge).Should().ContainSingle().Which.Should().Be(new ForumParagraph("start", false));
    }

    // ---------- sections, version and build ----------

    [Fact]
    public void Bold_paragraph_sections_are_read_with_their_lines_and_the_change_count()
    {
        var content = ForumPostReader.Read(ForumHtmlSamples.BoldParagraphSections("Today we updated the beta.",
            ("Bug Fixes", ["Fixed one thing.", "Fixed another thing."]), ("Classes", ["Changed a class."])));
        content.Highlights.Sections.Select(s => (s.Heading, string.Join("|", s.Items))).Should().Equal(
            ("Bug Fixes", "Fixed one thing.|Fixed another thing."), ("Classes", "Changed a class."));
        content.Highlights.ChangeCount.Should().Be(3);
        (content.Highlights.Version, content.Highlights.Build).Should().Be((null, null), "a post without a build line is still a post");
        content.Text.Should().Be("Today we updated the beta.\n# Change Log\nBug Fixes\n- Fixed one thing.\n- Fixed another thing.\nClasses\n- Changed a class.\n");
    }

    [Fact]
    public void Nested_list_sections_are_read_the_same_way()
    {
        var content = ForumPostReader.Read(ForumHtmlSamples.NestedListSections("Today we updated the beta.",
            ("Bugfixes", ["Fixed one thing.", "Fixed another thing."]), ("Classes:", ["Changed a class."])));
        content.Highlights.Sections.Select(s => (s.Heading, string.Join("|", s.Items))).Should().Equal(
            ("Bugfixes", "Fixed one thing.|Fixed another thing."), ("Classes", "Changed a class."));
        content.Highlights.ChangeCount.Should().Be(3);
    }

    [Fact]
    public void A_plain_list_is_a_section_without_a_heading_and_deeper_levels_only_count()
    {
        var content = ForumPostReader.Read("<p>Notes.</p><ul><li>Loose one</li><li><strong>Druid</strong><ul><li>Change A<ul><li>Detail 1</li><li>Detail 2</li></ul></li><li>Change B</li></ul></li><li>Loose two</li></ul>");
        content.Highlights.Sections.Select(s => (s.Heading, string.Join("|", s.Items))).Should().Equal(
            (null, "Loose one|Loose two"), ("Druid", "Change A|Change B"));
        content.Highlights.ChangeCount.Should().Be(5, "leaves: Loose one, Detail 1, Detail 2, Change B, Loose two");
    }

    [Fact]
    public void A_lists_own_lines_stay_one_section_so_a_heading_never_repeats()
    {
        // Seen in a real post: lines, then an item whose nested list only holds a further nested list, then more lines.
        var content = ForumPostReader.Read(
            "<p><strong>Changes and Updates</strong></p><ul><li>First change.</li>" +
            "<li>New dungeons are open<ul><li><ul><li>Dungeon A</li><li>Dungeon B</li></ul></li></ul></li>" +
            "<li><strong>Camping</strong><ul><li>Camp change.</li></ul></li><li>Last change.</li></ul>");
        content.Highlights.Sections.Select(s => (s.Heading, string.Join("|", s.Items))).Should().Equal(
            ("Changes and Updates", "First change.|New dungeons are open|Last change."), ("Camping", "Camp change."));
        content.Highlights.ChangeCount.Should().Be(5);
    }

    [Theory]
    [InlineData("WoW Forever 1.60.1 Build 69977", "1.60.1", "69977")]
    [InlineData("Version 12.0 build 70205 is live", "12.0", "70205")]
    [InlineData("now at 1.60.1.3 Build 1234567.", "1.60.1.3", "1234567")]
    [InlineData("Build 70001 is available", null, "70001")]
    [InlineData("We will build 12345 houses and rebuild 70000 more", null, "12345")]
    [InlineData("Rebuild 70000 walls", null, null)]
    [InlineData("Build 123 is too short, Build 12345678 too long", null, null)]
    [InlineData("Patch 1.60.1 notes without a build", null, null)]
    [InlineData("", null, null)]
    public void Version_and_build_come_from_a_build_line_only(string text, string? version, string? build) =>
        ForumPostReader.VersionAndBuild(text).Should().Be((version, build));

    [Fact]
    public void A_client_update_post_gives_version_build_and_its_list()
    {
        var content = ForumPostReader.Read(ForumHtmlSamples.ClientUpdate("WoW Forever 1.60.1 Build 69977", "Fixed a display issue.", "Fixed a stability issue."));
        (content.Highlights.Version, content.Highlights.Build, content.Highlights.ChangeCount).Should().Be(("1.60.1", "69977", 2));
        content.Highlights.Sections.Should().ContainSingle().Which.Heading.Should().BeNull();
    }

    [Fact]
    public void A_cosmetic_re_render_of_the_html_is_not_a_change_and_a_real_edit_is()
    {
        var first = ForumPostReader.Read("<p><strong>Bug Fixes</strong></p>\n<ul>\n<li>Fixed one thing.</li>\n</ul>");
        var rebaked = ForumPostReader.Read("<p ><strong class=\"x\">Bug   Fixes</strong></p><ul data-a=\"1\"><li>Fixed one\nthing.</li></ul><!-- cache 2 -->");
        rebaked.Text.Should().Be(first.Text);
        ForumPostReader.Read("<p><strong>Bug Fixes</strong></p><ul><li>Fixed one thing, twice.</li></ul>").Text.Should().NotBe(first.Text);
    }

    // ---------- the bounded excerpt and its stored form ----------

    [Fact]
    public void Highlights_are_bounded_however_large_the_post_is()
    {
        var sections = Enumerable.Range(1, 20).Select(s => ("Section " + s + " " + new string('h', 200), Enumerable.Range(1, 20).Select(i => "Item " + i + " " + new string('x', 230)).ToArray())).ToArray();
        var content = ForumPostReader.Read(ForumHtmlSamples.BoldParagraphSections(null, sections));
        var highlights = content.Highlights;
        highlights.ChangeCount.Should().Be(400);
        highlights.Sections.Should().HaveCount(UpdateHighlights.MaxSections);
        highlights.Sections.Should().OnlyContain(s => s.Items.Count == UpdateHighlights.MaxItemsPerSection && s.Heading!.Length <= UpdateHighlights.MaxHeadingLength);
        highlights.Sections.SelectMany(s => s.Items).Should().OnlyContain(i => i.Length <= UpdateHighlights.MaxItemLength && i.EndsWith('…'));

        var json = UpdateHighlightsJson.Serialize(highlights)!;
        json.Length.Should().BeLessThanOrEqualTo(UpdateHighlightsJson.MaxLength);
        var back = UpdateHighlightsJson.Parse(json)!;
        back.ChangeCount.Should().Be(400);
        back.Sections.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(UpdateHighlights.MaxSections);
        back.Sections[0].Items.Should().Equal(highlights.Sections[0].Items);
    }

    [Fact]
    public void The_stored_form_round_trips_and_reading_it_is_forgiving()
    {
        var highlights = UpdateHighlights.Create(" 1.60.1 ", "69977", 12, [new UpdateSection("Bug Fixes", ["One", " Two\nlines "]), new UpdateSection(null, ["Three"]), new UpdateSection("Empty", [])]);
        (highlights.Version, highlights.Build).Should().Be(("1.60.1", "69977"));
        highlights.Sections.Select(s => (s.Heading, string.Join("|", s.Items))).Should().Equal(("Bug Fixes", "One|Two lines"), (null, "Three"));

        var back = UpdateHighlightsJson.Parse(UpdateHighlightsJson.Serialize(highlights))!;
        (back.Version, back.Build, back.ChangeCount).Should().Be(("1.60.1", "69977", 12));
        back.Sections.Select(s => (s.Heading, string.Join("|", s.Items))).Should().Equal(("Bug Fixes", "One|Two lines"), (null, "Three"));

        UpdateHighlightsJson.Serialize(null).Should().BeNull();
        UpdateHighlightsJson.Serialize(UpdateHighlights.Create(null, null, 0, [])).Should().BeNull("nothing to show is nothing to store");
        foreach (var broken in new[] { null, "", "not json", "[]", "{\"s\":5}", "{\"s\":[5,{\"i\":\"x\"}]}", "{\"n\":\"many\"}", new string('x', 5000) })
            UpdateHighlightsJson.Parse(broken).Should().BeNull();
        UpdateHighlightsJson.Parse("{\"b\":\"70001\",\"extra\":1,\"s\":[{\"h\":7,\"i\":[\"ok\",5]}]}")!.Should()
            .Match<UpdateHighlights>(h => h.Build == "70001" && h.Sections.Single().Heading == null && h.Sections.Single().Items.Single() == "ok");
    }
}
