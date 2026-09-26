using System.Text.Json;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers.OpenF1;

namespace ToroSquad.Tests.Contract;

/// <summary>
/// Race-control incidents from REAL OpenF1 race_control rows (Fixtures/f1/openf1-race-control-*.json, lifecycle-relevant rows
/// plus penalty/investigation noise from the same sessions) and a real session_result with disqualifications.
/// </summary>
public sealed class F1RaceControlContractTests
{
    private static JsonDocument Doc(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "f1", name)));

    private static IReadOnlyList<F1RaceControlIncident> Incidents(string name)
    {
        using var doc = Doc(name);
        return OpenF1Parser.ParseIncidents(doc.RootElement);
    }

    private static DateTimeOffset Utc(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Azerbaijan_2026_race_has_two_real_safety_car_phases_and_no_red_flag()
    {
        var incidents = Incidents("openf1-race-control-2026-azerbaijan-race.json");

        var starts = F1IncidentPhases.SafetyCarStarts(incidents);
        starts.Select(s => (s.OccurredAt, s.Lap)).Should().Equal((Utc("2026-09-26T11:58:05Z"), 31), (Utc("2026-09-26T12:12:40Z"), 36));
        incidents.Count(i => i.Kind == F1IncidentKind.SafetyCarEnding).Should().Be(2);
        F1IncidentPhases.RedFlagStarts(incidents).Should().BeEmpty();
    }

    [Fact]
    public void Monaco_2026_safety_car_then_red_flag_then_restart()
    {
        var incidents = Incidents("openf1-race-control-2026-monaco-race.json");

        F1IncidentPhases.SafetyCarStarts(incidents).Select(s => s.OccurredAt).Should().Equal(Utc("2026-06-07T14:20:02Z"), Utc("2026-06-07T14:31:18Z"));
        var red = F1IncidentPhases.RedFlagStarts(incidents).Should().ContainSingle().Subject;
        red.OccurredAt.Should().Be(Utc("2026-06-07T14:35:07Z"), "2026 races state the red flag as \"RED FLAG - RACE SUSPENDED\"");
        red.Lap.Should().Be(68);
    }

    [Fact]
    public void Netherlands_2026_red_flag_counts_and_vsc_is_never_a_safety_car()
    {
        var incidents = Incidents("openf1-race-control-2026-netherlands-race.json");

        F1IncidentPhases.RedFlagStarts(incidents).Should().ContainSingle();
        F1IncidentPhases.SafetyCarStarts(incidents).Should().BeEmpty("\"VSC DEPLOYED\" / \"VSC ENDING\" share the SafetyCar category but are not a Safety Car");
    }

    [Fact]
    public void Brazil_2024_qualifying_has_five_separate_red_flag_phases()
    {
        var incidents = Incidents("openf1-race-control-2024-brazil-qualifying.json");

        F1IncidentPhases.RedFlagStarts(incidents).Should().HaveCount(5, "each red flag is followed by a restart (or the next segment's start)");
    }

    [Fact]
    public void Monaco_2024_lap_one_red_flag_in_the_flag_row_form()
    {
        var red = F1IncidentPhases.RedFlagStarts(Incidents("openf1-race-control-2024-monaco-race.json")).Should().ContainSingle().Subject;

        red.OccurredAt.Should().Be(Utc("2024-05-26T13:04:08Z"));
        red.Lap.Should().Be(1);
    }

    [Theory]
    [InlineData("Other", null, null, "FIA STEWARDS: 5 SECOND TIME PENALTY FOR CAR 44 (HAM) - TRACK LIMITS")]
    [InlineData("Other", null, null, "FIA STEWARDS: 10 SECOND TIME PENALTY FOR CAR 31 (OCO) - CAUSING A COLLISION")]
    [InlineData("Other", null, null, "FIA STEWARDS: DRIVE THROUGH PENALTY FOR CAR 18 (STR) - SPEEDING IN THE PIT LANE")]
    [InlineData("Other", null, null, "FIA STEWARDS: 10 SECOND STOP AND GO PENALTY FOR CAR 2 (SAR) - UNSAFE RELEASE")]
    [InlineData("Other", null, null, "FIA STEWARDS: INCIDENT INVOLVING CAR 11 (PER) UNDER INVESTIGATION - FALSE START")]
    [InlineData("Other", null, null, "CAR 4 (NOR) TIME 1:46.347 DELETED - TRACK LIMITS AT TURN 2 LAP 5 7:35:10")]
    [InlineData("Other", null, null, "INCIDENT INVOLVING CAR 6 (HAD) NOTED - RED FLAG INFRINGEMENT")]
    [InlineData("Other", null, null, "STARTING PROCEDURE SUSPENDED")]
    [InlineData("Other", null, null, "RACE WILL RESUME AT 17:12")]
    [InlineData("SafetyCar", null, null, "VSC DEPLOYED")]
    [InlineData("SafetyCar", null, null, "VSC ENDING")]
    [InlineData("SafetyCar", null, null, "VIRTUAL SAFETY CAR DEPLOYED")]
    [InlineData("Flag", "YELLOW", "Sector", "YELLOW IN TRACK SECTOR 18")]
    [InlineData("Flag", "GREEN", "Track", "GREEN LIGHT - PIT EXIT OPEN")]
    [InlineData("Flag", "CHEQUERED", "Track", "CHEQUERED FLAG")]
    [InlineData("SessionStatus", null, null, "SESSION ABORTED")]
    [InlineData("SessionStatus", null, null, "SESSION FINISHED")]
    [InlineData("Other", null, null, "CAR 44 (HAM) DISQUALIFIED - PLANK WEAR")]
    public void Everything_else_is_not_an_incident(string category, string? flag, string? scope, string message)
    {
        var row = JsonSerializer.Serialize(new
        {
            session_key = 11377,
            date = "2026-09-26T12:00:00+00:00",
            category,
            flag,
            scope,
            message,
            lap_number = 10,
            driver_number = (int?)null,
        });
        using var doc = JsonDocument.Parse(row);

        OpenF1Parser.ParseIncident(doc.RootElement).Should().BeNull();
    }

    [Fact]
    public void Real_noise_rows_in_the_fixtures_produce_no_incident()
    {
        foreach (var name in new[]
                 {
                     "openf1-race-control-2026-azerbaijan-race.json", "openf1-race-control-2026-monaco-race.json", "openf1-race-control-2026-netherlands-race.json",
                     "openf1-race-control-2024-brazil-qualifying.json", "openf1-race-control-2024-monaco-race.json",
                 })
        {
            using var doc = Doc(name);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var message = row.GetProperty("message").GetString()!;
                if (message.Contains("PENALTY", StringComparison.Ordinal) || message.Contains("INVESTIGAT", StringComparison.Ordinal) ||
                    message.Contains("DELETED", StringComparison.Ordinal) || message.Contains("NOTED", StringComparison.Ordinal) || message.StartsWith("VSC", StringComparison.Ordinal))
                    OpenF1Parser.ParseIncident(row).Should().BeNull(message);
            }
        }
    }

    [Fact]
    public void Mqtt_race_control_message_yields_the_same_incident()
    {
        const string payload = """{"meeting_key":1295,"session_key":11377,"date":"2026-09-26T11:58:05+00:00","driver_number":null,"lap_number":31,"category":"SafetyCar","flag":null,"scope":null,"sector":null,"qualifying_phase":null,"message":"SAFETY CAR DEPLOYED","_id":1754,"_key":"abc"}""";

        var incident = OpenF1Parser.ParseMqttIncident(OpenF1Topics.RaceControl, payload);

        incident.Should().Be(new F1RaceControlIncident("openf1", "11377", F1IncidentKind.SafetyCarDeployed, Utc("2026-09-26T11:58:05Z"), 31, null, null, null));
        OpenF1Parser.ParseMqttIncident("v1/laps", payload).Should().BeNull();
        OpenF1Parser.ParseMqttIncident(OpenF1Topics.RaceControl, "{not json").Should().BeNull();
    }

    [Fact]
    public void Real_2025_china_classification_marks_the_three_disqualified_cars()
    {
        using var doc = Doc("openf1-session-result-2025-china-race-dsq.json");
        var session = new F1Session(2025, 2, F1SessionType.Race, Utc("2025-03-23T07:00:00Z"));

        var result = OpenF1Parser.ParseSessionResult(doc.RootElement, null, session);

        result.Entries.Where(e => e.Status == F1ResultStatus.Dsq).Select(e => e.DriverNumber).Should().BeEquivalentTo([16, 44, 10]);
    }
}
