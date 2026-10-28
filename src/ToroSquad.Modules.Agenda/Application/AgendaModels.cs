using ToroSquad.Core;
using ToroSquad.Core.Agenda;

namespace ToroSquad.Modules.Agenda.Application;

/// <summary>/ajanda (sports programme, 1–14 days) or /gundem (today's rest and tomorrow, sports and LFG).</summary>
public enum AgendaView
{
    Agenda = 0,
    Digest = 1,
}

/// <summary>The /ajanda <c>spor</c> option.</summary>
public enum AgendaSport
{
    All = 0,
    Formula1 = 1,
    Cs2 = 2,
    Volleyball = 3,
}

/// <summary>The /ajanda <c>donem</c> option: local calendar days of the guild's time zone, today included.</summary>
public enum AgendaPeriod
{
    Today = 0,
    Tomorrow = 1,
    SevenDays = 2,
    FourteenDays = 3,
}

/// <summary>The option values exactly as Discord sends them (choice values) and how they map.</summary>
public static class AgendaOptions
{
    public const string SportAll = "tum";
    public const string SportF1 = "f1";
    public const string SportCs2 = "cs2";
    public const string SportVolleyball = "voleybol";
    public const string PeriodToday = "bugun";
    public const string PeriodTomorrow = "yarin";
    public const string PeriodWeek = "7-gun";
    public const string PeriodTwoWeeks = "14-gun";

    /// <summary>Unknown or missing values fall back to the documented defaults (all sports, 7 days) — never to "nothing".</summary>
    public static AgendaSport Sport(string? value) => value switch
    {
        SportF1 => AgendaSport.Formula1,
        SportCs2 => AgendaSport.Cs2,
        SportVolleyball => AgendaSport.Volleyball,
        _ => AgendaSport.All,
    };

    public static AgendaPeriod Period(string? value) => value switch
    {
        PeriodToday => AgendaPeriod.Today,
        PeriodTomorrow => AgendaPeriod.Tomorrow,
        PeriodTwoWeeks => AgendaPeriod.FourteenDays,
        _ => AgendaPeriod.SevenDays,
    };

    /// <summary>The sources a view reads, in display order. /ajanda never reads LFG.</summary>
    public static IReadOnlyList<AgendaKind> Kinds(AgendaView view, AgendaSport sport) => view == AgendaView.Digest
        ? [AgendaKind.Formula1, AgendaKind.Esports, AgendaKind.Volleyball, AgendaKind.Lfg, AgendaKind.Predictions]
        : sport switch
        {
            AgendaSport.Formula1 => [AgendaKind.Formula1],
            AgendaSport.Cs2 => [AgendaKind.Esports],
            AgendaSport.Volleyball => [AgendaKind.Volleyball],
            _ => [AgendaKind.Formula1, AgendaKind.Esports, AgendaKind.Volleyball],
        };

    /// <summary>(first day offset from today, number of days).</summary>
    public static (int Offset, int Count) Days(AgendaView view, AgendaPeriod period) => view == AgendaView.Digest
        ? (0, 2)
        : period switch
        {
            AgendaPeriod.Today => (0, 1),
            AgendaPeriod.Tomorrow => (1, 1),
            AgendaPeriod.FourteenDays => (0, 14),
            _ => (0, 7),
        };
}

/// <summary>Who asked, where (the public card's channel), and what. The channel decides which LFG listings may be shown.</summary>
public sealed record AgendaRequest(GuildId Guild, ChannelId Channel, UserId Invoker, AgendaView View, AgendaSport Sport, AgendaPeriod Period);

/// <summary>One local calendar day of the guild and its UTC bounds [StartUtc, EndUtc) — converted separately (DST-safe).</summary>
public sealed record AgendaDay(DateOnly Date, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int OffsetFromToday);

/// <summary>
/// Local days in an IANA zone. Each day's midnight is converted on its own, so a DST day is 23 or 25 hours long, never a fixed
/// 24. A midnight that does not exist (a zone that springs forward at 00:00) starts at the first valid local minute; an
/// ambiguous one at its first occurrence.
/// </summary>
public static class AgendaCalendar
{
    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    public static DateTimeOffset DayStartUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static IReadOnlyList<AgendaDay> Days(DateTimeOffset now, TimeZoneInfo zone, int offset, int count)
    {
        var today = LocalDate(now, zone);
        var days = new List<AgendaDay>(count);
        for (var i = 0; i < count; i++)
        {
            var date = today.AddDays(offset + i);
            days.Add(new AgendaDay(date, DayStartUtc(date, zone), DayStartUtc(date.AddDays(1), zone), offset + i));
        }

        return days;
    }
}

/// <summary>
/// How one source answered, as the card reports it. <paramref name="Partial"/>: its data ends inside the window;
/// <paramref name="Unchecked"/>: some of its records could not be read or their channel could not be checked, so the list may
/// be incomplete (never with a count).
/// </summary>
public sealed record AgendaSourceView(AgendaKind Kind, AgendaSourceResult Result, bool Partial, bool Unchecked = false);

/// <summary>One day of the card: its items in time order, and (today only) the LFG listings looking for players now.</summary>
public sealed record AgendaDayGroup(AgendaDay Day, IReadOnlyList<AgendaItem> Now, IReadOnlyList<AgendaItem> Items)
{
    public int Count => Now.Count + Items.Count;
}

/// <summary>Everything one card shows, computed from ONE "now" (a refresh recomputes all of it, headings included).</summary>
public sealed record AgendaSnapshot(
    AgendaRequest Request,
    string Language,
    string TimeZoneId,
    DateTimeOffset Now,
    IReadOnlyList<AgendaDayGroup> Days,
    IReadOnlyList<AgendaSourceView> Sources);
