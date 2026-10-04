# Conflict Resolution

What happens when two people change the same thing at the same time. The
original decision is [ADR 0006](adr/0006-conflict-resolution.md). What is
built today is simpler than that plan (section 3).

## 1. The Problem

Two people can make a change against the same `parent_seq` before either
reaches the server. The server has to pick one outcome, and every editor has
to end up with the same result.

## 2. How It Works Today

- The server gives each branch one order ([operation_system.md](operation_system.md) §7).
  Operations are applied in that order everywhere, so all editors end up in
  the same state.
- Changes to different things (different nodes, or different properties of
  one node) don't clash; they are simply applied one after the other. This is
  what lets a team edit one scene together.
- Two changes to the same property: the one stored later wins. Both stay in
  the log, and the earlier author's editor moves to the stored value.
- A change to something that no longer exists (a node another person just
  deleted) is skipped when it is applied. Undo works the same way: the server
  refuses an undo that later operations make unsafe, and says why.
- Scripts are different: typing at the same time is merged, not overwritten.
  The server adjusts each edit for the ones stored before it
  ([text_editing.md](text_editing.md)).
- Files: the later `UpdateAsset` wins. An update made against an old version of
  a file still wins by order, and the editor keeps a copy of local unsent
  changes it overwrites ([assets.md](assets.md) §5).

The result only depends on the log, so replaying it always gives the same
outcome.

## 3. Planned: Classes and Locks

[ADR 0006](adr/0006-conflict-resolution.md) plans a class per kind of
operation:

| Class | Rule | For |
|---|---|---|
| Latest wins | The later `seq` wins. | Transforms, properties |
| Authority wins | The person with the higher role wins; the other operation is refused. | Hierarchy: create, delete, move |
| Manual | Held until someone decides. | Destructive operations |

Together with locks ([authority.md](authority.md) §3) and a `conflict_log`
table that records every clash and how it ended. None of this is built yet.

## 4. Rules

1. The server decides, at commit time.
2. Every editor ends up with the same state.
3. The outcome depends only on the log, never on timing.
4. Both sides of a clash stay in the log.

## See Also

- [operation_system.md](operation_system.md), [authority.md](authority.md),
  [text_editing.md](text_editing.md), [versioning.md](versioning.md)
