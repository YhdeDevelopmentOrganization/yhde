-- 030: invite links that let people edit (teams.md §4, onboarding.md §3).
-- A link is 'view' (as before: each download gets a watch-only invite code)
-- or 'edit': each download carries a one-time join ticket instead. The
-- ticket opens nothing by itself; a signed-in account redeems it and becomes
-- a member who edits, under the same checks as a join code.

ALTER TABLE download_links ADD COLUMN IF NOT EXISTS access TEXT NOT NULL DEFAULT 'view'
    CHECK (access IN ('view', 'edit'));

-- Only a SHA-256 hash of each ticket is kept. A ticket from a link dies with
-- the link (turned off: not usable; deleted: gone). A ticket made for one
-- account (the owner's own download, an accepted invitation) has user_id and
-- no link, and only that account can redeem it.
CREATE TABLE IF NOT EXISTS join_tickets (
    ticket_id    UUID        PRIMARY KEY,
    project_id   UUID        NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    link_id      UUID        REFERENCES download_links(link_id) ON DELETE CASCADE,
    user_id      UUID        REFERENCES users(user_id) ON DELETE CASCADE,
    token_hash   BYTEA       NOT NULL UNIQUE,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at   TIMESTAMPTZ NOT NULL,
    redeemed_at  TIMESTAMPTZ,
    redeemed_by  UUID        REFERENCES users(user_id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_join_tickets_link ON join_tickets (link_id) WHERE link_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_join_tickets_project ON join_tickets (project_id);
