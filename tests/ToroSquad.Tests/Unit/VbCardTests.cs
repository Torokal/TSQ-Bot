using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Tests.Unit;

/// <summary>Structural card tests: wording, perspective, flags/logo policy, attribution, demo labelling, Discord limits.</summary>
public sealed class VbCardTests
{
    private static readonly LocalizationCatalog Catalog = new(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(ToroSquad.Modules.Volleyball.VolleyballModule).Assembly, "ToroSquad.Modules.Volleyball.Localization"),
    ]);

    private static readonly DateTimeOffset Start = new(2026, 7, 26, 11, 30, 0, TimeSpan.Zero);

    private static VolleyballNotificationRenderer Renderer(bool demo = false, params string[] logoHosts) =>
        new(Catalog, new VbDataMode(demo ? VbProviderMode.Fixture : VbProviderMode.Live), new VbLogoHosts(logoHosts));

    private static VbMatchView View(FollowedSide side = FollowedSide.Home, IReadOnlyList<SetResult>? sets = null, string opponent = "Italy", string? opponentCode = "ITA",
        string? homeLogo = null, IReadOnlyList<string>? broadcasts = null)
    {
        sets ??= [];
        var home = sets.Count(s => s.HomeWon);
        return side == FollowedSide.Home
            ? new VbMatchView("fivb:1", "fivb", "Women's Volleyball Nations League 2026", null, "Final", Start, side, "Türkiye", "TUR", opponent, opponentCode, homeLogo, null,
                "East Asian Games Dome", "Macao", broadcasts ?? [], VolleyballMatchStatus.Live, home, sets.Count - home, sets, null, null, null, true, false, false, false, Start)
            : new VbMatchView("fivb:1", "fivb", "Women's Volleyball Nations League 2026", null, null, Start, side, opponent, opponentCode, "Türkiye", "TUR", null, homeLogo,
                null, null, broadcasts ?? [], VolleyballMatchStatus.Live, home, sets.Count - home, sets, null, null, null, true, false, false, false, Start);
    }

    private static void Valid(OutgoingMessage m) => DiscordLimits.Validate(m).Should().BeEmpty();

    [Fact]
    public void Reminder_card_shows_teams_competition_time_and_venue_with_discord_timestamps()
    {
        var m = Renderer().Reminder(View(), "tr", MentionPolicy.None, "vb.source.fivb");
        Valid(m);
        m.Embed!.Title.Should().Be("🇹🇷 Türkiye vs İtalya 🇮🇹");
        m.Embed.Description.Should().Be(
            "⏳ **Maç <t:1785065400:R> başlıyor**\n🕒 <t:1785065400:F>\n🏆 Women's Volleyball Nations League 2026 · Final\n📍 East Asian Games Dome, Macao");
        m.Embed.Footer.Should().Be("Kaynak: FIVB");
        m.Embed.ThumbnailUrl.Should().BeNull("no provider logo: flags only");
        m.Embed.Color.Should().Be(VolleyballNotificationRenderer.ReminderColor);
    }

    [Fact]
    public void Started_card_is_one_status_line_plus_context()
    {
        var m = Renderer().Started(View(), Start.AddMinutes(3), "tr", MentionPolicy.None, "vb.source.fivb");
        Valid(m);
        m.Embed!.Description.Should().StartWith("🔴 **Maç başladı** · <t:1785065580:R>");
        m.Content.Should().BeNull("no ping, no content");
    }

    [Fact]
    public void Won_set_card_says_the_set_is_turkeys_and_lists_the_sets()
    {
        var sets = new List<SetResult> { new(1, 25, 21), new(2, 22, 25), new(3, 25, 19) };
        var m = Renderer().SetFinished(View(sets: sets), 3, Start.AddMinutes(80), "tr", MentionPolicy.None, "vb.source.fivb");
        Valid(m);
        m.Embed!.Title.Should().Be("🇹🇷 Türkiye 2-1 İtalya 🇮🇹");
        m.Embed.Description.Should().StartWith("🇹🇷 **SET TÜRKİYE'NİN!** · 3. set")
            .And.Contain("**1. Set: 25-21**\n2. Set: 22-25\n**3. Set: 25-19**");
    }

    [Fact]
    public void Lost_set_card_is_neutral_and_names_the_opponent()
    {
        var sets = new List<SetResult> { new(1, 25, 21), new(2, 22, 25) };
        var m = Renderer().SetFinished(View(sets: sets), 2, Start.AddMinutes(50), "tr", MentionPolicy.None, "vb.source.fivb");
        m.Embed!.Description.Should().StartWith("2. seti **İtalya** aldı").And.NotContain("TÜRKİYE'NİN");
        m.Embed.Color.Should().Be(VolleyballNotificationRenderer.NeutralResultColor);
    }

    [Fact]
    public void A_set_card_shows_the_score_as_it_was_after_that_set()
    {
        // Rendered later (the planner re-renders within the fresh window): set 2's card still says 1-1, not 2-1.
        var sets = new List<SetResult> { new(1, 25, 21), new(2, 22, 25), new(3, 25, 19) };
        var m = Renderer().SetFinished(View(sets: sets), 2, Start.AddMinutes(50), "tr", MentionPolicy.None, "vb.source.fivb");
        m.Embed!.Title.Should().Be("🇹🇷 Türkiye 1-1 İtalya 🇮🇹");
        m.Embed.Description.Should().NotContain("3. Set");
    }

    [Fact]
    public void Away_matches_are_shown_from_turkeys_perspective()
    {
        // Provider order: Italy (home) 2-3 Türkiye (away).
        var sets = new List<SetResult> { new(1, 25, 16), new(2, 22, 25), new(3, 25, 12), new(4, 21, 25), new(5, 10, 15) };
        var m = Renderer().Final(View(FollowedSide.Away, sets), Start.AddMinutes(130), "tr", MentionPolicy.None, "vb.source.fivb");
        Valid(m);
        m.Embed!.Title.Should().Be("🇹🇷 Türkiye 3-2 İtalya 🇮🇹");
        m.Embed.Description.Should().StartWith("🏆 **Filenin Sultanları kazandı!**").And.Contain("1. Set: 16-25").And.Contain("**5. Set: 15-10**");
        m.Embed.Color.Should().Be(VolleyballNotificationRenderer.WinColor);
    }

    [Fact]
    public void Final_loss_names_the_winner_without_victory_wording()
    {
        var sets = new List<SetResult> { new(1, 20, 25), new(2, 25, 23), new(3, 19, 25), new(4, 22, 25) };
        var m = Renderer().Final(View(sets: sets, opponent: "Serbia", opponentCode: "SRB"), Start.AddHours(2), "tr", MentionPolicy.None, "vb.source.fivb");
        m.Embed!.Title.Should().Be("🇹🇷 Türkiye 1-3 Sırbistan 🇷🇸");
        m.Embed.Description.Should().StartWith("Maç sona erdi · **Sırbistan** kazandı").And.NotContain("Sultanları kazandı");
    }

    [Fact]
    public void Postponed_and_cancelled_cards_never_ping_and_show_the_planned_time()
    {
        var postponed = Renderer().Postponed(View(), Start.AddHours(-3), "tr", "vb.source.fivb");
        postponed.Embed!.Description.Should().Contain("ertelendi").And.Contain("Planlanan saat: <t:1785065400:F>");
        postponed.Mentions.Should().Be(MentionPolicy.None);
        var cancelled = Renderer().Cancelled(View(), Start.AddHours(-3), "tr", "vb.source.fivb");
        cancelled.Embed!.Description.Should().Contain("iptal edildi");
        cancelled.Mentions.Should().Be(MentionPolicy.None);
        cancelled.Embed.Color.Should().Be(VolleyballNotificationRenderer.CancelledColor);
    }

    [Fact]
    public void An_allow_listed_provider_logo_becomes_the_thumbnail_and_anything_else_is_dropped()
    {
        var withLogo = Renderer(false, "img.example-provider.test").Reminder(View(homeLogo: "https://cdn.img.example-provider.test/teams/tur.png"), "tr", MentionPolicy.None, "vb.source.fivb");
        withLogo.Embed!.ThumbnailUrl.Should().Be("https://cdn.img.example-provider.test/teams/tur.png");
        Valid(withLogo);

        Renderer(false, "img.example-provider.test").Reminder(View(homeLogo: "https://random-cdn.test/tur.png"), "tr", MentionPolicy.None, "vb.source.fivb")
            .Embed!.ThumbnailUrl.Should().BeNull("hosts outside the provider allow-list are never hotlinked");
        Renderer().Reminder(View(homeLogo: "https://cdn.img.example-provider.test/teams/tur.png"), "tr", MentionPolicy.None, "vb.source.fivb")
            .Embed!.ThumbnailUrl.Should().BeNull("no allow-listed host configured = flags only");
        Renderer(true, "img.example-provider.test").Reminder(View(homeLogo: "https://cdn.img.example-provider.test/teams/tur.png"), "tr", MentionPolicy.None, "vb.source.demo")
            .Embed!.ThumbnailUrl.Should().BeNull("demo cards never carry a real logo");
    }

    [Fact]
    public void Unknown_opponent_codes_fall_back_to_the_untrusted_provider_name_without_a_flag()
    {
        var m = Renderer().Reminder(View(opponent: "@everyone <@&1> **Evil**", opponentCode: null), "tr", MentionPolicy.None, "vb.source.fivb");
        m.Embed!.Title.Should().StartWith("🇹🇷 Türkiye vs @​everyone").And.EndWith("Evil**").And.NotContain("🇮🇹");
        DiscordText.RawMentionPattern().IsMatch(m.Embed.Title!).Should().BeFalse();
    }

    [Fact]
    public void Broadcasts_are_shown_only_when_the_provider_stated_them()
    {
        Renderer().Reminder(View(), "tr", MentionPolicy.None, "vb.source.fivb").Embed!.Description.Should().NotContain("📺");
        Renderer().Reminder(View(broadcasts: ["TRT 1", "TRT Spor Yıldız"]), "tr", MentionPolicy.None, "vb.source.fivb").Embed!.Description.Should().Contain("📺 TRT 1 · TRT Spor Yıldız");
    }

    [Fact]
    public void Demo_cards_are_labelled_and_name_no_real_source()
    {
        foreach (var kind in Enum.GetValues<VbPreviewKind>())
        {
            var m = VbDemoData.Card(Renderer(demo: true), kind, "tr", Start);
            Valid(m);
            m.Embed!.Title.Should().StartWith("[TEST/DEMO] ");
            m.Embed.Footer.Should().Be("TEST/DEMO · sentetik veri, gerçek bir maç değildir");
            m.Embed.Footer.Should().NotContain("FIVB");
            m.Mentions.Should().Be(MentionPolicy.None);
        }

        var act = () => VbDemoData.Card(Renderer(demo: false), VbPreviewKind.FinalWon, "tr", Start);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Pings_only_go_to_the_configured_role_and_never_to_everyone()
    {
        var guild = new ToroSquad.Core.GuildId(55);
        var config = new ToroSquad.Modules.Volleyball.Persistence.VolleyballGuildConfigEntity { PingRoleId = 66, PingOnReminder = true, PingOnFinal = false };
        VolleyballNotificationPlanner.Pings(config, guild, reminder: true).Roles.Should().Equal(new ToroSquad.Core.RoleId(66));
        VolleyballNotificationPlanner.Pings(config, guild, reminder: false).Should().Be(MentionPolicy.None);
        config.PingRoleId = 55; // == guild id == @everyone
        VolleyballNotificationPlanner.Pings(config, guild, reminder: true).Should().Be(MentionPolicy.None);
    }

    [Fact]
    public void English_cards_use_the_english_catalog()
    {
        var sets = new List<SetResult> { new(1, 25, 21) };
        var m = Renderer().SetFinished(View(sets: sets), 1, Start, "en", MentionPolicy.None, "vb.source.fivb");
        m.Embed!.Title.Should().Be("🇹🇷 Türkiye 1-0 Italy 🇮🇹");
        m.Embed.Description.Should().StartWith("🇹🇷 **SET TO TÜRKİYE!** · set 1").And.Contain("Set 1: 25-21");
        m.Embed.Footer.Should().Be("Source: FIVB");
    }
}
