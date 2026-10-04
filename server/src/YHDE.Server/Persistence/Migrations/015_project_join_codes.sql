-- 015: join codes (teams.md). A project's owner can turn on a short code;
-- anyone signed in who enters it joins the project as a developer while
-- there is room. A new code, or turning it off, stops the old one.

ALTER TABLE projects ADD COLUMN IF NOT EXISTS join_code TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS idx_projects_join_code ON projects (join_code) WHERE join_code IS NOT NULL;
