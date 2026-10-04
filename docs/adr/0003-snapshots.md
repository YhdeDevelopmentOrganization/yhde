# ADR 0003: Snapshots to Limit Replay

- Status: Accepted, not built yet
- Date: 2026-06-16
- See also: [snapshots.md](../snapshots.md), [operation_system.md](../operation_system.md)

## Situation

With the log as the truth ([ADR 0001](0001-operation-log.md)), state is
rebuilt by replaying operations. The log keeps growing, so replaying from the
start gets slower with every edit: opening a project, joining, rollback and
branching would all slow down over time.

## Decision

Add snapshots: saved results of a branch's log at one `seq`, never changed
once written. Any state is then the nearest snapshot plus the operations after
it. Snapshots are made in the background, never on the commit path, and are
indexed so the nearest one is one lookup away.

A snapshot can always be rebuilt from the log. It is never the truth and can
be deleted at any time.

## Why

- Joining, reconnecting, rollback and branching cost only the operations after
  the snapshot.
- The log stays the only truth.
- Read speed no longer depends on how long the history is.

## Costs

- Storage for snapshots, kept down by removing old ones.
- Background CPU to make them.
- A setting for how often to take them.

## Turned Down

1. Always replay from the start. Gets slower forever.
2. Store the current state as the truth and keep the log only for audit.
   Contradicts ADR 0001 and brings back the problems it solves.
3. Only caches in each editor. A new editor still replays everything.

## Where It Stands

Not built. Editors catch up in pages and keep a local cache, which is enough
for now ([snapshots.md](../snapshots.md)).
