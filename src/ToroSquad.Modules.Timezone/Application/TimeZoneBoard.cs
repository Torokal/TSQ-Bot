using ToroSquad.Core.Guilds;

namespace ToroSquad.Modules.Timezone.Application;

/// <summary>A row of the /saat card: flag, localization key of the place name, IANA time zone id.</summary>
public sealed record BoardZone(string Flag, string NameKey, string ZoneId);

/// <summary>
/// A wall-clock time on a date in a zone: it exists once (<see cref="Valid"/>), not at all because the clocks jumped forward
/// over it (<see cref="Skipped"/>), or twice because they were set back over it (<see cref="Repeated"/>).
/// </summary>
public enum LocalTimeKind
{
    Valid,
    Skipped,
    Repeated,
}

/// <summary>The instant in one zone: its local wall-clock time and how many calendar days it is from the entered date.</summary>
public sealed record ZoneTime(BoardZone Zone, DateTimeOffset Local, int DayShift);

/// <summary>
/// The zones /saat shows and the conversion itself. The entered time is read in the source zone (<see cref="SourceZoneId"/>
/// unless the user chose another one, <see cref="SourceTimeZones"/>) on today's date there; every row is that one instant converted with <see cref="TimeZoneInfo"/> — the OS time zone database by IANA id
/// (Linux: /usr/share/zoneinfo from tzdata; Windows: ICU mapping), so DST follows the real rules of each zone and its date.
/// There is no offset table anywhere. To add a zone: one line in <see cref="Zones"/> and its name in both catalogs.
/// </summary>
public static class TimeZoneBoard
{
    public const string SourceZoneId = GuildSettings.DefaultTimeZoneId; // Europe/Istanbul

    public static IReadOnlyList<BoardZone> Zones { get; } =
    [
        new("🇹🇷", "timezone.zone.turkey", "Europe/Istanbul"),
        new("🇬🇧", "timezone.zone.uk", "Europe/London"),
        new("🇺🇸", "timezone.zone.new_york", "America/New_York"),
        new("🇺🇸", "timezone.zone.chicago", "America/Chicago"),
        new("🇺🇸", "timezone.zone.los_angeles", "America/Los_Angeles"),
    ];

    /// <summary>The calendar date in <paramref name="zone"/> at <paramref name="now"/> (not the UTC date).</summary>
    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    /// <summary>
    /// Whether <paramref name="time"/> exists on <paramref name="date"/> in <paramref name="zone"/> (DST switches): /saat refuses
    /// a skipped or repeated time instead of picking an instant for the user.
    /// </summary>
    public static LocalTimeKind Classify(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return zone.IsInvalidTime(local) ? LocalTimeKind.Skipped
            : zone.IsAmbiguousTime(local) ? LocalTimeKind.Repeated
            : LocalTimeKind.Valid;
    }

    /// <summary>
    /// The instant at which <paramref name="zone"/>'s wall clock shows <paramref name="time"/> on <paramref name="date"/>.
    /// Never throws: a wall time that falls in a DST gap or overlap takes the zone's standard offset — which is why /saat
    /// checks <see cref="Classify"/> first and never lets that fallback choose for the user.
    /// </summary>
    public static DateTimeOffset Resolve(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary><paramref name="instant"/> in <paramref name="target"/>, with the day difference to <paramref name="sourceDate"/>.</summary>
    public static ZoneTime In(BoardZone row, DateTimeOffset instant, DateOnly sourceDate, TimeZoneInfo target)
    {
        var local = TimeZoneInfo.ConvertTime(instant, target);
        return new ZoneTime(row, local, DateOnly.FromDateTime(local.DateTime).DayNumber - sourceDate.DayNumber);
    }

    /// <summary>Resolves every zone id through the shared <see cref="GuildTime.TryResolve"/>; the ids that are unknown here.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string> zoneIds, out Dictionary<string, TimeZoneInfo> resolved)
    {
        resolved = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in zoneIds.Distinct(StringComparer.Ordinal))
        {
            if (GuildTime.TryResolve(id, out var zone))
                resolved[id] = zone;
            else
                missing.Add(id);
        }

        return missing;
    }
}
