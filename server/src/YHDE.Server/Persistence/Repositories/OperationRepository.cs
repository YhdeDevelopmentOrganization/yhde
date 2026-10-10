using System.Text.Json;
using Dapper;
using Npgsql;
using YHDE.Server.Domain;

namespace YHDE.Server.Persistence.Repositories;

// Appends operations to and reads them from the immutable log.
//
// CommitAsync holds a FOR UPDATE lock on the branch and, in one transaction,
// reads head_seq, inserts the operation with seq = head_seq + 1 and moves
// head_seq. The transaction commits before the caller broadcasts, and the lock
// keeps one writer per branch (database.md, reliability.md).
public sealed class OperationRepository(Database db) : IOperationRepository
{
    // Commit an operation submission into the log, returning the committed Operation
    // with server-assigned fields (Seq, ActorId, SessionId, CreatedAt, Signature).
    // The Signature is computed here as the SHA-256 hash chain.
    // Throws if the branch does not exist.
    public async Task<Operation> CommitAsync(
        OperationSubmission submission,
        Guid branchId,
        Guid actorId,
        Guid sessionId,
        CancellationToken ct)
    {
        // Validate payload is legal JSON before touching the DB.
        ValidatePayloadJson(submission.Payload);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Acquire a per-branch exclusive lock and read the current head_seq.
        // This serialises all writers for this branch.
        var headSeq = await conn.ExecuteScalarAsync<long>(
            "SELECT head_seq FROM branches WHERE branch_id = @branchId FOR UPDATE",
            new { branchId },
            transaction: tx);

        var seq = headSeq + 1;
        var createdAt = DateTime.UtcNow;

        // Compute hash-chain signature (database.md).
        var prevSignature = await GetPrevSignatureAsync(conn, tx, branchId, headSeq);
        var signature = HashChain.ComputeSignature(
            prevSignature, submission.OpId, actorId, branchId, seq,
            submission.Type, submission.Payload);

        // INSERT the operation (append-only; no UPDATE/DELETE on this table).
        await conn.ExecuteAsync(
            """
            INSERT INTO operations
              (op_id, seq, branch_id, type, target_id, payload,
               actor_id, session_id, client_op_ref, parent_seq,
               prev_signature, signature, created_at)
            VALUES
              (@opId, @seq, @branchId, @type, @targetId, @payload::jsonb,
               @actorId, @sessionId, @clientOpRef, @parentSeq,
               @prevSignature, @signature, @createdAt)
            """,
            new
            {
                opId = submission.OpId,
                seq,
                branchId,
                type = submission.Type,
                targetId = submission.TargetId,
                payload = submission.Payload,
                actorId,
                sessionId,
                clientOpRef = submission.ClientOpRef,
                parentSeq = submission.ParentSeq,
                prevSignature,
                signature,
                createdAt,
            },
            transaction: tx);

        // Advance head_seq atomically in the same transaction.
        await conn.ExecuteAsync(
            "UPDATE branches SET head_seq = @seq WHERE branch_id = @branchId",
            new { seq, branchId },
            transaction: tx);

        await tx.CommitAsync(ct);

        return new Operation(
            submission.OpId, seq, branchId, submission.Type, submission.TargetId,
            submission.Payload, actorId, sessionId, submission.ClientOpRef,
            submission.ParentSeq, createdAt, signature);
    }

