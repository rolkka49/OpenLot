# Task NNN: <short title>

**Milestone:** §X.Y <name>
**Branch:** agent/NNN-slug (already checked out in your worktree)

## Goal
<2-5 sentences: what must exist when you are done.>

## Allowed files
- <path or glob>
- <path or glob>

## Do NOT touch
- Any file not listed above
- Roadmap status lines, check counts, other suites' expected counts
- Hub files unless listed above: `Builder.cs`, `BuilderScene.cs`, `LotLuaApi.cs`, `LotPropertyRegistry*`

## Acceptance check
- <suite or command that must pass, e.g. `DecalSelfTest` 0 failures>
- <behaviour that must be observable>

## Notes
<Design decisions already made, relevant roadmap text, pitfalls.>

## Finish
Do not ask for approval; nobody is watching. When the acceptance check passes:
1. `git add` only your allowed files and commit to your branch with a clear message.
2. Call `submit_and_exit` with a short summary.
If you are blocked, or the same error happens twice, append a `blocked` entry to `docs/agents/HANDOFF.md`, commit it, and call `submit_and_exit` with `verified: false`.
