#!/usr/bin/env bash
# Code-approval gate and server address rules,
# run through the real native functions.
# Requires the native core built with -DYHDE_DIAGNOSTICS=ON.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 or 4.8 editor binary}"
WORK="${WORK:-$(mktemp -d)}"
mkdir -p "$WORK/addons/yhde_gate"
cat > "$WORK/project.godot" <<'P'
config_version=5
[application]
config/name="yhde-gate-test"
[editor_plugins]
enabled=PackedStringArray("res://addons/yhde_gate/plugin.cfg")
P
rm -rf "$WORK/addons/yhde" # where links are copies (Git Bash), a stale copy would stay
ln -sfn "$ADDON" "$WORK/addons/yhde"
cp "$HERE/plugin.gd" "$WORK/addons/yhde_gate/"
cp "$HERE/path_rules.json" "$WORK/"
printf '[plugin]\nname="yhde_gate"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/addons/yhde_gate/plugin.cfg"
cd "$WORK"
# First launch only imports the new project. Godot can crash as it quits this
# run (it is still writing the docs of the new extension classes), after the
# import itself is done: only a missing import counts as a failure.
timeout 300 "$GODOT" --headless --import > /dev/null 2>&1 || [[ -f .godot/extension_list.cfg ]] || { echo "first import failed"; exit 1; }
set +e
timeout "${TIMEOUT:-300}" "$GODOT" --headless --editor --path . > "$WORK/gate.log" 2>&1
code=$?
grep -E "^ok|^FAIL|^gate:|NO DIAGNOSTICS|SCRIPT ERROR" "$WORK/gate.log"
exit $code
