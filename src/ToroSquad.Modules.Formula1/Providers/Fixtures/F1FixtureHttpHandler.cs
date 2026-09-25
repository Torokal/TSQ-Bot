using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Modules.Formula1.Providers.Fixtures;

/// <summary>
/// Formula1:Provider:Mode=Fixture — serves Jolpica- and OpenF1-shaped responses generated from the SYNTHETIC timeline in
/// Fixtures/f1-demo-timeline.json, so fixture mode runs the real HTTP clients, parsers, lifecycle state machine, results
/// and standings workflow. Everything is relative to a persisted anchor: events appear only once their time has passed,
/// classifications a few minutes after a session ends (404 "No results found." before that, exactly like OpenF1), and
/// standings change after the sprint/race. Fictional names only — never real data, never a real source.
/// </summary>
public sealed class F1FixtureHttpHandler(TimeProvider clock, F1FixtureAnchor? anchor = null, string? timelineJson = null) : HttpMessageHandler
{
    private static readonly int[] RacePoints = [25, 18, 15, 12, 10, 8, 6, 4, 2, 1];
    private static readonly int[] SprintPoints = [8, 7, 6, 5, 4, 3, 2, 1];

    private readonly Lazy<JsonNode> _timeline = new(() => JsonNode.Parse(timelineJson ?? ReadEmbedded("f1-demo-timeline.json"))!);

    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestCount++;
        var now = clock.GetUtcNow();
        var origin = anchor is null ? now : await anchor.GetAsync(cancellationToken);
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath.TrimEnd('/');
        var query = HttpUtility.ParseQueryString(uri.Query);
        var timeline = Timeline.Load(_timeline.Value, origin);

        if (path.EndsWith("/races", StringComparison.Ordinal))
            return Json(JolpicaRaces(timeline, SeasonFrom(path)));
        if (path.EndsWith("/driverstandings", StringComparison.Ordinal) || path.EndsWith("/constructorstandings", StringComparison.Ordinal))
            return Json(JolpicaStandings(timeline, SeasonFrom(path), path.EndsWith("/driverstandings", StringComparison.Ordinal), now));

