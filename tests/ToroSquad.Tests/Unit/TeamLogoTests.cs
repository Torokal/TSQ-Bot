using System.Text.Json;
using System.Text.Json.Nodes;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Modules.Esports;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Fixtures;
using ToroSquad.Modules.Esports.Providers.PandaScore;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Team logos on match cards: only validated provider images (dark-mode logo first), shown as a small thumbnail only when
/// it cannot mislead (winner on plain results, the single followed team otherwise), never in spoiler results as the
/// winner, never on demo cards, and without changing anything else on the card or in delivery bookkeeping.
/// </summary>
public sealed class TeamLogoTests
{
    private static readonly LocalizationCatalog Localizer = new(
    [
        new LocalizationSource(typeof(CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
    ]);

    private const string LogoA = "https://cdn-api.pandascore.co/images/team/image/1/team_a.png";
    private const string LogoB = "https://cdn-api.pandascore.co/images/team/image/2/team_b.png";
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private static readonly IReadOnlySet<string> FollowsA = new HashSet<string> { "ps-team:1" };
    private static readonly IReadOnlySet<string> FollowsB = new HashSet<string> { "ps-team:2" };
    private static readonly IReadOnlySet<string> FollowsBoth = new HashSet<string> { "ps-team:1", "ps-team:2" };

    private static NotificationRenderer Live() => new(Localizer, new EsportsDataMode(ProviderMode.Live));

    private static EsportsMatch Match(MatchStatus status = MatchStatus.Scheduled, int? winner = null, bool draw = false, bool forfeit = false,
        string? logoA = LogoA, string? logoB = LogoB, MatchLinks? links = null) => new(
        new MatchKey("pandascore", "1234567"),
        new TournamentRef("pandascore", "ps-tournament:1", "StarLadder StarSeries Fall 2026", "1", null, null, null),
        Start, true, 3, status, "test",
        new MatchOpponent(OpponentKind.Team, new TeamRef("pandascore", "ps-team:1", "Natus Vincere", "NAVI", logoA),
            status == MatchStatus.Finished && !forfeit ? 2 : null, OpponentResult.Scored),
        new MatchOpponent(OpponentKind.Team, new TeamRef("pandascore", "ps-team:2", "Aurora", "AUR", logoB),
            status == MatchStatus.Finished && !forfeit ? 1 : null, OpponentResult.Scored),
        winner, draw, forfeit, [], "Playoffs", null, [],
        BeginAtUtc: status is MatchStatus.Live or MatchStatus.Finished ? Start : null,
        EndAtUtc: status == MatchStatus.Finished && !forfeit ? Start.AddHours(1) : null,
        Links: links);

    // ------------------------------------------------------------------ URL policy

    [Fact]
    public void Dark_mode_logo_is_preferred_and_image_url_is_the_fallback()
    {
        TeamLogoPolicy.Choose(LogoB, LogoA, out var rejected).Should().Be(LogoB);
        rejected.Should().BeFalse();
        TeamLogoPolicy.Choose(null, LogoA, out rejected).Should().Be(LogoA);
        rejected.Should().BeFalse();
        TeamLogoPolicy.Choose("javascript:alert(1)", LogoA, out _).Should().Be(LogoA, "an unusable dark-mode logo falls back to image_url");
        TeamLogoPolicy.Choose(null, null, out rejected).Should().BeNull();
        rejected.Should().BeFalse("a team without a logo is normal, not an error");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/images/team/image/1/a.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("file:///C:/logo.png")]
    [InlineData("http://cdn-api.pandascore.co/images/team/image/1/a.png")]
    [InlineData("https://cdn-api.pandascore.co.evil.net/images/team/image/1/a.png")]
    [InlineData("https://evilpandascore.co/images/a.png")]
    [InlineData("https://example.com/logo.png")]
    [InlineData("https://www.hltv.org/img/static/team/logo/1")]
    [InlineData("https://user:pass@cdn-api.pandascore.co/images/team/image/1/a.png")]
    [InlineData("https://cdn-api.pandascore.co:8443/images/team/image/1/a.png")]
    [InlineData("https://cdn-api.pandascore.co/images/team/image/1/a.png?token=secret")]
    [InlineData("https://cdn-api.pandascore.co/images/team/image/1/a.png#x")]
    [InlineData("https://cdn-api.pandascore.co/images/team/image/1/a.svg")]
    [InlineData("https://cdn-api.pandascore.co/images/team/image/1/")]
    public void Unsafe_or_unusable_logo_urls_are_rejected(string url)
    {
        TeamLogoPolicy.Validate(url).Should().BeNull();
        TeamLogoPolicy.Choose(url, null, out var rejected).Should().BeNull();
        rejected.Should().Be(!string.IsNullOrWhiteSpace(url), "a non-empty candidate that was refused is reported for logging");
    }

    [Fact]
    public void Overlong_logo_urls_are_rejected()
    {
        var url = "https://cdn-api.pandascore.co/images/team/image/1/" + new string('a', TeamLogoPolicy.MaxLength) + ".png";
        TeamLogoPolicy.Validate(url).Should().BeNull();
    }

    [Theory]
    [InlineData("https://cdn-api.pandascore.co/images/team/image/1/a.png")]
    [InlineData("https://CDN-API.PANDASCORE.CO/images/team/image/1/A.PNG")]
    [InlineData("https://cdn.pandascore.co/images/team/image/1/a.webp")]
    [InlineData("https://cdn-api.pandascore.co/images/team/image/1/a.jpg")]
    public void Provider_image_urls_are_accepted(string url) => TeamLogoPolicy.Validate(url).Should().NotBeNull();

    // ------------------------------------------------------------------ PandaScore parsing

    private static (EsportsMatch? Match, List<string> Warnings) Parse(JsonObject opponentA)
    {
        var json = new JsonObject
        {
            ["id"] = 77,
            ["status"] = "not_started",
            ["scheduled_at"] = "2026-09-24T18:00:00Z",
            ["opponents"] = new JsonArray(
                new JsonObject { ["type"] = "Team", ["opponent"] = opponentA },
                new JsonObject { ["type"] = "Team", ["opponent"] = new JsonObject { ["id"] = 2, ["name"] = "B" } }),
        }.ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var warnings = new List<string>();
        return (PandaScoreParser.ParseMatch(doc.RootElement.Clone(), warnings), warnings);
    }

    [Fact]
    public void Parser_takes_the_dark_mode_logo_then_image_url_and_tolerates_missing_or_bad_values()
    {
        var (both, w1) = Parse(new JsonObject { ["id"] = 1, ["name"] = "A", ["image_url"] = LogoA, ["dark_mode_image_url"] = LogoB });
        both!.A.Team!.LogoUrl.Should().Be(LogoB);
        w1.Should().BeEmpty();

        var (light, _) = Parse(new JsonObject { ["id"] = 1, ["name"] = "A", ["image_url"] = LogoA, ["dark_mode_image_url"] = null });
        light!.A.Team!.LogoUrl.Should().Be(LogoA);

        var (none, w2) = Parse(new JsonObject { ["id"] = 1, ["name"] = "A" });
        none!.A.Team!.LogoUrl.Should().BeNull();
        none.B.Team!.LogoUrl.Should().BeNull();
        w2.Should().BeEmpty("no logo is not a warning");

        var (bad, w3) = Parse(new JsonObject { ["id"] = 1, ["name"] = "A", ["image_url"] = "javascript:alert(1)", ["dark_mode_image_url"] = 42 });
        bad!.A.Team!.LogoUrl.Should().BeNull();
        bad.A.Team.Name.Should().Be("A", "the match is still parsed normally");
        w3.Should().ContainSingle().Which.Should().Contain("logo URL rejected").And.NotContain("javascript");
    }

    [Fact]
    public void Logos_are_not_part_of_match_snapshots_so_they_never_mark_a_match_as_changed()
    {
        var withLogo = Match();
        var withoutLogo = Match(logoA: null, logoB: null);
        var json = MatchJson.Serialize(withLogo);
        json.Should().NotContain("pandascore.co/images");
        MatchJson.Hash(json).Should().Be(MatchJson.Hash(MatchJson.Serialize(withoutLogo)));
        MatchJson.Deserialize(json)!.A.Team!.Name.Should().Be("Natus Vincere", "snapshots still round-trip");
    }

    // ------------------------------------------------------------------ selection rules

    [Fact]
    public void Plain_result_shows_the_winners_logo()
    {
        Live().Result(Match(MatchStatus.Finished, winner: 1), "tr", spoiler: false, MentionPolicy.None, Start).Embed!.ThumbnailUrl.Should().Be(LogoB);
        Live().Result(Match(MatchStatus.Finished, winner: 0), "tr", spoiler: false, MentionPolicy.None, Start, FollowsB).Embed!.ThumbnailUrl
            .Should().Be(LogoA, "the winner wins over the followed team on a plain result");
    }

    [Fact]
    public void Forfeit_result_shows_the_winners_logo()
    {
        var e = Live().Result(Match(MatchStatus.Finished, winner: 1, forfeit: true), "tr", spoiler: false, MentionPolicy.None, Start).Embed!;
        e.ThumbnailUrl.Should().Be(LogoB);
        e.Description.Should().Contain("Aurora");
    }

    [Fact]
    public void Result_without_a_known_winner_logo_shows_no_logo_rather_than_a_misleading_one()
    {
        Live().Result(Match(MatchStatus.Finished, winner: null), "tr", false, MentionPolicy.None, Start, FollowsA).Embed!.ThumbnailUrl
            .Should().BeNull("unknown winner");
        Live().Result(Match(MatchStatus.Finished, winner: 0, draw: true), "tr", false, MentionPolicy.None, Start, FollowsA).Embed!.ThumbnailUrl
            .Should().BeNull("draw");
        Live().Result(Match(MatchStatus.Finished, winner: 1, logoB: null), "tr", false, MentionPolicy.None, Start, FollowsA).Embed!.ThumbnailUrl
            .Should().BeNull("the winner has no logo; the loser's or followed team's logo would look like the winner");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Spoiler_result_never_shows_the_winner_through_the_logo(int winner)
    {
        Live().Result(Match(MatchStatus.Finished, winner: winner), "tr", spoiler: true, MentionPolicy.None, Start).Embed!.ThumbnailUrl
            .Should().BeNull("no followed team: nothing result-independent to show");
        Live().Result(Match(MatchStatus.Finished, winner: winner, forfeit: true), "tr", spoiler: true, MentionPolicy.None, Start).Embed!.ThumbnailUrl
            .Should().BeNull();
        Live().Result(Match(MatchStatus.Finished, winner: winner), "tr", spoiler: true, MentionPolicy.None, Start, FollowsA).Embed!.ThumbnailUrl
            .Should().Be(LogoA, "the followed team's logo is the same whoever won");
    }

    [Fact]
    public void Status_cards_show_only_the_single_followed_teams_logo()
    {
        var r = Live();
        var observed = Start.AddMinutes(5);
        foreach (var (name, render) in StatusCards(r, observed))
        {
            render(null).Embed!.ThumbnailUrl.Should().BeNull(name + ": no followed team");
            render(new HashSet<string>()).Embed!.ThumbnailUrl.Should().BeNull(name + ": empty team filter");
            render(FollowsA).Embed!.ThumbnailUrl.Should().Be(LogoA, name + ": followed team A");
            render(FollowsB).Embed!.ThumbnailUrl.Should().Be(LogoB, name + ": followed team B");
            render(FollowsBoth).Embed!.ThumbnailUrl.Should().BeNull(name + ": both teams followed is ambiguous");
            render(new HashSet<string> { "ps-team:999" }).Embed!.ThumbnailUrl.Should().BeNull(name + ": followed team not in this match");
        }
    }

    private static IEnumerable<(string, Func<IReadOnlySet<string>?, OutgoingMessage>)> StatusCards(NotificationRenderer r, DateTimeOffset observed) =>
    [
        ("started", f => r.Started(Match(MatchStatus.Live), "tr", MentionPolicy.None, observed, f)),
        ("postponed", f => r.Postponed(Match(MatchStatus.Postponed), "tr", observed, f)),
        ("rescheduled", f => r.Rescheduled(Match(), "tr", Start.AddDays(1), Istanbul, observed, f)),
        ("cancelled", f => r.Cancelled(Match(MatchStatus.Cancelled), "tr", observed, f)),
    ];

    [Fact]
    public void Followed_team_without_a_logo_means_no_thumbnail()
    {
        Live().Started(Match(MatchStatus.Live, logoA: null), "tr", MentionPolicy.None, Start, FollowsA).Embed!.ThumbnailUrl.Should().BeNull();
    }

    [Fact]
    public void Demo_cards_never_show_a_logo()
    {
        var demo = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Fixture));
        demo.Result(Match(MatchStatus.Finished, winner: 1), "tr", false, MentionPolicy.None, Start).Embed!.ThumbnailUrl.Should().BeNull();
        demo.Started(Match(MatchStatus.Live), "tr", MentionPolicy.None, Start, FollowsA).Embed!.ThumbnailUrl.Should().BeNull();
    }

    [Fact]
    public void An_unvalidated_logo_on_a_team_is_still_not_used()
    {
        // TeamRef values normally come from the parser, but the renderer re-validates before anything reaches Discord.
        Live().Result(Match(MatchStatus.Finished, winner: 1, logoB: "https://evil.example/x.png"), "tr", false, MentionPolicy.None, Start)
            .Embed!.ThumbnailUrl.Should().BeNull();
    }

    // ------------------------------------------------------------------ nothing else changes

    [Fact]
    public void The_logo_changes_nothing_else_on_the_card()
    {
        const string hltv = "https://www.hltv.org/matches/2388888/natus-vincere-vs-aurora-starladder-starseries-fall-2026";
        var links = new MatchLinks(HltvMatchUrl: hltv);
        var r = Live();
        var withLogo = r.Result(Match(MatchStatus.Finished, winner: 1, links: links), "tr", false, MentionPolicy.None, Start);
        var withoutLogo = r.Result(Match(MatchStatus.Finished, winner: 1, logoA: null, logoB: null, links: links), "tr", false, MentionPolicy.None, Start);

        withLogo.Embed!.ThumbnailUrl.Should().Be(LogoB);
        withoutLogo.Embed!.ThumbnailUrl.Should().BeNull();
        (withLogo.Embed with { ThumbnailUrl = null }).Should().BeEquivalentTo(withoutLogo.Embed, "title, match page, fields, footer, timestamp and colour are identical");
        withLogo.Embed.Url.Should().Be(hltv);
        withLogo.Content.Should().BeNull();
        withLogo.Mentions.Should().Be(MentionPolicy.None, "a logo never adds a ping");
        MessageFingerprint.Of(withLogo).Should().Be(MessageFingerprint.Of(withoutLogo), "ambiguous-delivery reconciliation ignores the thumbnail");
    }

    [Fact]
    public void Payloads_without_a_logo_serialize_exactly_as_before()
    {
        var r = Live();
        var noLogo = r.Started(Match(MatchStatus.Live, logoA: null, logoB: null), "tr", MentionPolicy.None, Start, FollowsA);
        PayloadSerializer.Serialize(noLogo).Should().NotContain("thumbnail", "existing outbox payload hashes stay stable");

        var logo = r.Started(Match(MatchStatus.Live), "tr", MentionPolicy.None, Start, FollowsA);
        var json = PayloadSerializer.Serialize(logo);
        json.Should().Contain("\"thumbnailUrl\":\"" + LogoA + "\"");
        PayloadSerializer.Deserialize(json).Should().BeEquivalentTo(logo);
    }

    [Fact]
    public void Discord_embed_carries_the_thumbnail_and_limits_reject_non_https_thumbnails()
    {
        var message = Live().Started(Match(MatchStatus.Live), "tr", MentionPolicy.None, Start, FollowsA);
        var embed = message.Embed!;
        DiscordConversions.ToEmbed(embed)!.Thumbnail!.Value.Url.Should().Be(LogoA);
        DiscordConversions.ToEmbed(embed with { ThumbnailUrl = null })!.Thumbnail.Should().BeNull();

        DiscordLimits.Validate(message).Should().BeEmpty();
        DiscordLimits.Validate(message with { Embed = embed with { ThumbnailUrl = "http://cdn-api.pandascore.co/a.png" } })
            .Should().Contain("embed thumbnail url invalid");
    }
}
