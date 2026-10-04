# Decision Records

The big decisions behind YHDE, one per file: the situation, what was decided,
why, what it costs and what was turned down. How each part works in detail is
in the documents one folder up.

An accepted record is not rewritten. To change a decision, add a new record
that replaces the old one.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-operation-log.md) | The operation log is the truth | Accepted |
| [0002](0002-server-authoritative.md) | The server decides | Accepted |
| [0003](0003-snapshots.md) | Snapshots to limit replay | Accepted, not built yet |
| [0004](0004-authority-system.md) | Roles and locks | Accepted, partly built |
| [0005](0005-asset-versioning.md) | Files stored by content hash | Accepted |
| [0006](0006-conflict-resolution.md) | Conflict handling by kind of operation | Accepted, partly built |
| [0007](0007-editor-client-capture.md) | The editor compares state and replays in log order | Accepted |
| [0008](0008-social-channel.md) | Chat and comments outside the log | Built |
| [0009](0009-shared-imports-and-dependencies.md) | Shared import results and files in dependency order | Built |
| [0010](0010-checkpoints-and-git-mirror.md) | Named checkpoints mirrored to Git | Proposed |
| [0011](0011-projects-and-invite-codes.md) | Many projects per server, joined with invite codes | Built |
| [0012](0012-live-text-editing.md) | Live text editing merged on the server | Built |
| [0013](0013-addon-from-the-server.md) | The server hands out the add-on and its updates | Built |
| [0014](0014-updates-approved-on-the-admin-page.md) | Updates only when approved on the admin page | Built |
| [0015](0015-accounts-and-sign-in.md) | Accounts on the website, Godot signs in through it | Built |
| [0016](0016-people-belong-to-projects.md) | People belong to projects, not teams | Built |

Start with 0001, which everything else depends on, then 0002.
