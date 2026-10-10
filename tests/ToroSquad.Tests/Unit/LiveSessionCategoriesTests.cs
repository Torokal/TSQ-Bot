using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>The pure category-history rules of a creator session: first-seen order, one entry per category, identity.</summary>
public sealed class LiveSessionCategoriesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);

    private sealed class Session
    {
        public List<SessionCategory> Rows { get; } = [];
        private int _tick;

        public SessionCategory? See(LivePlatform platform, string? name, string? id = null, int? at = null) =>
            SessionCategories.Record(Rows, "lordtoro", 1, platform, id, name, T0 + TimeSpan.FromSeconds(at ?? (_tick += 30)));

        public string[] Names => SessionCategories.Ordered(Rows).Select(c => c.Name).ToArray();
    }

    [Fact]
    public void Categories_are_listed_once_in_first_seen_order()
    {
        var s = new Session();
        foreach (var name in new[] { "A", "B", "A", "C", "B" })
            s.See(LivePlatform.Twitch, name);
        s.Names.Should().Equal("A", "B", "C");

        var stream = new Session();
        foreach (var name in new[] { "Minecraft", "Counter-Strike 2", "Grand Theft Auto V", "Counter-Strike 2", "Minecraft", "Phasmophobia" })
            stream.See(LivePlatform.Kick, name);
        stream.Names.Should().Equal("Minecraft", "Counter-Strike 2", "Grand Theft Auto V", "Phasmophobia");
    }

    [Theory]
    [InlineData(LivePlatform.Twitch)]
    [InlineData(LivePlatform.Kick)]
    public void The_platform_category_id_prevents_duplicates_even_when_the_name_differs(LivePlatform platform)
    {
        var s = new Session();
        s.See(platform, "Counter-Strike 2", "32399").Should().NotBeNull();
        s.See(platform, "Counter-Strike 2", "32399").Should().BeNull("same id");
        s.See(platform, "Counter-Strike", "32399").Should().BeNull("same id under another name (provider rename/localization)");
        s.See(platform, null, "32399").Should().BeNull("known id without a name");
        s.Names.Should().Equal("Counter-Strike 2");
        s.Rows.Single().PlatformId(platform).Should().Be("32399");
    }

    [Fact]
    public void The_same_name_on_two_platforms_is_one_entry_and_each_platform_keeps_its_own_id()
    {
        var s = new Session();
        s.See(LivePlatform.Twitch, "Minecraft", "27471");
        s.See(LivePlatform.Kick, "  minecraft ", "15").Should().BeNull("same normalized name");
        var row = s.Rows.Should().ContainSingle().Subject;
        row.Name.Should().Be("Minecraft", "the first seen spelling is shown");
        row.FirstPlatform.Should().Be(LivePlatform.Twitch);
        row.TwitchCategoryId.Should().Be("27471");
        row.KickCategoryId.Should().Be("15");
    }

    [Fact]
    public void An_equal_id_number_on_different_platforms_does_not_mean_the_same_game()
    {
        var s = new Session();
        s.See(LivePlatform.Twitch, "Minecraft", "15");
        s.See(LivePlatform.Kick, "Grand Theft Auto V", "15").Should().NotBeNull("Twitch id 15 and Kick id 15 are different id spaces");
        s.Names.Should().Equal("Minecraft", "Grand Theft Auto V");
    }

    [Fact]
    public void Different_games_on_two_platforms_merge_into_one_creator_history()
    {
        var s = new Session();
        s.See(LivePlatform.Twitch, "Minecraft", "27471");
        s.See(LivePlatform.Kick, "Minecraft", "15");
        s.See(LivePlatform.Twitch, "Counter-Strike 2", "32399");
        s.See(LivePlatform.Kick, "Grand Theft Auto V", "20");
        s.See(LivePlatform.Twitch, "Minecraft", "27471");
        s.Names.Should().Equal("Minecraft", "Counter-Strike 2", "Grand Theft Auto V");
    }

    [Fact]
    public void Blank_placeholder_or_missing_categories_never_create_an_entry()
    {
        var s = new Session();
        foreach (var name in new[] { null, "", "   ", "\t\n", "unknown", "NULL", "Undefined" })
            s.See(LivePlatform.Kick, name).Should().BeNull(name ?? "(null)");
        s.See(LivePlatform.Kick, null, "77").Should().BeNull("an id without a name cannot be shown");
        s.See(LivePlatform.Twitch, "", "0").Should().BeNull();
        s.Rows.Should().BeEmpty();
        SessionCategories.CleanId("0").Should().BeNull("Kick states id 0 for 'no category'");
        SessionCategories.CleanId(" 42 ").Should().Be("42");
    }

    [Fact]
    public void Names_are_normalized_for_comparison_but_never_fuzzily_merged()
    {
        var s = new Session();
        s.See(LivePlatform.Twitch, "Counter-Strike 2");
        s.See(LivePlatform.Kick, "COUNTER-STRIKE   2").Should().BeNull("case and whitespace only");
        s.See(LivePlatform.Kick, "Counter-Strike").Should().NotBeNull("a different name is a different category");
        s.See(LivePlatform.Kick, "Counter Strike 2").Should().NotBeNull("no fuzzy matching");
        s.See(LivePlatform.Twitch, "Just Chatting").Should().NotBeNull("categories are not filtered by kind");
        s.See(LivePlatform.Kick, "IRL").Should().NotBeNull();
        s.Names.Should().Equal("Counter-Strike 2", "Counter-Strike", "Counter Strike 2", "Just Chatting", "IRL");
        SessionCategories.CleanName(new string('x', 300))!.Length.Should().Be(SessionCategories.NameMax);
    }

    [Fact]
    public void A_name_only_entry_learns_the_platform_id_later_and_then_dedupes_by_id()
    {
        var s = new Session();
        s.See(LivePlatform.Kick, "Phasmophobia");
        s.See(LivePlatform.Kick, "Phasmophobia", "901").Should().BeNull();
        s.Rows.Single().KickCategoryId.Should().Be("901");
        s.See(LivePlatform.Kick, "Phasmophobia (renamed)", "901").Should().BeNull();
        s.Names.Should().Equal("Phasmophobia");
    }

    [Fact]
    public void Equal_first_seen_times_are_ordered_by_insertion_deterministically()
    {
        var s = new Session();
        s.See(LivePlatform.Twitch, "Twitch Game", at: 30);
        s.See(LivePlatform.Kick, "Kick Game", at: 30);
        s.See(LivePlatform.Twitch, "Earlier", at: 10);
        s.Names.Should().Equal("Earlier", "Twitch Game", "Kick Game");
        s.Rows.Select(r => r.Sequence).Should().Equal(1, 2, 3);
    }
}
