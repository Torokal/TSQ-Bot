# Volleyball data providers — research and decision

Scope: **only Türkiye women's senior national team ("Filenin Sultanları")**. Access date for every claim below:
**2026-09-26**. All requests were anonymous public GETs unless marked otherwise; no account was created, no form was
submitted. Module documentation: [VOLLEYBALL.md](VOLLEYBALL.md).

**Rule carried over from Formula 1:** a provider's coverage page is **not** evidence. A provider counts as verified only
when a real response contains real **2026** matches of Türkiye women's senior team.

Labels: `VERIFIED_LIVE` (a real response with real 2026 Türkiye W data was seen), `VERIFIED_DOCS_ONLY`, `NOT_VERIFIED`,
`BLOCKED`, `REJECTED`.

## Decision

**Primary provider: FIVB VIS web service** (`https://www.fivb.org/Vis2009/XmlRequest.asmx`). It is the only candidate
that returned real 2026 Türkiye women's matches (VNL 2026 and CEV EuroVolley 2026) with set points and UTC times, it is
official, and public data needs no credentials. **No fallback provider** is wired: none is verified (see table), and
mixing an unverified source into notifications is worse than having none.

## Summary

| Provider | 2026 Turkey W verified | Upcoming | Live | Set score | Official | Free quota | Terms | Risk |
|---|---|---|---|---|---|---|---|---|
| **FIVB VIS** | **VERIFIED_LIVE** — 24 matches (VNL 2026: 15, EuroVolley 2026: 9) | Yes (status 1 "Scheduled" seen for other teams; no Türkiye W match after 2026-09-26 exists yet) | **NOT_VERIFIED** — format documented; live observation of a CEV match: see below | Yes (`pointsTeamASet1..5`) | Yes (FIVB) | No published limit; app id "should", not "must" | No API terms published; volleyballworld.com ToS: personal non-commercial use, logos need permission | Medium: legacy ASMX behind Cloudflare, no SLA, incomplete docs |
| volleyballworld.com internal API | not called | – | – | – | FIVB site | – | `robots.txt: Disallow: */api/*`; ToS forbids scraping | **REJECTED** |
| CEV (eurovolley.cev.eu, www-old.cev.eu) | NOT_VERIFIED (HTML only, no JSON API found) | HTML | HTML `LiveScore.aspx` | HTML | Yes | – | not found | High (scraping) — **REJECTED** as a source; VIS already carries EuroVolley (`WCEV1573`) |
| TVF (tvf.org.tr, fikstur.tvf.org.tr, tvf-web.dataproject.com) | NOT_VERIFIED (news articles; domestic leagues only in fixtures) | news only | No | news text | Federation | – | no data terms | High — **REJECTED** as a feed; useful for manual verification of friendlies/broadcasts |
| live-volleyball-api.com | **NOT_VERIFIED** (docs sample shows `"Turkey (W)"` — not evidence; no public demo endpoint) | claimed | claimed | claimed | No (reseller of an undisclosed upstream) | 100 trial credits, then paid | commercial use allowed; "no guarantee of completeness, accuracy, timeliness" | High — would need a keyed test (account creation = owner approval) |
| **API-Sports volleyball** | **BLOCKED** on the Free plan: real request `GET /games?league=184&season=2026` → `errors.plan: "Free plans do not have access to this season, try from 2022 to 2024."` | – | – | – | No | 100 req/day (Free) | – | Coverage page lists 2026 but Free cannot read it — exactly the F1 precedent |
| Sofascore | not called | – | – | – | No (unofficial) | – | not permitted | **REJECTED** |
| Sportradar / Genius Sports / Flashscore | NOT_VERIFIED | – | – | – | No | none (enterprise) / scraping only | contract | cost |

## FIVB VIS — evidence

Documentation (primary source): <https://www.fivb.org/VisSDK/VisWebService/> — pages `Requests.html`,
`Application%20identifier.html`, `GetVolleyMatchList.html`, `VolleyMatchFilter.html`, `VolleyMatchStatus.html`,
`VolleyTournamentType.html`, `VolleyMatch.html`.

