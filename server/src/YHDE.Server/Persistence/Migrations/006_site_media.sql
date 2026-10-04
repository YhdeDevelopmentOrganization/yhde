-- 006: pictures for news posts and release notes (admin.md "Site")

-- Images uploaded on the admin page. The bytes live in the blob store under
-- their SHA-256 hash; only hashes listed here are served publicly at
-- /media/<hash>, and the blob clean-up after deleting a project keeps them.
CREATE TABLE IF NOT EXISTS site_media (
    hash          TEXT        PRIMARY KEY,
    content_type  TEXT        NOT NULL,
    size          BIGINT      NOT NULL,
    name          TEXT        NOT NULL DEFAULT '',
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- A post's cover picture: an empty string or /media/<hash>.
ALTER TABLE site_posts ADD COLUMN IF NOT EXISTS cover TEXT NOT NULL DEFAULT '';
