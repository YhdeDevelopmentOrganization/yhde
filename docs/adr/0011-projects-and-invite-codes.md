# ADR 0011: Many Projects per Server, Joined with Invite Codes

- Status: Built
- Date: 2026-09-25
- See also: [projects.md](../projects.md), [admin.md](../admin.md),
  [authentication.md](../authentication.md), [security.md](../security.md),
  [ADR 0004](0004-authority-system.md)

## Situation

The data model always had projects and branches, but only one of each was
seeded and nothing could create more. Every editor connected to the seeded
project with one server-wide access key. So one server meant one game, and
anyone with the key could reach everything on it. A team making two games, or
a studio hosting several teams, needs one server to hold many games with
separate access.

Two more problems came with that. Nothing tied a local folder to a server
project, so opening a different game's folder and pressing Connect would mix
the two games' files. And accounts are not built yet, so the answer
cannot depend on them.

## Decision

1. A server holds many projects. The operator creates, renames and archives
   them on the admin page ([admin.md](../admin.md)); each gets a `main`
   branch.
2. People join a project with an invite code: 80 random bits, shown once,
   stored only as a SHA-256 hash, labelled and revocable. A code opens exactly
   one project. `Welcome` tells the editor which project it is, so people
   paste the code and the address and nothing else.
3. The server access key remains the operator's key to every project.
4. The editor keeps local state per project and branch, and a folder
   remembers its project after its first connection.
5. The first connection of a folder to a project that already has files is
   held, with nothing sent or received, when fewer than half of the folder's
   files are in the project. A person must confirm it is the right folder.
6. Accounts replace invite codes with invitations to accounts and
   roles. Codes are the bridge until then, not a permission system.

## Why

- Per-project codes give real separation (a code cannot reach another
  project) without accounts, and the operator keeps control (revoke, archive).
- Hash-only storage and one-time display follow "security is mandatory": a
  leaked database does not leak working codes.
- The join check addresses the most damaging mistake that separate projects
  make possible, silently merging two games, before any data moves.

## Consequences

- Gains: one server for many games; people get only the game they were
  invited to; joining is paste-and-connect.
- Cost: inside a project everyone is still equal, and names are
  self-declared. Codes can be shared further by whoever has them (revoke and
  re-issue is the remedy).
- Cost: the blob store stays shared between projects (dedup). A
  person could only fetch another project's file by knowing its SHA-256, which
  requires already having its bytes.
- Cost: an archived project can be deleted from the admin page
  (typed name, nobody connected). That removes its operation log, the one
  exception to "operations are never deleted", and is recorded in the audit
  log.
- Later: accounts, roles and invitations; per-project
  storage quotas; branches in the UI.

## Turned Down

### One server per game
This works today, but it multiplies servers, backups and cost for small teams.
Rejected as the only option (still possible).

### Server key plus a project picker
It is simpler, but everyone with the key sees and can open every project.
Rejected: it gives no separation.

### Wait for accounts
This leaves the "every game on one key" problem in place until accounts exist, and
codes cost little to build now. Rejected.

## Changes Since

Accounts, teams, roles and access per project now exist
([ADR 0015](0015-accounts-and-sign-in.md), [teams.md](../teams.md)). Most
people sign in from Godot instead of using a code; invite codes remain for
invite links, and the join check still guards every folder.
