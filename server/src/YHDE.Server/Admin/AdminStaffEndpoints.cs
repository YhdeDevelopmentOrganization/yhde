using YHDE.Server.Accounts;
using YHDE.Server.Gateway;
using YHDE.Server.Projects;
using YHDE.Server.Teams;

namespace YHDE.Server.Admin;

// Staff sign-in and the admin page's people, teams, projects, audit log and
// promo codes (admin.md). The sign-in routes are open; the rest is mapped
// inside the signed-in group of AdminEndpoints.
//
// Support may look at everything and help people (sign them out everywhere,
// send a new confirmation email); every other change needs an admin.
public static class AdminStaffEndpoints
{
    public sealed record AccountLogin(string? Email, string? Password);
    public sealed record RoleRequest(string? Role);
    public sealed record DisableRequest(bool Disabled);
    public sealed record ConfirmRequest(string? Email);
    public sealed record TeamChange(string? Name, string? Plan, string? Period, int? ExtraSeats, int? ExtraStorage, int? FreeSeats);
    public sealed record MoveRequest(Guid? TeamId);
    public sealed record TestAccountRequest(string? Name, Guid? ProjectId);
    public sealed record AddPersonRequest(string? Email, string? Access);
    public sealed record PromoRequest(
        string? Code, string? Note, bool Active, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string[]? Plans,
        bool NewTeamsOnly, string? Kind, decimal Amount, int? Months, int? MaxRedemptions, bool UnlocksTeams = false);

    // Changes Support may make: helping someone, never changing settings.
    public static bool SupportMay(HttpRequest request)
    {
        var p = request.Path.Value ?? "";
        return p.EndsWith("/logout", StringComparison.Ordinal)
            || (p.StartsWith("/admin/api/users/", StringComparison.Ordinal)
                && (p.EndsWith("/sign-out", StringComparison.Ordinal) || p.EndsWith("/resend-confirmation", StringComparison.Ordinal)));
    }

    public static StaffSession Staff(HttpContext ctx) => (StaffSession)ctx.Items[nameof(StaffSession)]!;

