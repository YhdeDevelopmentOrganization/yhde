using Dapper;
using YHDE.Server.Assets;
using YHDE.Server.Domain;
using YHDE.Server.Gateway;
using YHDE.Server.Persistence;
using YHDE.Server.Presence;
using YHDE.Server.Projects;

namespace YHDE.Server.Admin;

// Admin API for statistics, download links, deleting projects and making a
// project from a game zip (admin.md, onboarding.md). Mapped inside the signed-
// in group of AdminEndpoints.
public static class AdminProjectEndpoints
{
    public sealed record NewLinkRequest(string? Label, int? Hours, int? MaxUses);
    public sealed record DeleteRequest(string? Name);

    public const long MaxGameZipBytes = 16L * 1024 * 1024 * 1024;

    public static void MapProjectAdmin(this RouteGroupBuilder secured)
    {
        secured.MapGet("/stats", async (AdminStats stats, CancellationToken ct) => Results.Ok(await stats.GetAsync(ct)));

        // In the editor over a range: hour, day, week, month, quarter, year or all.
        secured.MapGet("/people", async (string? range, AdminStats stats, CancellationToken ct) =>
            Results.Ok(await stats.PeopleAsync(AdminStats.Ranges.Contains(range) ? range! : "month", ct)));

        secured.MapPost("/projects/{id:guid}/links", async (Guid id, NewLinkRequest request, HttpContext ctx, IProjectStore projects, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(id, ct);
            if (project is null) return Results.NotFound();
            if (project.ArchivedAt is not null) return Results.Problem("The project is archived: restore it first.", statusCode: 409);
            if (request.Hours is < 0 or > 24 * 365) return Results.Problem("A link lasts up to a year.", statusCode: 400);
            if (request.MaxUses is < 1 or > 10_000) return Results.Problem("Uses must be between 1 and 10000.", statusCode: 400);
            DateTimeOffset? expires = request.Hours is > 0 and var h ? DateTimeOffset.UtcNow.AddHours(h) : null;
            var (link, token) = await projects.CreateLinkAsync(id, PresenceService.SanitizeText(request.Label, 80), expires, request.MaxUses, ct);
            // The only time the token exists outside the link that is sent.
            return Results.Ok(new { id = link.LinkId, token, url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/join/{token}" });
        });

        // Join codes (teams.md): anyone signed in who enters one joins the
        // project as a developer. Staff can make one for any owned project.
        secured.MapPost("/projects/{id:guid}/join-code", async (Guid id, IProjectStore projects, YHDE.Server.Teams.TeamStore teams, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(id, ct);
            if (project is null) return Results.NotFound();
            if (project.ArchivedAt is not null) return Results.Problem("The project is archived: restore it first.", statusCode: 409);
            if (await teams.OfProjectAsync(id, ct) is null)
                return Results.Problem("This project has no owner, so it can't take people in. Move it to a team first.", statusCode: 409);
            for (var attempt = 0; ; attempt++)
            {
                var code = YHDE.Server.Teams.JoinCode.New();
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

        secured.MapPost("/projects/{id:guid}/join-code/off", async (Guid id, YHDE.Server.Teams.TeamStore teams, CancellationToken ct) =>
        {
            await teams.SetJoinCodeAsync(id, null, ct);
            return Results.Ok(new { ok = true });
        });

        secured.MapPost("/links/{id:guid}/revoke", async (Guid id, IProjectStore projects, CancellationToken ct) =>
        {
            await projects.RevokeLinkAsync(id, ct);
            return Results.Ok(new { ok = true });
        });

        secured.MapPost("/links/{id:guid}/delete", async (Guid id, IProjectStore projects, CancellationToken ct) =>
            await projects.DeleteLinkAsync(id, ct) ? Results.Ok(new { ok = true }) : Results.NotFound());

        secured.MapPost("/invites/{id:guid}/delete", async (Guid id, IProjectStore projects, AccessGate gate, CancellationToken ct) =>
        {
            if (!await projects.DeleteInviteAsync(id, ct)) return Results.NotFound();
            gate.ForgetInvites();
            return Results.Ok(new { ok = true });
        });

        secured.MapPost("/projects/{id:guid}/delete", async (Guid id, DeleteRequest request, IProjectStore projects, Database db,
            BlobStore blobs, AdminStats stats, AccessGate gate, ISessionManager sessions, ILoggerFactory logs, CancellationToken ct) =>
        {
            var project = await projects.GetAsync(id, ct);
            if (project is null) return Results.NotFound();
            if (project.ArchivedAt is null) return Results.Problem("Archive the project first; only archived projects can be deleted.", statusCode: 409);
            if (!string.Equals(request.Name?.Trim(), project.Name, StringComparison.Ordinal))
                return Results.Problem("Type the project's name exactly to delete it.", statusCode: 400);
            if (sessions.GetProjectSessions(id).Any())
                return Results.Problem("Someone is still connected to this project. Try again in a minute.", statusCode: 409);

            var removed = await projects.DeleteArchivedAsync(id, ct);
            if (removed is null) return Results.Problem("The project is no longer archived.", statusCode: 409);
            gate.ForgetInvites();
            stats.Invalidate();
            await using (var conn = await db.OpenAsync(ct))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO audit_log (event_type, actor_id, project_id, detail) VALUES ('project.deleted', @actor, @id, @detail::jsonb)",
                    new
                    {
                        actor = DevIdentity.ActorId,
                        id,
                        detail = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            name = project.Name, removed.Operations, removed.ChatMessages, removed.CommentThreads, removed.Invites, removed.Links,
                        }),
                    }, cancellationToken: ct));
            }
            _ = Task.Run(() => FreeUnusedFiles(db, blobs, logs.CreateLogger("YHDE.Server.Admin.Cleanup")));
            return Results.Ok(new { ok = true, removed });
        });

        secured.MapPost("/projects/import", async (HttpContext ctx, ProjectImporter importer, BlobStore blobs, AdminStats stats, CancellationToken ct) =>
        {
            var limit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxGameZipBytes;
            var tmp = blobs.NewTempPath();
            try
            {
                await using (var file = File.Create(tmp)) await ctx.Request.Body.CopyToAsync(file, ct);
                if (new FileInfo(tmp).Length == 0) return Results.Problem("Choose the game's zip file.", statusCode: 400);
                var name = ctx.Request.Query["name"].ToString();
                var result = await importer.ImportAsync(tmp, name, ct);
                stats.Invalidate();
                return Results.Ok(new
                {
                    id = result.Project.ProjectId,
                    name = result.Project.Name,
                    files = result.Files,
                    bytes = result.Bytes,
                    skipped = result.Skipped.Take(50),
                    skippedCount = result.Skipped.Count,
                });
            }
            catch (ImportException e)
            {
                return Results.Problem(e.Message, statusCode: 400);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        });
    }

    // After a project is deleted, the stored files only it used are freed.
    public static async Task FreeUnusedFiles(Database db, BlobStore blobs, ILogger logger)
    {
        try
        {
            await using var conn = await db.OpenAsync();
            var used = (await conn.QueryAsync<string>(new CommandDefinition(
                """
                SELECT DISTINCT payload->>'h' FROM operations
                WHERE type IN ('RegisterAsset', 'UpdateAsset', 'MoveAsset') AND payload ? 'h'
                UNION SELECT hash FROM site_media
                UNION SELECT hash FROM project_blobs
                """, commandTimeout: 600))).ToHashSet(StringComparer.Ordinal);
            var (files, bytes) = blobs.RemoveUnreferenced(used);
            logger.LogInformation("Freed {Files} unused files ({Bytes} bytes) after a project was deleted", files, bytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not free unused files");
        }
    }
}
