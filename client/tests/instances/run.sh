#!/usr/bin/env bash
# Instance stress test: teammate edits of part.tscn reach an
# editor that keeps a node inside a refreshed instance selected and in the
# inspector. Needs a running server with an empty default branch, like e2e.
#   GODOT, YHDE_URL (default ws://127.0.0.1:5000/ws), YHDE_KEY, WORK,
#   ROUNDS, GAP. One run per empty branch: reset the server's database
#   before running it again.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E="$HERE/../e2e"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 editor binary}"
export YHDE_URL="${YHDE_URL:-ws://127.0.0.1:5000/ws}"
WORK="${WORK:-$(mktemp -d)}"
TIMEOUT="${TIMEOUT:-1200}"
STATUS=0
for run in 1; do
  rm -rf "$WORK" && mkdir -p "$WORK/template/addons" "$WORK/shared"
  printf 'config_version=5\n[application]\nconfig/name="yhde-inst"\n[editor_plugins]\nenabled=PackedStringArray("res://addons/yhde/plugin.cfg", "res://addons/yhde_inst/plugin.cfg")\n' > "$WORK/template/project.godot"
  cp "$E2E/setup_scenes.gd" "$WORK/template/"
  (cd "$WORK/template" && "$GODOT" --headless --path . -s res://setup_scenes.gd > /dev/null 2>&1) || true
  rm -f "$WORK/template/setup_scenes.gd"*
  for p in a b; do
    cp -r "$WORK/template" "$WORK/proj_$p"
    ln -sfn "$ADDON" "$WORK/proj_$p/addons/yhde"
    mkdir -p "$WORK/proj_$p/addons/yhde_inst"
    cp "$HERE/plugin.gd" "$WORK/proj_$p/addons/yhde_inst/"
    printf '[plugin]\nname="yhde_inst"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/proj_$p/addons/yhde_inst/plugin.cfg"
    # An AddressSanitizer build can hang as Godot quits: the import is done
    # once .godot/ exists (IMPORT_TIMEOUT shortens the wait).
    (cd "$WORK/proj_$p" && timeout "${IMPORT_TIMEOUT:-300}" "$GODOT" --headless --import > /dev/null 2>&1) || [[ -d "$WORK/proj_$p/.godot/imported" ]] || { echo "first import failed"; exit 1; }
  done
  (cd "$WORK/proj_a" && YHDE_ROLE=a YHDE_SHARED="$WORK/shared" timeout "$TIMEOUT" "$GODOT" --headless --editor --path . > "$WORK/a.log" 2>&1) & PA=$!
  (cd "$WORK/proj_b" && YHDE_ROLE=b YHDE_SHARED="$WORK/shared" timeout "$TIMEOUT" "$GODOT" --headless --editor --path . > "$WORK/b.log" 2>&1) & PB=$!
  set +e; wait $PA; RA=$?; wait $PB; RB=$?; set -e
  echo "run $run: editor A exit $RA, editor B exit $RB"
  grep -E "FAIL|phase|done," "$WORK/b.log" | tail -5 || true
  # A destroyed node seen by the editor: it reads as "Object (not inside tree)".
  if grep -q -E "Object \(not inside tree\)|AddressSanitizer|heap-use-after-free" "$WORK/a.log" "$WORK/b.log"; then
    echo "run $run: the editor used a freed node (above)"; grep -n -E "Object \(not inside tree\)|AddressSanitizer" "$WORK/a.log" "$WORK/b.log" | head; RB=99
  fi
  if [[ $RA -ne 0 || $RB -ne 0 ]]; then
    STATUS=1
    for e in a b; do echo "--- $e.log (tail) ---"; tail -40 "$WORK/$e.log"; done
    break
  fi
done
exit $STATUS