| # | Request (Filter / type) | Result |
|---|---|---|
| 1 | `GetServiceInformation` | HTTP 200 `<OK Id="FivbVis" Version="26.907.1759.1" Date="2026-09-07" />`, served via Cloudflare, no rate-limit headers |
| 2 | `GetVolleyTournamentList` `Genders="W" Seasons="2026"` | 54 tournaments, incl. `No=1662 WVNL2026` (type 12 NationsLeague), `No=1471 WCEV1573` (type 10), a **trap** `No=1736 WVNL26XX "VNL 2026 - WOMEN (TEST ONLY)"` (type 6 Test, contains TUR matches); `Seasons="2027"` → 0 items |
| 3 | `GetVolleyMatchList` `FirstDate=2026-06-01 LastDate=2026-09-30 TournamentGenders=W TournamentTypes=<senior allow-list>` + `Relation Tournament` (**the request TSQ uses**) | HTTP 200, 308 matches, **24 with TUR** — all VNL 2026 / EuroVolley 2026 senior women, no test or youth matches |
| 4 | same, JSON | e.g. `no 26665, dateTimeUtc 2026-07-26T11:30:00Z, TUR 3-1 BRA, sets 23-25 25-23 26-24 25-21, status 25 "Result is official"`; `no 28900, ITA 2-3 TUR, sets 25-16 22-25 25-12 21-25 10-15` (EuroVolley final; matches independent news: aa.com.tr) |
| 5 | `Version="<last>"` | `{"data":[],"nbItems":0,"version":…}` — incremental "no changes" works; later a changed match was returned when the version moved |
| 6 | `ForLiveScore="True"` (no date) | returned **all 1909 matches** → the flag is not a reliable filter; **not used** (TSQ uses `NoMatches`) |
| 7 | missing `Fields` / unknown request type | HTTP 400; unknown field names are silently ignored; `LastChangeDT`/`DeletedDT` are not public |
| 8 | real decoys (recorded as test fixtures) | U17 girls world championship (type 16) with TUR, EuroVolley **men** with TUR, the TEST tournament with TUR — all share the `TUR` code |

Recorded, trimmed responses are the contract test fixtures: `tests/ToroSquad.Tests/Fixtures/volleyball/*.json` (each
file carries its exact request).

### Türkiye women 2026 (from VIS)

- **VNL 2026** (tournament 1662, 15 matches, all official): 2026-06-03 DOM 2-3 TUR … final 2026-07-26 **TUR 3-1 BRA**.
- **CEV EuroVolley 2026** (tournament 1471, 9 matches, Istanbul): 2026-08-21 TUR 3-0 LAT … final 2026-09-06 **ITA 2-3 TUR**.
- **Not in VIS:** friendlies (e.g. vs France, Antalya, 2026-08-06..08 — TVF news only) and the Mediterranean Games squad.
  They cannot be announced by this module (honest limitation; documented to users via `/volleyball` footnote).
- **Next match:** none listed after 2026-09-26; no 2027 women's tournament exists in VIS yet. The 2027 FIVB Women's World
  Cup (Türkiye qualified; volleyballworld.com news 2026-09-15) and VNL 2027 are expected — the module will be idle until VIS
  publishes them.

### Field semantics used by TSQ (verified on real responses)

- `dateTimeUtc` is UTC with `Z`. `dateTimeLocal` has **no offset** in real responses (the docs example shows one) → never used.
- `scheduleInfo`: 4 = date and time confirmed; anything else = no start time for reminders.
- `status` (VolleyMatchStatus, ordered): 1–3 not started, 4–23 in play (set N ready / in set N / set N finished), 24 finished,
  25 official, 26 corrected, 27 closed. **No postponed/cancelled status exists.**
- `matchPointsA/B` = sets won, `pointsTeamASetN/BSetN` = set points, `nbSets` = current set while in play, `resultType` ≠ 0 = forfeit.
- Team numbers (`noTeamA/B`) are **per-tournament registrations** (Türkiye = 8632 in VNL 2026, 9283 in EuroVolley 2026) —
  not a stable team id. Identity therefore comes from: tournament `gender` (1 = women) + tournament `type` (senior allow-list:
  OlympicGames, WorldChampionship, ContinentalChampionship, OlympicGamesQualification, NationsLeague, WorldCup,
  WorldChampionshipQualification) + no age marker in tournament/team name + `teamXCode = TUR`.
- No pagination: the full filtered list is returned; TSQ checks `nbItems` against the items (truncation = schema error).

### Live state — observation (not a Türkiye match)

To check live behaviour, the CEV EuroVolley **men's** match FIN–SLO (VIS `no 28975`, scheduled 2026-09-26 15:00 UTC) was
polled every 2 minutes (2026-09-26) with the live request TSQ uses (`NoMatches`; full and `Version` requests alternating):

