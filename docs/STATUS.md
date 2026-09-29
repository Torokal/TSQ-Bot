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
- **Waitlist IMPLEMENTED / TESTED_OFFLINE** (`feat/lfg-waitlist`): a full team no longer refuses Katıl — the same button
  (🎟️ Sıraya Gir while full) queues the player first come first served (durable `WaitlistOrder`, assigned under the write
  lock); every freed slot (leave, maybe, a larger size, /privacy delete) goes to the first in line in the same write;
  invariant: no free slot while someone waits. Waitlisted players are never pinged and get no voice action; a promoted
  player is Joined for the next notices (no late reminder). Migration `LfgWaitlist` (one nullable column). NOT VERIFIED_LIVE.
- **V2 IMPLEMENTED / TESTED_OFFLINE**: Maybe RSVP (never a slot, never pinged), scheduled start (`EventAt`;
  `ExpiresAt = EventAt + duration`), opt-in 30-minute and start notices through the outbox (current Joined players only,
  once across restarts, never late, none for ended listings or a disabled module), `MentionPolicy.ExplicitUsers` (only
  the LFG notice renderer), optional voice channel + button (moves a member already in voice with Move Members; otherwise
  an honest "open channel" link), migration `LfgScheduledEvents`.
- **Start date IMPLEMENTED / TESTED_OFFLINE**: a full date and time (`05.10.2026 21:30`, `05.10.26 21:30`,
  culture-independent), read in the existing guild time zone (`/setup`, default Europe/Istanbul), DST gaps/overlaps
  refused, 1 minute – 1 year ahead; feeds the same `EventAt` (expiry, notices, card, voice unchanged). No relative starts.
  No new migration.
- **NOT VERIFIED_LIVE**: real user pings, the voice move and the channel link behaviour in Discord clients.
- **Form + owner edit IMPLEMENTED / TESTED_OFFLINE** (`feat/lfg-modal-edit`): `/ekip` has no options and opens a modal
  (game, players, details, start date — empty = now or `27.09.2026 21:30` / `27.09.26 21:30`, no relative times — and the
  voice channel as a native channel select); Discord's five-component modal limit puts the listing duration (1/2/3 h) and
  the two notice opt-ins in a private settings step (native selects) before **İlanı Oluştur**; drafts live only in memory (30 min, owner + guild bound). The card gains
  **✏️ Düzenle** (second row, via the additive `MessageButton.NewRow`): owner-only, same form prefilled; size never below
  the Joined players, start only before the event, `ExpiresAt = (EventAt ?? CreatedAt) + duration`, handled notices never
  repeat or revive, same card redrawn without pings. No migration.
- **Modal UX V3 IMPLEMENTED / TESTED_OFFLINE** (`feat/lfg-modal-edit`, follow-up PR): the main modal is exactly game (text),
  team size (string select generated from 2 … `Lfg:MaxPlayersPerListing`; a range over 25 options is a startup config error,
  never cut), start date (text; no public date picker), voice (channel select) and the two notices (checkbox group, off on
  create, current flags on edit; notices with an empty start are refused with a way back, never dropped). Details moved to
  their own small modal opened from the settings step (📝 Detay Ekle / Detayı Düzenle; empty clears). The card always shows
  the start (`🕘 Başlangıç: Şimdi` for "now"). No migration, command manifest unchanged.
