-- Migration 003: many game projects per server, joined with invite codes
-- (projects.md). An invite code lets someone into one project; the server
-- access key remains the operator's key to all.

ALTER TABLE projects ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ;

-- Only a hash of each code is stored: a leaked database does not leak codes.
CREATE TABLE IF NOT EXISTS project_invites (
    invite_id   UUID        PRIMARY KEY,
    project_id  UUID        NOT NULL REFERENCES projects(project_id),
    code_hash   BYTEA       NOT NULL UNIQUE,
    label       TEXT        NOT NULL DEFAULT '',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at  TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_project_invites_project
    ON project_invites (project_id);

-- The seeded development project keeps its fixed id and gets a readable name.
UPDATE projects SET name = 'First project'
WHERE project_id = 'ffffffff-0000-0000-0000-000000000001' AND name = 'default';

-- Latest state of one file (asset and text lookups by target).
CREATE INDEX IF NOT EXISTS idx_operations_branch_target_seq
    ON operations (branch_id, target_id, seq);
