-- 016: which project holds which stored file (assets.md). The blob store is
-- shared and content-addressed, so without this anyone who could reach the
-- file routes could read any file whose hash they knew, or ask whether it
-- exists. A file belongs to a project once someone who may edit it uploaded
-- the bytes there (proving they have them), or once the project's log
-- refers to it. Uploads that no operation uses yet (referenced = false)
-- count toward the owner's storage and are removed after two days.

CREATE TABLE IF NOT EXISTS project_blobs (
    project_id  UUID        NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    hash        TEXT        NOT NULL,
    size        BIGINT      NOT NULL,
    uploader    TEXT        NOT NULL DEFAULT '',
    referenced  BOOLEAN     NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (project_id, hash)
);

CREATE INDEX IF NOT EXISTS idx_project_blobs_hash ON project_blobs (hash);
CREATE INDEX IF NOT EXISTS idx_project_blobs_pending ON project_blobs (created_at) WHERE NOT referenced;

-- Every file the existing logs refer to.
INSERT INTO project_blobs (project_id, hash, size)
SELECT DISTINCT ON (b.project_id, o.payload->>'h') b.project_id, o.payload->>'h', COALESCE((o.payload->>'n')::BIGINT, 0)
FROM operations o JOIN branches b ON b.branch_id = o.branch_id
WHERE o.type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset') AND o.payload ? 'h'
  AND length(o.payload->>'h') = 64
ON CONFLICT DO NOTHING;
