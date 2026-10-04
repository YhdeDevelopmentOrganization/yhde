-- 010: team admins, access per project, test accounts (teams.md, admin.md)

-- A team admin manages projects and people; only the owner handles the plan.
ALTER TABLE team_members DROP CONSTRAINT IF EXISTS team_members_role_check;
ALTER TABLE team_members ADD CONSTRAINT team_members_role_check CHECK (role IN ('owner', 'admin', 'member'));

-- What a member may do in one project. No row: they can edit (the default
-- for everyone on the team). The owner and admins can always edit.
CREATE TABLE IF NOT EXISTS project_access (
    project_id  UUID NOT NULL REFERENCES projects(project_id),
    user_id     UUID NOT NULL REFERENCES users(user_id),
    access      TEXT NOT NULL CHECK (access IN ('edit', 'view', 'none')),
    PRIMARY KEY (project_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_project_access_user ON project_access (user_id);

-- Accounts made on the admin page for testing: marked, and easy to remove.
ALTER TABLE users ADD COLUMN IF NOT EXISTS is_test BOOLEAN NOT NULL DEFAULT FALSE;
