# Orchestrator procedure

The orchestrator turns one roadmap milestone into 1-3 disjoint tasks, launches one Cline worker per task, merges the results, and owns all roadmap edits. Workers never coordinate with each other; the task file and the git branch are the only contract.

## 1. Split a milestone

1. Read the milestone and list every file it will touch.
2. Group the files into **disjoint sets**. Usual seams in OpenLot:
   - pure math/data + its `*SelfTest` (no hub files)
   - bound Lua verbs + `LotPropertyRegistry` declaration (hub files)
   - UI / panels / rendering wiring
   - creator doc + design doc
3. Number of agents = number of disjoint groups, **max 3**. One shared hub file means 2 agents or a sequence. A small milestone means 1 agent.
4. **Hub files** (`Builder.cs`, `BuilderScene.cs`, `LotLuaApi.cs`, `LotPropertyRegistry*`, `LotObject.cs`) belong to exactly one task per wave. If two groups need one, run them in successive waves (merge wave 1 first, then create wave 2 worktrees from the new main).
5. Write one task file per group from `TASK_TEMPLATE.md` into `docs/agents/tasks/NNN-slug.md`.

## 2. Launch

`scripts/agents/run-agent.sh NNN-slug` per task (run in parallel with `&` + `wait`). Each gets its own worktree, branch `agent/NNN-slug`, and its own copy of the Cline state dir.

## 3. Judge the result (never trust the exit code alone)

A task is **done** only if all hold:
- the run's JSON contains a `submit_and_exit` event (a plan-only run exits 0 without it)
- the branch has at least one commit ahead of `main`
- the task's acceptance check passes (run it yourself in the worktree)
- the diff touches only the task's allowed files

Otherwise: retry once with the failure appended to the task file; on a second failure mark the task blocked in `HANDOFF.md` and move on.

## 4. Merge

Merge branches one at a time in dependency order, running the affected suites after each. Then run the full headless suite:
`godot --headless --quit-after 8000 res://builder/builder.tscn`
Only after it is green, update the milestone's roadmap status line, check counts and verification text (orchestrator only).

## 5. Rules

- Workers never edit roadmap status lines, check counts or other agents' suites' expected counts.
- Same error twice from a worker = stop, log in `HANDOFF.md`, reassign or escalate.
- `HANDOFF.md` is append-only: `done / blocked / next`.
- Worktrees and branches are cleaned up (`git worktree remove`, `git branch -d`) after a successful merge.
