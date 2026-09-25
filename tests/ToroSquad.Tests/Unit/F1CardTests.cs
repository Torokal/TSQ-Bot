using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord;
using ToroSquad.Modules.Formula1;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Tests.Unit;

/// <summary>Formula 1 cards: honest content, Discord limits, spoiler safety, untrusted provider text, TEST/DEMO labelling.</summary>
public sealed class F1CardTests
{
    private static readonly LocalizationCatalog Localizer = new(
    [
        new LocalizationSource(typeof(CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"),
    ]);

    private static readonly DateTimeOffset T = new(2030, 6, 9, 7, 0, 0, TimeSpan.Zero);
    private static readonly Formula1NotificationRenderer Live = new(Localizer, new F1DataMode(F1ProviderMode.Live));
    private static readonly Formula1NotificationRenderer Demo = new(Localizer, new F1DataMode(F1ProviderMode.Fixture));

    private static F1SessionView View(F1SessionType type, string meeting = "Valley Grand Prix", string circuit = "Valley Ring") =>
        new(new F1Session(2030, 8, type, T, T.AddHours(2)), meeting, circuit, "Otherland", "Valley Town", F1SessionState.Finalised, T, T.AddHours(2), T.AddHours(2), null, T, false);

    private static F1CachedResult Cached(F1SessionResult result) => new(result, T.AddHours(2), T.AddHours(2));

    private static F1DriverResult D(int? pos, int n, string name, F1ResultStatus status = F1ResultStatus.Classified, double? time = null, double? gap = null, int? laps = null) =>
        new(pos, n, name, "C" + n, "Team " + n, status, 50, time, gap, laps, null);

    private static F1SessionResult Race(params F1DriverResult[] rows) => new("2030-08-race", F1SessionType.Race, "openf1", rows);

    private static readonly F1StandingsAttachment NoStandings = new(F1StandingsSection.None, null, null, null);

    [Fact]
    public void Start_card_names_session_circuit_round_and_source_and_pings_only_the_given_role()
    {
        var view = View(F1SessionType.Race) with { State = F1SessionState.Started };
        var message = Live.Started(view, "tr", new MentionPolicy([new RoleId(77)]), "f1.source.openf1");
        message.Embed!.Title.Should().Be("🏎️ Valley Grand Prix — Yarış");
        message.Embed.Description.Should().Contain("🔴 **Yarış başladı**").And.Contain("<t:").And.Contain("Valley Ring · Otherland").And.Contain("Round 8 · 2030");
        message.Embed.Footer.Should().Be("Kaynak: OpenF1");
        message.Content.Should().Be("<@&77>");
        message.Mentions.Roles.Should().Equal(new RoleId(77));
        DiscordLimits.Validate(message).Should().BeEmpty();
    }

    [Fact]
    public void Race_result_lists_the_full_classification_with_medals_statuses_and_only_provider_gaps()
    {
        var result = Race(D(1, 1, "Alex Fast", time: 5455), D(2, 2, "Bo Quick", gap: 9.748), D(3, 3, "Cy Swift", gap: 15.974), D(4, 4, "Di Brisk", laps: 1),
            D(5, 5, "Ed Nimble"), D(null, 6, "Fi Rush", F1ResultStatus.Dnf), D(null, 7, "Gu Dash", F1ResultStatus.Dns), D(null, 8, "Ha Zoom", F1ResultStatus.Dsq));
        var message = Live.Result(View(F1SessionType.Race), Cached(result), NoStandings, spoiler: false, "tr", MentionPolicy.None, "f1.source.openf1", 10);
        var lines = message.Embed!.Description!.Split('\n');
        message.Embed.Title.Should().Be("🏁 Valley Grand Prix — Yarış Sonucu");
        lines.Should().Contain("🥇 1. Alex Fast — Team 1");
        lines.Should().Contain("🥈 2. Bo Quick — Team 2 — +9.748s");
        lines.Should().Contain("🥉 3. Cy Swift — Team 3 — +15.974s");
        lines.Should().Contain("4. Di Brisk — Team 4 — +1 tur");
        lines.Should().Contain("5. Ed Nimble — Team 5", "no gap was supplied, none is invented");
        lines.Should().Contain("`DNF` Fi Rush — Team 6").And.Contain("`DNS` Gu Dash — Team 7").And.Contain("`DSQ` Ha Zoom — Team 8");
        message.Embed.Footer.Should().Be("Kaynak: OpenF1");
        DiscordLimits.Validate(message).Should().BeEmpty();
    }

    [Fact]
    public void Practice_result_shows_best_lap_and_gaps_and_never_a_winner()
    {
        var result = new F1SessionResult("2030-08-fp1", F1SessionType.Practice1, "openf1",
            [D(1, 1, "Alex Fast", time: 91.504), D(2, 2, "Bo Quick", time: 91.958, gap: 0.454), D(3, 3, "Cy Swift")]);
        var message = Live.Result(View(F1SessionType.Practice1), Cached(result), NoStandings, false, "tr", MentionPolicy.None, "f1.source.openf1", 10);
        message.Embed!.Title.Should().Be("⏱️ Valley Grand Prix — 1. Antrenman Sonucu");
        var lines = message.Embed.Description!.Split('\n');
        lines.Should().Contain("1. Alex Fast — Team 1 — 1:31.504").And.Contain("2. Bo Quick — Team 2 — +0.454s").And.Contain("3. Cy Swift — Team 3");
        message.Embed.Description.Should().NotContain("🥇");
        Localizer.Get("tr", "f1.card.result_title_timed").Should().NotContainEquivalentOf("kazan");
    }

    [Fact]
    public void Lap_time_formatting_is_exact()
    {
        Formula1NotificationRenderer.LapTime(91.504).Should().Be("1:31.504");
        Formula1NotificationRenderer.LapTime(59.0005).Should().Be("0:59.001");
        Formula1NotificationRenderer.LapTime(5455.026).Should().Be("1:30:55.026");
        Formula1NotificationRenderer.LapTime(null).Should().BeEmpty();
    }

    [Fact]
    public void Spoiler_mode_hides_classification_and_standings_and_leaks_nothing_through_title_colour_or_thumbnail()
    {
        var a = Race(D(1, 1, "Alex Fast"), D(2, 2, "Bo Quick"));
        var b = Race(D(1, 2, "Bo Quick"), D(2, 1, "Alex Fast"));
        var standings = new F1StandingsAttachment(F1StandingsSection.Attached,
            new F1StandingsSnapshot(F1StandingsKind.Drivers, 2030, 8, "jolpica", [new(1, "fast", "Alex Fast", "FST", "Rapid", 145m, 4)], []),
            new F1StandingsSnapshot(F1StandingsKind.Constructors, 2030, 8, "jolpica", [], [new(1, "rapid", "Rapid Racing", 230m, 5)]), "f1.source.jolpica");
        var ma = Live.Result(View(F1SessionType.Race), Cached(a), standings, spoiler: true, "tr", MentionPolicy.None, "f1.source.openf1", 10);
        var mb = Live.Result(View(F1SessionType.Race), Cached(b), standings, spoiler: true, "tr", MentionPolicy.None, "f1.source.openf1", 10);

        ma.Embed!.Title.Should().Be(mb.Embed!.Title).And.NotContain("Alex").And.NotContain("Bo");
        ma.Embed.Color.Should().Be(mb.Embed.Color);
        ma.Embed.ThumbnailUrl.Should().BeNull();
        var description = ma.Embed.Description!;
        var spoilerStart = description.IndexOf("||", StringComparison.Ordinal);
        spoilerStart.Should().BeGreaterThan(0);
        description[..spoilerStart].Should().NotContain("Alex").And.NotContain("🥇");
        description.Should().EndWith("||");
        ma.Embed.Fields.Should().OnlyContain(f => f.Value.StartsWith("||", StringComparison.Ordinal) && f.Value.EndsWith("||", StringComparison.Ordinal));
        ma.Embed.Fields.Select(f => f.Name).Should().Equal("🏆 Sürücüler", "🏭 Takımlar");
    }

    [Fact]
    public void Provider_text_cannot_ping_break_markdown_or_create_links()
    {
        var hostile = Race(D(1, 1, "@everyone **pwn** ||x|| [click](https://evil.example) <@&123>"), D(2, 2, "@here"));
        var view = View(F1SessionType.Race, meeting: "@everyone GP https://evil.example", circuit: "<@&1> `code`");
        var message = Live.Result(view, Cached(hostile), NoStandings, spoiler: true, "tr", MentionPolicy.None, "f1.source.openf1", 10);
        var visible = message.Embed!.Title + "\n" + message.Embed.Description;
        DiscordText.RawMentionPattern().IsMatch(visible).Should().BeFalse();
        visible.Should().NotContain("://").And.NotContain("**pwn**").And.NotContain("[click](");
        message.Embed.Description!.Split("||", StringSplitOptions.None).Length.Should().Be(3, "exactly one spoiler: provider text cannot close it early");
        message.Mentions.Should().Be(MentionPolicy.None);
        message.Embed.Url.Should().BeNull("provider URLs are never emitted");
    }

    [Fact]
    public void A_full_grid_with_standings_fits_discord_limits_and_huge_input_is_truncated_honestly()
    {
        var grid = Race(Enumerable.Range(1, 22).Select(i => D(i, i, "Driver With A Fairly Long Name " + i, gap: i * 1.234)).ToArray());
        var table = new F1StandingsSnapshot(F1StandingsKind.Drivers, 2030, 8, "jolpica",
            Enumerable.Range(1, 22).Select(i => new F1DriverStanding(i, "d" + i, "Driver With A Fairly Long Name " + i, null, "Team", 400 - i, 0)).ToList(), []);
        var teams = new F1StandingsSnapshot(F1StandingsKind.Constructors, 2030, 8, "jolpica", [],
            Enumerable.Range(1, 11).Select(i => new F1ConstructorStanding(i, "t" + i, "Constructor Name " + i, 600 - i, 0)).ToList());
        var message = Live.Result(View(F1SessionType.Race), Cached(grid), new F1StandingsAttachment(F1StandingsSection.Attached, table, teams, "f1.source.jolpica"),
            false, "tr", MentionPolicy.None, "f1.source.openf1", 10);
        DiscordLimits.Validate(message).Should().BeEmpty();
        message.Embed!.Description.Should().Contain("22. Driver With A Fairly Long Name 22", "the full classification is preferred when it fits");
        message.Embed.Fields[0].Value.Should().Contain("/f1 standings drivers", "top-N with a pointer to the full table");
        message.Embed.Fields[1].Value.Should().Contain("/f1 standings constructors");

        var huge = Race(Enumerable.Range(1, 120).Select(i => D(i, i, new string('x', 60) + i)).ToArray());
        var cut = Live.Result(View(F1SessionType.Race), Cached(huge), NoStandings, true, "tr", MentionPolicy.None, "f1.source.openf1", 10);
        DiscordLimits.Validate(cut).Should().BeEmpty();
        cut.Embed!.Description.Should().Contain("kısaltıldı");
        cut.Embed.Description!.Split("||", StringSplitOptions.None).Length.Should().Be(3, "truncation never leaves an unclosed spoiler");
    }

    [Fact]
    public void Standings_sections_render_pending_not_updated_and_attached_states()
    {
        var result = Cached(Race(D(1, 1, "A"), D(2, 2, "B")));
        var pending = Live.Result(View(F1SessionType.Race), result, new(F1StandingsSection.Pending, null, null, null), false, "en", MentionPolicy.None, "f1.source.openf1", 10);
        pending.Embed!.Fields.Single().Value.Should().Contain("Waiting for the standings update");
        var stale = Live.Result(View(F1SessionType.Race), result, new(F1StandingsSection.NotUpdated, null, null, null), false, "en", MentionPolicy.None, "f1.source.openf1", 10);
        stale.Embed!.Fields.Single().Value.Should().Contain("/f1 standings drivers");
        stale.Embed.Footer.Should().Be("Source: OpenF1", "the standings source is only credited when its data is shown");
    }

    [Fact]
    public void Demo_cards_are_labelled_and_name_no_real_source()
    {
        var message = Demo.Started(View(F1SessionType.Race) with { State = F1SessionState.Started }, "tr", MentionPolicy.None, "f1.source.openf1");
        message.Embed!.Title.Should().StartWith("[TEST/DEMO]");
        message.Embed.Footer.Should().Contain("TEST/DEMO").And.NotContain("OpenF1").And.NotContain("Jolpica");
        foreach (var kind in Enum.GetValues<F1PreviewKind>())
        {
            var card = F1DemoData.Card(Demo, kind, spoiler: false, "tr", T, 10);
            card.Embed!.Title.Should().StartWith("[TEST/DEMO]").And.Contain(F1DemoData.MeetingName);
            card.Mentions.Should().Be(MentionPolicy.None);
            DiscordLimits.Validate(card).Should().BeEmpty();
        }

        var act = () => F1DemoData.Card(Live, F1PreviewKind.RaceStart, false, "tr", T, 10);
        act.Should().Throw<InvalidOperationException>("demo data is never rendered as real");
    }
}
