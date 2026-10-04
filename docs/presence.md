# Presence

Who is online in a project and what they are doing right now: scene,
selection, cursor, camera, drags, where they are typing. Presence is not in
the operation log and is never stored. It uses the `presence` channel
([network_protocol.md](network_protocol.md) §4).

## 1. Presence Is Not Operations

| | Operations | Presence |
|---|---|---|
| Stored | Yes, in PostgreSQL | No, in memory only |
| Every update arrives, in order | Yes | No: the newest replaces the rest |
| History | Yes | No |
| Survives a disconnect | Yes | No, and it shouldn't |

Cursor and selection updates come many times a second and are worthless a
moment later. Putting them in the log would only make it bigger.

## 2. What Is Sent

A `PresenceUpdate` has a display name, the scene, the tool and a small JSON
object with the rest. Every field in it is optional:

| Field | Meaning |
|---|---|
| `sel` | Selected node ids (at most 64). |
| `cur` | Cursor in world space: `[x, y]` in 2D, `[x, y, z]` in 3D. |
| `view` | 2D: `[center_x, center_y, zoom]`. 3D: 12 floats of the camera transform and the FOV. |
| `drag` | Nodes being dragged right now: `[id, "t2", 6 floats(, w, h)]` or `[id, "t3", 12 floats]` (global transforms). |
| `script` | The shared script the person is in. |
| `caret` | `[line, column]` in that script. |
| `tsel` | Selected text: `[from_line, from_column, to_line, to_column]`. |
| `typing` | `true` while they typed in the last 3 seconds. |
| `summon` | "Bring everyone here": a new id, kept for 20 seconds. Editors that see a new id start following that person, once per id. A summon already running when someone first appears is ignored. |

The cursor is left out while "Show my cursor to others" is off in the
panel's ⋮ menu.

The server adds who sent it (session, account, member id) and when. It cleans
every field and allows 30 updates a second per session
([network_protocol.md](network_protocol.md) §4).

Editors draw everything that moves (cursors, dragged nodes, cameras, follow
mode, carets in scripts) smoothly: each value glides toward its latest update,
so 15 updates a second look like continuous motion. How the editor shows it
all is in [editor_client.md](editor_client.md) §8.

## 3. Flow

```
editor  -> PresenceUpdate
server  -> keeps the newest entry for that session
        -> PresenceState to the others in the project (only what changed)
editors -> draw cursors, selections, avatars
```

- The editor limits how often it sends, so a moving cursor doesn't flood the
  connection.
- A lost update is just replaced by the next one. Nothing is sent again.

## 4. Joining and Leaving

- After `Subscribe`, the server sends the whole picture (`full = true`) and
  tells the others someone joined.
- When a connection closes, its entry is removed and the others are told
  (`left`). Entries belong to live connections, so a crashed editor leaves no
  ghost behind.

## 5. Who Sees It

Only people connected to the same project see each other's presence. It shows
what someone is doing, never content they couldn't see anyway.

## 6. Rules

1. Presence is never stored and never in the log.
2. Updates may be lost; the newest one counts.
3. Presence ends with the connection.

## See Also

- [network_protocol.md](network_protocol.md), [editor_client.md](editor_client.md),
  [social.md](social.md) (chat and comments, which are stored)
