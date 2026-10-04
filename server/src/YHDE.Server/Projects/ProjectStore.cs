using System.Security.Cryptography;
using System.Text;
using Dapper;
using YHDE.Server.Persistence;

namespace YHDE.Server.Projects;

public sealed record ProjectInfo(
    Guid ProjectId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    Guid MainBranchId);

public sealed record InviteInfo(
    Guid InviteId,
    Guid ProjectId,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    Guid? LinkId = null);

// A download link (onboarding.md): /join/<token> downloads a ready project.
public sealed record LinkInfo(
    Guid LinkId,
    Guid ProjectId,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    int? MaxUses,
    int Uses,
    DateTimeOffset? RevokedAt);

// What a used link grants: a starter download for this project.
public sealed record LinkUse(Guid LinkId, Guid ProjectId, string Label, int Uses);

// What deleting a project removed (for the audit log and the admin page).
public sealed record ProjectDeletion(long Operations, long ChatMessages, long CommentThreads, long Invites, long Links);

public sealed record ProjectStats(
    Guid ProjectId,
    long Operations,
    long Files,
    long FileBytes,
    DateTimeOffset? LastActivity);

// Game projects and their invite codes (projects.md).
public interface IProjectStore
{
    Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct);
    Task<ProjectInfo?> GetAsync(Guid projectId, CancellationToken ct);
    Task<ProjectInfo> CreateAsync(string name, CancellationToken ct);
    Task RenameAsync(Guid projectId, string name, CancellationToken ct);
    Task SetArchivedAsync(Guid projectId, bool archived, CancellationToken ct);

    // Returns the new invite and the code itself (shown once, never stored).
    Task<(InviteInfo Invite, string Code)> CreateInviteAsync(Guid projectId, string label, CancellationToken ct, Guid? linkId = null);
    Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(Guid projectId, CancellationToken ct);
    Task RevokeInviteAsync(Guid inviteId, CancellationToken ct);
    // The project a live code opens (not revoked, project not archived).
    Task<Guid?> ProjectForCodeAsync(string code, CancellationToken ct);

    // A code made by a project's view link (teams.md): it opens the project
    // to watch, never to change. Codes from the admin page still edit.
    Task<bool> CodeIsViewOnlyAsync(string code, CancellationToken ct) => Task.FromResult(false);

    Task<IReadOnlyList<ProjectStats>> StatsAsync(CancellationToken ct);

    // Download links. Returns the link and its token (shown once, never stored).
    Task<(LinkInfo Link, string Token)> CreateLinkAsync(Guid projectId, string label, DateTimeOffset? expiresAt, int? maxUses, CancellationToken ct);
    Task<IReadOnlyList<LinkInfo>> ListLinksAsync(Guid projectId, CancellationToken ct);
    Task RevokeLinkAsync(Guid linkId, CancellationToken ct);
    // Removes a link from the list for good (it stops working, if it still
    // did). Invite codes it already made keep working.
    Task<bool> DeleteLinkAsync(Guid linkId, CancellationToken ct);
    // Removes an invite code for good; it stops working at once.
    Task<bool> DeleteInviteAsync(Guid inviteId, CancellationToken ct);
    // Counts one use of a live link (not revoked, expired or used up; project
    // not archived). Null: the link does not work.
    Task<LinkUse?> UseLinkAsync(string token, CancellationToken ct);
    // The same check without using the link up (the page before the download).
    Task<LinkUse?> PeekLinkAsync(string token, CancellationToken ct);

    // Removes an archived project and everything in it. Null: not archived
    // (or no such project), nothing removed.
    Task<ProjectDeletion?> DeleteArchivedAsync(Guid projectId, CancellationToken ct);
}

// Download link tokens: XXXX-XXXX-XXXX, 60 random bits in Crockford base32.
// Different from invite codes on purpose: a link only downloads a project.
public static class LinkToken
{
    public static string New()
    {
        var code = InviteCode.New(); // YHDE-XXXX-XXXX-XXXX-XXXX
        return code[5..19];          // XXXX-XXXX-XXXX (60 of its random bits)
    }

    public static string Normalize(string token) => InviteCode.Normalize(token);
    public static bool LooksLikeToken(string value) => Normalize(value) is { Length: 12 } n && !n.StartsWith("YHDE", StringComparison.Ordinal);
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes("link:" + Normalize(token)));
}

