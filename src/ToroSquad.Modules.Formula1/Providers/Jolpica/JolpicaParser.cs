using System.Globalization;
using System.Text.Json;
using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Modules.Formula1.Providers.Jolpica;

/// <summary>
/// Parses Jolpica-F1's Ergast-compatible JSON (MRData envelope; docs/endpoints/races.md, driverStandings.md,
/// constructorStandings.md in github.com/jolpica/jolpica-f1, verified 2026-09-25). Tolerant of unknown/new fields;
/// strict about the envelope. A session without an exact UTC time is skipped (never given an invented time).
/// </summary>
public static class JolpicaParser
{
    public const string Source = "jolpica";

    private static readonly (string Property, F1SessionType Type)[] SessionProperties =
    [
        ("FirstPractice", F1SessionType.Practice1),
        ("SecondPractice", F1SessionType.Practice2),
        ("ThirdPractice", F1SessionType.Practice3),
        ("SprintQualifying", F1SessionType.SprintQualifying),
        ("SprintShootout", F1SessionType.SprintQualifying), // 2023 naming — same normalized session
        ("Sprint", F1SessionType.Sprint),
        ("Qualifying", F1SessionType.Qualifying),
    ];

    public static F1SeasonSchedule ParseSchedule(JsonElement root, int season)
    {
        var races = Table(root, "RaceTable").GetProperty("Races");
        var meetings = new List<F1Meeting>();
        foreach (var race in races.EnumerateArray())
        {
            var raceSeason = IntOf(race, "season") ?? throw new JsonException("race without season");
            var round = IntOf(race, "round") ?? throw new JsonException("race without round");
            if (raceSeason != season)
                continue;

            var sessions = new List<F1Session>();
            foreach (var (property, type) in SessionProperties)
            {
                if (race.TryGetProperty(property, out var s) && Instant(s) is { } at && sessions.All(x => x.Type != type))
                    sessions.Add(new F1Session(raceSeason, round, type, at));
            }

            if (Instant(race) is { } raceStart)
                sessions.Add(new F1Session(raceSeason, round, F1SessionType.Race, raceStart));

            var circuit = race.TryGetProperty("Circuit", out var c) ? c : default;
            var location = circuit.ValueKind == JsonValueKind.Object && circuit.TryGetProperty("Location", out var l) ? l : default;
            meetings.Add(new F1Meeting(
                raceSeason,
                round,
                Str(race, "raceName") ?? throw new JsonException("race without raceName"),
                null,
                Str(circuit, "circuitName") ?? "",
                Str(location, "country"),
                Str(location, "locality"),
                Str(circuit, "circuitId") ?? "",
                sessions.OrderBy(s => s.ScheduledStartUtc).ThenBy(s => F1SessionTypes.Order(s.Type)).ToList()));
        }

        return new F1SeasonSchedule(season, Source, meetings.OrderBy(m => m.Round).ToList());
    }

    public static F1StandingsSnapshot ParseDriverStandings(JsonElement root, int season)
    {
        var (listSeason, round, list) = StandingsList(root, season);
        var rows = new List<F1DriverStanding>();
        if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("DriverStandings", out var standings))
        {
            foreach (var row in standings.EnumerateArray())
            {
                var driver = row.GetProperty("Driver");
                var name = string.Join(' ', new[] { Str(driver, "givenName"), Str(driver, "familyName") }.Where(p => !string.IsNullOrWhiteSpace(p)));
                var team = row.TryGetProperty("Constructors", out var cs) && cs.ValueKind == JsonValueKind.Array && cs.GetArrayLength() > 0
                    ? Str(cs[cs.GetArrayLength() - 1], "name")
                    : null;
                rows.Add(new F1DriverStanding(
                    IntOf(row, "position"),
                    Str(driver, "driverId") ?? throw new JsonException("driver without driverId"),
                    name.Length > 0 ? name : Str(driver, "driverId")!,
                    Str(driver, "code"),
                    team,
                    DecimalOf(row, "points") ?? throw new JsonException("standing without points"),
                    IntOf(row, "wins") ?? 0));
            }
        }

        return new F1StandingsSnapshot(F1StandingsKind.Drivers, listSeason, round, Source, rows, []);
    }

    public static F1StandingsSnapshot ParseConstructorStandings(JsonElement root, int season)
    {
        var (listSeason, round, list) = StandingsList(root, season);
        var rows = new List<F1ConstructorStanding>();
        if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("ConstructorStandings", out var standings))
        {
            foreach (var row in standings.EnumerateArray())
            {
                var constructor = row.GetProperty("Constructor");
                rows.Add(new F1ConstructorStanding(
                    IntOf(row, "position"),
                    Str(constructor, "constructorId") ?? throw new JsonException("constructor without constructorId"),
                    Str(constructor, "name") ?? Str(constructor, "constructorId")!,
                    DecimalOf(row, "points") ?? throw new JsonException("standing without points"),
                    IntOf(row, "wins") ?? 0));
            }
        }

        return new F1StandingsSnapshot(F1StandingsKind.Constructors, listSeason, round, Source, [], rows);
    }

    /// <summary>A season without any standings yet is a valid, empty table (not an error).</summary>
    private static (int Season, int? Round, JsonElement List) StandingsList(JsonElement root, int season)
    {
        var lists = Table(root, "StandingsTable").GetProperty("StandingsLists");
        if (lists.GetArrayLength() == 0)
            return (season, null, default);
        var list = lists[0];
        var listSeason = IntOf(list, "season") ?? season;
        if (listSeason != season)
            throw new JsonException($"standings for season {listSeason}, expected {season}");
        return (listSeason, IntOf(list, "round"), list);
    }

    private static JsonElement Table(JsonElement root, string name) =>
        root.TryGetProperty("MRData", out var mr) && mr.TryGetProperty(name, out var table)
            ? table
            : throw new JsonException($"missing MRData.{name}");

    /// <summary>Only "date" + "time" (UTC, "HH:mm:ssZ") is an exact instant; a date alone is not.</summary>
    private static DateTimeOffset? Instant(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Str(e, "date") is not { } date || Str(e, "time") is not { } time)
            return null;
        return DateTimeOffset.TryParse(date + "T" + time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static int? IntOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            }
            : null;

    private static decimal? DecimalOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetDecimal(out var n) => n,
                JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            }
            : null;
}
