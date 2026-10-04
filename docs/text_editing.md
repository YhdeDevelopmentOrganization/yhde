# Live Text Editing

How several people type in the same script at once, like in a shared
document. The decision is [ADR 0012](adr/0012-live-text-editing.md).

## 1. What People See

- Everyone with the same script open sees each other's typing as it happens,
  character by character. Everyone can type at once, even on the same line.
- Other people's carets and selections are drawn in their color, with a name
  tag while they move or type ("Maya is typing"). The script list, FileSystem
  dock and People tab show who is in which script.
- Ctrl+Z and Ctrl+Y undo and redo only your own typing, never a teammate's.
- Saving saves for everyone: when anyone saves a shared script, every
  editor that has it open saves it too (so running the game uses the same
  code everywhere). Editors that do not have it open keep their file on disk
  up to date as edits arrive.
- Works for everything that opens in the Script editor: GDScript, C#, JSON,
  text, Markdown, config files, … (`TextSync::is_text_path`), and shaders
  (`.gdshader`, `.gdshaderinc`) in the Shader editor at the bottom of the
  screen, whichever main screen you are on.

## 2. Model

A text file starts as a shared file: its bytes are an asset operation
([assets.md](assets.md)). From then on every change is an `EditText`
operation in the log:

```
{"s": "res://player.gd", "b": <seq the edit applies to>, "t": [4, "hi", -2, 10], "save": true?}
```

`t` walks the whole text: a positive number keeps that many characters, a
negative number deletes, a string inserts. Characters are Unicode code
points (the unit of Godot's `String`); texts are normalized to `\n` line
endings without a byte order mark on both sides. It is operational
transformation, as in ot.js, with the server as the one place that orders
edits.

### Server
`TextDocuments` (server/src/YHDE.Server/Text/) keeps each recently edited
file's current text: the file's bytes plus the logged edits after them
(rebuilt on demand from the log). An incoming edit made against version `b`
is transformed over every edit committed after `b` (earlier edits win where
two insert at one place), checked against the current length, and logged in
its transformed form with `b` set to the version it now applies to. Edits
and file operations of a branch are committed one at a time. An edit made
before the file was replaced, or too old to merge (more than 4,000 edits
behind), is refused with `TextOutdated`.

### Editor
`TextSync` (client/gdextension/src/text/) keeps per file:

| | |
|---|---|
| `server_text`, `rev` | the text after the last logged edit, and its seq |
| `text` | what the editor shows (server text + unconfirmed typing) |
| `outstanding` | our edit on its way (at most one) |
| `buffer` | what we typed since, merged into one edit |

Typing is diffed against `text` and becomes an edit; while one is on its way,
further typing is composed into the buffer. A logged edit from someone else
is transformed over `outstanding` and `buffer` and applied in place in the
CodeEdit (`insert_text` / `remove_text`), so carets stay where they are. Our
own edit coming back only advances `server_text`. After a `TextOutdated`
refusal everything unconfirmed is re-diffed against `server_text` and sent
again. The state per file (`rev`, `server_text`) is kept in the editor's
project folder, so typing done while offline is sent as an edit on the next
connection.

The UI (`main.gd`) maps script editor tabs to files through the script list
(editor internals: item tooltip = path, metadata = tab index) and binds each
tab's CodeEdit. The Shader editor dock is mapped the same way: its file list
(tooltip = path) sits beside a TabContainer with one `TextShaderEditor` per
file in the same order (visual shaders have no text and are skipped). Saves
go through the cached `Script`, `Shader` or `ShaderInclude` resource. Undo
keys are intercepted on bound editors; the editor's own
undo history is cleared after remote edits so the Edit menu cannot undo a
teammate's text.

## 3. Saving

Pressing save sends an edit with `"save": true` (after any unconfirmed
typing). Every editor that applies it saves the file: open tabs through the
`Script` resource (`ResourceSaver`), closed files are written and their cached
script reloaded. A save made because a teammate saved is not announced again.
Saves are absorbed by the asset plane: a live text file's bytes are never
uploaded again while its edits travel as `EditText`. A change made outside the
editor (another text editor, a git pull) is shared as new bytes, and the live
text starts over from them.

## See Also

- [assets.md](assets.md), [presence.md](presence.md) (`script`, `caret`,
  `tsel`, `typing`), [operation_system.md](operation_system.md),
  [editor_client.md](editor_client.md), [ADR 0012](adr/0012-live-text-editing.md)
