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

## What has been verified against real Discord / real APIs

- Gateway connection, guild-only slash commands, minimum permissions, no privileged intents.
- Single-server restriction enforced server-side (other servers get a short refusal).
- PandaScore live data: team search, admin team filters, match reminders, time-change edits, "match started" and
  result cards; no duplicates after re-scans or restarts.
- Persistence across container restarts; in-app daily database backups.
- All card types rendered in Discord (including spoiler, postponed, rescheduled, cancelled and forfeit variants).

## Covered by automated tests only (not yet observed live)

- Postponed / cancelled / early-start transitions with real provider data.
- Admin vs. regular member separation with a second real account, self-service role grant/removal.
- Crash recovery, rate limiting, ambiguous delivery.
- Valve VRS live fetch; Liquipedia HLTV-link enrichment (needs an approved API key).

## Known limitations

- PandaScore offers no public match page; a "Maç Sayfası" link appears only when a verified link is available.
- Liquipedia enrichment is optional and disabled without an approved key.
- No DM commands or DM notifications (by design).
- Policies in [policies/](policies/) are drafts, not legal advice.

## Not planned yet

Public multi-server launch and global commands, news notifications, BOT Greg "stars", HLTV as a data provider.
