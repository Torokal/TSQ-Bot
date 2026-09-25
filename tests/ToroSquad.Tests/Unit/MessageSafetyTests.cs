using System.Text.RegularExpressions;
using Discord;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Esports;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Tests.Unit;

/// <summary>Criterion 13: spoilers never leak, allowed_mentions is closed by default, previews/edits never ping.</summary>
public sealed partial class MessageSafetyTests
{
    private static readonly ILocalizer Localizer = new LocalizationCatalog(
    [
        new LocalizationSource(typeof(CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(EsportsModule).Assembly, "ToroSquad.Modules.Esports.Localization"),
    ]);

    private static EsportsMatch Finished(string a = "Alpha Squad", string b = "Bravo Crew", bool forfeit = false) => new(
        new MatchKey("liquipedia:counterstrike", "R1"),
        new TournamentRef("liquipedia", "Cup/2026", "Cup 2026", "1", null, null, null),
        new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero), true, 3, MatchStatus.Finished, "test",
        new MatchOpponent(OpponentKind.Team, new TeamRef("liquipedia", "k/a", a, null), forfeit ? null : 2, forfeit ? OpponentResult.Win : OpponentResult.Scored),
        new MatchOpponent(OpponentKind.Team, new TeamRef("liquipedia", "k/b", b, null), forfeit ? null : 1, forfeit ? OpponentResult.Forfeit : OpponentResult.Scored),
        0, false, forfeit,
        forfeit ? [] :
        [
            new MapGame(1, "Mirage", GameStatus.Played, 13, 7, 0),
            new MapGame(2, "Nuke", GameStatus.Played, 11, 13, 1),
            new MapGame(3, "Inferno", GameStatus.Played, 13, 10, 0),
        ],
        "Final", "https://liquipedia.net/counterstrike/Cup/2026", []);

