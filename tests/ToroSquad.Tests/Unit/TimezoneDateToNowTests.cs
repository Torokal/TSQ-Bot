using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Timezone;
using ToroSquad.Modules.Timezone.Application;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// /saat V2: the optional <c>date</c> (DST-aware, year from the source zone), the single target <c>to</c> (same catalog as
/// <c>timezone</c>, Tokyo included), <c>time:now</c> on a frozen clock, the copyable timestamp field and the validation order.
/// Expected instants are written by hand and cross-checked with <see cref="TimeZoneInfo"/> directly.
/// </summary>
public sealed class TimezoneDateToNowTests
{
    private static readonly LocalizationCatalog Catalog = new(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(TimezoneModule).Assembly, "ToroSquad.Modules.Timezone.Localization"),
    ]);

    private static TimezoneCards Cards() => new(Catalog, NullLogger<TimezoneCards>.Instance);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = Utc(2026, 9, 30, 9); // 12:00 in Istanbul, 18:00 in Tokyo, 02:00 in Los Angeles

    private static TimezoneReply Reply(string time, string? zone = null, string? date = null, string? to = null, DateTimeOffset? now = null, string lang = "tr") =>
        Cards().Convert(lang, time, now ?? Now, "Deniz", zone, date, to);

    private static MessageEmbed Card(string time, string? zone = null, string? date = null, string? to = null, DateTimeOffset? now = null, string lang = "tr")
    {
        var reply = Reply(time, zone, date, to, now, lang);
        reply.Refusal.Should().BeNull();
        reply.Card.Should().NotBeNull();
        DiscordLimits.Validate(new OutgoingMessage(null, reply.Card, MentionPolicy.None)).Should().BeEmpty();
        return reply.Card!;
    }

    private static string Refusal(string time, string? zone = null, string? date = null, string? to = null, DateTimeOffset? now = null, string lang = "tr")
    {
        var reply = Reply(time, zone, date, to, now, lang);
        reply.Card.Should().BeNull();
        return reply.Refusal!;
    }

    private static TimeZoneInfo Zone(string id)
    {
        GuildTime.TryResolve(id, out var zone).Should().BeTrue(id);
        return zone;
    }

    private static string Wall(DateTimeOffset instant, string zoneId) =>
        TimeZoneInfo.ConvertTime(instant, Zone(zoneId)).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string[] Rows(MessageEmbed card) => card.Fields[1].Value.Split('\n');

    private static string[] Stamps(MessageEmbed card) =>
        Regex.Matches(string.Join("\n", card.Fields.Select(f => f.Value)), @"<t:(\d+):").Select(m => m.Groups[1].Value).Distinct().ToArray();

    private static string Unix(DateTimeOffset instant) => instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    // ---- date input ----

    [Theory]
    [InlineData("15.11", 2026, 11, 15)]
    [InlineData("15.11.2026", 2026, 11, 15)]
    [InlineData("15.11.2027", 2027, 11, 15)]
    [InlineData("5.11", 2026, 11, 5)]
    [InlineData("5.7.2026", 2026, 7, 5)]
    [InlineData("05.07", 2026, 7, 5)]
    [InlineData("01.01", 2026, 1, 1)]
    [InlineData("31.12.2026", 2026, 12, 31)]
    [InlineData(" 15.11 ", 2026, 11, 15)]
    [InlineData("29.02.2028", 2028, 2, 29)] // leap year
    public void Valid_dates_parse(string input, int year, int month, int day) =>
        DateInput.Parse(input, 2026).Should().Be(new DateOnly(year, month, day));

    [Theory]
    [InlineData("31.02")]
    [InlineData("30.02.2028")]
    [InlineData("29.02.2027")] // not a leap year
    [InlineData("29.02")]      // 2026 is not a leap year either
    [InlineData("31.04")]
    [InlineData("32.01")]
    [InlineData("00.01")]
    [InlineData("15.13")]
    [InlineData("15.00")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("2026-11-15")]
    [InlineData("15/11")]
    [InlineData("15-11")]
    [InlineData("15.11.26")]
    [InlineData("15.11.")]
    [InlineData(".11")]
    [InlineData("15")]
    [InlineData("015.11")]
    [InlineData("15.11.20266")]
    [InlineData("15.11.1899")]
    [InlineData("15.11.2101")]
    [InlineData("01.01.0001")]
    [InlineData("١٥.١١")] // Arabic-Indic digits
    public void Invalid_dates_are_refused(string input) => DateInput.Parse(input, 2026).Should().BeNull();

    [Fact]
    public void Null_date_is_refused_by_the_parser() => DateInput.Parse(null, 2026).Should().BeNull();

    [Fact]
    public void The_year_without_a_year_is_the_callers_and_a_past_day_does_not_jump_ahead()
    {
        DateInput.Parse("29.02", 2028).Should().Be(new DateOnly(2028, 2, 29));
        DateInput.Parse("01.01", 2026).Should().Be(new DateOnly(2026, 1, 1), "already past on 30 September — still this year");
    }

    // ---- date on the card: DST-aware ----

    [Fact]
    public void New_york_21_00_lands_on_different_turkish_times_in_winter_and_summer()
    {
        // 15 January: EST (UTC-5) → 02:00 UTC on the 16th → 05:00 in Türkiye.
        var winter = Utc(2026, 1, 16, 2);
        Wall(winter, "America/New_York").Should().Be("21:00", "cross-check with TimeZoneInfo");
        var january = Card("21:00", "ny", "15.01.2026");
        january.Fields[0].Value.Should().Be("`21:00` · New York (Doğu Saati) · 15.01.2026");
        Rows(january)[0].Should().Be("🇹🇷 Türkiye — `05:00` · Sonraki gün");
        Stamps(january).Should().Equal(Unix(winter)).And.Equal("1768528800");

        // 15 July: EDT (UTC-4) → 01:00 UTC on the 16th → 04:00 in Türkiye. A fixed offset cannot pass both.
        var summer = Utc(2026, 7, 16, 1);
        Wall(summer, "America/New_York").Should().Be("21:00");
        var july = Card("21:00", "ny", "15.07.2026");
        Rows(july)[0].Should().Be("🇹🇷 Türkiye — `04:00` · Sonraki gün");
        Stamps(july).Should().Equal(Unix(summer)).And.Equal("1784163600");

        // Every row of both cards is that instant as TimeZoneInfo reads it.
        foreach (var (card, instant) in new[] { (january, winter), (july, summer) })
            foreach (var (row, zone) in Rows(card).Zip(TimeZoneBoard.Zones))
                row.Should().Contain("`" + Wall(instant, zone.ZoneId) + "`", zone.ZoneId);
    }

    [Fact]
    public void Date_without_a_year_takes_the_source_zones_current_year()
    {
        // 03:00 UTC on 1 January 2027: 2027 in UTC and in Istanbul, still 31 December 2026 in Los Angeles.
        var newYear = Utc(2027, 1, 1, 3);
        Card("21:00", "la", "15.11", now: newYear).Fields[0].Value.Should().EndWith("· 15.11.2026");
        Card("21:00", "tr", "15.11", now: newYear).Fields[0].Value.Should().EndWith("· 15.11.2027");
        Card("21:00", "utc", "15.11", now: newYear).Fields[0].Value.Should().EndWith("· 15.11.2027");

        // 22:00 UTC on 31 December 2026: Istanbul and Tokyo are already in 2027, UTC and New York are not.
        var eve = Utc(2026, 12, 31, 22);
        Card("12:00", "tokyo", "15.11", now: eve).Fields[0].Value.Should().EndWith("· 15.11.2027");
        Card("12:00", "istanbul", "15.11", now: eve).Fields[0].Value.Should().EndWith("· 15.11.2027");
        Card("12:00", "ny", "15.11", now: eve).Fields[0].Value.Should().EndWith("· 15.11.2026");
    }

    [Fact]
    public void Without_date_nothing_changes()
    {
        Card("15:00", "pdt", now: Utc(2026, 9, 28, 12)).Should().BeEquivalentTo(Card("15:00", "pdt", null, null, Utc(2026, 9, 28, 12)));
        Card("15:00", "pdt", "", "", Utc(2026, 9, 28, 12)).Should().BeEquivalentTo(Card("15:00", "pdt", now: Utc(2026, 9, 28, 12)), "empty = not given");
        Card("15:00", "pdt", "28.09.2026", now: Utc(2026, 9, 28, 12)).Should().BeEquivalentTo(Card("15:00", "pdt", now: Utc(2026, 9, 28, 12)),
            "today's date written out is the same card");
    }

    [Theory]
    [InlineData("02:30", "ny", "08.03.2026")]   // spring forward in New York
    [InlineData("02:30", "pdt", "08.03.2026")]
    [InlineData("01:30", "uk", "29.03.2026")]
    public void An_explicit_date_keeps_the_dst_gap_check(string time, string zone, string date) =>
        Refusal(time, zone, date).Should().Be("Bu saat seçilen saat diliminde yaz saati geçişi nedeniyle mevcut değil.");

    [Theory]
    [InlineData("01:30", "ny", "01.11.2026")]   // fall back in New York
    [InlineData("01:30", "ct", "01.11.2026")]
    [InlineData("01:30", "london", "25.10.2026")]
    public void An_explicit_date_keeps_the_dst_overlap_check(string time, string zone, string date) =>
        Refusal(time, zone, date).Should().Contain("iki kez");

    [Fact]
    public void The_same_wall_time_on_an_ordinary_day_converts()
    {
        Stamps(Card("02:30", "ny", "09.03.2026")).Should().Equal(Unix(Utc(2026, 3, 9, 6, 30)));   // EDT
        Stamps(Card("01:30", "ny", "02.11.2026")).Should().Equal(Unix(Utc(2026, 11, 2, 6, 30)));  // EST
    }

    // ---- to: a single target ----

    [Fact]
    public void To_shows_only_the_target_row()
    {
        var card = Card("21:00", "ny", "15.11.2026", "tr");
        card.Fields.Select(f => f.Name).Should().Equal("Girilen saat", "Hedef saat", "Discord zamanı", "Discord timestamp kodu");
        card.Fields[0].Value.Should().Be("`21:00` · New York (Doğu Saati) · 15.11.2026");
        card.Fields[1].Value.Should().Be("🇹🇷 Türkiye — `05:00` · Sonraki gün"); // EST → 02:00 UTC on the 16th
        Stamps(card).Should().Equal(Unix(Utc(2026, 11, 16, 2))).And.Equal("1794794400");

        foreach (var alias in new[] { "TR", "istanbul", "turkiye", "Europe/Istanbul" })
            Card("21:00", "ny", "15.11.2026", alias).Should().BeEquivalentTo(card, alias);
    }

    [Fact]
    public void To_tokyo_from_turkiye()
    {
        var card = Card("21:00", "tr", to: "tokyo"); // 30.09 21:00 in Istanbul = 18:00 UTC = 03:00 on 1 October in Tokyo
        var instant = Utc(2026, 9, 30, 18);
        Wall(instant, "Asia/Tokyo").Should().Be("03:00");
        card.Fields[0].Value.Should().Be("`21:00` · Türkiye · 30.09.2026");
        card.Fields[1].Value.Should().Be("🇯🇵 Tokyo (Japonya Saati) — `03:00` · Sonraki gün");
        Stamps(card).Should().Equal(Unix(instant)).And.Equal("1790791200");

        Card("21:00", "tr", to: "tokyo", lang: "en").Fields[1].Value.Should().Be("🇯🇵 Tokyo (Japan Standard Time) — `03:00` · Next day");
    }

    [Fact]
    public void To_accepts_every_alias_of_the_shared_catalog()
    {
        var la = Card("21:00", to: "la");
        foreach (var alias in new[] { "pdt", "PST", "pt", "pacific", "America/Los_Angeles" })
            Card("21:00", to: alias).Should().BeEquivalentTo(la, alias);
        la.Fields[1].Value.Should().Be("🇺🇸 Los Angeles (Pasifik Saati) — `11:00`"); // 18:00 UTC in PDT
        Card("21:00", to: "utc").Fields[1].Value.Should().Be("🌐 UTC — `18:00`");
        Card("21:00", to: "gmt").Fields[1].Value.Should().Be("🇬🇧 Birleşik Krallık — `19:00`", "gmt names the zone; 30 September is on BST");
    }

    [Theory]
    [InlineData("tr", "Geçerli bir hedef saat dilimi girin. Örnek: tr, uk, est, pdt, tokyo veya utc.")]
    [InlineData("en", "Enter a valid target time zone. Example: tr, uk, est, pdt, tokyo or utc.")]
    public void Unknown_target_gets_its_own_refusal(string lang, string expected)
    {
        Refusal("21:00", to: "mars", lang: lang).Should().Be(expected);
        Refusal("21:00", "mars", lang: lang).Should().NotBe(expected, "a bad source says so, not 'target'");
    }

    // ---- Tokyo ----

    [Theory]
    [InlineData("tokyo")]
    [InlineData("japan")]
    [InlineData("jp")]
    [InlineData("jst")]
    [InlineData("Asia/Tokyo")]
    [InlineData("TOKYO")]
    [InlineData("Jst")]
    [InlineData(" asia/tokyo ")]
    public void Tokyo_aliases_resolve_to_asia_tokyo(string alias) =>
        SourceTimeZones.Resolve(alias)!.Should().Match<SourceZone>(z => z.ZoneId == "Asia/Tokyo" && z.Key == "tokyo");

    [Fact]
    public void Tokyo_is_one_catalog_entry_for_source_and_target_and_not_on_the_default_board()
    {
        SourceTimeZones.All.Where(z => z.ZoneId == "Asia/Tokyo").Should().ContainSingle();
        TimeZoneBoard.Zones.Should().NotContain(z => z.ZoneId == "Asia/Tokyo");
        Zone("Asia/Tokyo").HasIanaId.Should().BeTrue();
        Card("21:00").Fields[1].Value.Split('\n').Should().HaveCount(5, "the default board is unchanged");
        Card("12:00", "tokyo").Fields[1].Value.Split('\n').Should().HaveCount(5);
    }

    // ---- now ----

    [Fact]
    public void Now_uses_the_clocks_instant_in_the_source_zone()
    {
        var clock = new FakeTimeProvider(Utc(2026, 9, 30, 9, 42, 17));
        var card = Card("now", "tokyo", now: clock.GetUtcNow());
        card.Fields[0].Value.Should().Be("`18:42` · Tokyo (Japonya Saati) · 30.09.2026");
        Wall(clock.GetUtcNow(), "Asia/Tokyo").Should().Be("18:42");
        Rows(card).Should().Equal(
            "🇹🇷 Türkiye — `12:42`",
            "🇬🇧 Birleşik Krallık — `10:42`",
            "🇺🇸 New York — `05:42`",
            "🇺🇸 Chicago — `04:42`",
            "🇺🇸 Los Angeles — `02:42`");
        Stamps(card).Should().Equal("1790761337"); // the exact instant, seconds included — not rebuilt from 18:42
    }

    [Fact]
    public void Now_with_to_shows_only_the_target()
    {
        var card = Card("now", "tokyo", to: "tr", now: Utc(2026, 9, 30, 9, 42, 17));
        card.Fields[0].Value.Should().Be("`18:42` · Tokyo (Japonya Saati) · 30.09.2026");
        card.Fields[1].Value.Should().Be("🇹🇷 Türkiye — `12:42`");
        Stamps(card).Should().Equal("1790761337");
    }

    [Theory]
    [InlineData("now")]
    [InlineData("NOW")]
    [InlineData(" Now ")]
    public void Now_is_case_insensitive_and_defaults_to_turkiye(string input)
    {
        var card = Card(input, now: Utc(2026, 9, 30, 21, 30));
        card.Fields[0].Value.Should().Be("`00:30` · Türkiye · 01.10.2026", "Istanbul is already on the 1st");
        Rows(card)[4].Should().Be("🇺🇸 Los Angeles — `14:30` · Önceki gün");
    }

    [Fact]
    public void Now_is_never_refused_for_dst_it_is_an_instant_not_a_wall_time()
    {
        // 09:30 UTC on 1 November 2026 = 01:30 PST in Los Angeles, a wall time that happens twice that night.
        var card = Card("now", "la", now: Utc(2026, 11, 1, 9, 30));
        card.Fields[0].Value.Should().Be("`01:30` · Los Angeles (Pasifik Saati) · 01.11.2026");
        Stamps(card).Should().Equal(Unix(Utc(2026, 11, 1, 9, 30)));
    }

    [Theory]
    [InlineData("tr", "date seçeneği time:now ile birlikte kullanılamaz.")]
    [InlineData("en", "The date option cannot be used together with time:now.")]
    public void Now_with_a_date_is_refused_not_ignored(string lang, string expected)
    {
        Refusal("now", date: "15.11", lang: lang).Should().Be(expected);
        Refusal("now", "tokyo", "15.11.2026", "tr", lang: lang).Should().Be(expected);
    }

    [Fact]
    public void Now_is_not_a_clock_time_for_the_parser() => ClockInput.Parse("now").Should().BeNull();

    // ---- copyable timestamp ----

    [Fact]
    public void The_copy_field_holds_the_rendered_tokens_as_raw_code()
    {
        var card = Card("15:00", "pdt", now: Utc(2026, 9, 28, 12));
        card.Fields[2].Name.Should().Be("Discord zamanı");
        card.Fields[2].Value.Should().Be("<t:1790632800:t> · <t:1790632800:R>");
        card.Fields[3].Name.Should().Be("Discord timestamp kodu");
        card.Fields[3].Value.Should().Be("`<t:1790632800:t>`\n`<t:1790632800:R>`", "inline code: Discord shows it raw, ready to copy");
        Stamps(card).Should().Equal("1790632800");
        Card("15:00", "pdt", now: Utc(2026, 9, 28, 12), lang: "en").Fields[3].Name.Should().Be("Discord timestamp code");
    }

    // ---- validation order ----

    [Fact]
    public void Validation_order_is_time_source_date_target_then_dst()
    {
        const string time = "Geçerli bir saat girin. Örnek: `21:00`";
        const string source = "Geçerli bir saat dilimi girin. Örnek: tr, pdt, est, cst, uk veya utc.";
        const string date = "Geçerli bir tarih girin. Örnek: 15.11 veya 15.11.2026";
        const string target = "Geçerli bir hedef saat dilimi girin. Örnek: tr, uk, est, pdt, tokyo veya utc.";

        Refusal("25:00", "mars", "31.02", "mars").Should().Be(time);
        Refusal("21:00", "mars", "31.02", "mars").Should().Be(source);
        Refusal("21:00", "ny", "31.02", "mars").Should().Be(date);
        Refusal("21:00", "ny", "15.11", "mars").Should().Be(target);
        Refusal("02:30", "ny", "08.03.2026", "mars").Should().Be(target, "the target is checked before the DST gap");
        Refusal("02:30", "ny", "08.03.2026", "tr").Should().Contain("mevcut değil");
        Refusal("now", "mars", "15.11").Should().Be(source);
        Refusal("now", "ny", "31.02", "mars").Should().Be("date seçeneği time:now ile birlikte kullanılamaz.");
    }

    // ---- autocomplete for to ----

    [Fact]
    public void Autocomplete_finds_tokyo_and_every_target_label()
    {
        string[] Suggest(string? typed, string lang = "tr") =>
            SourceTimeZones.Suggest(typed, z => Catalog.Get(lang, z.NameKey)).Select(z => z.Key).ToArray();

        Suggest("tok").Should().Equal("tokyo");
        Suggest("jst").Should().Equal("tokyo");
        Suggest("japonya").Should().Equal("tokyo");
        Suggest("japan", "en").Should().Equal("tokyo");
        foreach (var label in new[] { "Türkiye", "United Kingdom", "New York", "Chicago", "Los Angeles", "Tokyo", "UTC" })
            Suggest(label, "en").Should().NotBeEmpty(label);
    }
}
