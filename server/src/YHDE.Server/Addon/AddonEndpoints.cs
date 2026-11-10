using System.Collections.Concurrent;
using System.Net;
using YHDE.Server.Admin;
using YHDE.Server.Projects;

namespace YHDE.Server.Addon;

// Onboarding (onboarding.md):
// - GET  /join                  the join page (public; nothing on it is secret)
// - POST /join                  form with an optional invite code: a starter
//                               project for that code, or the plain add-on
// - GET  /addon/manifest.json   the current add-on's version (editors check it)
// - GET  /addon/yhde-addon.zip  the add-on itself
// - POST /admin/api/addon       the operator uploads a new add-on (admin only)
public static class AddonEndpoints
{
    // Invite codes cannot be guessed (80 bits), but the join form still limits
    // tries per address so nobody can hammer the database with it.
    private static readonly YHDE.Server.Accounts.Limiter Tries = new(20, TimeSpan.FromMinutes(10));

    public static void MapAddonEndpoints(this WebApplication app)
    {
        app.MapGet("/join", (AddonStore addon, string? code) => Page(addon, null, HttpStatusCode.OK, code: code));

        app.MapPost("/join", async (HttpContext ctx, AddonStore addon, IProjectStore projects, YHDE.Server.Teams.TeamStore teams, CancellationToken ct) =>
        {
            if (addon.Current is null) return Page(addon, null, HttpStatusCode.OK);
            var form = await ctx.Request.ReadFormAsync(ct);
            var raw = form["code"].ToString().Trim();
            if (raw.Length == 0) return Download(addon);

            if (!AllowTry(ctx.Connection.RemoteIpAddress))
                return Page(addon, "Too many tries. Wait a few minutes.", HttpStatusCode.TooManyRequests);
            // A project's join code: joining needs an account, so it goes on to
            // the dashboard, which asks to sign in first when needed.
            if (YHDE.Server.Teams.JoinCode.Normalize(raw) is { } join && await teams.ProjectForJoinCodeAsync(join, ct) is not null)
                return Results.Redirect("/app#/join/" + join);
            var code = InviteCode.Normalize(raw);
            var projectId = InviteCode.LooksLikeCode(code) ? await projects.ProjectForCodeAsync(code, ct) : null;
            var project = projectId is { } id ? await projects.GetAsync(id, ct) : null;
            if (project is null)
                return Page(addon, "That code doesn't work. Check it, or ask for a new one.", HttpStatusCode.OK, code: raw.Length <= 64 ? raw : null);

            return Starter(ctx, addon, project, new JoinFile(Code: code, Project: project.Name));
        }).DisableAntiforgery();

        // Invite links (/join/<token>). Opening the link says who invited
        // whom to what, with one button for the download. Nothing downloads
        // by itself, and only the download uses the link, so link previews in
        // chat apps never use one up.
        app.MapGet("/join/{token}", async (string token, HttpContext ctx, AddonStore addon, IProjectStore projects,
            YHDE.Server.Teams.TeamStore teams, CancellationToken ct) =>
        {
            if (addon.Current is null) return Page(addon, null, HttpStatusCode.ServiceUnavailable);
            if (!LinkToken.LooksLikeToken(token) || !AllowTry(ctx.Connection.RemoteIpAddress))
                return Page(addon, null, HttpStatusCode.NotFound, dead: new DeadInvite(null));
            var link = await projects.PeekLinkAsync(token, ct);
            var project = link is null ? null : await projects.GetAsync(link.ProjectId, ct);
            if (link is null || project is null)
            {
                // A link that worked once: name who to ask for a new one.
                var was = await projects.ProjectOfLinkAsync(token, ct);
                var owner = was is { } id ? await OwnerNameAsync(teams, id, ct) : null;
                return Page(addon, null, HttpStatusCode.NotFound, dead: new DeadInvite(owner));
            }
            var normal = LinkToken.Normalize(token);
            var from = await OwnerNameAsync(teams, project.ProjectId, ct);
            var image = await teams.ImageStampsAsync([project.ProjectId], ct);
            return Page(addon, null, HttpStatusCode.OK, new Invite(project.Name, from, link.Access,
                $"/join/{normal}/download", AddonStore.FolderName(project.Name) + ".zip",
                image.TryGetValue(project.ProjectId, out var at) ? $"/join/{normal}/image?v={at.ToUnixTimeMilliseconds()}" : null));
        });

        // The game's image on the invite page, for whoever has a working link.
        app.MapGet("/join/{token}/image", async (string token, HttpContext ctx, IProjectStore projects, YHDE.Server.Teams.TeamStore teams, CancellationToken ct) =>
        {
            if (!LinkToken.LooksLikeToken(token) || !AllowTry(ctx.Connection.RemoteIpAddress)) return Results.NotFound();
            if (await projects.PeekLinkAsync(token, ct) is not { } link || await teams.ImageAsync(link.ProjectId, ct) is not { } image)
                return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=604800, immutable";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(image.Data, image.ContentType);
        });

        app.MapGet("/join/{token}/download", async (string token, HttpContext ctx, AddonStore addon, IProjectStore projects,
            YHDE.Server.Teams.TeamStore teams, YHDE.Server.Teams.JoinTicketStore tickets, CancellationToken ct) =>
        {
            if (addon.Current is null) return Page(addon, null, HttpStatusCode.ServiceUnavailable);
            if (!LinkToken.LooksLikeToken(token) || !AllowTry(ctx.Connection.RemoteIpAddress))
                return Page(addon, null, HttpStatusCode.NotFound, dead: new DeadInvite(null));
            // An edit link needs an add-on that reads tickets; check before
            // using the link up.
            if (await projects.PeekLinkAsync(token, ct) is { Access: "edit" } && !addon.ReadsTickets)
                return Page(addon, NeedsNewAddon, HttpStatusCode.ServiceUnavailable);
            var use = await projects.UseLinkAsync(token, ct);
            var project = use is null ? null : await projects.GetAsync(use.ProjectId, ct);
            if (use is null || project is null)
            {
                var was = await projects.ProjectOfLinkAsync(token, ct);
                return Page(addon, null, HttpStatusCode.NotFound, dead: new DeadInvite(was is { } id ? await OwnerNameAsync(teams, id, ct) : null));
            }
            var from = await OwnerNameAsync(teams, project.ProjectId, ct);
            if (use.Access == "edit")
            {
                // A ticket: the person signs in and joins with it (teams.md §4).
                var ticket = await tickets.CreateAsync(project.ProjectId, use.LinkId, null, ct);
                return Starter(ctx, addon, project, new JoinFile(Ticket: ticket, Project: project.Name, From: from));
            }
            // Each download gets its own invite code: the link can expire, the
            // people who used it keep working (and can be revoked one by one).
            var label = (use.Label.Length > 0 ? use.Label : "Download link") + $" · download {use.Uses}";
            var (_, code) = await projects.CreateInviteAsync(project.ProjectId, label, ct, use.LinkId);
            return Starter(ctx, addon, project, new JoinFile(Code: code, Project: project.Name, From: from));
        });

        app.MapGet("/addon/manifest.json", (AddonStore addon) =>
            addon.Current is { } m ? Results.Ok(m) : Results.NotFound());
        app.MapGet("/addon/yhde-addon.zip", (AddonStore addon) =>
            addon.Current is null ? Results.NotFound() : Download(addon));
    }

