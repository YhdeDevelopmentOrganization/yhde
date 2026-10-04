#!/usr/bin/env bash
# End-to-end test: two headless Godot editors collaborate through a real YHDE
# server, then their project states are compared.
#
# Requirements
#   GODOT      path to a Godot 4.7.x editor binary
#   YHDE_URL   server websocket url (default ws://127.0.0.1:5000/ws)
#   YHDE_BLEND_FILE, YHDE_BLENDER  optional: a .blend file and the Blender
#              executable. Editor A imports the .blend with Blender, editor B
#              (no Blender) must use A's import result without errors.
#              Use a self-contained Godot copy (an empty ._sc_ file next to the
#              binary): the editors change their Blender path setting.
#   YHDE_KEY   server access key, if the server requires one
#   WORK       scratch directory (default: a new temp dir)
#   The server's default branch must be empty (fresh database): the test
#   projects are new, so they replay the whole branch history.
#   The native core must be built into client/godot/addons/yhde/bin.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 editor binary}"
YHDE_URL="${YHDE_URL:-ws://127.0.0.1:5000/ws}"
WORK="${WORK:-$(mktemp -d)}"
TIMEOUT="${TIMEOUT:-1800}"

echo "work dir: $WORK"
rm -rf "$WORK/template" "$WORK/proj_a" "$WORK/proj_b" "$WORK/shared"
mkdir -p "$WORK/template/addons" "$WORK/shared"

cat > "$WORK/template/project.godot" <<'EOF'
config_version=5

[application]
config/name="yhde-e2e"

[editor_plugins]
enabled=PackedStringArray("res://addons/yhde/plugin.cfg", "res://addons/yhde_e2e/plugin.cfg")
EOF
cp "$HERE/setup_scenes.gd" "$WORK/template/"
(cd "$WORK/template" && "$GODOT" --headless --path . -s res://setup_scenes.gd 2>&1 | grep -E "scenes ready|ERROR" || true)
rm -f "$WORK/template/setup_scenes.gd" "$WORK/template/setup_scenes.gd.uid"

for p in a b; do
  cp -r "$WORK/template" "$WORK/proj_$p"
  ln -sfn "$ADDON" "$WORK/proj_$p/addons/yhde"
  mkdir -p "$WORK/proj_$p/addons/yhde_e2e"
  cp "$HERE/driver/plugin.gd" "$WORK/proj_$p/addons/yhde_e2e/"
  printf '[plugin]\nname="yhde_e2e"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/proj_$p/addons/yhde_e2e/plugin.cfg"
  # First launch imports the project; keep it out of the measured run. Use
  # --import: "--editor --quit" crashes Godot 4.7.2 when it first loads a GDExtension.
  (cd "$WORK/proj_$p" && timeout 300 "$GODOT" --headless --import > /dev/null 2>&1) || { echo "first import failed"; exit 1; }
done

run_editor() {
  local role=$1
  (cd "$WORK/proj_$role" && YHDE_AUDIT=1 YHDE_ROLE=$role YHDE_SHARED="$WORK/shared" YHDE_URL="$YHDE_URL" \
    timeout "$TIMEOUT" "$GODOT" --headless --editor --path . > "$WORK/$role.log" 2>&1)
}

run_editor a & PA=$!
run_editor b & PB=$!
set +e
wait $PA; RA=$?
wait $PB; RB=$?
set -e

echo "--- editor A (exit $RA) ---"; grep -E "^\[e2e|SCRIPT ERROR|FAIL" "$WORK/a.log" | tail -40 || true
echo "--- editor B (exit $RB) ---"; grep -E "^\[e2e|SCRIPT ERROR|FAIL" "$WORK/b.log" | tail -40 || true

if [[ ! -f "$WORK/shared/a.dump.json" || ! -f "$WORK/shared/b.dump.json" ]]; then
  echo "dumps missing"; exit 1
fi
python3 "$HERE/compare_dumps.py" "$WORK/shared/a.dump.json" "$WORK/shared/b.dump.json"

# Files that arrive from a teammate must load cleanly: an error about a file
# that "works anyway" means it was used before it was written or imported.
LOAD_ERRORS=0
for e in a b; do
  if grep -E "ERROR: .*(Failed loading resource|Can't open dependency|No loader found|Cannot open file|invalid UID|Can't find file).*(art/|materials/|scripts/mover2|data/|models/)" "$WORK/$e.log"; then
    echo "editor $e: load errors for shared files (above)"; LOAD_ERRORS=1
  fi
done
# B has no Blender: it must never have tried to import the .blend itself.
if [[ -n "${YHDE_BLEND_FILE:-}" ]] && grep -E "Blender path is invalid|blender" -i "$WORK/b.log" | grep -i error; then
  echo "editor B tried to run Blender (above)"; LOAD_ERRORS=1
fi
[[ $RA -eq 0 && $RB -eq 0 && $LOAD_ERRORS -eq 0 ]]
