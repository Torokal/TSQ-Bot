# TSQ Bot

A modular, self-hostable Discord bot. Its first module tracks **Counter-Strike 2 esports**: it posts compact match
cards (reminder, match started, result and schedule changes) into a server channel and answers esports slash commands.
A separate **Formula 1** module posts confirmed session starts, results (with in-place corrections) and championship
standings ([docs/FORMULA1.md](docs/FORMULA1.md)). A **Volleyball** module follows only Türkiye's women's senior national team
("Filenin Sultanları") ([docs/volleyball/VOLLEYBALL.md](docs/volleyball/VOLLEYBALL.md)). **TSQ Live** announces the configured
creators' Twitch/Kick streams ([docs/live/TSQ_LIVE.md](docs/live/TSQ_LIVE.md)). **TSQ LFG — Oyuncu Bul** lets members
open quick group listings for any game with `/ekip` ([docs/lfg/TSQ_LFG.md](docs/lfg/TSQ_LFG.md)). **TSQ Quote** turns a
message into a black-and-white quote card with `/quote` ([docs/quote/TSQ_QUOTE.md](docs/quote/TSQ_QUOTE.md)). **TSQ Doğum
Günü** celebrates members' birthdays (day and month only) with one message and a role for the day
([docs/birthday/TSQ_BIRTHDAY.md](docs/birthday/TSQ_BIRTHDAY.md)). **TSQ Döviz & Altın** shows the current US dollar, euro
and gram gold buy/sell prices with `/dolar`, `/euro` and `/altın` ([docs/currency/TSQ_CURRENCY.md](docs/currency/TSQ_CURRENCY.md)).
**TSQ Randomizer** rolls dice, picks a number, picks one of your options and flips a coin with `/zarat`, `/randomsayi`,
`/sec` and `/yazitura` ([docs/randomizer/TSQ_RANDOMIZER.md](docs/randomizer/TSQ_RANDOMIZER.md)). **TSQ Çekiliş** runs reaction
giveaways: `/giveaway create` opens a form, members enter with 🎉, winners are drawn automatically
([docs/giveaway/TSQ_GIVEAWAY.md](docs/giveaway/TSQ_GIVEAWAY.md)). **TSQ Özet** summarizes the latest messages of a channel
in Turkish with `/ozetle` ([docs/summary/TSQ_SUMMARY.md](docs/summary/TSQ_SUMMARY.md)).

