-- Migration 002: chat and comments (social.md).
-- People talking about the project. Stored for everyone who was away, never
-- part of the operation log. Nothing is hard-deleted: a removed comment keeps
-- its row (deleted_at) and every edit or removal is written to audit_log.

-- Chat: one channel per project plus direct messages.
-- recipient_id NULL = the project channel; otherwise a direct message
-- between author_id and recipient_id.
CREATE TABLE IF NOT EXISTS chat_messages (
    message_id    UUID        PRIMARY KEY,
    project_id    UUID        NOT NULL REFERENCES projects(project_id),
    author_id     UUID        NOT NULL,
    author_name   TEXT        NOT NULL,
    recipient_id  UUID,
    body          TEXT        NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_chat_project_channel
    ON chat_messages (project_id, created_at) WHERE recipient_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_chat_direct
    ON chat_messages (project_id, author_id, recipient_id, created_at) WHERE recipient_id IS NOT NULL;

-- Comments: threads pinned in a scene (to a node, or a spot in the scene),
-- Figma style. Scoped to a branch like the scenes they talk about.
CREATE TABLE IF NOT EXISTS comment_threads (
    thread_id         UUID        PRIMARY KEY,
    project_id        UUID        NOT NULL REFERENCES projects(project_id),
    branch_id         UUID        NOT NULL REFERENCES branches(branch_id),
    scene             TEXT        NOT NULL,
    node_id           UUID,
    node_path         TEXT        NOT NULL DEFAULT '',
    anchor            JSONB       NOT NULL,
    author_id         UUID        NOT NULL,
    author_name       TEXT        NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resolved_at       TIMESTAMPTZ,
    resolved_by_name  TEXT
);

CREATE INDEX IF NOT EXISTS idx_comment_threads_branch
    ON comment_threads (branch_id, created_at);

CREATE TABLE IF NOT EXISTS comment_messages (
    message_id   UUID        PRIMARY KEY,
    thread_id    UUID        NOT NULL REFERENCES comment_threads(thread_id),
    author_id    UUID        NOT NULL,
    author_name  TEXT        NOT NULL,
    body         TEXT        NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    edited_at    TIMESTAMPTZ,
    deleted_at   TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_comment_messages_thread
    ON comment_messages (thread_id, created_at);
