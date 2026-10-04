# Database

What PostgreSQL stores and why. File contents are not in the database; they
are in the blob store on disk ([assets.md](assets.md)). The schema is built by
the migrations in `server/src/YHDE.Server/Persistence/Migrations/`, which run
when the server starts.

## 1. Why PostgreSQL

- Transactions: an operation is stored before it is broadcast, so a stored
  edit is never lost ([reliability.md](reliability.md)).
- Indexes that fit an ordered log.
- JSONB for operation payloads, which differ by type.

## 2. Tables

| Area | Tables | Migration |
|---|---|---|
| Log | `projects`, `branches`, `scenes`, `operations` | 001, 003 |
| Chat and comments | `chat_messages`, `comment_threads`, `comment_messages` | 002 |
| Projects | `project_invites`, `download_links` | 003, 004 |
| Admin | `audit_log`, `session_log` | 001, 004 |
| Website | `site_settings`, `site_posts`, `site_media`, `early_access` | 005, 006 |
| Accounts | `users`, `user_sessions`, `oauth_links`, `email_tokens` | 007 |
| Teams | `teams`, `team_members`, `team_invites`, `project_access` | 008, 010 |
| Promo codes | `promo_codes`, `promo_redemptions` | 009, 011 |

## 3. The Operation Log

`operations` is only ever added to. The server never updates or deletes a
row; a correction is a new operation.

| Column | Type | |
|---|---|---|
| `op_id` | UUID, primary key | Also how duplicates are dropped after a reconnect. |
| `seq` | BIGINT | Order within the branch, set by the server. Unique with `branch_id`. |
| `branch_id` | UUID | |
| `type` | TEXT | See [operation_system.md](operation_system.md) §3. |
| `target_id` | UUID | The node, resource or file. |
| `payload` | JSONB | The change, with the old value for undo. |
| `actor_id` | UUID | Who, from the session. |
| `session_id` | UUID | Which connection. |
| `client_op_ref` | UUID | The editor's own id for it. |
| `parent_seq` | BIGINT | What the editor had seen. |
| `prev_signature`, `signature` | BYTEA | The hash chain. |
| `created_at` | TIMESTAMPTZ | When it was stored. |

Indexes: `(branch_id, seq)` for replay and catching up, `target_id` for the
history of one object, `(actor_id, created_at)` for one person's history.

The hash chain ([HashChain.cs](../server/src/YHDE.Server/Persistence/HashChain.cs)):

```
signature = SHA-256(prev_signature || op_id || actor_id || branch_id
                    || seq (8 bytes, big-endian) || type || payload)
```

Changing any stored row breaks the chain from that row on, so tampering
shows ([security.md](security.md)).

Each branch has one writer, `BranchCommitter`: whatever is waiting is
committed together in one transaction, and only then broadcast, in log
order.

## 4. Projects and Branches

- `projects`: name, team (`team_id`) and when it was archived.
- `branches`: lines of history in a project, each with its own `seq`. Every
  project has `main`. See [versioning.md](versioning.md).
- `scenes`: only the scene's id and metadata. What a scene contains comes
  from the log.

## 5. Accounts and Teams

- `users`: email, name, password hash (if any), whether the email is
  confirmed, `staff_role` (admin or support, for the admin page) and
  `is_test` for test accounts.
- `user_sessions`: website and editor sign-ins, stored as hashes of the
  tokens.
- `oauth_links`: linked GitHub and Google accounts.
- `teams`, `team_members` (role owner, admin or member), `team_invites`.
- `project_access`: a member's access to one project when it is less than
  edit. No row means edit.

Details: [authentication.md](authentication.md), [teams.md](teams.md).

## 6. What Is Not in the Database

- File contents: in the blob store ([assets.md](assets.md)).
- Presence: in memory only ([presence.md](presence.md)).
- Scene files: they are rebuilt from the log.
- Editor caches: they can always be rebuilt.

Snapshots, locks and a conflict log are planned but have no tables yet
([snapshots.md](snapshots.md), [authority.md](authority.md),
[conflict_resolution.md](conflict_resolution.md)).

## 7. Backups

The server kit takes a compressed `pg_dump` of the whole database once a day
and when it starts, keeps 14 days by default (`KEEP_DAYS`) and copies the
blob store alongside ([deployment.md](deployment.md)). The log is the
project, so restoring the database brings back every project's state.

## See Also

- [operation_system.md](operation_system.md), [assets.md](assets.md),
  [versioning.md](versioning.md), [reliability.md](reliability.md),
  [ADR 0001](adr/0001-operation-log.md)
