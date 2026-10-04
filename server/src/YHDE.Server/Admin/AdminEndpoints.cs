using YHDE.Server.Addon;
using YHDE.Server.Assets;
using YHDE.Server.Gateway;
using YHDE.Server.Presence;
using YHDE.Server.Projects;

namespace YHDE.Server.Admin;

// The server admin page (admin.md): a single web page at /admin and the JSON
// API it uses. Projects, invite codes, who is online, storage and backups.
public static class AdminEndpoints
{
    public sealed record LoginRequest(string? Password);
    public sealed record NewProjectRequest(string? Name);
    public sealed record ProjectChange(string? Name, bool? Archived);
    public sealed record NewInviteRequest(string? Label);
    public sealed record UpdateRequest(string? Commit);

    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private static (DateTimeOffset At, long Blobs, long Bytes) _usage;

    public static void MapAdminEndpoints(this WebApplication app)
    {
        // The page itself and its script: static, no secrets in them.
        app.MapGet("/admin", () => UiPages.Page("admin.html"));

        var api = app.MapGroup("/admin/api");
        // The server password: the way in before any account is staff.
        api.MapPost("/login", async (LoginRequest request, HttpContext ctx, AdminAuth auth, StaffStore staff) =>
        {
            if (ctx.Request.Headers[AdminAuth.HeaderName] != "1") return Results.Problem("Missing admin header.", statusCode: 403);
            var (result, token) = auth.Login(request.Password, ctx.Connection.RemoteIpAddress);
            switch (result)
            {
                case AdminAuth.LoginResult.Disabled:
                    return Results.Problem("The server password is off. Sign in with a staff account, or set ADMIN_PASSWORD in the server kit (deploy/.env) and restart.", statusCode: 404);
                case AdminAuth.LoginResult.LockedOut:
                    return Results.Problem("Too many wrong passwords. Try again in 15 minutes.", statusCode: 429);
                case AdminAuth.LoginResult.Wrong:
                    return Results.Problem("Wrong password.", statusCode: 401);
            }
            AdminStaffEndpoints.SetPasswordCookie(ctx, token!);
            await staff.WriteAsync("admin.signed_in", null, new { with = "server password", ip = ctx.Connection.RemoteIpAddress?.ToString() });
            return Results.Ok(new { ok = true });
        });
        api.MapStaffLogin();

        var secured = api.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext;
            var auth = ctx.RequestServices.GetRequiredService<AdminAuth>();
            if (auth.Get(ctx.Request.Cookies[AdminAuth.CookieName]) is not { } session)
                return Results.Problem("Log in first.", statusCode: 401);
            if (!HttpMethods.IsGet(ctx.Request.Method))
            {
                if (ctx.Request.Headers[AdminAuth.HeaderName] != "1")
                    return Results.Problem("Missing admin header.", statusCode: 403);
                if (!session.IsAdmin && !AdminStaffEndpoints.SupportMay(ctx.Request))
                    return Results.Problem("Only admins can change this. Support can look, and help people sign in.", statusCode: 403);
            }
            ctx.Items[nameof(StaffSession)] = session;
            ctx.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        secured.MapStaffAdmin();

        secured.MapPost("/logout", (HttpContext ctx, AdminAuth auth) =>
        {
            auth.Logout(ctx.Request.Cookies[AdminAuth.CookieName]);
            ctx.Response.Cookies.Delete(AdminAuth.CookieName, new CookieOptions { Path = "/admin" });
            return Results.Ok(new { ok = true });
        });
        secured.MapAddonAdmin();
        YHDE.Server.Site.SiteEndpoints.MapSiteAdmin(secured);
        secured.MapProjectAdmin();
        secured.MapGet("/server-update", (ServerUpdates updates) => Results.Ok(updates.Get()));
        secured.MapPost("/server-update", (UpdateRequest request, ServerUpdates updates) =>
            updates.Request(request.Commit) is { } error ? Results.Problem(error, statusCode: 400) : Results.Ok(new { ok = true }));
        secured.MapPost("/server-update/check", (ServerUpdates updates) =>
            updates.RequestCheck() is { } error ? Results.Problem(error, statusCode: 400) : Results.Ok(new { ok = true }));

        secured.MapGet("/overview", async (IProjectStore projects, ISessionManager sessions, PresenceService presence,
            BlobStore blobs, AddonStore addon, YHDE.Server.Teams.TeamStore teams, IConfiguration config, CancellationToken ct) =>
        {
            var list = await projects.ListAsync(ct);
            var invites = new Dictionary<Guid, IReadOnlyList<InviteInfo>>();
            var links = new Dictionary<Guid, IReadOnlyList<LinkInfo>>();
            var joinCodes = new Dictionary<Guid, string?>();
            foreach (var p in list)
            {
                invites[p.ProjectId] = await projects.ListInvitesAsync(p.ProjectId, ct);
                links[p.ProjectId] = await projects.ListLinksAsync(p.ProjectId, ct);
                joinCodes[p.ProjectId] = await teams.JoinCodeAsync(p.ProjectId, ct);
            }
            var stats = (await projects.StatsAsync(ct)).ToDictionary(s => s.ProjectId);
            var names = list.ToDictionary(p => p.ProjectId, p => p.Name);
            var online = sessions.All.Where(s => s.IsSubscribed).Select(s =>
            {
                var p = presence.Get(s.SessionId);
                return new
                {
                    name = s.MemberName,
                    project = s.SubscribedProjectId is { } pid && names.TryGetValue(pid, out var n) ? n : "",
                    projectId = s.SubscribedProjectId,
                    scene = p?.Scene ?? "",
                    tool = p?.Tool ?? "",
                    version = s.ClientVersion,
                    via = s.Grant.UserId is not null ? "account" : s.Grant.ProjectId is null ? "server key" : "invite code",
                };
            }).ToList();
            var now = DateTimeOffset.UtcNow;
            if (now - _usage.At > TimeSpan.FromMinutes(1))
            {
                var (count, bytes) = blobs.Usage();
                _usage = (now, count, bytes);
            }
            return Results.Ok(new
            {
                version = ServerInfo.Version,
                started = Started,
                storage = new { files = _usage.Blobs, bytes = _usage.Bytes, maxFileBytes = blobs.MaxBlobBytes },
                projects = list.Select(p => new
                {
                    id = p.ProjectId,
                    name = p.Name,
                    created = p.CreatedAt,
                    archived = p.ArchivedAt,
                    branch = p.MainBranchId,
                    operations = stats.TryGetValue(p.ProjectId, out var st) ? st.Operations : 0,
                    files = st?.Files ?? 0,
                    fileBytes = st?.FileBytes ?? 0,
                    lastActivity = st?.LastActivity,
                    online = online.Count(o => o.projectId == p.ProjectId),
                    joinCode = joinCodes[p.ProjectId],
                    invites = invites[p.ProjectId].Select(i => new
                    {
                        id = i.InviteId, label = i.Label, created = i.CreatedAt, revoked = i.RevokedAt, fromLink = i.LinkId,
                    }),
                    links = links[p.ProjectId].Select(l => new
                    {
                        id = l.LinkId, label = l.Label, created = l.CreatedAt, expires = l.ExpiresAt,
                        maxUses = l.MaxUses, uses = l.Uses, revoked = l.RevokedAt,
                    }),
                }),
                online,
                addon = addon.Current,
                addonPending = addon.Pending,
                backups = Backups(config),
            });
        });

        secured.MapPost("/projects", async (NewProjectRequest request, IProjectStore projects, CancellationToken ct) =>
        {
            var name = CleanName(request.Name);
            if (name.Length == 0) return Results.Problem("Give the project a name.", statusCode: 400);
            var project = await projects.CreateAsync(name, ct);
            return Results.Ok(new { id = project.ProjectId, name = project.Name });
        });

        secured.MapPost("/projects/{id:guid}", async (Guid id, ProjectChange change, IProjectStore projects, AccessGate gate, CancellationToken ct) =>
        {
            if (await projects.GetAsync(id, ct) is null) return Results.NotFound();
            if (change.Name is not null)
            {
                var name = CleanName(change.Name);
                if (name.Length == 0) return Results.Problem("Give the project a name.", statusCode: 400);
                await projects.RenameAsync(id, name, ct);
            }
            if (change.Archived is { } archived)
            {
                await projects.SetArchivedAsync(id, archived, ct);
                gate.ForgetInvites(); // archived projects' codes stop working now
            }
            return Results.Ok(new { ok = true });
        });

        secured.MapGet("/projects/{id:guid}/invites", async (Guid id, IProjectStore projects, CancellationToken ct) =>
            Results.Ok((await projects.ListInvitesAsync(id, ct)).Select(i => new
            {
                id = i.InviteId,
                label = i.Label,
                created = i.CreatedAt,
                revoked = i.RevokedAt,
            })));

        secured.MapPost("/projects/{id:guid}/invites", async (Guid id, NewInviteRequest request, IProjectStore projects, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(id, ct);
            if (project is null) return Results.NotFound();
            if (project.ArchivedAt is not null) return Results.Problem("The project is archived.", statusCode: 409);
            var (invite, code) = await projects.CreateInviteAsync(id, CleanName(request.Label), ct);
            // The only time the code exists outside the person who receives it.
            return Results.Ok(new { id = invite.InviteId, label = invite.Label, code });
        });

        secured.MapPost("/invites/{id:guid}/revoke", async (Guid id, IProjectStore projects, AccessGate gate, CancellationToken ct) =>
        {
            await projects.RevokeInviteAsync(id, ct);
            gate.ForgetInvites();
            return Results.Ok(new { ok = true });
        });
    }

    private static string CleanName(string? value) => PresenceService.SanitizeText(value, 80);

    private static object Backups(IConfiguration config)
    {
        var dir = config["Yhde:BackupDir"];
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return new { configured = false, files = Array.Empty<object>() };
        try
        {
            var files = new DirectoryInfo(dir).EnumerateFiles("yhde-*.dump")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(10)
                .Select(f => (object)new { name = f.Name, bytes = f.Length, at = new DateTimeOffset(f.LastWriteTimeUtc) })
                .ToList();
            return new { configured = true, files };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Not readable by the server (e.g. a folder made before the kit
            // allowed it): the rest of the page still works.
            return new { configured = false, files = Array.Empty<object>() };
        }
    }
}
