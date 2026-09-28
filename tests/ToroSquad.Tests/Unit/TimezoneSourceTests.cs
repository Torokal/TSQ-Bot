using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Timezone;
using ToroSquad.Modules.Timezone.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// /saat's optional <c>timezone</c> option: aliases resolve to zones (never offsets), "today" is the source zone's date, the
/// DST rules of the source zone decide the offset, skipped and repeated wall times are refused, and without the option
/// nothing changes. Expected instants are written out by hand and cross-checked with <see cref="TimeZoneInfo"/> directly.
/// </summary>
public sealed class TimezoneSourceTests
{
    private static readonly LocalizationCatalog Catalog = new(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(TimezoneModule).Assembly, "ToroSquad.Modules.Timezone.Localization"),
    ]);

    private static TimezoneCards Cards() => new(Catalog, NullLogger<TimezoneCards>.Instance);

    private static TimeZoneInfo Zone(string id)
    {
        GuildTime.TryResolve(id, out var zone).Should().BeTrue(id);
        return zone;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static MessageEmbed Card(TimezoneReply reply)
    {
        reply.Refusal.Should().BeNull();
        reply.Card.Should().NotBeNull();
        DiscordLimits.Validate(new OutgoingMessage(null, reply.Card, MentionPolicy.None)).Should().BeEmpty();
        return reply.Card!;
    }

    private static MessageEmbed Card(string input, DateTimeOffset now, string? zone, string lang = "tr") =>
        Card(Cards().Convert(lang, input, now, "Deniz", zone));

    private static string[] Rows(MessageEmbed card) => card.Fields[1].Value.Split('\n');

    private static string[] Stamps(MessageEmbed card) =>
        Regex.Matches(string.Join("\n", card.Fields.Select(f => f.Value)), @"<t:(\d+):").Select(m => m.Groups[1].Value).ToArray();

    private static string Unix(DateTimeOffset instant) => instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Wall(DateTimeOffset instant, string zoneId) =>
        TimeZoneInfo.ConvertTime(instant, Zone(zoneId)).ToString("HH:mm", CultureInfo.InvariantCulture);

    // ---- aliases ----

    [Theory]
    [InlineData("tr", "Europe/Istanbul")]
    [InlineData("turkey", "Europe/Istanbul")]
    [InlineData("turkiye", "Europe/Istanbul")]
    [InlineData("Türkiye", "Europe/Istanbul")]
    [InlineData("istanbul", "Europe/Istanbul")]
    [InlineData("pt", "America/Los_Angeles")]
    [InlineData("pst", "America/Los_Angeles")]
    [InlineData("pdt", "America/Los_Angeles")]
    [InlineData("pacific", "America/Los_Angeles")]
    [InlineData("losangeles", "America/Los_Angeles")]
    [InlineData("los_angeles", "America/Los_Angeles")]
    [InlineData("la", "America/Los_Angeles")]
    [InlineData("et", "America/New_York")]
    [InlineData("est", "America/New_York")]
    [InlineData("edt", "America/New_York")]
    [InlineData("eastern", "America/New_York")]
    [InlineData("newyork", "America/New_York")]
    [InlineData("new_york", "America/New_York")]
    [InlineData("ny", "America/New_York")]
    [InlineData("ct", "America/Chicago")]
    [InlineData("cst", "America/Chicago")]
    [InlineData("cdt", "America/Chicago")]
    [InlineData("central", "America/Chicago")]
    [InlineData("chicago", "America/Chicago")]
    [InlineData("uk", "Europe/London")]
    [InlineData("gb", "Europe/London")]
    [InlineData("london", "Europe/London")]
    [InlineData("gmt", "Europe/London")]
    [InlineData("bst", "Europe/London")]
    [InlineData("utc", "UTC")]
    [InlineData("PDT", "America/Los_Angeles")]
    [InlineData("pDt", "America/Los_Angeles")]
    [InlineData("  pdt ", "America/Los_Angeles")]
    [InlineData("UTC", "UTC")]
    [InlineData("America/Los_Angeles", "America/Los_Angeles")] // the IANA id itself
    [InlineData("europe/london", "Europe/London")]
    public void Aliases_resolve_to_a_zone_ignoring_case(string alias, string zoneId) =>
        SourceTimeZones.Resolve(alias)!.ZoneId.Should().Be(zoneId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_timezone_means_turkiye(string? input)
    {
        SourceTimeZones.Resolve(input).Should().BeSameAs(SourceTimeZones.Default);
        SourceTimeZones.Default.ZoneId.Should().Be(TimeZoneBoard.SourceZoneId).And.Be("Europe/Istanbul");
    }

    [Theory]
    [InlineData("mars")]
    [InlineData("pdt2")]
    [InlineData("UTC+3")]
    [InlineData("GMT-7")]
    [InlineData("-07:00")]
    [InlineData("Europe/Paris")] // a real zone, but not one of the supported sources
    [InlineData("p d t")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopq")]
    public void Unknown_timezones_are_refused(string input) => SourceTimeZones.Resolve(input).Should().BeNull();

    [Fact]
    public void Source_zones_are_real_iana_zones_with_unique_names()
    {
        SourceTimeZones.All.Select(z => z.ZoneId).Should().Equal("Europe/Istanbul", "Europe/London", "America/New_York", "America/Chicago", "America/Los_Angeles", "UTC");
        SourceTimeZones.All.SelectMany(z => z.Aliases).Should().OnlyHaveUniqueItems();
        foreach (var zone in SourceTimeZones.All)
        {
            Zone(zone.ZoneId).Should().NotBeNull();
            zone.Aliases.Should().Contain(zone.Key, "the autocomplete value must resolve");
            SourceTimeZones.Resolve(zone.Key).Should().BeSameAs(zone);
            zone.Aliases.Should().OnlyContain(a => string.Equals(a, a.ToLowerInvariant(), StringComparison.Ordinal) && a.Trim() == a);
            Catalog.HasKey("tr", zone.NameKey).Should().BeTrue(zone.NameKey);
        }

        // Every board row can also be the source.
        TimeZoneBoard.Zones.Select(z => z.ZoneId).Should().BeSubsetOf(SourceTimeZones.All.Select(z => z.ZoneId));
    }

    // ---- backward compatibility ----

    [Fact]
    public void Without_the_option_the_card_is_the_same_as_timezone_tr()
    {
        var now = Utc(2026, 9, 27, 22, 30);
        var plain = Card("21:00", now, null);
        foreach (var zone in new[] { "tr", "TR", "turkiye", "istanbul", "", " " })
            Card("21:00", now, zone).Should().BeEquivalentTo(plain, zone);
        plain.Fields[0].Value.Should().Be("`21:00` · Türkiye · 28.09.2026");
        Stamps(plain).Should().Equal("1790618400", "1790618400");
    }

    // ---- the /saat 15:00 timezone:pdt scenario ----

    [Fact]
    public void Pdt_15_00_on_28_september_2026()
    {
        var expected = Utc(2026, 9, 28, 22); // 15:00 PDT (UTC-7 on this date) — cross-checked below with TimeZoneInfo
        Wall(expected, "America/Los_Angeles").Should().Be("15:00");
        TimeZoneInfo.ConvertTime(expected, Zone("Europe/Istanbul")).Day.Should().Be(29);

        var card = Card("15:00", Utc(2026, 9, 28, 12), "pdt"); // 05:00 in Los Angeles on the 28th
        card.Fields[0].Value.Should().Be("`15:00` · Los Angeles (Pasifik Saati) · 28.09.2026");
        Rows(card).Should().Equal(
            "🇹🇷 Türkiye — `01:00` · Sonraki gün",
            "🇬🇧 Birleşik Krallık — `23:00`",
            "🇺🇸 New York — `18:00`",
            "🇺🇸 Chicago — `17:00`",
            "🇺🇸 Los Angeles — `15:00`");
        Stamps(card).Should().Equal(Unix(expected), Unix(expected));
        Unix(expected).Should().Be("1790632800");
        card.Fields[2].Value.Should().Be("<t:1790632800:t> · <t:1790632800:R>");

        // The row values are that one instant, read by TimeZoneInfo itself.
        foreach (var (row, zone) in Rows(card).Zip(TimeZoneBoard.Zones))
            row.Should().Contain("`" + Wall(expected, zone.ZoneId) + "`", zone.ZoneId);

        foreach (var alias in new[] { "PDT", "pDt", "pst", "pt", "la", "pacific", "America/Los_Angeles" })
            Card("15:00", Utc(2026, 9, 28, 12), alias).Should().BeEquivalentTo(card, alias);
    }

    [Fact]
    public void Source_label_names_the_region_in_english_too_never_an_abbreviation()
    {
        var card = Card("15:00", Utc(2026, 9, 28, 12), "pst", "en"); // PST typed, but the date is on PDT
        card.Fields[0].Value.Should().Be("`15:00` · Los Angeles (Pacific Time) · 28.09.2026");
        card.Fields[1].Value.Should().StartWith("🇹🇷 Türkiye — `01:00` · Next day");
        string.Join("\n", card.Fields.Select(f => f.Value)).Should().NotMatchRegex(@"\b(PDT|PST|EDT|EST|BST)\b");

        Card("12:00", Utc(2026, 9, 28, 12), "est", "en").Fields[0].Value.Should().Be("`12:00` · New York (Eastern Time) · 28.09.2026");
        Card("12:00", Utc(2026, 9, 28, 12), "cdt", "en").Fields[0].Value.Should().Be("`12:00` · Chicago (Central Time) · 28.09.2026");
        Card("12:00", Utc(2026, 9, 28, 12), "gmt", "en").Fields[0].Value.Should().Be("`12:00` · United Kingdom · 28.09.2026");
        Card("12:00", Utc(2026, 9, 28, 12), "utc", "en").Fields[0].Value.Should().Be("`12:00` · UTC · 28.09.2026");
    }

    // ---- DST of the source zone: summer and winter ----

    [Theory]
    // Pacific: PDT (UTC-7) in July, PST (UTC-8) in January — whatever abbreviation was typed.
    [InlineData("pst", 2026, 7, 15, "15:00", 22)]
    [InlineData("pdt", 2026, 1, 15, "15:00", 23)]
    // Eastern: EDT (UTC-4) / EST (UTC-5).
    [InlineData("est", 2026, 7, 15, "12:00", 16)]
    [InlineData("edt", 2026, 1, 15, "12:00", 17)]
    // Central: CDT (UTC-5) / CST (UTC-6).
    [InlineData("cst", 2026, 7, 15, "12:00", 17)]
    [InlineData("cdt", 2026, 1, 15, "12:00", 18)]
    // London: BST (UTC+1) / GMT (UTC+0).
    [InlineData("gmt", 2026, 7, 15, "12:00", 11)]
    [InlineData("bst", 2026, 1, 15, "12:00", 12)]
    // UTC never moves.
    [InlineData("utc", 2026, 7, 15, "12:00", 12)]
    [InlineData("utc", 2026, 1, 15, "12:00", 12)]
    public void The_source_offset_comes_from_the_zone_and_the_date(string alias, int year, int month, int day, string time, int utcHour)
    {
        var expected = Utc(year, month, day, utcHour);
        var zoneId = SourceTimeZones.Resolve(alias)!.ZoneId;
        Wall(expected, zoneId).Should().Be(time, "cross-check with TimeZoneInfo");

        var card = Card(time, Utc(year, month, day, 12), alias); // noon UTC: the same calendar day in every source zone
        Stamps(card).Distinct().Should().Equal(Unix(expected));
        foreach (var (row, zone) in Rows(card).Zip(TimeZoneBoard.Zones))
            row.Should().Contain("`" + Wall(expected, zone.ZoneId) + "`", zone.ZoneId);
    }

    [Fact]
    public void Pacific_and_eastern_offsets_differ_between_summer_and_winter()
    {
        var la = Zone("America/Los_Angeles");
        TimeZoneBoard.Resolve(new DateOnly(2026, 7, 15), new TimeOnly(15, 0), la).Offset.Should().Be(TimeSpan.FromHours(-7));
        TimeZoneBoard.Resolve(new DateOnly(2026, 1, 15), new TimeOnly(15, 0), la).Offset.Should().Be(TimeSpan.FromHours(-8));
        var ny = Zone("America/New_York");
        TimeZoneBoard.Resolve(new DateOnly(2026, 7, 15), new TimeOnly(12, 0), ny).Offset.Should().Be(TimeSpan.FromHours(-4));
        TimeZoneBoard.Resolve(new DateOnly(2026, 1, 15), new TimeOnly(12, 0), ny).Offset.Should().Be(TimeSpan.FromHours(-5));
        var london = Zone("Europe/London");
        TimeZoneBoard.Resolve(new DateOnly(2026, 3, 28), new TimeOnly(12, 0), london).Offset.Should().Be(TimeSpan.Zero, "GMT the day before the switch");
        TimeZoneBoard.Resolve(new DateOnly(2026, 3, 29), new TimeOnly(12, 0), london).Offset.Should().Be(TimeSpan.FromHours(1), "BST from 29 March 2026");
    }

    [Fact]
    public void Winter_pst_puts_turkiye_on_the_next_day()
    {
        var card = Card("15:00", Utc(2026, 1, 15, 12), "pst"); // 15:00 PST = 23:00 UTC
        Rows(card).Should().Equal(
            "🇹🇷 Türkiye — `02:00` · Sonraki gün",
            "🇬🇧 Birleşik Krallık — `23:00`",
            "🇺🇸 New York — `18:00`",
            "🇺🇸 Chicago — `17:00`",
            "🇺🇸 Los Angeles — `15:00`");
    }

    // ---- "today" is the source zone's date ----

    [Fact]
    public void Today_is_the_date_in_the_source_zone()
    {
        // 03:00 UTC on 28 September: 06:00 on the 28th in Istanbul, but 20:00 on the 27th in Los Angeles.
        var now = Utc(2026, 9, 28, 3);
        Card("00:30", now, "pdt").Fields[0].Value.Should().EndWith("· 27.09.2026");
        Stamps(Card("00:30", now, "pdt")).Distinct().Should().Equal(Unix(Utc(2026, 9, 27, 7, 30)));
        Card("00:30", now, "tr").Fields[0].Value.Should().EndWith("· 28.09.2026");
        Card("00:30", now, "utc").Fields[0].Value.Should().EndWith("· 28.09.2026");

        // 22:30 UTC on 27 September: already the 28th in Istanbul, still the 27th in UTC and in Los Angeles.
        now = Utc(2026, 9, 27, 22, 30);
        Card("21:00", now, "tr").Fields[0].Value.Should().EndWith("· 28.09.2026");
        Card("21:00", now, "utc").Fields[0].Value.Should().EndWith("· 27.09.2026");
        Card("21:00", now, "la").Fields[0].Value.Should().EndWith("· 27.09.2026");
        Stamps(Card("21:00", now, "utc")).Distinct().Should().Equal(Unix(Utc(2026, 9, 27, 21)));
    }

    // ---- skipped and repeated wall times ----

    [Theory]
    [InlineData(2026, 3, 8, 2, 0, "America/Los_Angeles", LocalTimeKind.Skipped)]   // 02:00 → 03:00 PDT
    [InlineData(2026, 3, 8, 2, 30, "America/Los_Angeles", LocalTimeKind.Skipped)]
    [InlineData(2026, 3, 8, 3, 0, "America/Los_Angeles", LocalTimeKind.Valid)]
    [InlineData(2026, 3, 8, 1, 59, "America/Los_Angeles", LocalTimeKind.Valid)]
    [InlineData(2026, 3, 8, 2, 30, "America/New_York", LocalTimeKind.Skipped)]
    [InlineData(2026, 3, 29, 1, 30, "Europe/London", LocalTimeKind.Skipped)]       // 01:00 → 02:00 BST
    [InlineData(2026, 11, 1, 1, 30, "America/Los_Angeles", LocalTimeKind.Repeated)] // 02:00 PDT → 01:00 PST
    [InlineData(2026, 11, 1, 1, 30, "America/Chicago", LocalTimeKind.Repeated)]
    [InlineData(2026, 10, 25, 1, 30, "Europe/London", LocalTimeKind.Repeated)]     // 02:00 BST → 01:00 GMT
    [InlineData(2026, 11, 1, 2, 0, "America/Los_Angeles", LocalTimeKind.Valid)]
    [InlineData(2026, 3, 29, 3, 0, "Europe/Istanbul", LocalTimeKind.Valid)]         // Türkiye: no DST
    [InlineData(2026, 3, 8, 2, 30, "UTC", LocalTimeKind.Valid)]
    public void Wall_times_are_classified_by_the_zone(int year, int month, int day, int hour, int minute, string zoneId, LocalTimeKind expected) =>
        TimeZoneBoard.Classify(new DateOnly(year, month, day), new TimeOnly(hour, minute), Zone(zoneId)).Should().Be(expected);

    [Theory]
    [InlineData("02:30", 2026, 3, 8, "pdt")]
    [InlineData("02:30", 2026, 3, 8, "est")]
    [InlineData("01:30", 2026, 3, 29, "uk")]
    public void A_time_skipped_by_dst_is_refused(string time, int year, int month, int day, string zone)
    {
        var reply = Cards().Convert("tr", time, Utc(year, month, day, 18), "Deniz", zone);
        reply.Card.Should().BeNull();
        reply.Refusal.Should().Be("Bu saat seçilen saat diliminde yaz saati geçişi nedeniyle mevcut değil.");
        Cards().Convert("en", time, Utc(year, month, day, 18), "Deniz", zone).Refusal
            .Should().Be("This time does not exist in the chosen time zone because of a daylight saving time change.");
    }

    [Theory]
    [InlineData("01:30", 2026, 11, 1, "pst")]
    [InlineData("01:30", 2026, 11, 1, "ct")]
    [InlineData("01:30", 2026, 10, 25, "london")]
    public void A_time_repeated_by_dst_is_refused_instead_of_guessing(string time, int year, int month, int day, string zone)
    {
        var reply = Cards().Convert("tr", time, Utc(year, month, day, 18), "Deniz", zone);
        reply.Card.Should().BeNull();
        reply.Refusal.Should().Contain("iki kez").And.Contain("timezone:utc");
        Cards().Convert("en", time, Utc(year, month, day, 18), "Deniz", zone).Refusal.Should().Contain("twice");
    }

    [Fact]
    public void The_first_valid_time_after_the_gap_converts()
    {
        var card = Card("03:00", Utc(2026, 3, 8, 18), "pdt"); // 03:00 PDT = 10:00 UTC
        Stamps(card).Distinct().Should().Equal(Unix(Utc(2026, 3, 8, 10)));
    }

    // ---- refusals ----

    [Theory]
    [InlineData("tr", "Geçerli bir saat dilimi girin. Örnek: tr, pdt, est, cst, uk veya utc.")]
    [InlineData("en", "Enter a valid time zone. Example: tr, pdt, est, cst, uk or utc.")]
    public void Unknown_timezone_gets_a_short_refusal(string lang, string expected)
    {
        var reply = Cards().Convert(lang, "15:00", Utc(2026, 9, 28, 12), "Deniz", "mars");
        reply.Card.Should().BeNull();
        reply.Refusal.Should().Be(expected);
    }

    [Fact]
    public void An_invalid_time_is_reported_before_the_timezone()
    {
        foreach (var zone in new[] { "pdt", "mars" })
            Cards().Convert("tr", "25:00", Utc(2026, 9, 28, 12), "Deniz", zone).Refusal.Should().Be("Geçerli bir saat girin. Örnek: `21:00`");
    }

    // ---- autocomplete ----

    private static string[] Suggest(string? typed, string lang = "tr") =>
        SourceTimeZones.Suggest(typed, z => Catalog.Get(lang, z.NameKey)).Select(z => z.Key).ToArray();

    [Fact]
    public void Autocomplete_offers_every_zone_and_filters_by_name_or_alias()
    {
        Suggest(null).Should().Equal("tr", "uk", "ny", "chicago", "la", "utc");
        Suggest("").Should().Equal("tr", "uk", "ny", "chicago", "la", "utc");
        Suggest("pdt").Should().Equal("la");
        Suggest("PD").Should().Equal("la");
        Suggest("est").Should().Equal("ny");
        Suggest("new").Should().Equal("ny");
        Suggest("los ang").Should().Equal("la");
        Suggest("birleşik").Should().Equal("uk");
        Suggest("united", "en").Should().Equal("uk");
        Suggest("pasifik").Should().Equal("la");
        Suggest("pacific time", "en").Should().Equal("la");
        Suggest("mars").Should().BeEmpty();
    }
}
