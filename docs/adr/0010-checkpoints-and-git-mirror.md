# ADR 0010: Named Checkpoints Mirrored to Git

- Status: Proposed
- Date: 2026-09-25
- See also: [versioning.md](../versioning.md), [snapshots.md](../snapshots.md),
  [assets.md](../assets.md), [deployment.md](../deployment.md),
  [ADR 0001](0001-operation-log.md), [ADR 0003](0003-snapshots.md)

## Situation

[versioning.md](../versioning.md) plans two layers: Git for milestones, and
the operation log for every edit. The Git side was left open: "connect it to
GitHub" can mean very different things.

If every teammate also runs Git on their live copy, commits, pulls and merges
fight the live sync. Two people pulling the same commit both "change" every
file, and a merge conflict in a `.tscn` has no place in a system where the
scene is already converged. At the same time, teams want what Git gives them:
an off-site backup, a history they can browse, release tags, diffs in pull
requests, and CI that builds the game.

## Decision

1. Checkpoints are named, immutable points in a branch's history (a tag:
   `(branch, seq)`, a name, an author and a note), created from the editor by
   anyone ("v0.3 playable"). They are part of the log's metadata, not
   operations on project state.
2. A Git mirror turns each checkpoint into one commit of the complete
   project files at that seq. It is pushed to a configured repository (for
   example GitHub), using Git LFS for large binary files. The commit message
   carries the checkpoint name, the note, the authors since the last
   checkpoint and the seq.
3. The mirror is written by one writer: a headless Godot editor that runs
   next to the server (in the server kit), stays connected, and holds the full
   project. Nobody's own editor pushes. The mirror is optional and
   push-only.
4. Git never feeds back into the live project automatically. Bringing in
   outside work (a pull request) is an explicit import that becomes ordinary
   file operations in the log.
5. Restoring a checkpoint appends operations (and file operations) that bring
   the branch back to that state. It never rewrites history.

## Why

- One writer means no merges and no races: the mirror is a projection of the
  log, like every editor's files ([ADR 0001](0001-operation-log.md)).
- Checkpoints give people the milestone concept they know from Git without
  asking them to run Git.
- A headless editor already has everything needed to write exact project files
  (scenes are projected by the editor, not the server). It also gives the
  server kit an always-complete copy of the project, which helps backups and
  late joiners.

## Consequences

- Gains: backups off the server, readable history on GitHub, release
  tags, CI on real project files, all without Git in anyone's daily loop.
- Cost: the server kit gains a Godot process (memory, updates in
  step with the editor version). The Git credentials live on the server.
- Cost: Git history is only as fine as the checkpoints. That is by
  design, since the log keeps every edit.
- Later: the `checkpoint.*` requests and a Checkpoints list in the
  dock; the mirror bot and its setup in `deploy/`; restore-to-checkpoint; the
  explicit import of outside commits. Build this together with login and
  authority (who may create checkpoints, restore or configure the mirror).

## Turned Down

### Git in every editor ("git pull / git push" buttons)
This is familiar, but it puts merges and conflicts back into a live system,
and every person needs credentials and LFS set up. Rejected.

### The server writes the Git commits itself
The server has the log and the blobs but cannot turn scene operations back
into `.tscn` files without Godot. Rejected unless snapshots
([ADR 0003](0003-snapshots.md)) someday store full scene files.

### Auto-commit on a timer
Commits with no meaning ("autosave 14:05") clutter the history the team wants
to read. It is kept as an optional mirror setting in addition to checkpoints,
not instead of them.
