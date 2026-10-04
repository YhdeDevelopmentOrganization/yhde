# Architecture

The parts of YHDE, how they talk to each other and how an edit travels. Each
part has its own document with the details; this one links to them.

## 1. The Shape of It

- The server decides. Editors propose changes, and only the server stores
  them ([ADR 0002](adr/0002-server-authoritative.md)).
- A project is a log of operations that are never changed once stored.
  Scene state is rebuilt from the log ([ADR 0001](adr/0001-operation-log.md)).
- Editors stay connected over a WebSocket and get each stored operation as
  soon as it is committed.

## 2. Overview

```
  Godot editor + YHDE add-on
  (GDScript UI, C++ native core)
        |
        |  WSS: edits, presence, chat (binary frames)
        |  HTTPS: files, sign-in, website
        v
  +------------------------------------------------------+
  | Server (C#)                                          |
  |                                                      |
  |  WebSocket gateway -> access check -> operation      |
  |  processor -> log, then broadcast to the project     |
  |                                                      |
  |  presence, chat and comments, live text, files,      |
  |  accounts and teams, website and admin page          |
  +---------------+--------------------------+-----------+
                  |                          |
           PostgreSQL                  blob store on disk
   (operation log, accounts, teams,   (file contents by hash)
    chat, audit log)
```

## 3. What Each Part Is Built With

| Part | Built with | Why |
|---|---|---|
| Editor | Godot 4.7, tested on 4.7.2 | The engine teams already use. |
| Native core | GDExtension in C++ | Fast diffing, networking and caching inside the editor without rebuilding Godot. It is not a security measure ([security.md](security.md)). |
| Server | C# on .NET 10 | Good async I/O, WebSocket and PostgreSQL libraries, one process to run. |
| Database | PostgreSQL | Transactions, so no stored edit is lost; indexes for the log; JSONB for operation payloads. |
| Files | Blob store on disk | Contents stored once per hash. See [assets.md](assets.md). |
| Connection | WSS for the edit stream, HTTPS for files | See [network_protocol.md](network_protocol.md). |

## 4. The Editor Side

The native core (`client/gdextension/src/`) does the work; the GDScript
add-on (`client/godot/addons/yhde/`) is the UI. Nothing on the client is
trusted by the server.

| Part | Folder | What it does |
|---|---|---|
| Session | `yhde_session.*` | Ties everything together and talks to the add-on. |
| Networking | `networking/` | The WebSocket, framing, heartbeat and reconnect. |
| Operation queue | `operation_queue/` | Edits waiting for the server, kept across restarts and sent again after a reconnect. |
| Sync engine | `sync/` | Turns editor changes into operations and applies incoming ones to scenes and resources. |
| Local cache | `local_cache/` | What this editor already has, so reconnecting only fetches what's new. |
| Files | `assets/` | Hashes, uploads and downloads project files. |
| Presence | `presence/` | Cursors, selections and where everyone is. |
| Live text | `text/` | Several people typing in one script. |

Details: [editor_client.md](editor_client.md).

## 5. The Server Side

| Part | Folder | What it does |
|---|---|---|
| Gateway | `Gateway/` | Accepts WebSocket connections, checks access (editor sign-in, invite link or server key) and routes messages. |
| Operations | `Operations/` | Checks, orders and commits operations, then broadcasts them. Undo is done here too. |
| Storage | `Persistence/` | Database access, migrations and the hash chain over the log. |
| Presence | `Presence/` | Passes presence between the people in a project. |
| Social | `Social/` | Chat, direct messages and comments ([social.md](social.md)). |
| Text | `Text/` | Merges live script edits ([text_editing.md](text_editing.md)). |
| Files | `Assets/` | The blob store and its HTTP endpoints ([assets.md](assets.md)). |
| Projects | `Projects/` | Projects, branches, invite links and imports ([projects.md](projects.md)). |
| Accounts | `Accounts/` | Sign-up, sign-in, OAuth and editor sign-in ([authentication.md](authentication.md)). |
| Teams | `Teams/` | Teams, plans, roles, access per project and promo codes ([teams.md](teams.md)). |
| Admin | `Admin/` | The admin page, staff accounts, audit log, health and updates ([admin.md](admin.md)). |
| Website | `Site/`, `Addon/` | Website posts and add-on downloads ([onboarding.md](onboarding.md)). |

Planned and not built yet: snapshots ([snapshots.md](snapshots.md)), locks
([authority.md](authority.md)) and the Git mirror
([ADR 0010](adr/0010-checkpoints-and-git-mirror.md)).

## 6. How an Edit Travels

```
1. Someone moves a node in Godot.
2. The native core turns the change into an operation (target UUID and the
   new value), shows it right away and queues it for the server.
3. The gateway receives it and attaches who sent it.
4. The server checks that this person may edit the project.
5. The operation processor checks the operation against the current state,
   gives it the next sequence number and commits it to the log.
6. The committed operation goes to everyone in the project. The sender
   replaces its early preview with the stored result; the others apply it.
```

A refused operation is never stored or broadcast. Only the sender hears
about it, so its editor can undo the preview.

## 7. Log and State

```
  Operation log (only ever appended to)
  op 1  op 2  op 3  ...  op N
    |                      |
    +------ replay --------+
                |
                v
     scene state in the editor
     (rebuilt from the log, cached locally)
```

The editor's state can always be rebuilt from the log. Snapshots, when they
come, will only make that faster.

## 8. Identity

- Projects, branches, nodes, resources, files, operations, people and
  sessions all have UUIDs.
- Operations point at objects by UUID, never by scene path or child index,
  so moving and reordering at the same time can't break references.
- File contents are stored by hash; a file in the project is known by its
  path and the hash of its current content.

## 9. Rules That Always Hold

Breaking one of these is a bug.

1. Stored operations are never changed. A correction is a new operation.
2. The server sets the order. The client's order is only a guess until then.
3. Scene files are never sent as the truth.
4. An operation is stored before it is broadcast.
5. Clients only hold state that can be rebuilt; losing it loses nothing.
6. The server checks permission for every operation.

## See Also

- [vision.md](vision.md)
- [roadmap.md](roadmap.md)
- [security.md](security.md), [reliability.md](reliability.md),
  [capacity.md](capacity.md), [deployment.md](deployment.md)
