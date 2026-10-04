# Editor Client

How the Godot editor takes part: how edits become operations, how stored
operations become edits, how objects keep the same identity everywhere, and
what the add-on shows. The wire format is in
[network_protocol.md](network_protocol.md), the operation model in
[operation_system.md](operation_system.md), presence in
[presence.md](presence.md).

## 1. Two Layers

Needs Godot 4.7 (tested on 4.7.2).

| Layer | Language | Does |
|---|---|---|
| Native core, `client/gdextension` | C++ GDExtension, editor only | Connection, framing, queue, local cache, identity, scene diffing and applying, value encoding, presence data. |
| Add-on, `client/godot/addons/yhde` | GDScript | Top bar, panel, 2D and 3D overlays, follow mode, passing editor events to the core. |

The core gives scripts one class, `YhdeSession` (a `Node`). Its API is about
what to show: names, colors, cursor positions, selections, status.
Operations, payloads and ids never reach GDScript. That is not a security
measure (the server checks everything, [security.md](security.md)); it keeps
the part that must be correct in one place.

## 2. Documents

Everything that is synced belongs to a document:

- a scene document: a saved `.tscn` or `.scn`, from its scene root; or
- a resource document: a saved `.tres` or `.res` file, such as a material
  several scenes share.

Every operation payload names its document in `"s"`. A scene that was never
saved has no shared identity and isn't synced; the panel asks to save it. The
first save (or Save As) sends the whole tree as `CreateNode` operations so
the others can create the file.

A document is live when the editor has it in memory: open scene tabs,
including ones in the background, and loaded resources. Incoming operations
change live documents in memory. For anything else the client loads the file,
applies the operations and saves it.

## 3. Identity

Every node and embedded resource has a UUID, kept in hidden editor metadata
(`_yhde_id`) so it survives saving and loading.

- The first time a document is seen, ids come from that metadata, or are
  derived (UUIDv5) from the document path and where the object sits. Two
  editors that see the same file for the first time get the same ids without
  talking to each other.
- Anything created after that gets a new random id. A duplicated or pasted
  node keeps nothing from its source.
- Nodes inside an instanced scene (editable children) derive their id from
  the instance's id and their path inside it.

## 4. Local Edits to Operations

The client doesn't guess what an action did. After every change to the
editor's undo history (`history_changed`, `version_changed`) it compares the
affected documents with a shadow copy of the last synced state:

1. Structure: created, moved, deleted, reordered and renamed nodes become
   `CreateNode`, `MoveNode`, `DeleteNode`, `ReorderNode` and `RenameNode`.
   Sibling order is rebuilt with as few reorders as possible (longest
   increasing subsequence over what the others have).
2. Every stored property (`PROPERTY_USAGE_STORAGE`) of every node becomes
   `ChangeProperty`, plus two made-up properties: `@groups` (persistent
   groups) and `@conns` (persistent signal connections).
3. Every property of every reachable embedded resource becomes
   `ChangeResourceProperty`. External `.tres` and `.res` files are compared as
   their own documents. When entries of a resource are gone for good (a
   deleted tile, a removed TileSet layer or pattern, a removed curve point),
   setting a single property cannot express it: the resource's whole state is
   sent once as the made-up property `@state`, and the receiver resets the
   resource (`reset_state()`) before applying it.

