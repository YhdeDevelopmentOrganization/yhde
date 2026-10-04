#!/usr/bin/env bash
# Runs the two-editor e2e test against the live YHDE server, in a separate
# ownerless test project, with the .blend phase: editor A has Blender,
# editor B doesn't.
#
#   bash client/tests/e2e/run-on-server.sh
#
# Settings (environment, all optional):
#   SERVER    ssh host of the server, e.g. root@<ip> (required)
#   URL       its websocket url, e.g. wss://yhde.<yourdomain>/ws (required)
#   GODOT     Godot 4.7 editor binary, in a self-contained copy (._sc_ next to it)
#   BLENDER   Blender executable
#   BLEND     a .blend file to share
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${SERVER:?set SERVER to the ssh host of the server, e.g. root@<ip>}"
: "${URL:?set URL to its websocket url, e.g. wss://yhde.<yourdomain>/ws}"
: "${GODOT:?set GODOT to a Godot 4.7 editor binary (self-contained copy)}"
: "${BLENDER:?set BLENDER to blender.exe}"
: "${BLEND:?set BLEND to a .blend file}"

echo "Making a test project on $SERVER…"
read -r PROJECT BRANCH < <(ssh -o BatchMode=yes "$SERVER" "cd /opt/yhde/deploy && docker compose exec -T db psql -U yhde -d yhde -tAq -c \"WITH p AS (INSERT INTO projects (name) VALUES ('e2e-test-' || to_char(now(),'YYYYMMDD-HH24MI')) RETURNING project_id), b AS (INSERT INTO branches (project_id, name) SELECT project_id, 'main' FROM p RETURNING project_id, branch_id) SELECT project_id || ' ' || branch_id FROM b;\"")
[[ -n "${BRANCH:-}" ]] || { echo "could not make the test project"; exit 1; }
echo "project $PROJECT, branch $BRANCH"

# The access key goes straight into the environment, never printed.
YHDE_KEY="$(ssh -o BatchMode=yes "$SERVER" "sed -n 's/^ACCESS_KEY=//p' /opt/yhde/deploy/.env | tail -1")"
[[ -n "$YHDE_KEY" ]] || { echo "no access key on the server"; exit 1; }

# run.sh calls python3; on Windows that name is often the Store stub.
if ! python3 -c "" 2>/dev/null; then
  SHIM="$(mktemp -d)"; printf '#!/bin/sh\nexec python "$@"\n' > "$SHIM/python3"; chmod +x "$SHIM/python3"; PATH="$SHIM:$PATH"
fi

export GODOT YHDE_KEY YHDE_URL="$URL" YHDE_PROJECT="$PROJECT" YHDE_BRANCH="$BRANCH" \
  YHDE_BLENDER="$BLENDER" YHDE_BLEND_FILE="$BLEND"
set +e
bash "$HERE/run.sh"
RESULT=$?
set -e
echo
echo "Test project left on the server for inspection: e2e-test (project $PROJECT). It has no owner, so no dashboard shows it."
[[ $RESULT -eq 0 ]] && echo "PASSED" || echo "FAILED (exit $RESULT)"
exit $RESULT
