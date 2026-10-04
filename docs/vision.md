# Vision

Why YHDE exists and what it tries to be. How it is built is in
[architecture.md](architecture.md).

## What YHDE Is

YHDE lets a Godot team work in the same project at the same time. Everyone
sees each other's edits, cursors and selections as they happen, and the
project keeps a full history of who changed what.

It works like a multiplayer game. Players don't send each other copies of the
world; they send their actions to a server, which keeps the one shared state.
In YHDE the editors send their edits to the server, and the server decides the
order and tells everyone.

## Edits Are Operations

Every edit (adding a node, moving it, changing a property, importing a
texture) is a small operation that is never changed once stored. A project is
the ordered log of those operations. Scene files are built from the log, like
a bank balance is built from the transactions, and YHDE never sends scene
files between editors.

Most of the hard parts follow from the log:

| Feature | How the log gives it |
|---|---|
| History | The log is the history. |
| Rollback | Replay up to a point, or apply the inverse operations. |
| Replay | Apply the operations again to rebuild any state. |
| Snapshots | A saved result of the log at one point, to replay less. |
| Conflicts | Operations have an order and an author. |
| Sync | Sending an operation to everyone is the sync. |
| Audit | Each operation records who made it and when. |

So the one thing that must never go wrong is the log.

## Who It Is For

Small Godot teams, from 2 to about 12 people, indie studios and hobby
groups, who want to work together live without emailing zips or fighting
merge conflicts in scene files. The team owner picks a plan and invites the
others, who join free.

## Goals

1. Live editing of Godot scenes with low latency, sending only what changed.
2. The server decides. Clients are never trusted.
3. Every change can be replayed, undone and traced to a person.
4. Cheap to run: one small VPS serves many teams (see
   [capacity.md](capacity.md)).
5. Room to grow: new operation types, file types and inspectors can be added
   later without rebuilding the core.
6. Security and reliability come from the design, not from hiding things.

## Not Goals

- A file sync tool. YHDE does not diff or merge `.tscn` text.
- A replacement for Git. Git stays the place for commits and releases; YHDE
  is the live layer on top. See [versioning.md](versioning.md).
- Offline editing, for now. It is on the [roadmap](roadmap.md).
- A client that can't be cracked. No client is. The server checks
  everything, so a modified client gains nothing (see
  [security.md](security.md)).

## When It Is Working

- Two people edit the same scene and each sees the other's changes right
  away, with no scene file conflicts.
- Any earlier state of a project can be rebuilt and looked at.
- A server crash, a lost connection or a power cut loses no stored edit.
- A new team can sign up, install the add-on and be editing together in a
  few minutes.

## See Also

- [architecture.md](architecture.md)
- [roadmap.md](roadmap.md)
- [operation_system.md](operation_system.md)
- [ADR 0001](adr/0001-operation-log.md)