    private static void SetCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(AdminAuth.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/admin",
            MaxAge = AdminAuth.SessionLifetime,
        });

    // Signing in

    public static void MapStaffLogin(this RouteGroupBuilder api)
    {
        // Staff sign-in changes need the admin header too (CSRF).
        static IResult? NoHeader(HttpContext ctx) =>
            ctx.Request.Headers[AdminAuth.HeaderName] != "1" ? Results.Problem("Missing admin header.", statusCode: 403) : null;

        // Who is signed in on the website here, so the page can offer "Continue as …".
        api.MapGet("/login/site", async (HttpContext ctx, AccountStore accounts, StaffStore staff, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (await AuthEndpoints.CurrentAsync(ctx, accounts, ct) is not { } s) return Results.Ok(new { signedIn = false });
            var role = await staff.RoleAsync(s.User.Id, ct);
            return Results.Ok(new { signedIn = true, name = s.User.Name, email = s.User.Email, staff = role is not null });
        });

        api.MapPost("/login/site", async (HttpContext ctx, AccountStore accounts, StaffStore staff, AdminAuth auth, CancellationToken ct) =>
        {
            if (NoHeader(ctx) is { } refused) return refused;
            if (await AuthEndpoints.CurrentAsync(ctx, accounts, ct) is not { } s) return Results.Problem("Sign in on the website first.", statusCode: 401);
            return await StartAsync(ctx, s.User, staff, auth, ct);
        });

        api.MapPost("/login/account", async (AccountLogin r, HttpContext ctx, AccountService service, StaffStore staff, AdminAuth auth, CancellationToken ct) =>
        {
            if (NoHeader(ctx) is { } refused) return refused;
            User user;
            try
            {
                user = await service.LoginAsync(r.Email, r.Password, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct);
            }
            catch (AccountService.Refused e)
            {
                return Results.Problem(e.Message, statusCode: e.Status);
            }
            return await StartAsync(ctx, user, staff, auth, ct);
        });
    }

    private static async Task<IResult> StartAsync(HttpContext ctx, User user, StaffStore staff, AdminAuth auth, CancellationToken ct)
    {
        var role = await staff.RoleAsync(user.Id, ct);
        if (role is null)
        {
            await staff.WriteAsync("admin.refused", user.Id, new { reason = "not staff" }, ct: ct);
            return Results.Problem("This account doesn't have access to the admin page.", statusCode: 403);
        }
        if (!user.EmailVerified) return Results.Problem("Confirm this account's email address first.", statusCode: 403);
        SetCookie(ctx, auth.Start(user.Id, user.Name, user.Email, role));
        await staff.WriteAsync("admin.signed_in", user.Id, new { role, ip = ctx.Connection.RemoteIpAddress?.ToString() }, ct: ct);
        return Results.Ok(new { ok = true });
    }

    public static void SetPasswordCookie(HttpContext ctx, string token) => SetCookie(ctx, token);

    // The signed-in part

    public static void MapStaffAdmin(this RouteGroupBuilder secured)
    {
        secured.MapGet("/me", (HttpContext ctx) =>
        {
            var s = Staff(ctx);
            return Results.Ok(new { s.Name, s.Email, s.Role, account = s.UserId is not null });
        });

        // People
        secured.MapGet("/users", async (string? q, StaffStore staff, CancellationToken ct) =>
            Results.Ok(await staff.UsersAsync(q, 300, ct)));

        secured.MapPost("/users/{id:guid}/role", async (Guid id, RoleRequest r, HttpContext ctx, StaffStore staff, AdminAuth auth, CancellationToken ct) =>
        {
            var role = r.Role is "admin" or "support" ? r.Role : null;
            var me = Staff(ctx);
            if (me.UserId == id && role != "admin" && await staff.AdminCountAsync(ct) <= 1)
                return Results.Problem("You're the only admin account. Make someone else an admin first.", statusCode: 409);
            if (!await staff.SetRoleAsync(id, role, ct)) return Results.NotFound();
            auth.EndFor(id);
            await staff.WriteAsync("admin.role_changed", me.UserId, new { role, by = me.Name }, targetId: id, ct: ct);
            return Results.Ok(new { ok = true });
        });

        secured.MapPost("/users/{id:guid}/disable", async (Guid id, DisableRequest r, HttpContext ctx, StaffStore staff, AdminAuth auth, CancellationToken ct) =>
        {
            var me = Staff(ctx);
            if (me.UserId == id) return Results.Problem("You can't disable your own account.", statusCode: 409);
            if (!await staff.SetDisabledAsync(id, r.Disabled, ct)) return Results.NotFound();
            auth.EndFor(id);
            await staff.WriteAsync(r.Disabled ? "admin.user_disabled" : "admin.user_enabled", me.UserId, new { by = me.Name }, targetId: id, ct: ct);
            return Results.Ok(new { ok = true });
        });

        secured.MapPost("/users/{id:guid}/sign-out", async (Guid id, HttpContext ctx, StaffStore staff, CancellationToken ct) =>
        {
            var me = Staff(ctx);
            var ended = await staff.SignOutEverywhereAsync(id, ct);
            await staff.WriteAsync("admin.user_signed_out", me.UserId, new { sessions = ended, by = me.Name }, targetId: id, ct: ct);
            return Results.Ok(new { ok = true, ended });
        });

        secured.MapPost("/users/{id:guid}/resend-confirmation", async (Guid id, HttpContext ctx, AccountStore accounts, AccountService service,
            AccountOptions options, StaffStore staff, CancellationToken ct) =>
        {
            var user = await accounts.ByIdAsync(id, ct);
            if (user is null) return Results.NotFound();
            if (user.EmailVerified) return Results.Problem("This email address is already confirmed.", statusCode: 409);
            await service.SendVerificationAsync(user, options.BaseUrl(ctx.Request), ct);
            await staff.WriteAsync("admin.confirmation_sent", Staff(ctx).UserId, new { by = Staff(ctx).Name }, targetId: id, ct: ct);
            return Results.Ok(new { ok = true });
        });

        secured.MapPost("/users/{id:guid}/delete", async (Guid id, ConfirmRequest r, HttpContext ctx, AccountStore accounts, TeamStore teams,
            StaffStore staff, AdminAuth auth, CancellationToken ct) =>
        {
            var me = Staff(ctx);
            var user = await accounts.ByIdAsync(id, ct);
            if (user is null) return Results.NotFound();
            if (me.UserId == id) return Results.Problem("Delete your own account on the website's Account page.", statusCode: 409);
            if (!string.Equals(AccountStore.NormalizeEmail(r.Email ?? ""), user.Email, StringComparison.Ordinal))
                return Results.Problem("Type the account's email address exactly to delete it.", statusCode: 400);
            if (await teams.OwnedAsync(id, ct) is { } owned && (await teams.ProjectIdsAsync(owned.Id, ct)).Count > 0)
                return Results.Problem("They still own projects. Delete or move those projects first.", statusCode: 409);
            await teams.DeleteUserAsync(id, ct);
            auth.EndFor(id);
            await staff.WriteAsync("admin.user_deleted", me.UserId, new { email = user.Email, by = me.Name }, targetId: id, ct: ct);
            return Results.Ok(new { ok = true });
        });

        // Test accounts: real, confirmed accounts with made-up addresses on the
        // reserved .test domain (no email ever goes there), marked is_test.
        // The password is shown once, here.
        secured.MapPost("/test-accounts", async (TestAccountRequest r, HttpContext ctx, AccountStore accounts, StaffStore staff, TeamStore teams, CancellationToken ct) =>
        {
            var name = NameRules.Clean(r.Name, NameRules.Kind.Person);
            if (name.Length == 0) name = "Test person";
            var email = $"test-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}@test.yhde";
            var password = Secrets.NewToken()[..20];
            var user = await accounts.CreateAsync(email, name, await Passwords.HashAsync(password), verified: true, ct)
                ?? throw new InvalidOperationException("A test account's address was taken; try again.");
            await staff.MarkTestAsync(user.Id, ct);
            string? project = null;
            if (r.ProjectId is { } projectId)
            {
                var p = await ctx.RequestServices.GetRequiredService<IProjectStore>().GetAsync(projectId, ct);
                if (p is null) return Results.Problem("No such project.", statusCode: 404);
                await teams.AddMemberAsync(projectId, user.Id, "edit", ct);
                project = p.Name;
                ctx.RequestServices.GetRequiredService<EditorTokens>().Forget();
            }
            var me = Staff(ctx);
            await staff.WriteAsync("admin.test_account_created", me.UserId, new { email, project, by = me.Name }, targetId: user.Id, ct: ct);
            return Results.Ok(new { id = user.Id, email, password, name, project });
        });

        secured.MapPost("/test-accounts/delete-all", async (HttpContext ctx, StaffStore staff, TeamStore teams, AdminAuth auth, CancellationToken ct) =>
        {
            var ids = await staff.RemovableTestAccountsAsync(ct);
            foreach (var id in ids)
            {
                await teams.DeleteUserAsync(id, ct);
                auth.EndFor(id);
            }
            var me = Staff(ctx);
            await staff.WriteAsync("admin.test_accounts_deleted", me.UserId, new { count = ids.Count, by = me.Name }, ct: ct);
            return Results.Ok(new { deleted = ids.Count });
        });

        // Teams
        secured.MapGet("/teams", async (string? q, StaffStore staff, CancellationToken ct) => Results.Ok(await staff.TeamsAsync(q, ct)));

        secured.MapGet("/teams/{id:guid}", async (Guid id, HttpContext ctx, TeamStore teams, IProjectStore projects, PromoStore promos, CancellationToken ct) =>
        {
            var team = await teams.GetAsync(id, ct);
            if (team is null) return Results.NotFound();
            var ids = await teams.ProjectIdsAsync(id, ct);
            var stats = (await teams.StatsAsync(ids, ct)).ToDictionary(s => s.ProjectId);
            var members = await teams.MembersOfAsync(ids, ct);
            var invites = await teams.InvitesOfAsync(ids, ct);
            var viewers = await teams.ViewersAsync(ids, ct);
            var list = new List<object>();
            foreach (var pid in ids)
                if (await projects.GetAsync(pid, ct) is { } p)
                    list.Add(new
                    {
                        id = pid,
                        name = p.Name,
                        archived = p.ArchivedAt is not null,
                        files = stats.GetValueOrDefault(pid)?.Files ?? 0,
                        bytes = stats.GetValueOrDefault(pid)?.FileBytes ?? 0,
                        members = members[pid].Where(m => m.Role == "member").Select(m => new { userId = m.UserId, name = m.Name, email = m.Email, access = m.Access, joined = m.Joined, lastSeen = m.LastSeen }),
                        invites = invites[pid].Select(i => new { id = i.Id, email = i.Email, sent = i.Sent }),
                        viewers = viewers.GetValueOrDefault(pid),
                    });
            var owner = await ctx.RequestServices.GetRequiredService<AccountStore>().ByIdAsync(team.OwnerId, ct);
            return Results.Ok(new
            {
                team,
                owner = owner is null ? null : new { name = owner.Name, email = owner.Email },
                peoplePerProject = team.PeoplePerProject,
                maxProjects = team.MaxProjects,
                viewersPerProject = Plans.ViewersPerProject,
                storageBytes = team.StorageBytes,
                usedBytes = stats.Values.Sum(s => s.FileBytes),
                projects = list,
                promos = await promos.ForTeamAsync(id, ct),
            });
        });

        secured.MapPost("/teams/{id:guid}", async (Guid id, TeamChange r, HttpContext ctx, TeamStore teams, StaffStore staff, CancellationToken ct) =>
        {
            var team = await teams.GetAsync(id, ct);
            if (team is null) return Results.NotFound();
            var plan = r.Plan is null ? team.PlanInfo : Plans.Find(r.Plan);
            if (plan is null) return Results.Problem("No such plan.", statusCode: 400);
            var name = r.Name is null ? team.Name : NameRules.Clean(r.Name, NameRules.Kind.Team);
            if (name.Length == 0) return Results.Problem("The team needs a name.", statusCode: 400);
            var extraSeats = Math.Clamp(r.ExtraSeats ?? team.ExtraSeats, 0, plan.MaxExtraSeats);
            var extraStorage = Math.Clamp(r.ExtraStorage ?? team.ExtraStorage, 0, Plans.MaxExtraStorage);
            // Free seats: given by the operator, never charged for.
            var freeSeats = Math.Clamp(r.FreeSeats ?? team.FreeSeats, 0, 100);
            var fits = (team with { Plan = plan.Id, ExtraSeats = extraSeats, FreeSeats = freeSeats }).PeoplePerProject;
            foreach (var pid in await teams.ProjectIdsAsync(id, ct))
            {
                var people = (await teams.MembersAsync(pid, ct)).Count(m => m.Role == "member") + (await teams.InvitesAsync(pid, ct)).Count;
                if (people > fits)
                    return Results.Problem($"A project has {people} people and invitations besides its owner; this would leave room for {fits}. Remove people first.", statusCode: 409);
            }
            var period = r.Period is "year" or "month" ? r.Period : team.Period;
            await teams.UpdateAsync(id, name, plan.Id, period, extraSeats, extraStorage, ct);
            if (freeSeats != team.FreeSeats) await teams.SetFreeSeatsAsync(id, freeSeats, ct);
            var me = Staff(ctx);
            await staff.WriteAsync("admin.team_changed", me.UserId, new { name, plan = plan.Id, period, extraSeats, extraStorage, freeSeats, by = me.Name }, targetId: id, ct: ct);
            return Results.Ok(new { ok = true });
        });

        // People in a project: staff add an existing account straight in (no
        // invitation to accept; they get an email saying so) or take one out.
        secured.MapPost("/projects/{id:guid}/members", async (Guid id, AddPersonRequest r, HttpContext ctx, TeamStore teams, IProjectStore projects,
            AccountStore accounts, StaffStore staff, IEmailSender mail, AccountOptions options, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(id, ct);
            if (project is null) return Results.NotFound();
            var person = await accounts.ByEmailAsync(r.Email ?? "", ct);
            if (person is null) return Results.Problem("No account has that email address.", statusCode: 404);
            var user = person.Value.User;
            if ((await teams.MembersAsync(id, ct)).Any(m => m.UserId == user.Id)) return Results.Problem($"{user.Name} is already in {project.Name}.", statusCode: 409);
            await teams.AddMemberAsync(id, user.Id, r.Access == "view" ? "view" : "edit", ct);
            ctx.RequestServices.GetRequiredService<EditorTokens>().Forget();
            var me = Staff(ctx);
            await staff.WriteAsync("admin.member_added", me.UserId, new { email = user.Email, by = me.Name }, projectId: id, targetId: user.Id, ct: ct);
            await TeamEndpoints.NotifyAsync(mail, user.Email, $"You were added to {project.Name} on YHDE",
                $"Hi,\n\nYou're now in the Godot project {project.Name} on YHDE. Sign in from the YHDE panel in Godot and pick it, or see it on your dashboard:\n{options.BaseUrl(ctx.Request)}/app#/projects/{id}", ct);
            return Results.Ok(new { ok = true });
        });

        secured.MapPost("/projects/{id:guid}/members/{userId:guid}/remove", async (Guid id, Guid userId, HttpContext ctx, TeamStore teams, StaffStore staff, CancellationToken ct) =>
        {
            if (!await teams.RemoveMemberAsync(id, userId, ct)) return Results.Problem("That person isn't in the project (its owner can't be removed).", statusCode: 409);
            ctx.RequestServices.GetRequiredService<EditorTokens>().Forget();
            await EditorTokens.DisconnectAsync(ctx.RequestServices.GetRequiredService<ISessionManager>(), userId);
            var me = Staff(ctx);
            await staff.WriteAsync("admin.member_removed", me.UserId, new { by = me.Name }, projectId: id, targetId: userId, ct: ct);
            return Results.Ok(new { ok = true });
        });

        // Projects: which team owns each one, and moving them.
        secured.MapGet("/project-teams", async (StaffStore staff, CancellationToken ct) =>
            Results.Ok((await staff.ProjectTeamsAsync(ct)).ToDictionary(kv => kv.Key.ToString(), kv => new { id = kv.Value.Id, name = kv.Value.Name })));

        secured.MapPost("/projects/{id:guid}/team", async (Guid id, MoveRequest r, HttpContext ctx, TeamStore teams, StaffStore staff,
            IProjectStore projects, EditorTokens editors, CancellationToken ct) =>
        {
            if (await projects.GetAsync(id, ct) is null) return Results.NotFound();
            if (r.TeamId is { } t && await teams.GetAsync(t, ct) is null) return Results.Problem("No such team.", statusCode: 404);
            await staff.MoveProjectAsync(id, r.TeamId, ct);
            editors.Forget();
            var me = Staff(ctx);
            await staff.WriteAsync("admin.project_moved", me.UserId, new { team = r.TeamId, by = me.Name }, projectId: id, ct: ct);
            return Results.Ok(new { ok = true });
        });

        // Audit log
        secured.MapGet("/audit", async (string? type, long? before, StaffStore staff, CancellationToken ct) =>
            Results.Ok(await staff.AuditAsync(type, before, 200, ct)));

        // Promo codes
        secured.MapGet("/promos", async (PromoStore promos, CancellationToken ct) =>
            Results.Ok((await promos.ListAsync(ct)).Select(p => new { p.Id, p.Code, p.Note, p.Active, p.StartsAt, p.EndsAt, p.Plans, p.NewTeamsOnly, p.Kind, p.Amount, p.Months, p.MaxRedemptions, p.Created, p.Redemptions, p.UnlocksTeams, text = PromoStore.Describe(p.Kind, p.Amount, p.Months, p.Plans) })));

        secured.MapPost("/promos", (PromoRequest r, HttpContext ctx, PromoStore promos, StaffStore staff, CancellationToken ct) =>
            SavePromoAsync(null, r, ctx, promos, staff, ct));
        secured.MapPost("/promos/{id:guid}", (Guid id, PromoRequest r, HttpContext ctx, PromoStore promos, StaffStore staff, CancellationToken ct) =>
            SavePromoAsync(id, r, ctx, promos, staff, ct));

        secured.MapPost("/promos/{id:guid}/delete", async (Guid id, HttpContext ctx, PromoStore promos, StaffStore staff, CancellationToken ct) =>
        {
            if (!await promos.DeleteUnusedAsync(id, ct))
                return Results.Problem("Teams have used this code, so it can't be deleted. Switch it off instead.", statusCode: 409);
            var me = Staff(ctx);
            await staff.WriteAsync("admin.promo_deleted", me.UserId, new { by = me.Name }, targetId: id, ct: ct);
            return Results.Ok(new { ok = true });
        });
    }

    private static async Task<IResult> SavePromoAsync(Guid? id, PromoRequest r, HttpContext ctx, PromoStore promos, StaffStore staff, CancellationToken ct)
    {
        var code = PromoStore.Normalize(r.Code);
        if (!PromoStore.LooksLikeCode(code)) return Results.Problem("A code is 3 to 40 letters, digits, - or _.", statusCode: 400);
        if (r.Kind is not { } kind || !PromoStore.Kinds.Contains(kind)) return Results.Problem("Pick what the code gives.", statusCode: 400);
        if (kind == "nothing" && !r.UnlocksTeams) return Results.Problem("A code that gives nothing else must unlock teams.", statusCode: 400);
        var needsAmount = kind is not ("free_months" or "nothing");
        if (needsAmount && r.Amount <= 0) return Results.Problem("Enter how much the code gives.", statusCode: 400);
        if (kind == "percent_off" && r.Amount > 100) return Results.Problem("A discount can be at most 100 %.", statusCode: 400);
        if (kind is "extra_seats" or "extra_storage" && r.Amount != decimal.Truncate(r.Amount)) return Results.Problem("Use a whole number.", statusCode: 400);
        if (kind == "free_months" && r.Months is not > 0) return Results.Problem("Enter how many months are free.", statusCode: 400);
        var months = kind == "nothing" ? null : r.Months;
        if (months is <= 0 or > 120) return Results.Problem("The benefit lasts 1 to 120 months (or leave it empty for as long as they're subscribed).", statusCode: 400);
        if (r.MaxRedemptions is <= 0) return Results.Problem("Leave the limit empty, or make it at least 1.", statusCode: 400);
        if (r.StartsAt is { } s && r.EndsAt is { } e && e <= s) return Results.Problem("The end date must be after the start date.", statusCode: 400);
        var plans = r.Plans?.Select(p => Plans.Find(p)?.Id).OfType<string>().Distinct().ToArray();
        var promo = new PromoCode(Guid.Empty, code, (r.Note ?? "").Trim(), r.Active, r.StartsAt, r.EndsAt, plans, r.NewTeamsOnly, kind,
            needsAmount ? r.Amount : 0, months, r.MaxRedemptions, DateTimeOffset.UtcNow, 0, r.UnlocksTeams);
        var me = Staff(ctx);
        var (ok, saved) = await promos.SaveAsync(id, promo, me.UserId, ct);
        if (!ok) return Results.Problem("Another code already uses that text.", statusCode: 409);
        await staff.WriteAsync(id is null ? "admin.promo_created" : "admin.promo_changed", me.UserId,
            new { code, kind, amount = promo.Amount, promo.Months, plans, promo.Active, unlocksTeams = r.UnlocksTeams, by = me.Name }, targetId: saved, ct: ct);
        return Results.Ok(new { id = saved });
    }
}