    public async Task<IReadOnlyList<Operation>> CommitBatchAsync(
        IReadOnlyList<OperationSubmission> submissions,
        Guid branchId,
        Guid actorId,
        Guid sessionId,
        CancellationToken ct)
    {
        foreach (var s in submissions) ValidatePayloadJson(s.Payload);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var headSeq = await conn.ExecuteScalarAsync<long>(
            "SELECT head_seq FROM branches WHERE branch_id = @branchId FOR UPDATE",
            new { branchId },
            transaction: tx);

        var prevSignature = await GetPrevSignatureAsync(conn, tx, branchId, headSeq);
        var createdAt = DateTime.UtcNow;
        var committed = new List<Operation>(submissions.Count);
        var seq = headSeq;
        foreach (var submission in submissions)
        {
            seq++;
            var signature = HashChain.ComputeSignature(
                prevSignature, submission.OpId, actorId, branchId, seq,
                submission.Type, submission.Payload);

            await conn.ExecuteAsync(
                """
                INSERT INTO operations
                  (op_id, seq, branch_id, type, target_id, payload,
                   actor_id, session_id, client_op_ref, parent_seq,
                   prev_signature, signature, created_at)
                VALUES
                  (@opId, @seq, @branchId, @type, @targetId, @payload::jsonb,
                   @actorId, @sessionId, @clientOpRef, @parentSeq,
                   @prevSignature, @signature, @createdAt)
                """,
                new
                {
                    opId = submission.OpId,
                    seq,
                    branchId,
                    type = submission.Type,
                    targetId = submission.TargetId,
                    payload = submission.Payload,
                    actorId,
                    sessionId,
                    clientOpRef = submission.ClientOpRef,
                    parentSeq = submission.ParentSeq,
                    prevSignature,
                    signature,
                    createdAt,
                },
                transaction: tx);

            committed.Add(new Operation(
                submission.OpId, seq, branchId, submission.Type, submission.TargetId,
                submission.Payload, actorId, sessionId, submission.ClientOpRef,
                submission.ParentSeq, createdAt, signature));
            prevSignature = signature;
        }

        await conn.ExecuteAsync(
            "UPDATE branches SET head_seq = @seq WHERE branch_id = @branchId",
            new { seq, branchId },
            transaction: tx);

        await tx.CommitAsync(ct);
        return committed;
    }

