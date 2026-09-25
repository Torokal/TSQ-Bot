using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ToroSquad.Modules.Formula1.Domain;

/// <summary>Classification status of one driver as stated by the results provider (never inferred).</summary>
public enum F1ResultStatus
{
    Classified = 0,

    /// <summary>No position and no DNF/DNS/DSQ flag from the provider.</summary>
    NotClassified = 1,
    Dnf = 2,
    Dns = 3,
    Dsq = 4,
}

/// <summary>
/// One line of a session classification. Every optional value is exactly what the provider supplied — missing timing
/// stays missing (never computed, never guessed). <see cref="TimeSeconds"/> is the best lap (practice/qualifying: the last
/// segment the driver set a time in) or the total race time; <see cref="GapLaps"/> is set for lapped drivers.
/// </summary>
public sealed record F1DriverResult(
    int? Position,
    int DriverNumber,
    string DriverName,
    string? DriverCode,
    string? TeamName,
    F1ResultStatus Status,
    int? Laps,
    double? TimeSeconds,
    double? GapSeconds,
    int? GapLaps,
    double? Points);

/// <summary>
/// A normalized session classification from a results provider. <see cref="ExpectedDriverNumbers"/> is the provider's
/// participant roster for this session (OpenF1: /drivers?session_key=…) when it supplies one; completeness is proven
/// against it (never against a fixed grid size). Null = the provider has no roster concept.
/// </summary>
public sealed record F1SessionResult(string SessionKey, F1SessionType Type, string Source, IReadOnlyList<F1DriverResult> Entries,
    IReadOnlyList<int>? ExpectedDriverNumbers = null)
{
    /// <summary>Classified drivers by position, then the rest (DNF, DNS, DSQ, not classified) by driver number.</summary>
    public IEnumerable<F1DriverResult> Ordered => Entries
        .OrderBy(e => e.Position is null ? 1 : 0)
        .ThenBy(e => e.Position ?? int.MaxValue)
        .ThenBy(e => (int)e.Status)
        .ThenBy(e => e.DriverNumber);

    /// <summary>
    /// Deterministic content hash: independent of provider JSON property order, entry order and culture. Any change a
    /// reader could see (position, status, time, gap, points, names) changes the hash; nothing else does.
    /// </summary>
    public string CanonicalHash()
    {
        var sb = new StringBuilder();
        sb.Append("result|").Append(SessionKey).Append('|').Append(F1SessionTypes.Slug(Type)).Append('\n');
        foreach (var e in Ordered)
        {
            sb.Append(F1Canonical.Number(e.Position)).Append('|')
              .Append(e.DriverNumber.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(F1Canonical.Text(e.DriverName)).Append('|')
              .Append(F1Canonical.Text(e.DriverCode)).Append('|')
              .Append(F1Canonical.Text(e.TeamName)).Append('|')
              .Append((int)e.Status).Append('|')
              .Append(F1Canonical.Number(e.Laps)).Append('|')
              .Append(F1Canonical.Millis(e.TimeSeconds)).Append('|')
              .Append(F1Canonical.Millis(e.GapSeconds)).Append('|')
              .Append(F1Canonical.Number(e.GapLaps)).Append('|')
              .Append(F1Canonical.Millis(e.Points)).Append('\n');
        }

        return F1Canonical.Hash(sb.ToString());
    }
}

/// <summary>
/// Decides whether a provider classification is complete enough to publish. Empty, half-populated or inconsistent
/// classifications are rejected (the workflow retries later) — never published with placeholders.
/// <para>Completeness = every driver of the session's participant roster has a result row (DNF/DNS/DSQ rows count as
/// present) and no row belongs to a driver outside the roster. A contiguous 1..10 classification is therefore NOT complete
/// while a 20-driver roster exists. No grid size is assumed; <c>minEntries</c> is only an extra sanity lower bound.</para>
/// </summary>
public static class F1ResultValidator
{
    public static string? Problem(F1SessionResult result, int minEntries)
    {
        if (result.Entries.Count == 0)
            return "empty classification";
        if (result.Entries.Select(e => e.DriverNumber).Distinct().Count() != result.Entries.Count)
            return "duplicate driver numbers";
        if (result.ExpectedDriverNumbers is { } roster)
        {
            if (roster.Count == 0)
                return "empty participant roster (completeness cannot be proven)";
            if (roster.Distinct().Count() != roster.Count)
                return "duplicate driver numbers in the participant roster (ambiguous)";
            var present = result.Entries.Select(e => e.DriverNumber).ToHashSet();
            var missing = roster.Count(n => !present.Contains(n));
            if (missing > 0)
                return $"{missing} of {roster.Count} session drivers have no result row yet";
            if (present.Any(n => !roster.Contains(n)))
                return "result rows for drivers outside the session roster (ambiguous)";
        }

        if (result.Entries.Count < minEntries)
            return $"only {result.Entries.Count} entries (< {minEntries})";
        if (result.Entries.Any(e => string.IsNullOrWhiteSpace(e.DriverName)))
            return "driver without a name";

        var positions = result.Entries.Where(e => e.Position is not null).Select(e => e.Position!.Value).OrderBy(p => p).ToList();
        if (positions.Count == 0)
            return "no classified positions";
        if (positions.Where((p, i) => p != i + 1).Any())
            return "positions are not a contiguous 1..n sequence";
        if (result.Entries.Any(e => e.Position is not null && e.Status is F1ResultStatus.Dns or F1ResultStatus.Dsq))
            return "a DNS/DSQ entry also has a position";

        var winner = result.Entries.Single(e => e.Position == 1);
        if (F1SessionTypes.IsPractice(result.Type) && winner.TimeSeconds is null)
            return "practice leader without a lap time";
        return null;
    }
}

/// <summary>Canonical text for hashing (culture-invariant, fixed precision).</summary>
public static class F1Canonical
{
    public static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    /// <summary>Seconds/points rounded to thousandths, so float noise (0.1+0.2) never changes a hash.</summary>
    public static string Millis(double? value) =>
        value is { } v ? Math.Round(v, 3, MidpointRounding.AwayFromZero).ToString("0.000", CultureInfo.InvariantCulture) : "-";

    public static string Points(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Text(string? value) => (value ?? "").Normalize(NormalizationForm.FormC).Trim().Replace("|", "/", StringComparison.Ordinal);

    public static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
