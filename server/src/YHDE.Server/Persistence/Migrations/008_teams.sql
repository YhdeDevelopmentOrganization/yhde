-- 008: teams on accounts (the website dashboard, PRODUCT.md plans)

-- A team: one owner who picks the plan, and the people they invite.
CREATE TABLE IF NOT EXISTS teams (
    team_id        UUID        PRIMARY KEY,
    name           TEXT        NOT NULL,
    owner_id       UUID        NOT NULL REFERENCES users(user_id),
    plan           TEXT        NOT NULL CHECK (plan IN ('solo', 'trio', 'team', 'studio')),
    period         TEXT        NOT NULL DEFAULT 'month' CHECK (period IN ('month', 'year')),
    extra_seats    INT         NOT NULL DEFAULT 0,
    extra_storage  INT         NOT NULL DEFAULT 0,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS team_members (
    team_id    UUID        NOT NULL REFERENCES teams(team_id),
    user_id    UUID        NOT NULL REFERENCES users(user_id),
    role       TEXT        NOT NULL CHECK (role IN ('owner', 'member')),
    joined_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (team_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_team_members_user ON team_members (user_id);

-- An invitation to an email address. The person accepts it on the dashboard
-- once signed in with that (verified) address, so no secret is needed.
CREATE TABLE IF NOT EXISTS team_invites (
    invite_id   UUID        PRIMARY KEY,
    team_id     UUID        NOT NULL REFERENCES teams(team_id),
    email       TEXT        NOT NULL,
    invited_by  UUID        NOT NULL REFERENCES users(user_id),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at   TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_team_invites_email ON team_invites (email) WHERE closed_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_team_invites_team ON team_invites (team_id) WHERE closed_at IS NULL;

-- Projects made from the dashboard belong to a team; ones made on the admin
-- page belong to none (the operator's).
ALTER TABLE projects ADD COLUMN IF NOT EXISTS team_id UUID REFERENCES teams(team_id);
CREATE INDEX IF NOT EXISTS idx_projects_team ON projects (team_id);
