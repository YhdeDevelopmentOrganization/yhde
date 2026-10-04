# ADR 0012: Live Text Editing with Server-Side Transformation

- Status: Built
- Date: 2026-09-25
- See also: [text_editing.md](../text_editing.md), [assets.md](../assets.md),
  [operation_system.md](../operation_system.md), [ADR 0001](0001-operation-log.md),
  [ADR 0009](0009-shared-imports-and-dependencies.md)

## Situation

Scripts were shared as whole files when saved. Two people working in one
script overwrote each other (the log's last save won, and the other version
was kept as a backup). The goal is document-style co-editing: everyone types
at once, sees each other's typing and carets, and nobody loses characters.
Undo must stay personal.

Scene edits are captured by diffing state after each editor action
([ADR 0007](0007-editor-client-capture.md)). That does not fit text:
keystrokes arrive many times a second, and two edits to one line must merge
character by character, not "last value wins".

## Decision

1. Text changes are `EditText` operations in the log: a list of
   retain/insert/delete components over Unicode code points, made against a
   version (the seq of the last edit the author had applied).
2. The server transforms each incoming edit over the edits committed
   since its version (operational transformation, earlier edits first where
   two insert at one place), checks it against the file's current text, and
   logs the result. Edits and file operations of a branch are committed one at
   a time. Every editor applies logged edits in log order.
3. An editor has at most one edit in flight. Typing meanwhile is composed into
   one next edit, and remote edits are transformed over both before they are
   applied in place.
4. Undo in a shared text file is per person: the editor keeps its own undo
   stack of inverse edits, transformed as others type. The editor's built-in
   text undo is not used for shared files.
5. Saving is shared: a save marker in the edit stream makes every editor save.
   A live text file's bytes are not re-uploaded; outside changes reset its
   live text from new bytes.
6. The text model and transformation exist twice, in C# (server) and C++
   (editor), with randomized convergence tests on the server.

## Why

- The server already orders everything ([ADR 0002](0002-server-authoritative.md)).
  Transforming there keeps editors simple and every editor's result identical,
  and the log stores edits that apply in order, so replay and late joiners stay
  trivial ([ADR 0001](0001-operation-log.md)).
- One edit in flight is the well-understood ot.js/Google Docs client model.
  Clients never have to transform against each other directly.
- A CRDT would avoid server transformation, but it carries per-character
  metadata forever. The log already gives a total order, which is what OT
  needs.

## Consequences

- Gains: many people type in one script at once, with live carets and
  personal undo; no overwritten saves.
- Cost: the server keeps recently edited texts in memory and can
  rebuild any from the log. Edits more than 4,000 behind are refused and
  re-sent from the editor's text.
- Cost: the script editor is reached through editor internals (the
  script list maps tabs to files). A Godot upgrade may need adjustments.
- Cost: shaders in the Shader editor are not live yet (shared on
  save).
- Later: the Shader editor; line-level soft locks; a history view per
  script.

## Turned Down

### Whole-file sync on save (before)
It is simple, but people overwrite each other and see nothing live. Rejected.

### Diff every change like scenes, last writer wins
Keystroke-level conflicts on one line would lose characters. Rejected.

### CRDT (e.g. RGA/Yjs)
It needs no server transform, but has per-character identity overhead, a
larger client, and gains nothing given the server-ordered log. Rejected for
now.

## Changes Since

Shaders in the Shader editor are live too, mapped the same way as scripts
([text_editing.md](../text_editing.md)).
