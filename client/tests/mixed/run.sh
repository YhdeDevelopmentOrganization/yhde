#!/usr/bin/env bash
# Mixed versions: A on a newer Godot sets properties the older Godot of B does
# not have. B must take the rest, send nothing back, and A must keep its
# values when B edits and saves the same scenes.
#
#   GODOT_A   the newer Godot (4.8)
#   GODOT_B   the older Godot (4.7)
#   YHDE_URL  server websocket url (default ws://127.0.0.1:5000/ws)
#   YHDE_KEY  server access key, if the server requires one
#   WORK      scratch directory (default: a new temp dir)
# The server's default branch must be empty (fresh database).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT_A="${GODOT_A:?set GODOT_A to the newer Godot}"
GODOT_B="${GODOT_B:?set GODOT_B to the older Godot}"
export YHDE_URL="${YHDE_URL:-ws://127.0.0.1:5000/ws}"
WORK="${WORK:-$(mktemp -d)}"
TIMEOUT="${TIMEOUT:-600}"

echo "work dir: $WORK"
rm -rf "$WORK/template" "$WORK/proj_a" "$WORK/proj_b" "$WORK/shared"
mkdir -p "$WORK/template/addons" "$WORK/shared"
cat > "$WORK/template/project.godot" <<'EOF'
config_version=5

[application]
config/name="yhde-mixed"

[editor_plugins]
enabled=PackedStringArray("res://addons/yhde/plugin.cfg", "res://addons/yhde_mixed/plugin.cfg")
EOF
# Scenes made by the older Godot, as a team that starts on it.
cat > "$WORK/template/setup.gd" <<'EOF'
extends SceneTree
func _init() -> void:
	for scene in ["open", "closed"]:
		var root := Node3D.new()
		root.name = scene.capitalize()
		var sun := DirectionalLight3D.new()
		sun.name = "Sun"
		root.add_child(sun)
		sun.owner = root
		var text := Label3D.new()
		text.name = "Text"
		text.text = "start"
		root.add_child(text)
		text.owner = root
		var ui := Label.new()
		ui.name = "Ui"
		root.add_child(ui)
		ui.owner = root
		var ps := PackedScene.new()
		ps.pack(root)
		ResourceSaver.save(ps, "res://%s.tscn" % scene)
		root.free()
	print("scenes ready")
	quit()
EOF
(cd "$WORK/template" && "$GODOT_B" --headless --path . -s res://setup.gd 2>&1 | grep -E "scenes ready|ERROR" || true)
rm -f "$WORK/template/setup.gd" "$WORK/template/setup.gd.uid"
for p in a b; do
  G=$GODOT_A; [[ $p == b ]] && G=$GODOT_B
  cp -r "$WORK/template" "$WORK/proj_$p"
  ln -sfn "$ADDON" "$WORK/proj_$p/addons/yhde"
  mkdir -p "$WORK/proj_$p/addons/yhde_mixed"
  cp "$HERE/plugin.gd" "$WORK/proj_$p/addons/yhde_mixed/"
  printf '[plugin]\nname="yhde_mixed"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/proj_$p/addons/yhde_mixed/plugin.cfg"
  (cd "$WORK/proj_$p" && timeout 300 "$G" --headless --import > /dev/null 2>&1) \
    || [[ -f "$WORK/proj_$p/.godot/extension_list.cfg" ]] || { echo "first import failed"; exit 1; }
done

editor() {
  local role=$1 G=$2
  (cd "$WORK/proj_$role" && YHDE_AUDIT=1 YHDE_ROLE=$role YHDE_SHARED="$WORK/shared" \
    timeout "$TIMEOUT" "$G" --headless --editor --path . > "$WORK/$role.log" 2>&1)
}
set +e
editor a "$GODOT_A" & PA=$!
editor b "$GODOT_B" & PB=$!
wait $PA; RA=$?
wait $PB; RB=$?
set -e

grep -hE "^\[mixed" "$WORK/a.log" "$WORK/b.log" || true
echo "--- errors on B (4.7) ---"
grep -E "SCRIPT ERROR|^ERROR" "$WORK/b.log" | grep -v "leaked at exit\|still in use at exit" | head -20 || true
echo "exit codes: A $RA, B $RB"
[[ $RA -eq 0 && $RB -eq 0 ]] && echo "MIXED OK" || { echo "MIXED FAILED"; exit 1; }