    public static void MapAddonAdmin(this RouteGroupBuilder secured)
    {
        secured.MapGet("/addon", (AddonStore addon) => Results.Ok(new { current = addon.Current, pending = addon.Pending }));
        secured.MapPost("/addon/release", (AddonStore addon, TimeProvider time) =>
            addon.Release(time.GetUtcNow()) ? Results.Ok(new { current = addon.Current }) : Results.Problem("There is no uploaded add-on waiting.", statusCode: 409));
        secured.MapPost("/addon/discard", (AddonStore addon) =>
        {
            addon.Discard();
            return Results.Ok(new { ok = true });
        });
        secured.MapPost("/addon", async (HttpContext ctx, AddonStore addon, TimeProvider time, CancellationToken ct) =>
        {
            var limit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = AddonStore.MaxPackageBytes;
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length == 0) return Results.Problem("Choose the add-on zip to upload.", statusCode: 400);
            var error = addon.Install(ms.ToArray(), time.GetUtcNow());
            return error is null ? Results.Ok(new { current = addon.Current, pending = addon.Pending }) : Results.Problem(error, statusCode: 400);
        });
    }

    private const string NeedsNewAddon =
        "This link needs a newer YHDE than this server has. Ask the person who sent it for the code instead.";

    // What the invite page shows: who invited, to which game, edit or watch.
    private sealed record Invite(string Project, string? From, string Access, string DownloadUrl, string FileName, string? Image);
    // A link that no longer works, and who to ask for a new one.
    private sealed record DeadInvite(string? From);

    private static async Task<string?> OwnerNameAsync(YHDE.Server.Teams.TeamStore teams, Guid projectId, CancellationToken ct) =>
        (await teams.MembersAsync(projectId, ct)).FirstOrDefault(m => m.Role == "owner")?.Name;

    public static IResult Starter(HttpContext ctx, AddonStore addon, ProjectInfo project, JoinFile join)
    {
        var scheme = ctx.Request.IsHttps ? "wss" : "ws";
        var url = $"{scheme}://{ctx.Request.Host}/ws";
        var ms = new MemoryStream();
        addon.WriteStarter(ms, project.Name, url, join);
        ms.Position = 0;
        return Results.File(ms, "application/zip", AddonStore.FolderName(project.Name) + ".zip");
    }

    private static IResult Download(AddonStore addon) =>
        Results.File(addon.PackagePath, "application/zip", $"yhde-addon-{addon.Current!.Version}.zip");

    private static bool AllowTry(IPAddress? from) => Tries.Try(from?.ToString() ?? "unknown");

    // The join page: an invite (from a link), a link that no longer works,
    // the code form (with an error), or "not available" when no add-on is
    // released. godot: the oldest Godot the released add-on runs on.
    private static IResult Page(AddonStore addon, string? error, HttpStatusCode status, Invite? invite = null, DeadInvite? dead = null, string? code = null)
    {
        object data = addon.Current is null
            ? new { mode = "missing" }
            : invite is not null
                ? new { mode = "invite", project = invite.Project, from = invite.From, access = invite.Access,
                        downloadUrl = invite.DownloadUrl, fileName = invite.FileName, image = invite.Image, godot = addon.OldestGodot }
                : dead is not null
                    ? new { mode = "dead", from = dead.From }
                    : new { mode = "form", error, code = code?.Length <= 64 ? code : null, godot = addon.OldestGodot };
        return UiPages.Page("join.html", data, (int)status);
    }

    private static string Pretty(string platform) => platform switch
    {
        "windows" => "Windows",
        "linux" => "Linux",
        "macos" => "macOS",
        _ => platform,
    };
}