// Invite codes: YHDE-XXXX-XXXX-XXXX-XXXX, 80 random bits in Crockford base32
// (no I, L, O, U: easy to read aloud and type).
public static class InviteCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder("YHDE");
        ulong acc = 0;
        var bits = 0;
        var chars = 0;
        foreach (var b in bytes)
        {
            acc = (acc << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                if (chars % 4 == 0) sb.Append('-');
                sb.Append(Alphabet[(int)((acc >> bits) & 31)]);
                chars++;
            }
        }
        return sb.ToString();
    }

    // Case, dashes, spaces and look-alike letters do not matter when typing a code.
    public static string Normalize(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var raw in code.Trim().ToUpperInvariant())
        {
            var c = raw switch { 'O' => '0', 'I' => '1', 'L' => '1', _ => raw };
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool LooksLikeCode(string value)
    {
        var n = Normalize(value);
        return n.Length == 20 && n.StartsWith("YHDE", StringComparison.Ordinal);
    }

    public static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)));
}

public sealed class ProjectStore(Database db) : IProjectStore
{
    private const string ProjectSelect =
        """
        SELECT p.project_id, p.name, p.created_at, p.archived_at,
               (SELECT b.branch_id FROM branches b WHERE b.project_id = p.project_id AND b.name = 'main') AS main_branch_id
        FROM projects p
        """;

