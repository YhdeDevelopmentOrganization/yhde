using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using YHDE.Server.Gateway;
using YHDE.Server.Teams;

namespace YHDE.Server.Assets;

// Who asks the file routes, for which project (resolved from the grant and the
// X-YHDE-Project header by the endpoint filter).
// - Project set: files that project holds; uploads need edit rights there.
// - ReadableProjects set: an add-on that does not name its project yet may
//   read files any of these projects holds, and not upload.
// - Neither: the operator's server key without a project, the whole store.
public sealed record AssetScope(Guid? Project, bool CanUpload, string Uploader)
{
    public IReadOnlyCollection<Guid>? ReadableProjects { get; init; }

    public bool Global => Project is null && ReadableProjects is null;

    public static AssetScope Server(string uploader = "server") => new(null, true, uploader);
}

// The asset plane's bulk transfer (assets.md, network_protocol.md):
// plain HTTP next to the WebSocket, behind the same access key, for one
// project (X-YHDE-Project). A file the project does not hold looks the same
// as a file that does not exist.
//
//   POST  /assets/missing        {"hashes":[...]} -> which of them to upload
//   HEAD  /assets/blobs/{hash}   200 when held; 404 + Upload-Offset otherwise
//   GET   /assets/blobs/{hash}   the bytes (Range supported, immutable)
//   PATCH /assets/blobs/{hash}   append a chunk: Upload-Offset, Upload-Length
public static class AssetEndpoints
{
    public const string UploadOffsetHeader = "Upload-Offset";
    public const string UploadLengthHeader = "Upload-Length";
    public const string ProjectHeader = "X-YHDE-Project";
    public const long MaxChunkBytes = 64L * 1024 * 1024;
    public const int MaxHashesPerQuery = 10_000;

    public sealed record MissingRequest(List<string>? Hashes);

    public sealed record MissingResponse(List<string> Missing, Dictionary<string, long> Partial);

    public sealed record ChunkResponse(long Offset, bool Complete);

