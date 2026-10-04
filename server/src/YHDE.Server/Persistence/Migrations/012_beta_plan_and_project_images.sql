-- 012: the beta tester plan, and an image per project (teams.md, projects.md)

-- Until 1.0.0 every team is on the free beta tester plan: the owner and three
-- invited people. Teams made before this move to it; a team that already
-- has more people keeps them as extra seats, so nobody is removed.
ALTER TABLE teams DROP CONSTRAINT IF EXISTS teams_plan_check;
ALTER TABLE teams ADD CONSTRAINT teams_plan_check CHECK (plan IN ('beta', 'solo', 'trio', 'team', 'studio'));

UPDATE teams t SET plan = 'beta', period = 'month', extra_seats = GREATEST(0,
        (SELECT COUNT(*) FROM team_members m WHERE m.team_id = t.team_id)
      + (SELECT COUNT(*) FROM team_invites i WHERE i.team_id = t.team_id AND i.closed_at IS NULL) - 4)
 WHERE plan <> 'beta';

-- The picture shown for a project on the dashboard. Small (the server caps
-- it at 2 MB) and only PNG, JPEG or WebP, checked by its first bytes.
CREATE TABLE IF NOT EXISTS project_images (
    project_id    UUID        PRIMARY KEY REFERENCES projects(project_id),
    content_type  TEXT        NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    data          BYTEA       NOT NULL,
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
