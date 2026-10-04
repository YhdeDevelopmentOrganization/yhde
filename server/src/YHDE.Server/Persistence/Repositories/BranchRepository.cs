using Dapper;
using YHDE.Server.Domain;

namespace YHDE.Server.Persistence.Repositories;

public sealed class BranchRepository(Database db) : IBranchRepository
{
    public async Task<Branch?> GetAsync(Guid branchId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Branch>(
            """
            SELECT branch_id AS BranchId, project_id AS ProjectId, name AS Name,
                   head_seq AS HeadSeq, base_branch_id AS BaseBranchId,
                   base_seq AS BaseSeq, created_at AS CreatedAt
            FROM branches
            WHERE branch_id = @branchId
            """,
            new { branchId });
    }

    public async Task<Branch?> GetByNameAsync(Guid projectId, string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Branch>(
            """
            SELECT branch_id AS BranchId, project_id AS ProjectId, name AS Name,
                   head_seq AS HeadSeq, base_branch_id AS BaseBranchId,
                   base_seq AS BaseSeq, created_at AS CreatedAt
            FROM branches
            WHERE project_id = @projectId AND name = @name
            """,
            new { projectId, name });
    }

    public async Task<Branch> CreateAsync(Guid projectId, string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleAsync<Branch>(
            """
            INSERT INTO branches (project_id, name)
            VALUES (@projectId, @name)
            RETURNING branch_id AS BranchId, project_id AS ProjectId, name AS Name,
                      head_seq AS HeadSeq, base_branch_id AS BaseBranchId,
                      base_seq AS BaseSeq, created_at AS CreatedAt
            """,
            new { projectId, name });
    }
}
