# Reliability

The promise is simple: an edit the server has stored is never lost, and an
edit still on its way is not lost either. This page says how, for crashes,
dropped connections and power cuts.

## 1. What Can Fail

| Failure | What must happen | How |
|---|---|---|
| The editor crashes | Unsent edits survive and go out after a restart. | The queue is saved to disk and sent again. |
| The connection drops | The editor catches up with no gaps and no duplicates. | Paged catch-up from the last `seq`, duplicates dropped by `op_id`. |
| The server crashes | Nothing stored is lost. | Stored before broadcast, PostgreSQL recovery. |
| A power cut | Same as a server crash. | PostgreSQL's write-ahead log, daily backups. |

## 2. Stored Before Broadcast

The one rule that matters most:

```
An operation is sent to editors only after it is committed to the
PostgreSQL log in a transaction.
```

- If the server crashes after the commit, the operation is in the log and
  editors get it when they reconnect.
- If it crashes before, the operation was never confirmed. The sender still
  has it in its queue and sends it again. Either way nothing is lost and
  nothing is doubled.
- An editor never sees an operation that isn't stored, so it can always line
  up with the log.

`OperationProcessor` and `BranchCommitter` hold to this
([operation_system.md](operation_system.md) §5).

## 3. The Editor's Queue

- Edits not yet confirmed are saved to disk, in order, with their `op_id` and
  `client_op_ref`.
- After a reconnect they are sent again. The server ignores ones it already
  stored, so sending again is always safe.
- A refused edit is undone in the editor.

So an edit is safe from the moment it is made: on disk in the editor until the
server confirms it, then in the server's log.

## 4. Reconnecting

- The editor notices a dead connection (no traffic for 20 seconds) and
  reconnects by itself, waiting longer between tries.
- It subscribes from the last `seq` it has and gets the rest in pages
  ([network_protocol.md](network_protocol.md) §7).
- A refused sign-in (code 4401) stops the retries and asks the person to sign
  in again, instead of trying forever.
- If the log is ahead of what the editor applied and nothing is waiting, the
  editor re-syncs after a few seconds.

### Slow and silent connections (server)

- Nothing waits for another person's network. Each connection has its own
  outgoing queue and sender (`Outbox`): the commit loop, presence and chat
  only add to queues. A connection whose queue passes 64 MiB, or that takes
  more than 20 seconds to take one message, is cut off; its editor
  reconnects and catches up from the log, which loses nothing. The admin
  page counts these ("Slow editors cut off").
- For a peer that lags, old cursor positions are dropped (a newer one
  follows); a leave or any operation never is.
- A connection the server hears nothing from for 60 seconds
  (`Yhde:IdleTimeoutSeconds`; the editor pings every 5) is closed, and
  presence entries without a live connection are removed every 10 seconds.
  A person's newer connection replaces their older entry at once.
- Subscribing reads the branch head again after the connection receives
  broadcasts, so an operation committed in between is in the catch-up.
- Undo and redo go through the branch's one writer, so they keep log order.
- A request that fails (a malformed id, the database busy for a moment) is
  answered, never by closing the connection. "Try again" answers (a
  retryable `Error`, or `TryAgain` for an undo) make the editor send what it
  is still waiting for again after 3 seconds.

## 5. Scenes Not Saved

The local cache records, per document, the highest `seq` applied in memory
and the highest saved to disk. A scene closed without saving gets the rest
applied again next time, so nothing depends on anyone pressing Save
([editor_client.md](editor_client.md) §7).

## 6. Files

Uploads and downloads resume where they stopped. A file is written only after
its hash checks out ([assets.md](assets.md)). When many files vanish at once,
nothing is deleted for the team until someone confirms.

## 7. Backups

- The server kit dumps the whole database once a day and when it starts, and
  keeps 14 days by default. Blobs never change once stored, so copying the new
  ones each time is a complete backup of the files
  ([deployment.md](deployment.md)).
- The log is every project, so restoring the database and the blob store
  restores everything.
- Every server update takes a backup first and rolls back if the new version
  doesn't come up.
- Backups are on the same machine for now. Copying them off the server is on
  the [roadmap](roadmap.md).

## 8. Rules

1. A stored operation is never lost.
2. Operations are stored before they are broadcast.
3. The editor's queue is saved to disk.
4. Sending again is always safe: duplicates are dropped by `op_id`.
5. Reconnecting is automatic.

## See Also

- [operation_system.md](operation_system.md), [database.md](database.md),
  [network_protocol.md](network_protocol.md), [deployment.md](deployment.md)