    public async Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ProjectRow>(new CommandDefinition(ProjectSelect + " ORDER BY p.created_at", cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<ProjectInfo?> GetAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            ProjectSelect + " WHERE p.project_id = @projectId", new { projectId }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<ProjectInfo> CreateAsync(string name, CancellationToken ct)
    {
        var projectId = Guid.NewGuid();
        await using (var conn = await db.OpenAsync(ct))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO projects (project_id, name) VALUES (@projectId, @name)", new { projectId, name }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO branches (project_id, name) VALUES (@projectId, 'main')", new { projectId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        return (await GetAsync(projectId, ct))!;
    }

    public async Task RenameAsync(Guid projectId, string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET name = @name WHERE project_id = @projectId", new { projectId, name }, cancellationToken: ct));
    }

    public async Task SetArchivedAsync(Guid projectId, bool archived, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET archived_at = CASE WHEN @archived THEN NOW() ELSE NULL END WHERE project_id = @projectId",
            new { projectId, archived }, cancellationToken: ct));
    }

    public async Task<(InviteInfo Invite, string Code)> CreateInviteAsync(Guid projectId, string label, CancellationToken ct, Guid? linkId = null)
    {
        var code = InviteCode.New();
        var invite = new InviteInfo(Guid.NewGuid(), projectId, label, DateTimeOffset.UtcNow, null, linkId);
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_invites (invite_id, project_id, code_hash, label, created_at, link_id)
            VALUES (@InviteId, @ProjectId, @hash, @Label, @created, @LinkId)
            """,
            new { invite.InviteId, invite.ProjectId, hash = InviteCode.Hash(code), invite.Label, created = invite.CreatedAt.UtcDateTime, invite.LinkId },
            cancellationToken: ct));
        return (invite, code);
    }

    public async Task<(LinkInfo Link, string Token)> CreateLinkAsync(Guid projectId, string label, DateTimeOffset? expiresAt, int? maxUses, CancellationToken ct)
    {
        var token = LinkToken.New();
        var link = new LinkInfo(Guid.NewGuid(), projectId, label, DateTimeOffset.UtcNow, expiresAt, maxUses, 0, null);
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO download_links (link_id, project_id, token_hash, label, created_at, expires_at, max_uses)
            VALUES (@LinkId, @ProjectId, @hash, @Label, @created, @expires, @MaxUses)
            """,
            new
            {
                link.LinkId, link.ProjectId, hash = LinkToken.Hash(token), link.Label, created = link.CreatedAt.UtcDateTime,
                expires = expiresAt?.UtcDateTime, link.MaxUses,
            },
            cancellationToken: ct));
        return (link, token);
    }

    public async Task<IReadOnlyList<LinkInfo>> ListLinksAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<LinkRow>(new CommandDefinition(
            """
            SELECT link_id, project_id, label, created_at, expires_at, max_uses, uses, revoked_at
            FROM download_links WHERE project_id = @projectId ORDER BY created_at
            """,
            new { projectId }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<bool> DeleteLinkAsync(Guid linkId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM download_links WHERE link_id = @linkId", new { linkId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> DeleteInviteAsync(Guid inviteId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_invites WHERE invite_id = @inviteId", new { inviteId }, cancellationToken: ct)) > 0;
    }

    public async Task RevokeLinkAsync(Guid linkId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE download_links SET revoked_at = NOW() WHERE link_id = @linkId AND revoked_at IS NULL",
            new { linkId }, cancellationToken: ct));
    }

    public async Task<LinkUse?> UseLinkAsync(string token, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<LinkUseRow>(new CommandDefinition(
            """
            UPDATE download_links l SET uses = l.uses + 1
            FROM projects p
            WHERE l.token_hash = @hash AND p.project_id = l.project_id AND p.archived_at IS NULL
              AND l.revoked_at IS NULL
              AND (l.expires_at IS NULL OR l.expires_at > NOW())
              AND (l.max_uses IS NULL OR l.uses < l.max_uses)
            RETURNING l.link_id, l.project_id, l.label, l.uses
            """,
            new { hash = LinkToken.Hash(token) }, cancellationToken: ct));
        return row is null ? null : new LinkUse(row.link_id, row.project_id, row.label, row.uses);
    }

    public async Task<LinkUse?> PeekLinkAsync(string token, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<LinkUseRow>(new CommandDefinition(
            """
            SELECT l.link_id, l.project_id, l.label, l.uses
            FROM download_links l JOIN projects p ON p.project_id = l.project_id
            WHERE l.token_hash = @hash AND p.archived_at IS NULL
              AND l.revoked_at IS NULL
              AND (l.expires_at IS NULL OR l.expires_at > NOW())
              AND (l.max_uses IS NULL OR l.uses < l.max_uses)
            """,
            new { hash = LinkToken.Hash(token) }, cancellationToken: ct));
        return row is null ? null : new LinkUse(row.link_id, row.project_id, row.label, row.uses);
    }

    public async Task<ProjectDeletion?> DeleteArchivedAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var archived = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM projects WHERE project_id = @projectId AND archived_at IS NOT NULL)",
            new { projectId }, tx, cancellationToken: ct));
        if (!archived) return null;
        async Task<long> Run(string sql) =>
            await conn.ExecuteAsync(new CommandDefinition(sql, new { projectId }, tx, commandTimeout: 600, cancellationToken: ct));

        // Children first (foreign keys). The one place rows leave the
        // operation log: the whole project goes, on the operator's typed
        // confirmation, and the audit log keeps a record (admin.md).
        await Run("DELETE FROM comment_messages WHERE thread_id IN (SELECT thread_id FROM comment_threads WHERE project_id = @projectId)");
        var threads = await Run("DELETE FROM comment_threads WHERE project_id = @projectId");
        var chat = await Run("DELETE FROM chat_messages WHERE project_id = @projectId");
        var invites = await Run("DELETE FROM project_invites WHERE project_id = @projectId");
        var links = await Run("DELETE FROM download_links WHERE project_id = @projectId");
        await Run("DELETE FROM project_access WHERE project_id = @projectId");
        await Run("DELETE FROM project_members WHERE project_id = @projectId");
        await Run("DELETE FROM team_invites WHERE project_id = @projectId");
        await Run("DELETE FROM project_images WHERE project_id = @projectId");
        await Run("DELETE FROM scenes WHERE project_id = @projectId");
        var ops = await Run("DELETE FROM operations WHERE branch_id IN (SELECT branch_id FROM branches WHERE project_id = @projectId)");
        await Run("UPDATE branches SET base_branch_id = NULL WHERE project_id = @projectId");
        await Run("DELETE FROM branches WHERE project_id = @projectId");
        await Run("UPDATE session_log SET project_id = NULL WHERE project_id = @projectId");
        await Run("DELETE FROM projects WHERE project_id = @projectId");
        await tx.CommitAsync(ct);
        return new ProjectDeletion(ops, chat, threads, invites, links);
    }

    public async Task<IReadOnlyList<InviteInfo>> ListInvitesAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<InviteRow>(new CommandDefinition(
            """
            SELECT invite_id, project_id, label, created_at, revoked_at, link_id
            FROM project_invites WHERE project_id = @projectId ORDER BY created_at
            """,
            new { projectId }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task RevokeInviteAsync(Guid inviteId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE project_invites SET revoked_at = NOW() WHERE invite_id = @inviteId AND revoked_at IS NULL",
            new { inviteId }, cancellationToken: ct));
    }

    public async Task<Guid?> ProjectForCodeAsync(string code, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            SELECT i.project_id FROM project_invites i JOIN projects p ON p.project_id = i.project_id
            WHERE i.code_hash = @hash AND i.revoked_at IS NULL AND p.archived_at IS NULL
            """,
            new { hash = InviteCode.Hash(code) }, cancellationToken: ct));
    }

    public async Task<bool> CodeIsViewOnlyAsync(string code, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM project_invites i JOIN projects p ON p.project_id = i.project_id
                           WHERE i.code_hash = @hash AND i.link_id IS NOT NULL AND p.team_id IS NOT NULL)
            """,
            new { hash = InviteCode.Hash(code) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ProjectStats>> StatsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // Files: per path, the latest asset operation, unless it is a delete.
        var rows = await conn.QueryAsync<StatsRow>(new CommandDefinition(
            """
            WITH ops AS (
                SELECT b.project_id, o.seq, o.branch_id, o.type, o.target_id, o.payload, o.created_at
                FROM operations o JOIN branches b ON b.branch_id = o.branch_id
            ),
            latest AS (
                SELECT DISTINCT ON (branch_id, target_id) project_id, type, payload
                FROM ops WHERE type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset', 'DeleteAsset')
                ORDER BY branch_id, target_id, seq DESC
            )
            SELECT p.project_id,
                   (SELECT COUNT(*) FROM ops WHERE ops.project_id = p.project_id) AS operations,
                   (SELECT COUNT(*) FROM latest l WHERE l.project_id = p.project_id AND l.type <> 'DeleteAsset') AS files,
                   (SELECT COALESCE(SUM((l.payload->>'n')::BIGINT), 0) FROM latest l
                      WHERE l.project_id = p.project_id AND l.type <> 'DeleteAsset') AS file_bytes,
                   (SELECT MAX(created_at) FROM ops WHERE ops.project_id = p.project_id) AS last_activity
            FROM projects p
            """,
            cancellationToken: ct));
        return rows.Select(r => new ProjectStats(r.project_id, r.operations, r.files, r.file_bytes,
            r.last_activity is { } t ? Utc(t) : null)).ToList();
    }

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

#pragma warning disable IDE1006 // column names
    private sealed class ProjectRow
    {
        public Guid project_id { get; init; }
        public string name { get; init; } = "";
        public DateTime created_at { get; init; }
        public DateTime? archived_at { get; init; }
        public Guid? main_branch_id { get; init; }

        public ProjectInfo ToModel() =>
            new(project_id, name, Utc(created_at), archived_at is { } a ? Utc(a) : null, main_branch_id ?? Guid.Empty);
    }

    private sealed class InviteRow
    {
        public Guid invite_id { get; init; }
        public Guid project_id { get; init; }
        public string label { get; init; } = "";
        public DateTime created_at { get; init; }
        public DateTime? revoked_at { get; init; }
        public Guid? link_id { get; init; }

        public InviteInfo ToModel() => new(invite_id, project_id, label, Utc(created_at), revoked_at is { } r ? Utc(r) : null, link_id);
    }

    private sealed class LinkRow
    {
        public Guid link_id { get; init; }
        public Guid project_id { get; init; }
        public string label { get; init; } = "";
        public DateTime created_at { get; init; }
        public DateTime? expires_at { get; init; }
        public int? max_uses { get; init; }
        public int uses { get; init; }
        public DateTime? revoked_at { get; init; }

        public LinkInfo ToModel() => new(link_id, project_id, label, Utc(created_at), expires_at is { } e ? Utc(e) : null,
            max_uses, uses, revoked_at is { } r ? Utc(r) : null);
    }

    private sealed class LinkUseRow
    {
        public Guid link_id { get; init; }
        public Guid project_id { get; init; }
        public string label { get; init; } = "";
        public int uses { get; init; }
    }

    private sealed class StatsRow
    {
        public Guid project_id { get; init; }
        public long operations { get; init; }
        public long files { get; init; }
        public long file_bytes { get; init; }
        public DateTime? last_activity { get; init; }
    }
#pragma warning restore IDE1006
}
