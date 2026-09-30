using Microsoft.Extensions.Logging.Abstractions;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Timezone;
using ToroSquad.Modules.Timezone.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Saat Dönüştürücü: the /saat input rule, the conversion on real IANA zones (fixed dates on both sides of every DST
/// switch — never the machine clock), day rollover, the single Discord timestamp and the card.
/// </summary>
public sealed class TimezoneTests
{
    private static LocalizationCatalog Localizer() => new(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(TimezoneModule).Assembly, "ToroSquad.Modules.Timezone.Localization"),
    ]);

    private static TimezoneCards Cards() => new(Localizer(), NullLogger<TimezoneCards>.Instance);

    private static readonly string Zwsp = char.ConvertFromUtf32(0x200B); // what DiscordText inserts to defuse a mention

    private static TimeZoneInfo Zone(string id)
    {
        GuildTime.TryResolve(id, out var zone).Should().BeTrue(id);
        return zone;
    }

    private static DateTimeOffset Istanbul(int year, int month, int day, int hour, int minute) =>
        TimeZoneBoard.Resolve(new DateOnly(year, month, day), new TimeOnly(hour, minute), Zone("Europe/Istanbul"));

    private static ZoneTime In(string zoneId, DateTimeOffset instant) =>
        TimeZoneBoard.In(new BoardZone("", "", zoneId), instant, TimeZoneBoard.Today(instant, Zone("Europe/Istanbul")), Zone(zoneId));

    private static MessageEmbed Card(TimezoneReply reply)
    {
        reply.Refusal.Should().BeNull();
        reply.Card.Should().NotBeNull();
        DiscordLimits.Validate(new OutgoingMessage(null, reply.Card, MentionPolicy.None)).Should().BeEmpty();
        return reply.Card!;
    }

    // ---- input ----

    [Theory]
    [InlineData("21:00", 21, 0)]
    [InlineData("9:00", 9, 0)]
    [InlineData("09:00", 9, 0)]
    [InlineData("21.00", 21, 0)]
    [InlineData("9.30", 9, 30)]
    [InlineData("0:00", 0, 0)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData("  21:00 ", 21, 0)]
    public void Valid_times_parse(string input, int hour, int minute) =>
        ClockInput.Parse(input).Should().Be(new TimeOnly(hour, minute));

    [Theory]
    [InlineData("25:00")]
    [InlineData("24:00")]
    [InlineData("21:70")]
    [InlineData("21:75")]
    [InlineData("21:60")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("21")]
    [InlineData("2100")]
    [InlineData("21:0")]
    [InlineData("21:000")]
    [InlineData("021:00")]
    [InlineData("21:00:00")]
    [InlineData("-1:00")]
    [InlineData("21,00")]
    [InlineData("21 00")]
    [InlineData("21:00pm")]
    [InlineData("9pm")]
    [InlineData("٢١:٠٠")]      // Arabic-Indic digits: \d would accept them, the parser does not
    [InlineData("２１:００")]   // full-width digits
    [InlineData("21:00        ")] // longer than the option allows
    public void Invalid_times_are_refused(string input) => ClockInput.Parse(input).Should().BeNull();

    [Fact]
    public void Null_is_refused() => ClockInput.Parse(null).Should().BeNull();

    // ---- conversion on real IANA zones ----

    [Theory]
    // Summer: Türkiye +3, UK BST +1, US daylight time.
    [InlineData(2026, 7, 15, "Europe/London", "19:00")]
    [InlineData(2026, 7, 15, "America/New_York", "14:00")]
    [InlineData(2026, 7, 15, "America/Chicago", "13:00")]
    [InlineData(2026, 7, 15, "America/Los_Angeles", "11:00")]
    // Winter: UK GMT, US standard time — every row moves one hour, Türkiye does not.
    [InlineData(2026, 1, 15, "Europe/London", "18:00")]
    [InlineData(2026, 1, 15, "America/New_York", "13:00")]
    [InlineData(2026, 1, 15, "America/Chicago", "12:00")]
    [InlineData(2026, 1, 15, "America/Los_Angeles", "10:00")]
    // US already on daylight time (8 March), UK not yet (29 March): only a real zone database gets both right.
    [InlineData(2026, 3, 10, "Europe/London", "18:00")]
    [InlineData(2026, 3, 10, "America/New_York", "14:00")]
    [InlineData(2026, 3, 10, "America/Los_Angeles", "11:00")]
    // UK back on GMT (25 October), US still on daylight time (until 1 November).
    [InlineData(2026, 10, 28, "Europe/London", "18:00")]
    [InlineData(2026, 10, 28, "America/New_York", "14:00")]
    [InlineData(2026, 10, 28, "America/Los_Angeles", "11:00")]
    public void Istanbul_21_00_follows_each_zones_own_dst_rules(int year, int month, int day, string zoneId, string expected)
    {
        var row = In(zoneId, Istanbul(year, month, day, 21, 0));
        row.Local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected);
        row.DayShift.Should().Be(0);
    }

    [Fact]
    public void Istanbul_is_read_as_utc_plus_three_from_the_zone_database()
    {
        var instant = Istanbul(2026, 9, 28, 21, 0);
        instant.Offset.Should().Be(TimeSpan.FromHours(3));
        instant.UtcDateTime.Should().Be(new DateTime(2026, 9, 28, 18, 0, 0, DateTimeKind.Utc));
        Istanbul(2026, 1, 15, 21, 0).Offset.Should().Be(TimeSpan.FromHours(3), "Türkiye has no DST");
    }

    [Fact]
    public void Every_board_zone_is_a_real_iana_zone_on_this_os()
    {
        TimeZoneBoard.Zones.Select(z => z.ZoneId).Should().Equal("Europe/Istanbul", "Europe/London", "America/New_York", "America/Chicago", "America/Los_Angeles");
        TimeZoneBoard.SourceZoneId.Should().Be("Europe/Istanbul");
        foreach (var id in TimeZoneBoard.Zones.Select(z => z.ZoneId))
        {
            var zone = Zone(id);
            zone.HasIanaId.Should().BeTrue(id);
            if (id != "Europe/Istanbul") // Istanbul keeps historical DST rules; its current +3 is checked above
                zone.SupportsDaylightSavingTime.Should().BeTrue(id);
        }

        TimeZoneBoard.Missing(TimeZoneBoard.Zones.Select(z => z.ZoneId), out var resolved).Should().BeEmpty();
        resolved.Should().HaveCount(5);
        TimeZoneBoard.Missing(["Mars/Olympus_Mons"], out _).Should().Equal("Mars/Olympus_Mons");
    }

    // ---- dates ----

    [Fact]
    public void Today_is_the_date_in_istanbul_not_the_utc_date()
    {
        var istanbul = Zone("Europe/Istanbul");
        TimeZoneBoard.Today(new DateTimeOffset(2026, 9, 27, 22, 30, 0, TimeSpan.Zero), istanbul).Should().Be(new DateOnly(2026, 9, 28), "01:30 in Istanbul");
        TimeZoneBoard.Today(new DateTimeOffset(2026, 9, 28, 20, 59, 0, TimeSpan.Zero), istanbul).Should().Be(new DateOnly(2026, 9, 28), "23:59 in Istanbul");
        TimeZoneBoard.Today(new DateTimeOffset(2026, 9, 28, 21, 0, 0, TimeSpan.Zero), istanbul).Should().Be(new DateOnly(2026, 9, 29), "midnight in Istanbul");
    }

    [Fact]
    public void Early_morning_in_istanbul_is_the_previous_day_in_the_americas()
    {
        var winter = Istanbul(2026, 1, 15, 2, 0); // 23:00 UTC on 14 January
        In("Europe/London", winter).Should().Match<ZoneTime>(r => r.Local.Hour == 23 && r.Local.Day == 14 && r.DayShift == -1);
        In("America/New_York", winter).Should().Match<ZoneTime>(r => r.Local.Hour == 18 && r.DayShift == -1);
        In("America/Los_Angeles", winter).Should().Match<ZoneTime>(r => r.Local.Hour == 15 && r.DayShift == -1);

        var summer = Istanbul(2026, 7, 15, 2, 0); // 23:00 UTC on 14 July — London is already at midnight of the 15th
        In("Europe/London", summer).Should().Match<ZoneTime>(r => r.Local.Hour == 0 && r.Local.Day == 15 && r.DayShift == 0);
        In("America/New_York", summer).Should().Match<ZoneTime>(r => r.Local.Hour == 19 && r.DayShift == -1);
    }

    [Fact]
    public void A_zone_east_of_istanbul_can_be_on_the_next_day()
    {
        // Helper level only: Tokyo is not on the production board.
        var row = In("Asia/Tokyo", Istanbul(2026, 9, 28, 21, 0));
        row.Local.Should().Be(new DateTimeOffset(2026, 9, 29, 3, 0, 0, TimeSpan.FromHours(9)));
        row.DayShift.Should().Be(1);
    }

    [Fact]
    public void A_wall_time_in_a_dst_gap_or_overlap_never_throws()
    {
        // Istanbul has no gap; the helper still has to be total for a zone that does.
        var newYork = Zone("America/New_York");
        var gap = TimeZoneBoard.Resolve(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), newYork);
        gap.Offset.Should().Be(TimeSpan.FromHours(-5));
        var overlap = TimeZoneBoard.Resolve(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), newYork);
        overlap.Offset.Should().Be(TimeSpan.FromHours(-5));
    }

    // ---- the card ----

    [Fact]
    public void Card_for_21_00_on_28_september_in_turkish()
    {
        // 22:30 UTC on 27 September is already 01:30 on the 28th in Istanbul: "today" is the 28th.
        var card = Card(Cards().Convert("tr", "21:00", new DateTimeOffset(2026, 9, 27, 22, 30, 0, TimeSpan.Zero), "Deniz"));

        card.Title.Should().Be("🕐 Saat Dönüştürücü");
        card.Color.Should().Be(0xE8590C);
        card.Footer.Should().Be("Deniz tarafından istendi");
        card.Fields.Select(f => f.Name).Should().Equal("Girilen saat", "Saat dilimleri", "Discord zamanı", "Discord timestamp kodu");
        card.Fields[0].Value.Should().Be("`21:00` · Türkiye · 28.09.2026");
        card.Fields[1].Value.Should().Be(string.Join("\n",
            "🇹🇷 Türkiye — `21:00`",
            "🇬🇧 Birleşik Krallık — `19:00`",
            "🇺🇸 New York — `14:00`",
            "🇺🇸 Chicago — `13:00`",
            "🇺🇸 Los Angeles — `11:00`"));
        card.Fields[2].Value.Should().Be("<t:1790618400:t> · <t:1790618400:R>"); // 2026-09-28T18:00:00Z
        card.Fields[3].Value.Should().Be("`<t:1790618400:t>`\n`<t:1790618400:R>`"); // the same tokens, raw for copying
    }

    [Fact]
    public void Card_in_english()
    {
        var card = Card(Cards().Convert("en", "9.05", new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), "Deniz"));
        card.Title.Should().Be("🕐 Time Converter");
        card.Footer.Should().Be("Requested by Deniz");
        card.Fields.Select(f => f.Name).Should().Equal("Entered time", "Time zones", "Discord time", "Discord timestamp code");
        card.Fields[0].Value.Should().Be("`09:05` · Türkiye · 15.07.2026");
        card.Fields[1].Value.Should().Be(string.Join("\n",
            "🇹🇷 Türkiye — `09:05`",
            "🇬🇧 United Kingdom — `07:05`",
            "🇺🇸 New York — `02:05`",
            "🇺🇸 Chicago — `01:05`",
            "🇺🇸 Los Angeles — `23:05` · Previous day"));
    }

    [Fact]
    public void Previous_day_is_marked_on_the_rows_that_change_date()
    {
        var card = Card(Cards().Convert("tr", "02:00", new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero), "Deniz"));
        card.Fields[1].Value.Split('\n').Should().Equal(
            "🇹🇷 Türkiye — `02:00`",
            "🇬🇧 Birleşik Krallık — `23:00` · Önceki gün",
            "🇺🇸 New York — `18:00` · Önceki gün",
            "🇺🇸 Chicago — `17:00` · Önceki gün",
            "🇺🇸 Los Angeles — `15:00` · Önceki gün");
    }

    [Fact]
    public void One_discord_timestamp_for_the_one_instant()
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var card = Card(Cards().Convert("tr", "21:00", now, "Deniz"));
        var instant = Istanbul(2026, 7, 15, 21, 0);
        instant.ToUnixTimeSeconds().Should().Be(1784138400); // 2026-07-15T18:00:00Z

        var text = string.Join("\n", card.Fields.Select(f => f.Value));
        System.Text.RegularExpressions.Regex.Matches(text, @"<t:(\d+):").Select(m => m.Groups[1].Value).Should().Equal("1784138400", "1784138400", "1784138400", "1784138400"); // rendered + raw copy
        foreach (var zone in TimeZoneBoard.Zones)
            TimeZoneBoard.In(zone, instant, new DateOnly(2026, 7, 15), Zone(zone.ZoneId)).Local.ToUnixTimeSeconds().Should().Be(1784138400, zone.ZoneId);
    }

    [Theory]
    [InlineData("tr", "Geçerli bir saat girin. Örnek: `21:00`")]
    [InlineData("en", "Enter a valid time. Example: `21:00`")]
    public void Invalid_time_gets_a_short_refusal_and_no_card(string lang, string expected)
    {
        foreach (var input in new[] { "25:00", "21:70", "abc", "" })
        {
            var reply = Cards().Convert(lang, input, new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), "Deniz");
            reply.Card.Should().BeNull(input);
            reply.Refusal.Should().Be(expected, input);
        }
    }

    [Fact]
    public void The_display_name_is_defused_and_nobody_is_pinged()
    {
        var card = Card(Cards().Convert("tr", "21:00", new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), "@everyone <@1>"));
        card.Footer.Should().Contain("@" + Zwsp + "everyone").And.NotContain("<@1>");
        string.Join("\n", card.Fields.Select(f => f.Value)).Should().NotContain("@");
    }
}
