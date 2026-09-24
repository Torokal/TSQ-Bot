# TSQ Bot

Modular Discord bot with esports match tracking and extensible server modules.

> **Status (2026-09-24): early development — not production-ready and not live-verified.**
> Everything below is built and tested **offline** (automated tests, fake Discord transport, synthetic provider data).
> The bot has **not** yet been connected to a real Discord server or to the live Liquipedia API.
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

Status words: **TESTED_OFFLINE** = proven by automated tests locally · **VERIFIED_LIVE** = checked against real Discord /
real APIs · **BLOCKED** = waiting on an owner action · **DEFERRED** = intentionally not built yet.

| Area | Status |
|---|---|
| Module core, per-server module enable/disable, server-side authorization, guild isolation | TESTED_OFFLINE |
| Slash command schema (`/help`, `/bot`, `/privacy`, `/setup`, `/modules`, `/esports`, `/esports-admin`) | TESTED_OFFLINE |
| Command registration tool (dry-run default, guild allow-list, prune only on explicit flag) | TESTED_OFFLINE (fake registrar) |
| Liquipedia client/parser (error types, pagination, request budget) · VRS parser and team matching | TESTED_OFFLINE (synthetic fixtures) |
| Notification planner, durable outbox, safe self-service roles, spoiler mode, mention safety | TESTED_OFFLINE |
| Privacy export/delete, data retention | TESTED_OFFLINE |
| Real Discord connection, commands visible in a server, real role changes | **BLOCKED** — no Discord application/token or test server yet |
| Live Liquipedia data | **BLOCKED** — needs an approved Liquipedia API key |
| Live VRS fetch | not run yet |
| News notifications, "match is live" notifications, Docker / 24×7 hosting | DEFERRED |
| Anything | **VERIFIED_LIVE: none yet** |

## Features

- **Real Discord slash commands** only (no prefix/message commands; Message Content intent is not used).
- **Modules**: each server enables/disables modules; disabling stops delivery but keeps data.
- **Esports (CS2)**: upcoming matches, results, events, VRS rankings, team lookup, follow/unfollow teams,
  per-server filters (team / tournament / tier / VRS top-N), planned-start reminders and result posts.
- **Honest data**: provider errors are never shown as "no matches"; a passed start time is never called "live";
  live mode never falls back to demo data.
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
| `Esports:Provider:Mode` | `Fixture` | synthetic data through the real client/parser, labelled TEST/DEMO |
| `Discord:AllowGlobalCommandSync` | `false` | global command registration is a separate approval gate |
| `Bot:SourceUrl` | `https://github.com/Torokal/TSQ-Bot` | shown by `/bot source` (see [Source Code](#source-code)) |

Secrets are read **only** from `dotnet user-secrets` or environment variables with the `TOROSQUAD_` prefix
(`:` → `__`, e.g. `TOROSQUAD_Discord__Token`). There is no `.env` file support, and secrets never belong in the repository.

## Discord Setup

**BLOCKED — the Discord application "TSQ Bot" has not been created/configured yet.**
When it is: create the application and bot in the Discord Developer Portal, store the token with
`dotnet user-secrets set "Discord:Token" "<token>" --project src\ToroSquad.Bot`, invite the bot to a **test** server
(scopes `bot applications.commands`; permissions integer 84992, or 268520448 with self-service roles; no Administrator),
then register commands with `Sync-Commands.ps1` (dry-run first). Only the non-privileged **Guilds** gateway intent is used.

## Liquipedia Setup

**BLOCKED — no approved Liquipedia API key.** LiquipediaDB API access is granted on request; paid plans exist and free
access is limited to approved open-source, non-commercial projects (the decision and any cost are the operator's).
Live mode also requires a User-Agent that identifies **your** bot and contact. Liquipedia content is CC BY-SA 3.0 and is
attributed in every message. Details: [docs/PROVIDERS.md](docs/PROVIDERS.md).

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

Canonical repository: **https://github.com/Torokal/TSQ-Bot** (currently private). `/bot source` shows this URL together with the running
version and commit. If you run a **modified** version for other users, AGPL-3.0 §13 requires you to offer *your*
modified source: set `Bot:SourceUrl` to your own repository (or use `Export-Source.ps1`).

## Current Limitations

- No live verification yet (Discord, Liquipedia, VRS) — see the status table.
- No "match is live" detection: Liquipedia offers no verified live flag, so reminders are planned-start reminders.
- Single instance only (SQLite, in-process scheduler); no horizontal scaling.
- No DM commands or DM notifications (by design).
- Policies in `docs/policies/` are drafts, not legal advice.
