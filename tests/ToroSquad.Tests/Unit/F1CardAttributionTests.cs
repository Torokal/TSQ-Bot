using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord;
using ToroSquad.Modules.Formula1;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Notification and preview cards show no provider attribution (no "Kaynak/Source", no OpenF1/Jolpica); the source stays
/// internal metadata and remains visible in /f1-admin doctor.
/// </summary>
public sealed class F1CardAttributionTests
{
    private static readonly LocalizationCatalog Localizer = new(
    [
        new LocalizationSource(typeof(CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"),
    ]);

    private static readonly string[] Forbidden = ["OpenF1", "Jolpica", "Kaynak", "Kaynaklar", "Source", "Sources"];
    private static readonly DateTimeOffset T = new(2030, 6, 9, 13, 0, 0, TimeSpan.Zero);

    private static Formula1NotificationRenderer Renderer(F1ProviderMode mode) =>
        new(Localizer, new F1DataMode(mode), Options.Create(new Formula1Options()));

    private static F1SessionView View(F1SessionType type) =>
        new(new F1Session(2030, 8, type, T, T.AddHours(2)), "Valley Grand Prix", "Valley Ring", "Otherland", "Valley Town", F1SessionState.Finalised, T, T.AddHours(2),
            T.AddHours(2), null, T, false);

    private static string VisibleText(OutgoingMessage m) => string.Join("\n",
        new[] { m.Content, m.Embed?.Title, m.Embed?.Description, m.Embed?.Footer }
            .Concat(m.Embed?.Fields.SelectMany(f => new[] { f.Name, f.Value }) ?? []).Where(s => s is not null));

    private static void ShouldShowNoSource(OutgoingMessage m, string what)
    {
        var text = VisibleText(m);
        foreach (var word in Forbidden)
            text.Should().NotContainEquivalentOf(word, what);
    }

    /// <summary>Every automatic card kind with the real provider source keys as the planner passes them.</summary>
    private static IEnumerable<(string Name, OutgoingMessage Card)> AllCards(Formula1NotificationRenderer r, string lang)
    {
        var result = F1DemoData.Result(new F1Session(2030, 8, F1SessionType.Race, T));
        var cached = new F1CachedResult(result, T.AddHours(2), T.AddHours(2));
        var drivers = F1DemoData.DriverStandings(2030);
        var teams = F1DemoData.ConstructorStandings(2030);
        var incident = new F1RaceControlIncident("openf1", "1", F1IncidentKind.SafetyCarDeployed, T.AddMinutes(40), 12, null, null, null);
        foreach (var type in Enum.GetValues<F1SessionType>().Where(t => t != F1SessionType.Unknown))
            yield return ("started " + type, r.Started(View(type), lang, MentionPolicy.None, "f1.source.openf1"));
        yield return ("result", r.Result(View(F1SessionType.Practice1), cached, new(F1StandingsSection.None, null, null, null), false, lang, MentionPolicy.None, "f1.source.openf1", 10));
        foreach (var section in new[] { F1StandingsSection.Pending, F1StandingsSection.NotUpdated })
            yield return ("result " + section, r.Result(View(F1SessionType.Race), cached, new(section, null, null, null), false, lang, MentionPolicy.None, "f1.source.openf1", 10));
        yield return ("result + standings", r.Result(View(F1SessionType.Race), cached, new(F1StandingsSection.Attached, drivers, teams, "f1.source.jolpica"), false, lang,
            MentionPolicy.None, "f1.source.openf1", 10));
        yield return ("weekend", r.WeekendSchedule([View(F1SessionType.Practice1), View(F1SessionType.Race)], lang, "f1.source.jolpica"));
        yield return ("reminder", r.RaceReminder(View(F1SessionType.Race), lang, "f1.source.jolpica"));
        yield return ("safety car", r.SafetyCar(View(F1SessionType.Race), incident, lang, "f1.source.openf1"));
        yield return ("red flag", r.RedFlag(View(F1SessionType.Race), incident with { Kind = F1IncidentKind.RedFlag }, lang, "f1.source.openf1"));
        yield return ("dsq", r.Disqualification(View(F1SessionType.Race), incident with { Kind = F1IncidentKind.Disqualified, DriverNumber = 44 }, "Test Driver", lang,
            "f1.source.openf1"));
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void Live_notification_cards_show_no_provider_attribution(string lang)
    {
        foreach (var (name, card) in AllCards(Renderer(F1ProviderMode.Live), lang))
        {
            ShouldShowNoSource(card, name);
            card.Embed!.Footer.Should().BeNull(name);
        }
    }

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void Demo_and_preview_cards_keep_the_test_demo_label_but_show_no_source(string lang)
    {
        var demo = Renderer(F1ProviderMode.Fixture);
        foreach (var (name, card) in AllCards(demo, lang))
        {
            ShouldShowNoSource(card, name);
            card.Embed!.Footer.Should().Be(Localizer.Get(lang, "f1.demo_footer"), name);
        }

        foreach (var kind in Enum.GetValues<F1PreviewKind>())
        {
            var preview = F1DemoData.Card(demo, kind, spoiler: false, lang, T, 10);
            ShouldShowNoSource(preview, "preview " + kind);
            preview.Embed!.Footer.Should().Contain("TEST/DEMO");
        }
    }

    [Fact]
    public async Task Doctor_still_names_the_providers()
    {
        var (host, _, _) = await RaceWeekendAsync();
        await using var _ = host;
        await StepAsync(host, TimeSpan.Zero);

        var (auth, checks) = await host.InScopeAsync(sp => sp.GetRequiredService<Formula1Doctor>().RunAsync(TestHost.Admin(Guild), CancellationToken.None));

        auth.Succeeded.Should().BeTrue();
        checks.SelectMany(c => c.Args).Select(a => a?.ToString()).Should().Contain("f1.source.jolpica");
        Localizer.Get("tr", "f1.source.jolpica").Should().Contain("Jolpica");
        Localizer.Get("tr", "f1.source.openf1").Should().Contain("OpenF1");
    }
}
