using System.Text.RegularExpressions;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord;
using ToroSquad.Modules.Formula1;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Commands;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Tests.Unit;

/// <summary>/f1 command answers: public vs private, the season overview, round detail, season choice, no source names.</summary>
public sealed partial class F1CommandResponsesTests
{
    private static readonly LocalizationCatalog Localizer = new(
    [
        new LocalizationSource(typeof(CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"),
    ]);

    private static readonly string[] Forbidden = ["OpenF1", "Jolpica", "Kaynak", "Kaynaklar", "Source", "Sources"];
    private static readonly F1Sources Sources = new("f1.source.jolpica", "f1.source.openf1", "f1.source.openf1", "f1.source.jolpica", LifecycleConfigured: true);

    /// <summary>Now: Saturday of round 3's weekend.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 21, 12, 0, 0, TimeSpan.Zero);

    private static F1SessionView S(int round, F1SessionType type, DateTimeOffset start, F1SessionState state = F1SessionState.Scheduled, int season = 2026, string? meeting = null) =>
        new(new F1Session(season, round, type, start), meeting ?? $"Country{round} Grand Prix", "Circuit " + round, "Land " + round, "Town " + round, state,
            state is F1SessionState.Started or F1SessionState.Suspended or F1SessionState.FinishedPendingResults or F1SessionState.Finalised ? start : null,
            null, state == F1SessionState.Finalised ? start.AddHours(2) : null, null, state == F1SessionState.Scheduled ? null : start, false);

    /// <summary>Round 1 done (race finalised), round 2 sprint weekend done by the clock only, round 3 now, rounds 4 and 5 later.</summary>
    private static List<F1SessionView> Season(F1SessionState round3Qualifying = F1SessionState.Scheduled)
    {
        var d = new DateTimeOffset(2026, 3, 6, 0, 0, 0, TimeSpan.Zero);
        return
        [
            S(1, F1SessionType.Practice1, d.AddHours(11)), S(1, F1SessionType.Qualifying, d.AddDays(1).AddHours(15)),
            S(1, F1SessionType.Race, d.AddDays(2).AddHours(5), F1SessionState.Finalised),
            S(2, F1SessionType.Practice1, d.AddDays(7).AddHours(3)), S(2, F1SessionType.SprintQualifying, d.AddDays(7).AddHours(7)),
            S(2, F1SessionType.Sprint, d.AddDays(8).AddHours(3)), S(2, F1SessionType.Qualifying, d.AddDays(8).AddHours(7)), S(2, F1SessionType.Race, d.AddDays(9).AddHours(7)),
            S(3, F1SessionType.Practice1, d.AddDays(14).AddHours(3)), S(3, F1SessionType.Qualifying, d.AddDays(15).AddHours(11), round3Qualifying),
            S(3, F1SessionType.Race, d.AddDays(16).AddHours(6)),
            S(4, F1SessionType.Practice1, d.AddDays(28).AddHours(3)), S(4, F1SessionType.Race, d.AddDays(30).AddHours(6)),
            S(5, F1SessionType.Race, d.AddDays(44).AddHours(13), meeting: "Emilia-Romagna Grand Prix"),
        ];
    }

    private static F1CommandResponses Responses(IReadOnlyList<F1SessionView> sessions, F1Sources? sources = null, bool lifecycleHealthy = true, bool standings = true)
    {
        var cache = new Formula1Cache();
        cache.SetSchedule(new F1Feed<IReadOnlyList<F1SeasonSchedule>>([], Now.AddMinutes(-30), F1ProviderOutcome.Success, null, Now.AddMinutes(-30), 0));
        cache.SetSessions(sessions, new Dictionary<string, F1CachedResult>(StringComparer.Ordinal)
        {
            [sessions[2].Session.Key] = new(F1DemoData.Result(sessions[2].Session), Now.AddDays(-1), Now.AddDays(-12)),
        });
        if (standings)
        {
            cache.RestoreStandings(F1DemoData.DriverStandings(2026), Now.AddHours(-2));
            cache.RestoreStandings(F1DemoData.ConstructorStandings(2026), Now.AddHours(-2));
        }

        if (lifecycleHealthy)
            cache.RecordLifecycleAttempt(F1ProviderOutcome.Success, null, Now.AddMinutes(-1));
        var renderer = new Formula1NotificationRenderer(Localizer, new F1DataMode(F1ProviderMode.Live));
        return new F1CommandResponses(cache, renderer, Localizer, sources ?? Sources, new Formula1Options());
    }

    private static string Visible(F1CommandReply r) => string.Join("\n",
        new[] { r.Text, r.Embed?.Title, r.Embed?.Description, r.Embed?.Footer }.Concat(r.Embed?.Fields.SelectMany(f => new[] { f.Name, f.Value }) ?? []).Where(x => x is not null));

    [GeneratedRegex(@"`(\d\d)`")]
    private static partial Regex RoundTag();

    // ---------------------------------------------------------------- visibility

    [Fact]
    public void Next_schedule_standings_and_now_answer_publicly_results_stays_private()
    {
        var r = Responses(Season());

        r.Next("tr", Now).Ephemeral.Should().BeFalse();
        r.Schedule("tr", Now, null).Ephemeral.Should().BeFalse();
        r.Schedule("tr", Now, 3).Ephemeral.Should().BeFalse();
        r.Standings("tr", Now, F1StandingsKind.Drivers).Ephemeral.Should().BeFalse();
        r.Standings("tr", Now, F1StandingsKind.Constructors).Ephemeral.Should().BeFalse();
        r.Now("tr", Now).Ephemeral.Should().BeFalse();
        r.Results("tr", Now, null, spoiler: false).Ephemeral.Should().BeTrue("results can spoil a race for the whole channel");
        r.Results("tr", Now, F1SessionType.Sprint, spoiler: true).Ephemeral.Should().BeTrue();
    }

    [Fact]
    public void Lookup_errors_stay_private()
    {
        Responses(Season()).Schedule("tr", Now, 17).Should().Match<F1CommandReply>(x => x.Ephemeral && x.Text!.Contains("17"));
        Responses(Season(), standings: false).Standings("tr", Now, F1StandingsKind.Drivers).Ephemeral.Should().BeTrue();
    }

    [Fact]
    public void Admin_answers_are_never_public_and_only_the_intended_f1_commands_are()
    {
        var root = CommandManifestTests.RepoRoot();
        var admin = File.ReadAllText(Path.Combine(root, "src", "ToroSquad.Modules.Formula1", "Commands", "Formula1AdminOperations.cs"));
        admin.Should().NotContain("ephemeral: false").And.NotContain("F1CommandReply").And.NotContain("Public(");

        var commands = File.ReadAllText(Path.Combine(root, "src", "ToroSquad.Modules.Formula1", "Commands", "Formula1Commands.cs"));
        Regex.Count(commands, "DeferEphemeralAsync\\(\\)").Should().Be(1, "only /f1 results defers privately");
        commands.Should().Contain("await DeferEphemeralAsync(); // results stay private");
    }

    // ---------------------------------------------------------------- season overview

    [Fact]
    public void Schedule_without_round_shows_the_whole_season_in_round_order()
    {
        var reply = Responses(Season()).Schedule("tr", Now, null);
        var text = reply.Embed!.Description!;

        reply.Embed.Title.Should().Be("🏎️ 2026 Formula 1 Takvimi");
        RoundTag().Matches(text).Select(m => m.Groups[1].Value).Should().Equal("01", "02", "03", "04", "05");
        text.Should().Contain("`02` Country2 GP · ⚡ Sprint").And.Contain("`05` Emilia-Romagna GP");
        text.Should().NotContain("`01` Country1 GP · ⚡", "a standard weekend is not marked as sprint");
        text.Should().NotContain("Antrenman").And.NotContain("Sıralama", "the overview lists weekends, not every session");
        var race4 = Season().Single(s => s.Session.Round == 4 && s.Session.Type == F1SessionType.Race).Session.ScheduledStartUtc;
        text.Should().Contain(DiscordText.Timestamp(race4, 'D'));
        text.Should().Contain("/f1 schedule round:");
        DiscordLimits.Validate(new OutgoingMessage(null, reply.Embed, MentionPolicy.None)).Should().BeEmpty();
    }

    [Fact]
    public void Season_marks_past_current_and_future_and_the_clock_never_makes_live()
    {
        // Round 3's qualifying time has passed but the lifecycle provider has said nothing: current, not live.
        var text = Responses(Season()).Schedule("tr", Now, null).Embed!.Description!;
        text.Should().Contain("✅ `01`").And.Contain("✅ `02`", "past by the schedule (display only)");
        text.Should().Contain("➡️ `03`").And.Contain("▫️ `04`").And.Contain("▫️ `05`");
        text.Split('\n').Should().NotContain(l => l.StartsWith("🔴", StringComparison.Ordinal));

        // The provider reports round 3's qualifying running: now it is live.
        var live = Responses(Season(F1SessionState.Started)).Schedule("tr", Now, null).Embed!.Description!;
        live.Should().Contain("🔴 `03`");
    }

    [Fact]
    public void Season_is_taken_from_the_cache_not_hard_coded()
    {
        var future = Season().Select(v => v with { Session = v.Session with { Season = 2031, ScheduledStartUtc = v.Session.ScheduledStartUtc.AddYears(5) } }).ToList();
        Responses(future).Schedule("en", Now, null).Embed!.Title.Should().Be("🏎️ 2031 Formula 1 Calendar", "the only season in the cache");

        var sessions = Season();
        F1CommandViews.CurrentSeason(sessions, Now).Should().Be(2026, "the season of the current weekend");
        F1CommandViews.CurrentSeason([], Now).Should().Be(2026, "the current year only as a last resort");
        F1CommandViews.CurrentSeason(sessions, Now.AddYears(1)).Should().Be(2026, "no upcoming weekend: the newest season in the cache");
    }

    [Fact]
    public void A_full_24_round_season_with_long_names_fits_one_embed()
    {
        var start = new DateTimeOffset(2026, 3, 1, 5, 0, 0, TimeSpan.Zero);
        var sessions = Enumerable.Range(1, 24).SelectMany(r => new[]
        {
            S(r, F1SessionType.SprintQualifying, start.AddDays((r * 7) - 1), meeting: new string('X', 60) + " Grand Prix"),
            S(r, F1SessionType.Race, start.AddDays(r * 7), meeting: new string('X', 60) + " Grand Prix"),
        }).ToList();

        var reply = Responses(sessions).Schedule("tr", Now, null);

        RoundTag().Matches(reply.Embed!.Description!).Should().HaveCount(24);
        DiscordLimits.Validate(new OutgoingMessage(null, reply.Embed, MentionPolicy.None)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- round detail

    [Fact]
    public void Schedule_with_round_shows_only_that_weekend_in_detail()
    {
        var reply = Responses(Season()).Schedule("tr", Now, 2);
        var text = reply.Embed!.Description!;

        reply.Embed.Title.Should().Be("📅 Country2 Grand Prix — Program");
        text.Should().Contain("Sprint hafta sonu");
        new[] { "1. Antrenman", "Sprint Sıralaması", "Sprint", "Sıralama", "Yarış" }
            .Select(s => text.IndexOf("**" + s + "**", StringComparison.Ordinal)).Should().BeInAscendingOrder().And.NotContain(-1);
        text.Should().NotContain("Country1").And.NotContain("Country3");
    }

    [Fact]
    public void Round_detail_shows_provider_lifecycle_states_only()
    {
        var text = Responses(Season(F1SessionState.Started)).Schedule("tr", Now, 3).Embed!.Description!;

        text.Should().Contain("**Sıralama**").And.Contain("🔴");
        Responses(Season()).Schedule("tr", Now, 3).Embed!.Description.Should().NotContain("🔴", "a passed scheduled time is not a start");
    }

    // ---------------------------------------------------------------- now

    [Fact]
    public void Now_is_live_only_from_the_lifecycle_provider()
    {
        Responses(Season(F1SessionState.Started)).Now("tr", Now).Embed!.Description.Should().Contain("Seans devam ediyor");

        var quiet = Responses(Season()).Now("tr", Now);
        quiet.Embed.Should().BeNull();
        quiet.Text.Should().NotContain("devam ediyor");

        var unknown = Responses(Season(), lifecycleHealthy: false).Now("tr", Now);
        unknown.Should().Match<F1CommandReply>(x => !x.Ephemeral && x.Text!.Contains("bilinmiyor"));
    }

    // ---------------------------------------------------------------- source UI

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void No_f1_command_answer_names_a_provider(string lang)
    {
        foreach (var healthy in new[] { true, false })
        {
            var r = Responses(Season(F1SessionState.Started), lifecycleHealthy: healthy);
            var quiet = Responses(Season(), lifecycleHealthy: healthy);
            foreach (var reply in new[]
                     {
                         r.Next(lang, Now), r.Schedule(lang, Now, null), r.Schedule(lang, Now, 2), r.Standings(lang, Now, F1StandingsKind.Drivers),
                         r.Standings(lang, Now, F1StandingsKind.Constructors), r.Now(lang, Now), quiet.Now(lang, Now), r.Results(lang, Now, null, false),
                     })
            {
                foreach (var word in Forbidden)
                    Visible(reply).Should().NotContainEquivalentOf(word);
                if (reply.Embed is { } e)
                    e.Footer.Should().BeNull();
            }
        }

        Sources.LifecycleKey.Should().Be("f1.source.openf1", "provider metadata stays intact internally");
    }
}
