using System.Collections.Concurrent;
using Dapper;
using YHDE.Server.Persistence;

namespace YHDE.Server.Assets;

// A stored file as one project sees it: its size, and whether an operation in
// the project's log uses it yet (an upload nothing uses is "pending").
public sealed record ProjectBlob(long Size, bool Referenced);

// Which project holds which stored file (016_project_blobs.sql, assets.md).
public interface IProjectBlobs
{
    Task<ProjectBlob?> GetAsync(Guid projectId, string hash, CancellationToken ct);
    // Whether any of these projects holds the file (an add-on that does not
    // name its project yet).
    Task<bool> AnyAsync(IReadOnlyCollection<Guid> projectIds, string hash, CancellationToken ct);
    // Records an upload or a use. A use (referenced) is never undone by a
    // later upload of the same bytes.
    Task AddAsync(Guid projectId, string hash, long size, string uploader, bool referenced, CancellationToken ct);
    Task MarkReferencedAsync(Guid projectId, string hash, CancellationToken ct);
    // Bytes uploaded to these projects that no operation uses yet.
    Task<long> PendingBytesAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct);
    // Forgets pending uploads older than `age` and returns the hashes that no
    // project holds any more (their bytes may go).
    Task<IReadOnlyList<string>> ExpirePendingAsync(TimeSpan age, CancellationToken ct);
}

public sealed class ProjectBlobs(Database db) : IProjectBlobs
{
    // File transfers ask once per chunk: a file a project holds stays held
    // (until the project is deleted), so a "yes" is remembered for a while.
    private static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<(Guid, string), (ProjectBlob Blob, DateTimeOffset Until)> _known = new();

    public async Task<ProjectBlob?> GetAsync(Guid projectId, string hash, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_known.TryGetValue((projectId, hash), out var k) && k.Until > now && k.Blob.Referenced) return k.Blob;
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(long size, bool referenced)?>(new CommandDefinition(
            "SELECT size, referenced FROM project_blobs WHERE project_id = @projectId AND hash = @hash",
            new { projectId, hash }, cancellationToken: ct));
        if (row is not { } r) return null;
        var blob = new ProjectBlob(r.size, r.referenced);
        if (_known.Count > 50_000) _known.Clear();
        _known[(projectId, hash)] = (blob, now + RememberFor);
        return blob;
    }

    public async Task<bool> AnyAsync(IReadOnlyCollection<Guid> projectIds, string hash, CancellationToken ct)
    {
        if (projectIds.Count == 0) return false;
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM project_blobs WHERE hash = @hash AND project_id = ANY(@ids))",
            new { hash, ids = projectIds.ToArray() }, cancellationToken: ct));
    }

    public async Task AddAsync(Guid projectId, string hash, long size, string uploader, bool referenced, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_blobs (project_id, hash, size, uploader, referenced)
            VALUES (@projectId, @hash, @size, @uploader, @referenced)
            ON CONFLICT (project_id, hash) DO UPDATE
              SET referenced = project_blobs.referenced OR EXCLUDED.referenced,
                  created_at = CASE WHEN project_blobs.referenced THEN project_blobs.created_at ELSE now() END
            """, new { projectId, hash, size, uploader, referenced }, cancellationToken: ct));
        _known.TryRemove((projectId, hash), out _);
    }

    public async Task MarkReferencedAsync(Guid projectId, string hash, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE project_blobs SET referenced = TRUE WHERE project_id = @projectId AND hash = @hash AND NOT referenced",
            new { projectId, hash }, cancellationToken: ct));
        _known.TryRemove((projectId, hash), out _);
    }

    public async Task<long> PendingBytesAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return 0;
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(SUM(size), 0) FROM project_blobs WHERE NOT referenced AND project_id = ANY(@ids)",
            new { ids = projectIds.ToArray() }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> ExpirePendingAsync(TimeSpan age, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var gone = (await conn.QueryAsync<string>(new CommandDefinition(
            """
            WITH expired AS (
                DELETE FROM project_blobs WHERE NOT referenced AND created_at < now() - @age
                RETURNING hash
            )
            SELECT DISTINCT e.hash FROM expired e
            -- (this statement still sees the rows it deletes: skip those)
            WHERE NOT EXISTS (SELECT 1 FROM project_blobs p WHERE p.hash = e.hash
                              AND (p.referenced OR p.created_at >= now() - @age))
            """, new { age }, cancellationToken: ct))).ToList();
        _known.Clear();
        return gone;
    }
}
