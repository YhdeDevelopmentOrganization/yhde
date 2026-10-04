using Dapper;
using YHDE.Server.Persistence;

namespace YHDE.Server.Teams;

// The plans, the same numbers as server/admin-ui/src/world/plans.ts.
// Seats count the owner: a plan's people per project are Seats - 1.
public sealed record Plan(string Id, string Name, int Seats, int StorageGb, int MaxExtraSeats, int? MaxProjects = null);

public static class Plans
{
    public static readonly IReadOnlyList<Plan> All =
    [
        new("solo", "Solo", 1, 2, 2),
        new("trio", "Trio", 3, 5, 3),
        new("team", "Team", 6, 15, 6),
        new("studio", "Studio", 12, 40, 12),
    ];

    // Extra storage is sold in blocks of this size, up to MaxExtraStorage blocks.
    public const int ExtraStorageGb = 10;
    public const int MaxExtraStorage = 20;

    // Free during the beta: up to three projects and 2 GB in all, whichever
    // comes first; each project is its owner and three invited people. Nothing
    // extra to buy. Until 1.0.0 it is the only plan (Open); the paid plans
    // open with the release.
    public static readonly Plan Beta = new("beta", "Beta tester", 4, 2, 0, MaxProjects: 3);

    // A self-hosted server's only plan: its operator pays for the disk, so no
    // seat, storage or project limits beyond the server's own.
    public static readonly Plan SelfHosted = new("self", "Self-hosted", 100_000, 1_000_000, 0);

    // The plan new owners start on.
    public static Plan Default => Site.SiteMode.Official ? Beta : SelfHosted;

    // Everyone who opens a project from one of its view links, at most.
    public const int ViewersPerProject = 5;

    public static bool Open => int.Parse(Gateway.ServerInfo.Version.Split('.')[0]) >= 1;

    public static Plan? Find(string? id) =>
        !Site.SiteMode.Official ? SelfHosted : id == "beta" ? Beta : All.FirstOrDefault(p => p.Id == (id == "duo" ? "trio" : id));

    // The plan a new owner or a plan change may pick right now.
    public static Plan? Pickable(string? id) =>
        !Site.SiteMode.Official ? SelfHosted : Open ? All.FirstOrDefault(p => p.Id == (id == "duo" ? "trio" : id)) : Beta;
}

// What someone who owns projects has: their plan and its limits (the teams
// table, one row per owner). BonusSeats and BonusStorageGb come from promo
// codes still running; FreeSeats are given by staff on the admin page.
public sealed record Team(Guid Id, string Name, Guid OwnerId, string Plan, string Period, int ExtraSeats, int ExtraStorage, DateTimeOffset Created,
    int BonusSeats = 0, int BonusStorageGb = 0, int FreeSeats = 0)
{
    public Plan PlanInfo => Plans.Find(Plan) ?? (Site.SiteMode.Official ? Plans.All[0] : Plans.SelfHosted);
    public int Seats => PlanInfo.Seats + ExtraSeats + BonusSeats + FreeSeats;
    // People each project may have besides its owner (members and open invitations).
    public int PeoplePerProject => Seats - 1;
    public int? MaxProjects => PlanInfo.MaxProjects;
    public long StorageBytes => (PlanInfo.StorageGb + (long)ExtraStorage * Plans.ExtraStorageGb + BonusStorageGb) * 1024L * 1024 * 1024;
}

// A project someone can open, and as what: its owner, or a member who can
// edit or only view.
public sealed record MyProject(Guid ProjectId, Guid TeamId, string Role, string Access);
public sealed record ProjectMember(Guid UserId, string Name, string Email, string Role, string Access, DateTimeOffset Joined, DateTimeOffset? LastSeen);
public sealed record ProjectInvite(Guid Id, Guid ProjectId, string Email, DateTimeOffset Sent)
{
    public DateTimeOffset Expires => Sent + TeamStore.InviteLifetime;
    public bool Expired => Expires <= DateTimeOffset.UtcNow;
}
// An invitation addressed to the signed-in person, or opened from the email's
// link: to which project, who sent it (their account's name and address) and
// to which address.
public sealed record Invitation(Guid Id, Guid ProjectId, string Project, string From, string FromEmail, string To, DateTimeOffset Sent);

