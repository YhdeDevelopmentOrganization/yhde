-- 005: the public website's content (admin.md "Site")

-- News posts and release notes, written on the admin page. A post is live
-- when it is published and its publish time has come (NULL: at once), so a
-- published post with a future publish_at is "scheduled". Removing a post
-- only marks it (deleted_at); every change is also in audit_log.
CREATE TABLE IF NOT EXISTS site_posts (
    post_id     UUID        PRIMARY KEY,
    kind        TEXT        NOT NULL CHECK (kind IN ('news', 'release')),
    title       TEXT        NOT NULL,
    version     TEXT        NOT NULL DEFAULT '',
    summary     TEXT        NOT NULL DEFAULT '',
    body        TEXT        NOT NULL DEFAULT '',
    status      TEXT        NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'published')),
    publish_at  TIMESTAMPTZ,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at  TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_site_posts_live
    ON site_posts (kind, status, publish_at)
    WHERE deleted_at IS NULL;

-- Site-wide switches (announcement bar, maintenance mode), one JSON value per key.
CREATE TABLE IF NOT EXISTS site_settings (
    key         TEXT        PRIMARY KEY,
    value       JSONB       NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- The early-access list: email addresses left on the website.
CREATE TABLE IF NOT EXISTS early_access (
    email       TEXT        PRIMARY KEY,
    source      TEXT        NOT NULL DEFAULT '',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
