# ADR 0006: Conflict Handling by Kind of Operation

- Status: Accepted, partly built
- Date: 2026-06-16
- See also: [conflict_resolution.md](../conflict_resolution.md), [authority.md](../authority.md)

## Situation

Live editing means two people make changes against the same state. The server
gives each branch one order ([ADR 0002](0002-server-authoritative.md)), but it
still has to decide what happens when two operations touch the same thing,
and every editor has to end up in the same place. Overwriting a position is
fine; quietly merging two changes to the node tree, or two deletions, is not.

## Decision

Decide at commit time, by a class set per operation type:

- Latest wins: the later `seq` wins. For transforms and properties.
- Authority wins: the person with the higher role wins; the other operation
  is refused. For changes to the node tree.
- Manual: held for a person to decide. For destructive operations.

Hard locks ([ADR 0004](0004-authority-system.md)) prevent the worst clashes
before they happen. Every clash is logged.

## Why

- Each kind of edit gets fitting handling: cheap where it's safe, precedence
  where structure matters, a person where a loss can't be undone.
- The outcome depends only on stored, ordered data, so every editor and every
  replay ends in the same state.
- Locks handle the rest, so the common case stays smooth.

## Costs

- Latest wins drops the earlier value (the operation stays in the history).
- Authority wins refuses the losing change, which its author has to redo.
- Manual handling needs a person.

## Turned Down

1. One rule for everything, such as always latest wins. Unsafe for the node
   tree and deletions.
2. CRDT merging for every type. Changes to structure have no safe automatic
   merge, and it adds complexity the server's single order doesn't need.
3. Always manual. Unworkable for edits that happen many times a second.
4. Operational transformation for everything. Built for text; used only for
   scripts ([ADR 0012](0012-live-text-editing.md)).

## Where It Stands

Built: one order per branch, later wins for the same property, operations on
removed nodes skipped, text merged by operational transformation. Not built:
authority wins, manual handling and the conflict log
([conflict_resolution.md](../conflict_resolution.md)).
