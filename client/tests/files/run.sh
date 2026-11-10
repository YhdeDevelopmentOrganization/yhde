#!/usr/bin/env bash
# Files test: what one editor cannot write, and editors that stop in the
# middle of a transfer, must never cost a teammate their files.
#
#   a  B cannot write a folder; A adds files there. A keeps them, B sends no
#      deletion and says the files are not written here, then writes them
#      once it can.
#   b  B cannot overwrite a file it has; its copy is then touched. B never
#      sends its old bytes back.
#   j  B's editor dies while it writes a teammate's add-on, then starts again.
#      B deletes nothing for A, says Godot closed unexpectedly, and ends with
#      the whole add-on.
#   k  A restarts its editor while B is sending files. Nothing is lost or
#      deleted on either side.
#
#   GODOT     Godot 4.7 or 4.8 editor binary
#   YHDE_URL  server websocket url (default ws://127.0.0.1:5000/ws)
#   YHDE_KEY  server access key, if the server requires one
#   WORK      scratch directory (default: a new temp dir)
# The server's default branch must be empty (fresh database). The native core
# must be built with -DYHDE_DIAGNOSTICS=ON (phase j stops B on purpose).
# Linux and macOS only: phases a and b use folder permissions.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 or 4.8 editor binary}"
export YHDE_URL="${YHDE_URL:-ws://127.0.0.1:5000/ws}"
WORK="${WORK:-$(mktemp -d)}"
TIMEOUT="${TIMEOUT:-900}"

echo "work dir: $WORK"
[[ -d "$WORK/proj_b/locked" ]] && chmod -R u+w "$WORK/proj_b" 2>/dev/null || true
rm -rf "$WORK/template" "$WORK/proj_a" "$WORK/proj_b" "$WORK/shared"
mkdir -p "$WORK/template/addons" "$WORK/shared"
cat > "$WORK/template/project.godot" <<'EOF'
config_version=5

[application]
config/name="yhde-files"

[editor_plugins]
enabled=PackedStringArray("res://addons/yhde/plugin.cfg", "res://addons/yhde_files/plugin.cfg")
EOF
# A project of some size: a few files going missing is below the share that
# makes YHDE ask first, as in a real project.
mkdir -p "$WORK/template/base"
for i in $(seq -w 0 59); do echo "base $i" > "$WORK/template/base/b$i.txt"; done
for p in a b; do
  cp -r "$WORK/template" "$WORK/proj_$p"
  ln -sfn "$ADDON" "$WORK/proj_$p/addons/yhde"
  mkdir -p "$WORK/proj_$p/addons/yhde_files"
  cp "$HERE/plugin.gd" "$WORK/proj_$p/addons/yhde_files/"
  printf '[plugin]\nname="yhde_files"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/proj_$p/addons/yhde_files/plugin.cfg"
  # Godot can crash as it quits the run that first loads a GDExtension, after
  # the import is done: only a missing import counts.
  (cd "$WORK/proj_$p" && timeout 300 "$GODOT" --headless --import > /dev/null 2>&1) \
    || [[ -f "$WORK/proj_$p/.godot/extension_list.cfg" ]] || { echo "first import failed"; exit 1; }
done
# Phase a: a folder B cannot write into.
mkdir -p "$WORK/proj_b/locked"
chmod 555 "$WORK/proj_b/locked"

editor() { # role run [extra env]
  local role=$1 run=$2; shift 2
  (cd "$WORK/proj_$role" && env YHDE_AUDIT=1 YHDE_ROLE=$role YHDE_RUN=$run YHDE_SHARED="$WORK/shared" "$@" \
    timeout "$TIMEOUT" "$GODOT" --headless --editor --path . >> "$WORK/$role.log" 2>&1)
}
wait_file() { for _ in $(seq 1 6000); do [[ -f "$1" ]] && return 0; sleep 0.1; done; echo "timeout: $1"; return 1; }

set +e
editor a 1 & PA=$!
editor b 1 & PB=$!
# Phase j: B stops by itself in the middle of writing A's add-on.
wait $PB; RB1=$?
touch "$WORK/shared/harness.b_stopped"
editor b 2 & PB=$!
# Phase k: A restarts itself (the editor's own restart starts a new process,
# which writes a.k_back). Its first process ends here.
wait $PA; RA1=$?
wait_file "$WORK/shared/a.done"
wait $PB; RB2=$?
set -e
sleep 1
pkill -f -- "--path $WORK/proj_a" 2>/dev/null || true
chmod -R u+w "$WORK/proj_b" 2>/dev/null || true

grep -hE "^\[files" "$WORK/a.log" "$WORK/b.log" || true
echo "exit codes: A first run $RA1, B first run $RB1 (stopped on purpose), B second run $RB2"
python3 - "$WORK/shared" <<'PY'
import json, os, sys
d = sys.argv[1]
failures = []
for name in ("a", "b"):
    p = os.path.join(d, name + ".failures.json")
    if not os.path.exists(p):
        failures.append(name.upper() + ": no result")
        continue
    failures += json.load(open(p))
for f in failures:
    print("FAIL", f)
print("FILES", "OK" if not failures else "FAILED (%d)" % len(failures))
sys.exit(1 if failures else 0)
PY
