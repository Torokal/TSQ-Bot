# Project status

_Last updated: 2026-09-25_

## Maturity

TSQ Bot is in **early production use for a single Discord server**. It is not publicly launched: there is no public
invite link and no global command registration. The source code is public under AGPL-3.0.

## Supported hosting

- **Production:** one Docker container on Railway with a persistent volume for SQLite, one replica, no public HTTP
  ([RAILWAY_DEPLOYMENT.md](RAILWAY_DEPLOYMENT.md)).
- **Development:** Windows 10/11 with the .NET 10 SDK ([WINDOWS_SETUP.md](WINDOWS_SETUP.md)).
- Horizontal scaling is not supported (SQLite, single in-process scheduler).

## Formula 1 module (new, 2026-09-25)

- **IMPLEMENTED / TESTED_OFFLINE**: schedule (Jolpica), driver/constructor standings (Jolpica), live lifecycle via OpenF1
  MQTT + REST reconciliation, results and corrections (OpenF1), standings on sprint/race cards, commands, admin, preview,
  doctor, migration `Formula1Module` — covered by fixture/contract/integration tests (no network).
- **Not VERIFIED_LIVE**: no real Discord delivery and no live provider run yet. Provider payload shapes were checked once
  against the public APIs (read-only) on 2026-09-25.
- **BLOCKED**: OpenF1 live access needs a paid sponsor account (owner decision); until then start notifications are
  honestly `NOT_CONFIGURED`. Details and usage terms: [FORMULA1.md](FORMULA1.md).

## Volleyball module — Filenin Sultanları (new, 2026-09-26, branch `feat/volleyball-turkey-women`)

- Scope: **Türkiye women's senior national team only** ([volleyball/VOLLEYBALL.md](volleyball/VOLLEYBALL.md)).
- **VERIFIED_LIVE (provider read only)**: FIVB VIS returned real 2026 Türkiye women's matches (VNL 2026 + EuroVolley 2026,
  24 matches, set points, UTC times) to anonymous requests; recordings are the contract test fixtures
  ([volleyball/PROVIDER_RESEARCH.md](volleyball/PROVIDER_RESEARCH.md)).
- **IMPLEMENTED / TESTED_OFFLINE**: identity filter, match state machine, reminder / started / set / final cards, dedupe,
  restart/outage safety, shared fetch, doctor/health, commands, migration `VolleyballModule`.
- **NOT verified**: live in-match updates from VIS for a Türkiye match (next match not published yet), real Discord delivery.
- **BLOCKED**: API-Sports Free (no 2026 access). **NOT_VERIFIED**: live-volleyball-api.com (would need an account).
- Module off by default; no production change without owner approval.

## TSQ Live — Twitch + Kick stream announcements (new, 2026-09-26, branch `feat/tsq-live`)

- Scope and rules: [live/TSQ_LIVE.md](live/TSQ_LIVE.md). Creators LORDTORO and NASILYANI69 (Twitch + Kick).
- **IMPLEMENTED / TESTED_OFFLINE**: creator session state machine (multistream dedupe, reconnect grace, bootstrap baseline,
  gap freshness), official-API reconciliation (Twitch Helix, Kick Public API), title sync by editing the same message,
  restart safety, provider-failure isolation, deleted-card replacement without mentions, doctor/health, migration
  `LiveModule`.
- **NOT VERIFIED_LIVE**: no real Twitch/Kick call (credentials not configured), no real Discord delivery, no real `@everyone`.
- **DEFERRED**: webhooks (Kick, Twitch EventSub webhook) — the current deployment exposes no HTTP callback endpoint (not a
  Railway limitation); EventSub WebSocket requires a user access token + refresh-token lifecycle (owner decision).
- Off by default (`Live:Enabled=false`, module gate off); no production change without owner approval.

## What has been verified against real Discord / real APIs

- Gateway connection, guild-only slash commands, minimum permissions, no privileged intents.
- Single-server restriction enforced server-side (other servers get a short refusal).
- PandaScore live data: team search, admin team filters, match reminders, time-change edits, "match started" and
  result cards; no duplicates after re-scans or restarts.
- Persistence across container restarts; in-app daily database backups.
- All card types rendered in Discord (including spoiler, postponed, cancelled and forfeit variants). A changed start
  time edits the existing reminder (verified live 2026-09-25); the former separate "time changed" card was removed on
  2026-09-26 by owner decision.

## Covered by automated tests only (not yet observed live)

- Postponed / cancelled / early-start transitions with real provider data.
- Admin vs. regular member separation with a second real account, self-service role grant/removal.
- Crash recovery, rate limiting, ambiguous delivery.
- Valve VRS live fetch; automatic HLTV links from Liquipedia (LiquipediaDB with a key, otherwise the free MediaWiki API —
  its page format was checked once against a real page on 2026-09-26; no link has been observed on a live card yet).

## Known limitations

- PandaScore offers no public match page; a "Maç Sayfası" link appears only when a verified link is available.
- Liquipedia enrichment is optional and disabled without an approved key.
- No DM commands or DM notifications (by design).
- Policies in [policies/](policies/) are drafts, not legal advice.

## Not planned yet

Public multi-server launch and global commands, news notifications, BOT Greg "stars", HLTV as a data provider.
