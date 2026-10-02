using System.Globalization;

namespace ToroSquad.Modules.Predictions.Domain;

/// <summary>
/// THE weekly slot logic (one place): a day and a local time in one IANA zone, never the host's zone. A slot is due from its
/// instant for <see cref="CatchUp"/>; a bot that was down longer skips that week instead of posting it late. The week key is
/// the ISO week of the slot's LOCAL date (yyyyww), so one guild gets at most one post per calendar week even if the day or
/// time setting changes during that week. A local time that does not exist (a spring-forward gap) moves forward to the
/// first valid minute; an ambiguous one (fall-back) is its first occurrence.
/// </summary>
public sealed record WeeklySchedule(DayOfWeek Day, TimeOnly Time, TimeZoneInfo Zone, TimeSpan CatchUp)
{
    /// <summary>The latest slot at or before <paramref name="now"/>.</summary>
    public DateTimeOffset LatestSlot(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        var date = DateOnly.FromDateTime(local.DateTime).AddDays(-(((int)local.DayOfWeek - (int)Day + 7) % 7));
        var slot = ToInstant(date);
        return slot <= now ? slot : ToInstant(date.AddDays(-7));
    }

    /// <summary>The slot to post now, or null outside its catch-up window.</summary>
    public DateTimeOffset? Due(DateTimeOffset now)
    {
        var slot = LatestSlot(now);
        return now - slot < CatchUp ? slot : null;
    }

    /// <summary>yyyyww: the ISO week (and ISO year) of the slot's local date.</summary>
    public int WeekKey(DateTimeOffset slot)
    {
        var local = TimeZoneInfo.ConvertTime(slot, Zone).DateTime;
        return ISOWeek.GetYear(local) * 100 + ISOWeek.GetWeekOfYear(local);
    }

    private DateTimeOffset ToInstant(DateOnly date)
    {
        var local = date.ToDateTime(Time, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
