using System.Collections.Concurrent;
using System.Net;
using System.Text;
using YHDE.Server.Assets;

namespace YHDE.Server.Site;

// The public website's content (admin.md "Site").
// Public:
// - GET  /api/site          announcement bar and maintenance switch
// - GET  /api/posts?kind=    live news posts and release notes
// - POST /api/early-access   {email}: the early-access list
// - POST /api/early-access/remove {email}: takes an address off it (GDPR)
// - GET  /media/<hash>       a picture uploaded for a post (only those)
// Admin (/admin/api, signed in): posts, settings and the early-access list.
public static class SiteEndpoints
{
    private static readonly YHDE.Server.Accounts.Limiter Tries = new(10, TimeSpan.FromMinutes(10));

    public sealed record EarlyAccessRequest(string? Email, string? Source);
    public sealed record EmailRequest(string? Email);

    public static void MapSiteEndpoints(this WebApplication app)
    {
        app.MapGet("/api/site", async (SiteStore site, CancellationToken ct) =>
        {
            var s = await site.SettingsAsync(ct);
            return Results.Ok(new
            {
                announcement = s.Announcement.Enabled ? s.Announcement : null,
                maintenance = s.Maintenance.Enabled ? s.Maintenance : null,
                site = SiteMode.PageInfo(),
            });
        });

        app.MapGet("/api/posts", async (string? kind, SiteStore site, CancellationToken ct) =>
            Results.Ok(await site.LiveAsync(kind is "news" or "release" ? kind : null, ct)));

        // The early-access list is YHDE's own (the official site only).
        app.MapPost("/api/early-access", async (EarlyAccessRequest request, HttpContext ctx, SiteStore site, CancellationToken ct) =>
        {
            if (!SiteMode.Official) return Results.NotFound();
            if (!AllowTry(ctx.Connection.RemoteIpAddress))
                return Results.Problem("Too many tries. Wait a few minutes.", statusCode: 429);
            var email = SiteStore.NormalizeEmail(request.Email);
            if (email is null) return Results.Problem("That email address does not look right.", statusCode: 400);
            await site.AddEarlyAccessAsync(email, request.Source ?? "", ct);
            // Same answer whether or not the address was already on the list.
            return Results.Ok(new { ok = true });
        }).DisableAntiforgery();

        // Anyone can take an address off the list, with the same answer
        // whether or not it was there, so the list can't be probed.
        app.MapPost("/api/early-access/remove", async (EmailRequest request, HttpContext ctx, SiteStore site, CancellationToken ct) =>
        {
            if (!SiteMode.Official) return Results.NotFound();
            if (!AllowTry(ctx.Connection.RemoteIpAddress))
                return Results.Problem("Too many tries. Wait a few minutes.", statusCode: 429);
            var email = SiteStore.NormalizeEmail(request.Email);
            if (email is null) return Results.Problem("That email address does not look right.", statusCode: 400);
            await site.RemoveEarlyAccessAsync(email, ct);
            return Results.Ok(new { ok = true });
        }).DisableAntiforgery();

        // Only pictures uploaded on the admin page are public; every other
        // blob stays behind the access key (/assets).
        app.MapGet("/media/{hash}", async (string hash, HttpContext ctx, SiteStore site, BlobStore blobs, CancellationToken ct) =>
        {
            if (!BlobStore.IsValidHash(hash) || !blobs.Exists(hash)) return Results.NotFound();
            var media = await site.MediaAsync(hash, ct);
            if (media is null) return Results.NotFound();
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.CacheControl = "public, max-age=31536000, immutable";
            h.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.File(blobs.OpenRead(hash), media.ContentType, enableRangeProcessing: false);
        });
    }

