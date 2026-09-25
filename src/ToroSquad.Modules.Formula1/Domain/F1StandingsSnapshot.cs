using System.Globalization;
using System.Text;

namespace ToroSquad.Modules.Formula1.Domain;

public enum F1StandingsKind
{
    Drivers = 1,
    Constructors = 2,
}

/// <summary>One row of the drivers' championship as published by the standings provider (authoritative).</summary>
public sealed record F1DriverStanding(int? Position, string DriverId, string DriverName, string? DriverCode, string? TeamName, decimal Points, int Wins);

/// <summary>One row of the constructors' championship as published by the standings provider (authoritative).</summary>
public sealed record F1ConstructorStanding(int? Position, string ConstructorId, string Name, decimal Points, int Wins);

/// <summary>
/// A provider standings table. The bot never computes championship points, tie-breaks or countbacks itself: this
/// snapshot is displayed as published. <see cref="Round"/> is the provider's "after round N" marker when supplied.
/// </summary>
public sealed record F1StandingsSnapshot(
    F1StandingsKind Kind,
    int Season,
    int? Round,
    string Source,
    IReadOnlyList<F1DriverStanding> Drivers,
    IReadOnlyList<F1ConstructorStanding> Constructors)
{
    public int Count => Kind == F1StandingsKind.Drivers ? Drivers.Count : Constructors.Count;

    /// <summary>
    /// Deterministic hash of the normalized table (season, round, every row's position/id/name/points/wins) — independent
    /// of provider JSON property order, row order and culture. Only a real table change changes it.
    /// </summary>
    public string CanonicalHash()
    {
        var sb = new StringBuilder();
        sb.Append("standings|").Append((int)Kind).Append('|')
          .Append(Season.ToString(CultureInfo.InvariantCulture)).Append('|').Append(F1Canonical.Number(Round)).Append('\n');
        if (Kind == F1StandingsKind.Drivers)
        {
            foreach (var d in Drivers.OrderBy(d => d.Position ?? int.MaxValue).ThenBy(d => d.DriverId, StringComparer.Ordinal))
            {
                sb.Append(F1Canonical.Number(d.Position)).Append('|').Append(F1Canonical.Text(d.DriverId)).Append('|')
                  .Append(F1Canonical.Text(d.DriverName)).Append('|').Append(F1Canonical.Text(d.DriverCode)).Append('|')
                  .Append(F1Canonical.Text(d.TeamName)).Append('|').Append(F1Canonical.Points(d.Points)).Append('|')
                  .Append(d.Wins.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        else
        {
            foreach (var c in Constructors.OrderBy(c => c.Position ?? int.MaxValue).ThenBy(c => c.ConstructorId, StringComparer.Ordinal))
            {
                sb.Append(F1Canonical.Number(c.Position)).Append('|').Append(F1Canonical.Text(c.ConstructorId)).Append('|')
                  .Append(F1Canonical.Text(c.Name)).Append('|').Append(F1Canonical.Points(c.Points)).Append('|')
                  .Append(c.Wins.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return F1Canonical.Hash(sb.ToString());
    }
}
