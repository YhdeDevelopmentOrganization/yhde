-- 007: accounts and sign-in (ADR 0015, authentication.md)

-- One person. email is stored lower-case; a NULL password_hash means the
-- account signs in only with a linked provider (GitHub, Google).
CREATE TABLE IF NOT EXISTS users (
    user_id            UUID        PRIMARY KEY,
    email              TEXT        NOT NULL UNIQUE,
    email_verified_at  TIMESTAMPTZ,
    display_name       TEXT        NOT NULL DEFAULT '',
    password_hash      TEXT,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    disabled_at        TIMESTAMPTZ
);

-- Linked sign-in providers: one row per provider identity.
CREATE TABLE IF NOT EXISTS oauth_links (
    provider          TEXT        NOT NULL CHECK (provider IN ('github', 'google')),
    provider_user_id  TEXT        NOT NULL,
    user_id           UUID        NOT NULL REFERENCES users(user_id),
    email             TEXT        NOT NULL DEFAULT '',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (provider, provider_user_id)
);

CREATE INDEX IF NOT EXISTS idx_oauth_links_user ON oauth_links (user_id);

-- Signed-in browsers and editors. Only the SHA-256 of each token is kept.
-- kind 'web': the website cookie. kind 'editor': a Godot editor's sign-in
-- token from the device flow.
CREATE TABLE IF NOT EXISTS user_sessions (
    session_id    UUID        PRIMARY KEY,
    user_id       UUID        NOT NULL REFERENCES users(user_id),
    kind          TEXT        NOT NULL CHECK (kind IN ('web', 'editor')),
    token_hash    BYTEA       NOT NULL UNIQUE,
    device        TEXT        NOT NULL DEFAULT '',
    ip            TEXT        NOT NULL DEFAULT '',
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at    TIMESTAMPTZ NOT NULL,
    revoked_at    TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_user_sessions_user ON user_sessions (user_id, created_at);

-- One-time links sent by email: verify an address, reset a password.
CREATE TABLE IF NOT EXISTS email_tokens (
    token_hash  BYTEA       PRIMARY KEY,
    user_id     UUID        NOT NULL REFERENCES users(user_id),
    purpose     TEXT        NOT NULL CHECK (purpose IN ('verify', 'reset')),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at  TIMESTAMPTZ NOT NULL,
    used_at     TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_email_tokens_user ON email_tokens (user_id, purpose);
