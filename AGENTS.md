# AGENTS.md

Instructions for any coding agent (Claude Code, Codex, others) working on ToroSquad Bot.

1. Start by reading `docs/PROJECT_STATE.md` and `CLAUDE.md`; they are authoritative for state and rules.
2. Only one agent writes to the working tree at a time. A reviewer agent (e.g. Codex, only if the owner authorizes it)
   works read-only and reports findings; the implementing agent applies them.
3. Do not claim tool usage you did not perform, tests you did not run, or live verification you did not do.
4. Keep changes local: no pushes, releases, deployments, bot invites, command registration outside the allow-listed test
   guild, or messages to third parties without explicit owner approval.
5. Gate before handing off: `.\scripts\Test.ps1` must pass; record the result in `docs/PROJECT_STATE.md`.
