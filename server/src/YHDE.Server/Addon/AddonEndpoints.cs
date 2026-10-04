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
        app.MapGet("/join", (AddonStore addon) => Page(addon, null, HttpStatusCode.OK));

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
                return Page(addon, "That invite code does not work.", HttpStatusCode.OK);

            return Starter(ctx, addon, project, code);
        }).DisableAntiforgery();

        // Download links (/join/<token>). Opening the link shows the steps and
        // starts the download at once (meta refresh); only that download uses
        // the link, so link previews in chat apps never use one up.
        app.MapGet("/join/{token}", async (string token, HttpContext ctx, AddonStore addon, IProjectStore projects, CancellationToken ct) =>
        {
            if (addon.Current is null) return Page(addon, null, HttpStatusCode.ServiceUnavailable);
            var link = LinkToken.LooksLikeToken(token) && AllowTry(ctx.Connection.RemoteIpAddress)
                ? await projects.PeekLinkAsync(token, ct) : null;
            var project = link is null ? null : await projects.GetAsync(link.ProjectId, ct);
            if (project is null) return Page(addon, DeadLink, HttpStatusCode.NotFound);
            return Page(addon, null, HttpStatusCode.OK, new StarterDownload(project.Name, $"/join/{LinkToken.Normalize(token)}/download"));
        });

        app.MapGet("/join/{token}/download", async (string token, HttpContext ctx, AddonStore addon, IProjectStore projects, CancellationToken ct) =>
        {
            if (addon.Current is null) return Page(addon, null, HttpStatusCode.ServiceUnavailable);
            if (!LinkToken.LooksLikeToken(token) || !AllowTry(ctx.Connection.RemoteIpAddress))
                return Page(addon, DeadLink, HttpStatusCode.NotFound);
            var use = await projects.UseLinkAsync(token, ct);
            var project = use is null ? null : await projects.GetAsync(use.ProjectId, ct);
            if (use is null || project is null) return Page(addon, DeadLink, HttpStatusCode.NotFound);
            // Each download gets its own invite code: the link can expire, the
            // people who used it keep working (and can be revoked one by one).
            var label = (use.Label.Length > 0 ? use.Label : "Download link") + $" · download {use.Uses}";
            var (_, code) = await projects.CreateInviteAsync(project.ProjectId, label, ct, use.LinkId);
            return Starter(ctx, addon, project, code);
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

    private const string DeadLink =
        "This link has expired, been used up or been turned off.";

    private sealed record StarterDownload(string ProjectName, string Url);

    private static IResult Starter(HttpContext ctx, AddonStore addon, ProjectInfo project, string code)
    {
        var scheme = ctx.Request.IsHttps ? "wss" : "ws";
        var url = $"{scheme}://{ctx.Request.Host}/ws";
        var ms = new MemoryStream();
        addon.WriteStarter(ms, project.Name, url, code);
        ms.Position = 0;
        return Results.File(ms, "application/zip", AddonStore.FolderName(project.Name) + ".zip");
    }

    private static IResult Download(AddonStore addon) =>
        Results.File(addon.PackagePath, "application/zip", $"yhde-addon-{addon.Current!.Version}.zip");

    private static bool AllowTry(IPAddress? from) => Tries.Try(from?.ToString() ?? "unknown");

    // The join page: a download (from a link), the code form (with an
    // error), or "not available" when no add-on is released.
    private static IResult Page(AddonStore addon, string? error, HttpStatusCode status, StarterDownload? download = null)
    {
        object data = addon.Current is null
            ? new { mode = "missing" }
            : download is not null
                ? new { mode = "download", project = download.ProjectName, downloadUrl = download.Url }
                : new { mode = "form", error };
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
