-- 013: invitations that can be accepted from the email, free seats given by
-- the operator, and no editor history for deleted accounts (teams.md, admin.md)

-- The email's accept link carries a secret; only its SHA-256 hash is kept.
-- Invitations sent before this have none and are accepted on the dashboard.
ALTER TABLE team_invites ADD COLUMN IF NOT EXISTS token_hash BYTEA;
CREATE UNIQUE INDEX IF NOT EXISTS idx_team_invites_token ON team_invites (token_hash) WHERE token_hash IS NOT NULL;

-- Seats the operator gives a team free of charge, on top of its plan.
ALTER TABLE teams ADD COLUMN IF NOT EXISTS free_seats INT NOT NULL DEFAULT 0 CHECK (free_seats >= 0);

-- Editor sessions of accounts that no longer exist lose who they were, so
-- deleted people stop showing on the admin page. The audit log names every
-- deleted account (deleted by the person, by an admin, or a test account).
UPDATE session_log SET member_id = NULL, member_name = ''
 WHERE member_id IS NOT NULL
   AND member_id NOT IN (SELECT user_id FROM users)
   AND member_id IN (
       SELECT actor_id FROM audit_log WHERE event_type = 'account.deleted' AND actor_id IS NOT NULL
       UNION SELECT target_id FROM audit_log WHERE event_type IN ('admin.user_deleted', 'admin.test_account_created') AND target_id IS NOT NULL);

-- From now on a signed-in editor's session says so, so the admin page can
-- tell accounts from invite codes and the server key.
UPDATE session_log SET via = 'account' WHERE member_id IN (SELECT user_id FROM users);
