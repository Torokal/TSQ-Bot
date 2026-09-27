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

## TSQ LFG — Oyuncu Bul (new, 2026-09-27, branch `feat/lfg`)

- Scope and rules: [lfg/TSQ_LFG.md](lfg/TSQ_LFG.md). One generic listing lifecycle for any game (no game-specific model).
- **IMPLEMENTED / TESTED_OFFLINE**: `/ekip` (free-text game + details, team size, optional 1/2/3 h), join/leave/full/reopen,
  owner/moderator close with confirmation, lazy + worker expiry, restart recovery, deleted-card handling (`Orphaned`),
  per-user active limit, optional listing channel (`/lfg-admin`), privacy export/delete/retention, race tests (last slot,
  many joins, double click, concurrent creates), migration `LfgModule`. Deleted cards: no message-delete events (Guilds
  intent only) — a throttled one-read check per active card (≤ every 5 min) and an immediate check of the caller's cards at
  the limit orphan them. Offline contract test of the edit route (bot-token PATCH on the channel message, no interaction
  token, allowed_mentions empty).
- **Production (2026-09-27)**: PR #12 merged and deployed, `/ekip` + `/lfg-admin` synced to the main guild; enabling the
  module (`/modules enable lfg`) and the live checks are done by the owner in Discord.
- **NOT VERIFIED_LIVE**: the bot editing its own card through the channel endpoint (expiry/close) has not been observed live.
- **V2 IMPLEMENTED / TESTED_OFFLINE**: Maybe RSVP (never a slot, never pinged), relative scheduled start (`EventAt`;
  `ExpiresAt = EventAt + duration`), opt-in 30-minute and start notices through the outbox (current Joined players only,
  once across restarts, never late, none for ended listings or a disabled module), `MentionPolicy.ExplicitUsers` (only
  the LFG notice renderer), optional voice channel + button (moves a member already in voice with Move Members; otherwise
  an honest "open channel" link), migration `LfgScheduledEvents`.
- **Custom start date IMPLEMENTED / TESTED_OFFLINE**: `/ekip tarih_saat` (`05.10.2026 21:30`, culture-independent), read in
  the existing guild time zone (`/setup`, default Europe/Istanbul), DST gaps/overlaps refused, 1 minute – 1 year ahead,
  exclusive with `baslangic`; feeds the same `EventAt` (expiry, notices, card, voice unchanged). No new migration.
- **NOT VERIFIED_LIVE**: real user pings, the voice move and the channel link behaviour in Discord clients.
- **Form + owner edit IMPLEMENTED / TESTED_OFFLINE** (`feat/lfg-modal-edit`): `/ekip` has no options and opens a modal
  (game, players, details, one start field — empty/now, `30 dk`, `1,5 saat`, `2 saat`, `05.10.2026 21:30` — and duration);
  Discord's five-component modal limit puts the two notice opt-ins and the voice channel in a private settings step
  (native selects) before **İlanı Oluştur**; drafts live only in memory (30 min, owner + guild bound). The card gains
  **✏️ Düzenle** (second row, via the additive `MessageButton.NewRow`): owner-only, same form prefilled; size never below
  the Joined players, start only before the event, `ExpiresAt = (EventAt ?? CreatedAt) + duration`, handled notices never
  repeat or revive, same card redrawn without pings. No migration.
- **NOT VERIFIED_LIVE**: the modal and settings step in Discord clients, the public follow-up card after the settings step
  and the bot's later edit of that card.
- Off by default (module gate off); commands are registered only by the owner via `scripts/Sync-Commands.ps1`.

## TSQ Quote (new, 2026-09-27, branch `feat/quote`)

- Scope and rules: [quote/TSQ_QUOTE.md](quote/TSQ_QUOTE.md). Stateless utility module: no table, no migration, no worker.
- **IMPLEMENTED / TESTED_OFFLINE**: `/quote message:<id or link> [channel]`; reference parsing (snowflake, discord.com /
  ptb / canary / discordapp.com links, DM and other-server links refused); server-side checks (same guild, message channel
  of this guild, member and bot `ViewChannel` + `ReadMessageHistory` on the channel or the thread's parent, no private
  threads, no age-restricted text into other channels) with one answer for every "no"; Discord text → plain text (mentions,
  markdown, code, timestamps, escapes); ImageSharp card renderer (embedded Noto fonts, greyscale avatar fade, wrapping,
  font shrink, grapheme-safe ellipsis, fallback panel); bounded avatar download (Discord CDN only, 5 s, 4 MB).
- **Message-ID flow IMPLEMENTED / TESTED_OFFLINE**: primary UX `/quote message:<id>` (current channel, or `channel:`; link
  still accepted; never a channel scan); the bot's `ViewChannel` + `AttachFiles` where the command ran is checked before
  anything is read or drawn (`quote.bot_cannot_attach`); an empty text is classified from the returned message
  (attachments/embeds/poll, forward, system, own/mentioning message = no text; a completely empty normal message = content
  probably withheld, `quote.content_unavailable`, no claimed certainty); rendering failures log ids + exception type + trace
  code only; tests assert no log line ever contains the message body, names or avatar url. Invite integer 117760.
- **Owner decision made (2026-09-27)**: Message Content privileged access is used for TSQ Quote. Discord documents it as
  not tied to any gateway event (REST content fields), so the gateway Identify stays `Guilds`; no message events, no
  cache, no listener. **Pending (owner)**: toggle MESSAGE CONTENT INTENT in the Developer Portal; grant Attach Files to the
  bot role (or the channels) in the main guild.
- **NOT VERIFIED_LIVE**: the whole command in Discord (a normal member's non-mentioning message read by id with content,
  private defer → public `quote.png` follow-up, Attach Files enforcement, the real Discord CDN download, Discord.Net
  permission resolution on real overwrites). Live acceptance plan: docs/quote/TSQ_QUOTE.md.
- Off by default (module gate off); `/quote` is in the manifest but not synced.

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
