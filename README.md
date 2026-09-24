# TSQ Bot

Modular Discord bot with esports match tracking and extensible server modules.

> **Status (2026-09-25): early development — not production-ready, not publicly launched.**
> Core Discord behaviour is verified live in one private **test guild**; match data runs on **synthetic demo data**
> because no provider credentials are configured (PandaScore/Liquipedia live data: BLOCKED).
> Up-to-date state (in Turkish): [docs/PROJECT_STATE.md](docs/PROJECT_STATE.md).

## What is TSQ Bot?

TSQ Bot is an independent, self-hostable Discord bot built as a **modular monolith**: a small core (modules, per-server
settings, permissions, localization, durable message delivery, privacy) plus feature modules that plug into it.
The first feature module tracks **Counter-Strike 2 esports** — upcoming matches, results, tournaments, Valve Regional
Standings (VRS) — and posts notifications into a server channel. Future modules (moderation, welcome, stream alerts,
polls, …) can be added without changing the core or the esports module.

The esports module is inspired by the user experience of the discontinued **BOT Greg** and reuses adapted portions of
the open-source [BOT-Greg-v2_API](https://github.com/julius-gmeinder/BOT-Greg-v2_API) (AGPL-3.0). TSQ Bot is **not** an
official continuation of BOT Greg, is not affiliated with or endorsed by its developer, and uses none of its branding.
Details: [docs/PROVENANCE.md](docs/PROVENANCE.md).

## Current Status

Status words: **TESTED_OFFLINE** = proven by automated tests locally · **VERIFIED_LIVE** = observed against real Discord /
real APIs · **BLOCKED** = waiting on an owner action · **DEFERRED** = intentionally not built yet ·
**PRE-RELEASE REQUIREMENT** = must happen before public launch.

| Area | Status |
|---|---|
| Discord gateway, guild-only command registration (no global commands), minimum permissions, no privileged intents | **VERIFIED_LIVE** (test guild) |
| `/help`, `/bot`, `/modules`, `/setup`, `/esports …`, `/esports-admin configure\|doctor\|roles map`, team autocomplete, `/privacy export\|delete` | **VERIFIED_LIVE** (test guild) |
| TEST/DEMO notification delivery, no duplicate on re-scan or restart, settings survive restarts | **VERIFIED_LIVE** (test guild) |
| Compact match cards (started / result / postponed / rescheduled / cancelled / forfeit) | TESTED_OFFLINE; Discord rendering pending live check |
| PandaScore provider (default): parser, lifecycle, pagination, error types, rate budget | TESTED_OFFLINE (synthetic fixtures) — **live BLOCKED** (no token) |
| Liquipedia provider (legacy/optional) | TESTED_OFFLINE — live BLOCKED (no approved key) |
| Valve VRS rankings | TESTED_OFFLINE — live fetch not run |
| Verified external match links (HLTV/official/provider), URL safety | TESTED_OFFLINE |
| Admin/member permission separation with a second account, role grant/removal, cross-guild isolation, crash recovery | TESTED_OFFLINE |
| HLTV as a data provider, BOT Greg "stars", news, Docker / 24×7 hosting, global commands, public launch | DEFERRED |
| GitHub repository public visibility | **PRE-RELEASE REQUIREMENT** — private during development/testing, made public before public bot launch |

## Features

- **Real Discord slash commands** only (no prefix/message commands; Message Content intent is not used).
- **Modules**: each server enables/disables modules; disabling stops delivery but keeps data.
- **Esports (CS2)**: upcoming matches, results, events, VRS rankings, team lookup, follow/unfollow teams,
  per-server filters (team / tournament / tier / VRS top-N).
- **Match notifications as compact cards**: planned-start reminder, **match started** (only when the provider states
  `running`), **result** (score in the title, winner line), **postponed**, **time changed** (provider-flagged, shown in
  Europe/Istanbul), **cancelled**, **forfeit** — each sent once, never duplicated by re-scans or restarts.
- **"Maç Sayfası" link**: a verified HLTV match page is preferred, then an official or provider page, otherwise no link.
  HLTV is **never scraped** and HLTV URLs are never guessed.
- **Honest data**: provider errors are never shown as "no matches" or turned into match states; a passed start time is
  never called "started"; live mode never falls back to demo data; unknown stays unknown.
- **Durable delivery**: persistent outbox with de-duplication, crash recovery and ambiguous-delivery reconciliation;
  corrections edit the existing message without pinging. No "exactly once" claim.
- **Safe roles**: only roles that grant no permissions and sit below the approver can be self-service; the bot never
  removes a role it cannot prove it granted.
- **Spoiler mode**, `allowed_mentions` locked down by default, provider text cannot create links or mentions.
- **Privacy**: `/privacy export` and `/privacy delete` (confirmed, single-use), retention after the bot leaves a server.
- Turkish by default with English fallback; Europe/Istanbul default time zone.

## Slash Commands

| Group | Commands | Who |
|---|---|---|
| General | `/help`, `/bot status\|about\|source`, `/privacy export\|delete` | everyone |
| Admin | `/setup`, `/modules list\|enable\|disable` | Manage Server |
| Esports | `/esports matches\|results\|events\|rankings\|team\|follow\|unfollow\|subscriptions` | everyone (module on) |
| Esports admin | `/esports-admin configure\|filters …\|roles …\|panel\|preview\|pause\|resume\|doctor` | Manage Server (+ Manage Roles for roles) |

Full list, permissions, intents and invite scopes: [docs/COMMANDS_AND_PERMISSIONS.md](docs/COMMANDS_AND_PERMISSIONS.md).
Generated, test-checked schema: [docs/commands.manifest.json](docs/commands.manifest.json).

## Architecture

.NET 10 (LTS) · Discord.Net 3.20 Interaction Framework · EF Core 10 + SQLite · single process, single instance.

| Project | Role |
|---|---|
| `ToroSquad.Core` | Module contracts, authorization, localization, message model, role policy, privacy contracts |
| `ToroSquad.Infrastructure` | EF Core/SQLite, outbox + dispatcher, backups, secret redaction |
| `ToroSquad.Discord` | Interaction host, core commands, command manifest/sync, Discord transport |
| `ToroSquad.Modules.Esports` | The esports module (providers, planner, commands) |
| `ToroSquad.Modules.Example` | Minimal example module (proves modules plug in without touching others) |
| `ToroSquad.Bot` | Composition root, CLI, migrations |

The `ToroSquad.*` project/namespace names are internal identifiers kept from the project's former working
name; the product name is TSQ Bot. Dependency rules are enforced by architecture tests.
Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/adr/](docs/adr/), [docs/ADDING_A_MODULE.md](docs/ADDING_A_MODULE.md).

