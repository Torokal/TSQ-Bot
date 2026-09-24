using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord;
using ToroSquad.Modules.Esports;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Compact match cards (Greg-style): exact golden layouts, spoiler safety, Discord limits, provider-text safety and the
/// "Maç Sayfası" link policy (verified HLTV → official → provider → none; HLTV never fetched or guessed).
/// </summary>
public sealed partial class MatchCardTests
{
    private static readonly LocalizationCatalog Localizer = new(
    [
        new LocalizationSource(typeof(CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
    ]);

    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 9, 17, 17, 46, 0, TimeSpan.Zero);
    private const string HltvUrl = "https://www.hltv.org/matches/2388888/natus-vincere-vs-aurora-starladder-starseries-fall-2026";

    private static NotificationRenderer Live() => new(Localizer, new EsportsDataMode(ProviderMode.Live));

    private static EsportsMatch Match(MatchStatus status = MatchStatus.Scheduled, int? a = null, int? b = null, int? winner = null,
        bool forfeit = false, MatchLinks? links = null, string teamA = "Natus Vincere", string teamB = "Aurora") => new(
        new MatchKey("pandascore", "1234567"),
        new TournamentRef("pandascore", "ps-tournament:1", "StarLadder StarSeries Fall 2026", "1", null, null, null),
        Start, true, 3, status, "test",
        new MatchOpponent(OpponentKind.Team, new TeamRef("pandascore", "ps-team:1", teamA, "NAVI"), a, OpponentResult.Scored),
        new MatchOpponent(OpponentKind.Team, new TeamRef("pandascore", "ps-team:2", teamB, "AUR"), b, OpponentResult.Scored),
        winner, false, forfeit, [new MapGame(1, "Mirage", GameStatus.Played, 13, 7, 1)], "Playoffs", null,
        [new StreamLink("Twitch", "https://www.twitch.tv/example")],
        BeginAtUtc: status is MatchStatus.Live or MatchStatus.Finished ? Start : null,
        EndAtUtc: status == MatchStatus.Finished ? End : null,
        Links: links);

    private static EsportsMatch Result(MatchLinks? links = null) => Match(MatchStatus.Finished, 0, 2, 1, links: links);

    // ------------------------------------------------------------------ golden layouts

    [Fact]
    public void Result_card_golden()
    {
        var e = Live().Result(Result(new MatchLinks(HltvMatchUrl: HltvUrl)), "tr", spoiler: false, MentionPolicy.None, End).Embed!;
        e.Title.Should().Be("Natus Vincere [0] - [2] Aurora");
        e.Description.Should().Be("🏆 Aurora maçı kazandı\n[Maç Sayfası](" + HltvUrl + ")");
        e.Fields.Select(f => (f.Name, f.Value, f.Inline)).Should().Equal(("Etkinlik", "StarLadder StarSeries Fall 2026", true), ("Format", "bo3", true));
        e.Footer.Should().Be("Kaynak: PandaScore");
        e.Timestamp.Should().Be(End, "the card is dated at the match end");
        e.Color.Should().Be(NotificationRenderer.ResultColor);
        e.Url.Should().BeNull();
    }

    [Fact]
    public void Started_card_golden()
    {
        var e = Live().Started(Match(MatchStatus.Live, 0, 0), "tr", MentionPolicy.None, Start.AddMinutes(3)).Embed!;
        e.Title.Should().Be("Natus Vincere vs Aurora");
        e.Description.Should().Be("▶️ Maç başladı");
        e.Fields.Select(f => (f.Name, f.Value)).Should().Equal(("Etkinlik", "StarLadder StarSeries Fall 2026"), ("Format", "bo3"));
        e.Footer.Should().Be("Kaynak: PandaScore");
        e.Timestamp.Should().Be(Start, "the provider's actual begin time");
        e.Color.Should().Be(NotificationRenderer.StartedColor);
    }

    [Fact]
    public void Postponed_card_golden()
    {
        var observed = new DateTimeOffset(2026, 9, 25, 16, 0, 0, TimeSpan.Zero);
        var e = Live().Postponed(Match(MatchStatus.Postponed), "tr", observed).Embed!;
        e.Title.Should().Be("Natus Vincere vs Aurora");
        e.Description.Should().Be("⏸️ Maç ertelendi\nYeni tarih henüz açıklanmadı.");
        e.Fields.Select(f => f.Name).Should().Equal("Etkinlik", "Format");
        e.Timestamp.Should().Be(observed);
        e.Color.Should().Be(NotificationRenderer.ChangeColor);
    }

    [Fact]
    public void Rescheduled_card_golden_shows_the_new_time_in_istanbul()
    {
        var newStart = new DateTimeOffset(2026, 9, 25, 19, 0, 0, TimeSpan.Zero);
        var e = Live().Rescheduled(Match(), "tr", newStart, Istanbul, newStart.AddHours(-5)).Embed!;
        e.Title.Should().Be("Natus Vincere vs Aurora");
        e.Description.Should().Be("🕒 Maçın saati değişti");
        e.Fields.Select(f => (f.Name, f.Value)).Should().Equal(("Etkinlik", "StarLadder StarSeries Fall 2026"), ("Format", "bo3"), ("Yeni Saat", "25/09/2026 22:00"));
        e.Color.Should().Be(NotificationRenderer.ChangeColor);
    }

    [Fact]
    public void Cancelled_and_forfeit_cards()
    {
        var cancelled = Live().Cancelled(Match(MatchStatus.Cancelled), "tr", Start).Embed!;
        cancelled.Description.Should().Be("❌ Maç iptal edildi");
        cancelled.Color.Should().Be(NotificationRenderer.CancelledColor);

        var forfeit = Live().Result(Match(MatchStatus.Finished, winner: 1, forfeit: true), "tr", false, MentionPolicy.None, End).Embed!;
        forfeit.Title.Should().Be("Natus Vincere vs Aurora", "a forfeit has no played score");
        forfeit.Description.Should().Be("🏳️ Maç hükmen sonuçlandı\n🏆 Aurora maçı kazandı");

        var noWinner = Live().Result(Match(MatchStatus.Finished, forfeit: true), "tr", false, MentionPolicy.None, End).Embed!;
        noWinner.Description.Should().Be("🏳️ Maç hükmen sonuçlandı", "no winner is stated without reliable winner data");
    }

    [Fact]
    public void Cards_are_compact_no_maps_streams_stages_ids_freshness_or_stars()
    {
        var renderer = Live();
        var cards = new[]
        {
            renderer.Result(Result(), "tr", false, MentionPolicy.None, End).Embed!,
            renderer.Started(Match(MatchStatus.Live), "tr", MentionPolicy.None, Start).Embed!,
            renderer.Postponed(Match(MatchStatus.Postponed), "tr", Start).Embed!,
            renderer.Rescheduled(Match(), "tr", Start.AddDays(1), Istanbul, Start).Embed!,
            renderer.Reminder(Match(), "tr", MentionPolicy.None, Start, null).Embed!,
        };
        foreach (var e in cards)
        {
            var all = string.Join("\n", new[] { e.Title, e.Description, e.Footer }.Concat(e.Fields.Select(f => f.Name + " " + f.Value)));
            all.Should().NotContain("Mirage").And.NotContain("twitch").And.NotContain("Playoffs").And.NotContain("1234567")
                .And.NotContain("ps-team").And.NotContain("Son veri").And.NotContain("⭐").And.NotContain("Yıldız");
            e.Fields.Count.Should().BeLessThanOrEqualTo(3);
            e.Description!.Split('\n').Length.Should().BeLessThanOrEqualTo(3);
        }
    }

    // ------------------------------------------------------------------ spoiler

    [Theory]
    [InlineData("tr", false)]
    [InlineData("en", false)]
    [InlineData("tr", true)]
    [InlineData("en", true)]
    public void Spoiler_result_hides_winner_score_and_forfeit_everywhere_outside_the_spoiler(string language, bool forfeit)
    {
        var match = forfeit ? Match(MatchStatus.Finished, winner: 1, forfeit: true) : Result(new MatchLinks(HltvMatchUrl: HltvUrl));
        var message = Live().Result(match, language, spoiler: true, MentionPolicy.None, End);
        var e = message.Embed!;
        var visible = string.Join("\n", new[] { message.Content, e.Title, e.Footer, e.Url, SpoilerPattern().Replace(e.Description!, "") }
            .Concat(e.Fields.Select(f => f.Name + " " + SpoilerPattern().Replace(f.Value, ""))));

        visible.Should().NotContain("[0]").And.NotContain("[2]").And.NotMatchRegex(@"\b[0-2]\s*[–-]\s*[0-2]\b");
        visible.Should().NotContain("kazandı").And.NotContain("won").And.NotContain("🏆").And.NotContain("🏳️")
            .And.NotContain("hükmen").And.NotContain("forfeit");
        e.Title.Should().Be("Natus Vincere vs Aurora");
        e.Description.Should().Contain("||");
        e.Color.Should().Be(NotificationRenderer.ResultColor, "colour never depends on the winner");
        if (!forfeit)
            e.Description.Should().Contain("[" + Localizer.Get(language, "esports.card.match_page") + "](" + HltvUrl + ")", "the match page link may stay in spoiler mode");
    }

    [Fact]
    public void Spoiler_line_has_the_same_visible_layout_whoever_wins()
    {
        var renderer = Live();
        var one = renderer.Result(Match(MatchStatus.Finished, 2, 0, 0), "tr", true, MentionPolicy.None, End).Embed!;
        var other = renderer.Result(Match(MatchStatus.Finished, 0, 2, 1), "tr", true, MentionPolicy.None, End).Embed!;
        one.Description!.Length.Should().Be(other.Description!.Length);
        one.Title.Should().Be(other.Title);
    }

    // ------------------------------------------------------------------ limits & provider text

    [Fact]
    public void Cards_stay_inside_discord_limits_with_hostile_long_names()
    {
        var huge = new string('W', 500);
        var renderer = Live();
        var match = Match(MatchStatus.Finished, 2, 1, 0, teamA: huge, teamB: huge) with
        {
            Tournament = new TournamentRef("pandascore", "t", new string('E', 3000), null, null, null, null),
        };
        foreach (var message in new[]
                 {
                     renderer.Result(match, "tr", false, MentionPolicy.None, End),
                     renderer.Result(match, "tr", true, MentionPolicy.None, End),
                     renderer.Started(match, "tr", MentionPolicy.None, Start),
                     renderer.Postponed(match, "tr", Start),
                     renderer.Rescheduled(match, "tr", Start, Istanbul, Start),
                     renderer.Cancelled(match, "tr", Start),
                     renderer.Reminder(match, "tr", MentionPolicy.None, Start, Start.AddHours(-1)),
                 })
        {
            DiscordLimits.Validate(message).Should().BeEmpty();
        }
    }

    [Fact]
    public void Provider_controlled_names_cannot_mention_or_create_links()
    {
        var evil = "@everyone <@&123> <@456> [free](https://evil.example/x) **bold**";
        var match = Match(MatchStatus.Finished, 2, 1, 0, teamA: evil) with
        {
            Tournament = new TournamentRef("pandascore", "t", "@here https://evil.example [x](https://evil.example)", null, null, null, null),
        };
        var renderer = Live();
        foreach (var message in new[] { renderer.Result(match, "tr", false, MentionPolicy.None, End), renderer.Started(match, "tr", MentionPolicy.None, Start) })
        {
            message.Content.Should().BeNull();
            message.Mentions.Roles.Should().BeEmpty();
            var e = message.Embed!;
            var text = string.Join("\n", new[] { e.Title, e.Description }.Concat(e.Fields.Select(f => f.Value)));
            DiscordText.RawMentionPattern().IsMatch(text).Should().BeFalse();
            text.Should().NotContain("](https://evil").And.NotContain("https://evil.example").And.NotContain("**bold**");
        }
    }

    // ------------------------------------------------------------------ HLTV link policy

    [Theory]
    [InlineData(HltvUrl, HltvUrl)]
    [InlineData("https://WWW.HLTV.ORG/matches/2388888/natus-vincere-vs-aurora", "https://www.hltv.org/matches/2388888/natus-vincere-vs-aurora")]
    [InlineData("https://www.hltv.org/matches/2388888/navi-vs-aurora?utm=x#top", "https://www.hltv.org/matches/2388888/navi-vs-aurora")]
    public void Valid_hltv_match_urls_are_accepted_and_normalized(string url, string expected) =>
        MatchLinkPolicy.ValidHltvMatchUrl(url).Should().Be(expected);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("discord://discord.com/channels/1/2")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("http://www.hltv.org/matches/2388888/navi-vs-aurora")]
    [InlineData("https://hltv.org/matches/2388888/navi-vs-aurora")]
    [InlineData("https://www.hltv.org.evil.example/matches/2388888/navi-vs-aurora")]
    [InlineData("https://evil.example/www.hltv.org/matches/2388888/navi-vs-aurora")]
    [InlineData("https://evil.example/?u=https://www.hltv.org/matches/2388888/navi-vs-aurora")]
    [InlineData("https://user:pw@www.hltv.org/matches/2388888/navi-vs-aurora")]
    [InlineData("https://www.hltv.org:8443/matches/2388888/navi-vs-aurora")]
    [InlineData("https://www.hltv.org/team/4608/natus-vincere")]
    [InlineData("https://www.hltv.org/matches/abc/navi-vs-aurora")]
    [InlineData("https://www.hltv.org/matches/2388888/navi vs aurora")]
    [InlineData("https://www.hltv.org/matches/2388888/navi-vs-aurora/../../admin")]
    [InlineData("https://localhost/matches/1/a")]
    [InlineData("")]
    [InlineData("not a url")]
    public void Malicious_or_non_hltv_match_urls_are_rejected(string url) =>
        MatchLinkPolicy.ValidHltvMatchUrl(url).Should().BeNull();