    [Theory]
    [InlineData("tr", false)]
    [InlineData("en", false)]
    [InlineData("tr", true)]
    [InlineData("en", true)]
    public void Spoiler_mode_leaks_no_score_or_winner_outside_spoiler_tags(string language, bool forfeit)
    {
        var renderer = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Live));
        var message = renderer.Result(Finished(forfeit: forfeit), language, spoiler: true, MentionPolicy.None, new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
        var e = message.Embed!;

        var visible = string.Join("\n", new[] { message.Content, e.Title, e.Footer, StripSpoilers(e.Description!) }
            .Concat(e.Fields.Select(f => f.Name))
            .Concat(e.Fields.Select(f => StripSpoilers(f.Value))));
        // Timestamps like <t:1790000000:R> are not scores; remove them before looking for digits.
        visible = TimestampPattern().Replace(visible, "");
        visible.Should().NotMatchRegex(@"\b(2|1)\s*[–-]\s*(1|2)\b");
        visible.Should().NotContain("13", "map scores must stay hidden");
        visible.Should().NotContain(Localizer.Get(language, "esports.result.winner", "").Split('*')[0].Trim());
        e.Color.Should().Be(NotificationRenderer.ResultColor, "colour must not depend on the winner");
        e.Title.Should().Contain("Alpha Squad").And.Contain("Bravo Crew");
        e.Title!.IndexOf("Alpha Squad", StringComparison.Ordinal).Should().BeLessThan(e.Title.IndexOf("Bravo Crew", StringComparison.Ordinal), "source order, never winner-first");
    }

    [Fact]
    public void Non_spoiler_result_shows_score_winner_and_maps()
    {
        var renderer = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Live));
        var e = renderer.Result(Finished(), "en", spoiler: false, MentionPolicy.None, DateTimeOffset.UnixEpoch).Embed!;
        e.Description.Should().Contain("**2** – **1**").And.Contain("Winner: **Alpha Squad**");
        e.Fields.Should().Contain(f => f.Value.Contains("1. Mirage: 13–7", StringComparison.Ordinal));
    }

    [Fact]
    public void Incomplete_maps_are_called_out_instead_of_filled_in()
    {
        var match = Finished() with { Maps = [new MapGame(1, "Mirage", GameStatus.Played, 13, 7, 0)] };
        var renderer = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Live));
        renderer.Result(match, "en", false, MentionPolicy.None, DateTimeOffset.UnixEpoch).Embed!.Description
            .Should().Contain("Map details are incomplete");
    }

    [Fact]
    public void Demo_mode_labels_every_notification()
    {
        var renderer = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Fixture));
        var msg = renderer.Result(Finished(), "tr", false, MentionPolicy.None, DateTimeOffset.UnixEpoch);
        msg.Embed!.Title.Should().StartWith("[TEST/DEMO]");
        msg.Embed.Footer.Should().Contain("TEST/DEMO");
    }

    [Fact]
    public void Demo_messages_link_nowhere_and_do_not_claim_a_real_source()
    {
        var match = Finished() with { Status = MatchStatus.Scheduled, SourceUrl = "https://liquipedia.net/counterstrike/X", Streams = [new StreamLink("twitch", "https://www.twitch.tv/somebody")] };
        var demo = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Fixture)).Reminder(match, "tr", MentionPolicy.None, DateTimeOffset.UnixEpoch, null).Embed!;
        demo.Url.Should().BeNull();
        demo.Fields.Should().NotContain(f => f.Value.Contains("twitch", StringComparison.OrdinalIgnoreCase));
        demo.Footer.Should().Contain("sentetik").And.NotContain("Kaynak: Liquipedia");

        var live = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Live)).Reminder(match, "tr", MentionPolicy.None, DateTimeOffset.UnixEpoch, null).Embed!;
        live.Url.Should().Be("https://liquipedia.net/counterstrike/X");
        live.Fields.Should().Contain(f => f.Value.Contains("twitch.tv/somebody", StringComparison.Ordinal));
        live.Footer.Should().Contain("Liquipedia (CC BY-SA 3.0)");
    }

    [Fact]
    public void Allowed_mentions_default_is_closed()
    {
        var none = DiscordConversions.ToAllowedMentions(MentionPolicy.None);
        none.AllowedTypes.Should().Be(AllowedMentionTypes.None);
        (none.RoleIds ?? []).Should().BeEmpty();
        (none.UserIds ?? []).Should().BeEmpty();
        none.MentionRepliedUser.Should().BeFalse();
    }

    [Fact]
    public void Only_explicit_roles_are_allowed_and_never_everyone_or_users()
    {
        var allowed = DiscordConversions.ToAllowedMentions(new MentionPolicy([new RoleId(5), new RoleId(5), new RoleId(7)]));
        allowed.AllowedTypes.Should().Be(AllowedMentionTypes.None, "parse:[] — nothing is parsed from text");
        allowed.RoleIds.Should().Equal(5UL, 7UL);
        (allowed.UserIds ?? []).Should().BeEmpty();
    }

    [Fact]
    public void Edits_and_previews_strip_pings()
    {
        var message = new OutgoingMessage("<@&5>", null, new MentionPolicy([new RoleId(5)]));
        message.WithoutPings().Mentions.PingsAnything.Should().BeFalse();
    }

    [Fact]
    public void Provider_text_cannot_inject_mentions_or_break_spoilers()
    {
        var match = Finished(a: "@everyone Zulu", b: "Amber||spoil <@&123>");
        var renderer = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Live));
        var msg = renderer.Result(match, "en", spoiler: true, MentionPolicy.None, DateTimeOffset.UnixEpoch);
        var all = string.Join("\n", new[] { msg.Content, msg.Embed!.Title, msg.Embed.Description }.Concat(msg.Embed.Fields.Select(f => f.Value)));
        DiscordText.RawMentionPattern().IsMatch(all).Should().BeFalse();
        // Exactly one spoiler pair in the description body — the injected "||" was escaped.
        Regex.Count(msg.Embed.Description!, @"(?<!\\)\|\|").Should().Be(2);
        msg.Content.Should().BeNull("no configured roles → no content → nothing can ping");
    }

    [Fact]
    public void Provider_text_cannot_create_clickable_links_or_break_spoilers_with_extra_pipes()
    {
        DiscordText.Untrusted("Stage https://evil.example/x").Should().NotContain("://");
        DiscordText.UntrustedPlain("see http://evil.example").Should().NotContain("://");
        var spoiler = DiscordText.Spoiler("a|||b||||c");
        spoiler[2..^2].Should().NotContain("||");
    }

    [Fact]
    public void Spoiler_result_hides_winner_width_and_map_count()
    {
        var renderer = new NotificationRenderer(Localizer, new EsportsDataMode(ProviderMode.Live));
        var twoOne = renderer.Result(Finished(), "en", spoiler: true, MentionPolicy.None, DateTimeOffset.UnixEpoch).Embed!;
        twoOne.Description.Should().NotContain("Winner");
        twoOne.Fields.Should().NotContain(f => f.Name == "Maps");
        var swapped = Finished() with { WinnerIndex = 1 };
        var other = renderer.Result(swapped, "en", spoiler: true, MentionPolicy.None, DateTimeOffset.UnixEpoch).Embed!;
        other.Description!.Length.Should().Be(twoOne.Description!.Length, "the visible layout must not depend on who won");
    }

    [Theory]
    [InlineData("http://liquipedia.net/counterstrike/Cup", false)]
    [InlineData("https://liquipedia.net/counterstrike/Cup", true)]
    [InlineData("https://www.twitch.tv/x", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://evil.example/liquipedia.net", false)]
    [InlineData("https://user:pw@liquipedia.net/", false)]
    [InlineData("http://127.0.0.1/", false)]
    public void Only_allow_listed_https_links_are_rendered(string url, bool allowed)
    {
        (DiscordText.SafeUrl(url, NotificationRenderer.AllowedLinkHosts) is not null).Should().Be(allowed);
    }

    [Fact]
    public void Embed_limits_are_validated()
    {
        var tooLong = new OutgoingMessage(null, new MessageEmbed(new string('x', 300), null, null, [], null, null, null), MentionPolicy.None);
        DiscordLimits.Validate(tooLong).Should().NotBeEmpty();
    }

    private static string StripSpoilers(string text) => Regex.Replace(text, @"(?<!\\)\|\|.*?(?<!\\)\|\|", "", RegexOptions.Singleline);

    [GeneratedRegex(@"<t:\d+:[A-Za-z]>")]
    private static partial Regex TimestampPattern();
}
