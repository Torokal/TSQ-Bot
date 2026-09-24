# Upstream provenance, reuse and license

Reviewed on **2026-09-24** (clones in a scratch folder, read-only; not vendored into this repo).

## Repositories reviewed

| Repository | Branch / HEAD reviewed | Commit date | What it is (verified) | License |
|---|---|---|---|---|
| https://github.com/julius-gmeinder/BOT-Greg-v2_API | `master` @ `3898b4ebfd4ed26ec077f4167a780f1684d91331` ("vrs service, small fixes for matches and results") | 2026-09-23 17:32 +0200 | ASP.NET Core (`net10.0`) Web API start: `LiquipediaController`, `LiquipediaService`, `VrsService`, models, empty `DbContext`, Swagger. 3 commits total. **Not a Discord bot**; no command, scheduler or persistence code. | `LICENSE.txt` = GNU AGPL v3 (no per-file headers, no "or later" statement) |
| https://github.com/julius-gmeinder/BOT-Greg-Policies | `main` @ `4ed71e7b03a8b9fe16c10d2f7bb72d275d58d09e` | 2025-06-08 16:33 +0200 | Static HTML site (index, privacy-policy, terms-of-service), CSS, logo/icon SVGs for the old "BOT Greg" Discord app. | **No license file** → all rights reserved by default |

The commit mentioned in the task brief (`3898b4e…`) **is** the current HEAD as of 2026-09-24. Old BOT Greg service shutdown and
the V2 repository's development status are separate things: the V2 repo is an early API skeleton (3 commits, last 2026-09-23),
not a replacement bot, and nothing indicates the old bot's credentials, data or application would transfer.

## Findings in upstream code (why it was not copied as-is)

| Area | Upstream behaviour | Risk | TSQ Bot handling |
|---|---|---|---|
| Error handling | Non-2xx → logs and returns an **empty list** | API error indistinguishable from "no matches" | `ProviderResult` with Success / Partial / NotConfigured / AuthFailed / QuotaExceeded / Timeout / TransportError / SchemaError |
| Pagination | `limit=100`, single request | Silent truncation | Offset pagination, `MaxPages` bound, "did not advance" guard, Partial result |
| Match window | `date > now AND date < now+25h` | In-progress matches invisible | Window `now-12h … now+48h` without a `finished` condition |
| Parsing | `teamsNode[i]` for i=0..1, `GetProperty(...)!`, `GetString()!` | Crash on missing opponents/fields/nulls | Tolerant accessors, explicit TBD/Unknown opponents, warnings instead of exceptions |
| Dates | `DateTime.Parse(date)` (local-culture, unspecified kind) | Timezone/culture bugs | `ParseExact` as UTC (LPDB dates are UTC), invalid → null + warning |
| Scores | Map scores default `{0,0}`; forfeit mapped to "1-0" | Invented scores | Scores only when the source states them (`status=S`, ≥0); forfeit/draw/not-played explicit |
| User-Agent | `BOT-Greg-v2/1.0 (julius.gmeinder@proton.me)` | Impersonates another operator | Operator-configured UA required; config validation rejects the upstream identity |
| VRS | Latest file by string sort of whole tree path, column positions fixed | Wrong file / silent misparse | Date from file name, header-based columns, host allow-list, failure types |
| Tier | `Convert.ToInt32(liquipediatier)` | Crash on empty/non-numeric | Kept as string, nullable |

## What was reused

TSQ Bot reuses **ideas and adapted portions** of `Services/LiquipediaService.cs` and `Services/VrsService.cs`
(LPDB query shape `[[game::cs2]]` + date conditions, the PHP `[]`-vs-`{}` stream/links handling, stream de-duplication,
reading Valve's standings markdown). Those files carry a header naming the source, commit and license and stating
that they were modified:

- `src/ToroSquad.Modules.Esports/Providers/Liquipedia/LiquipediaParser.cs`
- `src/ToroSquad.Modules.Esports/Providers/Valve/ValveStandings.cs`

`CountryMapper.cs`, controllers, Swagger and the web host were **not** reused (TSQ Bot runs the data code in-process;
no public HTTP endpoint is needed — see docs/ARCHITECTURE.md).

Nothing from BOT-Greg-Policies (texts, logo, icon, CSS) was copied. TSQ Bot's policy texts are original drafts
(`docs/policies/`).

## License decision

Because adapted AGPL-3.0 code is included, **TSQ Bot as a whole is licensed AGPL-3.0-only** (`LICENSE`, verbatim
GNU AGPL v3 text, identical to upstream `LICENSE.txt`, SHA-256 prefix `6f1e622c82a38007`). Consequences, implemented:

- `/bot about` lists attributions (upstream repo, Liquipedia CC BY-SA 3.0, Valve VRS, Discord.Net MIT).
- Canonical source repository: **https://github.com/Torokal/TSQ-Bot** — the shipped default of `Bot:SourceUrl`. It is
  currently **private**; before the bot is offered to anyone else it must be public again, or the running commit's
  `Export-Source.ps1` archive must be published and `Bot:SourceUrl` pointed at it.
- `/bot source` points users of the running instance to its **Corresponding Source** via `Bot:SourceUrl`; startup in
  Gateway mode is refused without it unless `Bot:AllowMissingSourceUrlForPrivateTesting=true`.
- `scripts/Export-Source.ps1` produces the source archive of the exact committed version (with build instructions,
  without secrets/user data). A link to the *upstream* repository alone does **not** satisfy this.
- The running build embeds its git commit in the informational version (`/bot about`).

If the owner prefers a different license, the two derived files above must be rewritten from scratch first. This is a
technical record, not legal advice.

## Third-party data

| Source | Terms (see docs/PROVIDERS.md) | How TSQ Bot complies |
|---|---|---|
| Liquipedia (LiquipediaDB API) | CC BY-SA 3.0; attribution + link required; API key by approval; 60 req/h baseline | Footer "Kaynak: Liquipedia (CC BY-SA 3.0)" + source link on every message; per-table request budget |
| Valve regional standings | Public GitHub repo, **no license file**; data credits HLTV.org | Displayed with source, date and attribution only; no bulk redistribution; synthetic fixtures in tests |

## Contact with the upstream developer

A short English draft asking about the V2 plan and asset/collaboration permission is in
`docs/drafts/upstream-contact.md`. **It has not been sent** (sending requires the owner's explicit approval). Local work
does not depend on a reply.
