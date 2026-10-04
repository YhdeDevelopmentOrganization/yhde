# ADR 0002: The Server Decides

- Status: Accepted
- Date: 2026-06-16
- See also: [architecture.md](../architecture.md), [security.md](../security.md),
  [operation_system.md](../operation_system.md)

## Situation

YHDE puts a native client in the hands of many people, not all of them
trustworthy. Live editing needs one place that decides the order and validity
of changes. Any client can be cracked, so no client can be trusted.

The question: do the clients decide (peer to peer, eventual consistency), or
the server?

## Decision

The C# server decides. Clients propose operations; only the server checks,
authorizes, orders (gives each a `seq`), commits and broadcasts them. Undo is
built by the server. Clients hold only state they can rebuild.

A modified client gets no extra rights and can't damage a project, because
the server checks everything again.

## Why

- One writer per branch gives one clear order, which the whole model needs
  ([ADR 0001](0001-operation-log.md)).
- Trust lives on the server. The native core is C++ for speed, and no one
  pretends it can't be cracked ([security.md](../security.md)).
- Every operation passes one permission check ([authority.md](../authority.md)).
- The server stores before it broadcasts, which is what makes "no edit is
  lost" possible ([reliability.md](../reliability.md)).

## Costs

- The server is on the path of every edit. Editors show changes right away,
  but a change can be undone if the server refuses it.
- Confirming an edit takes a round trip (showing it doesn't).
- One writer per branch limits how many edits a branch can take. Measured, it
  is far from the limit ([capacity.md](../capacity.md)).

In return: tampered clients, faked identities and doing more than allowed are
stopped in one place, and every editor ends up in the same state.

## Turned Down

1. Peer to peer or CRDTs with no central authority. No way to enforce access,
   hard to audit, and hard to reason about for changes to the node tree.
2. Clients decide and the server only relays. A cracked client could damage
   everyone's project.
3. Clients decide and the server reconciles later. Undoing changes that were
   never allowed is both a security hole and confusing for people.
