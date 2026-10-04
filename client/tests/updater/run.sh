#!/usr/bin/env bash
# Updater test: only a release signed by a key in RELEASE_KEYS and newer than
# the installed add-on is accepted (tampered, unsigned, older and foreign-key
# packages are refused). Uses throwaway keys; needs Python's cryptography.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADDON="$(cd "$HERE/../../godot/addons/yhde" && pwd)"
GODOT="${GODOT:?set GODOT to a Godot 4.7 editor binary}"
WORK="${WORK:-$(mktemp -d)}"
PY="${PYTHON:-python}"
rm -rf "$WORK"; mkdir -p "$WORK/addons"
printf 'config_version=5\n[application]\nconfig/name="yhde-updater-test"\n' > "$WORK/project.godot"
cp -r "$ADDON" "$WORK/addons/yhde"
rm -rf "$WORK/addons/yhde/bin" "$WORK/addons/yhde/yhde.gdextension"* # only the scripts are tested
"$PY" "$HERE/make_packages.py" "$ADDON" "$WORK/packages"
# The test copy trusts the test key only.
"$PY" - "$WORK/addons/yhde/ui/updater.gd" "$WORK/packages/key.pem" <<'PY'
import re, sys
path, key = sys.argv[1], open(sys.argv[2]).read()
s = open(path, encoding="utf-8").read()
s, n = re.subn(r'const RELEASE_KEYS: Array\[String\] = \[.*?\n\]\n', 'const RELEASE_KEYS: Array[String] = [\n\t"""' + key + '""",\n]\n', s, flags=re.S)
assert n == 1, "RELEASE_KEYS not found"
open(path, "w", encoding="utf-8").write(s)
PY
cp "$HERE/check.gd" "$WORK/"
cd "$WORK"
"$GODOT" --headless --path . -s res://check.gd 2>&1 | grep -E "^ok|^FAIL|^updater:|SCRIPT ERROR|Parse Error"
exit "${PIPESTATUS[0]}"