- **V3 merged** (PR #17, `cbd7234`), deployed, command sync dry-run "Nothing to change".
- **Field errors IMPLEMENTED / TESTED_OFFLINE** (`feat/lfg-modal-edit`): Discord's own pre-submit checks are used wherever the
  public API has them (required, min/max length, select choices); everything else is checked after submit and refused as
  `❌ **field**` + reason with **✏️ Formu Düzelt** (the filled form again; a refused voice channel is not re-offered, a text
  outside the input's own limits is not prefilled). The card's start label is bold.
- **NOT VERIFIED_LIVE (V3)**: the string select and the checkbox group inside the modal in Discord clients (rendering,
  preselection on edit, the submitted values), the details modal opened from the settings message.
- **NOT VERIFIED_LIVE**: the voice channel select inside the modal (rendering, clearing, and Discord echoing the preselected
  channel on submit — an edit that only changes the details must keep the voice channel), the duration select in the settings
  step; the modal and settings step in Discord clients, the public follow-up card after the settings step
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

## TSQ Doğum Günü (new, 2026-09-28, branch `feat/birthday`)

- Scope and rules: [birthday/TSQ_BIRTHDAY.md](birthday/TSQ_BIRTHDAY.md). Additive migration `BirthdayModule` (4 new tables).
- **IMPLEMENTED / TESTED_OFFLINE**: `/birthday set|show|remove` (day + month, no year; 29.02 valid), `/birthday-admin
  configure|status|doctor`; reconciliation on the Europe/Istanbul day (startup, every 60 s, just after local midnight):
  one announcement per guild and day (unique guild + local date, staged with the outbox in one transaction, pings only the celebrants,
  never sent the next day), celebrated once a year (unique guild + user + year), temporary role given only by the bot and
  taken back when the next day starts (a role the member already had is never touched), restart/downtime/duplicate-pass
  safe, members who left skipped, role hierarchy problems isolated (announcement still sent, doctor FAILED).
- **Delta (2026-09-28)**: `/birthday-admin set member date` (Administrator or guild owner via `Authorize.Require`; Manage
  Server is not enough; same registration row and date rules; audit log `source=admin`); the announcement now pings
  exactly the celebrants it names (`MentionPolicy.ExplicitUsers`, second and last user-ping producer besides LFG).
- **Delta (2026-09-28, branch `feat/birthday-admin-show`)**: `/birthday-admin show member` — one member's saved day + month
  for an Administrator or the guild owner (same check as `set`, before the database is read; Manage Server refused),
  private and ping-free, audit log `birthday_admin_viewed` without the date. No list, no new data.
- `IGuildGateway.GetMemberAsync` (REST member lookup) added for the member check and the member's current roles.
- **NOT VERIFIED_LIVE**: everything (announcement and its user pings in Discord, `/birthday-admin set` with a real
  Administrator / owner / Manage-Server-only account, role grant/removal and the real hierarchy on TSQ, REST member lookup,
  midnight run on Railway). Off by default. Deployed and synced 2026-09-28 (PR #19, #20); `show` needs a new sync.

## TSQ Döviz & Altın (new, 2026-09-28, branch `feat/currency-module`)

- Scope and rules: [currency/TSQ_CURRENCY.md](currency/TSQ_CURRENCY.md). No own table, no migration, no API key; the only
  worker is the daily 09:00 card (staged into the shared outbox).
- **IMPLEMENTED / TESTED_OFFLINE**: `/dolar`, `/euro`, `/altın` (public cards); Altınkaynak → TCMB (USD/EUR, labelled
  "Gösterge Kuru", bulletin date) / Trunçgil (gram gold) → last good price ≤ 15 min (marked stale) → trace-coded notice;
  tr-TR decimal parsing (`"48,820"` = 48.820), GA never PGA, Türkiye local provider times; dataset cache 60 s / 30 s,
  failed provider skipped 30 s, single flight; `/bot status` line; `/modules` gate (off by default).
- **IMPLEMENTED / TESTED_OFFLINE (2026-09-28)**: commands only in the currency channel `1242464361855848459` (elsewhere a
  private pointer, nothing fetched); daily combined card at 09:00 Europe/Istanbul, catch-up until 09:30, per-instrument
  source/time, outbox key `day:<Türkiye date>` (no second card after restart/overlap, no edit of a sent card), module gate,
  partial/none data handling, no pings.
- **Contract checked live (read-only GET, 2026-09-28)** through the module's own HTTP clients and parsers: Altınkaynak
  Currency/Gold, TCMB, Trunçgil — all fields present and parsed. Not a Discord observation.
- **NOT VERIFIED_LIVE**: the commands in Discord (`/altın` registration with the Turkish ı, public defer → card, card
  rendering in clients, the private wrong-channel pointer), the first real 09:00 card and its delivery. `/dolar`, `/euro`, `/altın` are in the manifest but not synced.

## TSQ Randomizer (new, 2026-09-28, branch `feat/randomizer-module`)

- Scope and rules: [randomizer/TSQ_RANDOMIZER.md](randomizer/TSQ_RANDOMIZER.md). Stateless: no table, no migration, no
  HTTP, no worker, no configuration. Off by default (`/modules enable randomizer`).
- **IMPLEMENTED / TESTED_OFFLINE**: `/zarat` (`1-20`, `2d6`, 1–20 dice, 2–10,000 sides), `/randomsayi` (inclusive, default
  minimum 1, ±1,000,000,000), `/sec` (comma and/or pipe, trim, case-insensitive dedupe, 2–25 options, 100 chars each),
  `/yazitura` (0 → Yazı, 1 → Tura); one `RandomNumberGenerator` source; public cards, private refusals, no pings,
  defused option text; `/help` and `/modules` through the manifest/registry.
- **NOT VERIFIED_LIVE**: everything in Discord (command registration incl. the `seçenekler` option name with `ç`, card
  rendering, private refusals). In the manifest but not synced.

## TSQ Saat Dönüştürücü (new, 2026-09-28, branch `feat/timezone-module`)

- Module `timezone`, command `/saat time:<HH:MM>`. Stateless: no table, no migration, no HTTP, no worker, no cache, no
  configuration, no new package. Off by default (`/modules enable timezone`).
- **IMPLEMENTED / TESTED_OFFLINE**: `H:MM`/`HH:MM` with `:` or `.` (ASCII digits, culture-independent), read as today in
  Europe/Istanbul; rows for Europe/Istanbul, Europe/London, America/New_York, America/Chicago, America/Los_Angeles through
  `TimeZoneInfo` (DST rules tested on both sides of the UK/US switches); "Previous day"/"Next day" marks; one Discord
  timestamp (`t` + `R`) for the single instant; public card, private refusal, no pings.
- **IMPLEMENTED / TESTED_OFFLINE** (source time zone): optional `timezone` option with autocomplete — `tr`, `uk`, `ny`,
  `chicago`, `la`, `utc` and aliases (`pdt`/`pst`/`pt`, `est`/`edt`/`et`, `cst`/`cdt`/`ct`, `gmt`/`bst`, …; case-insensitive,
  zones never offsets); "today" = the source zone's date; wall times skipped or repeated by a DST switch are refused.
  Without the option the card is unchanged.
- Linux: the runtime base image (`mcr.microsoft.com/dotnet/runtime:10.0`, Ubuntu Noble) installs `tzdata`; no Dockerfile
  change needed.
- **NOT VERIFIED_LIVE**: everything in Discord (registration, card rendering, the Linux zone data in the Railway
  container). In the manifest but not synced.

## TSQ Çekiliş (new, 2026-09-29, branch `feat/giveaway-module`)

- Module `giveaway`, command `/giveaway create|end|cancel|reroll` (Manage Server). Tables `giveaway` and
  `giveaway_winner` (additive migration `GiveawayModule`), worker `GiveawayWorker` (~30 s). Off by default
  (`/modules enable giveaway`).
- **IMPLEMENTED / TESTED_OFFLINE**: form validation (duration `30m`/`2h`/`1d 12h`/`30dk`/`2s`/`1g`, 1 min–30 days;
  1–10 winners; prize ≤ 100, description ≤ 500), channel permission precheck, card post incl. ambiguous-send reconciliation,
  bot 🎉; draw from all reaction pages (contract test with 237 users over 3 pages), bots/duplicates/removed reactions
  excluded, members who left skipped, unbiased partial Fisher–Yates; result card edit, winner-only ping via the outbox;
  end/cancel/reroll (earlier winners excluded, fallbacks), restart catch-up, draw-once under worker/end/cancel races,
  backoff when Discord cannot be read, orphaning of deleted cards, privacy export/delete.
- **NOT VERIFIED_LIVE**: everything in Discord (command registration, modal, 🎉 on the card, reading real reactions,
  edits, the winner ping). Add Reactions is a hard requirement (no giveaway, row or card without it); invite integer
  84992 → 85056 (117760 → 117824 with Attach Files) — the live bot role still needs Add Reactions granted. Disabling the
  module stops new giveaways only; started ones finish with their announcement. In the manifest but not synced; not
  merged, not deployed.

## TSQ Özet (new, 2026-09-29, branch `feat/summary-module`)

- Module `summary`, command `/ozetle` (no options; members with any one of `Summary:AllowedRoleIds` — six role ids by
  default — checked at run time before anything is read). Stateless: no table, no migration, no background job, no
  message listener or cache (gateway Identify stays Guilds). Off by default (`/modules enable summary`).
- Gates (2026-09-29, branch `feat/summary-role-gate-and-new-messages`): role gate (any-of, names from the guild cache,
  defused, deleted role shown by id); after TSQ Bot's own earlier summary (its user id + exact title) at least 100 new
  member messages, found by a newest → oldest scan of at most 10 pages that fails closed when inconclusive (first summary:
  MinMessages); channel/thread cooldown 120 s after a posted summary only (AI or post failure: 10 s). Deployed (#33),
  NOT_VERIFIED live.
- Spoilers (branch `feat/summary-spoilers`): `||…||` → `<spoiler>…</spoiler>` in the transcript (all spans, literal tags
  defused, closed when a long message is cut); prompt rules (only inside `||…||` with a non-revealing
  `**Spoiler (konu):**` label, `konu belirtilmemiş` fallback, no leak into Ana konu / headings / plans / atmosphere, no mixing
  of topics, no invented spoilers); output converts `<spoiler>`/`\|\|` to native `||` and closes an unclosed one; the
  2000-char split never cuts inside a spoiler (an oversized one is closed and reopened). TESTED_OFFLINE; model compliance
  NOT_VERIFIED (no live inference). Deployed (#35); first live run after it mentioned spoilers only generically (no `||`).
- Display names (branch `fix/summary-display-names`): live summaries had become over-anonymous ("bir kullanıcı …") although
  the transcript carries server display names; the prompt rule "names only when really needed" is replaced by: use the
  display name when a view, question, joke, experience, plan or action belongs to one person; no "bir kullanıcı" when the
  name is known; at most 2–3 names per bullet; plain names from the transcript only; attribution and spoiler rules
  unchanged. TESTED_OFFLINE.
- One summary = one AI request: OpenCode Go `POST /zen/go/v1/chat/completions`, model `deepseek-v4.1-flash` (API id
  verified from the live `/models` list), `thinking: disabled` **without** `reasoning_effort`, temperature 0.3, top_p 0.9,
  `max_tokens` 1200, no tools, 25 s timeout, **no retry, no fallback model, no second pass**. Fresh random
  `x-opencode-session` per summary; honest User-Agent `TSQBot/<version> SummaryModule`. Key only from `OPENCODE_GO_API_KEY` (redacted); without it `/ozetle`
  says "not configured" and nothing else is affected.
- **IMPLEMENTED / TESTED_OFFLINE**: member-only transcript (bots incl. earlier summaries, webhooks, system events out),
  oldest → newest, newest 100, mention/role/channel/emoji/timestamp normalization, links → `[link: host]`, attachment and
  sticker placeholders, 1500-char message cap and 40k transcript cap (newest kept), delimiter/fake-line injection defused;
  fixed system prompt with untrusted-data and attribution rules; deterministic clean-up (fences, preamble, exact
  `# Son Mesajların Özeti`, `##` headings, defused `@everyone`/`@here`) and 2000-char split at section/bullet/line/space;
  zero AI requests on every refusal (config, channel type, member/bot permissions, too few messages, withheld content,
  Discord read failure); cooldowns (30 s member / 60 s channel after a produced summary, 10 s after a failed request),
  one run per channel, at most two bot-wide without a queue; 400/401/403/429/5xx/network/timeout each after exactly one
  request; logs with ids, counts, tokens, latency and outcome only.
- **Observed live (2026-09-29, #29 deployed, `/ozetle` synced)**: the command runs; 100 real member messages were read
  through Message Content; OpenCode Go accepted the bot's request (HTTP 200, honest User-Agent). That first run returned no
  text — all 900 `max_tokens` went to hidden reasoning (`finish_reason: length`) — and was answered privately with nothing
  posted, as designed. With `max_tokens` 2500 (#30) the second live run spent all 2500 on reasoning too. Two diagnostic
  requests with the synthetic A/B transcript: the production request reasoned 2427 tokens and was cut off; the same
  request with `thinking: disabled` used 0 reasoning, ~640 answer tokens, ~7 s, correct format. Hence thinking disabled
  and `max_tokens` 1200 (#31).
- **VERIFIED_LIVE (2026-09-29, #31)**: a produced summary posted publicly in the channel (owner screenshot + log
  `inference ok … finish_reason=stop latency_ms=3588`, `posted parts=1`): exact title, `##` sections incl. the optional
  plans section, attribution wording.
- **Intermittent with #31**: the next run in another channel spent all 1200 tokens on reasoning (thinking disabled +
  `reasoning_effort: low` together); a diagnostic with a compact prompt did the same at 2000. DeepSeek documents
  `reasoning_effort` as a thinking-mode setting, so the pair is contradictory. Last diagnostic (synthetic transcript, same
  production prompt/settings, `thinking: disabled` alone): 0 reasoning, 757 tokens, 8.4 s, `stop`, complete format. Hence
  `reasoning_effort` is no longer sent with thinking disabled. VERIFIED_LIVE with #32 in the channel that had failed
  before (reasoning_tokens=0, 444 tokens, 5.4 s, posted).
- **NOT VERIFIED_LIVE**: the 2000-character split, thread handling, the role gate, the 100-message gate and the 120 s
  channel cooldown.

## TSQ Öngörü (new, 2026-09-29, branch `feat/predictions`)

- Module `predictions`, command group `/ongoru` (no default member permissions; every subcommand authorizes itself):
  `yarat` (predictions channel), `cuzdan`, `gunluk`, `tahminlerim`, `liderlik`, `turnuva durum|bitir` (commands channel;
  `bitir` Administrator/owner only). Management lives on the card: 🎯 Tahmin Yap, 🔒 Kilitle, ✅ Sonuçlandır,
  ↩️ İptal / İade (creator with the role, Administrator or owner; checked server-side on every click). No coin reset
  command; `/privacy delete` keeps the game records. Tables `prediction_tournament`, `prediction_wallet`, `prediction`,
  `prediction_outcome`, `prediction_entry`, `prediction_ledger`, `prediction_daily_claim`, `prediction_standing` (additive
  migration `PredictionsModule`), worker `PredictionWorker` (~10 s). Off by default (`/modules enable predictions`).
  Shared change: `OutgoingMessage.Select` (optional single-choice string select, omitted from stored payloads when absent).
- **IMPLEMENTED / TESTED_OFFLINE**: form parsing (every field and line, 2–25 outcomes, odds 1.01–1000.00, default odds,
  Türkiye lock time), private preview, single card per draft (double/parallel publish, ambiguous send reconciliation,
  abandoned uncertain posts), exact channel and role gates with no side effects, atomic entries (one per member and
  prediction, no overspending under parallel confirmations, check inside the write lock), exact deadline, integer payout
  math (rounding down, overflow), settle/cancel/no-winner, settle-vs-cancel race, daily reward (bounds, parallel claims,
  Türkiye midnight, no second claim after a tournament reset), tournament close (blocking, two admins, stale and expired
  confirmations, frozen podium, fresh 1000 wallets, announcement retry from the snapshot), leaderboards (tie order,
  isolation, eligibility: entered or created in the active tournament), card-button authorization (creator lost role,
  wrong message/channel, unauthorized clicks without side effects), parallel lock/settle/cancel, no coin reset path,
  deleted cards (replacement management card), failing cards, restart, module disable/enable, privacy export/delete,
  health lines.
- **NOT VERIFIED_LIVE**: everything in Discord (command registration, the modal with a select, the card buttons, edits,
  the announcement). Not merged, not deployed, not synced.

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
