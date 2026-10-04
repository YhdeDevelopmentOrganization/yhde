# ADR 0013: The Server Hands Out the Add-on and Updates It

- Status: Built
- Date: 2026-09-25
- See also: [onboarding.md](../onboarding.md), [projects.md](../projects.md),
  [security.md](../security.md), [ADR 0011](0011-projects-and-invite-codes.md)

## Situation

Getting a new person into a project took several hand-offs: install Godot,
get the `addons/yhde` folder (with a native library that is not in git) from
someone, make or open a project, then type the server address and an invite
code. Every add-on update meant sending the folder to everyone again, and
people ran mismatched versions. Team members often have no access to the
repository or its build artifacts.

The server is already the one place everyone trusts and reaches, and invite
codes already tell it which project a person belongs to
([ADR 0011](0011-projects-and-invite-codes.md)).

## Decision

1. The server keeps one add-on package, uploaded by the operator on the admin
   page. It is checked (the add-on's files, a version, a native core, safe
   names), stored normalized under `addons/yhde/`, and described by a
   manifest with its SHA-256. Nothing in it runs on the server.
2. A public join page hands out the package alone, or, for a live invite
   code, a starter project: an empty Godot project named after the game with
   YHDE enabled and a join file holding the server address and code. The
   editor consumes and deletes the join file on first start.
3. Editors compare their add-on version with the server's manifest when they
   connect. A newer package with this platform's native core is offered, and
   installing it verifies the SHA-256 and that every file is under
   `addons/yhde/` before writing anything, then asks for a restart.
4. Updates are offered, never applied without a click. An uploaded package
   is staged and reaches nobody until the admin releases it
   ([ADR 0014](0014-updates-approved-on-the-admin-page.md)).

## Why

- One upload replaces per-person hand-offs; versions converge on what the
  team's server hands out.
- The editor already trusts this server with the whole project, including
  scripts that run in the editor, so taking the add-on from it adds no new
  party to trust. The checksum guards against a damaged download, not
  against the server.
- Invite codes stay out of URLs (form body), and the starter holds only the
  code the person already typed in.

## Consequences

- Gains: a new person needs Godot, the join page and a code; updates reach
  everyone in one click.
- Cost: whoever controls the admin page can hand everyone new
  editor code. This is the same trust as today's shared scripts, but it is
  now explicit; accounts should limit uploads to owners and could
  add package signing.
- Cost: the native core is not in git, so the operator builds or
  downloads it and packs it (`client/package_addon.py`); platforms without a
  library in the package are not offered updates.
- Later: signed packages; building the package in CI; uploading
  a release straight from GitHub.

## Turned Down

### Send the folder around (before)
It needs no server work, but every update is a round of messages and people
end up on different versions. Rejected.

### Godot Asset Library
It is public, has a review delay, and has no per-server project starter.
Useful later for discovery, not for a team's own server. Rejected for now.

### Download from GitHub releases
It needs repository access and a CI build (Actions minutes), and it cannot
name the team's project or carry the invite code. Rejected.

## Changes Since

The admin page now has staff accounts with Admin and Support roles; only
admins can upload and release the add-on. Most people now install the add-on
from the dashboard (AssetLib, Import) and sign in, instead of using a starter
project. The add-on is also published in `YhdeDevelopmentOrganization/yhde-godot` for the
Godot Asset Library, and the native libraries are built by the GitHub Actions
workflow.
