using System.Globalization;
using System.Text.Json;
using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Modules.Formula1.Providers.OpenF1;

/// <summary>
/// Parses OpenF1 v1 JSON (documentation/includes/_api_endpoints.md and pages/auth.html in github.com/br-g/openf1, and
/// live payload shapes, verified 2026-09-25). Contract notes the rest of the module relies on:
/// <list type="bullet">
/// <item>Lifecycle = race_control rows with category "SessionStatus" and message "SESSION STARTED" / "SESSION ABORTED"
/// (stopped, e.g. red flag) / "SESSION FINISHED". Qualifying formats emit FINISHED after each segment
/// (qualifying_phase 1, 2, 3).</item>
/// <item>session_result: position is null for DNF/DNS/DSQ; duration and gap_to_leader are numbers (practice/race),
/// arrays of three (qualifying), "+N LAP(S)" strings for lapped drivers, or null.</item>
/// <item>An empty query answers HTTP 404 {"detail":"No results found."} — that is "no data yet", not an error.</item>
/// <item>MQTT messages mirror the REST objects plus "_id" (increasing) and "_key" (document id).</item>
/// </list>
/// Unknown/new fields are ignored.
/// </summary>
public static class OpenF1Parser
{
    public const string Source = "openf1";
    public const string EmptyResultDetail = "No results found";

    public static bool IsDocumentedEmptyResult(string body) =>
        body.Contains(EmptyResultDetail, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<F1ProviderSession> ParseSessions(JsonElement root)
    {
        var sessions = new List<F1ProviderSession>();
        foreach (var row in Array(root).EnumerateArray())
        {
            var key = Int(row, "session_key");
            var year = Int(row, "year");
            var start = Instant(row, "date_start");
            if (key is null || year is null || start is null)
                continue; // incomplete row: never mapped
            sessions.Add(new F1ProviderSession(
                Source,
                key.Value.ToString(CultureInfo.InvariantCulture),
                year.Value,
                F1SessionTypes.FromProviderName(Str(row, "session_name")),
                start.Value,
                Instant(row, "date_end"),
                Bool(row, "is_cancelled") ?? false,
                Str(row, "location")));
        }

        return sessions;
    }

    public static IReadOnlyList<F1LifecycleEvent> ParseLifecycleEvents(JsonElement root) =>
        Array(root).EnumerateArray().Select(ParseLifecycleEvent).OfType<F1LifecycleEvent>().OrderBy(e => e.OccurredAt).ToList();

    /// <summary>Null for anything that is not an unambiguous session status message.</summary>
    public static F1LifecycleEvent? ParseLifecycleEvent(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || Str(row, "category") != "SessionStatus")
            return null;
        var signal = Str(row, "message")?.ToUpperInvariant() switch
        {
            "SESSION STARTED" => F1LifecycleSignal.Started,
            "SESSION ABORTED" => F1LifecycleSignal.Suspended,
            "SESSION FINISHED" or "SESSION FINALISED" => F1LifecycleSignal.Finished,
            _ => (F1LifecycleSignal?)null,
        };
        var session = Int(row, "session_key");
        var at = Instant(row, "date");
        if (signal is null || session is null || at is null)
            return null;
        return new F1LifecycleEvent(Source, session.Value.ToString(CultureInfo.InvariantCulture), signal.Value, at.Value, Int(row, "qualifying_phase"));
    }

    /// <summary>Parses one MQTT message; returns the event (or null) and the provider's message identity for dedupe.</summary>
    public static (F1LifecycleEvent? Event, string? MessageId) ParseMqttMessage(string topic, string payload)
    {
        if (topic != OpenF1Topics.RaceControl)
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("_id", out var i) ? i.ToString() : null;
            var key = Str(root, "_key");
            return (ParseLifecycleEvent(root), key is null && id is null ? null : key + "#" + id);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Joins session_result with the session's driver list into a normalized classification.</summary>
    public static F1SessionResult ParseSessionResult(JsonElement resultRoot, JsonElement? driversRoot, F1Session session)
    {
        var drivers = new Dictionary<int, JsonElement>();
        var roster = new List<int>(); // kept with duplicates: a duplicated roster entry is ambiguous and fails validation
        if (driversRoot is { } dr)
        {
            foreach (var d in Array(dr).EnumerateArray())
            {
                if (Int(d, "driver_number") is { } n)
                {
                    drivers[n] = d;
                    roster.Add(n);
                }
            }
        }

        var entries = new List<F1DriverResult>();
        foreach (var row in Array(resultRoot).EnumerateArray())
        {
            var number = Int(row, "driver_number") ?? throw new JsonException("result row without driver_number");
            drivers.TryGetValue(number, out var driver);
            var status = Bool(row, "dsq") == true ? F1ResultStatus.Dsq
                : Bool(row, "dns") == true ? F1ResultStatus.Dns
                : Bool(row, "dnf") == true ? F1ResultStatus.Dnf
                : Int(row, "position") is null ? F1ResultStatus.NotClassified
                : F1ResultStatus.Classified;
            var (gapSeconds, gapLaps) = Gap(row);
            entries.Add(new F1DriverResult(
                Int(row, "position"),
                number,
                DriverName(driver),
                Str(driver, "name_acronym"),
                Str(driver, "team_name"),
                status,
                Int(row, "number_of_laps"),
                LastNumber(row, "duration"),
                gapSeconds,
                gapLaps,
                Double(row, "points")));
        }

        // The session roster makes completeness provable: without a driver list nothing can be proven complete.
        return new F1SessionResult(session.Key, session.Type, Source, entries, roster.Order().ToList());
    }

    /// <summary>"First Last" from first_name/last_name (the full_name field upper-cases the family name).</summary>
    private static string DriverName(JsonElement driver)
    {
        if (driver.ValueKind != JsonValueKind.Object)
            return "";
        var first = Str(driver, "first_name");
        var last = Str(driver, "last_name");
        if (first is not null && last is not null)
            return first + " " + last;
        return Str(driver, "full_name") ?? Str(driver, "broadcast_name") ?? "";
    }

    private static (double? Seconds, int? Laps) Gap(JsonElement row)
    {
        if (!row.TryGetProperty("gap_to_leader", out var g))
            return (null, null);
        if (g.ValueKind == JsonValueKind.Array)
            g = g.EnumerateArray().LastOrDefault(x => x.ValueKind is JsonValueKind.Number or JsonValueKind.String);
        return g.ValueKind switch
        {
            JsonValueKind.Number => (g.GetDouble(), null),
            JsonValueKind.String => (null, LapsBehind(g.GetString())),
            _ => (null, null),
        };
    }

    /// <summary>"+1 LAP" / "+3 LAPS" → 1 / 3; anything else is not interpreted.</summary>
    private static int? LapsBehind(string? text)
    {
        if (text is null)
            return null;
        var parts = text.Trim().TrimStart('+').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[1].StartsWith("LAP", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>For qualifying arrays [Q1, Q2, Q3] the last segment with a value is the driver's final time.</summary>
    private static double? LastNumber(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (double?)x.GetDouble()).LastOrDefault();
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    private static JsonElement Array(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root : throw new JsonException("response is not a JSON array");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            }
            : null;

    private static double? Double(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static DateTimeOffset? Instant(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at.ToUniversalTime()
            : null;
}

/// <summary>MQTT topics = REST endpoint paths (OpenF1 auth guide, "Topics").</summary>
public static class OpenF1Topics
{
    public const string RaceControl = "v1/race_control";
}
