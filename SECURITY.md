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
`Directory.Build.props`, owner decision 2026-10-08) are five availability issues of `SixLabors.ImageSharp` 3.1.12, plus the
single advisory below. They are fixed only in releases that enforce a Six Labors license key at build time (3.2.0 and 4.1.2),
which this repository does not have; the package version is kept at 3.1.12:

| Advisory | Component | Why TSQ Bot does not reach it |
|---|---|---|
| GHSA-j3p4-wp97-rph4 | `HistogramEqualization` on a float TIFF | never called |
| GHSA-jjfr-hcj7-qf5w | TIFF CCITT Group 4 encoder | the bot writes one PNG, never a TIFF |
| GHSA-j9gm-c75j-xc9q | TIFF CCITT Group 3 encoder | same |
| GHSA-wmxv-xphr-5c9g | BigTIFF metadata reader (loop) | avatars are decoded with PNG, JPEG, WebP and GIF decoders only; TIFF is "not an image" before any of its code runs (tested) |
| GHSA-gwg2-r3hj-4w44 | ICC profile CLUT allocation | `IccProfile.Entries` is never read |

### GHSA-v76p-62qx-wwq2 (owner decision 2026-10-28)

Heap out-of-bounds write in the TIFF decoder (tiled TIFF with fax compression; high, availability, reached through
`Image.Load` with the default configuration). Fixed in 3.2.0 / 4.1.1. The package version is kept; this advisory alone is
suppressed, and only in the three projects whose restore graph contains ImageSharp (`ToroSquad.Modules.Quote`, and
`ToroSquad.Bot` / `ToroSquad.Tests` through it). Any other project that pulls ImageSharp in fails the build again. This is
not a fix: the vulnerable decoder is still in the package.

Basis (source review, 2026-10-28):

- The only ImageSharp decode of untrusted bytes in `src/` is `QuoteImageRenderer.DecodeAvatar`
  (`src/ToroSquad.Modules.Quote/Application/QuoteImageRenderer.cs`). Its only caller is `QuoteImageRenderer.DrawPhoto`.
  Everything else is a new canvas, drawing and one PNG encode; fonts are read by SixLabors.Fonts, not by an image decoder.
- `DecodeAvatar` uses `Image.Identify` and `Image.Load` with `DecoderOptions.Configuration = AvatarFormats`. That
  configuration registers the PNG, JPEG, WebP and GIF modules only, so the default configuration is never used and there is
  no fallback to it. A TIFF or BigTIFF is detected by its content as an unknown format and refused before any TIFF code runs.
  The "not an image" result falls back to the initial-letter card.
- The check reads the bytes themselves. `QuoteAvatarClient` passes whatever it downloaded (at most 4 MiB, from Discord CDN
  hosts) without looking at a file extension or an HTTP `Content-Type`.
- Tests: `QuoteRendererTests.Tiff_bigtiff_bmp_tga_and_other_formats_are_not_images_for_the_avatar_decoder` covers TIFF,
  BigTIFF (hostile header, rejected at once), BMP, TGA, PBM and QOI, plus the card's fallback.
  `QuoteRendererTests.An_avatar_is_decoded_from_png_jpeg_webp_and_gif_only` covers the four allowed formats, and
  `QuoteAvatarClientTests.Invalid_image_bytes_download_fine_and_the_renderer_falls_back` shows that non-image bytes reach the
  decoder unfiltered and are refused there.

Re-evaluate the suppression whenever an image is decoded anywhere else, the decoder configuration of `DecodeAvatar`
changes, an avatar source other than these bytes is added, or the ImageSharp version changes.

The library is used only by TSQ Quote (avatar in, PNG out; avatars come from Discord's CDN hosts only). Any new advisory
fails the build again. The suppressions are removed when a licensed upgrade (3.2.0 or later) is made.