This is cheap enough to run after every edit: a node's list of stored
properties is cached until the engine sends `property_list_changed` (for a
resource also `changed`: TileSet and its sources grow and shrink their lists
without announcing it; a TileMap's list is always read fresh), and
signal connections take one `get_incoming_connections()` call per node (about
27 ms for a 1,000-node scene, headless). Undo and redo are seen through the
editor's undo manager and each open scene's own history.

Because it starts from the undo history, anything the editor can do and undo
is synced: inspector edits, gizmo drags, tile painting, curve editing, script
variables, drag-and-drop instancing, in 2D, 3D and UI scenes. A drag is sent
once, when it ends. Values the engine changes by itself right after a scene
opens or a remote batch is applied (layout, transforms recomputed on load) go
into the shadow instead of being sent, except on selected or inspected nodes.

## 5. Encoding Values

Payloads are JSON (JSONB in the database). Values use a codec with a type tag
for every `Variant` type, exact to the bit: typed arrays and dictionaries,
packed arrays (base64), node references (`["node", id, path]`), external
resources (`["res", path, uid]`) and embedded resources
(`["sub", id, class, [props...]]`, shared ones by reference). Decoding only
creates `Node` and `Resource` classes. Built-in scripts are refused unless the
person allows them, because they are code.

`client/tests/codec` checks it: every stored property of every Node and
Resource class that can be created in the running Godot, set to a non-default
value, must survive encode, JSON and decode.

## 6. Operations to Local Edits

Stored operations are applied in `seq` order. Frames that arrive out of order
wait, and a gap that doesn't close makes the client re-sync.

- A remote change to a property this client is still waiting to have stored
  is skipped, because our later operation will replace it everywhere. The
  pending queue is checked at each operation's place in the log, so this is
  exact even inside one batch.
- Every client replays structure, the author included: `CreateNode` appends,
  `MoveNode` moves and appends, `ReorderNode` reorders, in log order. So
  inserts and moves made at the same time end in the same order everywhere.
- After applying, only the properties that really changed move the shadow
  forward. A drag in progress is never lost, and nothing applied is sent
  back.
- References to nodes created later in the same batch are resolved at the
  end of the batch. Name clashes from swaps are retried.
- Deleted nodes are detached first and freed later. As the Scene dock does,
  the client first removes the node from the selection and the inspector,
  then frees it after a short wait, and only if nothing attached it again.
  Editor docks hold raw pointers for a moment, and the undo history may own
  the node (it frees by id, so there's no double free).

## 7. Nothing Gets Lost

- Operations not yet confirmed are saved (`.godot/editor/yhde/...queue.json`)
  and sent again with the same ids after a reconnect or crash. The server
  drops duplicates.
- The local cache keeps, per document, the highest `seq` applied in memory
  and the highest saved to disk. A scene closed without saving, or reloaded,
  gets the rest applied again on the next connect, so nothing depends on
  someone pressing Save.

## 8. What the Add-on Shows

Presence carries the scene, the editor screen (`2D`, `3D`, `Script`...) and a
small state object: selection, cursor, view and drags in progress
([presence.md](presence.md)). The add-on shows:

- Top bar: the YHDE logo (opens the panel), overlapping avatars (click to
  follow, right-click to message) and the comment tool. The connection state
  is written next to the logo only when something is wrong.
- Panel: signing in through the website, a grid of the team's projects,
  connecting a folder to a project or making a new one, team people and
  invites, and tabs for People, Chat, Comments and Activity with unread counts
  ([social.md](social.md)). Problems are explained in plain words with what to
  do about them.
- Comments: numbered pins in the author's color that follow their node (press
  C or use the comment tool, then click).
- Where everyone is: avatars on scene tabs, next to the nodes others selected
  in the Scene tree, next to the scenes and scripts they have open in the
  FileSystem dock, and in the script list (with "typing").
- Shared scripts: several people typing at once, with everyone's carets and
  selections ([text_editing.md](text_editing.md)).
- Bring everyone here (People tab): everyone follows your view until they
  click or press a key.
- Options (⋮ in the panel): Show my cursor to others (on by default), which
  of the others' marks to show (cursors, scene tabs, Scene tree, FileSystem,
  script list, code carets, smooth motion), and which notifications pop up
  (direct messages, mentions, comments, "bring everyone here", sync info).
  Warnings and errors always show.
- In the 2D and 3D views: each person's cursor with a name tag, outlines
  around their selection and dashed outlines of what they are dragging.
- Follow mode: shows the person's scene, screen and view, framed in their
  color. Any click or key press stops it.

Signing in is done in the browser: the panel opens the website, the person
presses Allow, and the editor gets a sign-in token
([authentication.md](authentication.md) §4). The token is kept in the
editor's own settings folder, never in the project, because projects get
committed and zipped. The native core stores secrets per server address with
owner-only permissions; scripts can set or replace one but never read it
back, and it is only sent to the address it belongs to. A refused secret
takes the session offline with a message instead of retrying.

## 9. Known Limits

- Changing the scene root's type is not synced; the editor shows a warning.
- The add-on loads on any Godot 4.x and explains when the version is wrong.
  Everything else needs 4.7.
- Engine setters that adjust their input (such as `up_direction`) can differ
  in the last bit of a float between editors. This never makes values bounce
  back and forth.

## See Also

- [ADR 0007](adr/0007-editor-client-capture.md): why the client compares
  state and replays in log order.
- [ADR 0009](adr/0009-shared-imports-and-dependencies.md): file order and
  imports.
- [operation_system.md](operation_system.md), [network_protocol.md](network_protocol.md),
  [social.md](social.md), [text_editing.md](text_editing.md),
  [projects.md](projects.md), [presence.md](presence.md),
  [reliability.md](reliability.md)
