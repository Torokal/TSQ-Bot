# Esports data providers — capabilities, terms, status

Research dates: Liquipedia/Valve 2026-09-24, PandaScore 2026-09-25. Labels: **VERIFIED** (read at the primary/official source), **VERIFIED (archive)**
(official page read via Web Archive because the live page was behind a bot challenge), **VERIFIED (3rd-party)**
(read in public third-party code/copies), **INFERRED**, **NOT_VERIFIED**.

## Current status

| Provider | Role | Mode today | Live access | Status |
|---|---|---|---|---|
| **PandaScore** (REST API) | **Default match provider** (`Esports:Provider:Name=PandaScore`) | Fixture (synthetic data through the real client/parser) | Needs a PandaScore token (free plan exists) — none configured | **BLOCKED** (live) / **TESTED_OFFLINE** (parser, lifecycle, pagination, error types) |
| Liquipedia (LiquipediaDB API v3) | **Legacy / optional** match provider (only when selected) | Fixture | Requires an **approved API key** — none configured | **BLOCKED** (live) / **TESTED_OFFLINE** |
| Valve Regional Standings (GitHub) | Rankings (VRS), independent of the match provider | Fixture | Public, no key | **TESTED_OFFLINE**; live fetch **NOT_RUN** |
| HLTV | **Not a data provider.** Preferred *external match page* when a verified URL exists | — | No authorized access; **scraping prohibited by design** | Link policy **TESTED_OFFLINE**; data integration **DEFERRED** |

A failing live provider **never** falls back to fixture data: the fixture handler is only wired in Fixture mode
(`src/ToroSquad.Modules.Esports/EsportsModule.cs`).

## PandaScore (REST API) — default

All facts below were read on 2026-09-25 at developers.pandascore.co (docs pages + OpenAPI definitions) and pandascore.co
(pricing, terms). Labels as above.

- **Endpoints** (VERIFIED): CS2 uses the legacy `/csgo/` prefix. TSQ Bot calls `GET /csgo/matches?range[scheduled_at]=FROM,TO&sort=scheduled_at`
  (one query returns every status in the window) and `GET /csgo/tournaments?range[begin_at]=…` for events. Both are marked
  "available to all customers". (`/csgo/matches/{id}` needs a historical plan — not used.)
- **Auth** (VERIFIED): token as `Authorization: Bearer <token>` header (or `?token=`). TSQ Bot uses the header only, so the
  token never appears in a URL, log or trace. Config: `PandaScore:Token` via user-secrets/env only.
- **Rate limit** (VERIFIED): "Schedules, Results & Context Data" (free) plan **1,000 requests/hour**; paid plans 10,000/h.
  Remaining quota in `X-Rate-Limit-Remaining`. 429 on excess. TSQ Bot budgets **50%** (500/h) with a local token bucket
  that also charges retries; default polling (matches every 5 min, ≤5 pages; events every 6 h) needs ≤ 61 req/h worst case,
  validated at startup.
- **Pagination** (VERIFIED): `page[number]` (from 1) and `page[size]` (default 50, **max 100**); `Link`, `X-Page`,
  `X-Per-Page`, `X-Total` headers. TSQ Bot: size 100, ≤5 pages, stops at a short page or `X-Total`; hitting the page
  limit is reported as **Partial**, never as complete.
- **Errors** (VERIFIED): 400 malformed, 401 missing token, 403 endpoint/field not in plan, 404 not found, 429 limit, 5xx
  server (retry). TSQ Bot: 401/403 → AuthFailed, 429 → QuotaExceeded (+Retry-After), 5xx/timeout → bounded retry then
  TransportError/Timeout, 400/404/malformed/non-array → SchemaError. **None of these is "no matches".**
- **Lifecycle** (VERIFIED, "Matches lifecycle"): `not_started` (`begin_at` = `scheduled_at` for legacy reasons),
  `running` (`begin_at` = actual start), `finished` (`end_at`, `winner_id`), `canceled` (`forfeit=false`: no winner;
  `forfeit=true`: `winner_id` set), `postponed` (new date unknown, `scheduled_at` not updated). Officially
  **rescheduled** matches keep `not_started` with `rescheduled=true`, new `scheduled_at` and `original_scheduled_at`;
  delays caused by a previous match are **not** flagged as rescheduled.