    [Fact]
    public void Verified_hltv_link_wins_over_official_and_provider_links()
    {
        var page = MatchLinkPolicy.Resolve(new MatchLinks(HltvUrl, "https://organizer.example/match/1", "https://liquipedia.net/counterstrike/X"), NotificationRenderer.AllowedLinkHosts);
        page.Should().Be(new MatchPage(MatchPageKind.Hltv, HltvUrl));
    }

    [Fact]
    public void Official_then_provider_fallbacks_and_invalid_candidates_are_skipped()
    {
        MatchLinkPolicy.Resolve(new MatchLinks("https://evil.example/matches/1/x", "https://organizer.example/match/1", "https://liquipedia.net/counterstrike/X"), NotificationRenderer.AllowedLinkHosts)
            .Should().Be(new MatchPage(MatchPageKind.Official, "https://organizer.example/match/1"), "an invalid HLTV candidate is never shown");
        MatchLinkPolicy.Resolve(new MatchLinks(null, "javascript:alert(1)", "https://liquipedia.net/counterstrike/X"), NotificationRenderer.AllowedLinkHosts)
            .Should().Be(new MatchPage(MatchPageKind.Provider, "https://liquipedia.net/counterstrike/X"));
        MatchLinkPolicy.Resolve(new MatchLinks(null, null, "https://unlisted.example/x"), NotificationRenderer.AllowedLinkHosts)
            .Should().BeNull("provider links must be on the provider allow-list");
    }