| UTC | Request | VIS answer (match 28975) |
|---|---|---|
| 14:48–15:08 | full / `Version` alternating | `status 1` (Scheduled), no score; "no changes" answers to `Version` worked |
| 15:10–15:18 | versioned | item returned whenever its `version` moved (57446013 → 57446021), still `status 1` — the match started later than 15:00 |
| 15:20:50 | full | `status 5` (in set 1), `nbSets 1`, `0-0`, set 1 **15-14** |
| 15:22:50, 15:24:50 | versioned, full | **`status 1`, no score again** (newer `version` 57446023) — a live answer followed by "scheduled" |
| ≈15:28 | full | `status 5`, set 1 19-17 |
| ≈15:38 | full | `status 5` ("in set 1") but `matchPoints 1-0` and set 1 **25-23**, no set 2 points — the status lags the score |

What this proves (and what not):

- VIS **does** carry in-match set points for a CEV-run match, updated every few minutes (not per rally in this sample).
- VIS answers can **go backwards** (live → scheduled) and the status field can **lag** the set count. The module therefore
  (a) derives set transitions from set counts + set points, never from the status field, (b) never lowers a recorded state,
  and (c) announces a new state only after **two consecutive consistent observations**.
- This was a men's CEV match, not a Türkiye women's FIVB match: live cards for Filenin Sultanları remain **NOT_VERIFIED**.

Conclusion for notifications: "match started" and set cards depend on VIS status/set points changing **during** the match.
Until that is observed for a Türkiye women's (FIVB-run) match, live cards are **NOT_VERIFIED**; the module is fail-safe
(no live change in VIS = no card; the final is still sent when VIS publishes the result).

### Terms, attribution, quota

- Public data is readable anonymously ("If your application needs only access to public data, you don't need to
  authenticate"); FIVB asks for an application id header `X-FIVB-App-ID` (by e-mail to FIVB) — optional config
  `Volleyball:Fivb:AppId`, never logged.
- No published rate limit. TSQ's own budget: 10 requests/minute; real use ≈ 4 requests/day without a match, ≈ 1/minute
  during a match window (~5 h), one shared fetch for all servers.
- No API-specific terms were found. volleyballworld.com ToS (<https://en.volleyballworld.com/terms-of-service>): personal,
  non-commercial use; FIVB/Volleyball World marks need permission → TSQ shows "Kaynak: FIVB" as text and **uses no FIVB,
  Volleyball World, CEV or TVF logo**. Commercial use would need written FIVB consent (operator responsibility).

## API-Sports — evidence (owner's existing Free key, 2026-09-26)

- `GET /status` → `subscription.plan = "Free"`, `limit_day = 100` (not counted).
- `GET /leagues?search=Nations` → `id 184 "Nations League Women"`, seasons list **includes 2026** (coverage claim).
- `GET /games?league=184&season=2026` → HTTP 200, `results 0`,
  `errors.plan = "Free plans do not have access to this season, try from 2022 to 2024."`
- `GET /games?league=184&season=2024` → 104 games, e.g. `Japan W 3-2 Turkey W` (team id 2229).
- Verdict: **NO_GO on Free** (identical to the Formula 1 finding). A paid plan would need a new keyed test and owner approval.

## Sources

- FIVB VIS docs: <https://www.fivb.org/VisSDK/VisWebService/>
- VIS endpoint: <https://www.fivb.org/Vis2009/XmlRequest.asmx>
- volleyballworld.com ToS: <https://en.volleyballworld.com/terms-of-service>; robots: <https://en.volleyballworld.com/robots.txt>
- CEV: <https://eurovolley.cev.eu/en/2026/women/>, <https://www-old.cev.eu/Competition-Area/competition.aspx?ID=1573>
- TVF: <https://tvf.org.tr/takvim>, <https://fikstur.tvf.org.tr/Takvim>, <https://tvf.org.tr/icerik/filenin-sultanlari-hazirlik-macinda-fransayi-3-1-maglup-etti>
- live-volleyball-api.com: <https://live-volleyball-api.com/docs>, `/pricing`, `/terms`
- API-Sports: <https://api-sports.io/documentation/volleyball/v1> (Cloudflare bot check for anonymous browsers), `https://v1.volleyball.api-sports.io`
- EuroVolley final (independent): <https://www.aa.com.tr/en/turkiye/turkiye-retain-european-womens-volleyball-title-with-3-2-win-over-italy/4049063>
- 2027 World Cup qualification: <https://en.volleyballworld.com/news/18-teams-already-qualified-for-women-s-world-cup-2027>
