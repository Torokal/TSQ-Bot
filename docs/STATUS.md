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

## TSQ Quote (2026-09-27, PR #14, live)

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
- **Message Content (owner decision 2026-09-27)**: the application's Message Content privileged access is on (Developer
  Portal toggle by the owner). Discord documents it as not tied to any gateway event (REST content fields), so the gateway
  Identify stays `Guilds`; no message events, no cache, no listener.
- **Production**: PR #14 merged (`b1a8084`), Railway deployment healthy (single startup, no migration, manifest OK),
  `/quote` synced to the main guild (Create only; every other command Unchanged, re-run dry-run zero diff), module
  enabled by the owner in Discord. Rollback: `/modules disable quote`.
- **VERIFIED_LIVE (2026-09-27)**: a normal member's message that does not mention the bot, quoted by id in the same
  channel: text delivered (Message Content over REST), Turkish characters and wrapping correct, greyscale server avatar
  fading into black, display name + `@username` correct, public `quote.png` follow-up, no pings (owner screenshot).
  Railway log held only `Quote resolved guild=… channel=… message=…` — no text, names or avatar url.
- **Owner-reported working (2026-09-30)**: the other paths (`channel:`, message links, refusals). Not observed in the
  logs here: Railway keeps logs only since the latest deployment and they hold no Quote lines.
