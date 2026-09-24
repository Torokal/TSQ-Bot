# CLAUDE.md — TSQ Bot

Read first, every session: `docs/PROJECT_STATE.md` (state, decisions, tests run, blockers, the single NEXT ACTION), then
verify with `git status` / `git log --oneline -10`. Do not restart work that the state file marks done.

## Ground rules (from the master spec `TSQ_Bot_Claude_Code_Master_Prompt.md`)
- Product name: **TSQ Bot** (formerly ToroSquad Bot). Modular monolith; esports is the first module. Real Discord
  slash commands only. User-facing text takes the name from `ProductInfo.ProductName` (localization: `{product}`
  token); never hard-code it. Internal identifiers (`ToroSquad.*` projects/namespaces, `TOROSQUAD_` env prefix,
  `torosquad.db`, user-secrets id) intentionally keep the old name — do not mass-rename them.
- Canonical source repository: https://github.com/Torokal/TSQ-Bot (`Bot:SourceUrl` default in appsettings.json).
- Never report unexecuted tests as passing or mock/fixture success as live success. Use the status words
  `IMPLEMENTED`, `TESTED_OFFLINE`, `VERIFIED_LIVE`, `BLOCKED`, `DEFERRED`.
- Explicit owner approval is required for: paid APIs/subscriptions, account creation/authorization, bot invites,
  sending anything to external systems, deployment, GitHub repo/push/release, messages to the upstream developer,
  production/main changes, global command registration, actions on a live server, user-data deletion/reset,
  destructive migrations, new secrets, security reductions. A test-guild approval is not a production approval.
- Never ask for secret values in chat. Secrets: env vars `TOROSQUAD_*` or `dotnet user-secrets` (docs/WINDOWS_SETUP.md).
- Windows + PowerShell first. Scripts must stay ASCII (PS 5.1).

## Working in this repo
- Build/test gate: `.\scripts\Test.ps1` (warnings are errors, format check, manifest check, tests).
- After changing slash commands: regenerate `docs/commands.manifest.json`
  (`$env:DOTNET_ENVIRONMENT='Production'; dotnet run --project src/ToroSquad.Bot -- commands export --out docs/commands.manifest.json`).
- After changing EF entities: `dotnet tool restore; dotnet ef migrations add <Name> --project src/ToroSquad.Bot`.
- Dependency rules are enforced by `tests/ToroSquad.Tests/Architecture`. Core must not reference Discord.Net/EF/modules;
  esports Domain/Providers/Application/Persistence must not use the Discord SDK.
- Every interaction class needs `[ToroModule("<id>")]`; every admin operation must call `Authorize.Require` server-side.
- Notifications go through `INotificationOutbox` only; never send directly from a module.
- Branch: work on `feature/*`; `main` holds only reviewed milestones (merging to main = owner decision).
- Update `docs/PROJECT_STATE.md` at milestones/blockers (not after every small step).