- **Match fields used** (VERIFIED, OpenAPI): id, status, scheduled_at, begin_at, end_at, original_scheduled_at, rescheduled,
  forfeit, draw, match_type (`best_of`/`first_to`/…), number_of_games, winner_id, opponents[{type, opponent{id,name,acronym}}],
  results[{team_id, score}], games[{position,status,winner}], tournament{name,tier}, league{name}, serie{full_name},
  streams_list (not shown in cards). Tiers `s,a,b,c,d,unranked` → TSQ scale 1..5 / none.
- **Public match page**: **none** in the match object (`league.url` is the league website). So PandaScore matches get a
  "Maç Sayfası" only from a verified external link (see HLTV below).
- **Plans** (VERIFIED, pricing page): free plan 0 €, 1K req/h, "no credit card"; stats plans "restricted to non-betting-
  related usage". **Conflict** (NOT_VERIFIED): the developer overview says the free plan includes "schedules and results",
  while the pricing page lists "No post-match results or historical statistics" for it. Whether `winner_id`/`results` are
  populated on the free plan must be checked with a real token before result cards are called VERIFIED_LIVE.
- **Terms** (VERIFIED, pandascore.co/terms-and-condition): art. 6.4 requires the source line **"Source: PandaScore"** on any
  medium reproducing the data → every card footer says "Kaynak: PandaScore" and `/bot about` lists PandaScore. Raw data
  must not be redistributed as-is and direct API access/URLs must not be given to end users (art. 6.3/6.4) → TSQ Bot shows
  only processed cards, never API URLs or raw JSON.
- **Not used**: WebSockets/live feed (paid), `/incidents` change feed (available on all plans; not needed at current volume —
  one windowed query per poll already sees reschedules/corrections).

## HLTV — link only, never scraped

- TSQ Bot **does not** scrape HLTV HTML, bypass Cloudflare, automate a browser, use unofficial scraping libraries, call
  undocumented endpoints, guess match ids or construct HLTV URLs. PandaScore data is never presented as HLTV data.
- A verified HLTV match URL may be the **preferred "Maç Sayfası"** target. It enters only from a trusted deterministic source:
  today the operator-curated `Esports:VerifiedMatchLinks` list (match key → URL), validated at startup; later possibly an
  authorized provider field or an admin command. Validation is syntactic only (`https://www.hltv.org/matches/<id>/<slug>`),
  the page is never fetched.
- Direct HLTV data integration requires authorized access → **DEFERRED**.

### Where legitimate HLTV match links come from (research 2026-09-25)

| Source | HLTV match link? | Access | Status |
|---|---|---|---|
| HLTV itself | — | **No official public API** found; every "HLTV API" found is an unofficial scraper | Not usable (scraping prohibited) |
| **Liquipedia (LPDB match2 `links.hltv`)** | **Yes** — editors enter the HLTV match id; Liquipedia builds `https://www.hltv.org/matches/<id>/match` (VERIFIED: Liquipedia/Lua-Modules `Module:MatchExternalLinks` + `MatchGroup/Input/Custom.getLinks`, commit 23835da, 2026-09-09) and stores it with the match; upstream BOT-Greg-v2 reads `links.hltv["1"]["1"]` (VERIFIED) — this is how BOT Greg's "Matchpage" worked | Approved LPDB API key (free for approved open-source non-commercial projects, else paid) | Parser support **TESTED_OFFLINE**; live **BLOCKED** (no key) |
| PandaScore | No HLTV id/URL in the match object (VERIFIED, OpenAPI) | — | Not available |
| GRID | NOT_VERIFIED | All listed plans commercial, custom-priced (grid.gg, 2026-09-25) | Not evaluated further |

With `Esports:Provider:Name=Liquipedia` the cards link to HLTV automatically. With PandaScore as match provider, HLTV links
would need a second source (Liquipedia) and a cross-provider match mapping — not implemented (owner decision pending).

## Capabilities (as implemented)

