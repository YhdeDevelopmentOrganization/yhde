-- 018: small summaries of the operation log for the statistics (admin.md,
-- ADR 0017). Counting changes and adding up file sizes used to read the
-- whole log on every dashboard and admin refresh. Two tables now hold the
-- answers, kept up to date by triggers as operations are added, so the
-- statistics read a few hundred rows instead of the whole log. The log
-- itself is unchanged.

-- The latest asset operation of each file on each branch: what the project
-- holds now (a DeleteAsset row means the file is gone).
CREATE TABLE IF NOT EXISTS asset_heads (
    branch_id   UUID    NOT NULL REFERENCES branches(branch_id) ON DELETE CASCADE,
    target_id   UUID    NOT NULL,
    project_id  UUID    NOT NULL,
    seq         BIGINT  NOT NULL,
    type        TEXT    NOT NULL,
    path        TEXT,
    hash        TEXT,
    size        BIGINT  NOT NULL DEFAULT 0,
    PRIMARY KEY (branch_id, target_id)
);

CREATE INDEX IF NOT EXISTS idx_asset_heads_project ON asset_heads (project_id);

-- Changes per hour, project, connection and operation type. The connection
-- (session_log.session_id) says who; a session with no session_log row is
-- the server itself, such as a project imported from a zip.
CREATE TABLE IF NOT EXISTS op_hourly (
    hour        TIMESTAMPTZ NOT NULL,
    project_id  UUID        NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    session_id  UUID        NOT NULL,
    type        TEXT        NOT NULL,
    n           BIGINT      NOT NULL,
    last_at     TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (hour, project_id, session_id, type)
);

CREATE INDEX IF NOT EXISTS idx_op_hourly_project ON op_hourly (project_id, hour);
CREATE INDEX IF NOT EXISTS idx_op_hourly_session ON op_hourly (session_id);

-- A size from a payload, or 0. Never raises: a bad payload must not stop an
-- operation from being stored.
CREATE OR REPLACE FUNCTION yhde_payload_size(p JSONB) RETURNS BIGINT
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN jsonb_typeof(p->'n') = 'number' AND (p->>'n') ~ '^[0-9]{1,18}$'
                THEN (p->>'n')::BIGINT ELSE 0 END
$$;

CREATE OR REPLACE FUNCTION yhde_track_operation() RETURNS TRIGGER
LANGUAGE plpgsql AS $$
DECLARE
    pid UUID;
BEGIN
    SELECT project_id INTO pid FROM branches WHERE branch_id = NEW.branch_id;
    IF pid IS NULL THEN
        RETURN NULL;
    END IF;

    INSERT INTO op_hourly (hour, project_id, session_id, type, n, last_at)
    VALUES (date_trunc('hour', NEW.created_at), pid, NEW.session_id, NEW.type, 1, NEW.created_at)
    ON CONFLICT (hour, project_id, session_id, type)
    DO UPDATE SET n = op_hourly.n + 1, last_at = GREATEST(op_hourly.last_at, EXCLUDED.last_at);

    IF NEW.type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset') THEN
        INSERT INTO asset_heads (branch_id, target_id, project_id, seq, type, path, hash, size)
        VALUES (NEW.branch_id, NEW.target_id, pid, NEW.seq, NEW.type,
                NEW.payload->>'s', NEW.payload->>'h', yhde_payload_size(NEW.payload))
        ON CONFLICT (branch_id, target_id) DO UPDATE
            SET seq = EXCLUDED.seq, type = EXCLUDED.type, path = EXCLUDED.path,
                hash = EXCLUDED.hash, size = EXCLUDED.size
            WHERE asset_heads.seq < EXCLUDED.seq;
    END IF;
    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS trg_operations_track ON operations;
CREATE TRIGGER trg_operations_track
    AFTER INSERT ON operations
    FOR EACH ROW EXECUTE FUNCTION yhde_track_operation();

-- Fill both from the log that is already there (once, when this runs).
INSERT INTO op_hourly (hour, project_id, session_id, type, n, last_at)
SELECT date_trunc('hour', o.created_at), b.project_id, o.session_id, o.type, COUNT(*), MAX(o.created_at)
FROM operations o JOIN branches b ON b.branch_id = o.branch_id
GROUP BY 1, 2, 3, 4
ON CONFLICT (hour, project_id, session_id, type) DO NOTHING;

INSERT INTO asset_heads (branch_id, target_id, project_id, seq, type, path, hash, size)
SELECT DISTINCT ON (o.branch_id, o.target_id)
       o.branch_id, o.target_id, b.project_id, o.seq, o.type,
       o.payload->>'s', o.payload->>'h', yhde_payload_size(o.payload)
FROM operations o JOIN branches b ON b.branch_id = o.branch_id
WHERE o.type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset')
ORDER BY o.branch_id, o.target_id, o.seq DESC
ON CONFLICT (branch_id, target_id) DO NOTHING;