## Development

Requirements: Windows 10/11, .NET SDK 10.0.401+ (pinned by `global.json`), Git. PowerShell scripts work on Windows
PowerShell 5.1 and PowerShell 7.

```powershell
.\scripts\Doctor.ps1                 # environment + configuration diagnosis (never prints secrets)
.\scripts\Test.ps1                   # build (warnings = errors) + format + manifest check + all tests
.\scripts\Start-Dev.ps1              # run locally in safe mode (NO Discord connection)
.\scripts\Start-Dev.ps1 -Simulate    # offline end-to-end: fixture data -> planner -> outbox -> fake Discord
.\scripts\Sync-Commands.ps1 -GuildId <id>   # slash command registration, DRY-RUN by default
.\scripts\Export-Source.ps1          # source archive of the running commit (AGPL Corresponding Source)
```

## Windows Setup

Step-by-step (Turkish): [docs/WINDOWS_SETUP.md](docs/WINDOWS_SETUP.md) — SDK install, secrets, Discord application,
test server, going live.

## Configuration

`src/ToroSquad.Bot/appsettings.json` ships **safe defaults**:

| Setting | Default | Meaning |
|---|---|---|
| `Discord:Transport` | `Fake` | no Discord connection; messages stay in-process |
| `Delivery:Mode` | `DryRun` | notifications are planned and logged, not sent |
| `Esports:Provider:Name` | `PandaScore` | match data provider (`Liquipedia` = legacy/optional) |
| `Esports:Provider:Mode` | `Fixture` | synthetic data through the real client/parser, labelled TEST/DEMO |
| `Esports:MatchPollMinutes` | `5` | validated against the provider's request budget at startup |
| `Esports:VerifiedMatchLinks` | `[]` | operator-curated verified HLTV/official match pages |
| `Discord:AllowGlobalCommandSync` | `false` | global command registration is a separate approval gate |
| `Bot:SourceUrl` | `https://github.com/Torokal/TSQ-Bot` | shown by `/bot source` (see [Source Code](#source-code)) |

Secrets are read **only** from `dotnet user-secrets` or environment variables with the `TOROSQUAD_` prefix
(`:` → `__`, e.g. `TOROSQUAD_Discord__Token`, `TOROSQUAD_PandaScore__Token`). There is no `.env` file support, and secrets never belong in the repository.

## Discord Setup

The Discord application **"TSQ Bot"** exists and is verified in a private test guild. To run your own: create the
application and bot in the Discord Developer Portal, store the token with
`dotnet user-secrets set "Discord:Token" "<token>" --project src\ToroSquad.Bot`, invite the bot to a **test** server
(scopes `bot applications.commands`; permissions integer 84992, or 268520448 with self-service roles; no Administrator),
then register commands with `Sync-Commands.ps1` (dry-run first). Only the non-privileged **Guilds** gateway intent is used.

## Data Providers

- **PandaScore (default)** — create a token at app.pandascore.co (a free plan with 1,000 requests/hour exists; no paid plan
  is enabled automatically) and store it with `dotnet user-secrets set "PandaScore:Token" "<TOKEN>" --project src\ToroSquad.Bot`.
  Without a token live PandaScore data is **BLOCKED**. Every card says "Kaynak: PandaScore" as PandaScore's terms require.
- **Liquipedia (legacy/optional)** — only if `Esports:Provider:Name=Liquipedia`; needs an approved LiquipediaDB API key
  and a User-Agent with your contact. Content is CC BY-SA 3.0 and attributed in every message.
- **Valve Regional Standings** — rankings for `/esports rankings` and VRS filters.
- **HLTV** — not a data source; only verified match-page links.

Details, verified limits and terms: [docs/PROVIDERS.md](docs/PROVIDERS.md).

## Testing

`.\scripts\Test.ps1 -Repeat 3` runs the full gate three times. Tests use a real SQLite file per test, real migrations,
the production DI registration, a fake clock and a fake Discord transport; they make **no network calls**.
Latest result and the acceptance-criteria mapping: [docs/TESTING.md](docs/TESTING.md),
[docs/PROJECT_STATE.md](docs/PROJECT_STATE.md).

## Privacy

TSQ Bot does not read message content and does not download member lists. It stores per-server settings, follows,
notification state and minimal user preferences. Users can export or delete their data with `/privacy`.
Details: [docs/PRIVACY_AND_DATA.md](docs/PRIVACY_AND_DATA.md); draft policies: [docs/policies/](docs/policies/).

## License

**AGPL-3.0-only** — see [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Portions of the
data-access code are adapted from BOT-Greg-v2_API (AGPL-3.0); provenance and attributions are in
[docs/PROVENANCE.md](docs/PROVENANCE.md).

## Source Code

Canonical repository: **https://github.com/Torokal/TSQ-Bot**. `/bot source` shows this URL together with the running
version and commit.

Release approach: the repository is **private during development and testing** (TSQ Bot is not publicly launched, so
the link only works for authorized GitHub users for now). **Before public bot launch the repository will be made
public**, and the same URL becomes the public Corresponding Source location; the published source must match the
deployed version. This is the project's compliance approach, not legal advice.

If you run a **modified** version for other users, AGPL-3.0 §13 requires you to offer *your* modified source: set
`Bot:SourceUrl` to your own repository (or publish an `Export-Source.ps1` archive).

## Current Limitations

- Live match data is not verified yet (no PandaScore token); cards were verified with synthetic TEST/DEMO data only.
- Whether PandaScore's free plan fills result fields (`winner_id`, scores) is unverified (official pages conflict).
- Not publicly launched; the source repository is private until the pre-release step makes it public.
- "Match started" needs a provider that states it (PandaScore); with Liquipedia only planned-start reminders exist.
- PandaScore offers no public match page; a "Maç Sayfası" link appears only for operator-verified links.
- Single instance only (SQLite, in-process scheduler); no horizontal scaling.
- No DM commands or DM notifications (by design).
- Policies in `docs/policies/` are drafts, not legal advice.
