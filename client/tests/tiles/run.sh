#!/usr/bin/env bash
# Tile test: TileSet / TileMapLayer / TileMap edits mirrored between two sync
# documents (no server). Requires the native core built with -DYHDE_DIAGNOSTICS=ON.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 editor binary}"
WORK="${WORK:-$(mktemp -d)}"
rm -rf "$WORK"; mkdir -p "$WORK/addons/yhde_tiles"
cat > "$WORK/project.godot" <<'P'
config_version=5
[application]
config/name="yhde-tiles-test"
P
cp -r "$ADDON" "$WORK/addons/yhde"
cp "$HERE/plugin.gd" "$WORK/addons/yhde_tiles/"
printf '[plugin]\nname="yhde_tiles"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/addons/yhde_tiles/plugin.cfg"
cp "$HERE/setup.gd" "$HERE/setup2.gd" "$WORK/"
cd "$WORK"
"$GODOT" --headless --path . -s res://setup.gd 2>&1 | grep -E "ready|ERROR" || true
# --import, not "--editor --quit": that crashes Godot 4.7.2 when it first loads a GDExtension.
timeout 300 "$GODOT" --headless --import > /dev/null 2>&1 || { echo "first import failed"; exit 1; }
"$GODOT" --headless --path . -s res://setup2.gd 2>&1 | grep -E "ready|ERROR" || true
printf '\n[editor_plugins]\nenabled=PackedStringArray("res://addons/yhde_tiles/plugin.cfg")\n' >> project.godot
set +e
timeout "${TIMEOUT:-600}" "$GODOT" --headless --editor --path . > "$WORK/tiles.log" 2>&1
code=$?
grep -E "^ok|^FAIL|^       |^tiles:|NO DIAGNOSTICS|SCRIPT ERROR|Parse Error" "$WORK/tiles.log"
exit $code
