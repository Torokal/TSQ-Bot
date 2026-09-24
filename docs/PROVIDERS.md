# Esports data providers — capabilities, terms, status

Research date: 2026-09-24. Labels: **VERIFIED** (read at the primary/official source), **VERIFIED (archive)**
(official page read via Web Archive because the live page was behind a bot challenge), **VERIFIED (3rd-party)**
(read in public third-party code/copies), **INFERRED**, **NOT_VERIFIED**.

## Current status

| Provider | Mode today | Live access | Status |
|---|---|---|---|
| Liquipedia (LiquipediaDB API v3) | Fixture (synthetic data through the real client/parser) | Requires an **approved API key** — none configured | **BLOCKED** (live) / **TESTED_OFFLINE** (parser, pagination, error types) |
| Valve Regional Standings (GitHub) | Fixture | Public, no key | **TESTED_OFFLINE**; live fetch **NOT_RUN** (no live run was authorized/needed yet) |

A failing live provider **never** falls back to fixture data: the fixture handler is only wired in Fixture mode
(`src/ToroSquad.Modules.Esports/EsportsModule.cs`).

## Capabilities (as implemented)

| Capability | Liquipedia adapter | Valve adapter |
|---|---|---|
| Fixtures (upcoming) | ✅ | — |
| Results | ✅ | — |
| Tournaments | ✅ | — |
| Team list (from match data) | ✅ | — |
| **Verified live status** | ❌ **unavailable** — LPDB has no live flag (VERIFIED: Liquipedia's own MatchTicker treats "not finished and start passed" as ongoing; that is not evidence of play) | — |
| Rankings | — | ✅ global VRS |

Consequences in the product: reminders are "planned start" reminders; a passed start time is shown as "awaiting
result"; there is no "match is live" notification.

## Liquipedia (LiquipediaDB API v3)

- **Access**: "Upon approved request" (VERIFIED, https://liquipedia.net/api-terms-of-use). Keys at https://api.liquipedia.net.
- **Plans** (VERIFIED (archive) 2026-06-18 of https://liquipedia.net/api; live page returned a bot challenge): Basic
  $49/mo per data type (1000 req/h), Premium $199/mo (5000 req/h), Enterprise custom. Free access "strictly reserved"
  for educational / non-commercial / community-feature projects, **code must be open source**, usually time-limited.
  Whether a Discord bot qualifies: **NOT_VERIFIED** (Liquipedia decides). A search snippet claiming only Enterprise is
  currently served: NOT_VERIFIED. → Requesting access or paying is the owner's decision (approval gate).
- **Rate limit**: "no more than 60 requests per 1 hour" baseline (VERIFIED, terms). Applies per key/wiki/table
  (VERIFIED (3rd-party) via 429 message format). ToroSquad enforces a local token bucket per table at
  `RequestsPerHourPerTable × BudgetShare` (default 60 × 0.8 = 48/h) and validates the polling config against it at
  startup (default: matches every 10 min, ≤5 pages → ≤30 req/h worst case; tournaments every 6 h).
- **Headers**: `Authorization: Apikey <key>` (VERIFIED (3rd-party) OpenAPI copy); custom User-Agent **with contact
  info** and gzip (VERIFIED for the wiki API; applied to LPDB as well). ToroSquad refuses live mode without an operator
  UA containing contact info and rejects the upstream developer's identity.
- **Errors**: 403 invalid key, 404 no data, 429 over limit, body `{"error":[...]}` (VERIFIED (3rd-party)). ToroSquad
  maps 401/403→AuthFailed, 429→QuotaExceeded(+Retry-After), 404→SchemaError (never "empty"), 5xx→bounded retry then
  TransportError, malformed JSON / missing `result`→SchemaError.
- **Pagination**: `limit`/`offset`; max limit NOT_VERIFIED (3rd-party clients cap at 1000). ToroSquad uses 200 and at
  most 5 pages; a full last page yields `Partial`.
- **Dates**: `date` is UTC `YYYY-MM-DD HH:MM:SS` (INFERRED, strong: official Lua `Date/Ext` + real record timestamp).
- **Fields used**: match2id, date, dateexact, finished, winner ("0" = draw), status (`notplayed`), resulttype/walkover
  (deprecated, fallback only), bestof, section, tournament, pagename, parent, liquipediatier(type), publishertier,
  stream, match2opponents (type, name, teamtemplate.page/name/shortname, score, status S/W/L/D/FF/DQ),
  match2games (map, scores, winner, status). VERIFIED against Help:LiquipediaDB/Match (rev 2026-09-03).
- **License / attribution**: content CC BY-SA 3.0; attribute Liquipedia and link (VERIFIED). Every message has
  "Kaynak: Liquipedia (CC BY-SA 3.0)" and a link. Logos are not used.
- **Caching**: "cache for as long as possible", no HTML scraping, don't share keys (VERIFIED). One shared fetch for
  all guilds; last good data persisted and restored after restart with its original timestamp.

## Valve Regional Standings

- Repo https://github.com/ValveSoftware/counter-strike_regional_standings, HEAD `84ccfa4d751f…` (2026-09-09,
  "Updating Regional Standings for 9/7/2026") — VERIFIED.
- **No LICENSE file** (VERIFIED). Files credit HLTV.org event data. ToroSquad displays standings with source, publication
  date and attribution; test fixtures are synthetic (no copied rows).
- Layout: `live/<YYYY>/standings_global_<YYYY>_<MM>_<DD>.md`; table `| Standing | Points | Team Name | Roster | |`,
  heading repeated; 2024 files used hyphenated dates (VERIFIED). Parser is header-based and date comes from the file name.
- Cadence: monthly (first Monday) since 2025-03 (INFERRED from tree); ToroSquad polls every 12 h. GitHub unauthenticated
  API: 60 req/h — ToroSquad uses 1–2 per refresh.
- **Team matching** (docs/NOTIFICATIONS.md#vrs): exact → configured alias (`Esports:TeamAliases`) → normalized
  (strip "team/esports/gaming/clan/club/gg") only if unique; otherwise Ambiguous/NotFound and **never** used by filters.

## Live contract tests (Phase F)

When a key is available: run a small, authorized sample (≤ 5 requests) against LPDB, store the raw response as a
contract fixture **only if** its license permits (CC BY-SA 3.0 → with attribution), and compare parser output. Until
then this is **NOT_RUN / BLOCKED**.
