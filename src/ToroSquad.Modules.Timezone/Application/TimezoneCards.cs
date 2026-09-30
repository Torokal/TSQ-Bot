using System.Globalization;
using Microsoft.Extensions.Logging;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Modules.Timezone.Application;

/// <summary>
/// What /saat answers: a public <see cref="Card"/>, or a short <see cref="Refusal"/> that only the user who ran the command
/// sees. Never both.
/// </summary>
public sealed record TimezoneReply(MessageEmbed? Card, string? Refusal)
{
    public static TimezoneReply Refused(string text) => new(null, text);
}

/// <summary>
/// /saat end to end, without the Discord SDK (unit-tested): parse the time (or <c>now</c>), the source zone
/// (<see cref="SourceTimeZones"/>, default Türkiye), the optional date and the optional single target; read the time on that date
/// (default: today) in the source zone — refused if a DST switch skips or repeats it there — convert the one instant to every
/// <see cref="TimeZoneBoard.Zones"/> row (or only the target) and render the card in the TSQ card style (brand colour, emoji
/// title, fields, who asked in the footer). The Discord timestamp is a single field for the same instant — Discord renders
/// it in each viewer's own time zone, and the same tokens are shown once more as raw code to copy; the rows are the explicit
/// conversions. The display name is defused before it is shown.
/// Nothing here stores anything; only a missing time zone (no tz data on the host) is logged.
/// </summary>
public sealed class TimezoneCards(ILocalizer localizer, ILogger<TimezoneCards> logger)
{
    public const uint Color = 0xE8590C; // the TSQ brand colour, as on every other public card
    public const int DisplayNameMax = 64;
    public const string UnavailableKey = "timezone.unavailable";
    public const string SkippedKey = "timezone.skipped_time";
    public const string RepeatedKey = "timezone.repeated_time";

    public const string NowWithDateKey = "timezone.now_with_date";
    public const string InvalidTargetKey = "timezone.invalid_target";

