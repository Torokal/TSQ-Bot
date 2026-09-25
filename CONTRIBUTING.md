# Contributing to TSQ Bot

Thanks for your interest! This document describes how changes are made and checked.

## Prerequisites

- Windows 10/11 (PowerShell 5.1 or 7), .NET SDK 10.0.401+ (pinned by `global.json`), Git.
- `dotnet tool restore` installs the repository-local tools (e.g. `dotnet-ef`).
- No Discord token or provider key is needed for development or tests: the defaults use synthetic data, a fake
  Discord transport and dry-run delivery. See [docs/WINDOWS_SETUP.md](docs/WINDOWS_SETUP.md).

## Workflow

1. Create a branch from `main` (`feature/…`, `fix/…`, `docs/…`, `chore/…`).
2. Keep changes focused; update the relevant docs in `docs/` when behaviour changes.
3. Open a pull request against `main`. `main` is protected: changes land only through reviewed pull requests, and
   force pushes are not allowed.

Commit messages follow a conventional style, for example:

```
feat(esports): add match lifecycle handling
fix(storage): preserve notification state after restart
docs: simplify deployment guide
test(discord): cover guild authorization
```

## Build and test

```powershell
.\scripts\Test.ps1 -Repeat 3
```

The gate must pass with **0 warnings and 0 errors**: Release build with analyzers (warnings are errors),
`dotnet format --verify-no-changes`, command manifest validation and the full test suite. If a test is flaky,
investigate the cause instead of re-running until it is green.

Additional rules:

- **Formatting:** run `dotnet format` before committing; `.editorconfig` is authoritative.
- **Slash commands:** after changing commands, regenerate the manifest:
  `$env:DOTNET_ENVIRONMENT='Production'; dotnet run --project src/ToroSquad.Bot -- commands export --out docs/commands.manifest.json`
- **Database:** after changing EF entities, add a migration:
  `dotnet ef migrations add <Name> --project src/ToroSquad.Bot`. Destructive migrations need a clear justification and a
  backup/restore note.
- **Architecture:** dependency rules are enforced by `tests/ToroSquad.Tests/Architecture`. Every interaction class needs
  `[ToroModule("<id>")]`, every admin operation must authorize server-side, and modules send notifications only through
  the outbox.
- **Scripts:** PowerShell scripts must stay ASCII (Windows PowerShell 5.1).
- **Naming:** the product name is TSQ Bot (from `ProductInfo.ProductName`); the `ToroSquad.*` identifiers are internal
  and are not renamed casually.

## Provider APIs

Automated tests never call external services: provider behaviour is tested with synthetic fixtures and contract tests.
Do not add tests that need a real PandaScore, Liquipedia or Discord connection, and never commit real API responses
that are not allowed to be redistributed. Live checks are manual and read-only (e.g. `esports provider-check`).
HLTV must never be scraped.

## Secrets

Never commit tokens, API keys, `.env` files, user-secrets, databases or backups. Secrets are provided through
`dotnet user-secrets` or `TOROSQUAD_*` environment variables only. Tests that need token-shaped strings build fake
values at runtime so secret scanners are not triggered.

## License

By contributing you agree that your contribution is licensed under the project's license (AGPL-3.0-only).
