namespace ToroSquad.Modules.Formula1.Domain;

/// <summary>
/// Race-control incidents TSQ may announce (low-spam V2). Only exact provider messages map here; everything else (VSC,
/// yellow/green flags, time penalties, investigations, track limits, deleted laps) is ignored by the parser.
/// The numeric values are persisted.
/// </summary>
public enum F1IncidentKind
{
    /// <summary>OpenF1 category "SafetyCar", message "SAFETY CAR DEPLOYED" — opens a Safety Car phase.</summary>
    SafetyCarDeployed = 1,

    /// <summary>OpenF1 category "SafetyCar", message "SAFETY CAR IN THIS LAP" — closes the open Safety Car phase (never announced).</summary>
    SafetyCarEnding = 2,

    /// <summary>OpenF1 category "Flag", flag "RED", track scope — opens a red-flag phase.</summary>
    RedFlag = 3,

    /// <summary>"SESSION STARTED": the session (re)starts, closing an open red-flag phase (never announced).</summary>
    RedFlagCleared = 4,

    /// <summary>
    /// A car the provider's classification marks as disqualified (OpenF1 session_result "dsq": true). Race control carries no
    /// disqualification messages in 2024–2026 data, so none are parsed from free text.
    /// </summary>
    Disqualified = 5,
}

/// <summary>
/// One normalized race-control incident. <see cref="Fingerprint"/> identifies the provider message (provider time + kind
/// + car), so the same message delivered by MQTT, REST reconciliation or a replay is stored once.
/// </summary>
public sealed record F1RaceControlIncident(
    string ProviderId,
    string ProviderSessionRef,
    F1IncidentKind Kind,
    DateTimeOffset OccurredAt,
    int? Lap,
    int? DriverNumber,
    string? DriverCode,
    string? Reason)
{
    public string Fingerprint => string.Join('|', (int)Kind, OccurredAt.UtcTicks, DriverNumber);
}

/// <summary>
/// Phase logic over the persisted incident history of one session (pure, order-independent input). A Safety Car phase
/// starts at a "deployed" message while no phase is open and ends at "in this lap"; repeated "deployed" messages inside an
/// open phase belong to it. Red-flag phases work the same way with a provider-stated restart as the end. Only phase starts
/// are announced, so each real phase notifies at most once.
/// </summary>
public static class F1IncidentPhases
{
    /// <summary>
    /// "In this lap" ends a Safety Car phase; so does a red flag or a restart (2026 Monaco: Safety Car deployed, then red
    /// flag, then restart — a later deployment is a new phase).
    /// </summary>
    public static IReadOnlyList<F1RaceControlIncident> SafetyCarStarts(IEnumerable<F1RaceControlIncident> history) =>
        Starts(history, F1IncidentKind.SafetyCarDeployed, F1IncidentKind.SafetyCarEnding, F1IncidentKind.RedFlag, F1IncidentKind.RedFlagCleared);

    public static IReadOnlyList<F1RaceControlIncident> RedFlagStarts(IEnumerable<F1RaceControlIncident> history) =>
        Starts(history, F1IncidentKind.RedFlag, F1IncidentKind.RedFlagCleared);

    /// <summary>One entry per disqualified car (the earliest message), however often it is repeated.</summary>
    public static IReadOnlyList<F1RaceControlIncident> Disqualifications(IEnumerable<F1RaceControlIncident> history) =>
        history.Where(i => i.Kind == F1IncidentKind.Disqualified && i.DriverNumber is not null)
            .GroupBy(i => i.DriverNumber!.Value)
            .Select(g => g.OrderBy(i => i.OccurredAt).First())
            .OrderBy(i => i.OccurredAt)
            .ToList();

    private static List<F1RaceControlIncident> Starts(IEnumerable<F1RaceControlIncident> history, F1IncidentKind open, params F1IncidentKind[] close)
    {
        var starts = new List<F1RaceControlIncident>();
        var inPhase = false;
        // Same provider time: the closing message sorts first so "ended and redeployed at once" stays two phases.
        foreach (var i in history.Where(i => i.Kind == open || close.Contains(i.Kind)).OrderBy(i => i.OccurredAt).ThenBy(i => i.Kind == open ? 1 : 0))
        {
            if (close.Contains(i.Kind))
            {
                inPhase = false;
            }
            else if (!inPhase)
            {
                inPhase = true;
                starts.Add(i);
            }
        }

        return starts;
    }
}
