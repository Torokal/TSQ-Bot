# Security Policy

## Reporting a vulnerability

Please report security issues **privately** through GitHub's
[private vulnerability reporting](https://github.com/Torokal/TSQ-Bot/security/advisories/new)
("Report a vulnerability" on the repository's Security tab). Include steps to reproduce and the affected version or
commit (`/bot about` shows it). If that option is not available, open an issue that only asks for a private contact
channel, without any vulnerability details.

- Do **not** open a public issue for a vulnerability.
- Never post tokens, API keys, `.env` contents, database files or other secrets in issues, pull requests or reports.
  If you believe a secret was exposed, say so without including the value.

## Supported versions

Only the latest commit on `main` is supported. There are no versioned releases yet; fixes are made on `main`.

## Scope notes

- The bot requests minimum Discord permissions and no privileged intents; admin actions are authorized server-side.
- Secrets are read only from `dotnet user-secrets` or `TOROSQUAD_*` environment variables and are masked in logs.
