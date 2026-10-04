-- 004: download links and connection history (admin.md, onboarding.md)

-- Download links: https://<server>/join/<token> downloads a ready project.
-- A link is not a way in: each download gets its own invite code (below), so
-- a link can expire while the people who used it keep working. Tokens are
-- stored only as SHA-256 hashes.
CREATE TABLE IF NOT EXISTS download_links (
    link_id     UUID        PRIMARY KEY,
    project_id  UUID        NOT NULL REFERENCES projects(project_id),
    token_hash  BYTEA       NOT NULL UNIQUE,
    label       TEXT        NOT NULL DEFAULT '',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at  TIMESTAMPTZ,
    max_uses    INT,
    uses        INT         NOT NULL DEFAULT 0,
    revoked_at  TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_download_links_project
    ON download_links (project_id, created_at);

-- The invite code a download link made (NULL: made by hand on the admin page).
ALTER TABLE project_invites ADD COLUMN IF NOT EXISTS link_id UUID;

-- Who was connected, to which project, and for how long (admin statistics).
-- One row per connection; last_seen_at is refreshed every minute while it is
-- open, so a server restart never leaves a connection "online forever".
CREATE TABLE IF NOT EXISTS session_log (
    session_id      UUID        PRIMARY KEY,
    member_id       UUID,
    member_name     TEXT        NOT NULL DEFAULT '',
    project_id      UUID,
    client_version  TEXT        NOT NULL DEFAULT '',
    via             TEXT        NOT NULL DEFAULT '',
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at        TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_session_log_project
    ON session_log (project_id, started_at);

CREATE INDEX IF NOT EXISTS idx_session_log_member
    ON session_log (member_id, started_at);

-- Statistics read operations by time.
CREATE INDEX IF NOT EXISTS idx_operations_created
    ON operations (created_at);
