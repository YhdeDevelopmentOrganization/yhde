#!/usr/bin/env bash
# Account test: the sign-in token is never sent over plain http:// to another
# computer, and a redirect is followed by hand to the same server only. Starts
# three local servers (127.0.0.1:18081, 127.0.0.1:18082, 127.0.0.2:18081) and
# checks that the other two receive nothing. On macOS first add the alias:
# sudo ifconfig lo0 alias 127.0.0.2
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 editor binary}"
WORK="${WORK:-$(mktemp -d)}"
PY="${PYTHON:-python3}"
rm -rf "$WORK"; mkdir -p "$WORK/addons"
printf 'config_version=5\n[application]\nconfig/name="yhde-account-test"\n' > "$WORK/project.godot"
cp -r "$ADDON" "$WORK/addons/yhde"
rm -rf "$WORK/addons/yhde/bin" "$WORK/addons/yhde/yhde.gdextension"* # only the scripts are tested
cp "$HERE/check.gd" "$WORK/"
LOG="$WORK/log.jsonl"
"$PY" -I "$HERE/servers.py" "$LOG" > "$WORK/servers.out" 2>&1 &
SERVERS=$!
trap 'kill $SERVERS 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do grep -q ready "$WORK/servers.out" 2>/dev/null && break; sleep 0.1; done
grep -q ready "$WORK/servers.out" || { echo "FAIL servers did not start"; cat "$WORK/servers.out"; exit 1; }
cd "$WORK"
set +e
"$GODOT" --headless --path . -s res://check.gd 2>&1 | grep -E "^ok|^FAIL|^account:|SCRIPT ERROR|Parse Error"
STATUS="${PIPESTATUS[0]}"
set -e
# Nothing may reach the other servers, and the token must never arrive without being asked for.
OTHER=$("$PY" -I - "$LOG" <<'PY'
import json, sys
n = 0
try:
    for line in open(sys.argv[1]):
        r = json.loads(line)
        if r["server"] != "A":
            n += 1
except FileNotFoundError:
    pass
print(n)
PY
)
if [ "$OTHER" != "0" ]; then echo "FAIL $OTHER request(s) reached another server"; STATUS=1; else echo "ok   other servers received 0 requests"; fi
exit "$STATUS"