    [Fact]
    public void Official_and_provider_fallbacks_are_rendered_and_never_labelled_hltv()
    {
        var official = Live().Result(Result(new MatchLinks(OfficialMatchUrl: "https://organizer.example/match/1")), "tr", false, MentionPolicy.None, End).Embed!;
        official.Description.Should().EndWith("[Maç Sayfası](https://organizer.example/match/1)").And.NotContain("HLTV");

        var provider = Live().Result(Result() with { SourceUrl = "https://liquipedia.net/counterstrike/Cup" }, "tr", false, MentionPolicy.None, End).Embed!;
        provider.Description.Should().EndWith("[Maç Sayfası](https://liquipedia.net/counterstrike/Cup)");
    }

    [Fact]
    public void No_link_means_no_match_page_line_and_no_placeholder()
    {
        var e = Live().Result(Result(), "tr", false, MentionPolicy.None, End).Embed!;
        e.Description.Should().Be("🏆 Aurora maçı kazandı");
        e.Description.Should().NotContain("Maç Sayfası").And.NotContain("N/A");
        e.Fields.Should().NotContain(f => f.Name.Contains("Sayfa", StringComparison.Ordinal));
    }

    [Fact]
    public void Demo_cards_never_link_even_with_a_verified_hltv_url()
    {
        var demo = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Fixture));
        var e = demo.Result(Result(new MatchLinks(HltvMatchUrl: HltvUrl)), "tr", false, MentionPolicy.None, End).Embed!;
        e.Title.Should().StartWith("[TEST/DEMO] ");
        e.Description.Should().NotContain("hltv").And.NotContain("](");
        e.Footer.Should().Contain("TEST/DEMO").And.NotContain("PandaScore");
    }

    [Fact]
    public void Curated_catalog_attaches_only_valid_links_and_reports_invalid_config()
    {
        var options = Options.Create(new EsportsOptions
        {
            VerifiedMatchLinks =
            [
                new VerifiedMatchLink { Match = "pandascore:1234567", Hltv = HltvUrl },
                new VerifiedMatchLink { Match = "pandascore:7", Hltv = "https://evil.example/matches/1/x", Official = "https://organizer.example/m/7" },
            ],
        });
        var catalog = new MatchLinkCatalog(options);
        catalog.Apply(Match()).Links!.HltvMatchUrl.Should().Be(HltvUrl);
        var seven = catalog.Apply(Match() with { Key = new MatchKey("pandascore", "7") }).Links!;
        seven.HltvMatchUrl.Should().BeNull("an invalid HLTV entry is dropped, never shown");
        seven.OfficialMatchUrl.Should().Be("https://organizer.example/m/7");
        catalog.Apply(Match() with { Key = new MatchKey("pandascore", "8") }).Links.Should().BeNull("no entry → untouched");

        MatchLinkCatalog.Problems(options.Value.VerifiedMatchLinks).Should().ContainSingle(p => p.Contains("Hltv must look like", StringComparison.Ordinal));
        MatchLinkCatalog.Problems([new VerifiedMatchLink { Match = "no-colon" }]).Should().ContainSingle();
    }

    [Fact]
    public void Demo_cards_cover_every_kind_are_labelled_link_free_and_inside_limits()
    {
        var demo = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Fixture));
        var cards = EsportsDemoCards.Build(demo, "tr", Istanbul, End);
        cards.Select(c => c.Kind).Should().Equal("demo-started", "demo-result", "demo-result-spoiler", "demo-postponed", "demo-rescheduled", "demo-cancelled", "demo-forfeit");
        foreach (var (_, message) in cards)
        {
            message.Embed!.Title.Should().StartWith("[TEST/DEMO] ");
            message.Embed.Footer.Should().Contain("TEST/DEMO");
            message.Embed.Description.Should().NotContain("](");
            message.Content.Should().BeNull();
            DiscordLimits.Validate(message).Should().BeEmpty();
        }

        FluentActions.Invoking(() => EsportsDemoCards.Build(Live(), "tr", Istanbul, End)).Should().Throw<InvalidOperationException>("demo cards must never look real");
    }

    [GeneratedRegex(@"\|\|.*?\|\|", RegexOptions.Singleline)]
    private static partial Regex SpoilerPattern();
}
