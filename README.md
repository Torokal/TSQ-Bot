# TSQ Bot

A modular, self-hostable Discord bot. Its first module tracks **Counter-Strike 2 esports**: it posts compact match
cards (reminder, match started, result and schedule changes) into a server channel and answers esports slash commands.
A separate **Formula 1** module posts confirmed session starts, results (with in-place corrections) and championship
standings ([docs/FORMULA1.md](docs/FORMULA1.md)).

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
- **Compact match cards**: planned-start reminder, match started (only when the provider reports it), result
  (optional spoiler mode), postponed, time changed, cancelled, forfeit. Each is sent once; later corrections edit
  the same message without pinging again.
- **Opt-in pings**: role mentions only for roles an admin explicitly mapped; `allowed_mentions` is locked down.
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

Commands are registered per server (guild commands) with a dry-run first. Permissions, intents and invite scopes:
[docs/COMMANDS_AND_PERMISSIONS.md](docs/COMMANDS_AND_PERMISSIONS.md). The generated, test-checked schema is
[docs/commands.manifest.json](docs/commands.manifest.json).

## Esports Providers

| Provider | Role |
|---|---|
| **PandaScore** | Default match data (fixtures, live state, results). A free plan exists; a token is required. |
| **Valve Regional Standings** | Rankings for `/esports rankings` and VRS filters. |
| **Liquipedia** (optional) | Only used to find verified HLTV match-page links; needs an approved LiquipediaDB key. |
| **HLTV** | Never scraped. Only verified match-page links are displayed. |

Operators can also add verified match links manually (`Esports:VerifiedMatchLinks`). Limits, terms and attribution:
[docs/PROVIDERS.md](docs/PROVIDERS.md).

## Formula 1 Providers

| Provider | Role |
|---|---|
| **Jolpica F1** | Schedule and championship standings (no key; non-commercial use, data CC BY-NC-SA 4.0). |
| **OpenF1** | Live session lifecycle (MQTT, paid sponsor access) and session results (free historical access); data CC BY-NC-SA 4.0. |

Without OpenF1 live credentials the module runs honestly without start notifications. Details: [docs/FORMULA1.md](docs/FORMULA1.md).

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
Optional link data from Liquipedia (CC BY-SA 3.0). Portions of the data-access code adapted from BOT-Greg-v2_API
(AGPL-3.0). Details: [docs/PROVENANCE.md](docs/PROVENANCE.md).
