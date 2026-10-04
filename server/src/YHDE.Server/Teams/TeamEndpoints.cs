using System.Text.Json;
using Dapper;
using YHDE.Server.Accounts;
using YHDE.Server.Admin;
using YHDE.Server.Assets;
using YHDE.Server.Gateway;
using YHDE.Server.Persistence;
using YHDE.Server.Presence;
using YHDE.Server.Projects;

namespace YHDE.Server.Teams;

// The website dashboard's API (server/admin-ui/src/app/store.ts). People
// belong to projects (teams.md): someone with an access code owns up to three
// projects under their plan, and invites people into each one; anyone can be
// in any number of other people's projects.
//   GET  /api/dashboard                              -> everything the dashboard shows
//   POST /api/team {code}                            -> start owning projects (access code)
//   POST /api/team/plan|seats|storage|promo          -> the owner's plan
//   POST /api/team/projects {name}, /api/team/projects/import?name= (zip body)
//   POST /api/team/projects/{id}/rename|archive|delete|links|transfer|leave
//   POST /api/team/projects/{id}/invites {email}, .../invites/{inviteId}/resend|cancel
//   POST /api/team/projects/{id}/members/{userId}/remove|access
//   GET  /api/team/projects/{id}/activity?who=, /api/team/projects/{id}/image
//   POST /api/team/projects/{id}/image (PNG, JPEG or WebP body), .../image/remove
//   POST /api/team/links/{id}/revoke|delete
//   POST /api/invitations/{id}/accept|decline
//   GET  /api/invitations/by-token/{token}, POST .../accept|decline   (the email's link)
//   GET  /api/me/export, POST /api/me/delete {email}   (GDPR)
// A project's owner changes it and says who is in it; members can open it,
// see who else is, and leave. Every POST needs the X-YHDE header and this
// site's Origin (as /api/auth).
public static class TeamEndpoints
{
    public sealed record NewTeam(string? Name, string? Plan, string? Period, string? Code);
    public sealed record NameRequest(string? Name);
    public sealed record PlanRequest(string? Plan, string? Period);
    public sealed record CountRequest(int Count);
    public sealed record EmailRequest(string? Email);
    public sealed record ArchiveRequest(bool Archived);
    public sealed record LinkRequest(string? Label, int? Hours, int? MaxUses);
    public sealed record CodeRequest(string? Code);
    public sealed record AccessRequest(string? Access);
    public sealed record TransferRequest(Guid UserId);

    private const int Days = 30;
    public const int MaxImageBytes = 2 * 1024 * 1024;

    // Per-account limits, so a signed-in script can't flood the server or
    // use invitations to send email to anyone (security.md). Generous for
    // people clicking around; the site-wide per-address limit is in Program.cs.
    private static readonly Limiter Reads = new(60, TimeSpan.FromMinutes(1));
    private static readonly Limiter Writes = new(120, TimeSpan.FromMinutes(10));
    private static readonly Limiter Invites = new(20, TimeSpan.FromHours(1));
    private static readonly Limiter InvitesTo = new(3, TimeSpan.FromDays(1));
    private static readonly Limiter NewProjects = new(30, TimeSpan.FromHours(1));
    private static readonly Limiter NewLinks = new(60, TimeSpan.FromHours(1));
    private static readonly Limiter Deletes = new(5, TimeSpan.FromHours(1));
    // Promo codes can't be guessed: a few tries per account and per address.
    private static readonly Limiter CodeTries = new(5, TimeSpan.FromHours(1));
    private static readonly Limiter CodeTriesByAddress = new(20, TimeSpan.FromHours(1));

    public static bool TeamsNeedCode(IConfiguration config) => config.GetValue("Yhde:TeamsNeedCode", false);

    // The plan of someone who owns projects. With access codes off, everyone
    // can make projects: their Beta tester plan is made the first time it's needed.
    internal static async Task<Team?> OwnedOrOpenAsync(TeamStore teams, IConfiguration config, User user, CancellationToken ct)
    {
        if (await teams.OwnedAsync(user.Id, ct) is { } owned) return owned;
        if (TeamsNeedCode(config)) return null;
        var name = NameRules.Clean(user.Name, NameRules.Kind.Team);
        return await teams.CreateAsync(user.Id, name.Length == 0 ? "Projects" : name, Plans.Default, "month", ct);
    }

    private static void Limit(Limiter limiter, string key, string message = "Too many changes at once. Wait a few minutes and try again.")
    {
        if (!limiter.Try(key)) throw No(message, 429);
    }

    // The statistics are the expensive part of the dashboard; every open tab
    // asks every few seconds, so a person's are reused for a few seconds.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset At, string Ids, IReadOnlyList<TeamProjectStats> Stats, IReadOnlyList<DailyCount> Daily, IReadOnlyList<ActivityRow> Activity)> StatsCache = new();
    private static readonly TimeSpan StatsFor = TimeSpan.FromSeconds(5);

    private static async Task<(IReadOnlyList<TeamProjectStats>, IReadOnlyList<DailyCount>, IReadOnlyList<ActivityRow>)> StatsAsync(TeamStore teams, Guid userId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var key = string.Join(',', ids);
        if (StatsCache.TryGetValue(userId, out var c) && c.Ids == key && DateTimeOffset.UtcNow - c.At < StatsFor) return (c.Stats, c.Daily, c.Activity);
        var stats = await teams.StatsAsync(ids, ct);
        var daily = await teams.DailyAsync(ids, Days, ct);
        var activity = await teams.ActivityAsync(ids, 25, ct);
        if (StatsCache.Count > 10_000) StatsCache.Clear();
        StatsCache[userId] = (DateTimeOffset.UtcNow, key, stats, daily, activity);
        return (stats, daily, activity);
    }

    private static AccountService.Refused No(string message, int status = 400) => new(message, status);

