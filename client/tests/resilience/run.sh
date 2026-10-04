#!/usr/bin/env bash
# Resilience test: two editors lose the server, keep editing offline, one of
# them quits without saving; after the server returns (and the editor
# restarts) both must hold the same state including every offline edit.
#
#   GODOT         Godot 4.7 editor binary
#   SERVER_START  command that starts the YHDE server (fresh, empty branch)
#   SERVER_STOP   command that stops it (the database must survive)
#   YHDE_URL      default ws://127.0.0.1:5000/ws
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E="$HERE/../e2e"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?}"; SERVER_START="${SERVER_START:?}"; SERVER_STOP="${SERVER_STOP:?}"
export YHDE_URL="${YHDE_URL:-ws://127.0.0.1:5000/ws}"
WORK="${WORK:-$(mktemp -d)}"
rm -rf "$WORK" && mkdir -p "$WORK/template/addons" "$WORK/shared"
printf 'config_version=5\n[application]\nconfig/name="yhde-res"\n[editor_plugins]\nenabled=PackedStringArray("res://addons/yhde/plugin.cfg", "res://addons/yhde_res/plugin.cfg")\n' > "$WORK/template/project.godot"
cp "$E2E/setup_scenes.gd" "$WORK/template/"
(cd "$WORK/template" && "$GODOT" --headless --path . -s res://setup_scenes.gd > /dev/null 2>&1) || true
rm -f "$WORK/template/setup_scenes.gd"*
for p in a b; do
  cp -r "$WORK/template" "$WORK/proj_$p"
  ln -sfn "$ADDON" "$WORK/proj_$p/addons/yhde"
  mkdir -p "$WORK/proj_$p/addons/yhde_res"
  cp "$HERE/plugin.gd" "$WORK/proj_$p/addons/yhde_res/"
  printf '[plugin]\nname="yhde_res"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/proj_$p/addons/yhde_res/plugin.cfg"
  # --import, not "--editor --quit": that crashes Godot 4.7.2 when it first loads a GDExtension.
  (cd "$WORK/proj_$p" && timeout 300 "$GODOT" --headless --import > /dev/null 2>&1) || { echo "first import failed"; exit 1; }
done
editor() { (cd "$WORK/proj_$1" && YHDE_ROLE=$1 YHDE_PHASE=$2 YHDE_SHARED="$WORK/shared" timeout 300 "$GODOT" --headless --editor --path . >> "$WORK/$1.log" 2>&1); }
wait_file() { for _ in $(seq 1 3000); do [[ -f "$1" ]] && return 0; sleep 0.1; done; echo "timeout: $1"; exit 1; }

eval "$SERVER_START"
editor b 1 & PB=$!
editor a 1 & PA=$!
wait_file "$WORK/shared/a.live"; wait_file "$WORK/shared/b.live"
eval "$SERVER_STOP"; touch "$WORK/shared/harness.down"
wait_file "$WORK/shared/a.edited"; wait_file "$WORK/shared/b.edited"
wait $PA || true                      # A quit without saving
sleep 2
eval "$SERVER_START"; touch "$WORK/shared/harness.up"
editor a 2 & PA=$!
set +e; wait $PA; RA=$?; wait $PB; RB=$?; set -e
grep -hE "^\[res" "$WORK/a.log" "$WORK/b.log"
python3 - "$WORK/shared" <<'PY'
import json, sys, os
d = sys.argv[1]
a = json.load(open(os.path.join(d, "a.state.json"))); b = json.load(open(os.path.join(d, "b.state.json")))
print("A:", a); print("B:", b)
ok = a == b and "OfflineA" in a["group_children"] and "OfflineB" in a["group_children"] \
     and a["label"] == "offline b" and a["sprite_position"] == "(111.0, 222.0)"
print("RESILIENCE", "OK" if ok else "FAILED")
sys.exit(0 if ok else 1)
PY
[[ $RA -eq 0 && $RB -eq 0 ]]
