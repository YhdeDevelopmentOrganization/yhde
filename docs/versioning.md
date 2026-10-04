# Versioning

YHDE keeps history in two layers: Git for commits and releases, and the
operation log for every single edit. How the log is stored is in
[database.md](database.md).

## 1. Two Layers

| Layer | Step size | Kept by | For |
|---|---|---|---|
| Git | A commit | The team's own Git workflow | Releases, code review, a baseline outside YHDE. |
| Operation log | One edit | YHDE | Undo, history of a node or a person, replay, branches. |

Git answers "what did the project look like at release 1.2?". The log answers
"who moved this node a minute ago, and where was it before?".

## 2. What the Log Gives Today

- History: every edit with who made it and when.
- Undo and redo on the server ([operation_system.md](operation_system.md) §6).
- Branches: each project has a `main` branch, and a branch has its own `seq`.
  Editors connect to one branch at a time.
- History of one object or one person: filter by `target_id` or `actor_id`
  (both indexed).
- Starting from an existing game: a project can be created from a zip of a
  Godot project, on the dashboard or the admin page. Its files become the
  first operations.

## 3. Planned

None of this is built yet.

- Tags: a name for one point `(branch, seq)`.
- Rollback: look at the project at an earlier `seq`, or add inverse
  operations that bring it back there. Both keep the log append-only, like
  undo.
- Branching and merging from the editor. A branch would start from a point
  in its parent without copying anything. Merging would replay one branch's
  operations onto another through the normal checks.
- Checkpoints mirrored to Git: YHDE writes the project at a named point as a
  Git commit, so the team's repository stays up to date
  ([ADR 0010](adr/0010-checkpoints-and-git-mirror.md)).
- Snapshots, to make all of the above fast ([snapshots.md](snapshots.md)).

## 4. Rules

1. The log is the history. Versioning adds names and views over it, never a
   second source of truth.
2. History is never rewritten; going back means adding operations.
3. Git is never used for the live editing.

## See Also

- [operation_system.md](operation_system.md), [snapshots.md](snapshots.md),
  [database.md](database.md), [ADR 0001](adr/0001-operation-log.md),
  [ADR 0010](adr/0010-checkpoints-and-git-mirror.md)
