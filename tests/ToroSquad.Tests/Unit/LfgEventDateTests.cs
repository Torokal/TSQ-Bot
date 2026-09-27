using ToroSquad.Core.Guilds;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Start dates: only a full date and time (GG.AA.YYYY SS:DD, GG.AA.YY SS:DD, ISO), parsed explicitly — a two-digit year from
/// the reference year, never a culture or platform setting — as wall-clock time in the guild's time zone (DST gaps and
/// overlaps refused, never guessed), a 1-minute minimum lead and a 1-year horizon. No relative times. Empty = now.
/// </summary>
public sealed class LfgEventDateTests
{
    // 27.09.2026 16:00 UTC = 19:00 in Istanbul (UTC+3 all year).
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 16, 0, 0, TimeSpan.Zero);

    private static TimeZoneInfo Zone(string id)
    {
        GuildTime.TryResolve(id, out var zone).Should().BeTrue(id);
        return zone;
    }

    private static TimeZoneInfo Istanbul => Zone(GuildSettings.DefaultTimeZoneId);

    [Theory]
    [InlineData("05.10.2026 21:30", "2026-10-05T18:30:00Z")]
    [InlineData("5.10.2026 21:30", "2026-10-05T18:30:00Z")]
    [InlineData("  27.09.2026 19:01  ", "2026-09-27T16:01:00Z")]
    [InlineData("01.01.2027 00:30", "2026-12-31T21:30:00Z")]
    [InlineData("2026-10-05 21:30", "2026-10-05T18:30:00Z")]
    [InlineData("05.10.26 21:30", "2026-10-05T18:30:00Z")]
    [InlineData("5.10.26 21:30", "2026-10-05T18:30:00Z")]
    [InlineData("27.09.26 21:30", "2026-09-27T18:30:00Z")]
    [InlineData("01.01.27 00:30", "2026-12-31T21:30:00Z")]
    public void Istanbul_wall_clock_times_become_exact_instants(string input, string expectedUtc)
    {
        var (at, error) = LfgEventDate.Resolve(input, Istanbul, Now);

        error.Should().Be(LfgDraftError.None);
        at.Should().Be(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("America/New_York", "05.10.2026 21:30", "2026-10-06T01:30:00Z")] // EDT, UTC-4
    [InlineData("America/New_York", "10.12.2026 21:30", "2026-12-11T02:30:00Z")] // EST, UTC-5
    [InlineData("Europe/London", "05.10.2026 21:30", "2026-10-05T20:30:00Z")] // BST, UTC+1
    [InlineData("UTC", "05.10.2026 21:30", "2026-10-05T21:30:00Z")]
    public void Other_time_zones_give_their_own_instant(string zone, string input, string expectedUtc) =>
        LfgEventDate.Resolve(input, Zone(zone), Now).At.Should().Be(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("05/10/2026 21:30")]
    [InlineData("05.10.2026")]
    [InlineData("21:30")]
    [InlineData("31.02.2026 21:00")]
    [InlineData("31.02.26 21:00")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("30 dk")]
    [InlineData("2 saat")]
    [InlineData("1 gün")]
    [InlineData("2 saat sonra")]
    [InlineData("1,5 saat")]
    [InlineData("akşam 9")]
    [InlineData("05.10.026 21:30")]
    [InlineData("05.10.2026 21:3")]
    [InlineData("١٥.١٠.٢٠٢٦ ٢١:٣٠")]
    [InlineData("05.13.2026 21:30")]
    [InlineData("05.10.2026 24:30")]
    [InlineData("yarın 21:30")]
    [InlineData("10/05/2026 9:30 PM")]
    public void Anything_but_the_supported_formats_is_refused(string input) =>
        LfgEventDate.Resolve(input, Istanbul, Now).Error.Should().Be(LfgDraftError.DateFormat);

    [Theory]
    [InlineData("27.09.2026 18:00", LfgDraftError.DateNotInFuture)] // past
    [InlineData("27.09.2026 19:00", LfgDraftError.DateNotInFuture)] // now
    [InlineData("27.09.2026 19:01", LfgDraftError.None)] // exactly the 1-minute minimum
    [InlineData("27.09.2027 19:00", LfgDraftError.None)] // exactly 365 days ahead
    [InlineData("27.09.2027 19:01", LfgDraftError.DateTooFar)]
    public void The_start_must_be_between_one_minute_and_one_year_ahead(string input, LfgDraftError expected) =>
        LfgEventDate.Resolve(input, Istanbul, Now).Error.Should().Be(expected);

    [Fact]
    public void Thirty_seconds_ahead_is_too_soon()
    {
        var now = Now.AddSeconds(30); // 19:00:30 local
        LfgEventDate.Resolve("27.09.2026 19:01", Istanbul, now).Error.Should().Be(LfgDraftError.DateNotInFuture);
    }

    [Theory]
    [InlineData("Europe/Berlin", "29.03.2027 02:30", LfgDraftError.None)] // not a transition day
    [InlineData("Europe/Berlin", "28.03.2027 02:30", LfgDraftError.DateNotInTimeZone)] // clocks jump 02:00 -> 03:00
    [InlineData("America/New_York", "14.03.2027 02:30", LfgDraftError.DateNotInTimeZone)]
    [InlineData("Europe/Berlin", "25.10.2026 02:30", LfgDraftError.DateAmbiguous)] // 02:00-03:00 happens twice
    [InlineData("America/New_York", "01.11.2026 01:30", LfgDraftError.DateAmbiguous)]
    public void Clock_change_gaps_and_overlaps_are_refused_not_guessed(string zone, string input, LfgDraftError expected) =>
        LfgEventDate.Resolve(input, Zone(zone), Now).Error.Should().Be(expected);

    [Theory]
    [InlineData(26, 2026, 2026)]
    [InlineData(27, 2026, 2027)]
    [InlineData(25, 2026, 2025)]
    [InlineData(0, 2099, 2100)]
    [InlineData(99, 2100, 2099)]
    [InlineData(75, 2026, 2075)] // the window is reference - 50 … reference + 49
    [InlineData(76, 2026, 1976)]
    public void A_two_digit_year_is_the_nearest_year_to_the_reference(int twoDigits, int reference, int expected) =>
        LfgEventDate.TwoDigitYear(twoDigits, reference).Should().Be(expected);

    [Fact]
    public void A_two_digit_year_never_depends_on_the_culture_or_the_platform()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var odd = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo("tr-TR").Clone();
            odd.Calendar.TwoDigitYearMax = 1999; // 26 would be 1926 for DateTime parsing with this culture
            System.Globalization.CultureInfo.CurrentCulture = odd;

            LfgEventDate.Resolve("27.09.26 21:30", Istanbul, Now).At.Should().Be(new DateTimeOffset(2026, 9, 27, 18, 30, 0, TimeSpan.Zero));
            LfgEventDate.TryReadWallClock("27.09.26 21:30", 2026, out var local).Should().BeTrue();
            local.Year.Should().Be(2026);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_start_is_now_or_a_date_and_ends_as_the_same_EventAt()
    {
        var custom = LfgRules.Validate("Deadlock", null, 6, 120, 20, 120, startAt: "05.10.2026 21:30", zone: Istanbul, now: Now).Draft!;
        var eventAt = new DateTimeOffset(2026, 10, 5, 18, 30, 0, TimeSpan.Zero);
        custom.Schedule(Now).Should().Be(((DateTimeOffset?)eventAt, eventAt.AddHours(2)), "ExpiresAt = EventAt + duration");

        var immediate = LfgRules.Validate("Deadlock", null, 6, 60, 20, 120, startAt: "   ", zone: Istanbul, now: Now).Draft!;
        immediate.Start!.IsNow.Should().BeTrue("a blank custom date is 'not given'");
        immediate.Schedule(Now).Should().Be(((DateTimeOffset?)null, Now.AddHours(1)));
        LfgRules.Validate("Deadlock", null, 6, 60, 20, 120).Draft!.Schedule(Now).Should().Be(((DateTimeOffset?)null, Now.AddHours(1)));
    }

    [Fact]
    public void Pings_work_with_a_custom_date_and_a_custom_date_needs_a_time_zone()
    {
        LfgRules.Validate("Deadlock", null, 6, null, 20, 120, notices: true, startAt: "05.10.2026 21:30", zone: Istanbul, now: Now)
            .Error.Should().Be(LfgDraftError.None);
        LfgRules.Validate("Deadlock", null, 6, null, 20, 120, notices: true, startAt: "05.10.2026 21:30", zone: null, now: Now)
            .Error.Should().Be(LfgDraftError.TimeZoneInvalid);
        LfgRules.Validate("Deadlock", null, 6, null, 20, 120, startAt: "05.10.2026 21:30", zone: Istanbul, now: Now).Draft!.Start!.At
            .Should().Be(new DateTimeOffset(2026, 10, 5, 18, 30, 0, TimeSpan.Zero));
    }
}
