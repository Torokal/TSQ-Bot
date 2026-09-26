namespace ToroSquad.Modules.Formula1.Providers.OpenF1;

/// <summary>
/// Exact race-control message formats (upper-cased), taken from real OpenF1 race_control rows 2024–2026 (fixtures:
/// tests/ToroSquad.Tests/Fixtures/f1/openf1-race-control-*.json). Anything that does not match is ignored — the parser
/// never guesses what a free-text message might mean.
/// </summary>
public static class RaceControlMessages
{
    /// <summary>Category "SafetyCar". "VSC DEPLOYED" / "VSC ENDING" share the category and are deliberately not matched.</summary>
    public const string SafetyCarDeployed = "SAFETY CAR DEPLOYED";

    public const string SafetyCarInThisLap = "SAFETY CAR IN THIS LAP";

    /// <summary>
    /// An explicit red flag: the track-wide flag row (category "Flag", flag "RED", scope "Track", message "RED FLAG"; 2024–2025
    /// sessions) or the 2026 race form "RED FLAG - RACE SUSPENDED" (category "Other", sent right after "SESSION ABORTED").
    /// "SESSION ABORTED" alone is not used: every observed one had an explicit red-flag message within a minute, but not every
    /// red flag has one. "... RED FLAG INFRINGEMENT" (investigations) and "STARTING PROCEDURE SUSPENDED" (aborted start) are not red flags.
    /// </summary>
    public static bool IsRedFlag(string? category, string? flag, string? scope, string message) =>
        (category == "Flag" && flag == "RED" && scope == "Track") ||
        (category == "Other" && message.StartsWith("RED FLAG - ", StringComparison.Ordinal) && message.EndsWith(" SUSPENDED", StringComparison.Ordinal));

    /// <summary>"SESSION STARTED" (category SessionStatus): the session runs again, which ends an open red-flag phase.</summary>
    public static bool IsSessionRestart(string? category, string message) =>
        category == "SessionStatus" && message == "SESSION STARTED";
}
