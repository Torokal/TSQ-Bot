# Third-party notices

## Code

| Component | License | Notes |
|---|---|---|
| BOT-Greg-v2_API — https://github.com/julius-gmeinder/BOT-Greg-v2_API (commit `3898b4ebfd4ed26ec077f4167a780f1684d91331`) | GNU AGPL v3 | Portions adapted and modified in `src/ToroSquad.Modules.Esports/Providers/Liquipedia/LiquipediaParser.cs` and `src/ToroSquad.Modules.Esports/Providers/Valve/ValveStandings.cs` (see file headers and docs/PROVENANCE.md). |
| Discord.Net 3.20.1 | MIT | NuGet dependency |
| .NET / Microsoft.Extensions.* / EF Core 10.0.12 | MIT | NuGet dependencies |
| xunit.v3, AwesomeAssertions, TngTech.ArchUnitNET | Apache-2.0 | Test-only dependencies |

Exact dependency versions are pinned in `Directory.Packages.props` and the per-project `packages.lock.json` files.

## Data

| Source | Terms | Attribution in the bot |
|---|---|---|
| PandaScore (REST API) — default match data | PandaScore terms and conditions (source attribution required, no raw-data redistribution) | "Kaynak: PandaScore" on every card using it; listed in `/bot about` |
| Liquipedia (LiquipediaDB API) — optional | Content CC BY-SA 3.0; API access by approval under Liquipedia's API terms | "Kaynak: Liquipedia (CC BY-SA 3.0)" + link on every message using it |
| Valve Counter-Strike Regional Standings (GitHub) | No license file published; event data credited to HLTV.org | Source, publication date and attribution shown with rankings |

Test and demo fixtures in this repository are synthetic (fictional teams/events) and contain no copied third-party rows.