    public static void MapSiteAdmin(this RouteGroupBuilder secured)
    {
        secured.MapGet("/posts", async (SiteStore site, CancellationToken ct) => Results.Ok(await site.AllAsync(ct)));

        secured.MapPost("/posts", async (SiteStore.PostInput input, SiteStore site, CancellationToken ct) =>
            SiteStore.Check(input) is { } error ? Results.Problem(error, statusCode: 400) : Results.Ok(await site.SaveAsync(null, input, ct)));

        secured.MapPost("/posts/{id:guid}", async (Guid id, SiteStore.PostInput input, SiteStore site, CancellationToken ct) =>
        {
            if (SiteStore.Check(input) is { } error) return Results.Problem(error, statusCode: 400);
            try { return Results.Ok(await site.SaveAsync(id, input, ct)); }
            catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: 404); }
        });

        secured.MapPost("/posts/{id:guid}/delete", async (Guid id, SiteStore site, CancellationToken ct) =>
            await site.DeleteAsync(id, ct) ? Results.Ok(new { ok = true }) : Results.Problem("That post is gone.", statusCode: 404));

        secured.MapGet("/media", async (SiteStore site, CancellationToken ct) => Results.Ok(await site.AllMediaAsync(ct)));

        // A picture for a post: the body is the image. PNG, JPEG, GIF or WebP,
        // told apart by its first bytes; up to 8 MB.
        secured.MapPost("/media", async (HttpContext ctx, SiteStore site, BlobStore blobs, CancellationToken ct) =>
        {
            var limit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = SiteStore.MaxMediaBytes + 1;
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + read > SiteStore.MaxMediaBytes) return Results.Problem("Pictures can be up to 8 MB.", statusCode: 413);
                ms.Write(buffer, 0, read);
            }
            if (ms.Length == 0) return Results.Problem("Choose a picture to upload.", statusCode: 400);
            var type = SiteStore.SniffImage(ms.GetBuffer().AsSpan(0, (int)Math.Min(ms.Length, 16)));
            if (type is null) return Results.Problem("Use a PNG, JPEG, GIF or WebP picture.", statusCode: 415);
            ms.Position = 0;
            var stored = await blobs.PutAsync(ms, ct);
            if (stored is null) return Results.Problem("That picture is too large to store.", statusCode: 413);
            var name = ctx.Request.Query["name"].ToString();
            return Results.Ok(await site.AddMediaAsync(stored.Value.Hash, type, stored.Value.Size, name, ct));
        });

        secured.MapGet("/site-settings", async (SiteStore site, CancellationToken ct) => Results.Ok(await site.SettingsAsync(ct)));

        secured.MapPost("/site-settings", async (SiteSettings settings, SiteStore site, CancellationToken ct) =>
        {
            if (settings.Announcement is null || settings.Maintenance is null) return Results.Problem("Send both settings.", statusCode: 400);
            var a = settings.Announcement;
            var m = settings.Maintenance;
            settings = new SiteSettings(
                new Announcement(a.Enabled, (a.Text ?? "").Trim(), (a.Link ?? "").Trim(), a.Tone ?? "info"),
                new Maintenance(m.Enabled, (m.Message ?? "").Trim()));
            if (SiteStore.Check(settings) is { } error) return Results.Problem(error, statusCode: 400);
            await site.SaveSettingsAsync(settings, ct);
            return Results.Ok(settings);
        });

        secured.MapGet("/early-access", async (SiteStore site, CancellationToken ct) => Results.Ok(await site.EarlyAccessAsync(ct)));

        secured.MapGet("/early-access.csv", async (SiteStore site, CancellationToken ct) =>
        {
            var csv = new StringBuilder("email,source,signed_up\n");
            foreach (var e in await site.EarlyAccessAsync(ct))
                csv.Append(Cell(e.Email)).Append(',').Append(Cell(e.Source)).Append(',').Append(e.Created.ToString("O")).Append('\n');
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", "yhde-early-access.csv");
        });

        secured.MapPost("/early-access/remove", async (EmailRequest request, SiteStore site, CancellationToken ct) =>
            SiteStore.NormalizeEmail(request.Email) is { } email && await site.RemoveEarlyAccessAsync(email, ct)
                ? Results.Ok(new { ok = true })
                : Results.Problem("That address is not on the list.", statusCode: 404));
    }

    // Quotes a CSV cell, and keeps spreadsheet apps from running it as a formula.
    private static string Cell(string value)
    {
        var v = value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private static bool AllowTry(IPAddress? from) => Tries.Try(from?.ToString() ?? "unknown");
}
