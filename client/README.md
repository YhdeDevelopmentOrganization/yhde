# YHDE Godot Client

The Godot add-on and its native core. Needs Godot 4.7 (tested on 4.7.2). How
it works: [docs/editor_client.md](../docs/editor_client.md).

```
client/
+-- gdextension/          C++ native core (editor-only GDExtension)
|   +-- src/
|       +-- core/         UUIDs (.NET wire order), MessagePack
|       +-- networking/   wire protocol v1, WebSocket transport
|       +-- operation_queue/  persisted pending ops
|       +-- local_cache/  per-document seq watermarks
|       +-- sync/         value codec, document diff/apply, sync engine
|       +-- presence/     peers and colors
|       +-- yhde_session  the only class visible to scripts
+-- godot/addons/yhde/    editor plugin (GDScript UI only) + yhde.gdextension
+-- tests/
    +-- codec/            every property of every class round-trips
    +-- e2e/              two headless editors collaborate through a server
    +-- resilience/       server loss, offline edits, restart without saving
    +-- access/           secrets: refused without one, stored outside the project
```

## Building the Native Core

Requirements: CMake ≥ 3.22 and a C++17 compiler. godot-cpp (v10, API 4.7) is
fetched automatically.

```
cmake -S gdextension -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release --target yhde
```

The library is written straight into `godot/addons/yhde/bin/`
(`libyhde.<platform>.editor.<arch>`). macOS universal:
`-DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"`. CI builds Linux, Windows and macOS
whenever the native code changes (`.github/workflows/native-core.yml`).

A local build contains the paths of the machine it was built on. Release
packages use the CI builds only.

The core is editor-only. Exclude `addons/yhde/*` in your export presets.

## Using It

1. Install the add-on in a project (AssetLib, Import..., the zip) or copy
   `godot/addons/yhde` with its `bin/` folder into the project, and enable
   YHDE under Project > Project Settings > Plugins.
2. In the YHDE panel, sign in (the browser opens; press Allow), then pick a
   project or make a new one. The folder remembers its project.
3. Work as usual. Every saved scene you open is shared; unsaved scenes ask to
   be saved first. Project files (textures, models, scripts, `.blend` and so
   on) are shared too, and nobody needs Blender to open a `.blend` a teammate
   imported.

For development, set `YHDE_SERVER=ws://localhost:5080/ws` before starting
Godot to use a local server, and `YHDE_NO_BROWSER=1` to not open a browser
when signing in.

YHDE needs Godot 4.7 (tested on 4.7.2). In another version the add-on says so
in a dialog instead of failing silently.

| Where | What |
|---|---|
| Top bar | The YHDE logo (opens the panel), avatars of everyone connected (click to follow, right-click to message) and the comment tool. The connection state shows only when something is wrong. |
| Panel | Signing in, the team's projects, people and invites, problems in plain words, file transfers and held deletions, and the tabs People, Chat, Comments and Activity. |
| 2D and 3D views | Others' cursors, selection outlines, dashed outlines of drags in progress, and comment pins that follow their node. |
| Comments | Press C (or the comment tool), click on something, write. Enter sends, Shift+Enter makes a new line, `@name` mentions. |
| Follow mode | Your editor shows their scene, screen and camera; click or press a key to stop. |

If 20 or more files disappear from the project at once, YHDE doesn't delete
them for everyone: the panel asks whether to restore them or delete them for
everyone.

The panel's advanced options include accepting built-in (embedded) scripts
from teammates. It is off by default, because they are code.

Set `YHDE_DEBUG=1` to log batch and scan timings.

## Tests

All need a Godot 4.7 editor binary in `GODOT`. The server-backed tests run
the server with an access key, as a real deployment does (`server-bin` is a
`dotnet publish` of the server).

```
# Codec and code-approval gate: need the core built with -DYHDE_DIAGNOSTICS=ON
GODOT=/path/to/godot tests/codec/run.sh
GODOT=/path/to/godot tests/gate/run.sh

# Scripts only (no native core): sign-in token guard, add-on updater
GODOT=/path/to/godot tests/account/run.sh
GODOT=/path/to/godot tests/updater/run.sh

export YHDE_KEY=local-test-key-0123456789

# End-to-end and access key: need a running server with an empty default branch
(cd server-bin && Yhde__AccessKey=$YHDE_KEY ./YHDE.Server --urls http://127.0.0.1:5000 &)
GODOT=/path/to/godot tests/e2e/run.sh
GODOT=/path/to/godot tests/access/run.sh

# Instances: a teammate edits a scene that is instanced in the other editor's
# open scenes, while that editor holds a node inside the instance
GODOT=/path/to/godot tests/instances/run.sh

# Resilience: the script stops and restarts the server itself
SERVER_START="(cd server-bin && Yhde__AccessKey=$YHDE_KEY nohup ./YHDE.Server --urls http://127.0.0.1:5000 > server.log 2>&1 < /dev/null &); sleep 4" \
SERVER_STOP="pkill -9 -x YHDE.Server" GODOT=/path/to/godot tests/resilience/run.sh
```

The MessagePack decoder has a libFuzzer target and a limits test in
`gdextension/fuzz/` (build commands in the files).

The end-to-end test drives two editors through real undo/redo actions:
perturbing every stored property of every node (including one node of every
Node2D, Control and Node3D class), structure changes, embedded and external
resources, groups, signal connections, instancing, attaching a script and
editing its exported variables (typed arrays/dictionaries, enums, flags, node
and resource references), undo/redo, deleting a node the other person is
inspecting, a scene edited while closed, *Save As*, edits in the other
direction, and simultaneous conflicting edits, then requires both editors to
hold identical state. Phase 9 has both editors type into the same script at
the same time (same line, different places), then checks identical text,
per-person undo, save-for-everyone, and a closed script following on disk.
Phase 7 covers project files: a texture used right after it was added (same uid on both sides), a material that depends on it,
scripts, moves, deletes, a project setting, and a folder deleted by mistake
(held, then restored). Any load error for those files in either editor's log
fails the run.

Phase 8 runs when `YHDE_BLEND_FILE` (a `.blend`) and `YHDE_BLENDER` (the
Blender executable) are set: editor A imports the `.blend` and places it in a
scene, and editor B, set up without Blender, must show it without trying to
import it. Use a self-contained Godot copy (an empty `._sc_` file next to the
binary), because the editors change their Blender path setting.
