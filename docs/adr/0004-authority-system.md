# ADR 0004: Roles and Locks

- Status: Accepted, partly built
- Date: 2026-06-16
- See also: [authority.md](../authority.md), [conflict_resolution.md](../conflict_resolution.md)

## Situation

People with different jobs (owners, programmers, artists, testers, people who
only watch) edit one project. We need to control who may do what, and to
avoid clashes where merging is unsafe, all checked on the server
([ADR 0002](0002-server-authoritative.md)).

That is two things: what someone may do at all (a role), and who is working
on a node right now (a lock).

## Decision

Roles and locks:

- Six roles, Owner, Admin, Developer, Artist, QA and Viewer, mapped to what
  kinds of operation each may make. The mapping is data, not code.
- Three lock states, unlocked, soft and hard, on single targets. Locks have a
  lease and are released when the holder disconnects or times out.
- Default locks: soft for transforms and properties, hard for hierarchy
  changes and resource deletion.

The server checks every operation before it is validated and committed. The
editor only mirrors the rules to hide what isn't allowed.

## Why

- Roles give clear permissions that fit a team and can be audited.
- Locks stop clashes before they happen where merging is unsafe (structure,
  deletions). They add to conflict handling
  ([ADR 0006](0006-conflict-resolution.md)); they don't replace it.
- A data-driven mapping can grow without core changes.
- Leases mean a crashed editor can't freeze a node.

## Costs

- A permission check on every operation (an in-memory lookup).
- Locks add state and edge cases: acquire, lease, release, force release.
- People have to understand the default locks.

## Turned Down

1. Permissions only, no locks. Leaves every clash to after the fact, which is
   unsafe for the node tree and deletions.
2. Locks only, no roles. A viewer could still edit anything unlocked.
3. Role checks written into each handler. Can't grow.
4. Only version checks. They say nothing about who is working where.

## Where It Stands

Built in a simpler form: team roles (owner, admin, member) and access per
project (edit, view, none), checked by the server on every operation
([teams.md](../teams.md), [authority.md](../authority.md)). The six roles
and locks are not built yet.
