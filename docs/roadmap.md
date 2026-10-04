# Roadmap

What is done, what comes next and what may come later. How each part works is
in its own document; the goals are in [vision.md](vision.md).

## 1. How We Decide the Order

1. The log comes first. Nothing ships unless it keeps the log correct.
2. The server decides from day one. No shortcut that trusts the client.
3. Correct first, then fast.
4. Reliability and security are part of every step, not a later phase.

## 2. Done

- The operation log: stored before broadcast, hash chained, undo on the
  server ([operation_system.md](operation_system.md)).
- Live editing of 2D, 3D and UI scenes and resources, checked by a
  two-editor end-to-end test that converges on more than 13,000 properties
  ([editor_client.md](editor_client.md)).
- Files: blob store, resumable transfers, dependency order, shared `.blend`
  imports, held mass deletions ([assets.md](assets.md)).
- Presence: cursors, selections, drags, follow mode, "bring everyone here",
  who is where in the editor ([presence.md](presence.md)).
- Chat, direct messages and comments pinned in scenes
  ([social.md](social.md)).
- Several people typing in one script or shader
  ([text_editing.md](text_editing.md)).
- Many projects per server, invite links and starter projects
  ([projects.md](projects.md), [onboarding.md](onboarding.md)).
- Add-on updates handed out by the server.
- Accounts: email, GitHub and Google, signing in from Godot
  ([authentication.md](authentication.md)).
- Teams with plans, roles, access per project, promo codes and beta access
  codes ([teams.md](teams.md)).
- The website, the dashboard and the admin page with staff roles and an
  audit log ([admin.md](admin.md)).
- The server kit, daily backups and updates approved on the admin page
  ([deployment.md](deployment.md)).
- A load test: about 700 editors online per small server
  ([capacity.md](capacity.md)).

## 3. Next

- Payments with Stripe. Then making a team no longer needs an access code
  (`TEAMS_NEED_CODE=false`).
- The company details on the website (name, business ID, address).
- Native builds for macOS and Linux from the GitHub Actions workflow, and the
  add-on on the Godot Asset Library.
- Backups copied off the server.
- The move from `frostinteractive.fi` to the new domain.

## 4. Later

- Snapshots, so new editors on big projects start fast
  ([snapshots.md](snapshots.md)).
- Locks and finer roles ([authority.md](authority.md)), and conflict classes
  ([conflict_resolution.md](conflict_resolution.md)).
- Tags, rollback, branching and merging from the editor, and checkpoints
  mirrored to Git ([versioning.md](versioning.md),
  [ADR 0010](adr/0010-checkpoints-and-git-mirror.md)).
- Signed native builds.
- A plugin API ([plugin_api.md](plugin_api.md)).
- Point-in-time recovery for the database.

## 5. Ideas

From the original spec, not planned yet. The log already records what they
would need.

- Offline editing with delayed sync
- Replaying a session
- Scene and file diff viewers
- Debugging together
- Tasks and issue tracking
- Help with merging, possibly AI-assisted
- Team statistics beyond the dashboard

## See Also

- [vision.md](vision.md), [architecture.md](architecture.md), [CHANGELOG.md](../CHANGELOG.md)
