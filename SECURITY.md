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

## Accepted dependency advisories

NuGet audit is on and its warnings fail the build. The only suppressed advisories (`NuGetAuditSuppress` in
`Directory.Build.props`, owner decision 2026-10-08) are five availability issues of `SixLabors.ImageSharp` 3.1.12 that are
fixed only in 4.1.2 — a release line that needs a Six Labors license key this repository does not have:

| Advisory | Component | Why TSQ Bot does not reach it |
|---|---|---|
| GHSA-j3p4-wp97-rph4 | `HistogramEqualization` on a float TIFF | never called |
| GHSA-jjfr-hcj7-qf5w | TIFF CCITT Group 4 encoder | the bot writes one PNG, never a TIFF |
| GHSA-j9gm-c75j-xc9q | TIFF CCITT Group 3 encoder | same |
| GHSA-wmxv-xphr-5c9g | BigTIFF metadata reader (loop) | avatars are decoded with PNG, JPEG, WebP and GIF decoders only; TIFF is "not an image" before any of its code runs (tested) |
| GHSA-gwg2-r3hj-4w44 | ICC profile CLUT allocation | `IccProfile.Entries` is never read |

The library is used only by TSQ Quote (avatar in, PNG out; avatars come from Discord's CDN hosts only). Any new advisory
fails the build again. The suppression is removed when a fixed 3.x release or a licensed 4.x upgrade is available.