| Capability | PandaScore adapter | Liquipedia adapter | Valve adapter |
|---|---|---|---|
| Fixtures (upcoming) | ✅ | ✅ | — |
| Results | ✅ (free-plan population NOT_VERIFIED) | ✅ | — |
| Tournaments | ✅ | ✅ | — |
| Team list (from match data) | ✅ (provider team ids) | ✅ | — |
| **Verified live status** | ✅ `running` (stated by PandaScore) | ❌ **unavailable** — LPDB has no live flag (VERIFIED: Liquipedia's own MatchTicker treats "not finished and start passed" as ongoing; that is not evidence of play) | — |
| Postponed / rescheduled / cancelled | ✅ (status + `rescheduled` flag) | partial (no flag) | — |
| Rankings | — | — | ✅ global VRS |

Consequences in the product: reminders are always "planned start" reminders. With PandaScore a provider-stated `running`
state (observed after a scheduled state) sends a "Maç başladı" card; with Liquipedia a passed start time is only shown as
"awaiting result" and no started card exists. Clock time alone never means "started".

## Liquipedia is legacy/optional

Select it with `Esports:Provider:Name=Liquipedia` (then `Esports:MatchPollMinutes` must be ≥ 10 for its 60 req/h budget).
Normal operation does not need a Liquipedia key.

## Liquipedia (LiquipediaDB API v3)

- **Access**: "Upon approved request" (VERIFIED, https://liquipedia.net/api-terms-of-use). Keys at https://api.liquipedia.net.
- **Plans** (VERIFIED (archive) 2026-06-18 of https://liquipedia.net/api; live page returned a bot challenge): Basic
  $49/mo per data type (1000 req/h), Premium $199/mo (5000 req/h), Enterprise custom. Free access "strictly reserved"
  for educational / non-commercial / community-feature projects, **code must be open source**, usually time-limited.
  Whether a Discord bot qualifies: **NOT_VERIFIED** (Liquipedia decides). A search snippet claiming only Enterprise is
  currently served: NOT_VERIFIED. → Requesting access or paying is the owner's decision (approval gate).
- **Rate limit**: "no more than 60 requests per 1 hour" baseline (VERIFIED, terms). Applies per key/wiki/table
  (VERIFIED (3rd-party) via 429 message format). TSQ Bot enforces a local token bucket per table at
  `RequestsPerHourPerTable × BudgetShare` (default 60 × 0.8 = 48/h; every HTTP attempt including retries spends a token) and validates the polling config against it at
  startup (default: matches every 10 min, ≤5 pages → ≤30 req/h worst case; tournaments every 6 h).
- **Headers**: `Authorization: Apikey <key>` (VERIFIED (3rd-party) OpenAPI copy); custom User-Agent **with contact
  info** and gzip (VERIFIED for the wiki API; applied to LPDB as well). TSQ Bot refuses live mode without an operator
  UA containing contact info and rejects the upstream developer's identity.
- **Errors**: 403 invalid key, 404 no data, 429 over limit, body `{"error":[...]}` (VERIFIED (3rd-party)). TSQ Bot
  maps 401/403→AuthFailed, 429→QuotaExceeded(+Retry-After), 404→SchemaError (never "empty"), 5xx→bounded retry then
  TransportError, malformed JSON / missing `result`→SchemaError.
- **Pagination**: `limit`/`offset`; max limit NOT_VERIFIED (3rd-party clients cap at 1000). TSQ Bot uses 200 and at
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
- **No LICENSE file** (VERIFIED). Files credit HLTV.org event data. TSQ Bot displays standings with source, publication
  date and attribution; test fixtures are synthetic (no copied rows).
- Layout: `live/<YYYY>/standings_global_<YYYY>_<MM>_<DD>.md`; table `| Standing | Points | Team Name | Roster | |`,
  heading repeated; 2024 files used hyphenated dates (VERIFIED). Parser is header-based and date comes from the file name.
- Cadence: monthly (first Monday) since 2025-03 (INFERRED from tree); TSQ Bot polls every 12 h. GitHub unauthenticated
  API: 60 req/h — TSQ Bot uses 1–2 per refresh.
- **Team matching** (docs/NOTIFICATIONS.md#vrs): exact → configured alias (`Esports:TeamAliases`) → normalized
  (strip "team/esports/gaming/clan/club/gg") only if unique; otherwise Ambiguous/NotFound and **never** used by filters.

## Live contract tests (Phase F)

When a key is available: run a small, authorized sample (≤ 5 requests) against LPDB, store the raw response as a
contract fixture **only if** its license permits (CC BY-SA 3.0 → with attribution), and compare parser output. Until
then this is **NOT_RUN / BLOCKED**.
