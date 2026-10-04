-- 014: people belong to projects, not teams (teams.md)
--
-- A beta tester owns up to three projects (the teams row stays as theirs:
-- plan, storage, codes used, free seats). Everyone else is in a project
-- because its owner invited them, and can be in any number of projects.

CREATE TABLE IF NOT EXISTS project_members (
    project_id  UUID        NOT NULL REFERENCES projects(project_id),
    user_id     UUID        NOT NULL REFERENCES users(user_id),
    access      TEXT        NOT NULL DEFAULT 'edit' CHECK (access IN ('edit', 'view')),
    joined_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (project_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_project_members_user ON project_members (user_id);

-- Invitations are to one project.
ALTER TABLE team_invites ADD COLUMN IF NOT EXISTS project_id UUID REFERENCES projects(project_id);
CREATE INDEX IF NOT EXISTS idx_team_invites_project ON team_invites (project_id) WHERE closed_at IS NULL;

-- Everyone on a team becomes a member of each of its projects they could
-- open, with the same access (admins could edit everything). Nobody is
-- removed, even where that is more people than a project allows now.
INSERT INTO project_members (project_id, user_id, access, joined_at)
SELECT p.project_id, m.user_id,
       CASE WHEN m.role = 'admin' THEN 'edit' ELSE COALESCE(a.access, 'edit') END,
       m.joined_at
  FROM team_members m
  JOIN projects p ON p.team_id = m.team_id
  LEFT JOIN project_access a ON a.project_id = p.project_id AND a.user_id = m.user_id
 WHERE m.role <> 'owner'
   AND (m.role = 'admin' OR COALESCE(a.access, 'edit') <> 'none')
ON CONFLICT DO NOTHING;

-- An open invitation to a team becomes one to each of its active projects
-- (accepted on the dashboard; it keeps its date, so it still runs out).
INSERT INTO team_invites (invite_id, team_id, email, invited_by, created_at, project_id)
SELECT gen_random_uuid(), i.team_id, i.email, i.invited_by, i.created_at, p.project_id
  FROM team_invites i
  JOIN projects p ON p.team_id = i.team_id AND p.archived_at IS NULL
 WHERE i.project_id IS NULL AND i.closed_at IS NULL;
UPDATE team_invites SET closed_at = NOW() WHERE project_id IS NULL AND closed_at IS NULL;

-- The team rows now only say who owns what.
DELETE FROM team_members WHERE role <> 'owner';
