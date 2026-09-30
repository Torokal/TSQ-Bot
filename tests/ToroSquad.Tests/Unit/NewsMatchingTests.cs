using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.News;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Domain;
using ToroSquad.Modules.News.Providers;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ News matching (Aurora and its current players, look-alike teams, ambiguous names, stale rosters), the Liquipedia
/// Active-squad parser, the card and the options. Synthetic headlines only.
/// </summary>
public sealed class NewsMatchingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static readonly NewsOptions Defaults = new();
    private static readonly RosterSnapshot Roster = new(["XANTARES", "woxic", "Wicadia", "Jimpphat", "kyxsan"], Now.AddDays(-1), "liquipedia");

    private static AuroraNewsMatcher Matcher(params string[] ambiguous) => new(Defaults.Team.Aliases, Defaults.Team.ExcludedNames, ambiguous);

    private static NewsMatch Match(string title, string description = "", RosterSnapshot? roster = null, AuroraNewsMatcher? matcher = null) =>
        (matcher ?? Matcher()).Evaluate(new NewsArticle(1, "https://www.hltv.org/news/1/a", title, Now, description), roster ?? Roster, Now, MaxAge);

    [Theory]
    [InlineData("Aurora complete roster with new signing")]
    [InlineData("AURORA part ways with coach")]
    [InlineData("Aurora Gaming announce bootcamp")]
    [InlineData("Aurora's new era begins")]
    [InlineData("FaZe beat Aurora in Cologne opener")]
    [InlineData("Vitality, Aurora and MOUZ qualify")]
    public void The_team_name_in_the_headline_matches(string title) =>
        Match(title).Reason.Should().Be(NewsMatchReason.TeamInTitle);

    [Fact]
    public void The_team_only_in_the_description_matches_as_such()
    {
        var match = Match("Turkish side confirm stand-in", "The Aurora organisation confirmed the move on Tuesday.");
        match.Reason.Should().Be(NewsMatchReason.TeamInDescription);
        match.Relevant.Should().BeTrue();
    }

    [Theory]
    [InlineData("XANTARES: \"We can win a Major\"")]
    [InlineData("woxic on the new role")]
    [InlineData("Jimpphat: \"The team clicked\"")]
    public void A_current_player_in_the_headline_matches_without_the_team_name(string title) =>
        Match(title).Reason.Should().Be(NewsMatchReason.CurrentPlayerInTitle);

    [Theory]
    [InlineData("CRUISER AURORA win regional qualifier")]
    [InlineData("Aurora Young Blood sign two")]
    [InlineData("Aurora YB take the title")]
    [InlineData("ex-Aurora player joins BIG")]
    [InlineData("Aurorae enter CS")]
    [InlineData("Borealis without a roster")]
    public void Look_alike_teams_and_partial_words_do_not_match(string title)
    {
        var match = Match(title);
        match.Relevant.Should().BeFalse(title);
        match.Reason.Should().Be(NewsMatchReason.Unrelated);
    }

    [Fact]
    public void A_look_alike_does_not_hide_a_separate_mention_of_the_team()
    {
        Match("CRUISER AURORA and Aurora share a bootcamp").Reason.Should().Be(NewsMatchReason.TeamInTitle);
        Match("Aurora Young Blood promote player to Aurora").Reason.Should().Be(NewsMatchReason.TeamInTitle);
        Match("Aurora Young Blood talent", "He trains with Aurora Young Blood.").Relevant.Should().BeFalse();
    }

    [Fact]
    public void Case_unicode_and_word_boundaries_are_handled()
    {
        Match("ＡＵＲＯＲＡ fullwidth headline").Reason.Should().Be(NewsMatchReason.TeamInTitle, "NFKC folds compatibility forms");
        Match("Aurora’s coach speaks").Reason.Should().Be(NewsMatchReason.TeamInTitle);
        Match("xantares interview").Reason.Should().Be(NewsMatchReason.CurrentPlayerInTitle);
        Match("XANTARESfan club opens").Relevant.Should().BeFalse("no word boundary");
        Match("iXANTARES is a different handle").Relevant.Should().BeFalse();
    }

    [Fact]
    public void Short_or_ordinary_player_names_need_the_team_name()
    {
        var roster = Roster with { Players = ["ash", "Ace", "zyx", "XANTARES"] };
        Match("ash: the long road back", roster: roster).Reason.Should().Be(NewsMatchReason.Ambiguous);
        Match("Ace wins award", roster: roster).Reason.Should().Be(NewsMatchReason.Ambiguous);
        Match("zyx interview", roster: roster).Reason.Should().Be(NewsMatchReason.Ambiguous, "shorter than four characters");
        Match("ash joins Aurora", roster: roster).Reason.Should().Be(NewsMatchReason.TeamInTitle);
        Match("Kyxsan on tactics", roster: Roster, matcher: Matcher("kyxsan")).Reason.Should().Be(NewsMatchReason.Ambiguous, "configured as ambiguous");
    }

    [Fact]
    public void A_former_player_in_another_teams_news_does_not_match()
    {
        // MAJ3R was benched on 2026-07-02 and is not in the Active squad: his move elsewhere is not Aurora news.
        Match("MAJ3R joins Eternal Fire").Relevant.Should().BeFalse();
        Match("Fabre named Eternal Fire coach").Relevant.Should().BeFalse();
    }

    [Fact]
    public void A_stale_roster_disables_player_matching_but_not_team_matching()
    {
        var stale = Roster with { VerifiedAt = Now.AddDays(-8) };
        var player = Match("XANTARES interview", roster: stale);
        player.Relevant.Should().BeFalse();
        player.Evidence.Should().Contain("stale");
        Match("Aurora interview", roster: stale).Reason.Should().Be(NewsMatchReason.TeamInTitle);
        Matcher().Evaluate(new NewsArticle(1, "https://www.hltv.org/news/1/a", "XANTARES interview", Now, ""), null, Now, MaxAge).Relevant.Should().BeFalse("no roster at all");
    }

    [Fact]
    public void A_player_only_in_the_description_is_not_enough()
    {
        Match("Top 20 players of the year", "XANTARES comes in at number 14.").Reason.Should().Be(NewsMatchReason.Ambiguous);
    }

    [Theory]
    [InlineData("Short news: Week 39", "Catch up with more Counter-Strike news")]
    [InlineData("Liquid claim IBP Masters title over Wildcard", "")]
    public void No_evidence_means_unrelated(string title, string description) =>
        Match(title, description).Reason.Should().Be(NewsMatchReason.Unrelated);

    [Fact]
    public void Team_and_player_together_are_one_match()
    {
        var match = Match("XANTARES and Aurora extend contract", "Aurora confirmed.");
        match.Reason.Should().Be(NewsMatchReason.TeamInTitle, "one reason per item; one card per item");
    }

    // ---------- roster ----------

    [Fact]
    public void Only_players_of_the_active_squad_are_read_from_liquipedia()
    {
        const string wikitext = """
            ==Player Roster==
            {{tabs dynamic
            |content1=
            ===Active===
            {{Squad|status=active
            |{{Person|flag=tr|id=XANTARES|name=A B|joindate=2025-04-05 <ref name="roster 2025"/>}}
            |{{Person|flag=tr|id=woxic|name=C D|joindate=2025-04-05 <ref name="roster 2025"/>}}
            |{{Person|flag=fi|id=Jimpphat|name=E F|joindate=2026-07-03}}
            |{{Person|flag=mk|id=kyxsan|igl=y|name=G H|joindate=2026-07-08}}
            |{{Person|flag=uk|id=ashhh|role=Coach|name=I J|joindate=2026-07-03}}
            }}

            ===Inactive===
            {{Squad|status=inactive
            |{{Person|flag=tr|id=MAJ3R|igl=y|name=K L|inactivedate=2026-07-02}}
            }}
            """;
        LiquipediaRosterClient.ParseActivePlayers(wikitext).Should().Equal("XANTARES", "woxic", "Jimpphat", "kyxsan");
        LiquipediaRosterClient.ParseActivePlayers("no roster here").Should().BeEmpty();
    }

    // ---------- card ----------

    private static NewsCardRenderer Renderer() => new(new LocalizationCatalog(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(NewsModule).Assembly, "ToroSquad.Modules.News.Localization"),
    ]), Options.Create(new NewsOptions()));

    [Fact]
    public void The_card_shows_the_headline_a_read_link_the_source_and_the_native_time_and_pings_nobody()
    {
        var published = new DateTimeOffset(2026, 9, 30, 9, 50, 0, TimeSpan.Zero);
        var message = Renderer().Render(new NewsArticle(45800, "https://www.hltv.org/news/45800/aurora-sign-x", "Aurora sign X", published, "desc"), "tr");
        var embed = message.Embed!;
        embed.Title.Should().Be("📰 Aurora — HLTV");
        embed.Url.Should().Be("https://www.hltv.org/news/45800/aurora-sign-x");
        embed.Description.Should().Be("**Aurora sign X**\n\n[HLTV’de oku](https://www.hltv.org/news/45800/aurora-sign-x)");
        embed.Footer.Should().Be("Kaynak: HLTV");
        embed.Timestamp.Should().Be(published);
        embed.ThumbnailUrl.Should().BeNull();
        embed.Fields.Should().BeEmpty();
        message.Content.Should().BeNull();
        message.Mentions.PingsAnything.Should().BeFalse();
        message.Mentions.Should().Be(MentionPolicy.None);
        DiscordLimits.Validate(message).Should().BeEmpty();
        Renderer().Render(new NewsArticle(1, "https://www.hltv.org/news/1/a", "x", null, ""), "en").Embed!.Timestamp.Should().BeNull("no date is ever invented");
    }

    [Fact]
    public void Feed_text_can_neither_mention_nor_link_nor_format()
    {
        var message = Renderer().Render(new NewsArticle(1, "https://www.hltv.org/news/1/a", "@everyone <@&123> [free](https://evil.example) **x** ||s||", Now, ""), "tr");
        var description = message.Embed!.Description!;
        DiscordText.RawMentionPattern().IsMatch(description).Should().BeFalse();
        description.Should().NotContain("https://evil").And.NotContain("[free](");
        description.Should().Contain("\\*\\*x\\*\\*");
        message.Mentions.PingsAnything.Should().BeFalse();
    }

    [Fact]
    public void Two_articles_with_the_same_headline_have_different_fingerprints()
    {
        var a = Renderer().Render(new NewsArticle(1, "https://www.hltv.org/news/1/aurora-x", "Aurora news", Now, ""), "tr");
        var b = Renderer().Render(new NewsArticle(2, "https://www.hltv.org/news/2/aurora-x", "Aurora news", Now, ""), "tr");
        MessageFingerprint.Of(a).Should().NotBe(MessageFingerprint.Of(b), "the link is part of the description");
    }

    [Fact]
    public void Long_headlines_are_shortened_safely()
    {
        var message = Renderer().Render(new NewsArticle(1, "https://www.hltv.org/news/1/a", new string('A', 290) + " Aurora", Now, ""), "tr");
        message.Embed!.Description!.Length.Should().BeLessThan(400);
        DiscordLimits.Validate(message).Should().BeEmpty();
    }

    // ---------- options ----------

    [Fact]
    public void Defaults_are_off_valid_and_the_production_file_keeps_the_module_off()
    {
        var o = new NewsOptions();
        o.Mode.Should().Be(NewsMode.Off);
        o.Validate().Should().BeEmpty();
        new NewsModule().Descriptor.EnabledByDefault.Should().BeFalse();
        new NewsOptions { PollIntervalMinutes = 1 }.Validate().Should().NotBeEmpty("never faster than every 5 minutes");
        new NewsOptions { UserAgent = "curl" }.Validate().Should().NotBeEmpty();

        var appsettings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "appsettings.json")).Build();
        var bound = appsettings.GetSection(NewsOptions.Section).Get<NewsOptions>()!;
        bound.Mode.Should().Be(NewsMode.Off, "live posting is switched on only after the owner's approval");
        bound.Team.Aliases.Should().Equal("Aurora", "Aurora Gaming");
        new NewsModule().ValidateConfiguration(appsettings).Should().BeEmpty();
    }
}