    public static void MapAssetEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/assets").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var gate = http.RequestServices.GetRequiredService<AccessGate>();
            var authorization = http.Request.Headers.Authorization.FirstOrDefault();
            var grant = await gate.AdmitAsync(authorization, http.RequestAborted);
            if (grant is null)
                return Results.Problem("Missing or wrong access key or invite code.", statusCode: StatusCodes.Status401Unauthorized);
            var scope = ScopeFor(grant, http.Request.Headers[ProjectHeader].FirstOrDefault(), authorization);
            if (scope is null)
                return Results.Problem("This project is not yours to open.", statusCode: StatusCodes.Status403Forbidden);
            http.Items[typeof(AssetScope)] = scope;
            return await next(context);
        });

        group.MapPost("/missing", (MissingRequest request, BlobStore blobs, IProjectBlobs held, HttpContext context) =>
            MissingAsync(request, blobs, ScopeOf(context), held, context.RequestAborted));
        group.MapMethods("/blobs/{hash}", [HttpMethods.Head], (string hash, BlobStore blobs, IProjectBlobs held, HttpContext context) =>
            HeadAsync(hash, blobs, context, ScopeOf(context), held));
        group.MapGet("/blobs/{hash}", (string hash, BlobStore blobs, IProjectBlobs held, HttpContext context) =>
            GetAsync(hash, blobs, ScopeOf(context), held, context.RequestAborted));
        group.MapMethods("/blobs/{hash}", [HttpMethods.Patch], (string hash, BlobStore blobs, IProjectBlobs held, StorageQuota quota, HttpContext context) =>
            AppendAsync(hash, blobs, context, ScopeOf(context), held, quota));
    }

    private static AssetScope ScopeOf(HttpContext context) => (AssetScope)context.Items[typeof(AssetScope)]!;

    // Null when the grant does not cover the named project.
    public static AssetScope? ScopeFor(AccessGrant grant, string? projectHeader, string? authorization)
    {
        var uploader = UploaderOf(grant, authorization);
        if (!string.IsNullOrWhiteSpace(projectHeader))
        {
            if (!Guid.TryParse(projectHeader.Trim(), out var project) || !grant.Allows(project)) return null;
            return new AssetScope(project, grant.CanEdit(project), uploader);
        }
        // Add-ons before 0.7 do not name the project. An invite code is for one
        // project; the server key is for all; a sign-in may read what its
        // projects hold but must update the add-on to upload.
        if (grant.ProjectId is { } one) return new AssetScope(one, grant.CanEdit(one), uploader);
        if (grant.Projects is { } theirs) return new AssetScope(null, false, uploader) { ReadableProjects = theirs.ToList() };
        return AssetScope.Server(uploader);
    }

    // Names the uploader's part file: a person, an invite code, or the server key.
    public static string UploaderOf(AccessGrant grant, string? authorization)
    {
        if (grant.UserId is { } user) return user.ToString("N");
        if (grant.ProjectId is not null && authorization is { Length: > 0 })
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(authorization)))[..24];
        return "server";
    }

    // Whether the asker may see this stored file.
    public static async Task<bool> VisibleAsync(string hash, BlobStore blobs, AssetScope scope, IProjectBlobs held, CancellationToken ct)
    {
        if (!blobs.Exists(hash)) return false;
        if (scope.Project is { } project) return await held.GetAsync(project, hash, ct) is not null;
        if (scope.ReadableProjects is { } projects) return await held.AnyAsync(projects, hash, ct);
        return true;
    }

    public static async Task<IResult> MissingAsync(MissingRequest request, BlobStore blobs, AssetScope scope, IProjectBlobs held, CancellationToken ct)
    {
        var hashes = request.Hashes ?? [];
        if (hashes.Count > MaxHashesPerQuery)
            return Results.Problem($"Ask about at most {MaxHashesPerQuery} hashes at once.", statusCode: StatusCodes.Status400BadRequest);
        var missing = new List<string>();
        var partial = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var hash in hashes.Distinct(StringComparer.Ordinal))
        {
            if (!BlobStore.IsValidHash(hash))
                return Results.Problem($"'{hash}' is not a lowercase hex SHA-256.", statusCode: StatusCodes.Status400BadRequest);
            if (await VisibleAsync(hash, blobs, scope, held, ct)) continue;
            missing.Add(hash);
            var offset = blobs.PartialLength(hash, scope.Uploader);
            if (offset > 0) partial[hash] = offset;
        }
        return Results.Ok(new MissingResponse(missing, partial));
    }

    public static async Task<IResult> HeadAsync(string hash, BlobStore blobs, HttpContext context, AssetScope scope, IProjectBlobs held)
    {
        if (!BlobStore.IsValidHash(hash)) return Results.BadRequest();
        if (!await VisibleAsync(hash, blobs, scope, held, context.RequestAborted))
        {
            context.Response.Headers[UploadOffsetHeader] = blobs.PartialLength(hash, scope.Uploader).ToString();
            return Results.NotFound();
        }
        context.Response.ContentLength = blobs.SizeOf(hash);
        return Results.Ok();
    }

    public static async Task<IResult> GetAsync(string hash, BlobStore blobs, AssetScope scope, IProjectBlobs held, CancellationToken ct)
    {
        if (!BlobStore.IsValidHash(hash)) return Results.BadRequest();
        if (!await VisibleAsync(hash, blobs, scope, held, ct)) return Results.NotFound();
        // Content-addressed: the bytes behind a name never change.
        return Results.File(blobs.OpenRead(hash), "application/octet-stream",
            entityTag: new EntityTagHeaderValue($"\"{hash}\""), enableRangeProcessing: true);
    }

    public static async Task<IResult> AppendAsync(string hash, BlobStore blobs, HttpContext context, AssetScope scope, IProjectBlobs held,
        StorageQuota? quota = null)
    {
        var ct = context.RequestAborted;
        if (!BlobStore.IsValidHash(hash))
            return Results.Problem("Not a lowercase hex SHA-256.", statusCode: StatusCodes.Status400BadRequest);
        if (!scope.CanUpload)
            return Results.Problem(scope.ReadableProjects is not null
                ? "Update the YHDE add-on to share files (this version does not say which project they are for)."
                : "You can view this project but not change it.", statusCode: StatusCodes.Status403Forbidden);
        if (!long.TryParse(context.Request.Headers[UploadOffsetHeader], out var offset) || offset < 0 ||
            !long.TryParse(context.Request.Headers[UploadLengthHeader], out var total) || total < 0)
            return Results.Problem($"{UploadOffsetHeader} and {UploadLengthHeader} are required.", statusCode: StatusCodes.Status400BadRequest);
        if (context.Request.ContentLength is > MaxChunkBytes)
            return Results.Problem($"Send at most {MaxChunkBytes} bytes per request.", statusCode: StatusCodes.Status413PayloadTooLarge);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxChunkBytes;

        // The project holds it already: nothing to send.
        if (await VisibleAsync(hash, blobs, scope, held, ct))
        {
            blobs.Touch(hash);
            context.Response.Headers[UploadOffsetHeader] = total.ToString();
            return Results.Ok(new ChunkResponse(total, true));
        }
        if (total > blobs.MaxBlobBytes)
            return Results.Problem($"Files are limited to {blobs.MaxBlobBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);

        var started = blobs.PartialLength(hash, scope.Uploader);
        if (started == 0 && offset == 0)
        {
            if (blobs.UnfinishedParts(scope.Uploader) >= BlobStore.MaxUnfinishedParts)
                return Results.Problem("Too many unfinished uploads. Let them finish, then try again.", statusCode: StatusCodes.Status429TooManyRequests);
            // A new upload counts toward the owner's storage from its first byte.
            if (scope.Project is { } p && quota is not null && await quota.RefuseUploadAsync(p, total, ct) is { } full)
                return Results.Problem(full, statusCode: StatusCodes.Status507InsufficientStorage);
        }
        if (blobs.LowOnDisk(Math.Max(0, total - started)))
            return Results.Problem("The server is low on disk space and takes no new files right now. Tell the server's operator.",
                statusCode: StatusCodes.Status507InsufficientStorage);

        var result = await blobs.AppendAsync(hash, offset, total, context.Request.Body, ct, scope.Uploader, proveBytes: !scope.Global);
        if (result.Status == BlobStore.AppendStatus.Completed && scope.Project is { } project)
            await held.AddAsync(project, hash, total, scope.Uploader, referenced: false, ct);
        context.Response.Headers[UploadOffsetHeader] = result.Offset.ToString();
        return result.Status switch
        {
            BlobStore.AppendStatus.Accepted => Results.Ok(new ChunkResponse(result.Offset, false)),
            BlobStore.AppendStatus.Completed or BlobStore.AppendStatus.AlreadyStored => Results.Ok(new ChunkResponse(result.Offset, true)),
            BlobStore.AppendStatus.OffsetMismatch => Results.Conflict(new ChunkResponse(result.Offset, false)),
            BlobStore.AppendStatus.Busy => Results.Problem("Another upload of this file is in progress.", statusCode: StatusCodes.Status423Locked),
            BlobStore.AppendStatus.TooLarge => Results.Problem($"Files are limited to {blobs.MaxBlobBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge),
            BlobStore.AppendStatus.HashMismatch => Results.Problem("The uploaded bytes do not match their hash.", statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
