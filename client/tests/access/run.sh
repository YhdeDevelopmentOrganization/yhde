#!/usr/bin/env bash
# Access-key test: one headless editor against a server that requires a key.
#
# Requirements
#   GODOT      path to a Godot 4.7.x editor binary
#   YHDE_KEY   the key the server was started with (Yhde:AccessKey)
#   YHDE_URL   server websocket url (default ws://127.0.0.1:5000/ws)
#   The native core must be built into client/godot/addons/yhde/bin.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 editor binary}"
export YHDE_KEY="${YHDE_KEY:?set YHDE_KEY to the server access key}"
export YHDE_URL="${YHDE_URL:-ws://127.0.0.1:5000/ws}"
WORK="${WORK:-$(mktemp -d)}"

rm -rf "$WORK/proj"
mkdir -p "$WORK/proj/addons/yhde_access"
cat > "$WORK/proj/project.godot" <<'CFG'
config_version=5

[application]
config/name="yhde-access"

[editor_plugins]
enabled=PackedStringArray("res://addons/yhde/plugin.cfg", "res://addons/yhde_access/plugin.cfg")
CFG
ln -sfn "$ADDON" "$WORK/proj/addons/yhde"
cp "$HERE/plugin.gd" "$WORK/proj/addons/yhde_access/"
printf '[plugin]\nname="yhde_access"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/proj/addons/yhde_access/plugin.cfg"
# First launch imports the project (--import: "--editor --quit" crashes Godot
# 4.7.2 on the run that first loads a GDExtension).
(cd "$WORK/proj" && YHDE_KEY= timeout 300 "$GODOT" --headless --import > /dev/null 2>&1) || { echo "first import failed"; exit 1; }

set +e
(cd "$WORK/proj" && timeout 120 "$GODOT" --headless --editor --path . > "$WORK/access.log" 2>&1)
RC=$?
set -e
grep -E "^\[access|SCRIPT ERROR" "$WORK/access.log" || true
exit $RC