- **Apps → Quote IMPLEMENTED / TESTED_OFFLINE** (`feat/quote-context-menu`): MESSAGE command `Quote` (type 3) next to
  `/quote`; the message comes from the interaction payload (REST message reads: zero, test-pinned) and runs the same text
  rules, card builder and renderer; checks: payload message in this channel, bot View Channel + Attach Files, member View
  Channel + Read Message History; works in private threads and age-restricted channels (the card stays where the message
  is); no Message Content dependency. The manifest/sync pipeline now also handles MESSAGE commands (dry-run offline:
  Create `Quote`, everything else Unchanged, no delete). **Not synced, NOT_VERIFIED_LIVE.**

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
- **`/çevir` IMPLEMENTED / TESTED_OFFLINE (2026-09-30, branch `feat/currency-convert`)**: TRY ↔ USD, TRY ↔ EUR, TRY ↔ gram gold
  through the same `MarketQuoteService`; asset → lira uses the provider's buy price, lira → asset its sell price; decimal
  math, display-only rounding; currency channel only; refusals private without a fetch; public card with source/fallback/
  stale labels. NOT synced, NOT VERIFIED_LIVE.

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
- **IMPLEMENTED / TESTED_OFFLINE** (V2, branch `feat/timezone-date-to-now`): optional `date` (`DD.MM[.YYYY]`, year = source
  zone's current year, DST gap/overlap checks kept), optional single target `to` (shared alias catalog, Asia/Tokyo added as
  source/target, not on the default board), `time:now` (clock instant; refused with `date`), raw copyable timestamp field.
  NOT VERIFIED_LIVE, not synced.
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
  unchanged. Deployed (#36); natural names VERIFIED_LIVE 2026-09-29.
- Grounded generation mode (2026-10-03, branch `fix/summary-grounded-context`): `Summary:GenerationMode` Legacy (default,
  unchanged request and output) | Grounded. Grounded sends one JSON record per message (request-local reference, display
  name, the real reply link, bounded one-level reply context from data already read, cut/context flags), asks for one JSON
  answer whose visible texts carry verbatim quotes, checks structure/sources/quotes/spoiler provenance in code and renders
  the same Markdown; a refused answer is not published, repaired, retried or replaced by Legacy. One inference per run in
  both modes; gates, cooldowns, logging and privacy unchanged. TESTED_OFFLINE; limited real-model comparison on two
  synthetic snapshots (4 requests): no critical meaning error, source error, spoiler leak or truncation in the two Grounded
  runs (Legacy merged two separate conversations once). The checks are structural: they do not prove the model's reading
  of a source is right. Output used ~76% of the 2000-token cap.
- **Current state (2026-10-03): production runs Legacy; the Grounded code is merged but switched off**
  (`TOROSQUAD_Summary__GenerationMode=Legacy`). Grounded was enabled after #54 and rolled back the same day: of three live
  runs on real 100-message windows, one was published (output 1661 of 2000 tokens) and two were cut off at the 2000-token
  cap (`finish_reason=length`, `validation=Truncated`) — refused as designed, nothing posted, no retry, no Legacy fallback.
  The two small synthetic snapshots (32–36 messages) had not been representative of real output size. The one published
  live result shows the path works end to end; it is not evidence of general reliability, and its meaning was not checked
  against the source. Going back to Legacy restored the command's availability — it does not mean the meaning-accuracy
  improvement is done: Legacy still has the errors that motivated Grounded (merged conversations, question → event).
- Compact grounded contract (2026-10-03, branch `fix/summary-grounded-compact`): contract `"v":2` with short keys (`t`,
  `e` as `[reference, quote]` pairs, optional `s` only on a spoiler claim, no null fields); the earlier shape is refused. Volume
  bound in the prompt and in the reader: claims of all points plus plans ≤ 8, ≤ 2 claims per point, ≤ 3 plans, ≤ 3 quotes per
  text (one is the norm), quote ≥ 3 characters; an answer over the bound is refused as a whole (`Limit`), never trimmed.
  Legacy path, model settings, `GroundedMaxOutputTokens` 2000 and all gates unchanged. TESTED_OFFLINE. Offline rewrite of the
  two earlier synthetic v1 answers: −21 % / −23 % characters (not measured tokens), same visible text and source matches.
  **Model check (2 requests, synthetic 100-message fixtures, local harness): both answers were valid v2 JSON with
  `finish_reason=stop` but over the volume bound (16 and 10 claims; 1858 and 1341 output tokens) and were refused — NOT
  PASSED.** The second request used a strengthened volume rule; it reduced the volume but not below the bound. Grounded
  stays off in production; re-enabling needs the owner's approval. See `docs/summary/TSQ_SUMMARY.md`.
- Flat grounded contract (2026-10-03, PR #56 merged `ac55c45`, deployed; production stays Legacy): contract `"v":3` — each point carries its own
  topic, text, evidence pairs and optional spoiler topic (no nested claims); v1/v2 answers are refused. Source safety and the
  display target are separated: up to 12 candidate points and 4 candidate plans are ALL checked (sources, verbatim quotes,
  context-only, spoiler provenance, technical references); one error still refuses the whole answer, also in an item that
  would not be shown. If everything checks out, the first 6 points and 2 plans are shown as whole items in the model's
  order — a 7th valid point is no longer a `Limit` failure. Only exact copies (same text, evidence and spoiler nature) are
  shown once; nothing is rewritten, shortened or fuzzily merged. Log line gains candidate/shown counts. Legacy path, model
  settings, `GroundedMaxOutputTokens` 2000 and all gates unchanged. TESTED_OFFLINE (3223 tests ×3).
  **Model check with one frozen prompt (2 requests, the same two synthetic 100-message fixtures, local harness): both
  answers valid, `finish_reason=stop`, `validation=None` — A 6162/1371 tokens (7 candidate points → 6 shown), B 6075/1313
  (6 → 6); all 54 quotes verbatim.** Against the answer keys: no inverted meaning and no wrong attribution in these two
  examples; **one certainty error** (B wrote an unconfirmed item — a member bringing a mouse — as settled; verbatim quotes
  cannot catch that); spoiler display was NOT verified with a real model answer because the spoiler topics were not
  selected (no leak into open text either). Lower-priority defects: plans repeat information from points, 2–3 quotes per
  item, output above the 1200-token optimisation target (about a third of the cap left).
  **Targeted check C (1 request after the merge, same prompt and contract; new synthetic fixture with an invented series as
  a main topic, an unconfirmed task and an explicit correction): 6284/1510 tokens, `stop`, `validation=None`, 30 quotes
  verbatim, 8 candidate points → 6 shown. Operation and meaning passed (the unconfirmed task was reported as not certain;
  the correction was kept). Spoiler display did NOT pass: the model wrote two labelled spoiler points but ranked them last
  (not shown) and their text did not summarise the hidden content; no leak into open text.** 490 tokens (24.5 %) of the
  cap were left. Spoiler display with a real model answer is still unverified.
  **Narrow spoiler fix (branch `fix/summary-grounded-spoiler-selection`, NOT merged): prompt rules (a spoiler mark does not
  mean "skip"; the hidden event is summarised in its own point; an empty meta sentence is not enough; importance follows
  the conversation) and one selection rule (if none of the first 6 points quotes a hidden span, the first later verified
  point that does replaces the last shown one; only verified evidence earns it). TESTED_OFFLINE (3233 tests ×3). Model
  re-check on fixture C with the frozen candidate (1 of 2 allowed requests): 6713/1379 tokens, `stop`, but
  `validation=Limit` (4 quotes on four texts; the limit is 3) and NO spoiler point at all — NOT PASSED; B was not run.**
  No leak into open text. The selection rule could not be exercised by real model output.
  **Diagnosis (2026-10-04, no code change): fixture C's input is intact (hidden spans present verbatim, not context, not cut);
  the refused DeepSeek answer had 31/31 verbatim quotes — the refusal was the 3-quote limit only — and no spoiler point. One
  GLM-5.3-Flash request with the same frozen prompt (no `thinking` field, `reasoning_effort: low`): valid, 5965/1244 tokens,
  but no spoiler point either.**
  **Required spoiler coverage + five-quote ceiling (same branch, PR #57 draft, NOT merged; contract v3 unchanged): records with
  a hidden part carry `"sp":true`; the window records among them are listed after the records block as required spoiler
  sources; the reader refuses an answer in which a required source is not quoted from its hidden part by a spoiler point
  (`MissingRequiredSpoiler`), reserves the fewest covering points (max 3) among the 6 shown, and refuses coverage that needs
  more (`SpoilerPointLimit`). `MaxEvidence` is a safety ceiling of 5 (prompt target unchanged: 1, or 2–3). TESTED_OFFLINE
  (3249 tests ×3). Model check on fixture C, one frozen candidate, 2 requests: DeepSeek 6876/957 tokens — no spoiler point;
  GLM 6106/1543 (145 reasoning) — summarised the hidden events correctly but under a new top-level field outside the
  contract; both refused with `MissingRequiredSpoiler` — NOT PASSED.** The coverage check worked as a guard; it did not make
  either model write a contract-conforming spoiler point. Spoiler display with a real, conforming model answer is still
  unverified.
- Grounded contract v4 (2026-10-04, branch `fix/summary-grounded-v4-spoilers`, draft PR, NOT merged; supersedes the v3
  spoiler approach of PR #57 if accepted): hidden information is a top-level `spoilers` array (always present, `[]` when
  nothing is hidden) instead of specially labelled points; the `s` field is gone; v1/v2/v3 answers are refused. `main`,
  `points`, `plans`, `atmosphere` are open — a quote from a hidden span there refuses the answer; hidden evidence is valid
  only inside `spoilers`. Every required spoiler source must be quoted from its hidden part by a `spoilers` item
  (`MissingRequiredSpoiler`). Rendering: `## Spoilerlar` with `- **topic:** ||text||`, only when there is an item. Points
  and spoilers do not compete for places; the v3 reservation rules and `SpoilerPointLimit` are removed. Bounds: 8 candidate
  points (6 shown), 4 spoiler items, 4 candidate plans (2 shown), 5 quotes per text. Input metadata (`"sp":true`, required
  sources line) kept. Legacy path, model settings, `GroundedMaxOutputTokens` 2000 and all gates unchanged. TESTED_OFFLINE
  (3242 tests ×3). **Model check on fixture C, one frozen candidate, 2 requests: GLM-5.3-Flash PASSED the agreed criteria —
  6138/1311 tokens, `stop`, 16.1 s, `validation=None`, 3/3 required sources covered, the hidden events really summarised and
  rendered as `||…||` under `## Spoilerlar`, safe topic, no leak, unconfirmed task kept unconfirmed, correction kept.
  DeepSeek V4.1 Flash: the request timed out at 25 s — no answer to evaluate (not retried).** Seen in the GLM answer: one
  probably wrong count ("7 people"), an inferred "CS", plans repeating points, 3 quotes on most texts, summary over 2000
  characters (two messages). One synthetic success is not a guarantee of accuracy; NOT VERIFIED_LIVE. Grounded stays off in
  production; which model, a short supervised live window and closing PR #57 are the owner's decisions.
- Grounded two-stage pipeline (2026-10-04, same draft PR, NOT merged): at most TWO inferences per Grounded run. Generator
  `GroundedGeneratorModel` (default `glm-5.3-flash`, no `thinking` object, `reasoning_effort: low`) writes the v4 draft →
  the reader → only an accepted draft goes, with the same records and required spoiler sources, to the reviewer
  `GroundedReviewerModel` (default `deepseek-v4.1-flash`, `thinking: disabled`) for a factual review (inverted meaning,
  later corrections/updates, changed counts, question/suggestion/opinion as fact, speaker mix-ups, anything the records do
  not state explicitly) → the reviewed full v4 answer replaces the draft and passes the same reader from scratch → render.
  Any failure of either stage = no public summary; no retry, no third request, no legacy fallback; the success cooldown
  starts only after the final post. Both stages are logged separately (model, tokens as reported or `unknown`, latency,
  total), never content. In Grounded mode the same transient transcript is therefore sent to the provider a second time
  (documented); Legacy stays one request and production stays Legacy. TESTED_OFFLINE (3268 tests ×3). **One pipeline run
  on fixture C: generator 6138/1212 tokens, 15.6 s, `validation=None`; reviewer 9294/1396 tokens, 7.4 s,
  `validation=None`; total 23.0 s. The reviewer removed the game name that the records never state and tightened one
  sentence; spoiler section and 3/3 coverage kept; unconfirmed task still unconfirmed; corrections kept — PASSED the agreed
  criteria.** Not exercised: the outdated-count error did not occur in this draft. Not improved: plans still repeat points.
  One synthetic run; NOT VERIFIED_LIVE.
  **Fixtures A and B through the frozen pipeline (4 requests, no retry, no timeout): all four stages `stop` and
  `validation=None`; A 5996/932 + 8841/1087 tokens, 12.5 s total; B 5946/1284 + 9177/1477 tokens, 13.3 s total. B: clean on
  every critical check — two spoiler items for two productions, 2/2 coverage, real content hidden, no leak; unconfirmed
  things kept unconfirmed; rumour, disagreement and both corrections right. A: no inversion, wrong person or stale state, but
  two low-severity findings in the critical classes that the reviewer left untouched — the game name attached to the
  training although the records name it only for the crash, and a hedged cause ("seems so") written as established.** The
  reviewer changed nothing in A and removed a supported detail ("LAN") in B. Gate: operation and B met; A not fully under a
  strict reading — merge NOT recommended by the agent; owner's decision.
  **Reviewer rules narrowed (reviewer prompt only: support must be local to the event, hedges and causal strength are kept,
  supported details are not removed; 3273 tests ×3) — the one re-run on fixture A could not evaluate them: the GLM generator
  request TIMED OUT at 25 s, the reviewer was not called, nothing was retried.** The new rules are therefore untested by a
  model. GLM latency on these fixtures ranged from 5.8 s to over 25 s (one timeout in seven requests).
  **Final repeat on fixture A with the frozen candidate (2 requests): generator 5996/867 tokens, 5.5 s; reviewer 9388/1055
  tokens, 6.7 s; both `stop` and `validation=None`. The draft again carried both target errors and the reviewer corrected
  both — the game name was detached from the training and kept only for the crash it is stated for; the hedged cause is
  hedged again — and it also restored a dropped step, removed two more unsupported details and added a supported one. No
  regression against the answer key.** Offline gate met: PR #58 is an offline merge candidate (agent's assessment); it is
  NOT VERIFIED_LIVE, not an approval to enable Grounded, and the generator's latency risk (one 25 s timeout in eight
  requests) remains open. Two synthetic examples are not a guarantee of accuracy or of never being cut off; NOT VERIFIED_LIVE.
- **Current state (2026-10-04): PR #58 merged (`980afd6`) and deployed — DEPLOYED_LEGACY_GROUNDED_AVAILABLE_OFF.** Production
  runs `TOROSQUAD_Summary__GenerationMode=Legacy`; the grounded pipeline is in the build but not used. Merging it is not a
  live approval of Grounded.
- Grounded canary (2026-10-04, branch `feat/summary-grounded-canary`): `Summary:GroundedCanaryChannelIds` (default empty =
  no canary). Mode per run, decided once from the interaction's own channel or thread id: global `Grounded` → Grounded
  everywhere; else an EXACT id match in the list → Grounded; else Legacy. No wildcard, no parent/category/guild
  inheritance; a thread counts by its own id. Stage-specific deadlines: `GroundedGeneratorTimeoutSeconds` 35 (the generator
  request took 5 to 25+ s in the checks and timed out once at 25 s), `GroundedReviewerTimeoutSeconds` 25; the legacy
  request keeps `RequestTimeoutSeconds` 25. Still one call per stage, at most two per grounded run, one per legacy run.
  Logs: `mode_source` (Legacy|Global|Canary) and `failed_stage` (none|generator|reviewer). `/bot status` shows the global
  mode, the canary channel COUNT and the two grounded models. No prompt or contract change, so no model check was run.
  TESTED_OFFLINE. The canary list stays EMPTY in production until the owner names a channel or thread id; turning a canary
  off is a manual configuration change and redeploy, not an automatic rollback.
- **Live Grounded evaluation (2026-10-04) — GROUNDED_LIVE_EVALUATION_STOPPED, PRODUCTION_LEGACY.** By the owner's decision
  Grounded was enabled globally in production twice (`mode_source=Global`, canary list empty). Three real 100-message
  attempts in total, 0 public Grounded summaries, 3 generator-stage failures: (1) generator timeout at 35.005 s; (2) the
  generator answered in 31.267 s (HTTP 200, `finish_reason=stop`, 5983 input / 1285 output / 0 reasoning tokens) and the
  strict reader refused the draft with `validation=Limit`; (3) second window: the generator answered in 5.344 s (HTTP 200,
  `finish_reason=stop`, 5983 / 1133 / 0 tokens, 3232 answer characters) and the reader refused the draft with
  `validation=NotJson`. The reviewer was not called in any live attempt. The first window was closed after two attempts
  (stop criterion: 2 operational failures within the first 10 real attempts), the second after one, both by rolling back
  to `TOROSQUAD_Summary__GenerationMode=Legacy` by hand. Two more commands in the first window ended at the
  100-new-message gate without an AI request and are not attempts. UNKNOWN and not recoverable, because answer content is
  never logged and the diagnostics came after each attempt: which `Limit` bound the second draft broke, and why the third
  draft was not one JSON object. The live attempts differ from the synthetic checks: the generator took 31-35+ s in the
  first window (5-22 s on the fixtures) and neither a `Limit` nor a `NotJson` refusal had occurred on contract v4.
  NOT VERIFIED_LIVE.
- Limit diagnostics (2026-10-04, branch `feat/summary-grounded-limit-reason`): a `Limit` refusal now names the first
  safety bound the reader met — `limit_reason` AnswerChars | CandidatePoints | CandidateSpoilers | CandidatePlans |
  EvidencePerText | TextChars | TopicChars | QuoteChars | RenderedChars (None for every other outcome) — and
  `limit_value`, the size that broke it. The validation line also carries `raw_answer_chars`, `candidate_spoiler_count`
  and `rendered_chars`; a number the reader did not reach is `unknown`, never a made-up 0 (`evidence_count` and the
  candidate counts of a refused answer used to be logged as 0). Names and numbers only, no content. No bound, prompt,
  model, contract or deadline changed; no model request was made. Merged and deployed (#60).
- NotJson diagnostics (2026-10-04, branch `feat/summary-grounded-not-json-reason`): a `NotJson` refusal now logs
  `not_json_reason` Envelope | CodeFence | MalformedJson | RootNotObject (None for every other outcome). Envelope: after
  the one supported enclosing fence the answer does not start with `{` and end with `}`; CodeFence: a leading fence in
  another shape; MalformedJson: brace to brace but not valid JSON; RootNotObject: valid JSON of another kind. Strictness
  is unchanged: no object is searched for inside the text, no text is cut away, nothing is repaired, the fence tolerance
  is the same, no repair request. An empty answer stays the provider client's own failure (EmptyOutput). The parser's
  message, the answer's first or last characters and a fence's language are not logged. No model request was made.
  TESTED_OFFLINE.
  Grounded stays off in production; re-enabling needs the owner's approval.
- One summary = one AI request in Legacy (production): OpenCode Go `POST /zen/go/v1/chat/completions`, model `deepseek-v4.1-flash` (API id
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

## TSQ Öngörü (2026-09-29, PR #37 merged, deployed, `/ongoru` synced; module off until enabled)

- Module `predictions`, command group `/ongoru` (no default member permissions; every subcommand authorizes itself):
  `yarat` (predictions channel), `cuzdan`, `gunluk`, `tahminlerim`, `liderlik`, `turnuva durum|bitir` (commands channel;
  `bitir` Administrator/owner only). Management lives on the card: 🎯 Tahmin Yap, 🔒 Kilitle, ✅ Sonuçlandır,
  ↩️ İptal / İade (creator with the role, Administrator or owner; checked server-side on every click). Entries: the form
  submit IS the entry (no second confirmation); while the prediction is open the member may ✏️ change outcome and stake
  (only the difference moves) or ↩️ withdraw (stake back; entry kept as `Withdrawn`). The public card no
  longer shows the tournament; each leaderboard shows at most 10. No coin reset command; `/privacy delete` keeps the game
  records. Migration `PredictionEntryLifecycle` (additive: `Revision`, `UpdatedAt` on `prediction_entry`). Tables `prediction_tournament`, `prediction_wallet`, `prediction`,
  `prediction_outcome`, `prediction_entry`, `prediction_ledger`, `prediction_daily_claim`, `prediction_standing` (additive
  migration `PredictionsModule`), worker `PredictionWorker` (~10 s). Off by default (`/modules enable predictions`).
  Shared change: `OutgoingMessage.Select` (optional single-choice string select, omitted from stored payloads when absent).
- **IMPLEMENTED / TESTED_OFFLINE**: form parsing (every field and line, 2–25 outcomes, odds 1.01–1000.00, default odds,
  Türkiye lock date and time in two fields, a date without a time and vice versa refused), private preview, single card per draft (double/parallel publish, ambiguous send reconciliation,
  abandoned uncertain posts), exact channel and role gates with no side effects, atomic entries (one per member and
  prediction, no overspending under parallel confirmations, check inside the write lock), exact deadline, integer payout
  math (rounding down, overflow), settle/cancel/no-winner, settle-vs-cancel race, daily reward (bounds, parallel claims,
  Türkiye midnight, no second claim after a tournament reset), tournament close (blocking, two admins, stale and expired
  confirmations, frozen podium, fresh 1000 wallets, announcement retry from the snapshot), leaderboards (tie order,
  isolation, eligibility), card-button authorization (creator lost role,
  wrong message/channel, unauthorized clicks without side effects), parallel lock/settle/cancel, no coin reset path,
  deleted cards (replacement management card), failing cards, restart, module disable/enable, privacy export/delete,
  health lines.
- Creation form (🔮 Öngörü Oluştur): Başlık, Seçenekler ve oranlar, Kilitlenme tarihi, Kilitlenme saati, Sonuç kuralı;
  short end-user hints, limits only in validation messages.
- **VERIFIED_LIVE (technical only)**: deploy with the additive migration and clean start, guild command registration of
  `/ongoru`. **NOT VERIFIED_LIVE**: every member interaction in Discord (both modals, the select, the card buttons, edits,
  the announcement).
- Terminal card retention + weekly leaderboard (PR #49, merged `3c86d63`, deployed 2026-10-02):
  settled/cancelled cards are removed from the channel 12 h after the settlement/cancellation commit (only the Discord
  message; history kept; never replaced), and the active tournament's leaderboard is posted once a week to the commands
  channel (default Sunday 20:00 Europe/Istanbul, 12 h catch-up, Top 10, nothing for an empty board) through the outbox;
  additive migration `PredictionsCardRetentionWeeklyBoard`. **IMPLEMENTED / TESTED_OFFLINE** (fake clock, real SQLite,
  fake Discord). Deployed: migration applied, clean start; the first cleanup pass closed two old cancelled cards whose
  messages were already gone (404). **NOT VERIFIED_LIVE**: a real card deletion, the first weekly post (first slot
  Sunday 04.10.2026 20:00 TR).
- Leaderboard V2 (PR #50, merged `5be2e07`, deployed 2026-10-02; command sync: nothing to change): only members with
  at least one settled own entry in the active tournament are ranked (creating, open/locked, withdrawn or refunded entries
  and daily rewards never count); coins = live wealth (available + principal in unsettled entries); Top 10 per board;
  `/ongoru liderlik` adds a private note with the asking member's own rank on each board where they are outside the Top 10
  (counted in SQLite with the board orders) or "not ranked yet"; the weekly post uses the same boards without personal notes.
  Ending a tournament is a separate rule (nothing unresolved, no pending stake, some activity): an empty leaderboard
  (e.g. every prediction cancelled) never blocks it; participants = members with an entry. No schema change.
  **IMPLEMENTED / TESTED_OFFLINE**, deployed with a clean start. **NOT VERIFIED_LIVE**: the private personal-rank note in
  Discord.
- Settlement announcement + settling with the creator role (PR #52, merged `5486266`, deployed 2026-10-02; command sync:
  nothing to change): any holder of the creator role (or Administrator/owner) may settle any prediction, automatic ones included; lock
  and cancel are unchanged. A settled prediction posts a public result announcement to the commands channel (winners'
  net gains, total payout, pings only for the listed winners, "nobody won", up to 5 parts of 15 winners), staged with the
  settlement in one transaction. No schema change. **IMPLEMENTED / TESTED_OFFLINE**, deployed with a clean start.
  **NOT VERIFIED_LIVE**: a real announcement in Discord (winner pings, multi-part messages).

## TSQ Öngörü automatic football (PR #40 merged `82b98d2`, deployed 2026-09-30; `Mode=Live` in production)

- Opens fixed-odds predictions for Galatasaray / Fenerbahçe / Beşiktaş matches of five allow-listed competitions on the
  match day (09:00 Türkiye time, or 2 h before an early kickoff), locks 2 min before the planned kickoff; results stay
  manual. The Odds API (h2h, eu, decimal; one call per competition per due batch), modes Disabled (default) / Observe /
  Live; tables `prediction_auto_event`, `prediction_auto_provider`, column `prediction.Origin` (migration
  `PredictionsAutoFootball`, additive). CLI `predictions football-check` (read-only). Docs: docs/predictions/AUTO_FOOTBALL.md.
- **IMPLEMENTED / TESTED_OFFLINE** (synthetic provider data, real SQLite): schedule and late start, club matching, derby,
  odds selection/conversion/freshness, bounded attempts across restarts, quota reserve/unknown usage/429/401/5xx, one
  match = one prediction (two processes, restart, new tournament, cancelled card), tournament race, uncertain delivery,
  late/stopped post, schedule change and vanished match after publishing, admin-only management, no wallet/eligibility,
  key redaction.
- **PROVIDER_VERIFIED_READ_ONLY** (2026-09-30, local read-only check, 3 credits): catalog, events and h2h odds for
  Galatasaray / Fenerbahçe / Beşiktaş in Süper Lig, Champions League and Europa League; exact club names, correct
  home/away, complete fresh 1-X-2 sets. NOT_OBSERVED: h2h regular-time semantics, live/postponed data, error responses.
  (At that check nothing was deployed yet.)
- Türkiye men's senior national team added as the fourth target (national competitions: Nations League, Euro
  qualification, Euro, World Cup qualifiers Europe, World Cup; outrights and friendlies excluded). Observed read-only with
  the rotated key (2026-09-30, 1 credit): Nations League in season, provider name "Turkey", Belgium – Turkey on
  02.10.2026 21:45 TR with a complete fresh h2h set.
- Narrow market-rule approval: only `pinnacle` (Pinnacle betting rules, Soccer rule 1: regular 90 minutes plus added
  time), only The Odds API's pre-match h2h 1-X-2 of men's senior teams in the allow-listed competitions; 1xBet and others
  stay unapproved and are never a fallback. Read-only re-check (2026-09-30, 1 credit): Belgium – Turkey Pinnacle set
  ACCEPTED, card preview "Belçika - Türkiye". Italy – Türkiye (05.10, official fixture) is not yet listed by the provider
  (COVERAGE_INCOMPLETE). Observations refused only for the missing rule are judged again (attempts kept, old odds not
  reused).
- Production (2026-09-30): PR #40 merged and deployed, Railway variables `Mode=Live` and the API key (value never shown);
  first discovery logged (catalog + in-season lists, free calls). PR #48 orders the publishing sync batch.
- First automatic card (log, 2026-10-02 06:00Z = 09:00 TR): one paid odds call (`x-requests-remaining=499` after the
  monthly renewal), `auto_football_published` prediction #4 (Belgium – Türkiye, lock 21:43 TR). The card's content in
  Discord, entries on it and the manual settlement are **NOT VERIFIED_LIVE** (no owner observation yet).

## TSQ Haber — Aurora · HLTV (new, 2026-09-30, branch `feat/aurora-hltv-news`)

- **IMPLEMENTED / TESTED_OFFLINE:** official HLTV RSS reader (hardened XML, no redirects, bounded body, 304/403/429/5xx/
  timeout classification), Aurora matcher (team names, look-alike exclusions, current players in the headline, stale
  roster), Liquipedia roster sync, baseline, per-guild dedup by article id, silent edit on corrections, dry-run separation,
  pause/resume, channel change, 6 h / 3-card catch-up, retention with watermark, module gate, persisted backoff, atomic
  rounds, reconciliation of ambiguous sends, `/news-admin`, `news check` CLI.
- **Real source (local network, 2026-09-30):** feed read and parsed (10/10 items, ttl 60, no ETag/Last-Modified, no team
  tags); Liquipedia roster read (5 players). No Aurora item was in the feed, so matching is not verified on a real item.
- **NOT_VERIFIED:** Railway-network access, an RSS-specific usage permission in HLTV's terms, HLTV team id 11861, a live card.
- Off by default (`News:Mode=Off`); merge, deploy, command sync, channel and live posting await the owner's approval.

## TSQ Bot Updates — game update notifications (new, 2026-10-05, branch `feat/updates-module`)

- **IMPLEMENTED / TESTED_OFFLINE:** generic game-update module (game definition + provider + classifier), Steam news
  provider (fixed public endpoint, no key, no redirects, bounded and validated JSON, every failure class kept apart),
  conservative Counter-Strike 2 classifier, baseline, per-guild dedup by post id, silent edit on corrections, dry-run
  separation, mode-change windows (nothing seen under DryRun or published while Off is posted live), pause/resume,
  channel and game changes, 24 h / 3-card catch-up, retention with a publication-time watermark, module gate, persisted
  backoff, source cache lifetime, atomic rounds, `/tsq-admin modul:updates`, `updates check` CLI. Additive migration
  `UpdatesModule` (five `updates_*` tables).
- **Real source (local network, 2026-10-05):** Steam answer read and parsed (HTTP 200, 20/20 posts usable); classifier
  on the real list: 17 updates, 1 ambiguous, 2 not an update.
- **NOT_VERIFIED:** Railway-network access, a live Discord card (only a real update published after going live can show it).
- Off by default (`Updates:Mode=Off`, no channel, no followed game); merge, deploy, channel, game activation and live
  posting await the owner's approval. Rollout order: [updates/TSQ_UPDATES.md](updates/TSQ_UPDATES.md).

## TSQ Bot Updates — World of Warcraft: Forever source (new, 2026-10-05)

- **IMPLEMENTED / TESTED_OFFLINE:** second game of the Updates module through the unchanged pipeline — Blizzard forum
  provider (watched Development Notes thread + new Blizzard threads of the Forever category, Blizzard posts only by the
  forum's own tracked-post markers, robots-allowed JSON paths only), bounded HTML reader, sections and version/build,
  deterministic classifier (development notes, client update, patch notes, hotfix; known issues, maintenance, marketing,
  other WoW versions and player posts never post), card excerpt, provider-specific poll interval, bounded following of
  verified update threads (7 days after the last verified update, at most 5 threads, derived from the stored posts), and
  an explicit source move when the forum category changes (posts published before the move are history, later ones are
  new — no second first-answer baseline). Reliability hardening: an unreadable followed or new thread no longer fails the
  round (HTTP 429 still does), the list is read back to the last complete round after an outage (capped by
  `CatchUpHours`, bounded by `MaxListPages`), Blizzard posts between a long thread's first posts and its newest one are
  read within three requests per thread, the watched thread takes no follow place, and no valid setting allows more than
  50 forum requests in a round. Additive migration `UpdatesItemHighlights` (one nullable column).
  Counter-Strike 2 behaviour and card are unchanged; the game operations of `/tsq-admin modul:updates` now always open
  the game picker.
- **Real source (local network, 2026-10-05, read-only):** forum answers read and parsed; `updates check --game
  wow-forever` classifies the two Development Notes posts as updates.
- **Not implemented:** Battle.net/CDN build watcher (a build number alone is not an update; follow-up).
- **NOT_VERIFIED live:** the game is registered but no guild follows it until an admin enables it
  (`/tsq-admin modul:updates islem:game-enable`); until then the forum is never requested. Forum access from the hosting
  network and a real card are not verified.

## One admin command: `/tsq-admin` (2026-09-30)

- PR #46 (merged, deployed, registered 2026-09-30) put the seven `/<module>-admin` commands under one root as subcommand
  groups; the menu still listed every path. Branch `fix/tsq-admin-flat` replaces that with ONE flat command without
  subcommands: `/tsq-admin modul:<module> islem:<operation> [kanal] [uye] [rol] [tarih]`, module and operation autocompleted,
  multi-field settings in private forms. 47 operations, same services and server-side authorization; no data/config migration.
  Old → new table: [COMMANDS_AND_PERMISSIONS.md](COMMANDS_AND_PERMISSIONS.md).
- **IMPLEMENTED / TESTED_OFFLINE** (flat): payload shape (0 subcommands, 0 groups), completeness, autocomplete, validation,
  operations run against the real services, form safety, sync plan (one update of the existing `/tsq-admin`).
- **NOT VERIFIED_LIVE** (flat): not pushed, deployed or synced; the Discord menu, autocomplete and forms in the real client are
  unchecked. The seven old roots are still registered (cleanup pending, separate approval). Entries above that name
  `/<module>-admin` describe the command as it was then.

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
