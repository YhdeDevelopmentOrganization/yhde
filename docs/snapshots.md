# Snapshots

Not built yet. This is the plan; the decision is
[ADR 0003](adr/0003-snapshots.md).

## 1. The Problem

Today an editor that joins a project, or has been away, gets the operations
it is missing in pages (`SyncState`, [network_protocol.md](network_protocol.md)
§7), and the local cache means a returning editor only fetches what is new.
A brand-new editor on an old project still downloads the whole log. That
grows with the project and will get slow.

## 2. The Plan

A snapshot is a branch's state at one `seq`: the result of applying
operations 1 to `seq`. A new editor would get the latest snapshot and only
the operations after it.

```
log:  op 1 ... op k  op k+1 ... op N
                |
         snapshot at k
                |
  state now = snapshot at k + replay(op k+1 .. op N)
```

Rules:

- A snapshot can always be rebuilt from the log. Deleting every snapshot loses
  nothing but speed.
- It holds the node and resource tree by UUID, property values and which file
  content each path has (hashes, not bytes).
- It is never changed once written, and carries a hash of its contents.
- Making one never holds up committing operations; a failed snapshot is just
  tried again.

## 3. When to Take One

To be tuned once it exists. Likely candidates: every N operations on a
branch, after some minutes of activity, and where a branch is made.

## 4. Keeping Them

- Keep at least the newest snapshot of each active branch.
- Keep snapshots at branch points and checkpoints longer
  ([versioning.md](versioning.md)).
- Removing a snapshot never removes operations.

## 5. Checking Them

Rebuilding a snapshot from the log must give the same hash. A mismatch means
corruption or tampering ([security.md](security.md)).

## See Also

- [ADR 0003](adr/0003-snapshots.md), [operation_system.md](operation_system.md),
  [database.md](database.md), [versioning.md](versioning.md)
