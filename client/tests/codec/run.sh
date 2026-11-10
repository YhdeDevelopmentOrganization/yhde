#!/usr/bin/env bash
# Codec round-trip over every Variant type and every stored property of every
# instantiable Node/Resource class in the running Godot build.
# Requires the native core built with -DYHDE_DIAGNOSTICS=ON.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 or 4.8 editor binary}"
WORK="${WORK:-$(mktemp -d)}"
mkdir -p "$WORK/addons/yhde_codec"
cat > "$WORK/project.godot" <<'P'
config_version=5
[application]
config/name="yhde-codec-test"
[editor_plugins]
enabled=PackedStringArray("res://addons/yhde_codec/plugin.cfg")
P
ln -sfn "$ADDON" "$WORK/addons/yhde"
cp "$HERE/plugin.gd" "$WORK/addons/yhde_codec/"
printf '[plugin]\nname="yhde_codec"\ndescription=""\nauthor=""\nversion="1"\nscript="plugin.gd"\n' > "$WORK/addons/yhde_codec/plugin.cfg"
cd "$WORK"
# First launch only imports the new project. Godot can crash as it quits this
# run (it is still writing the docs of the new extension classes), after the
# import itself is done: only a missing import counts as a failure.
timeout 300 "$GODOT" --headless --import > /dev/null 2>&1 || [[ -f .godot/extension_list.cfg ]] || { echo "first import failed"; exit 1; }
set +e
timeout "${TIMEOUT:-900}" "$GODOT" --headless --editor --path . > "$WORK/codec.log" 2>&1
code=$?
grep -E "^FAIL|^classes=|NO DIAGNOSTICS" "$WORK/codec.log"
exit $code
