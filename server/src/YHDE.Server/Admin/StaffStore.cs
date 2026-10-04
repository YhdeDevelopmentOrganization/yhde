using System.Text.Json;
using Dapper;
using YHDE.Server.Persistence;

namespace YHDE.Server.Admin;

public sealed record UserRow(
    Guid Id, string Email, string Name, bool EmailVerified, bool HasPassword, DateTimeOffset Created, bool Disabled,
    string? StaffRole, string[] Providers, Guid? TeamId, string? Team, string? TeamRole, DateTimeOffset? LastSeen, int Sessions, bool IsTest,
    int Owned = 0, int InProjects = 0);

public sealed record TeamRow(
    Guid Id, string Name, Guid OwnerId, string Owner, string OwnerEmail, string Plan, string Period, int ExtraSeats, int ExtraStorage,
    int BonusSeats, int BonusStorageGb, int Members, int Invites, int Projects, DateTimeOffset Created, int FreeSeats);

public sealed record AuditRow(long Id, DateTimeOffset At, string Type, Guid? ActorId, string Actor, Guid? ProjectId, string Project, Guid? TargetId, string Detail);

// What the admin page reads and changes about accounts and teams, and the
// audit log of who did what (admin.md).
public sealed class StaffStore(Database db)
{
    public async Task<string?> RoleAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT staff_role FROM users WHERE user_id = @userId AND disabled_at IS NULL", new { userId }, cancellationToken: ct));
    }

    public async Task<bool> SetRoleAsync(Guid userId, string? role, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET staff_role = @role WHERE user_id = @userId", new { userId, role }, cancellationToken: ct)) > 0;
    }

    public async Task<int> AdminCountAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM users WHERE staff_role = 'admin' AND disabled_at IS NULL", cancellationToken: ct));
    }

    public async Task<bool> SetDisabledAsync(Guid userId, bool disabled, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET disabled_at = CASE WHEN @disabled THEN COALESCE(disabled_at, NOW()) ELSE NULL END WHERE user_id = @userId",
            new { userId, disabled }, tx, cancellationToken: ct)) > 0;
        if (disabled)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE user_sessions SET revoked_at = NOW() WHERE user_id = @userId AND revoked_at IS NULL", new { userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task<int> SignOutEverywhereAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET revoked_at = NOW() WHERE user_id = @userId AND revoked_at IS NULL", new { userId }, cancellationToken: ct));
    }

    // Up to `limit` accounts, newest first; `q` matches name or email.
    public async Task<IReadOnlyList<UserRow>> UsersAsync(string? q, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<UserDb>(new CommandDefinition(
            """
            SELECT u.user_id, u.email, u.display_name, u.email_verified_at, u.password_hash IS NOT NULL AS has_password, u.created_at,
                   u.disabled_at, u.staff_role, u.is_test,
                   COALESCE((SELECT array_agg(o.provider ORDER BY o.provider) FROM oauth_links o WHERE o.user_id = u.user_id), '{}') AS providers,
                   m.team_id, t.name AS team, m.role AS team_role,
                   (SELECT MAX(s.last_seen_at) FROM user_sessions s WHERE s.user_id = u.user_id) AS last_seen,
                   (SELECT COUNT(*) FROM user_sessions s WHERE s.user_id = u.user_id AND s.revoked_at IS NULL AND s.expires_at > NOW())::int AS sessions,
                   (SELECT COUNT(*) FROM projects p JOIN teams o ON o.team_id = p.team_id WHERE o.owner_id = u.user_id)::int AS owned,
                   (SELECT COUNT(*) FROM project_members pm WHERE pm.user_id = u.user_id)::int AS in_projects
            FROM users u
            LEFT JOIN LATERAL (SELECT team_id, role FROM team_members WHERE user_id = u.user_id ORDER BY (role = 'owner') DESC, joined_at LIMIT 1) m ON TRUE
            LEFT JOIN teams t ON t.team_id = m.team_id
            WHERE @q = '' OR u.email ILIKE '%' || @q || '%' OR u.display_name ILIKE '%' || @q || '%'
            ORDER BY u.created_at DESC LIMIT @limit
            """, new { q = (q ?? "").Trim().Replace("%", "").Replace("_", ""), limit }, cancellationToken: ct));
        return rows.Select(r => new UserRow(r.user_id, r.email, r.display_name, r.email_verified_at is not null, r.has_password, Utc(r.created_at),
            r.disabled_at is not null, r.staff_role, r.providers, r.team_id, r.team, r.team_role, r.last_seen is { } l ? Utc(l) : null, r.sessions, r.is_test, r.owned, r.in_projects)).ToList();
    }

    public async Task<IReadOnlyList<TeamRow>> TeamsAsync(string? q, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<TeamDb>(new CommandDefinition(
            "SELECT t.*, u.display_name AS owner, u.email AS owner_email, " + Teams.TeamStore.BonusSql + """
            ,
                   (SELECT COUNT(DISTINCT m.user_id) FROM project_members m JOIN projects p ON p.project_id = m.project_id WHERE p.team_id = t.team_id)::int AS members,
                   (SELECT COUNT(*) FROM team_invites i JOIN projects p ON p.project_id = i.project_id
                     WHERE p.team_id = t.team_id AND i.closed_at IS NULL AND i.created_at > NOW() - INTERVAL '14 days')::int AS invites,
                   (SELECT COUNT(*) FROM projects p WHERE p.team_id = t.team_id)::int AS projects
            FROM teams t JOIN users u ON u.user_id = t.owner_id
            WHERE @q = '' OR t.name ILIKE '%' || @q || '%' OR u.email ILIKE '%' || @q || '%'
            ORDER BY t.created_at DESC LIMIT 500
            """, new { q = (q ?? "").Trim().Replace("%", "").Replace("_", "") }, cancellationToken: ct));
        return rows.Select(r => new TeamRow(r.team_id, r.name, r.owner_id, r.owner, r.owner_email, r.plan, r.period, r.extra_seats, r.extra_storage,
            r.bonus_seats, r.bonus_storage, r.members, r.invites, r.projects, Utc(r.created_at), r.free_seats)).ToList();
    }

    // Moves a project to a team, or back to the operator's (null).
    public async Task<bool> MoveProjectAsync(Guid projectId, Guid? teamId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET team_id = @teamId WHERE project_id = @projectId", new { projectId, teamId }, cancellationToken: ct)) > 0;
    }

    public async Task<Dictionary<Guid, (Guid Id, string Name)>> ProjectTeamsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(Guid, Guid, string)>(new CommandDefinition(
            "SELECT p.project_id, t.team_id, t.name FROM projects p JOIN teams t ON t.team_id = p.team_id", cancellationToken: ct));
        return rows.ToDictionary(r => r.Item1, r => (r.Item2, r.Item3));
    }

    // Test accounts

    public async Task MarkTestAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE users SET is_test = TRUE WHERE user_id = @userId", new { userId }, cancellationToken: ct));
    }

    // Test accounts that can go: those not owning projects.
    public async Task<IReadOnlyList<Guid>> RemovableTestAccountsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT u.user_id FROM users u
            WHERE u.is_test AND NOT EXISTS (
                SELECT 1 FROM teams t JOIN projects p ON p.team_id = t.team_id WHERE t.owner_id = u.user_id)
            """, cancellationToken: ct))).ToList();
    }

    // Audit log

    public async Task WriteAsync(string type, Guid? actor, object? detail = null, Guid? projectId = null, Guid? targetId = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO audit_log (event_type, actor_id, project_id, target_id, detail) VALUES (@type, @actor, @projectId, @targetId, @detail::jsonb)",
            new { type, actor, projectId, targetId, detail = JsonSerializer.Serialize(detail ?? new { }) }, cancellationToken: ct));
    }

    // The latest events, newest first, before `before` (for paging).
    public async Task<IReadOnlyList<AuditRow>> AuditAsync(string? type, long? before, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<AuditDb>(new CommandDefinition(
            """
            SELECT a.audit_id, a.created_at, a.event_type, a.actor_id, COALESCE(u.display_name, u.email, '') AS actor,
                   a.project_id, COALESCE(p.name, '') AS project, a.target_id, COALESCE(a.detail::text, '{}') AS detail
            FROM audit_log a
            LEFT JOIN users u ON u.user_id = a.actor_id
            LEFT JOIN projects p ON p.project_id = a.project_id
            WHERE (@type = '' OR a.event_type LIKE @type || '%') AND (@before::bigint IS NULL OR a.audit_id < @before)
            ORDER BY a.audit_id DESC LIMIT @limit
            """, new { type = type ?? "", before, limit }, cancellationToken: ct));
        return rows.Select(r => new AuditRow(r.audit_id, Utc(r.created_at), r.event_type, r.actor_id, r.actor, r.project_id, r.project, r.target_id, r.detail)).ToList();
    }

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

#pragma warning disable IDE1006 // column names
    private sealed class UserDb
    {
        public Guid user_id { get; init; }
        public string email { get; init; } = "";
        public string display_name { get; init; } = "";
        public DateTime? email_verified_at { get; init; }
        public bool has_password { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? disabled_at { get; init; }
        public string? staff_role { get; init; }
        public bool is_test { get; init; }
        public string[] providers { get; init; } = [];
        public Guid? team_id { get; init; }
        public string? team { get; init; }
        public string? team_role { get; init; }
        public DateTime? last_seen { get; init; }
        public int sessions { get; init; }
        public int owned { get; init; }
        public int in_projects { get; init; }
    }

    private sealed class TeamDb
    {
        public Guid team_id { get; init; }
        public string name { get; init; } = "";
        public Guid owner_id { get; init; }
        public string owner { get; init; } = "";
        public string owner_email { get; init; } = "";
        public string plan { get; init; } = "";
        public string period { get; init; } = "";
        public int extra_seats { get; init; }
        public int extra_storage { get; init; }
        public int bonus_seats { get; init; }
        public int bonus_storage { get; init; }
        public int free_seats { get; init; }
        public int members { get; init; }
        public int invites { get; init; }
        public int projects { get; init; }
        public DateTime created_at { get; init; }
    }

    private sealed class AuditDb
    {
        public long audit_id { get; init; }
        public DateTime created_at { get; init; }
        public string event_type { get; init; } = "";
        public Guid? actor_id { get; init; }
        public string actor { get; init; } = "";
        public Guid? project_id { get; init; }
        public string project { get; init; } = "";
        public Guid? target_id { get; init; }
        public string detail { get; init; } = "{}";
    }
#pragma warning restore IDE1006
}