        if (path.EndsWith("/v1/sessions", StringComparison.Ordinal))
            return Json(OpenF1Sessions(timeline, int.Parse(query["year"] ?? "0", CultureInfo.InvariantCulture)));
        if (path.EndsWith("/v1/race_control", StringComparison.Ordinal))
            return OrEmpty(OpenF1RaceControl(timeline, query["session_key"], now));
        if (path.EndsWith("/v1/session_result", StringComparison.Ordinal))
            return OrEmpty(OpenF1Result(timeline, query["session_key"], now));
        if (path.EndsWith("/v1/drivers", StringComparison.Ordinal))
            return OrEmpty(OpenF1Drivers(timeline, query["session_key"]));

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\":\"Not Found\"}", Encoding.UTF8, "application/json") };
    }

    private static int SeasonFrom(string path)
    {
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 4 && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
                return year;
        }

        return 0;
    }

    // ---------------------------------------------------------------- Jolpica shapes

    private static string JolpicaRaces(Timeline t, int season)
    {
        var races = new JsonArray();
        foreach (var m in t.Meetings.Where(m => m.Season == season))
        {
            var race = new JsonObject
            {
                ["season"] = season.ToString(CultureInfo.InvariantCulture),
                ["round"] = m.Round.ToString(CultureInfo.InvariantCulture),
                ["url"] = "https://example.com/tsq-bot-demo-grand-prix",
                ["raceName"] = m.Name,
                ["Circuit"] = new JsonObject
                {
                    ["circuitId"] = m.CircuitId,
                    ["url"] = "https://example.com/tsq-bot-demo-circuit",
                    ["circuitName"] = m.Circuit,
                    ["Location"] = new JsonObject { ["lat"] = "0", ["long"] = "0", ["locality"] = m.Locality, ["country"] = m.Country },
                },
            };
            foreach (var s in m.Sessions)
            {
                var (date, time) = (s.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s.Start.ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                if (s.Type == F1SessionType.Race)
                {
                    race["date"] = date;
                    race["time"] = time;
                    continue;
                }

                var property = s.Type switch
                {
                    F1SessionType.Practice1 => "FirstPractice",
                    F1SessionType.Practice2 => "SecondPractice",
                    F1SessionType.Practice3 => "ThirdPractice",
                    F1SessionType.SprintQualifying => "SprintQualifying",
                    F1SessionType.Sprint => "Sprint",
                    _ => "Qualifying",
                };
                race[property] = new JsonObject { ["date"] = date, ["time"] = time };
            }

            races.Add(race);
        }

        return Envelope("RaceTable", new JsonObject { ["season"] = season.ToString(CultureInfo.InvariantCulture), ["Races"] = races }, races.Count);
    }

    private static string JolpicaStandings(Timeline t, int season, bool drivers, DateTimeOffset now)
    {
        // Points of every sprint/race whose standings "publication" time has passed (simulated provider lag).
        var counted = t.Meetings.Where(m => m.Season == season)
            .SelectMany(m => m.Sessions.Where(s => F1SessionTypes.AwardsChampionshipPoints(s.Type) && now >= s.End + TimeSpan.FromMinutes(t.StandingsDelayMinutes)).Select(s => (m, s)))
            .ToList();
        var lists = new JsonArray();
        if (counted.Count > 0)
        {
            var points = new Dictionary<int, int>();
            var wins = new Dictionary<int, int>();
            foreach (var (_, s) in counted)
            {
                var table = s.Type == F1SessionType.Race ? RacePoints : SprintPoints;
                var order = FinishingOrder(s.Key);
                for (var p = 0; p < order.Count && p < 17; p++)
                {
                    points[order[p]] = points.GetValueOrDefault(order[p]) + (p < table.Length ? table[p] : 0);
                    if (p == 0 && s.Type == F1SessionType.Race)
                        wins[order[p]] = wins.GetValueOrDefault(order[p]) + 1;
                }
            }

            var round = counted.Max(c => c.m.Round);
            var list = new JsonObject { ["season"] = season.ToString(CultureInfo.InvariantCulture), ["round"] = round.ToString(CultureInfo.InvariantCulture) };
            if (drivers)
            {
                var rows = Enumerable.Range(1, 20).OrderByDescending(d => points.GetValueOrDefault(d)).ThenBy(d => d).Select((d, i) => (JsonNode)new JsonObject
                {
                    ["position"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                    ["positionText"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                    ["points"] = points.GetValueOrDefault(d).ToString(CultureInfo.InvariantCulture),
                    ["wins"] = wins.GetValueOrDefault(d).ToString(CultureInfo.InvariantCulture),
                    ["Driver"] = new JsonObject
                    {
                        ["driverId"] = "test_driver_" + d.ToString("00", CultureInfo.InvariantCulture),
                        ["permanentNumber"] = (100 + d).ToString(CultureInfo.InvariantCulture),
                        ["code"] = "T" + d.ToString("00", CultureInfo.InvariantCulture),
                        ["givenName"] = "Test",
                        ["familyName"] = "Driver " + d.ToString("00", CultureInfo.InvariantCulture),
                    },
                    ["Constructors"] = new JsonArray(new JsonObject { ["constructorId"] = TeamId(d), ["name"] = TeamName(d) }),
                }).ToArray();
                list["DriverStandings"] = new JsonArray(rows);
            }
            else
            {
                var teams = Enumerable.Range(1, 20).GroupBy(TeamId).Select(g => (Id: g.Key, Name: TeamName(g.First()), Points: g.Sum(d => points.GetValueOrDefault(d)), Wins: g.Sum(d => wins.GetValueOrDefault(d))))
                    .OrderByDescending(x => x.Points).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
                list["ConstructorStandings"] = new JsonArray(teams.Select((x, i) => (JsonNode)new JsonObject
                {
                    ["position"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                    ["positionText"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                    ["points"] = x.Points.ToString(CultureInfo.InvariantCulture),
                    ["wins"] = x.Wins.ToString(CultureInfo.InvariantCulture),
                    ["Constructor"] = new JsonObject { ["constructorId"] = x.Id, ["name"] = x.Name },
                }).ToArray());
            }

            lists.Add(list);
        }

        return Envelope("StandingsTable", new JsonObject { ["season"] = season.ToString(CultureInfo.InvariantCulture), ["StandingsLists"] = lists }, lists.Count);
    }

    private static string Envelope(string table, JsonObject content, int total) => new JsonObject
    {
        ["MRData"] = new JsonObject
        {
            ["xmlns"] = "",
            ["series"] = "f1",
            ["url"] = "https://example.com/tsq-bot-demo",
            ["limit"] = "100",
            ["offset"] = "0",
            ["total"] = total.ToString(CultureInfo.InvariantCulture),
            [table] = content,
        },
    }.ToJsonString();

    // ---------------------------------------------------------------- OpenF1 shapes

    private static string OpenF1Sessions(Timeline t, int year) => new JsonArray(t.Meetings.Where(m => m.Season == year).SelectMany(m => m.Sessions.Select(s => (JsonNode)new JsonObject
    {
        ["session_key"] = s.Key,
        ["session_type"] = s.Type switch { F1SessionType.Race or F1SessionType.Sprint => "Race", F1SessionType.Qualifying or F1SessionType.SprintQualifying => "Qualifying", _ => "Practice" },
        ["session_name"] = s.Type switch
        {
            F1SessionType.Practice1 => "Practice 1",
            F1SessionType.Practice2 => "Practice 2",
            F1SessionType.Practice3 => "Practice 3",
            F1SessionType.SprintQualifying => "Sprint Qualifying",
            F1SessionType.Sprint => "Sprint",
            F1SessionType.Qualifying => "Qualifying",
            _ => "Race",
        },
        ["date_start"] = Iso(s.Start),
        ["date_end"] = Iso(s.End),
        ["meeting_key"] = 9000 + m.Round,
        ["circuit_short_name"] = m.Locality,
        ["country_name"] = m.Country,
        ["location"] = m.Locality,
        ["year"] = m.Season,
        ["is_cancelled"] = false,
    })).ToArray()).ToJsonString();

    private static string? OpenF1RaceControl(Timeline t, string? sessionKey, DateTimeOffset now)
    {
        if (!int.TryParse(sessionKey, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || t.Session(key) is not { } s)
            return null;
        var rows = t.Events.Where(e => e.Key == key && s.Start + TimeSpan.FromMinutes(e.At) <= now).Select(e => (JsonNode)new JsonObject
        {
            ["meeting_key"] = 9000 + s.Round,
            ["session_key"] = key,
            ["date"] = Iso(s.Start + TimeSpan.FromMinutes(e.At)),
            ["driver_number"] = null,
            ["lap_number"] = null,
            ["category"] = "SessionStatus",
            ["flag"] = null,
            ["scope"] = null,
            ["sector"] = null,
            ["qualifying_phase"] = e.Phase,
            ["message"] = e.Message,
        }).ToArray();
        return rows.Length == 0 ? null : new JsonArray(rows).ToJsonString();
    }

    private static string? OpenF1Result(Timeline t, string? sessionKey, DateTimeOffset now)
    {
        if (!int.TryParse(sessionKey, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || t.Session(key) is not { } s ||
            now < s.End + TimeSpan.FromMinutes(t.ResultDelayMinutes))
            return null;
        var race = F1SessionTypes.AwardsChampionshipPoints(s.Type);
        var quali = F1SessionTypes.IsSegmented(s.Type);
        var order = FinishingOrder(key);
        var rows = new JsonArray();
        for (var p = 0; p < order.Count; p++)
        {
            var d = order[p];
            var status = race && p >= 17 ? p - 17 : -1; // last three of a race: DNF, DSQ, DNS
            var gap = race ? 2.5 * p : 0.1 * p;
            var row = new JsonObject
            {
                ["position"] = status >= 0 ? null : p + 1,
                ["driver_number"] = 100 + d,
                ["number_of_laps"] = status is 1 or 2 ? null : race ? (status == 0 ? 12 : 57) : 20 + p,
                ["dnf"] = status == 0,
                ["dns"] = status == 2,
                ["dsq"] = status == 1,
                ["meeting_key"] = 9000 + s.Round,
                ["session_key"] = key,
            };
            if (status >= 0)
            {
                row["duration"] = null;
                row["gap_to_leader"] = null;
            }
            else if (quali)
            {
                row["duration"] = new JsonArray(91.0 + gap, 90.8 + gap, p < 10 ? 90.5 + gap : null);
                row["gap_to_leader"] = new JsonArray(gap, gap, p < 10 ? gap : null);
            }
            else
            {
                row["duration"] = race ? 5400 + gap : 90.5 + gap;
                row["gap_to_leader"] = race && p == 16 ? JsonValue.Create("+1 LAP") : JsonValue.Create(gap);
            }

            if (race)
                row["points"] = status >= 0 ? 0.0 : (double)((s.Type == F1SessionType.Race ? RacePoints : SprintPoints).ElementAtOrDefault(p));
            rows.Add(row);
        }

        return rows.ToJsonString();
    }

    private static string? OpenF1Drivers(Timeline t, string? sessionKey)
    {
        if (!int.TryParse(sessionKey, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || t.Session(key) is not { } s)
            return null;
        return new JsonArray(Enumerable.Range(1, 20).Select(d => (JsonNode)new JsonObject
        {
            ["meeting_key"] = 9000 + s.Round,
            ["session_key"] = key,
            ["driver_number"] = 100 + d,
            ["broadcast_name"] = "T DRIVER " + d.ToString("00", CultureInfo.InvariantCulture),
            ["full_name"] = "Test DRIVER " + d.ToString("00", CultureInfo.InvariantCulture),
            ["name_acronym"] = "T" + d.ToString("00", CultureInfo.InvariantCulture),
            ["team_name"] = TeamName(d),
            ["team_colour"] = "808080",
            ["first_name"] = "Test",
            ["last_name"] = "Driver " + d.ToString("00", CultureInfo.InvariantCulture),
            ["headshot_url"] = null,
            ["country_code"] = null,
        }).ToArray()).ToJsonString();
    }

    /// <summary>Deterministic finishing order per session (rotated so winners differ between sessions).</summary>
    private static List<int> FinishingOrder(int sessionKey)
    {
        var shift = sessionKey % 5;
        return Enumerable.Range(0, 20).Select(i => ((i + shift) % 20) + 1).ToList();
    }

    private static string TeamId(int driver) => "test_team_" + (char)('a' + ((driver - 1) / 2));

    private static string TeamName(int driver) => "Test Team " + (char)('A' + ((driver - 1) / 2));

    private static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>OpenF1 answers an empty query with 404 {"detail":"No results found."}.</summary>
    private static HttpResponseMessage OrEmpty(string? body) => body is null
        ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\":\"No results found.\"}", Encoding.UTF8, "application/json") }
        : Json(body);

    private static string ReadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ToroSquad.Modules.Formula1.Fixtures." + name)
            ?? throw new InvalidOperationException("Missing F1 fixture " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record FixtureSession(int Key, int Round, F1SessionType Type, DateTimeOffset Start, DateTimeOffset End);

    private sealed record FixtureMeeting(int Season, int Round, string Name, string Circuit, string CircuitId, string Locality, string Country, IReadOnlyList<FixtureSession> Sessions);

    private sealed record FixtureEvent(int Key, int At, string Message, int? Phase);

    private sealed record Timeline(IReadOnlyList<FixtureMeeting> Meetings, IReadOnlyList<FixtureEvent> Events, int ResultDelayMinutes, int StandingsDelayMinutes)
    {
        public FixtureSession? Session(int key) => Meetings.SelectMany(m => m.Sessions).FirstOrDefault(s => s.Key == key);

        public static Timeline Load(JsonNode root, DateTimeOffset origin)
        {
            // Whole minutes keep every generated timestamp stable for the same anchor.
            origin = new DateTimeOffset(origin.Year, origin.Month, origin.Day, origin.Hour, origin.Minute, 0, TimeSpan.Zero);
            var season = origin.Year;
            var meetings = root["meetings"]!.AsArray().Select(m =>
            {
                var round = m!["round"]!.GetValue<int>();
                var sessions = m["sessions"]!.AsArray().Select(s =>
                {
                    if (!F1SessionTypes.TryParseSlug(s!["type"]!.GetValue<string>(), out var type))
                        throw new InvalidOperationException("fixture session with unknown type");
                    var start = origin + TimeSpan.FromMinutes(s["start"]!.GetValue<int>());
                    return new FixtureSession(s["key"]!.GetValue<int>(), round, type, start, start + TimeSpan.FromMinutes(s["minutes"]!.GetValue<int>()));
                }).ToList();
                return new FixtureMeeting(season, round, m["name"]!.GetValue<string>(), m["circuit"]!.GetValue<string>(), m["circuitId"]!.GetValue<string>(),
                    m["locality"]!.GetValue<string>(), m["country"]!.GetValue<string>(), sessions);
            }).ToList();
            var events = root["events"]!.AsArray().Select(e => new FixtureEvent(e!["key"]!.GetValue<int>(), e["at"]!.GetValue<int>(), e["message"]!.GetValue<string>(),
                e["phase"]?.GetValue<int>())).ToList();
            return new Timeline(meetings, events, root["resultDelayMinutes"]?.GetValue<int>() ?? 5, root["standingsDelayMinutes"]?.GetValue<int>() ?? 15);
        }
    }
}

/// <summary>
/// The instant the demo timeline is anchored to: persisted (<see cref="IF1FixtureAnchorStore"/>) and reused for up to
/// <see cref="MaxAge"/>, so a restart continues the same demo weekend instead of re-announcing a shifted one.
/// </summary>
public sealed class F1FixtureAnchor(TimeProvider clock, IF1FixtureAnchorStore? store = null)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private Task<DateTimeOffset>? _anchor;

    public Task<DateTimeOffset> GetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_anchor is null || _anchor.IsFaulted || _anchor.IsCanceled)
                _anchor = ResolveAsync(cancellationToken);
            return _anchor;
        }
    }

    private async Task<DateTimeOffset> ResolveAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var stored = store is null ? null : await store.LoadAsync(cancellationToken);
        if (stored is { } s && s <= now && now - s < MaxAge)
            return s;
        if (store is not null)
            await store.SaveAsync(now, cancellationToken);
        return now;
    }
}

/// <summary>Where the fixture anchor survives restarts. Implementations never throw (a failure means "no anchor").</summary>
public interface IF1FixtureAnchorStore
{
    Task<DateTimeOffset?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(DateTimeOffset anchor, CancellationToken cancellationToken);
}
