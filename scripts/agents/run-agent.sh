#!/usr/bin/env bash
# Launch one Cline worker on one task, in its own git worktree + state dir.
# Usage: scripts/agents/run-agent.sh <task-id>      (task file: docs/agents/tasks/<task-id>.md)
# Exit: 0 ok | 1 bad usage/setup | 2 no submit_and_exit seen | 3 no commits on branch | 4 cline failed
set -euo pipefail
export PATH="$HOME/.npm-global/bin:$HOME/.local/bin:$PATH"

TASK_ID="${1:?usage: run-agent.sh <task-id>}"
REPO="${OL_REPO:-/home/rolkka/openlot}"
TEMPLATE="${CLINE_TEMPLATE:-$HOME/.cline-template}"   # a data dir that already holds a working login
RUNS="${CLINE_RUNS:-$HOME/.cline-runs}"
PROVIDER="${CLINE_PROVIDER:-cline-pass}"
MODEL="${CLINE_MODEL:-cline-pass/deepseek-v4.1-flash}"
TIMEOUT="${CLINE_TIMEOUT:-1800}"
BASE="${OL_BASE_BRANCH:-main}"

TASK_FILE="$REPO/docs/agents/tasks/$TASK_ID.md"
WT="$(dirname "$REPO")/ol-wt-$TASK_ID"
BRANCH="agent/$TASK_ID"
DATA="$RUNS/$TASK_ID"
LOGDIR="$REPO/../ol-agent-logs"
LOG="$LOGDIR/$TASK_ID.json"

[ -f "$TASK_FILE" ] || { echo "missing task file: $TASK_FILE" >&2; exit 1; }
[ -d "$TEMPLATE" ]  || { echo "missing template data dir: $TEMPLATE (copy a logged-in --data-dir there)" >&2; exit 1; }
command -v cline >/dev/null || { echo "cline not on PATH" >&2; exit 1; }
mkdir -p "$RUNS" "$LOGDIR"

# Worktree + branch (reuse if it already exists, e.g. on retry)
if [ ! -d "$WT" ]; then
  git -C "$REPO" worktree add "$WT" -b "$BRANCH" "$BASE"
fi

# Fresh state dir per run. rm first, otherwise cp nests the copy inside it.
rm -rf "$DATA"
cp -r "$TEMPLATE" "$DATA"

PROMPT="Act mode. Do not ask for approval; nobody is watching. Read .clinerules first, then do exactly this task:

$(cat "$TASK_FILE")"

set +e
cline -y -P "$PROVIDER" -m "$MODEL" -c "$WT" --data-dir "$DATA" -t "$TIMEOUT" --json "$PROMPT" > "$LOG"
CLINE_STATUS=$?
set -e

SUBMITTED=no
grep -q '"toolName":"submit_and_exit"' "$LOG" && SUBMITTED=yes
AHEAD="$(git -C "$WT" rev-list --count "$BASE..HEAD")"
DIRTY="$(git -C "$WT" status --short | wc -l)"

echo "task=$TASK_ID cline_exit=$CLINE_STATUS submit_and_exit=$SUBMITTED commits_ahead=$AHEAD uncommitted_files=$DIRTY log=$LOG"
git -C "$WT" diff --stat "$BASE..HEAD" | tail -n 20

[ "$CLINE_STATUS" -eq 0 ]   || exit 4
[ "$SUBMITTED" = yes ]      || exit 2
[ "$AHEAD" -gt 0 ]          || exit 3
exit 0
