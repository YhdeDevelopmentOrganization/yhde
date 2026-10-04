# ADR 0001: The Operation Log Is the Truth

- Status: Accepted
- Date: 2026-06-16
- See also: [vision.md](../vision.md), [operation_system.md](../operation_system.md),
  [database.md](../database.md)

## Situation

Several people must edit one Godot project at the same time, and we also want
history, rollback, replay, an audit trail and a way to handle clashes. Syncing
scene files (`.tscn`) between editors fails at all of it: merged text or
binary scenes break, history is coarse, clashes are constant and nobody can
tell who changed what.

## Decision

A project is an append-only log of operations that are never changed once
stored. Every edit is an operation. Scene state is built from the log and is
never stored as the truth. Editors send operations, not scene files.

This is event sourcing applied to editing a game.

## Why

With a correct log, the hard features come from one mechanism:

- history is the log itself;
- rollback is rebuilding up to an earlier `seq`, or adding inverse operations;
- replay is applying the operations again;
- snapshots are saved results of the log ([ADR 0003](0003-snapshots.md));
- clashes are handled by the order of operations
  ([ADR 0006](0006-conflict-resolution.md));
- the audit trail is who and when on every operation, in a hash chain;
- sync is sending the operation to everyone.

## Costs

- Operations must give the same result everywhere and be invertible, which
  limits how edits can be modeled.
- Rebuilding state means replaying, so big projects will need snapshots.
- The log only grows.
- Every feature has to be expressed as operations.

In return: small messages, complete history with authors, the same state on
every editor, and room for later features such as session replay and diff
viewers.

## Turned Down

1. Syncing scene files, or merging their text. Breaks scenes, coarse history,
   no authors, fragile merges.
2. Only snapshots of the state, no log. Loses the per-edit history, undo and
   authors; clashes become guesswork.
3. Clients decide and reconcile later. Can't be trusted and gives no clean
   order ([ADR 0002](0002-server-authoritative.md)).
