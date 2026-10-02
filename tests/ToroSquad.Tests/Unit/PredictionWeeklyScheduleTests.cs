using ToroSquad.Core.Guilds;
using ToroSquad.Modules.Predictions;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// The weekly slot and its settings, network- and database-free: the zone's own rules (never the host's), the catch-up
/// window, the ISO week key of the LOCAL date (also across a year boundary), daylight-saving gaps and overlaps, and the
/// configuration's clear errors.
/// </summary>
public sealed class PredictionWeeklyScheduleTests
{
    private static TimeZoneInfo Zone(string id) => GuildTime.TryResolve(id, out var zone) ? zone : throw new InvalidOperationException(id);

    private static WeeklySchedule Sunday20(string zone = "Europe/Istanbul", int catchUpHours = 12) =>
        new(DayOfWeek.Sunday, new TimeOnly(20, 0), Zone(zone), TimeSpan.FromHours(catchUpHours));

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0, int year = 2026) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Sunday_20_00_in_turkiye_is_17_00_utc_and_due_for_the_catch_up_window_only()
    {
        var schedule = Sunday20();
        schedule.LatestSlot(Utc(10, 4, 16, 59)).Should().Be(Utc(9, 27, 17), "19:59 TR: still last week's slot");
        schedule.Due(Utc(10, 4, 16, 59)).Should().BeNull();
        schedule.Due(Utc(10, 4, 17)).Should().Be(Utc(10, 4, 17));
        schedule.Due(Utc(10, 5, 4, 59)).Should().Be(Utc(10, 4, 17), "Monday 07:59 TR: inside 12 h");
        schedule.Due(Utc(10, 5, 5)).Should().BeNull("Monday 08:00 TR: the window is over");
        schedule.Due(Utc(10, 6, 6)).Should().BeNull("Tuesday");
        Sunday20(catchUpHours: 2).Due(Utc(10, 4, 19, 30)).Should().BeNull();
    }

    [Fact]
    public void The_week_key_is_the_iso_week_of_the_local_date_also_across_the_new_year()
    {
        var schedule = Sunday20();
        schedule.WeekKey(Utc(10, 4, 17)).Should().Be(202640);
        schedule.WeekKey(Utc(10, 11, 17)).Should().Be(202641);
        schedule.WeekKey(Utc(1, 3, 17, year: 2027)).Should().Be(202653, "Sunday 3 January 2027 still belongs to ISO week 53 of 2026");
        schedule.WeekKey(Utc(1, 4, 17)).Should().Be(202601, "Sunday 4 January 2026 is ISO week 1 of 2026");

        var monday = new WeeklySchedule(DayOfWeek.Monday, new TimeOnly(0, 30), Zone("Europe/Istanbul"), TimeSpan.FromHours(12));
        var slot = monday.LatestSlot(Utc(10, 4, 22)); // Monday 5 Oct 01:00 TR = Sunday 22:00 UTC
        (slot, monday.WeekKey(slot)).Should().Be((Utc(10, 4, 21, 30), 202641), "the LOCAL date decides the week, not the UTC date");
    }

    [Fact]
    public void A_zone_with_daylight_saving_keeps_the_local_time_on_both_sides_of_the_change()
    {
        var berlin = Sunday20("Europe/Berlin");
        berlin.LatestSlot(Utc(10, 18, 19)).Should().Be(Utc(10, 18, 18), "summer time: 20:00 = 18:00 UTC");
        berlin.LatestSlot(Utc(11, 1, 20)).Should().Be(Utc(11, 1, 19), "winter time: 20:00 = 19:00 UTC");

        var gap = new WeeklySchedule(DayOfWeek.Sunday, new TimeOnly(2, 30), Zone("America/New_York"), TimeSpan.FromHours(12));
        gap.LatestSlot(Utc(3, 8, 12)).Should().Be(Utc(3, 8, 7), "02:30 does not exist on 8 March 2026: the first valid minute, 03:00 EDT");
        var overlap = new WeeklySchedule(DayOfWeek.Sunday, new TimeOnly(1, 30), Zone("America/New_York"), TimeSpan.FromHours(12));
        overlap.LatestSlot(Utc(11, 1, 12)).Should().Be(Utc(11, 1, 5, 30), "01:30 happens twice on 1 November 2026: the first one (EDT)");
    }

    [Fact]
    public void The_defaults_are_sunday_20_00_istanbul_with_12_h_catch_up_and_12_h_card_retention()
    {
        var options = new PredictionsOptions();
        options.Validate().Should().BeEmpty();
        options.TerminalCardRetention.Should().Be(TimeSpan.FromHours(12));
        var schedule = options.WeeklyLeaderboard.Schedule()!;
        (options.WeeklyLeaderboard.Enabled, schedule.Day, schedule.Time, schedule.Zone.Id, schedule.CatchUp)
            .Should().Be((true, DayOfWeek.Sunday, new TimeOnly(20, 0), Zone("Europe/Istanbul").Id, TimeSpan.FromHours(12)));
    }

    [Fact]
    public void Invalid_settings_are_clear_config_errors_and_give_no_schedule()
    {
        var options = new PredictionsOptions
        {
            TerminalCardRetentionHours = 0,
            WeeklyLeaderboard = new WeeklyLeaderboardOptions { DayOfWeek = "Pazar", LocalTime = "8pm", TimeZone = "Mars/Olympus", CatchUpHours = 0 },
        };
        options.Validate().Should().BeEquivalentTo(
            "Predictions:TerminalCardRetentionHours must be 1-168 (got 0)",
            "Predictions:WeeklyLeaderboard:DayOfWeek must be Monday-Sunday (got Pazar)",
            "Predictions:WeeklyLeaderboard:LocalTime must be HH:mm (got 8pm)",
            "Predictions:WeeklyLeaderboard:TimeZone must be an IANA time zone (got Mars/Olympus)",
            "Predictions:WeeklyLeaderboard:CatchUpHours must be 1-72 (got 0)");
        options.WeeklyLeaderboard.Schedule().Should().BeNull();
        new WeeklyLeaderboardOptions { DayOfWeek = "3" }.Validate().Should().ContainSingle("a number is not a day name");
        new WeeklyLeaderboardOptions { DayOfWeek = "saturday", LocalTime = "09:15" }.Schedule()!.Day.Should().Be(DayOfWeek.Saturday);
    }
}
