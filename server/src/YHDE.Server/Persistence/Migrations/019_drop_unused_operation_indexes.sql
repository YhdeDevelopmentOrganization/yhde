-- 019: indexes on operations that nothing uses. Every saved change paid for
-- them (the log is the busiest table).
--   idx_operations_branch_seq      same columns as the UNIQUE (branch_id, seq)
--                                  constraint's own index
--   idx_operations_target          no query looks up by target_id alone
--                                  (idx_operations_branch_target_seq serves them)
--   idx_operations_client_op_ref   no query looks up by client_op_ref
DROP INDEX IF EXISTS idx_operations_branch_seq;
DROP INDEX IF EXISTS idx_operations_target;
DROP INDEX IF EXISTS idx_operations_client_op_ref;
