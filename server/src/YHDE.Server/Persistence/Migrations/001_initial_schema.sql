-- Migration 001: initial schema
-- Implements the data model from database.md.
-- The operations table is only ever added to: the server never updates or
-- deletes a row, except when a whole project is deleted on the admin page.

-- Projects
CREATE TABLE IF NOT EXISTS projects (
    project_id  UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    name        TEXT        NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Branches
-- Each branch is an independent seq-space (versioning.md).
-- base_branch_id and base_seq record where it started (for branching later).
CREATE TABLE IF NOT EXISTS branches (
    branch_id       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    project_id      UUID        NOT NULL REFERENCES projects(project_id),
    name            TEXT        NOT NULL,
    head_seq        BIGINT      NOT NULL DEFAULT 0,
    base_branch_id  UUID        REFERENCES branches(branch_id),
    base_seq        BIGINT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (project_id, name)
);

-- Operations: the immutable, append-only log (operation_system.md,
-- database.md).
-- The UNIQUE (branch_id, seq) constraint enforces the authoritative order.
-- No UPDATE or DELETE is ever issued against this table in normal operation.
CREATE TABLE IF NOT EXISTS operations (
    op_id           UUID        PRIMARY KEY,
    seq             BIGINT      NOT NULL,
    branch_id       UUID        NOT NULL REFERENCES branches(branch_id),
    type            TEXT        NOT NULL,
    target_id       UUID        NOT NULL,
    payload         JSONB       NOT NULL,
    actor_id        UUID        NOT NULL,
    session_id      UUID        NOT NULL,
    client_op_ref   UUID        NOT NULL,
    parent_seq      BIGINT      NOT NULL,
    prev_signature  BYTEA       NOT NULL DEFAULT ''::BYTEA,
    signature       BYTEA       NOT NULL DEFAULT ''::BYTEA,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (branch_id, seq)
);

-- Primary read path: tail-of-log fetch and replay (database.md)
CREATE INDEX IF NOT EXISTS idx_operations_branch_seq
    ON operations (branch_id, seq);

-- Per-object history (scene / node history)
CREATE INDEX IF NOT EXISTS idx_operations_target
    ON operations (target_id);

-- Per-user history and audit
CREATE INDEX IF NOT EXISTS idx_operations_actor
    ON operations (actor_id, created_at);

-- Lookup by client_op_ref. Not unique: duplicates are stopped by op_id, the
-- primary key (a resend keeps its op_id and gets DuplicateOpId).
CREATE INDEX IF NOT EXISTS idx_operations_client_op_ref
    ON operations (client_op_ref);

-- Scenes (metadata only: content is a projection of operations)
CREATE TABLE IF NOT EXISTS scenes (
    scene_id    UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    project_id  UUID        NOT NULL REFERENCES projects(project_id),
    name        TEXT        NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (project_id, name)
);

-- Audit log: security-relevant events (database.md, security.md)
CREATE TABLE IF NOT EXISTS audit_log (
    audit_id    BIGSERIAL   PRIMARY KEY,
    event_type  TEXT        NOT NULL,
    actor_id    UUID,
    project_id  UUID,
    branch_id   UUID,
    target_id   UUID,
    detail      JSONB,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_audit_log_actor
    ON audit_log (actor_id, created_at);

CREATE INDEX IF NOT EXISTS idx_audit_log_project
    ON audit_log (project_id, created_at);

-- Seed: a default project and its main branch, for development
INSERT INTO projects (project_id, name)
VALUES ('ffffffff-0000-0000-0000-000000000001', 'default')
ON CONFLICT DO NOTHING;

INSERT INTO branches (branch_id, project_id, name, head_seq)
VALUES ('ffffffff-0000-0000-0000-000000000002', 'ffffffff-0000-0000-0000-000000000001', 'main', 0)
ON CONFLICT DO NOTHING;