    public static void MapTeamEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext;
            var path = ctx.Request.Path;
            if (!(path.StartsWithSegments("/api/dashboard") || path.StartsWithSegments("/api/team")
                  || path.StartsWithSegments("/api/invitations") || path.StartsWithSegments("/api/me")))
                return await next(context);
            if (!HttpMethods.IsGet(ctx.Request.Method))
            {
                if (ctx.Request.Headers[AuthEndpoints.CsrfHeader] != "1") return Results.Problem("Missing request header.", statusCode: 403);
                var origin = ctx.Request.Headers.Origin.ToString();
                var options = ctx.RequestServices.GetRequiredService<AccountOptions>();
                if (origin.Length > 0 && !string.Equals(origin, options.BaseUrl(ctx.Request), StringComparison.OrdinalIgnoreCase))
                    return Results.Problem("This request came from another site.", statusCode: 403);
            }
            ctx.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (AccountService.Refused r)
            {
                return Results.Problem(r.Message, statusCode: r.Status);
            }
        });

        api.MapGet("/dashboard", async (HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects,
            ISessionManager sessions, PresenceService presence, PromoStore promos, IConfiguration config, CancellationToken ct) =>
        {
            var user = await SignedInAsync(ctx, accounts, ct);
            Limit(Reads, user.Id.ToString(), "Too many requests. Wait a minute.");
            var invitations = user.EmailVerified ? await teams.InvitationsForAsync(user.Email, ct) : [];
            var me = new { id = user.Id, name = user.Name, email = user.Email, emailVerified = user.EmailVerified };
            var owned = await OwnedOrOpenAsync(teams, config, user, ct);
            var mine = await teams.ProjectsForAsync(user.Id, ct);
            var ids = mine.Select(m => m.ProjectId).ToList();
            var ownIds = mine.Where(m => m.Role == "owner").Select(m => m.ProjectId).ToList();

            var (statList, dailyList, activityList) = await StatsAsync(teams, user.Id, ids, ct);
            var stats = statList.ToDictionary(s => s.ProjectId);
            var daily = dailyList.ToLookup(d => d.ProjectId);
            var activity = activityList.ToLookup(a => a.ProjectId);
            var images = await teams.ImageStampsAsync(ids, ct);
            var members = await teams.MembersOfAsync(ids, ct);
            var invites = await teams.InvitesOfAsync(ownIds, ct, withExpired: true);
            var viewers = await teams.ViewersAsync(ids, ct);
            // Each project's limits come from its owner's plan.
            var plans = new Dictionary<Guid, Team>();
            foreach (var teamId in mine.Select(m => m.TeamId).Distinct())
                if (await teams.GetAsync(teamId, ct) is { } t) plans[teamId] = t;
            var today = DateTime.UtcNow.Date;

            var list = new List<object>();
            foreach (var m in mine)
            {
                var p = await projects.GetAsync(m.ProjectId, ct);
                if (p is null) continue;
                var id = m.ProjectId;
                var own = m.Role == "owner";
                var s = stats.GetValueOrDefault(id);
                var perDay = new long[Days];
                foreach (var d in daily[id])
                {
                    var i = Days - 1 - (int)(today - d.Day.Date).TotalDays;
                    if (i is >= 0 and < Days) perDay[i] = d.Count;
                }
                var links = own ? await projects.ListLinksAsync(id, ct) : [];
                var people = members[id].ToList();
                var owner = people.FirstOrDefault(x => x.Role == "owner");
                list.Add(new
                {
                    id,
                    name = p.Name,
                    created = p.CreatedAt,
                    archived = p.ArchivedAt is not null,
                    files = s?.Files ?? 0,
                    bytes = s?.FileBytes ?? 0,
                    changes = s?.Operations ?? 0,
                    daily = perDay,
                    activity = activity[id].Select(a => new { id = a.OpId, at = a.At, who = a.Name, kind = a.Type, path = a.Path }),
                    image = images.TryGetValue(id, out var imageAt) ? $"/api/team/projects/{id}/image?v={imageAt.ToUnixTimeMilliseconds()}" : null,
                    role = m.Role,
                    myAccess = m.Access,
                    owner = owner is null ? null : new { id = owner.UserId, name = owner.Name },
                    members = people.Select(x => new { id = x.UserId, name = x.Name, email = x.Email, role = x.Role, access = x.Access, joined = x.Joined, lastSeen = x.LastSeen }),
                    peopleLimit = plans.TryGetValue(m.TeamId, out var plan) ? plan.PeoplePerProject : 0,
                    joinCode = own ? await teams.JoinCodeAsync(id, ct) : null,
                    viewers = viewers.GetValueOrDefault(id),
                    viewersLimit = Plans.ViewersPerProject,
                    // For the owner: invitations waiting (and ones that ran
                    // out recently) and view links.
                    invites = invites[id].Select(i => new { id = i.Id, email = i.Email, sent = i.Sent, expires = i.Expires, expired = i.Expired }),
                    links = links.Select(l => new
                    {
                        id = l.LinkId,
                        label = l.Label,
                        created = l.CreatedAt,
                        expires = l.ExpiresAt,
                        maxUses = l.MaxUses,
                        uses = l.Uses,
                        revoked = l.RevokedAt is not null,
                    }),
                });
            }

            var here = ids.SelectMany(id => sessions.GetProjectSessions(id).Select(x => (Project: id, Session: x))).ToList();
            var starts = await teams.SessionStartsAsync(here.Select(h => h.Session.SessionId).ToList(), ct);
            var online = here.Select(h => new
            {
                sessionId = h.Session.SessionId,
                name = h.Session.MemberName,
                projectId = h.Project,
                scene = presence.Get(h.Session.SessionId)?.Scene ?? "",
                since = starts.TryGetValue(h.Session.SessionId, out var t) ? t : DateTimeOffset.UtcNow,
            });

            object? myPlan = null;
            if (owned is { } o)
                myPlan = new
                {
                    id = o.PlanInfo.Id,
                    period = o.Period,
                    extraSeats = o.ExtraSeats,
                    extraStorage = o.ExtraStorage,
                    bonusSeats = o.BonusSeats,
                    bonusStorageGb = o.BonusStorageGb,
                    freeSeats = o.FreeSeats,
                    promos = await promos.ForTeamAsync(o.Id, ct),
                    created = o.Created,
                    maxProjects = o.MaxProjects,
                    projects = ownIds.Count,
                    peoplePerProject = o.PeoplePerProject,
                    storageBytes = o.StorageBytes,
                    usedBytes = ownIds.Sum(id => stats.GetValueOrDefault(id)?.FileBytes ?? 0),
                };

            return Results.Ok(new
            {
                user = me,
                plan = myPlan,
                projects = list,
                invitations,
                presence = online,
                teamsNeedCode = TeamsNeedCode(config),
                // The Godot add-on 0.4 reads these; newer ones use plan and projects.
                team = owned is { } ot ? new { id = ot.Id, name = user.Name, plan = ot.PlanInfo.Id, role = "owner" } : null,
                members = Array.Empty<object>(),
                invites = Array.Empty<object>(),
            });
        });

        // Owning projects

        // Until payments start, owning projects needs an access code (a promo
        // code that unlocks teams, made on the admin page), so strangers can't
        // fill the server. Being invited into someone's project never does.
        // Yhde:TeamsNeedCode=false opens it to everyone.
        api.MapPost("/team", async (NewTeam r, HttpContext ctx, AccountStore accounts, TeamStore teams, PromoStore promos,
            StaffStore staff, IConfiguration config, CancellationToken ct) =>
        {
            var user = await SignedInAsync(ctx, accounts, ct);
            Limit(Writes, user.Id.ToString());
            if (await teams.OwnedAsync(user.Id, ct) is not null) throw No("You can already make projects.", 409);
            var name = NameRules.Clean(r.Name ?? user.Name, NameRules.Kind.Team);
            if (name.Length == 0) name = "Projects";
            var plan = Plans.Pickable(r.Plan) ?? throw No("Pick a plan.");
            var period = plan == Plans.Beta ? "month" : Period(r.Period);
            if (!TeamsNeedCode(config))
                return Results.Ok(new { id = (await teams.CreateAsync(user.Id, name, plan, period, ct)).Id });

            if (string.IsNullOrWhiteSpace(r.Code)) throw No("Enter your access code. During the beta, making projects needs one.");
            const string tooMany = "Too many tries. Wait an hour and try again.";
            Limit(CodeTries, user.Id.ToString(), tooMany);
            Limit(CodeTriesByAddress, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", tooMany);
            Team? team;
            string? why;
            try
            {
                (team, why) = await promos.CreateTeamAsync(r.Code, user.Id, name, plan, period, ct);
            }
            catch (Npgsql.PostgresException e) when (e.SqlState == "40001")
            {
                throw No("Someone used a code at the same moment. Try again.", 409);
            }
            var typed = PromoStore.Normalize(r.Code);
            await staff.WriteAsync(team is null ? "team.refused" : "team.created", user.Id,
                new { code = typed.Length > 40 ? "" : typed, why, plan = plan.Id }, targetId: team?.Id, ct: ct);
            if (why == "other plan") throw No("That code works on a different plan. Pick another plan, or check the code.");
            if (team is null) throw No("That access code doesn't work. Check it, or it may have ended.");
            return Results.Ok(new { id = team.Id });
        });

        api.MapPost("/team/plan", async (PlanRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams, CancellationToken ct) =>
        {
            var (_, team) = await OwnerAsync(ctx, accounts, teams, ct);
            if (!Plans.Open) throw No("During the beta everyone is on the free Beta tester plan. Paid plans start with YHDE 1.0.", 409);
            var next = Plans.Pickable(r.Plan) ?? throw No("Pick a plan.");
            var ids = await teams.ProjectIdsAsync(team.Id, ct);
            if (next.MaxProjects is { } max && ids.Count > max) throw No($"{next.Name} has up to {max} projects, and you have {ids.Count}.", 409);
            // Moving down keeps everyone: people the new plan lacks room for become extra seats.
            var most = await MostPeopleAsync(teams, ids, ct);
            if (most > next.Seats - 1 + next.MaxExtraSeats)
                throw No($"{next.Name} fits {next.Seats - 1 + next.MaxExtraSeats} people in a project with extra seats, and one of yours has {most}. Remove people first.", 409);
            var extraSeats = Math.Max(0, most - (next.Seats - 1));
            var moved = team with { Plan = next.Id, ExtraSeats = extraSeats };
            if (await UsedBytesAsync(teams, team.Id, ct) > moved.StorageBytes)
                throw No($"Your files take more than {next.Name}'s storage. Archive or delete a project, or add storage.", 409);
            await teams.UpdateAsync(team.Id, team.Name, next.Id, Period(r.Period ?? team.Period), extraSeats, team.ExtraStorage, ct);
            return Ok();
        });

        api.MapPost("/team/seats", async (CountRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams, CancellationToken ct) =>
        {
            var (_, team) = await OwnerAsync(ctx, accounts, teams, ct);
            if (!Plans.Open || team.PlanInfo.MaxExtraSeats == 0) throw No("Extra seats can be added from YHDE 1.0 on.", 409);
            var extra = Math.Clamp(r.Count, 0, team.PlanInfo.MaxExtraSeats);
            if (await MostPeopleAsync(teams, await teams.ProjectIdsAsync(team.Id, ct), ct) > (team with { ExtraSeats = extra }).PeoplePerProject)
                throw No("Remove someone from a project first: every seat is in use.", 409);
            await teams.UpdateAsync(team.Id, team.Name, team.Plan, team.Period, extra, team.ExtraStorage, ct);
            return Ok();
        });

        api.MapPost("/team/storage", async (CountRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams, CancellationToken ct) =>
        {
            var (_, team) = await OwnerAsync(ctx, accounts, teams, ct);
            if (!Plans.Open) throw No("Extra storage can be added from YHDE 1.0 on.", 409);
            var extra = Math.Clamp(r.Count, 0, Plans.MaxExtraStorage);
            if (await UsedBytesAsync(teams, team.Id, ct) > (team with { ExtraStorage = extra }).StorageBytes)
                throw No("Your files would not fit. Delete or archive a project first.", 409);
            await teams.UpdateAsync(team.Id, team.Name, team.Plan, team.Period, team.ExtraSeats, extra, ct);
            return Ok();
        });

        api.MapPost("/team/promo", async (CodeRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams, PromoStore promos,
            Database db, CancellationToken ct) =>
        {
            var (user, team) = await OwnerAsync(ctx, accounts, teams, ct);
            const string wrong = "That code doesn't work. Check it, or it may have ended.";
            Limit(CodeTries, team.Id.ToString(), "Too many tries. Wait an hour and try again.");
            Limit(CodeTriesByAddress, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", "Too many tries. Wait an hour and try again.");
            string? why;
            try
            {
                why = await promos.RedeemAsync(r.Code ?? "", team, user.Id, ct);
            }
            catch (Npgsql.PostgresException e) when (e.SqlState == "40001")
            {
                throw No("Someone used a code at the same moment. Try again.", 409);
            }
            await using (var conn = await db.OpenAsync(ct))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO audit_log (event_type, actor_id, target_id, detail) VALUES (@type, @actor, @team, @detail::jsonb)",
                    new { type = why is null ? "promo.redeemed" : "promo.refused", actor = user.Id, team = team.Id, detail = JsonSerializer.Serialize(new { code = PromoStore.Normalize(r.Code).Length > 40 ? "" : PromoStore.Normalize(r.Code), why }) },
                    cancellationToken: ct));
            }
            if (why == "already used by this team") throw No("You have already used this code.", 409);
            if (why is not null) throw No(wrong);
            return Ok();
        });

        // The Godot add-on 0.4 invited people to a whole team; that's gone.
        foreach (var old in new[] { "/team/invites", "/team/invites/{id:guid}/cancel", "/team/leave", "/team/rename",
                     "/team/members/{userId:guid}/remove", "/team/members/{userId:guid}/role", "/team/projects/{id:guid}/access" })
            api.MapPost(old, () => Results.Problem("People are invited into each project now. Do it on the project's page on the YHDE website.", statusCode: 410));

        // People in a project

        api.MapPost("/team/projects/{id:guid}/invites", async (Guid id, EmailRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams,
            IProjectStore projects, IEmailSender mail, AccountOptions options, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            if (x.Project.ArchivedAt is not null) throw No("The project is archived: restore it first.", 409);
            var email = YHDE.Server.Site.SiteStore.NormalizeEmail(r.Email ?? "") ?? throw No("That email address does not look right.");
            Limit(Invites, x.User.Id.ToString(), "You've sent a lot of invitations. Wait an hour and try again.");
            Limit(InvitesTo, email, "That address has been invited several times today. Try again tomorrow.");
            if (email == x.User.Email) throw No("That's you: you own this project.", 409);
            if ((await teams.MembersAsync(id, ct)).Any(m => m.Email == email)) throw No("That person is already in the project.", 409);
            var waiting = await teams.InvitesAsync(id, ct, withExpired: true);
            if (waiting.Any(i => i.Email == email && !i.Expired)) throw No("An invitation to that address is already waiting. Resend it instead.", 409);
            if (await PeopleInAsync(teams, id, ct) >= x.Plan.PeoplePerProject) throw No(FullMessage(x.Plan, x.Project.Name), 409);
            // An earlier invitation to them that ran out goes; this one replaces it.
            foreach (var old in waiting.Where(i => i.Email == email)) await teams.CloseInviteAsync(old.Id, id, ct);
            var (_, token) = await teams.InviteAsync(id, x.Plan.Id, email, x.User.Id, ct);
            await SendInviteAsync(mail, options.BaseUrl(ctx.Request), x.User, x.Project.Name, email, token, false, ct);
            return Ok();
        });

        // Sends it again with a new link, and it lasts 14 more days. One that
        // ran out takes its seat back, so it needs room.
        api.MapPost("/team/projects/{id:guid}/invites/{inviteId:guid}/resend", async (Guid id, Guid inviteId, HttpContext ctx, AccountStore accounts,
            TeamStore teams, IProjectStore projects, IEmailSender mail, AccountOptions options, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            Limit(Invites, x.User.Id.ToString(), "You've sent a lot of invitations. Wait an hour and try again.");
            var known = (await teams.InvitesAsync(id, ct, withExpired: true)).FirstOrDefault(i => i.Id == inviteId)
                ?? throw No("That invitation is no longer open.", 404);
            Limit(InvitesTo, known.Email, "That address has been invited several times today. Try again tomorrow.");
            if (known.Expired && await PeopleInAsync(teams, id, ct) >= x.Plan.PeoplePerProject) throw No(FullMessage(x.Plan, x.Project.Name), 409);
            var (invite, token) = await teams.ResendAsync(inviteId, id, ct) ?? throw No("That invitation is no longer open.", 404);
            await SendInviteAsync(mail, options.BaseUrl(ctx.Request), x.User, x.Project.Name, invite.Email, token, true, ct);
            return Ok();
        });

        api.MapPost("/team/projects/{id:guid}/invites/{inviteId:guid}/cancel", async (Guid id, Guid inviteId, HttpContext ctx, AccountStore accounts,
            TeamStore teams, IProjectStore projects, CancellationToken ct) =>
        {
            await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            await teams.CloseInviteAsync(inviteId, id, ct);
            return Ok();
        });

        // Join codes: the owner makes one (a new one replaces the old) or turns
        // it off; anyone signed in who enters it joins as a developer.
        api.MapPost("/team/projects/{id:guid}/join-code", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams,
            IProjectStore projects, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            if (x.Project.ArchivedAt is not null) throw No("The project is archived: restore it first.", 409);
            for (var attempt = 0; ; attempt++)
            {
                var code = JoinCode.New();
                try
                {
                    await teams.SetJoinCodeAsync(id, code, ct);
                    return Results.Ok(new { code });
                }
                catch (Npgsql.PostgresException e) when (e.SqlState == "23505" && attempt < 3)
                {
                    // Another project has that code already: pick another.
                }
            }
        });

        api.MapPost("/team/projects/{id:guid}/join-code/off", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams,
            IProjectStore projects, CancellationToken ct) =>
        {
            await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            await teams.SetJoinCodeAsync(id, null, ct);
            return Ok();
        });

        api.MapPost("/team/join", async (CodeRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects,
            EditorTokens editors, IEmailSender mail, CancellationToken ct) =>
        {
            var user = await SignedInAsync(ctx, accounts, ct);
            const string tooMany = "Too many tries. Wait an hour and try again.";
            Limit(CodeTries, user.Id.ToString(), tooMany);
            Limit(CodeTriesByAddress, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", tooMany);
            var code = JoinCode.Normalize(r.Code) ?? throw No("That doesn't look like a join code. It's 8 letters and numbers, like K7QM-2XRD.");
            var projectId = await teams.ProjectForJoinCodeAsync(code, ct)
                ?? throw No("That join code doesn't work. Ask the project's owner for the current one.", 404);
            var project = await projects.GetAsync(projectId, ct) ?? throw No("That join code doesn't work.", 404);
            var members = await teams.MembersAsync(projectId, ct);
            if (members.Any(m => m.UserId == user.Id)) return Results.Ok(new { id = projectId, name = project.Name, already = true });
            var plan = await teams.OfProjectAsync(projectId, ct) ?? throw No($"{project.Name} has no owner yet, so it can't take people in.", 409);
            if (await PeopleInAsync(teams, projectId, ct) >= plan.PeoplePerProject)
                throw No($"{project.Name} is full: it has room for {plan.PeoplePerProject} developers besides its owner. Ask the owner to make room.", 409);
            await teams.AddMemberAsync(projectId, user.Id, "edit", ct);
            editors.Forget(); // their editor can open it at once
            if (members.FirstOrDefault(m => m.Role == "owner") is { } owner)
                await NotifyAsync(mail, owner.Email, $"{user.Name} joined {project.Name}",
                    $"Hi,\n\n{user.Name} ({user.Email}) joined your project {project.Name} on YHDE with its join code. You can remove them or make a new code on the project's page.", ct);
            return Results.Ok(new { id = projectId, name = project.Name, already = false });
        });

        api.MapPost("/team/projects/{id:guid}/members/{userId:guid}/remove", async (Guid id, Guid userId, HttpContext ctx, AccountStore accounts,
            TeamStore teams, IProjectStore projects, EditorTokens editors, ISessionManager sessions, CancellationToken ct) =>
        {
            await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            if (!await teams.RemoveMemberAsync(id, userId, ct)) throw No("That person is not in the project.", 404);
            editors.Forget(); // their editor loses the project now
            await EditorTokens.DisconnectAsync(sessions, userId);
            return Ok();
        });

        // Whether a member can edit or only view.
        api.MapPost("/team/projects/{id:guid}/members/{userId:guid}/access", async (Guid id, Guid userId, AccessRequest r, HttpContext ctx,
            AccountStore accounts, TeamStore teams, IProjectStore projects, EditorTokens editors, ISessionManager sessions, CancellationToken ct) =>
        {
            await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            var access = r.Access is "edit" or "view" ? r.Access : throw No("Pick edit or view.");
            if (!await teams.SetAccessAsync(id, userId, access, ct)) throw No("That person is not in the project.", 404);
            editors.Forget();
            // Less access than before: their open connection re-checks at once.
            if (access == "view") await EditorTokens.DisconnectAsync(sessions, userId);
            return Ok();
        });

        api.MapPost("/team/projects/{id:guid}/leave", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects,
            EditorTokens editors, ISessionManager sessions, IEmailSender mail, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: false, ct);
            if (x.Role == "owner") throw No("You own this project. Hand it to someone else first, or delete it.", 409);
            await teams.RemoveMemberAsync(id, x.User.Id, ct);
            editors.Forget();
            await EditorTokens.DisconnectAsync(sessions, x.User.Id);
            if (await OwnerEmailAsync(teams, id, ct) is { } to)
                await NotifyAsync(mail, to, $"{x.User.Name} left {x.Project.Name}",
                    $"Hi,\n\n{x.User.Name} ({x.User.Email}) left your project {x.Project.Name} on YHDE. Their seat is free again.", ct);
            return Ok();
        });

        // Hands the project to a member who owns projects too; the old owner
        // stays in it as a member who edits.
        api.MapPost("/team/projects/{id:guid}/transfer", async (Guid id, TransferRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams,
            IProjectStore projects, EditorTokens editors, StaffStore staff, IEmailSender mail, AccountOptions options, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            var member = (await teams.MembersAsync(id, ct)).FirstOrDefault(m => m.UserId == r.UserId && m.Role == "member")
                ?? throw No("Pick someone who is in the project.", 404);
            var to = await teams.OwnedAsync(member.UserId, ct)
                ?? throw No($"{member.Name} can't own projects yet: they need a beta access code first.", 409);
            if (to.MaxProjects is { } max && (await teams.ProjectIdsAsync(to.Id, ct)).Count >= max)
                throw No($"{member.Name} already has {max} projects, the most a beta tester can have.", 409);
            var size = (await teams.StatsAsync([id], ct)).FirstOrDefault()?.FileBytes ?? 0;
            if (await UsedBytesAsync(teams, to.Id, ct) + size > to.StorageBytes)
                throw No($"{x.Project.Name} doesn't fit in {member.Name}'s storage.", 409);
            await teams.TransferAsync(id, to, x.User.Id, ct);
            editors.Forget();
            await staff.WriteAsync("project.transferred", x.User.Id, new { to = member.UserId }, projectId: id, ct: ct);
            await NotifyAsync(mail, member.Email, $"{x.User.Name} gave you {x.Project.Name}",
                $"Hi,\n\n{x.User.Name} ({x.User.Email}) handed their project {x.Project.Name} to you on YHDE. You own it now: " +
                $"you invite people, make view links and decide what happens to it. {x.User.Name} stays in it and can still edit.\n\n" +
                $"{options.BaseUrl(ctx.Request)}/app#/projects/{id}", ct);
            return Ok();
        });

        // Changes in the project, optionally by one person, and who made them.
        api.MapGet("/team/projects/{id:guid}/activity", async (Guid id, Guid? who, HttpContext ctx, AccountStore accounts, TeamStore teams,
            IProjectStore projects, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: false, ct, write: false);
            Limit(Reads, x.User.Id.ToString(), "Too many requests. Wait a minute.");
            var rows = await teams.ProjectActivityAsync(id, who, 200, ct);
            var people = await teams.ContributorsAsync(id, ct);
            return Results.Ok(new
            {
                people = people.Select(p => new { id = p.Id, name = p.Name, changes = p.Changes }),
                activity = rows.Select(a => new { id = a.OpId, at = a.At, who = a.Name, whoId = a.WhoId, kind = a.Type, path = a.Path }),
            });
        });

        // Answering an invitation

        // On the dashboard: an invitation to the person's confirmed address.
        api.MapPost("/invitations/{id:guid}/accept", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams, EditorTokens editors,
            IEmailSender mail, CancellationToken ct) =>
        {
            var user = await SignedInAsync(ctx, accounts, ct);
            Limit(Writes, user.Id.ToString());
            if (!user.EmailVerified) throw No("Confirm your email address first: open the link we sent you.", 403);
            var invitation = (await teams.InvitationsForAsync(user.Email, ct)).FirstOrDefault(i => i.Id == id)
                ?? throw No("This invitation is no longer open.", 404);
            await JoinAsync(teams, editors, mail, user, invitation, user.Email, ct);
            return Ok();
        });

        api.MapPost("/invitations/{id:guid}/decline", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams, IEmailSender mail, CancellationToken ct) =>
        {
            var user = await SignedInAsync(ctx, accounts, ct);
            Limit(Writes, user.Id.ToString());
            var invitation = (await teams.InvitationsForAsync(user.Email, ct)).FirstOrDefault(i => i.Id == id)
                ?? throw No("This invitation is no longer open.", 404);
            await DeclineAsync(teams, mail, invitation, user.Name, ct);
            return Ok();
        });

        // The invitation an email's link is for, before accepting. The link
        // itself is the proof: only the invited address received it.
        api.MapGet("/invitations/by-token/{token}", async (string token, HttpContext ctx, AccountStore accounts, TeamStore teams, CancellationToken ct) =>
        {
            Limit(Reads, "token:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"), "Too many requests. Wait a minute.");
            var i = await teams.InvitationByTokenAsync(token, ct) ?? throw No("This invitation is no longer open. It may have been accepted, declined, withdrawn, or it ran out after 14 days.", 404);
            var me = await AuthEndpoints.CurrentAsync(ctx, accounts, ct);
            return Results.Ok(new { project = i.Project, from = i.From, fromEmail = i.FromEmail, to = i.To, sent = i.Sent, signedInAs = me?.User.Email });
        });

        api.MapPost("/invitations/by-token/{token}/accept", async (string token, HttpContext ctx, AccountStore accounts, TeamStore teams, EditorTokens editors,
            IEmailSender mail, CancellationToken ct) =>
        {
            var user = await SignedInAsync(ctx, accounts, ct);
            Limit(Writes, user.Id.ToString());
            var i = await teams.InvitationByTokenAsync(token, ct) ?? throw No("This invitation is no longer open.", 404);
            await JoinAsync(teams, editors, mail, user, i, null, ct);
            return Ok();
        });

        api.MapPost("/invitations/by-token/{token}/decline", async (string token, HttpContext ctx, TeamStore teams, IEmailSender mail, CancellationToken ct) =>
        {
            Limit(Writes, "token:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
            var i = await teams.InvitationByTokenAsync(token, ct) ?? throw No("This invitation is no longer open.", 404);
            await DeclineAsync(teams, mail, i, i.To, ct);
            return Ok();
        });

        // Projects

        api.MapPost("/team/projects", async (NameRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects, AdminStats stats, EditorTokens editors, CancellationToken ct) =>
        {
            var (_, team) = await OwnerAsync(ctx, accounts, teams, ct);
            var name = CleanName(r.Name, NameRules.Kind.Project, "Give the project a name.");
            Limit(NewProjects, team.Id.ToString(), "You've made a lot of projects in a short time. Wait an hour and try again.");
            await EnsureRoomForProjectAsync(teams, team, ct);
            await EnsureNewNameAsync(teams, projects, team.Id, name, null, ct);
            var p = await projects.CreateAsync(name, ct);
            await teams.AttachProjectAsync(p.ProjectId, team.Id, ct);
            stats.Invalidate();
            editors.Forget(); // signed-in editors may open it at once
            return Results.Ok(new { id = p.ProjectId });
        });

        api.MapPost("/team/projects/import", async (HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects,
            ProjectImporter importer, BlobStore blobs, AdminStats stats, EditorTokens editors, CancellationToken ct) =>
        {
            var (_, team) = await OwnerAsync(ctx, accounts, teams, ct);
            var name = CleanName(ctx.Request.Query["name"], NameRules.Kind.Project, "Give the project a name.");
            Limit(NewProjects, team.Id.ToString(), "You've made a lot of projects in a short time. Wait an hour and try again.");
            await EnsureRoomForProjectAsync(teams, team, ct);
            await EnsureNewNameAsync(teams, projects, team.Id, name, null, ct);
            var room = team.StorageBytes - await UsedBytesAsync(teams, team.Id, ct);
            if (ctx.Request.ContentLength is { } size && size > room)
                throw No("That zip does not fit in your storage. Delete a project first.", 413);
            var limit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = Math.Min(AdminProjectEndpoints.MaxGameZipBytes, Math.Max(0, room));
            var tmp = blobs.NewTempPath();
            try
            {
                await using (var file = File.Create(tmp)) await ctx.Request.Body.CopyToAsync(file, ct);
                if (new FileInfo(tmp).Length == 0) throw No("Choose the game's zip file.");
                var result = await importer.ImportAsync(tmp, name, ct);
                await teams.AttachProjectAsync(result.Project.ProjectId, team.Id, ct);
                stats.Invalidate();
                editors.Forget();
                return Results.Ok(new { id = result.Project.ProjectId, files = result.Files, skipped = result.Skipped.Count });
            }
            catch (ImportException e)
            {
                throw No(e.Message);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        });

        api.MapPost("/team/projects/{id:guid}/rename", async (Guid id, NameRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            var name = CleanName(r.Name, NameRules.Kind.Project, "The name cannot be empty.");
            await EnsureNewNameAsync(teams, projects, x.Plan.Id, name, id, ct);
            await projects.RenameAsync(id, name, ct);
            return Ok();
        });

        // The project's image. Anyone in the project may see it; its owner changes it.
        api.MapGet("/team/projects/{id:guid}/image", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams, CancellationToken ct) =>
        {
            var user = await SignedInAsync(ctx, accounts, ct);
            Limit(Reads, user.Id.ToString(), "Too many requests. Wait a minute.");
            if (!(await teams.ProjectsForAsync(user.Id, ct)).Any(m => m.ProjectId == id)) return Results.NotFound();
            if (await teams.ImageAsync(id, ct) is not { } image) return Results.NotFound();
            // The address changes with the image (?v=), so it can be kept.
            ctx.Response.Headers.CacheControl = "private, max-age=604800, immutable";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(image.Data, image.ContentType);
        });

        api.MapPost("/team/projects/{id:guid}/image", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects, CancellationToken ct) =>
        {
            await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            if (ctx.Request.ContentLength is > MaxImageBytes) throw No("The image is too big. Use one under 2 MB.", 413);
            var limit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxImageBytes;
            using var body = new MemoryStream();
            try
            {
                await ctx.Request.Body.CopyToAsync(body, ct);
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
            {
                throw No("The image is too big. Use one under 2 MB.", 413);
            }
            if (body.Length == 0) throw No("Choose an image.");
            var data = body.ToArray();
            var type = TeamStore.ImageType(data) ?? throw No("Use a PNG, JPEG or WebP image.");
            await teams.SetImageAsync(id, type, data, ct);
            return Ok();
        });

        api.MapPost("/team/projects/{id:guid}/image/remove", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects, CancellationToken ct) =>
        {
            await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            await teams.RemoveImageAsync(id, ct);
            return Ok();
        });

        api.MapPost("/team/projects/{id:guid}/archive", async (Guid id, ArchiveRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams,
            IProjectStore projects, ISessionManager sessions, AccessGate gate, CancellationToken ct) =>
        {
            await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            if (r.Archived && sessions.GetProjectSessions(id).Any())
                throw No("Someone is working in it right now. Try again when nobody is connected.", 409);
            await projects.SetArchivedAsync(id, r.Archived, ct);
            gate.ForgetInvites();
            return Ok();
        });

        api.MapPost("/team/projects/{id:guid}/delete", async (Guid id, NameRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams,
            IProjectStore projects, Database db, BlobStore blobs, AdminStats stats, AccessGate gate, ISessionManager sessions, ILoggerFactory logs, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            var project = x.Project;
            if (project.ArchivedAt is null) throw No("Archive the project first.", 409);
            if (!string.Equals(r.Name?.Trim(), project.Name, StringComparison.Ordinal)) throw No("The name does not match.");
            if (sessions.GetProjectSessions(id).Any()) throw No("Someone is still connected to this project. Try again in a minute.", 409);
            var removed = await projects.DeleteArchivedAsync(id, ct) ?? throw No("The project is no longer archived.", 409);
            gate.ForgetInvites();
            stats.Invalidate();
            await using (var conn = await db.OpenAsync(ct))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO audit_log (event_type, actor_id, project_id, detail) VALUES ('project.deleted', @actor, @id, @detail::jsonb)",
                    new { actor = x.User.Id, id, detail = JsonSerializer.Serialize(new { name = project.Name, team = x.Plan.Id, removed.Operations }) },
                    cancellationToken: ct));
            }
            _ = Task.Run(() => AdminProjectEndpoints.FreeUnusedFiles(db, blobs, logs.CreateLogger("YHDE.Server.Teams.Cleanup")));
            return Ok();
        });

        // View links: each lets in a number of people who can open the
        // project in Godot and watch, never change anything, up to
        // Plans.ViewersPerProject per project.
        api.MapPost("/team/projects/{id:guid}/links", async (Guid id, LinkRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams,
            IProjectStore projects, AccountOptions options, CancellationToken ct) =>
        {
            var x = await ProjectAsync(ctx, accounts, teams, projects, id, ownerOnly: true, ct);
            if (x.Project.ArchivedAt is not null) throw No("The project is archived: restore it first.", 409);
            if (r.Hours is < 0 or > 24 * 365) throw No("A link lasts up to a year.");
            Limit(NewLinks, x.User.Id.ToString(), "You've made a lot of links in a short time. Wait an hour and try again.");
            var free = Plans.ViewersPerProject - (await teams.ViewersAsync([id], ct)).GetValueOrDefault(id);
            if (free <= 0) throw No($"A project has room for {Plans.ViewersPerProject} viewers, and view links already hold them all. Turn one off to make room.", 409);
            if (r.MaxUses is not { } uses || uses < 1) throw No("Say how many people the link lets in.");
            if (uses > free) throw No($"There's room for {free} more {(free == 1 ? "viewer" : "viewers")} in this project.", 409);
            DateTimeOffset? expires = r.Hours is > 0 and var h ? DateTimeOffset.UtcNow.AddHours(h) : null;
            var (_, token) = await projects.CreateLinkAsync(id, NameRules.Clean(r.Label, NameRules.Kind.Label), expires, uses, ct);
            // The only time the token exists outside the link that is sent.
            return Results.Ok(new { url = $"{options.BaseUrl(ctx.Request)}/join/{token}" });
        });

        // Turning a link off frees its room: nobody new can download with it,
        // and the people who did are cut off.
        api.MapPost("/team/links/{id:guid}/revoke", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects,
            AccessGate gate, CancellationToken ct) =>
        {
            await OwnLinkAsync(ctx, accounts, teams, projects, id, ct);
            await projects.RevokeLinkAsync(id, ct);
            await teams.RevokeLinkCodesAsync(id, ct);
            gate.ForgetInvites();
            return Ok();
        });

        api.MapPost("/team/links/{id:guid}/delete", async (Guid id, HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects,
            AccessGate gate, CancellationToken ct) =>
        {
            await OwnLinkAsync(ctx, accounts, teams, projects, id, ct);
            await teams.RevokeLinkCodesAsync(id, ct);
            await projects.DeleteLinkAsync(id, ct);
            gate.ForgetInvites();
            return Ok();
        });

        // Your data (GDPR)

        api.MapGet("/me/export", async (HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects, YHDE.Server.Site.SiteStore site, CancellationToken ct) =>
        {
            var user = await WebOnlyAsync(ctx, accounts, ct);
            Limit(Writes, user.Id.ToString());
            var owned = await teams.OwnedAsync(user.Id, ct);
            var mine = new List<object>();
            foreach (var m in await teams.ProjectsForAsync(user.Id, ct))
                if (await projects.GetAsync(m.ProjectId, ct) is { } p) mine.Add(new { p.Name, m.Role, m.Access });
            var data = new
            {
                exported = DateTimeOffset.UtcNow,
                account = new { user.Id, user.Email, user.EmailVerified, user.Name, user.HasPassword, user.Created },
                signInProviders = await accounts.ProvidersAsync(user.Id, ct),
                sessions = (await accounts.SessionsAsync(user.Id, ct)).Select(s => new { s.Kind, s.Device, s.Ip, s.Created, s.LastSeen, s.Expires }),
                plan = owned is { } t ? new { plan = t.PlanInfo.Name, t.Period, t.Created } : null,
                projects = mine,
                invitationsToYou = user.EmailVerified ? await teams.InvitationsForAsync(user.Email, ct) : [],
                earlyAccessList = (await site.EarlyAccessAsync(ct)).FirstOrDefault(e => e.Email == user.Email),
                messagesAndComments = await teams.WrittenByAsync(user.Id, ct),
                note = "The projects themselves (scenes, scripts, files) belong to their owners; they are in each person's Godot project folder.",
            };
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"yhde-my-data.json\"";
            return Results.Json(data, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        });

        api.MapPost("/me/delete", async (EmailRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams, YHDE.Server.Site.SiteStore site, Database db, CancellationToken ct) =>
        {
            var user = await WebOnlyAsync(ctx, accounts, ct);
            Limit(Deletes, user.Id.ToString());
            if (!string.Equals(AccountStore.NormalizeEmail(r.Email ?? ""), user.Email, StringComparison.Ordinal))
                throw No("Type your email address exactly to delete your account.");
            if (await teams.OwnedAsync(user.Id, ct) is { } owned && (await teams.ProjectIdsAsync(owned.Id, ct)).Count > 0)
                throw No("You still own projects. Hand them to someone, or archive and delete them first, so nothing is lost by accident.", 409);
            await teams.DeleteUserAsync(user.Id, ct);
            await site.RemoveEarlyAccessAsync(user.Email, ct);
            await using (var conn = await db.OpenAsync(ct))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO audit_log (event_type, actor_id, detail) VALUES ('account.deleted', @actor, '{}'::jsonb)",
                    new { actor = user.Id }, cancellationToken: ct));
            }
            ctx.Response.Cookies.Delete(AuthEndpoints.SessionCookie);
            return Ok();
        });
    }

    private static IResult Ok() => Results.Ok(new { ok = true });

    // The website's sign-in cookie, or the Godot editor's sign-in token (the
    // YHDE panel manages projects too). Tokens can't be sent by other sites,
    // so they need no CSRF check beyond the X-YHDE header.
    private static async Task<User> SignedInAsync(HttpContext ctx, AccountStore accounts, CancellationToken ct)
    {
        if (await AuthEndpoints.CurrentAsync(ctx, accounts, ct) is { } web) return web.User;
        var h = ctx.Request.Headers.Authorization.ToString();
        if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && EditorTokens.LooksLikeToken(h[7..].Trim())
            && await accounts.SessionAsync(h[7..].Trim(), "editor", ct) is { } editor)
            return editor.User;
        throw No("Not signed in.", 401);
    }

    // Your data and deleting the account: only on the website, signed in there.
    private static async Task<User> WebOnlyAsync(HttpContext ctx, AccountStore accounts, CancellationToken ct) =>
        (await AuthEndpoints.CurrentAsync(ctx, accounts, ct))?.User ?? throw No("Do this on the website, signed in there.", 401);

    // Someone who owns projects: their plan.
    private static async Task<(User User, Team Team)> OwnerAsync(HttpContext ctx, AccountStore accounts, TeamStore teams, CancellationToken ct)
    {
        var user = await SignedInAsync(ctx, accounts, ct);
        Limit(Writes, user.Id.ToString());
        var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var team = await OwnedOrOpenAsync(teams, config, user, ct) ?? throw No("You can't make projects yet. Enter a beta access code first.", 404);
        return (user, team);
    }

    private sealed record InProject(User User, ProjectInfo Project, Team Plan, string Role, string Access);

    // A project the signed-in person is in, and as what. Plan: its owner's.
    private static async Task<InProject> ProjectAsync(HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects,
        Guid projectId, bool ownerOnly, CancellationToken ct, bool write = true)
    {
        var user = await SignedInAsync(ctx, accounts, ct);
        if (write) Limit(Writes, user.Id.ToString());
        var mine = (await teams.ProjectsForAsync(user.Id, ct)).FirstOrDefault(m => m.ProjectId == projectId) ?? throw No("No such project.", 404);
        if (ownerOnly && mine.Role != "owner") throw No("Only the project's owner can change this.", 403);
        var project = await projects.GetAsync(projectId, ct) ?? throw No("No such project.", 404);
        var plan = await teams.GetAsync(mine.TeamId, ct) ?? throw No("No such project.", 404);
        return new InProject(user, project, plan, mine.Role, mine.Access);
    }

    private static async Task OwnLinkAsync(HttpContext ctx, AccountStore accounts, TeamStore teams, IProjectStore projects, Guid linkId, CancellationToken ct)
    {
        var user = await SignedInAsync(ctx, accounts, ct);
        Limit(Writes, user.Id.ToString());
        foreach (var m in (await teams.ProjectsForAsync(user.Id, ct)).Where(m => m.Role == "owner"))
            if ((await projects.ListLinksAsync(m.ProjectId, ct)).Any(l => l.LinkId == linkId)) return;
        throw No("That link is not one of your projects'.", 404);
    }

    private static async Task EnsureNewNameAsync(TeamStore teams, IProjectStore projects, Guid teamId, string name, Guid? except, CancellationToken ct)
    {
        foreach (var id in await teams.ProjectIdsAsync(teamId, ct))
            if (id != except && await projects.GetAsync(id, ct) is { ArchivedAt: null } p && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                throw No($"You already have a project called {name}.", 409);
    }

    // Archived projects count: they keep their files. Deleting one makes room.
    private static async Task EnsureRoomForProjectAsync(TeamStore teams, Team team, CancellationToken ct)
    {
        if (team.MaxProjects is { } max && (await teams.ProjectIdsAsync(team.Id, ct)).Count >= max)
            throw No($"A beta tester can have {max} projects, and you have {max}. Archive and delete one to make another.", 409);
        if (await UsedBytesAsync(teams, team.Id, ct) >= team.StorageBytes)
            throw No("Your storage is full. Delete a project, or files in one, first.", 409);
    }

    // People in a project besides its owner: members and invitations still open.
    private static async Task<int> PeopleInAsync(TeamStore teams, Guid projectId, CancellationToken ct) =>
        (await teams.MembersAsync(projectId, ct)).Count(m => m.Role == "member") + (await teams.InvitesAsync(projectId, ct)).Count;

    private static async Task<int> MostPeopleAsync(TeamStore teams, IReadOnlyList<Guid> projectIds, CancellationToken ct)
    {
        var most = 0;
        foreach (var id in projectIds) most = Math.Max(most, await PeopleInAsync(teams, id, ct));
        return most;
    }

    private static string FullMessage(Team plan, string project) =>
        $"{project} has room for {plan.PeoplePerProject} people besides you, and they're all in or invited. Withdraw an invitation or remove someone first.";

    public static async Task<long> UsedBytesAsync(TeamStore teams, Guid teamId, CancellationToken ct) =>
        (await teams.StatsAsync(await teams.ProjectIdsAsync(teamId, ct), ct)).Sum(s => s.FileBytes);

    // Joins a project from an invitation and tells its owner.
    private static async Task JoinAsync(TeamStore teams, EditorTokens editors, IEmailSender mail, User user, Invitation invitation, string? email, CancellationToken ct)
    {
        var members = await teams.MembersAsync(invitation.ProjectId, ct);
        if (members.Any(m => m.UserId == user.Id && m.Role == "owner")) throw No("You own this project already.", 409);
        if (await teams.AcceptAsync(invitation.Id, user.Id, email, ct) is null) throw No("This invitation is no longer open.", 404);
        editors.Forget();
        if (members.FirstOrDefault(m => m.Role == "owner") is { } owner)
            await NotifyAsync(mail, owner.Email, $"{user.Name} joined {invitation.Project}",
                $"Hi,\n\n{user.Name} ({user.Email}) accepted your invitation and is now in {invitation.Project} on YHDE.", ct);
    }

    private static async Task DeclineAsync(TeamStore teams, IEmailSender mail, Invitation invitation, string who, CancellationToken ct)
    {
        await teams.CloseInviteAsync(invitation.Id, null, ct);
        if (await OwnerEmailAsync(teams, invitation.ProjectId, ct) is { } to)
            await NotifyAsync(mail, to, $"{who} declined your invitation to {invitation.Project}",
                $"Hi,\n\nThe invitation to {invitation.To} for {invitation.Project} on YHDE was declined. Its seat is free again.", ct);
    }

    private static async Task<string?> OwnerEmailAsync(TeamStore teams, Guid projectId, CancellationToken ct) =>
        (await teams.MembersAsync(projectId, ct)).FirstOrDefault(m => m.Role == "owner")?.Email;

    // Addresses on a .test domain (test accounts) never get email; what
    // concerns them waits on their dashboard.
    public static bool Mailable(string email) =>
        !email.EndsWith(".test", StringComparison.Ordinal) && !email.EndsWith("@test.yhde", StringComparison.Ordinal);

    public static async Task NotifyAsync(IEmailSender mail, string to, string subject, string body, CancellationToken ct)
    {
        if (!Mailable(to)) return;
        try
        {
            await mail.SendAsync(to, subject, body + "\n\nYHDE", ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A notice is a courtesy: what it tells about already happened.
        }
    }

    private static async Task SendInviteAsync(IEmailSender mail, string url, User from, string project, string email, string token, bool again, CancellationToken ct)
    {
        if (!Mailable(email)) return;
        await mail.SendAsync(email, $"{(again ? "Reminder: " : "")}{from.Name} invited you to {project} on YHDE",
            $"Hi,\n\n{from.Name} ({from.Email}) invited you to their Godot project {project} on YHDE, so you can build it together, live.\n\n" +
            $"See the invitation, and accept or decline it (the link works for 14 days{(again ? "; earlier links no longer work" : "")}):\n{url}/app#/invite/{token}\n\n" +
            $"The page is on {url}, and it shows who sent the invitation and from which address, so you can check it's really them. " +
            $"You'll sign in, or make a free account, before joining. The invitation also waits on your YHDE dashboard when you sign in as {email}.\n\n" +
            "If you didn't expect this, decline it on that page, or just ignore this email.", ct);
    }

    private static string CleanName(string? name, NameRules.Kind kind, string empty)
    {
        var clean = NameRules.Clean(name, kind);
        if (clean.Length == 0) throw No(empty);
        return NameRules.Problem(clean, kind) is { } problem ? throw No(problem) : clean;
    }

    private static string Period(string? period) => period == "year" ? "year" : "month";
}
