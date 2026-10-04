# ADR 0007: The Editor Compares State and Replays in Log Order

- Status: Accepted
- Date: 2026-09-24
- See also: [editor_client.md](../editor_client.md), [operation_system.md](../operation_system.md)

## Situation

The Godot editor has no stream of "what changed". Edits come from hundreds of
tools (inspector, gizmos, tile painting, curve editors, the Scene dock,
drag-and-drop, script variables) that all end up in the editor's undo system,
whose actions can't be looked into. Every property of every node and resource
has to sync, in 2D, 3D and UI scenes, without special code per tool. The
client must also agree with everyone else when edits happen at once, survive
disconnects and crashes, and keep its logic out of GDScript.

## Decision

1. Find changes by comparing. After any change to the undo history, the
   client compares the affected documents with a shadow copy of the last
   synced state and sends the differences as operations. Every node and
   embedded resource has a UUID in hidden metadata, derived the same way on
   every machine the first time a file is seen.
2. Local undo is an edit forward. Ctrl+Z makes ordinary operations that go
   into the log, so the history shows both.
3. Replay in log order. Incoming values are applied in `seq` order. A remote
   change to something with a pending local change is skipped at that
   operation's place in the log. Position changes (create, move, reorder) are
   replayed by every client, the author included, so sibling order is the
   same everywhere.
4. Native core, GDScript UI. All protocol, identity, comparing and applying is
   in C++ (an editor-only GDExtension). GDScript only draws the UI and passes
   editor signals through one class.

## Why

- Comparing looks at results, so new editor tools, other plugins' undoable
  actions and new engine classes work without code changes. The codec is
  tested against every class in the running engine.
- Deriving ids the same way lets editors that start from the same files agree
  without talking.
- Skipping by log position and replaying structure in log order gives every
  editor the same final state as the log ([ADR 0001](0001-operation-log.md)).
- Keeping logic native keeps the UI replaceable. It is not a security measure;
  the server checks everything.

## Costs

- Comparing costs time in proportion to the scene. Cached property lists and
  one connection query per node keep a 1,000-node scene at about 27 ms
  (headless).
- Changes made outside the undo system (tool scripts setting properties
  directly, animation previews) are not treated as edits and are not sent.
- Setters that adjust their input can leave last-bit float differences
  between editors. They never loop, because only real changes move the
  shadow.
- When the author's new node is committed after someone else's insert, the
  author sees it move once into the stored order.

## Turned Down

- Hooking every tool or wrapping `EditorUndoRedoManager`: the actions are
  opaque and there are too many tools. Missing one would silently break sync.
- Comparing the whole scene all the time: picks up animation previews and
  wastes CPU when nothing happens.
- Syncing `.tscn` text: breaks the rule of never syncing scene files, and
  can't merge edits made at the same time.
- Skipping locally without the author replaying: inserts made at the same time
  ended in different orders on different editors (seen in testing).

## Changes Since

Undo later moved to the server (`UndoService`, `UndoRequest`), so undoing
works on the server's log for everyone ([operation_system.md](../operation_system.md) §6),
tested in phase 4 of the end-to-end test.
