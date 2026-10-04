# Operation System

What an operation is, which types exist, and what happens to one from the
editor to the log. How the log is stored is in [database.md](database.md),
how clashes are handled in [conflict_resolution.md](conflict_resolution.md).
The decision behind it: [ADR 0001](adr/0001-operation-log.md).

## 1. What an Operation Is

An operation is one change, and once stored it is never changed. Every edit
in the editor becomes one or more operations, and a project is the ordered
list of its committed operations: the log. To change a scene you add an
operation; nobody edits scene files directly.

An operation says what changed and how ("set Enemy's position from (0, 0) to
(10, 0)"), with enough of the old value to undo it.

## 2. Fields

| Field | Meaning | Set by |
|---|---|---|
| `op_id` | UUID of the operation. | Client |
| `seq` | Its place in the branch's order. | Server |
| `branch_id` | The branch it belongs to. | Server |
| `type` | For example `ChangeProperty`. | Client |
| `target_id` | UUID of the node, resource or file it acts on. | Client |
| `payload` | The change itself, as a JSON object. | Client |
| `actor_id` | Who made it, from the signed-in session. | Server |
| `session_id` | The connection it came from. | Server |
| `client_op_ref` | The client's own id, to match the stored result to its preview. | Client |
| `parent_seq` | The last `seq` the client had seen when it made the change. | Client |
| `created_at` | When it was stored. | Server |
| `prev_signature`, `signature` | Links in a hash chain over the log. | Server |

The server never takes `seq`, `actor_id`, `session_id` or `created_at` from the
client ([security.md](security.md)). Each signature includes the one before
it, so changing an old operation would break the chain and show.

## 3. Types

Defined in `server/src/YHDE.Server/Domain/OperationType.cs`.

Nodes:
- `CreateNode`: a new node under a parent.
- `DeleteNode`: removes a node and its children.
- `MoveNode`: gives a node a new parent.
- `RenameNode`, `ReorderNode`: name and position among siblings.
- `ChangeNodeType`: the same node as another class (Change Type in the editor).
- `SetSceneRoot`: another node becomes the scene root.

Properties and resources:
- `ChangeProperty`: one property's new value, with the old one. A transform
  change is a `ChangeProperty` too.
- `AddResource`, `RemoveResource`, `ChangeResourceProperty`: resources on a
  node or in the project.

Files ([assets.md](assets.md)). The log records which content each project
path holds; the bytes are in the blob store. `s` is the path, `h` the SHA-256
of the content, `n` its size and `o` the previous hash. The server checks that
the path is inside `res://`, is not hidden and is not the YHDE add-on, and
that the blob is stored.
- `RegisterAsset {s,h,n}`: a new file.
- `UpdateAsset {s,h,n,o}`: new content.
- `MoveAsset {s,f,h,n}`: the file at `f` moved to `s`.
- `DeleteAsset {s,o}`: the file was deleted.

Scripts ([text_editing.md](text_editing.md)):
- `EditText {s,b,t,save?}`: one edit of a shared text file against version
  `b`. The server adjusts it for edits made at the same time and stores the
  result. Text edits are undone per person in the editor, not with
  `UndoRequest`.

## 4. What Every Type Must Support

| | |
|---|---|
| Replay | The same operation on the same state always gives the same result. |
| Undo | The server can build the inverse, so payloads carry the old value. |
| Branches | An operation belongs to one branch ([versioning.md](versioning.md)). |
| Rollback | The log can be rebuilt up to any `seq`. |
| Audit | Who and when are stored with it, inside the hash chain. |

## 5. From the Editor to the Log

In the editor:
1. A change in the editor becomes an operation with a `client_op_ref` and
   the `parent_seq` the editor has seen.
2. The editor shows the change right away and puts the operation in its
   queue.
3. It is sent over the WebSocket.

On the server:
4. The gateway adds who sent it and from which connection.
5. The server checks the person may edit this project. A person with view
   access is refused here ([teams.md](teams.md)).
6. The operation is checked: well formed, the target exists, the values fit.
7. It gets the next `seq` and is written to the log in a transaction. It is
   stored before anyone else sees it ([reliability.md](reliability.md)).
8. It is sent to everyone connected to the branch.

Back in the editors:
9. The sender matches it by `client_op_ref` and replaces its preview with
   the stored version. If it was refused, the preview is undone.
10. Everyone else applies it.

## 6. Undo

Undo happens on the server; there is no local undo.

1. The editor asks the server to undo an `op_id`.
2. The server checks the person may do that and that the operation can still
   be undone given what came after it.
3. The server builds the inverse operation and commits it like any other.
4. The inverse goes out to everyone.

The original operation stays in the log, followed by its undo. Redo is an
undo of the inverse. The server says no, with a reason, when later edits make
an undo unsafe, for example undoing a `CreateNode` whose node now has
children.

## 7. Order

- The server numbers each branch's operations one after another. That order
  is the truth.
- `parent_seq` tells the server whether the change was made against an older
  state ([conflict_resolution.md](conflict_resolution.md)).
- Each branch's operations are committed by one writer at a time, which keeps
  the order clean ([capacity.md](capacity.md) has the numbers).

## 8. Same Result Everywhere

Replaying the log must give the same result on every machine, so operations
can't depend on local time, randomness, the locale, scene paths or child
indices (always UUIDs), or anything not in the log.

## 9. The Queue in the Editor

- Holds operations the server hasn't confirmed yet, in the order they were
  made.
- Is saved to disk, so a crash or restart doesn't lose them.
- Sends them again after a reconnect with the same ids; the server ignores
  ones it already has.

## 10. Example

```
Enemy (UUID e1) is at (0, 0). The branch is at seq 40.

A drags Enemy to (10, 0):  ChangeProperty e1 position (0,0) -> (10,0), parent_seq 40
B drags Enemy to (0, 5):   ChangeProperty e1 position (0,0) -> (0,5),  parent_seq 40
Both see their own move right away.

A's arrives first: stored as seq 41 and sent to both.
B's arrives next: stored as seq 42 and sent to both.

Everyone ends with Enemy at (0, 5). A's editor moves Enemy from its preview
to (0, 5) when seq 42 arrives. The log keeps both moves and who made them.
```

## See Also

- [ADR 0001](adr/0001-operation-log.md), [ADR 0002](adr/0002-server-authoritative.md)
- [conflict_resolution.md](conflict_resolution.md), [database.md](database.md),
  [network_protocol.md](network_protocol.md), [reliability.md](reliability.md)
