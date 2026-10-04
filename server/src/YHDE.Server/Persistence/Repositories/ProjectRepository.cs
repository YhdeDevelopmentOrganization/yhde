using Dapper;
using YHDE.Server.Domain;

namespace YHDE.Server.Persistence.Repositories;

public sealed class ProjectRepository(Database db)
{
    public async Task<ProjectEntity?> GetAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ProjectEntity>(
            """
            SELECT project_id AS ProjectId, name AS Name, created_at AS CreatedAt
            FROM projects
            WHERE project_id = @projectId
            """,
            new { projectId });
    }

    public async Task<ProjectEntity> CreateAsync(string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleAsync<ProjectEntity>(
            """
            INSERT INTO projects (name)
            VALUES (@name)
            RETURNING project_id AS ProjectId, name AS Name, created_at AS CreatedAt
            """,
            new { name });
    }
}
