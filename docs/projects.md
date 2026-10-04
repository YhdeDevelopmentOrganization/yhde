# Projects and Invite Links

How one server holds many games, and how a folder in Godot ends up connected
to the right one. The decision is
[ADR 0011](adr/0011-projects-and-invite-codes.md). Teams and accounts are in
[teams.md](teams.md).

## 1. One Server, Many Games

A server holds any number of projects, one per game. Each has its own
branches (`main` is made with it), operation log, files, chat and comments.
Nothing is shared between projects except the blob store, which keeps
identical bytes once for everyone ([assets.md](assets.md) §3).

Projects are made on the dashboard, in Godot or on the admin page. An
archived project can't be opened and its invite links stop working; nothing
is deleted until someone deletes the project.

## 2. Ways In

Three kinds of secret are accepted as `Authorization: Bearer <secret>` on the
WebSocket and on the file routes:

| Secret | Opens | Who has it |
|---|---|---|
| Editor sign-in | The projects the person owns or was invited to | Everyone with an account ([authentication.md](authentication.md) §4) |
| Invite code `YHDE-XXXX-XXXX-XXXX-XXXX` | One project (read-only when a view link made it) | People who got a view link, or a code from the admin page |
| Server key (`Yhde:AccessKey`) | Every project | The operator only |

Invite codes:

- Made from a project's invite links (section 4), shown once, stored only as
  a SHA-256 hash.
- Can be labelled and revoked. A revoked code stops working at once when
  revoked on the admin page or dashboard, otherwise within 30 seconds
  (lookups are cached).
- Forgiving to type: case, dashes, spaces and the look-alikes O/0 and I/L/1
  don't matter. They are 80 random bits (Crockford base32).
- With a code, `Welcome` names the project (`project_id`, `project_name`,
  `branch_id`, [network_protocol.md](network_protocol.md) §4) and the editor
  subscribes to it. Subscribing to another project is refused (403).

## 3. One Folder, One Project

The editor keeps its local state (cache, queue, files, live text) per project
and branch. After the first connection the folder remembers its project in
the editor's project metadata (`yhde/project_id`, `branch_id`,
`project_name`).

The first time a folder connects to a project, the editor compares its files
with the project's, before anything is applied. If the folder has at least 8
files and fewer than half of them are in the project, nothing is sent or
received and the panel asks "Is this the right folder for Platformer?" with
It's the right folder or Disconnect. That stops two games from getting mixed.
An empty folder, or one that matches, joins without asking.

## 4. Starting, Inviting and Ending

- Start a project empty, or from a zip of the game (on the dashboard or the
  admin page). The zip's files become ordinary file operations
  ([admin.md](admin.md) §3).
- Invite teammates by email from the dashboard or the Godot panel
  ([teams.md](teams.md)), or send an invite link
  ([onboarding.md](onboarding.md)). Each download from a link is a ready
  project with its own invite code.
- End a project by archiving it (nobody can connect; it can be restored),
  then delete it if it should go ([admin.md](admin.md) §3).

## See Also

- [teams.md](teams.md), [onboarding.md](onboarding.md), [admin.md](admin.md),
  [security.md](security.md) §9, [ADR 0011](adr/0011-projects-and-invite-codes.md)
