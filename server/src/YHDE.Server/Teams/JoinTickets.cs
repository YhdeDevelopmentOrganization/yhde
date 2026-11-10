using System.Security.Cryptography;
using System.Text;
using Dapper;
using YHDE.Server.Accounts;
using YHDE.Server.Persistence;
using YHDE.Server.Projects;

namespace YHDE.Server.Teams;

// Join tickets (teams.md §4): the one-time secret in a starter project's
// join.cfg when it came from an edit link, or was downloaded by someone
// already in the project. A ticket opens nothing by itself: a signed-in
// account redeems it and becomes a member who edits.
public static class JoinTicket
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    // 256 random bits, URL-safe base64 without padding (43 characters).
    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool LooksLikeTicket(string? value) =>
        value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static byte[] Hash(string ticket) => SHA256.HashData(Encoding.UTF8.GetBytes("ticket:" + ticket));
}

public sealed record TicketInfo(
    Guid TicketId,
    Guid ProjectId,
    Guid? LinkId,
    Guid? UserId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt,
    Guid? RedeemedBy,
    bool LinkTurnedOff,
    bool ProjectArchived);

public sealed class JoinTicketStore(Database db)
{
    // linkId: the edit link it was downloaded from. userId: the one account
    // that may redeem it (a download by someone already in the project).
    public async Task<string> CreateAsync(Guid projectId, Guid? linkId, Guid? userId, CancellationToken ct)
    {
        var ticket = JoinTicket.New();
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO join_tickets (ticket_id, project_id, link_id, user_id, token_hash, expires_at)
            VALUES (@id, @projectId, @linkId, @userId, @hash, @expires)
            """,
            new { id = Guid.NewGuid(), projectId, linkId, userId, hash = JoinTicket.Hash(ticket), expires = DateTime.UtcNow + JoinTicket.Lifetime },
            cancellationToken: ct));
        return ticket;
    }

    public async Task<TicketInfo?> FindAsync(string ticket, CancellationToken ct)
    {
        if (!JoinTicket.LooksLikeTicket(ticket)) return null;
        await using var conn = await db.OpenAsync(ct);
        var r = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT t.ticket_id, t.project_id, t.link_id, t.user_id, t.expires_at, t.redeemed_at, t.redeemed_by,
                   (l.link_id IS NOT NULL AND l.revoked_at IS NOT NULL) AS link_off,
                   (p.archived_at IS NOT NULL) AS archived
            FROM join_tickets t
            JOIN projects p ON p.project_id = t.project_id
            LEFT JOIN download_links l ON l.link_id = t.link_id
            WHERE t.token_hash = @hash
            """,
            new { hash = JoinTicket.Hash(ticket) }, cancellationToken: ct));
        return r is null ? null : new TicketInfo(r.ticket_id, r.project_id, r.link_id, r.user_id, Utc(r.expires_at),
            r.redeemed_at is { } at ? Utc(at) : null, r.redeemed_by, r.link_off, r.archived);
    }

    // Marks it used by this account. False: someone else used it first.
    public async Task<bool> ClaimAsync(Guid ticketId, Guid userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE join_tickets SET redeemed_at = COALESCE(redeemed_at, NOW()), redeemed_by = @userId
            WHERE ticket_id = @ticketId AND (redeemed_at IS NULL OR redeemed_by = @userId)
            """,
            new { ticketId, userId }, cancellationToken: ct)) > 0;
    }

    // Whether this unused ticket is one of the places TeamStore.EditHoldsAsync counts.
    public static bool HoldsAPlace(TicketInfo t, DateTimeOffset now) =>
        t.LinkId is not null && !t.LinkTurnedOff && t.RedeemedAt is null && t.ExpiresAt > now;

    private static DateTimeOffset Utc(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));

#pragma warning disable IDE1006 // column names
    private sealed class Row
    {
        public Guid ticket_id { get; init; }
        public Guid project_id { get; init; }
        public Guid? link_id { get; init; }
        public Guid? user_id { get; init; }
        public DateTime expires_at { get; init; }
        public DateTime? redeemed_at { get; init; }
        public Guid? redeemed_by { get; init; }
        public bool link_off { get; init; }
        public bool archived { get; init; }
    }
#pragma warning restore IDE1006
}

// What redeeming gave: the project, and whether they were in it already.
// OwnerEmail: tell the owner someone new joined (null when nobody new did).
public sealed record Redeemed(Guid ProjectId, string Name, string Access, bool Already, string? OwnerEmail);

public static class JoinTicketRedeemer
{
    // The same checks and words as joining with a join code (TeamEndpoints,
    // POST /api/team/join), plus the ticket's own: used by someone else, run
    // out, link turned off, made for another account.
    public static async Task<Redeemed> RedeemAsync(TeamStore teams, JoinTicketStore tickets, IProjectStore projects,
        User user, string? ticket, CancellationToken ct)
    {
        static AccountService.Refused No(string message, int status = 400) => new(message, status);
        var t = await tickets.FindAsync(ticket ?? "", ct) ?? throw No("This invite doesn't work. Ask for a new link.", 404);
        var project = await projects.GetAsync(t.ProjectId, ct) ?? throw No("This invite doesn't work. Ask for a new link.", 404);
        var members = await teams.MembersAsync(t.ProjectId, ct);
        var owner = members.FirstOrDefault(m => m.Role == "owner");
        var ownerName = owner?.Name ?? "the owner";
        if (t.UserId is { } only && only != user.Id)
            throw No($"This download was made for another account. Sign in with that account, or ask {ownerName} for a link.", 403);
        // Someone in the project already just opens it (an unused ticket is
        // marked used, so the place it held is free again).
        var me = members.FirstOrDefault(m => m.UserId == user.Id);
        if (me is not null)
        {
            await tickets.ClaimAsync(t.TicketId, user.Id, ct);
            return new Redeemed(t.ProjectId, project.Name, me.Access, true, null);
        }
        if (t.RedeemedAt is not null && t.RedeemedBy != user.Id)
            throw No($"Someone else already used this invite. Ask {ownerName} for a new link.", 409);
        if (t.ProjectArchived) throw No($"{project.Name} is archived, so it can't take anyone in.", 409);
        if (t.LinkTurnedOff) throw No($"This link was turned off. Ask {ownerName} for a new one.", 410);
        if (t.ExpiresAt <= DateTimeOffset.UtcNow) throw No($"This invite ran out. Ask {ownerName} for a new link.", 410);
        var plan = await teams.OfProjectAsync(t.ProjectId, ct) ?? throw No($"{project.Name} has no owner yet, so it can't take people in.", 409);
        // This ticket already holds one of the places counted here.
        var held = (await teams.EditHoldsAsync([t.ProjectId], ct)).GetValueOrDefault(t.ProjectId);
        var inside = members.Count(m => m.Role == "member") + (await teams.InvitesAsync(t.ProjectId, ct)).Count + held
            - (JoinTicketStore.HoldsAPlace(t, DateTimeOffset.UtcNow) ? 1 : 0);
        if (inside >= plan.PeoplePerProject)
            throw No($"{project.Name} is full: it has room for {plan.PeoplePerProject} developers besides its owner. Ask the owner to make room.", 409);
        if (!await tickets.ClaimAsync(t.TicketId, user.Id, ct))
            throw No($"Someone else already used this invite. Ask {ownerName} for a new link.", 409);
        await teams.AddMemberAsync(t.ProjectId, user.Id, "edit", ct);
        return new Redeemed(t.ProjectId, project.Name, "edit", false, owner?.Email);
    }
}