    public async Task<IReadOnlyList<Operation?>> CommitGroupAsync(
        IReadOnlyList<CommitItem> items, Guid branchId, CancellationToken ct)
    {
        var results = new Operation?[items.Count];
        if (items.Count == 0) return results;
        foreach (var item in items) ValidatePayloadJson(item.Submission.Payload);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var headSeq = await conn.ExecuteScalarAsync<long>(
            "SELECT head_seq FROM branches WHERE branch_id = @branchId FOR UPDATE",
            new { branchId },
            transaction: tx);

        // Resends after a reconnect are acknowledged, not committed twice.
        var ids = items.Select(i => i.Submission.OpId).Distinct().ToArray();
        var existing = (await conn.QueryAsync<Guid>(
            "SELECT op_id FROM operations WHERE op_id = ANY(@ids)",
            new { ids },
            transaction: tx)).ToHashSet();

        var prevSignature = await GetPrevSignatureAsync(conn, tx, branchId, headSeq);
        var createdAt = DateTime.UtcNow;
        var seq = headSeq;
        await using var batch = new NpgsqlBatch(conn, tx);
        for (var i = 0; i < items.Count; i++)
        {
            var (submission, actorId, sessionId) = items[i];
            if (!existing.Add(submission.OpId)) continue;
            seq++;
            var signature = HashChain.ComputeSignature(
                prevSignature, submission.OpId, actorId, branchId, seq,
                submission.Type, submission.Payload);
            var command = new NpgsqlBatchCommand(
                "INSERT INTO operations (op_id, seq, branch_id, type, target_id, payload, actor_id, session_id, " +
                "client_op_ref, parent_seq, prev_signature, signature, created_at) " +
                "VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, $8, $9, $10, $11, $12, $13)");
            foreach (var value in new object[]
                     {
                         submission.OpId, seq, branchId, submission.Type, submission.TargetId, submission.Payload,
                         actorId, sessionId, submission.ClientOpRef, submission.ParentSeq, prevSignature, signature, createdAt,
                     })
            {
                command.Parameters.Add(new NpgsqlParameter { Value = value });
            }
            batch.BatchCommands.Add(command);
            results[i] = new Operation(
                submission.OpId, seq, branchId, submission.Type, submission.TargetId,
                submission.Payload, actorId, sessionId, submission.ClientOpRef,
                submission.ParentSeq, createdAt, signature);
            prevSignature = signature;
        }

        if (seq > headSeq)
        {
            var advance = new NpgsqlBatchCommand("UPDATE branches SET head_seq = $1 WHERE branch_id = $2");
            advance.Parameters.Add(new NpgsqlParameter { Value = seq });
            advance.Parameters.Add(new NpgsqlParameter { Value = branchId });
            batch.BatchCommands.Add(advance);
            await batch.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return results;
    }

    public async Task<IReadOnlyList<Operation>> GetByIdsAsync(
        Guid branchId, IReadOnlyCollection<Guid> opIds, CancellationToken ct)
    {
        if (opIds.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<OperationRow>(
            """
            SELECT op_id, seq, branch_id, type, target_id,
                   payload::text AS payload, actor_id, session_id,
                   client_op_ref, parent_seq, prev_signature, signature, created_at
            FROM operations
            WHERE branch_id = @branchId AND op_id = ANY(@ids)
            ORDER BY seq
            """,
            new { branchId, ids = opIds.ToArray() });
        return rows.Select(MapRow).ToList();
    }

    public async Task<Operation?> GetLatestAssetOpAsync(Guid branchId, Guid targetId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<OperationRow>(
            """
            SELECT op_id, seq, branch_id, type, target_id,
                   payload::text AS payload, actor_id, session_id,
                   client_op_ref, parent_seq, prev_signature, signature, created_at
            FROM operations
            WHERE branch_id = @branchId AND target_id = @targetId
              AND type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset')
            ORDER BY seq DESC
            LIMIT 1
            """,
            new { branchId, targetId });
        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<string>> LivePathsDifferingInCaseAsync(Guid branchId, string path, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // Every file op that names such a path (as the file or as a move's
        // source); a path is live unless its latest such op deleted it or
        // moved it away (idx_operations_asset_path_lower, 017).
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            """
            WITH touched AS (
                SELECT seq, type, payload->>'s' AS s, payload->>'f' AS f FROM operations
                WHERE branch_id = @branchId
                  AND type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset')
                  AND (lower(payload->>'s') = lower(@path) OR (type = 'MoveAsset' AND lower(payload->>'f') = lower(@path)))
            ), paths AS (
                SELECT s AS p FROM touched WHERE lower(s) = lower(@path)
                UNION SELECT f FROM touched WHERE f IS NOT NULL AND lower(f) = lower(@path)
            )
            SELECT p FROM paths
            WHERE p <> @path AND (
                SELECT NOT ((t.type = 'DeleteAsset' AND t.s = paths.p) OR (t.type = 'MoveAsset' AND t.f = paths.p))
                FROM touched t WHERE t.s = paths.p OR t.f = paths.p ORDER BY t.seq DESC LIMIT 1)
            """, new { branchId, path }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Operation>> GetTargetOpsAsync(
        Guid branchId, Guid targetId, long afterSeq, string type, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<OperationRow>(
            """
            SELECT op_id, seq, branch_id, type, target_id,
                   payload::text AS payload, actor_id, session_id,
                   client_op_ref, parent_seq, prev_signature, signature, created_at
            FROM operations
            WHERE branch_id = @branchId AND target_id = @targetId AND seq > @afterSeq AND type = @type
            ORDER BY seq
            """,
            new { branchId, targetId, afterSeq, type });
        return rows.Select(MapRow).ToList();
    }

    // Fetch operations in (fromSeqExclusive, toSeqInclusive] for a branch (tail fetch).
    public async Task<IReadOnlyList<Operation>> GetTailAsync(
        Guid branchId, long fromSeqExclusive, long toSeqInclusive, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<OperationRow>(
            """
            SELECT op_id, seq, branch_id, type, target_id,
                   payload::text AS payload, actor_id, session_id,
                   client_op_ref, parent_seq, prev_signature, signature, created_at
            FROM operations
            WHERE branch_id = @branchId
              AND seq > @from
              AND seq <= @to
            ORDER BY seq
            """,
            new { branchId, from = fromSeqExclusive, to = toSeqInclusive });

        return rows.Select(MapRow).ToList();
    }

    // Check if an op_id has already been committed (idempotent replay, reliability.md).
    public async Task<bool> ExistsAsync(Guid opId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM operations WHERE op_id = @opId)",
            new { opId });
    }

    private static async Task<byte[]> GetPrevSignatureAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid branchId, long headSeq)
    {
        if (headSeq == 0) return [];

        var sig = await conn.ExecuteScalarAsync<byte[]?>(
            "SELECT signature FROM operations WHERE branch_id = @branchId AND seq = @seq",
            new { branchId, seq = headSeq },
            transaction: tx);

        return sig ?? [];
    }

    private static void ValidatePayloadJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Operation payload must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Operation payload is not valid JSON.", ex);
        }
    }

    private static Operation MapRow(OperationRow r) => new(
        r.op_id, r.seq, r.branch_id, r.type, r.target_id,
        r.payload, r.actor_id, r.session_id,
        r.client_op_ref, r.parent_seq, r.created_at, r.signature);

    // Dapper mapping row: matches SELECT column names exactly.
    private sealed record OperationRow(
        Guid op_id, long seq, Guid branch_id, string type, Guid target_id,
        string payload, Guid actor_id, Guid session_id,
        Guid client_op_ref, long parent_seq, byte[] prev_signature,
        byte[] signature, DateTime created_at);
}
