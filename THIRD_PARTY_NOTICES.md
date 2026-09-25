# Third-party notices

## Code

| Component | License | Notes |
|---|---|---|
| BOT-Greg-v2_API — https://github.com/julius-gmeinder/BOT-Greg-v2_API (commit `3898b4ebfd4ed26ec077f4167a780f1684d91331`) | GNU AGPL v3 | Portions adapted and modified in `src/ToroSquad.Modules.Esports/Providers/Liquipedia/LiquipediaParser.cs` and `src/ToroSquad.Modules.Esports/Providers/Valve/ValveStandings.cs` (see file headers and docs/PROVENANCE.md). |
| Discord.Net 3.20.1 | MIT | NuGet dependency |
| MQTTnet 5.2.0.1603 — https://github.com/dotnet/MQTTnet | MIT | NuGet dependency (OpenF1 live MQTT connection, Formula 1 module) |
| .NET / Microsoft.Extensions.* / EF Core 10.0.12 | MIT | NuGet dependencies |
| xunit.v3, AwesomeAssertions, TngTech.ArchUnitNET | Apache-2.0 | Test-only dependencies |

Exact dependency versions are pinned in `Directory.Packages.props` and the per-project `packages.lock.json` files.

## Data

| Source | Terms | Attribution in the bot |
|---|---|---|
| PandaScore (REST API) — default match data | PandaScore terms and conditions (source attribution required, no raw-data redistribution) | "Kaynak: PandaScore" on every card using it; listed in `/bot about` |
| Liquipedia (LiquipediaDB API) — optional | Content CC BY-SA 3.0; API access by approval under Liquipedia's API terms | "Kaynak: Liquipedia (CC BY-SA 3.0)" + link on every message using it |
| Valve Counter-Strike Regional Standings (GitHub) | No license file published; event data credited to HLTV.org | Source, publication date and attribution shown with rankings |
| Jolpica F1 API (api.jolpi.ca) — Formula 1 schedule and standings | Terms of Use (2025-08-27): non-commercial use; data CC BY-NC-SA 4.0; commercial use by agreement (admin@jolpi.ca) | "Kaynak: Jolpica F1" / "Puan durumu: Jolpica F1"; listed in `/bot about` |
| OpenF1 (api.openf1.org) — Formula 1 session lifecycle and results | CC BY-NC-SA 4.0; educational, personal, research and non-commercial fan use; unofficial, not associated with the Formula 1 companies; live data is a paid sponsor tier | "Kaynak: OpenF1" on every card using it; listed in `/bot about` |

Test and demo fixtures in this repository are synthetic (fictional teams/events/drivers) and contain no copied third-party rows.
The Formula 1 contract fixtures reproduce the verified payload *shapes* of Jolpica and OpenF1 with fictional values.