public sealed record TeamProjectStats(Guid ProjectId, long Operations, long Files, long FileBytes);
public sealed record DailyCount(Guid ProjectId, DateTime Day, long Count);
public sealed record ActivityRow(Guid ProjectId, Guid OpId, string Type, DateTimeOffset At, string Path, string Name, Guid? WhoId = null);

// Owners and their plans, the people in each project and invitations to
// projects (008_teams.sql, 014_projects_not_teams.sql).
public sealed class TeamStore(Database db)
{
    // Extra seats and storage from running promo codes, for a query on teams t.
    public const string BonusSql = TeamRow.Bonus;

    // An invitation lasts 14 days from when it was (last) sent; after that it
    // no longer holds a seat and can't be accepted. Resending restarts it.
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);
    private const string Open = "i.closed_at IS NULL AND i.created_at > NOW() - INTERVAL '14 days'";
    // Ones that ran out in the last 30 days still show, so they can be resent.
    private const string OpenOrRecent = "i.closed_at IS NULL AND i.created_at > NOW() - INTERVAL '44 days'";

    // Owners

    // The plan of someone who owns projects; null if they own none (they
    // haven't used an access code), though they can still be in others'.
    public async Task<Team?> OwnedAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<TeamRow>(new CommandDefinition(
            "SELECT t.*, " + TeamRow.Bonus + " FROM teams t WHERE t.owner_id = @userId ORDER BY t.created_at LIMIT 1",
            new { userId }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<Team?> GetAsync(Guid teamId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TeamRow>(new CommandDefinition(
            "SELECT t.*, " + TeamRow.Bonus + " FROM teams t WHERE t.team_id = @teamId", new { teamId }, cancellationToken: ct));
        return row?.ToModel();
    }

    // The owner's plan a project comes under.
    public async Task<Team?> OfProjectAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TeamRow>(new CommandDefinition(
            "SELECT t.*, " + TeamRow.Bonus + " FROM teams t JOIN projects p ON p.team_id = t.team_id WHERE p.project_id = @projectId",
            new { projectId }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<Team> CreateAsync(Guid ownerId, string name, Plan plan, string period, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var team = await InsertAsync(conn, tx, ownerId, name, plan, period, ct);
        await tx.CommitAsync(ct);
        return team;
    }

    // The owner's row, inside the caller's transaction (also used by
    // PromoStore.CreateTeamAsync, where a code must work for it to exist).
    internal static async Task<Team> InsertAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx,
        Guid ownerId, string name, Plan plan, string period, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO teams (team_id, name, owner_id, plan, period) VALUES (@id, @name, @ownerId, @plan, @period)",
            new { id, name, ownerId, plan = plan.Id, period }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO team_members (team_id, user_id, role) VALUES (@id, @ownerId, 'owner')", new { id, ownerId }, tx, cancellationToken: ct));
        return new Team(id, name, ownerId, plan.Id, period, 0, 0, DateTimeOffset.UtcNow);
    }

    public async Task UpdateAsync(Guid teamId, string name, string plan, string period, int extraSeats, int extraStorage, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE teams SET name = @name, plan = @plan, period = @period, extra_seats = @extraSeats, extra_storage = @extraStorage
            WHERE team_id = @teamId
            """, new { teamId, name, plan, period, extraSeats, extraStorage }, cancellationToken: ct));
    }

    // Free seats from staff (admin page): more people in each of the owner's projects.
    public async Task SetFreeSeatsAsync(Guid teamId, int freeSeats, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE teams SET free_seats = @freeSeats WHERE team_id = @teamId", new { teamId, freeSeats }, cancellationToken: ct));
    }

    // Projects and who is in them

    // Every project the person can open: their own, then those they were
    // invited to, newest first.
    public async Task<IReadOnlyList<MyProject>> ProjectsForAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<MyProjectRow>(new CommandDefinition(
            """
            SELECT p.project_id, p.team_id, 'owner' AS role, 'edit' AS access, p.created_at
              FROM projects p JOIN teams t ON t.team_id = p.team_id WHERE t.owner_id = @userId
            UNION ALL
            SELECT p.project_id, p.team_id, 'member', m.access, p.created_at
              FROM project_members m JOIN projects p ON p.project_id = m.project_id WHERE m.user_id = @userId
            ORDER BY role DESC, created_at DESC
            """, new { userId }, cancellationToken: ct))).Select(r => new MyProject(r.project_id, r.team_id, r.role, r.access)).ToList();
    }

    // The owner's projects.
    public async Task<IReadOnlyList<Guid>> ProjectIdsAsync(Guid teamId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT project_id FROM projects WHERE team_id = @teamId ORDER BY created_at DESC", new { teamId }, cancellationToken: ct))).ToList();
    }

    // The owner first, then the members. Last seen: the latest use of any of
    // the person's signed-in sessions.
    public async Task<IReadOnlyList<ProjectMember>> MembersAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<MemberRow>(new CommandDefinition(
            """
            SELECT u.user_id, u.display_name, u.email, x.role, x.access, x.joined_at,
                   (SELECT MAX(s.last_seen_at) FROM user_sessions s WHERE s.user_id = u.user_id) AS last_seen
            FROM (
                SELECT t.owner_id AS user_id, 'owner' AS role, 'edit' AS access, p.created_at AS joined_at
                  FROM projects p JOIN teams t ON t.team_id = p.team_id WHERE p.project_id = @projectId
                UNION ALL
                SELECT user_id, 'member', access, joined_at FROM project_members WHERE project_id = @projectId
            ) x JOIN users u ON u.user_id = x.user_id
            ORDER BY (x.role = 'owner') DESC, x.joined_at
            """, new { projectId }, cancellationToken: ct));
        return rows.Select(r => new ProjectMember(r.user_id, r.display_name, r.email, r.role, r.access, Utc(r.joined_at), r.last_seen is { } t ? Utc(t) : null)).ToList();
    }

    // Everyone in these projects, for the dashboard (one query for all).
    public async Task<ILookup<Guid, ProjectMember>> MembersOfAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return Array.Empty<ProjectMember>().ToLookup(_ => Guid.Empty);
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<MemberRow>(new CommandDefinition(
            """
            SELECT x.project_id, u.user_id, u.display_name, u.email, x.role, x.access, x.joined_at,
                   (SELECT MAX(s.last_seen_at) FROM user_sessions s WHERE s.user_id = u.user_id) AS last_seen
            FROM (
                SELECT p.project_id, t.owner_id AS user_id, 'owner' AS role, 'edit' AS access, p.created_at AS joined_at
                  FROM projects p JOIN teams t ON t.team_id = p.team_id WHERE p.project_id = ANY(@ids)
                UNION ALL
                SELECT project_id, user_id, 'member', access, joined_at FROM project_members WHERE project_id = ANY(@ids)
            ) x JOIN users u ON u.user_id = x.user_id
            ORDER BY (x.role = 'owner') DESC, x.joined_at
            """, new { ids = projectIds.ToArray() }, cancellationToken: ct));
        return rows.ToLookup(r => r.project_id,
            r => new ProjectMember(r.user_id, r.display_name, r.email, r.role, r.access, Utc(r.joined_at), r.last_seen is { } t ? Utc(t) : null));
    }

    // Adds someone to a project (staff, or accepting an invitation). False:
    // they were already in it.
    public async Task<bool> AddMemberAsync(Guid projectId, Guid userId, string access, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_members (project_id, user_id, access) VALUES (@projectId, @userId, @access) ON CONFLICT DO NOTHING",
            new { projectId, userId, access }, cancellationToken: ct)) > 0;
    }

    // Join codes (JoinCode.cs): one per project at most, set by its owner.
    public async Task<string?> JoinCodeAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT join_code FROM projects WHERE project_id = @projectId", new { projectId }, cancellationToken: ct));
    }

    public async Task SetJoinCodeAsync(Guid projectId, string? code, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET join_code = @code WHERE project_id = @projectId", new { projectId, code }, cancellationToken: ct));
    }

    // The active project a join code opens; archived projects take nobody in.
    public async Task<Guid?> ProjectForJoinCodeAsync(string code, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT project_id FROM projects WHERE join_code = @code AND archived_at IS NULL", new { code }, cancellationToken: ct));
    }

    public async Task<bool> RemoveMemberAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_members WHERE project_id = @projectId AND user_id = @userId", new { projectId, userId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> SetAccessAsync(Guid projectId, Guid userId, string access, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE project_members SET access = @access WHERE project_id = @projectId AND user_id = @userId",
            new { projectId, userId, access }, cancellationToken: ct)) > 0;
    }

    // Hands a project to one of its members who owns projects too: it moves
    // under their plan, and the old owner stays in it as a member who edits.
    public async Task TransferAsync(Guid projectId, Team to, Guid oldOwnerId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET team_id = @team WHERE project_id = @projectId", new { projectId, team = to.Id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_members WHERE project_id = @projectId AND user_id = @newOwner", new { projectId, newOwner = to.OwnerId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_members (project_id, user_id, access) VALUES (@projectId, @oldOwnerId, 'edit') ON CONFLICT DO NOTHING",
            new { projectId, oldOwnerId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE team_invites SET team_id = @team WHERE project_id = @projectId", new { projectId, team = to.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // Invitations

    // Open invitations to a project; withExpired adds those that ran out in
    // the last 30 days (shown with Resend, holding no seat).
    public async Task<IReadOnlyList<ProjectInvite>> InvitesAsync(Guid projectId, CancellationToken ct, bool withExpired = false) =>
        (await InvitesOfAsync([projectId], ct, withExpired))[projectId].ToList();

    public async Task<ILookup<Guid, ProjectInvite>> InvitesOfAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct, bool withExpired = false)
    {
        if (projectIds.Count == 0) return Array.Empty<ProjectInvite>().ToLookup(_ => Guid.Empty);
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<InviteRow>(new CommandDefinition(
            "SELECT i.invite_id, i.project_id, i.email, i.created_at FROM team_invites i WHERE i.project_id = ANY(@ids) AND "
            + (withExpired ? OpenOrRecent : Open) + " ORDER BY i.created_at DESC",
            new { ids = projectIds.ToArray() }, cancellationToken: ct));
        return rows.ToLookup(r => r.project_id, r => new ProjectInvite(r.invite_id, r.project_id, r.email, Utc(r.created_at)));
    }

    // Returns the secret for the email's accept link (only its hash is kept).
    public async Task<(ProjectInvite Invite, string Token)> InviteAsync(Guid projectId, Guid teamId, string email, Guid invitedBy, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var token = Accounts.Secrets.NewToken();
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO team_invites (invite_id, team_id, project_id, email, invited_by, token_hash)
            VALUES (@id, @teamId, @projectId, @email, @invitedBy, @hash)
            """, new { id, teamId, projectId, email, invitedBy, hash = Accounts.Secrets.Hash(token) }, cancellationToken: ct));
        return (new ProjectInvite(id, projectId, email, DateTimeOffset.UtcNow), token);
    }

    // Sends an invitation again: it lasts 14 more days, and a new accept link
    // replaces the old one. Null: not this project's, or accepted, declined or
    // withdrawn (one that ran out can be resent).
    public async Task<(ProjectInvite Invite, string Token)?> ResendAsync(Guid inviteId, Guid projectId, CancellationToken ct)
    {
        var token = Accounts.Secrets.NewToken();
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<InviteRow>(new CommandDefinition(
            """
            UPDATE team_invites SET created_at = NOW(), token_hash = @hash
            WHERE invite_id = @inviteId AND project_id = @projectId AND closed_at IS NULL
            RETURNING invite_id, project_id, email, created_at
            """, new { inviteId, projectId, hash = Accounts.Secrets.Hash(token) }, cancellationToken: ct));
        return row is null ? null : (new ProjectInvite(row.invite_id, row.project_id, row.email, Utc(row.created_at)), token);
    }

    public async Task<bool> CloseInviteAsync(Guid inviteId, Guid? projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE team_invites SET closed_at = NOW() WHERE invite_id = @inviteId AND closed_at IS NULL AND (@projectId::uuid IS NULL OR project_id = @projectId)",
            new { inviteId, projectId }, cancellationToken: ct)) > 0;
    }

    // The open invitation an email's accept link is for.
    public async Task<Invitation?> InvitationByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<InvitationRow>(new CommandDefinition(
            InvitationRow.Select + " WHERE i.token_hash = @hash AND " + Open,
            new { hash = Accounts.Secrets.Hash(token) }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<Invitation>> InvitationsForAsync(string email, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<InvitationRow>(new CommandDefinition(
            InvitationRow.Select + " WHERE i.email = @email AND " + Open + " ORDER BY i.created_at DESC",
            new { email }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    // Joins the project the invitation is for. The invitation is either
    // addressed to the person's (confirmed) email, or opened with the secret
    // from the email itself (email: null). Null: no such open invitation.
    public async Task<Guid?> AcceptAsync(Guid inviteId, Guid userId, string? email, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var projectId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "UPDATE team_invites i SET closed_at = NOW() WHERE i.invite_id = @inviteId AND (@email::text IS NULL OR i.email = @email) AND "
            + Open + " AND i.project_id IS NOT NULL RETURNING i.project_id",
            new { inviteId, email }, tx, cancellationToken: ct));
        if (projectId is null) return null;
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_members (project_id, user_id, access) VALUES (@projectId, @userId, 'edit') ON CONFLICT DO NOTHING",
            new { projectId, userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return projectId;
    }

    // View links

    // How many people each project's view links let in (TeamStore.ViewersPerProject
    // at most). A link not turned off counts everyone who downloaded with it
    // (their invite codes still work) and, while it still works, each download
    // it has left. Turning it off frees them all.
    public async Task<Dictionary<Guid, int>> ViewersAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, int N)>(new CommandDefinition(
            """
            SELECT l.project_id, SUM(l.uses + CASE WHEN l.max_uses IS NOT NULL AND (l.expires_at IS NULL OR l.expires_at > NOW())
                                                 THEN GREATEST(l.max_uses - l.uses, 0) ELSE 0 END)::int
            FROM download_links l WHERE l.project_id = ANY(@ids) AND l.revoked_at IS NULL GROUP BY 1
            """, new { ids = projectIds.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.N);
    }

    // Turning a view link off also cuts off everyone who downloaded with it.
    public async Task RevokeLinkCodesAsync(Guid linkId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE project_invites SET revoked_at = NOW() WHERE link_id = @linkId AND revoked_at IS NULL", new { linkId }, cancellationToken: ct));
    }

    // Changes in one project, newest first, optionally by one person (the
    // account or invite code that made them).
    public async Task<IReadOnlyList<ActivityRow>> ProjectActivityAsync(Guid projectId, Guid? who, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ActRow>(new CommandDefinition(
            """
            SELECT b.project_id, o.op_id, o.type, o.created_at, COALESCE(o.payload->>'s', '') AS path,
                   COALESCE(sl.member_name, '') AS name, sl.member_id
            FROM operations o JOIN branches b ON b.branch_id = o.branch_id
            LEFT JOIN session_log sl ON sl.session_id = o.session_id
            WHERE b.project_id = @projectId AND b.name = 'main' AND (@who::uuid IS NULL OR sl.member_id = @who)
            ORDER BY o.seq DESC LIMIT @limit
            """, new { projectId, who, limit }, cancellationToken: ct));
        return rows.Select(r => new ActivityRow(r.project_id, r.op_id, r.type, Utc(r.created_at), r.path, r.name, r.member_id)).ToList();
    }

    // Who made changes in a project, with how many, for the activity filter.
    public async Task<IReadOnlyList<(Guid Id, string Name, long Changes)>> ContributorsAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<(Guid, string, long)>(new CommandDefinition(
            """
            SELECT sl.member_id, (array_agg(sl.member_name ORDER BY sl.started_at DESC))[1], COUNT(*)
            FROM operations o JOIN branches b ON b.branch_id = o.branch_id JOIN session_log sl ON sl.session_id = o.session_id
            WHERE b.project_id = @projectId AND b.name = 'main' AND sl.member_id IS NOT NULL
            GROUP BY sl.member_id ORDER BY 3 DESC LIMIT 50
            """, new { projectId }, cancellationToken: ct))).ToList();
    }

    // Project images (012_beta_plan_and_project_images.sql)

    // When each project's image last changed; projects without one are left out.
    public async Task<Dictionary<Guid, DateTimeOffset>> ImageStampsAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, DateTime At)>(new CommandDefinition(
            "SELECT project_id, updated_at FROM project_images WHERE project_id = ANY(@ids)", new { ids = projectIds.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => Utc(r.At));
    }

    public async Task<(string ContentType, byte[] Data)?> ImageAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ImageRow>(new CommandDefinition(
            "SELECT content_type, data FROM project_images WHERE project_id = @projectId", new { projectId }, cancellationToken: ct));
        return row is null ? null : (row.content_type, row.data);
    }

    public async Task SetImageAsync(Guid projectId, string contentType, byte[] data, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_images (project_id, content_type, data) VALUES (@projectId, @contentType, @data)
            ON CONFLICT (project_id) DO UPDATE SET content_type = EXCLUDED.content_type, data = EXCLUDED.data, updated_at = NOW()
            """, new { projectId, contentType, data }, cancellationToken: ct));
    }

    public async Task RemoveImageAsync(Guid projectId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM project_images WHERE project_id = @projectId", new { projectId }, cancellationToken: ct));
    }

    // PNG, JPEG or WebP, told by the file's first bytes (never by what the
    // browser claims), so nothing else can be served back as an image.
    public static string? ImageType(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public async Task AttachProjectAsync(Guid projectId, Guid teamId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET team_id = @teamId WHERE project_id = @projectId", new { projectId, teamId }, cancellationToken: ct));
    }

    // Size and change counts of the team's projects. Files: per path, the
    // latest asset operation, unless it is a delete (as ProjectStore.StatsAsync).
    public async Task<IReadOnlyList<TeamProjectStats>> StatsAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<StatsRow>(new CommandDefinition(
            """
            WITH ops AS (
                SELECT b.project_id, o.seq, o.branch_id, o.type, o.target_id, o.payload
                FROM operations o JOIN branches b ON b.branch_id = o.branch_id
                WHERE b.project_id = ANY(@ids)
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
                      WHERE l.project_id = p.project_id AND l.type <> 'DeleteAsset') AS file_bytes
            FROM projects p WHERE p.project_id = ANY(@ids)
            """, new { ids = projectIds.ToArray() }, cancellationToken: ct));
        return rows.Select(r => new TeamProjectStats(r.project_id, r.operations, r.files, r.file_bytes)).ToList();
    }

    // Changes per day (UTC) over the last `days` days.
    public async Task<IReadOnlyList<DailyCount>> DailyAsync(IReadOnlyList<Guid> projectIds, int days, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<DailyRow>(new CommandDefinition(
            """
            SELECT b.project_id, (o.created_at AT TIME ZONE 'UTC')::date AS day, COUNT(*) AS count
            FROM operations o JOIN branches b ON b.branch_id = o.branch_id
            WHERE b.project_id = ANY(@ids) AND o.created_at >= (NOW() AT TIME ZONE 'UTC')::date - @back
            GROUP BY 1, 2
            """, new { ids = projectIds.ToArray(), back = days - 1 }, cancellationToken: ct));
        return rows.Select(r => new DailyCount(r.project_id, r.day, r.count)).ToList();
    }

    // The latest changes in each project, with the name the editor gave.
    public async Task<IReadOnlyList<ActivityRow>> ActivityAsync(IReadOnlyList<Guid> projectIds, int perProject, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ActRow>(new CommandDefinition(
            """
            SELECT b.project_id, o.op_id, o.type, o.created_at, COALESCE(o.payload->>'s', '') AS path, COALESCE(sl.member_name, '') AS name
            FROM branches b
            CROSS JOIN LATERAL (
                SELECT op_id, type, created_at, payload, session_id FROM operations
                WHERE branch_id = b.branch_id ORDER BY seq DESC LIMIT @perProject
            ) o
            LEFT JOIN session_log sl ON sl.session_id = o.session_id
            WHERE b.project_id = ANY(@ids) AND b.name = 'main'
            ORDER BY o.created_at DESC
            """, new { ids = projectIds.ToArray(), perProject }, cancellationToken: ct));
        return rows.Select(r => new ActivityRow(r.project_id, r.op_id, r.type, Utc(r.created_at), r.path, r.name)).ToList();
    }

    public async Task<Dictionary<Guid, DateTimeOffset>> SessionStartsAsync(IReadOnlyList<Guid> sessionIds, CancellationToken ct)
    {
        if (sessionIds.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(Guid, DateTime)>(new CommandDefinition(
            "SELECT session_id, started_at FROM session_log WHERE session_id = ANY(@ids)", new { ids = sessionIds.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Item1, r => Utc(r.Item2));
    }

    // Deleting an account (GDPR)

    // Removes the person and everything that is only theirs. Their own plan
    // row goes too, which the caller allows only when they own no projects.
    public async Task DeleteUserAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var sql in new[]
        {
            "DELETE FROM team_invites WHERE team_id IN (SELECT team_id FROM teams WHERE owner_id = @userId) OR invited_by = @userId",
            "DELETE FROM promo_redemptions WHERE team_id IN (SELECT team_id FROM teams WHERE owner_id = @userId)",
            "UPDATE promo_redemptions SET redeemed_by = NULL WHERE redeemed_by = @userId",
            "DELETE FROM team_members WHERE team_id IN (SELECT team_id FROM teams WHERE owner_id = @userId) OR user_id = @userId",
            "DELETE FROM teams WHERE owner_id = @userId",
            "DELETE FROM project_members WHERE user_id = @userId",
            "DELETE FROM project_access WHERE user_id = @userId",
            // Nothing on the admin page keeps showing a deleted person.
            "UPDATE session_log SET member_id = NULL, member_name = '' WHERE member_id = @userId",
            "DELETE FROM user_sessions WHERE user_id = @userId",
            "DELETE FROM oauth_links WHERE user_id = @userId",
            "DELETE FROM email_tokens WHERE user_id = @userId",
            "DELETE FROM users WHERE user_id = @userId",
        })
            await conn.ExecuteAsync(new CommandDefinition(sql, new { userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // For "Download my data": the chat messages, direct messages and comments
    // the person wrote, and direct messages sent to them.
    public async Task<object> WrittenByAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var chat = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT c.project_id AS project, p.name AS "projectName", c.author_name AS "from",
                   c.recipient_id IS NOT NULL AS direct, c.author_id = @userId AS "wroteIt", c.body AS text, c.created_at AS sent
            FROM chat_messages c JOIN projects p ON p.project_id = c.project_id
            WHERE c.author_id = @userId OR c.recipient_id = @userId ORDER BY c.created_at
            """, new { userId }, cancellationToken: ct));
        var comments = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT t.project_id AS project, t.scene, m.body AS text, m.created_at AS written, m.edited_at AS edited, m.deleted_at AS deleted
            FROM comment_messages m JOIN comment_threads t ON t.thread_id = m.thread_id
            WHERE m.author_id = @userId ORDER BY m.created_at
            """, new { userId }, cancellationToken: ct));
        return new { chat, comments };
    }

    private sealed class ImageRow
    {
        public string content_type { get; init; } = "";
        public byte[] data { get; init; } = [];
    }

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

#pragma warning disable IDE1006 // column names
    private sealed class TeamRow
    {
        public Guid team_id { get; init; }
        public string name { get; init; } = "";
        public Guid owner_id { get; init; }
        public string plan { get; init; } = "";
        public string period { get; init; } = "";
        public int extra_seats { get; init; }
        public int extra_storage { get; init; }
        public DateTime created_at { get; init; }
        public string role { get; init; } = "";
        public int bonus_seats { get; init; }
        public int bonus_storage { get; init; }
        public int free_seats { get; init; }
        public Team ToModel() => new(team_id, name, owner_id, plan, period, extra_seats, extra_storage, Utc(created_at), bonus_seats, bonus_storage, free_seats);

        // Extra seats and storage from promo codes that are still running.
        public const string Bonus =
            """
            COALESCE((SELECT SUM(c.amount)::int FROM promo_redemptions r JOIN promo_codes c ON c.code_id = r.code_id
                      WHERE r.team_id = t.team_id AND c.kind = 'extra_seats' AND (r.until IS NULL OR r.until > NOW())), 0) AS bonus_seats,
            COALESCE((SELECT SUM(c.amount)::int FROM promo_redemptions r JOIN promo_codes c ON c.code_id = r.code_id
                      WHERE r.team_id = t.team_id AND c.kind = 'extra_storage' AND (r.until IS NULL OR r.until > NOW())), 0) AS bonus_storage
            """;
    }

    private sealed class MyProjectRow
    {
        public Guid project_id { get; init; }
        public Guid team_id { get; init; }
        public string role { get; init; } = "";
        public string access { get; init; } = "";
        public DateTime created_at { get; init; }
    }

    private sealed class MemberRow
    {
        public Guid project_id { get; init; }
        public Guid user_id { get; init; }
        public string display_name { get; init; } = "";
        public string email { get; init; } = "";
        public string role { get; init; } = "";
        public string access { get; init; } = "";
        public DateTime joined_at { get; init; }
        public DateTime? last_seen { get; init; }
    }

    private sealed class InviteRow
    {
        public Guid invite_id { get; init; }
        public Guid project_id { get; init; }
        public string email { get; init; } = "";
        public DateTime created_at { get; init; }
    }

    private sealed class InvitationRow
    {
        public const string Select =
            """
            SELECT i.invite_id, i.project_id, p.name AS project, u.display_name AS sender, u.email AS sender_email, i.email, i.created_at
            FROM team_invites i JOIN projects p ON p.project_id = i.project_id JOIN users u ON u.user_id = i.invited_by
            """;

        public Guid invite_id { get; init; }
        public Guid project_id { get; init; }
        public string project { get; init; } = "";
        public string sender { get; init; } = "";
        public string sender_email { get; init; } = "";
        public string email { get; init; } = "";
        public DateTime created_at { get; init; }
        public Invitation ToModel() => new(invite_id, project_id, project, sender, sender_email, email, Utc(created_at));
    }

    private sealed class StatsRow
    {
        public Guid project_id { get; init; }
        public long operations { get; init; }
        public long files { get; init; }
        public long file_bytes { get; init; }
    }

    private sealed class DailyRow
    {
        public Guid project_id { get; init; }
        public DateTime day { get; init; }
        public long count { get; init; }
    }

    private sealed class ActRow
    {
        public Guid project_id { get; init; }
        public Guid op_id { get; init; }
        public string type { get; init; } = "";
        public DateTime created_at { get; init; }
        public string path { get; init; } = "";
        public string name { get; init; } = "";
        public Guid? member_id { get; init; }
    }
#pragma warning restore IDE1006
}
