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
/// /saat end to end, without the Discord SDK (unit-tested): parse the time and the source zone (<see cref="SourceTimeZones"/>,
/// default Türkiye), read the time as today in that zone — refused if a DST switch skips or repeats it there — convert the one
/// instant to every <see cref="TimeZoneBoard.Zones"/> row and render the card in the TSQ card style (brand colour, emoji
/// title, fields, who asked in the footer). The Discord timestamp is a single field for the same instant — Discord renders
/// it in each viewer's own time zone; the rows are the explicit conversions. The display name is defused before it is shown.
/// Nothing here stores anything; only a missing time zone (no tz data on the host) is logged.
/// </summary>
public sealed class TimezoneCards(ILocalizer localizer, ILogger<TimezoneCards> logger)
{
    public const uint Color = 0xE8590C; // the TSQ brand colour, as on every other public card
    public const int DisplayNameMax = 64;
    public const string UnavailableKey = "timezone.unavailable";
    public const string SkippedKey = "timezone.skipped_time";
    public const string RepeatedKey = "timezone.repeated_time";

    /// <param name="zoneInput">The <c>timezone</c> option as typed; <c>null</c> = Türkiye, exactly as before the option existed.</param>
    public TimezoneReply Convert(string lang, string? input, DateTimeOffset now, string displayName, string? zoneInput = null)
    {
        if (ClockInput.Parse(input) is not { } time)
            return TimezoneReply.Refused(L(lang, ClockInput.InvalidKey));
        if (SourceTimeZones.Resolve(zoneInput) is not { } from)
            return TimezoneReply.Refused(L(lang, SourceTimeZones.InvalidKey));

        var missing = TimeZoneBoard.Missing(TimeZoneBoard.Zones.Select(z => z.ZoneId).Append(from.ZoneId), out var zones);
        if (missing.Count > 0)
        {
            logger.LogError("Timezone /saat: time zone data not found on this host for {ZoneIds}", string.Join(",", missing));
            return TimezoneReply.Refused(L(lang, UnavailableKey));
        }

        // "Today" is the date in the source zone at this instant — not the host's, not UTC's, not Istanbul's.
        var source = zones[from.ZoneId];
        var date = TimeZoneBoard.Today(now, source);
        switch (TimeZoneBoard.Classify(date, time, source))
        {
            case LocalTimeKind.Skipped:
                return TimezoneReply.Refused(L(lang, SkippedKey));
            case LocalTimeKind.Repeated:
                return TimezoneReply.Refused(L(lang, RepeatedKey));
        }

        var instant = TimeZoneBoard.Resolve(date, time, source);
        var rows = TimeZoneBoard.Zones.Select(z => TimeZoneBoard.In(z, instant, date, zones[z.ZoneId]));

        var fields = new List<EmbedField>
        {
            new(L(lang, "timezone.input"), L(lang, "timezone.input.value", Clock(time),
                L(lang, from.NameKey), date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))),
            new(L(lang, "timezone.zones"), string.Join("\n", rows.Select(r => Row(lang, r)))),
            new(L(lang, "timezone.discord"), DiscordText.Timestamp(instant, 't') + " · " + DiscordText.Timestamp(instant, 'R')),
        };
        return new TimezoneReply(new MessageEmbed(L(lang, "timezone.title"), null, null, fields,
            L(lang, "timezone.footer", DiscordText.UntrustedPlain(displayName, DisplayNameMax)), null, Color), null);
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
