# ADR 0014: Updates Approved on the Admin Page

- Status: Built
- Date: 2026-09-26
- See also: [deployment.md](../deployment.md), [admin.md](../admin.md),
  [onboarding.md](../onboarding.md), [security.md](../security.md),
  [ADR 0013](0013-addon-from-the-server.md)

## Situation

Updating a YHDE server meant logging in over SSH and running the server
kit's update command, and nobody saw that an update existed until they looked
on GitHub. Updating automatically is not an option: a broken commit would take
down the server for every team on it, and editing sessions would be
interrupted at random times. The add-on had the same problem the other way
round: an upload on the admin page reached every editor at once, with no
chance to hold it back.

The server runs as a locked-down container (read-only disk, no shell, no
Docker access). Giving it the Docker socket or git would let anyone who
compromises the server take over the host.

## Decision

1. A timer on the host (installed by the server kit) looks for newer commits
   of the branch the server follows, at most every 10 minutes or on request,
   and writes a report into a folder the server can read.
2. The admin page lists the newer versions. Nothing is installed until the
   admin chooses a version. The server only writes that choice (a commit id)
   into the same folder.
3. The host applies a request only for a newer commit of the same branch:
   database backup, fast-forward, rebuild, health check. If the new version
   does not build or does not come up healthy, it returns to the previous
   commit. The outcome is shown on the admin page.
4. An uploaded add-on is staged. Editors, download links and the join page
   keep the released add-on until the admin releases the staged one.

## Why

- An update happens when a person decides, so a broken commit is never
  installed by surprise, and a failing one undoes itself.
- The server stays unable to run anything on the host: the host decides what
  a request may do (security is mandatory).
- A report-and-request folder keeps the two sides simple and auditable (the
  update log and the last result are files on the host).

## Consequences

- Gains: updates are visible and one click; failed updates roll back; the
  add-on is released deliberately.
- Cost: the updater is part of the server kit (systemd on the
  host); servers run another way update by hand as before.
- Cost: the rollback covers code, not database migrations. A
  migration that ran before a failure stays (migrations are additive and
  idempotent), and the backup taken first is the way back.
- Cost: with a private repository the checker cannot show
  GitHub's build results without a token; it relies on its own build and
  health check.
- Later: show CI results with an optional token; release notes per
  version; approve an add-on and a server version together.

## Turned Down

### Update automatically
It is the least work for the admin, but one broken commit takes everyone
down and sessions break at random times. Rejected.

### Give the server the Docker socket
The server could update itself, but a compromised server would own the
host. Rejected.

### Watchtower-style image updates
They need published images (a registry and CI builds), still update without
asking, and do not roll back on a failed health check. Rejected.
