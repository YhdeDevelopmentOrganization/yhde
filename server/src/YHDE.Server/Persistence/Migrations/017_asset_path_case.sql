-- 017: finding a file by its path without regard to capitals:
-- a new file whose name differs from an existing one only in capitals is
-- refused, because Windows and macOS store both as the same file.

CREATE INDEX IF NOT EXISTS idx_operations_asset_path_lower
    ON operations (branch_id, lower(payload->>'s'))
    WHERE type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset');

CREATE INDEX IF NOT EXISTS idx_operations_move_source_lower
    ON operations (branch_id, lower(payload->>'f'))
    WHERE type = 'MoveAsset';
