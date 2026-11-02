#!/usr/bin/env bash
# Catch-up test: an editor that lost its sync state (a sudden power loss) or
# joins for the first time replays a long history. It must end with the same
# files, without writing old versions over newer ones, and a scene that is not
# open must not be saved for every few operations. With BASELINE set to another
# build of the native core, the same runs are made with it for comparison.
#
#   GODOT     Godot 4.7 editor binary, in a self-contained copy (._sc_ next to it)
#   YHDE_URL  default ws://127.0.0.1:5000/ws, a server with an empty default branch
#   BASELINE  optional: a libyhde to compare with
#   NODES     nodes in the test scene (default 3000)
#   WORK      scratch directory (default: a new temp dir)
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?}"
export YHDE_URL="${YHDE_URL:-ws://127.0.0.1:5000/ws}"
WORK="${WORK:-$(mktemp -d)}"
LIB="$(ls "$ADDON"/bin/libyhde.* | head -1)"
if python3 -c "" 2>/dev/null; then PY=python3; else PY=python; fi # on Windows python3 is often the Store stub

rm -rf "$WORK" && mkdir -p "$WORK/template/addons/yhde_cu" "$WORK/results"
cp "$HERE/plugin.gd" "$WORK/template/addons/yhde_cu/"
printf '[plugin]\nname="yhde_cu"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/template/addons/yhde_cu/plugin.cfg"
printf 'config_version=5\n[application]\nconfig/name="yhde-catchup"\n[editor_plugins]\nenabled=PackedStringArray("res://addons/yhde/plugin.cfg", "res://addons/yhde_cu/plugin.cfg")\n' > "$WORK/template/project.godot"
cat > "$WORK/template/setup.gd" <<'EOF'
extends SceneTree
func _init() -> void:
	var root := Node2D.new()
	root.name = "Big"
	for i in int(OS.get_environment("YHDE_CU_NODES")):
		var n := Node2D.new()
		n.name = "N%d" % i
		root.add_child(n)
		n.owner = root
	var rebuilt := Node2D.new()
	rebuilt.name = "Rebuilt"
	root.add_child(rebuilt)
	rebuilt.owner = root
	for i in 30:
		var n := Node2D.new()
		n.name = "R%02d" % i
		rebuilt.add_child(n)
		n.owner = root
	var ps := PackedScene.new()
	ps.pack(root)
	ResourceSaver.save(ps, "res://big.tscn")
	root.free()
	FileAccess.open("res://code.gd", FileAccess.WRITE).store_string("extends Node\nconst VERSION := -1\n")
	var image := Image.create(256, 256, false, Image.FORMAT_RGB8)
	image.save_png("res://texture.png")
	quit()
EOF
(cd "$WORK/template" && YHDE_CU_NODES="${NODES:-3000}" "$GODOT" --headless --path . -s res://setup.gd > /dev/null 2>&1) || true
rm -f "$WORK/template/setup.gd"*

# make <project dir> <library>: the add-on in its own copy, with that library.
install_addon() {
  rm -rf "$1/addons/yhde"
  cp -r "$ADDON" "$1/addons/yhde"
  cp "$2" "$1/addons/yhde/bin/$(basename "$LIB")"
}
import() { (cd "$1" && timeout 300 "$GODOT" --headless --import > /dev/null 2>&1) || { echo "import failed: $1"; exit 1; }; }

cp -r "$WORK/template" "$WORK/writer"
install_addon "$WORK/writer" "${WRITER_LIB:-$LIB}"
import "$WORK/writer"
echo "making the history..."
(cd "$WORK/writer" && YHDE_CU_ROLE=writer timeout 900 "$GODOT" --headless --editor --path . > "$WORK/writer.log" 2>&1)
grep -E "^\[catchup" "$WORK/writer.log" | tail -2 | cut -c1-120
# Optional: lengthen the history (a command run once the writer is done).
if [[ -n "${AFTER_WRITER:-}" ]]; then eval "$AFTER_WRITER"; fi

# join <name> <library> <lost|fresh>
join() {
  local dir="$WORK/join_$1"
  rm -rf "$dir"
  if [[ $3 == lost ]]; then
    # The state of an editor whose files are final but whose records are gone.
    cp -r "$WORK/writer" "$dir"
    # The layout goes too: the scene must not come back open in a tab.
    rm -rf "$dir/.godot/editor" "$dir/.godot/yhde"
  else
    cp -r "$WORK/template" "$dir"
  fi
  install_addon "$dir" "$2"
  [[ $3 == lost ]] || import "$dir"
  (cd "$dir" && YHDE_CU_ROLE=joiner YHDE_CU_OUT="$WORK/results/$1.json" timeout 1900 "$GODOT" --headless --editor --path . > "$WORK/$1.log" 2>&1) || true
  if [[ -f "$WORK/results/$1.json" ]]; then echo "$1: $(cat "$WORK/results/$1.json")"; else echo "$1: no result"; tail -5 "$WORK/$1.log"; fi
}

if [[ -n "${BASELINE:-}" ]]; then
  join old_lost "$BASELINE" lost
  join old_fresh "$BASELINE" fresh
fi
join new_lost "$LIB" lost
join new_fresh "$LIB" fresh
# Small batches: a rebuilt node and the deletion of its namesake come apart.
YHDE_OPS_PER_FRAME=8 join new_small "$LIB" fresh

# A power loss empties the newest records: the backup must save the replay.
crash() {
  local dir="$WORK/join_new_lost" state="$WORK/join_new_lost/.godot/editor/yhde"
  (cd "$dir" && YHDE_CU_STAY=40 YHDE_CU_ROLE=joiner YHDE_CU_OUT="$WORK/stay.json" timeout 300 "$GODOT" --headless --editor --path . > "$WORK/stay.log" 2>&1) || true
  for f in "$state"/*.cache.json "$state"/*.assets.json; do : > "$f"; done
  (cd "$dir" && YHDE_CU_ROLE=joiner YHDE_CU_OUT="$WORK/results/new_crash.json" timeout 600 "$GODOT" --headless --editor --path . > "$WORK/new_crash.log" 2>&1) || true
  echo "new_crash: $(cat "$WORK/results/new_crash.json")"
}
crash

"$PY" - "$WORK" <<'PY'
import json, os, sys, hashlib
work = sys.argv[1]
def sha(p): return hashlib.sha256(open(p, "rb").read()).hexdigest()
want = {f"res://{n}": sha(os.path.join(work, "writer", n)) for n in ("code.gd", "texture.png")}
ok = True
for name in sorted(os.listdir(os.path.join(work, "results"))):
    r = json.load(open(os.path.join(work, "results", name)))
    same = all(r["hashes"][k] == v for k, v in want.items())
    names = sorted(r["rebuilt_names"].split(","))  # a stand-in looks alike: R00 becomes R30
    names_ok = names == [f"R{i:02d}" for i in range(30)]
    print(f"{name[:-5]:12} {r['seconds']:8.1f} s  longest frame {r['longest_frame_ms']:6} ms  file changes {r['file_changes']}  same files {same}  scene {r['scene_position_sum']}  names kept {names_ok}")
    if name.startswith("new"):
        ok = ok and same and names_ok
        if name.startswith(("new_lost", "new_crash")):
            ok = ok and r["file_changes"]["res://code.gd"] == 0
        if name.startswith("new_crash"):
            ok = ok and r["seconds"] < 10
print("CATCHUP", "OK" if ok else "FAILED")
sys.exit(0 if ok else 1)
PY
