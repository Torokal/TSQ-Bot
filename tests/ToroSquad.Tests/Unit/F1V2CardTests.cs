using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord;
using ToroSquad.Modules.Formula1;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Tests.Unit;

/// <summary>Low-spam V2 cards: in-house thumbnails, concise content, untrusted text defused, Discord limits.</summary>
public sealed class F1V2CardTests
{
    private static readonly LocalizationCatalog Localizer = new(
    [
        new LocalizationSource(typeof(CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(Formula1Module).Assembly, "ToroSquad.Modules.Formula1.Localization"),
    ]);

    private static readonly DateTimeOffset T = new(2030, 6, 9, 13, 0, 0, TimeSpan.Zero);

    private static Formula1NotificationRenderer Renderer(string? assetBase = null) =>
        new(Localizer, new F1DataMode(F1ProviderMode.Live), Options.Create(assetBase is null ? new Formula1Options() : new Formula1Options { AssetBaseUrl = assetBase }));

    private static F1SessionView View(F1SessionType type, DateTimeOffset? start = null) =>
        new(new F1Session(2030, 8, type, start ?? T), "Valley Grand Prix", "Valley Ring", "Otherland", "Valley Town", F1SessionState.Started, T, null, null, null, T, false);

    private static F1RaceControlIncident Incident(F1IncidentKind kind, int? driver = null, string? reason = null) =>
        new("openf1", "1", kind, T.AddMinutes(40), 12, driver, driver is null ? null : "ABC", reason);

    [Fact]
    public void Each_card_selects_its_own_in_house_visual()
    {
        var r = Renderer();
        const string Base = "https://raw.githubusercontent.com/Torokal/TSQ-Bot/main/assets/formula1/";

        r.Started(View(F1SessionType.Race), "tr", MentionPolicy.None, "f1.source.openf1").Embed!.ThumbnailUrl.Should().Be(Base + "start-lights.png");
        r.SafetyCar(View(F1SessionType.Race), Incident(F1IncidentKind.SafetyCarDeployed), "tr", "f1.source.openf1").Embed!.ThumbnailUrl.Should().Be(Base + "safety-car.png");
        r.RedFlag(View(F1SessionType.Race), Incident(F1IncidentKind.RedFlag), "tr", "f1.source.openf1").Embed!.ThumbnailUrl.Should().Be(Base + "red-flag.png");
        r.WeekendSchedule([View(F1SessionType.Practice1, T.AddDays(-2)), View(F1SessionType.Race)], "tr", "f1.source.jolpica").Embed!.ThumbnailUrl
            .Should().Be(Base + "weekend-schedule.png");
    }

    [Fact]
    public void Visual_assets_exist_in_the_repository()
    {
        var root = CommandManifestTests.RepoRoot();
        foreach (var name in new[] { "start-lights.png", "safety-car.png", "red-flag.png", "weekend-schedule.png" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, "assets", "formula1", name));
            bytes.Take(8).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, name + " is a PNG");
        }
    }

    [Fact]
    public void Thumbnails_can_be_turned_off()
    {
        Renderer(assetBase: "").SafetyCar(View(F1SessionType.Race), Incident(F1IncidentKind.SafetyCarDeployed), "tr", "f1.source.openf1").Embed!.ThumbnailUrl.Should().BeNull();
        new Formula1NotificationRenderer(Localizer, new F1DataMode(F1ProviderMode.Live)).Started(View(F1SessionType.Race), "tr", MentionPolicy.None, "f1.source.openf1")
            .Embed!.ThumbnailUrl.Should().BeNull();
    }

    [Fact]
    public void Incident_cards_are_short_and_state_the_lap()
    {
        var r = Renderer();
        var sc = r.SafetyCar(View(F1SessionType.Race), Incident(F1IncidentKind.SafetyCarDeployed), "tr", "f1.source.openf1");
        var red = r.RedFlag(View(F1SessionType.Race), Incident(F1IncidentKind.RedFlag), "en", "f1.source.openf1");

        sc.Embed!.Description.Should().StartWith("🚗 **SAFETY CAR**").And.Contain("Tur 12");
        red.Embed!.Description.Should().StartWith("🚩 **RED FLAG**").And.Contain("Lap 12");
        new[] { sc, red }.Should().OnlyContain(m => m.Mentions.Roles.Count == 0 && DiscordLimits.Validate(m).Count == 0);
    }

    [Fact]
    public void Disqualification_card_defuses_provider_text()
    {
        var card = Renderer().Disqualification(View(F1SessionType.Race), Incident(F1IncidentKind.Disqualified, 44, "@everyone <@&1> **plank**"), "Lewis <@1> Hamilton",
            "tr", "f1.source.openf1");

        card.Embed!.Description.Should().StartWith("⛔ **DİSKALİFİYE**").And.Contain("#44");
        card.Embed.Description.Should().NotContain("@everyone").And.NotContain("<@&1>").And.NotContain("<@1>");
        card.Embed.ThumbnailUrl.Should().BeNull();
        DiscordLimits.Validate(card).Should().BeEmpty();
    }

    [Fact]
    public void Race_reminder_is_one_short_line_with_the_start_timestamp()
    {
        var card = Renderer().RaceReminder(View(F1SessionType.Race), "tr", "f1.source.jolpica");

        card.Embed!.Description.Should().StartWith("🏁 **Valley Grand Prix 15 dakika sonra başlıyor**").And.Contain("<t:");
        card.Mentions.Roles.Should().BeEmpty();
    }

    [Fact]
    public void Weekend_window_is_the_thursday_of_the_race_week()
    {
        var sunday = new DateTimeOffset(2026, 10, 4, 7, 0, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 9, 26, 11, 0, 0, TimeSpan.Zero);
        var o = new Formula1Options();

        Formula1NotificationPlanner.WeekendScheduleWindow(sunday, o).From.Should().Be(new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero));
        Formula1NotificationPlanner.WeekendScheduleWindow(saturday, o).Should().Be((new DateTimeOffset(2026, 9, 24, 6, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero)));
    }
}