    /// <summary>
    /// Inputs are checked in a fixed order — time (or <c>now</c>), source zone, date, target zone, then the DST check — so a
    /// request with several mistakes always gets the same refusal.
    /// </summary>
    /// <param name="zoneInput">The <c>timezone</c> option as typed; <c>null</c> = Türkiye, exactly as before the option existed.</param>
    /// <param name="dateInput">The <c>date</c> option; <c>null</c> = today in the source zone, exactly as before.</param>
    /// <param name="targetInput">The <c>to</c> option; <c>null</c> = the full board, exactly as before.</param>
    public TimezoneReply Convert(string lang, string? input, DateTimeOffset now, string displayName, string? zoneInput = null,
        string? dateInput = null, string? targetInput = null)
    {
        var isNow = ClockInput.IsNow(input);
        var parsedTime = isNow ? null : ClockInput.Parse(input);
        if (!isNow && parsedTime is null)
            return TimezoneReply.Refused(L(lang, ClockInput.InvalidKey));
        if (SourceTimeZones.Resolve(zoneInput) is not { } from)
            return TimezoneReply.Refused(L(lang, SourceTimeZones.InvalidKey));
        if (Unavailable(lang, [from.ZoneId], out var zones) is { } noSource)
            return noSource;
        var source = zones[from.ZoneId];

        // "Today" and a date's missing year come from the source zone at this instant — not the host's, UTC's or Istanbul's.
        var today = TimeZoneBoard.Today(now, source);
        DateOnly? explicitDate = null;
        if (!string.IsNullOrWhiteSpace(dateInput))
        {
            if (isNow)
                return TimezoneReply.Refused(L(lang, NowWithDateKey));
            explicitDate = DateInput.Parse(dateInput, today.Year);
            if (explicitDate is null)
                return TimezoneReply.Refused(L(lang, DateInput.InvalidKey));
        }

        SourceZone? to = null;
        if (!string.IsNullOrWhiteSpace(targetInput))
        {
            to = SourceTimeZones.Resolve(targetInput);
            if (to is null)
                return TimezoneReply.Refused(L(lang, InvalidTargetKey));
        }

        IReadOnlyList<BoardZone> rows = to is null ? TimeZoneBoard.Zones : [new BoardZone(to.Flag, to.NameKey, to.ZoneId)];
        if (Unavailable(lang, rows.Select(z => z.ZoneId), out var targets) is { } noTarget)
            return noTarget;

        DateOnly date;
        TimeOnly time;
        DateTimeOffset instant;
        if (isNow)
        {
            // The clock's instant itself: never rebuilt from a wall-clock time, so the timestamp keeps its seconds.
            instant = now;
            date = today;
            time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, source).DateTime);
        }
        else
        {
            date = explicitDate ?? today;
            time = parsedTime!.Value;
            switch (TimeZoneBoard.Classify(date, time, source))
            {
                case LocalTimeKind.Skipped:
                    return TimezoneReply.Refused(L(lang, SkippedKey));
                case LocalTimeKind.Repeated:
                    return TimezoneReply.Refused(L(lang, RepeatedKey));
            }

            instant = TimeZoneBoard.Resolve(date, time, source);
        }

        var converted = rows.Select(z => TimeZoneBoard.In(z, instant, date, targets[z.ZoneId]));
        var fields = new List<EmbedField>
        {
            new(L(lang, "timezone.input"), L(lang, "timezone.input.value", Clock(time),
                L(lang, from.NameKey), date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))),
            new(L(lang, to is null ? "timezone.zones" : "timezone.target"), string.Join("\n", converted.Select(r => Row(lang, r)))),
            new(L(lang, "timezone.discord"), DiscordText.Timestamp(instant, 't') + " · " + DiscordText.Timestamp(instant, 'R')),
            // The same two tokens as inline code: Discord shows them raw, ready to copy into a message.
            new(L(lang, "timezone.discord_code"), "`" + DiscordText.Timestamp(instant, 't') + "`\n`" + DiscordText.Timestamp(instant, 'R') + "`"),
        };
        return new TimezoneReply(new MessageEmbed(L(lang, "timezone.title"), null, null, fields,
            L(lang, "timezone.footer", DiscordText.UntrustedPlain(displayName, DisplayNameMax)), null, Color), null);
    }

    /// <summary>Resolves the zone ids; a refusal (and an error log) when the host has no data for one of them.</summary>
    private TimezoneReply? Unavailable(string lang, IEnumerable<string> zoneIds, out Dictionary<string, TimeZoneInfo> zones)
    {
        var missing = TimeZoneBoard.Missing(zoneIds, out zones);
        if (missing.Count == 0)
            return null;
        logger.LogError("Timezone /saat: time zone data not found on this host for {ZoneIds}", string.Join(",", missing));
        return TimezoneReply.Refused(L(lang, UnavailableKey));
    }

    /// <summary><c>🇬🇧 United Kingdom — `19:00`</c>, plus <c>· Previous day</c> / <c>· Next day</c> when the date differs.</summary>
    private string Row(string lang, ZoneTime row)
    {
        var line = row.Zone.Flag + " " + L(lang, row.Zone.NameKey) + " — `" + Clock(TimeOnly.FromDateTime(row.Local.DateTime)) + "`";
        return row.DayShift switch
        {
            < 0 => line + " · " + L(lang, "timezone.previous_day"),
            > 0 => line + " · " + L(lang, "timezone.next_day"),
            _ => line,
        };
    }

    private static string Clock(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Formats the template here (not through the localizer's arguments), exactly like the other TSQ cards.</summary>
    private string L(string lang, string key, params object?[] args) =>
        args.Length == 0 ? localizer.Get(lang, key) : string.Format(CultureInfo.InvariantCulture, localizer.Get(lang, key), args);
}