TSQ Bot is an independent project. The esports module is inspired by the user experience of the discontinued
**BOT Greg** and reuses adapted portions of the open-source
[BOT-Greg-v2_API](https://github.com/julius-gmeinder/BOT-Greg-v2_API) (AGPL-3.0). It is **not** an official
continuation of BOT Greg, is not affiliated with or endorsed by its developer, and uses none of its branding.
See [docs/PROVENANCE.md](docs/PROVENANCE.md).

**Status:** early but running in production for a single Discord server. There is no public invite and there are no
global commands yet. See [docs/STATUS.md](docs/STATUS.md).

## Features

- **Real slash commands only** (no prefix commands; the Message Content intent is not used).
- **Modules** that each server can enable or disable; disabling stops delivery but keeps data.
- **Esports (CS2)**: upcoming matches, results, events, Valve Regional Standings (VRS), team lookup, personal team
  follows, and server filters (team / tournament / tier / VRS top-N) set by server admins.
- **Formula 1** (separate module, off by default): session started (only when a live provider confirms it — never
  from the clock), practice/sprint/race results, drivers' and constructors' standings, `/f1 next|schedule|results|now`.
- **Volleyball — Filenin Sultanları** (separate module, off by default): Türkiye women's senior national team only;
  15-minute reminder, match started, each set, final result (low spam, no point-by-point updates), `/volleyball next|schedule`.
- **TSQ Live** (separate module, off by default and `Live:Enabled=false`): Twitch + Kick live announcements for configured
  creators via the official APIs — one `@everyone` per new creator session (multistream = one message), title/platform
  changes and the end of the stream edit the same message without pinging; restart, reconnect and provider-outage safe.
- **TSQ LFG — Oyuncu Bul** (separate module, off by default): `/ekip` opens a form for a group listing for any game or
  activity (free-text game name, team size from a select, start — now or a date and time in the server's time zone —
  voice channel and opt-in pings, then the duration and optional details); members join/leave with buttons, the owner can edit the
  listing (✏️ Düzenle), the owner or a moderator closes it, it expires on its own; Maybe RSVP, a first-come first-served
  waitlist when the team is full (freed slots are filled automatically), opt-in pings of the joined
  players 30 minutes before / at the start, optional voice channel; one generic lifecycle for every game, restart- and
  race-safe.
- **TSQ Quote** (separate module, off by default): `/quote message:<message id> [channel]` (Copy Message ID; a message
  link also works) posts one message of this server as a PNG quote card — the author's avatar in black and white fading
  into black, the text in large white type, "— name" and "@username" below. The message is read once by id over REST
  (no channel scan, no message events); server-side access checks (same server; the member and the bot may view the
  channel and read its history; the bot may attach files where the command ran); stateless (nothing stored or logged).
  Needs the application's Message Content access (Developer Portal) — the gateway intents stay `Guilds`.
- **TSQ Döviz & Altın** (separate module, off by default): `/dolar`, `/euro`, `/altın` answer publicly in the currency
  channel (elsewhere: a private pointer to it) with the current USD/TRY, EUR/TRY and gram gold buy/sell prices and the
  provider's own update time; one combined card is posted there daily at 09:00 Türkiye time (outbox, catch-up until 09:30). Altınkaynak's public JSON service is
  the primary source; TCMB's daily indicative rate (USD/EUR, labelled as such) and Trunçgil (gram gold) are fallbacks, then
  the last good price for up to 15 minutes, clearly marked. Fetched on demand only, cached in memory (60 s / 30 s,
  one shared fetch for concurrent requests); no API key, no own table, no price polling.
- **TSQ Randomizer** (separate module, off by default): `/zarat zar:2d6` (1–20 dice, 2–10,000 sides; `2-6` works too),
  `/randomsayi maksimum:100 [minimum:1]` (inclusive, ±1,000,000,000), `/sec seçenekler:"CS2, Valheim, WoW"` (2–25 distinct
  options; `,` and `|` both separate, mixed too), `/yazitura`. Public result cards, private refusals for invalid input; every draw from
  `RandomNumberGenerator` (never `System.Random`); option text is defused, nothing pings; stateless (nothing stored or logged).
- **TSQ Çekiliş** (separate module, off by default): `/giveaway create` opens a form (prize, duration such as `30m`/`2h`/`1d 12h`,
  1–10 winners, optional description); the bot posts the card and adds 🎉, members enter with Discord's own 🎉 reaction, and
  when the time is up the bot reads every reaction page, draws unique non-bot members and turns the same card into the
  result with one short message that pings only the winners. `/giveaway end|cancel|reroll` for admins (reroll leaves
  earlier winners out). Restart-safe (stored giveaways, one worker loop) and race-safe (drawn at most once).
- **TSQ Özet** (separate module, off by default): `/ozetle` reads the latest 100 member messages of the channel or thread
  when it runs (REST; no message events, no cache, nothing stored), sends them as a normalized transcript to one AI model
  (OpenCode Go, `deepseek-v4.1-flash`) in a single request, and posts a short Turkish summary publicly without pings. No
  retry, no fallback model; cooldowns, one summary per channel at a time, at most two bot-wide; refusals are private.
- **Compact match cards**: planned-start reminder, match started (only when the provider reports it), result
  (optional spoiler mode), postponed, cancelled, forfeit. Each is sent once; later corrections edit the same message
  without pinging again — a changed start time updates the existing reminder instead of posting a new card.
- **Opt-in pings**: role mentions only for roles an admin explicitly mapped; `allowed_mentions` is locked down. The only
  `@everyone` is TSQ Live's first announcement of a new stream session (never on edits, replacements or restarts).
- **Honest data**: provider errors are never shown as "no matches", a passed start time is never "started", and
  HLTV is never scraped (only verified match-page links are shown).
- **Durable delivery**: persistent outbox with de-duplication, crash recovery and ambiguous-delivery reconciliation.
- **Privacy**: `/privacy export` and `/privacy delete`, data retention after the bot leaves a server.
- Turkish by default with English fallback; Europe/Istanbul default time zone.

## Discord Commands

| Group | Commands | Who |
|---|---|---|
| General | `/help`, `/bot status\|about\|source`, `/privacy export\|delete` | everyone |
| Admin | `/setup`, `/modules list\|enable\|disable` | Manage Server |
| Esports | `/esports matches\|results\|events\|rankings\|team\|follow\|unfollow\|subscriptions` | everyone (module on) |
| Esports admin | `/esports-admin configure\|filters\|roles\|panel\|preview\|pause\|resume\|doctor` | Manage Server (+ Manage Roles for roles) |
| Formula 1 | `/f1 next\|schedule\|results\|now`, `/f1 standings drivers\|constructors` | everyone (module on) |
| Formula 1 admin | `/f1-admin configure channel\|notifications\|role\|spoilers`, `/f1-admin preview\|status\|doctor\|pause\|resume` | Manage Server |
| Volleyball | `/volleyball next\|schedule` | everyone (module on) |
| Volleyball admin | `/volleyball-admin configure channel\|notifications\|role`, `/volleyball-admin preview\|status\|doctor\|pause\|resume` | Manage Server |
| TSQ Live admin | `/live-admin doctor` | Manage Server |
| TSQ LFG | `/ekip` (opens the listing form; buttons: Katıl (🎟️ Sıraya Gir while full) · Belki · Ayrıl · 🔊 Ses Odası · ✏️ Düzenle (owner only) · İlanı Kapat) | everyone (module on) |
| TSQ LFG admin | `/lfg-admin channel\|status` | Manage Server |
| TSQ Quote | `/quote message:<message id> [channel]` (or a message link) | everyone (module on; the member must be able to read the quoted message) |
| TSQ Doğum Günü | `/birthday set\|show\|remove` (own birthday only) | everyone (module on) |
| TSQ Döviz & Altın | `/dolar`, `/euro`, `/altın` (no options; public answer, currency channel only) | everyone (module on) |
| TSQ Randomizer | `/zarat zar:…`, `/randomsayi maksimum:… [minimum:…]`, `/sec seçenekler:…`, `/yazitura` (public answer) | everyone (module on) |
| TSQ Çekiliş | `/giveaway create` (form → card → 🎉 → automatic draw), `/giveaway end\|cancel\|reroll giveaway:<#number \| message link \| id>` | Manage Server (module on); entering: everyone, with 🎉 |
| TSQ Özet | `/ozetle` (no options; public summary of this channel's latest messages) | everyone (module on) |
| TSQ Doğum Günü admin | `/birthday-admin configure\|status\|doctor`; `/birthday-admin set\|show member …` | Manage Server; `set`/`show`: Administrator or server owner |

Commands are registered per server (guild commands) with a dry-run first. Permissions, intents and invite scopes:
[docs/COMMANDS_AND_PERMISSIONS.md](docs/COMMANDS_AND_PERMISSIONS.md). The generated, test-checked schema is
[docs/commands.manifest.json](docs/commands.manifest.json).

## Esports Providers

| Provider | Role |
|---|---|
| **PandaScore** | Default match data (fixtures, live state, results). A free plan exists; a token is required. |
| **Valve Regional Standings** | Rankings for `/esports rankings` and VRS filters. |
| **Liquipedia** | Only used to find the editor-entered HLTV match id for a match page: LiquipediaDB with an approved key, otherwise the free MediaWiki API (API only, rate-limited, cached). |
| **HLTV** | Never scraped or contacted. A "Maç Sayfası" link appears only when Liquipedia has the match's HLTV id. |

HLTV links are fully automatic; there is no manual link workflow. Limits, terms and attribution:
[docs/PROVIDERS.md](docs/PROVIDERS.md).

## Formula 1 Providers

| Provider | Role |
|---|---|
| **Jolpica F1** | Schedule and championship standings (no key; non-commercial use, data CC BY-NC-SA 4.0). |
| **OpenF1** | Live session lifecycle (MQTT, paid sponsor access) and session results (free historical access); data CC BY-NC-SA 4.0. |

Without OpenF1 live credentials the module runs honestly without start notifications. Details: [docs/FORMULA1.md](docs/FORMULA1.md).

## Volleyball Provider

| Provider | Role |
|---|---|
| **FIVB VIS** | Official FIVB web service (public data, no key): fixtures, results and set scores of Türkiye's women's senior team in FIVB/CEV tournaments. No logos are used. |

Why FIVB VIS (and why not API-Sports, CEV, TVF or others): [docs/volleyball/PROVIDER_RESEARCH.md](docs/volleyball/PROVIDER_RESEARCH.md).

## Installation / Development

Requirements: Windows 10/11, .NET SDK 10.0.401+ (pinned by `global.json`) and Git. The PowerShell scripts work on
Windows PowerShell 5.1 and PowerShell 7.

```powershell
.\scripts\Doctor.ps1                 # environment + configuration diagnosis (never prints secrets)
.\scripts\Test.ps1                   # build (warnings = errors) + format + manifest check + all tests
.\scripts\Start-Dev.ps1              # run locally in safe mode (no Discord connection)
.\scripts\Start-Dev.ps1 -Simulate    # offline end-to-end run with synthetic data and a fake Discord
.\scripts\Sync-Commands.ps1 -GuildId <id>   # slash command registration, dry-run by default
.\scripts\Export-Source.ps1          # source archive of the running commit (AGPL Corresponding Source)
```

Step-by-step setup (Turkish): [docs/WINDOWS_SETUP.md](docs/WINDOWS_SETUP.md). Adding a module:
[docs/ADDING_A_MODULE.md](docs/ADDING_A_MODULE.md).

## Configuration

`src/ToroSquad.Bot/appsettings.json` ships **safe defaults**:

| Setting | Default | Meaning |
|---|---|---|
| `Discord:Transport` | `Fake` | no Discord connection; messages stay in-process |
| `Delivery:Mode` | `DryRun` | notifications are planned and logged, not sent |
| `Esports:Provider:Name` | `PandaScore` | match data provider |
| `Esports:Provider:Mode` | `Fixture` | synthetic data through the real client/parser, labelled TEST/DEMO |
| `Formula1:Provider:Mode` | `Fixture` | synthetic TEST/DEMO race weekend; `Live` for Jolpica + OpenF1 |
| `Volleyball:Provider:Mode` | `Fixture` | synthetic TEST/DEMO match; `Live` for FIVB VIS |
| `Live:Enabled` | `false` | TSQ Live off; needs `Live:DiscordChannelId` and Twitch/Kick credentials ([docs/live/TSQ_LIVE.md](docs/live/TSQ_LIVE.md)) |
| `Lfg:*` | `120` min, `2`, `20` | default listing duration, active listings per user, max team size ([docs/lfg/TSQ_LFG.md](docs/lfg/TSQ_LFG.md)) |
| `Discord:AllowedGuildIds` | `[]` | when set, the bot only serves these servers (enforced server-side) |
| `Discord:AllowGlobalCommandSync` | `false` | global command registration is a separate, explicit step |
| `Bot:SourceUrl` | `https://github.com/Torokal/TSQ-Bot` | shown by `/bot source` |

Secrets are read **only** from `dotnet user-secrets` or environment variables with the `TOROSQUAD_` prefix
(`:` → `__`, e.g. `TOROSQUAD_Discord__Token`, `TOROSQUAD_PandaScore__Token`). There is no `.env` support, and secrets
never belong in the repository. The `ToroSquad.*` project names and the `TOROSQUAD_` prefix are internal identifiers
kept from the project's former working name.

## Deployment

TSQ Bot runs as a single container with a persistent volume for its SQLite database (one replica, no public HTTP).
A Railway guide is in [docs/RAILWAY_DEPLOYMENT.md](docs/RAILWAY_DEPLOYMENT.md) (Turkish); backups, restore and the
release checklist are in [docs/OPERATIONS.md](docs/OPERATIONS.md).

## Testing

`.\scripts\Test.ps1 -Repeat 3` runs the full gate three times. Tests use a real SQLite file per test, real
migrations, the production DI registration, a fake clock and a fake Discord transport; they make **no network
calls**. Test coverage by area: [docs/TESTING.md](docs/TESTING.md).

## Architecture

.NET 10 (LTS) · Discord.Net 3.20 Interaction Framework · EF Core 10 + SQLite · single process, single instance.

| Project | Role |
|---|---|
| `ToroSquad.Core` | Module contracts, authorization, localization, message model, role policy, privacy contracts |
| `ToroSquad.Infrastructure` | EF Core/SQLite, outbox + dispatcher, backups, secret redaction |
| `ToroSquad.Discord` | Interaction host, core commands, command manifest/sync, Discord transport |
| `ToroSquad.Modules.Esports` | The esports module (providers, planner, commands) |
| `ToroSquad.Modules.Formula1` | The Formula 1 module (provider capabilities, lifecycle state machine, planner, commands) |
| `ToroSquad.Modules.Volleyball` | The volleyball module (Türkiye women's senior team only: identity filter, match state machine, FIVB VIS provider, planner, commands) |
| `ToroSquad.Modules.Live` | TSQ Live (Twitch + Kick stream announcements: creator session state machine, official-API reconciliation, planner) |
| `ToroSquad.Modules.Lfg` | TSQ LFG — Oyuncu Bul (generic group-finder listings: create/join/leave/close/expire, race-safe persistence, card sync) |
| `ToroSquad.Modules.Quote` | TSQ Quote (`/quote`: message reference parsing, access checks, Discord text → plain text, ImageSharp card renderer with embedded fonts) |
| `ToroSquad.Modules.Currency` | TSQ Döviz & Altın (`/dolar`, `/euro`, `/altın`: Altınkaynak / TCMB / Trunçgil parsers, dataset cache with single flight, last-known-good) |
| `ToroSquad.Modules.Randomizer` | TSQ Randomizer (`/zarat`, `/randomsayi`, `/sec`, `/yazitura`: dice notation and option parsers, one secure random source) |
| `ToroSquad.Modules.Giveaway` | TSQ Çekiliş (`/giveaway`: form and duration parser, restart-safe draw worker, paged 🎉 reaction reading, unbiased winner draw, card sync, winner announcement via the outbox) |
| `ToroSquad.Modules.Summary` | TSQ Özet (`/ozetle`: on-demand channel read, transcript normalization, one OpenCode Go request, deterministic clean-up and split, in-memory cooldowns) |
| `ToroSquad.Modules.Birthday` | TSQ Doğum Günü (day + month registrations, restart-safe daily reconciliation: one announcement, temporary role) |
| `ToroSquad.Modules.Example` | Minimal example module (proves modules plug in without touching others) |
| `ToroSquad.Bot` | Composition root, CLI, migrations |

Dependency rules are enforced by architecture tests. Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and
[docs/adr/](docs/adr/). Notification behaviour: [docs/NOTIFICATIONS.md](docs/NOTIFICATIONS.md).

## Privacy / Security

TSQ Bot does not read message content and does not download member lists. It stores per-server settings, follows,
notification state and minimal user preferences. Details: [docs/PRIVACY_AND_DATA.md](docs/PRIVACY_AND_DATA.md);
draft policies: [docs/policies/](docs/policies/). To report a vulnerability, see [SECURITY.md](SECURITY.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

**AGPL-3.0-only**: see [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). `/bot source` shows
this repository together with the running version and commit. If you run a **modified** version for other users,
AGPL-3.0 §13 requires you to offer your modified source: set `Bot:SourceUrl` to your own repository (or publish an
`Export-Source.ps1` archive).

## Attribution

Match data by PandaScore ("Kaynak: PandaScore" on every card). Rankings from Valve's public regional standings.
Optional link data from Liquipedia (CC BY-SA 3.0). Volleyball data from the FIVB VIS web service ("Kaynak: FIVB"). Portions of the data-access code adapted from BOT-Greg-v2_API
(AGPL-3.0). Details: [docs/PROVENANCE.md](docs/PROVENANCE.md).
